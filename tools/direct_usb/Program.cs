using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace XHeadDirectUsb
{
    /// <summary>
    /// Talks directly to the XHEAD-USB device via WinUSB, bypassing mnservice.exe entirely.
    /// Implements the register-bus protocol reverse-engineered in
    /// docs/protocol/modulation_capabilities.md and tools/usb_capture/README.md:
    ///   bRequest=0x4A (SET address, wIndex=addr, big-endian, wLength=0)
    ///   bRequest=0x4E (READ, wLength=8, response=[address echo BE][data BE])
    ///   bRequest=0x4F (WRITE, address+data packed BE across wValue+wIndex, wLength=0)
    ///
    /// SAFETY: read-only by default. Writing requires --write on the command line AND a
    /// single target address, to make accidental blind writes to unidentified registers hard.
    /// mnservice.exe must be stopped first (WinUSB interfaces are exclusive-access).
    /// </summary>
    internal static class Program
    {
        // Device interface GUID for XHEAD-USB, read from this machine's registry:
        // HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_17A7&PID_0008\<serial>\Device Parameters
        //   DeviceInterfaceGUIDs = {2F110364-7C93-4684-B4DC-46D95D5B3A9D}
        private static readonly Guid DeviceInterfaceGuid = new Guid("2F110364-7C93-4684-B4DC-46D95D5B3A9D");

        private const byte REQ_SET_ADDRESS = 0x4A;
        private const byte REQ_READ = 0x4E;
        private const byte REQ_WRITE = 0x4F;

        private const byte BM_HOST_TO_DEVICE_VENDOR_DEVICE = 0x40;
        private const byte BM_DEVICE_TO_HOST_VENDOR_DEVICE = 0xC0;

        // Confirmed via USBPcap capture (tools/usb_capture/README.md): bulk OUT endpoint address 0x01.
        // WinUSB pipe IDs are the raw endpoint address byte, so this is usable directly with WinUsb_WritePipe.
        private const byte PIPE_ID_BULK_OUT = 0x01;

        // mslicebuffer.cc's own logged slice geometry: 24064 bytes = 128 x 188-byte MPEG-TS packets.
        private const int SLICE_SIZE_BYTES = 24064;
        private const int TS_PACKET_SIZE = 188;
        private const int TS_PACKETS_PER_SLICE = SLICE_SIZE_BYTES / TS_PACKET_SIZE;

        private static SafeFileHandle _fileHandle;
        private static IntPtr _winusbHandle = IntPtr.Zero;

        private static int Main(string[] args)
        {
            bool writeMode = false;
            bool configureMode = false;
            bool streamMode = false;
            bool stopMode = false;
            ushort? writeAddr = null;
            uint? writeData = null;

            uint freqKHz = 473000;
            uint mode = 5;            // ISDB_T (matches the Mode enum raw value, see RunConfigureSequence)
            uint constellation = 1;   // QPSK
            uint bandwidth = 6;
            uint fft = 1;
            uint coderate = 3;
            uint guardinterval = 1;
            uint timeinterleave = 3;
            uint carrier = 0;         // DTMB only: 0=CARRIER_3780 1=CARRIER_1
            uint frame = 1;           // DTMB only: 0=FRAME_420 1=FRAME_945(既定) 2=FRAME_595
            uint interleave = 3;      // DTMB only: 2=TI_240 3=TI_720(既定)
            int dacgain = -10;
            int streamSeconds = 3;
            bool forceUntestedMode = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--write") writeMode = true;
                else if (args[i] == "--addr") writeAddr = Convert.ToUInt16(args[++i], 16);
                else if (args[i] == "--data") writeData = Convert.ToUInt32(args[++i], 16);
                else if (args[i] == "--configure") configureMode = true;
                else if (args[i] == "--stream") streamMode = true;
                else if (args[i] == "--stop") stopMode = true;
                else if (args[i] == "--seconds") streamSeconds = Convert.ToInt32(args[++i]);
                else if (args[i] == "--freq") freqKHz = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--mode") mode = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--force-untested-mode") forceUntestedMode = true;
                else if (args[i] == "--constellation") constellation = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--bandwidth") bandwidth = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--fft") fft = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--coderate") coderate = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--guardinterval") guardinterval = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--timeinterleave") timeinterleave = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--carrier") carrier = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--frame") frame = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--interleave") interleave = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--dacgain") dacgain = Convert.ToInt32(args[++i]);
            }

            if ((configureMode || streamMode) && !forceUntestedMode)
            {
                // 0=DVB_T, 2=ATSC, 3=J83B, 5=ISDB_T have all been directly verified against a real
                // mnservice.exe register capture (docs/protocol/modulation_capabilities.md 続報17/19)
                // and are known RF-safe. 4=DTMB/6=J83C are ALSO now verified RF-safe via direct_usb
                // specifically (続報22) -- these reliably hang mnservice.exe's own gRPC service when
                // driven through the normal software stack (続報13), but bypassing that stack
                // entirely (this tool) completes cleanly and produces real RF output, live-verified
                // twice each. This strongly suggests the mnservice.exe hang is a software-side
                // wait/race condition in its own DTMB/J83C handling, not a hardware-level lockup.
                // 1=J83A is ALSO now verified RF-safe via direct_usb (続報24) -- Ghidra decompilation
                // of mnservice.exe's own modulation-param validator showed J83A's "modulation param
                // invalid" rejection is a software-side bitrate/symbol-rate calculation that needs a
                // parameter J83A's property descriptor never exposes a way to set (unrelated to any
                // hardware capability check), and J83A shares the same "Constellation only" register
                // footprint as ATSC/J83B/J83C -- live-verified twice, real RF output both times.
                // 7=DVB_T2 remains unverified: its rejection is confirmed (via the same decompilation)
                // to be genuine DVB-T2 standard compatibility validation (FFT x PilotPattern x CodeRate
                // x GuardInterval x FEC), not a missing-parameter gap like J83A -- two different
                // alternate parameter combinations were tried via mnservice.exe and both were also
                // rejected, so neither the correct spec-valid combination nor DVB_T2's raw register
                // footprint (never captured -- 11 fields, none register-mapped) are known. Require an
                // explicit opt-in before sending untested raw values to the modulator hardware.
                bool[] verifiedSafeModes = new bool[8]; // index = Mode enum raw value
                verifiedSafeModes[0] = true; // DVB_T
                verifiedSafeModes[1] = true; // J83A (続報24, direct_usb only -- mnservice.exe rejects via software validation)
                verifiedSafeModes[2] = true; // ATSC
                verifiedSafeModes[3] = true; // J83B
                verifiedSafeModes[4] = true; // DTMB (続報22, direct_usb only -- hangs via mnservice.exe)
                verifiedSafeModes[5] = true; // ISDB_T
                verifiedSafeModes[6] = true; // J83C (続報22, direct_usb only -- hangs via mnservice.exe)
                if (mode >= (uint)verifiedSafeModes.Length || !verifiedSafeModes[mode])
                {
                    Console.WriteLine($"REFUSING: --mode {mode} has not been verified against a real mnservice.exe " +
                        "register capture, or (DTMB/J83C/J83A) against direct_usb itself. DVB_T2(7) was only " +
                        "ever rejected by mnservice.exe's own software validation before reaching hardware -- its " +
                        "raw register behavior is genuinely unknown. Sending raw register writes for these Modes " +
                        "carries unknown hardware risk. Pass --force-untested-mode to override if you understand " +
                        "and accept this risk.");
                    return 1;
                }
            }

            Console.WriteLine("=== XHeadDirectUsb: raw WinUSB register-bus probe (bypasses mnservice.exe) ===");
            Console.WriteLine(writeMode ? "Mode: WRITE (explicit --write given)" :
                streamMode ? "Mode: CONFIGURE + STREAM (bulk-OUT null-TS payload after full ChannelStart replay)" :
                configureMode ? "Mode: CONFIGURE (full ChannelStart write-sequence replay, bypasses mnservice.exe entirely)" :
                stopMode ? "Mode: STOP (send the ChannelStop-equivalent teardown register write)" :
                "Mode: READ-ONLY (default, safe)");
            Console.Out.Flush();

            if (!OpenDevice())
            {
                Console.WriteLine("FATAL: could not open XHEAD-USB via WinUSB. Is mnservice.exe still running " +
                    "(it holds the interface exclusively)? Stop it first.");
                return 1;
            }

            try
            {
                if (streamMode)
                {
                    RunConfigureSequence(mode, freqKHz, constellation, bandwidth, fft, coderate, guardinterval, timeinterleave, carrier, frame, interleave, dacgain);
                    RunStreamTest(streamSeconds);
                }
                else if (configureMode)
                {
                    RunConfigureSequence(mode, freqKHz, constellation, bandwidth, fft, coderate, guardinterval, timeinterleave, carrier, frame, interleave, dacgain);
                }
                else if (stopMode)
                {
                    RunStopSequence();
                }
                else if (writeMode)
                {
                    if (writeAddr == null || writeData == null)
                    {
                        Console.WriteLine("--write requires both --addr <hex> and --data <hex>. Aborting, no write sent.");
                        return 1;
                    }
                    Console.WriteLine($"  *** ABOUT TO WRITE: addr=0x{writeAddr:X4} data=0x{writeData:X8} ***");
                    Console.WriteLine("  Reading current value first for reference...");
                    Console.Out.Flush();
                    SetAddress(writeAddr.Value);
                    Thread.Sleep(50);
                    var before = ReadRegister();
                    Console.WriteLine($"  Before: echoAddr=0x{before.Item1:X8} data=0x{before.Item2:X8}");
                    Console.Out.Flush();

                    SetAddress(writeAddr.Value);
                    Thread.Sleep(50);
                    WriteRegister(writeData.Value);
                    Console.WriteLine("  Write sent.");
                    Thread.Sleep(50);

                    SetAddress(writeAddr.Value);
                    Thread.Sleep(50);
                    var after = ReadRegister();
                    Console.WriteLine($"  After:  echoAddr=0x{after.Item1:X8} data=0x{after.Item2:X8}");
                }
                else
                {
                    // Read-only scan of every address seen live via cdb (known + unidentified),
                    // one at a time, with a pause between each -- no need to hammer the device.
                    ushort[] scanAddrs = new ushort[]
                    {
                        // Confirmed modulation params
                        0x1202, // Frequency
                        0x0684, // Bandwidth
                        0x0690, // Constellation
                        0x0691, // FFT
                        0x0692, // GuardInterval (high confidence)
                        0x0693, // CodeRate
                        0x0694, // TimeInterleavce (high confidence)
                        // RF power bank
                        0x1220, 0x1221, 0x1228, 0x1229, 0x1290,
                        // Confirmed 2026-07-26: read-only RF calibration table, implemented in
                        // mnservice.exe by a dedicated class (mazo::mbroadcast::mCalibration).
                        // Writes here are always gated off; only ever read (0xa/0x88/0x0/0x4
                        // observed), gated on 0x1220 showing its "committed" magic signature.
                        // Likely where PAGain-related trimming actually lives, in software, rather
                        // than PAGain being written to any register directly (tools/usb_capture/
                        // README.md "続報10").
                        0x1280, 0x1281, 0x1282, 0x1283,
                        // Unidentified, stable across runs
                        0x0600, 0x0601, 0x0602, 0x0629, 0x0640, 0x0641, 0x0642,
                        0x0680, 0x0681, 0x0682, 0x0683,
                        // NEW (2026-07-26): a third register bank, read once at device-connect
                        // time before any ChannelStart -- 0x0025's value matched mwinusb.cc's
                        // logged "Transform" value byte-for-byte, so this looks like a device
                        // identification/compatibility-check block. 0x0020 additionally showed a
                        // changing low byte later on (not purely static). See
                        // tools/usb_capture/README.md "続報9".
                        0x0020, 0x0023, 0x0024, 0x0025, 0x0026, 0x0027, 0x0028, 0x0029,
                        // Unexplored neighbours of the confirmed cluster, in case a Mode-select
                        // register lives nearby (pure exploration, low confidence)
                        0x0685, 0x0686, 0x0687, 0x0688, 0x0689,
                        0x1203, 0x1204, 0x1205,
                    };

                    foreach (var addr in scanAddrs)
                    {
                        SetAddress(addr);
                        Thread.Sleep(30);
                        var (echoAddr, data) = ReadRegister();
                        string note = echoAddr == addr ? "" : "  <-- echo MISMATCH!";
                        Console.WriteLine($"  0x{addr:X4} -> echoAddr=0x{echoAddr:X8} data=0x{data:X8}{note}");
                        Console.Out.Flush();
                        Thread.Sleep(30);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                return 1;
            }
            finally
            {
                CloseDevice();
            }

            Console.WriteLine("Done.");
            return 0;
        }

        /// <summary>
        /// Replays the exact register-write sequence mnservice.exe issues during CmdChannelStart,
        /// reconstructed from cdb captures (cdb_stdout14.log, corroborated by cdb_stdout17.log) by
        /// breakpointing the native write helpers and dumping rcx/r8/r9 (address, data) per hit.
        /// Includes several registers whose purpose is still unidentified (see tools/direct_usb/README.md
        /// and tools/usb_capture/README.md) -- these are replayed with the exact constant values observed
        /// in every capture, on the theory that fidelity to the real sequence is the safest way to trigger
        /// whatever latch/state-machine behavior they gate, even without understanding them individually.
        ///
        /// 2026-07-27 (続報19): 0x0680 was previously sent as a hardcoded constant (5) with an
        /// "unidentified" label. Capturing ATSC's and J83B's own native ChannelStart sequences
        /// (docs/protocol/modulation_capabilities.md 続報19) showed 0x0680 tracking mModulationParam's
        /// Mode enum raw value exactly across all four modes captured so far (ISDB_T=5, DVB_T=0,
        /// ATSC=2, J83B=3) -- this is very likely the actual hardware Mode-select register. It is now
        /// a real parameter (`mode`) instead of a hardcoded constant. The same captures also showed
        /// ATSC/J83B write ONLY the Constellation register (0x0690) -- no Bandwidth/FFT/CodeRate/
        /// GuardInterval writes at all, since those Modes have no such fields -- and that DVB_T,
        /// unlike ISDB_T, has no TimeInterleavce field either. The field set written is now mode-aware
        /// to match native behavior exactly, rather than always sending the full ISDB_T-shaped set.
        ///
        /// 2026-07-27 (続報22): DTMB (mode=4) captured live via cdb -- unexpectedly, mnservice.exe's
        /// own ChannelStart for DTMB succeeded this time (previously confirmed to reliably hang the
        /// service, 続報13), suggesting the hang is a race/timing issue rather than a deterministic
        /// one. The captured register sequence for DTMB is genuinely odd: after Constellation(0x690)
        /// and Bandwidth(0x684), address 0x692 is written TWICE with different values in a row
        /// (CodeRate's raw value, then immediately overwritten with Carrier's raw value), followed by
        /// 0x694=Frame and 0x691=Interleave. Whether CodeRate's write to 0x692 is simply clobbered by
        /// the very next Carrier write (a possible bug in mnservice.exe's own DTMB field-to-register
        /// table -- plausible given DTMB's history of rough edges) is unknown; this code replicates
        /// the exact observed sequence (including the double-write) rather than guessing a "corrected"
        /// version, since fidelity to what was actually observed working is safer than a plausible-
        /// looking guess.
        /// </summary>
        private static void RunConfigureSequence(uint mode, uint freqKHz, uint constellation, uint bandwidth, uint fft,
            uint coderate, uint guardinterval, uint timeinterleave, uint carrier, uint frame, uint interleave, int dacgain)
        {
            byte dacByte = unchecked((byte)dacgain);
            uint dacPacked = (uint)((dacByte << 8) | dacByte);
            uint extReg = 0x45585400u | 0x02; // observed as constant 0x45585402 in every capture; meaning unknown

            // Confirmed (続報19・22) field sets per Mode: ISDB_T alone has TimeInterleavce; DVB_T
            // shares the other four OFDM fields with ISDB_T; ATSC/J83B/J83C write Constellation only;
            // DTMB has its own distinct field set (see 続報22 above).
            bool hasOfdmFields = mode == 0 || mode == 5;   // DVB_T, ISDB_T
            bool hasTimeInterleave = mode == 5;             // ISDB_T only
            bool isDtmb = mode == 4;

            Console.WriteLine("  Mode=" + mode + " Frequency=" + freqKHz + "kHz Constellation=" + constellation +
                (hasOfdmFields ? " Bandwidth=" + bandwidth + " FFT=" + fft + " CodeRate=" + coderate +
                    " GuardInterval=" + guardinterval : "") +
                (hasTimeInterleave ? " TimeInterleavce=" + timeinterleave : "") +
                (isDtmb ? " Bandwidth=" + bandwidth + " CodeRate=" + coderate + " Carrier=" + carrier +
                    " Frame=" + frame + " Interleave=" + interleave : "") +
                " DACGain=" + dacgain);
            Console.Out.Flush();

            var seq = new System.Collections.Generic.List<(ushort addr, uint data, string label)>
            {
                (0x0602, 1,          "unidentified (observed constant)"),
                (0x0640, 3,          "unidentified (observed constant)"),
                (0x0642, 0,          "unidentified (observed constant)"),
                (0x0641, 1,          "unidentified (observed constant)"),
                (0x0601, 5,          "unidentified (observed constant)"),
                (0x1202, freqKHz,    "Frequency"),
                (0x0600, 0x1000,     "unidentified (transitional state?)"),
                (0x0681, 1,          "unidentified (observed constant)"),
                (0x0682, 0,          "unidentified (observed constant)"),
                (0x0683, 0,          "unidentified (observed constant)"),
                (0x1202, freqKHz,    "Frequency (repeat, as observed)"),
                (0x0681, 1,          "unidentified (repeat, as observed)"),
                (0x0681, 1,          "unidentified (repeat, as observed)"),
                (0x0682, 0,          "unidentified (repeat, as observed)"),
                (0x0683, 0,          "unidentified (repeat, as observed)"),
                (0x0680, mode,       "Mode select (続報19)"),
                (0x0690, constellation, "Constellation"),
            };
            if (hasOfdmFields)
            {
                seq.Add((0x0684, bandwidth,     "Bandwidth"));
                seq.Add((0x0691, fft,           "FFT"));
                seq.Add((0x0693, coderate,      "CodeRate"));
                seq.Add((0x0692, guardinterval, "GuardInterval"));
            }
            if (hasTimeInterleave)
            {
                seq.Add((0x0694, timeinterleave, "TimeInterleavce"));
            }
            if (isDtmb)
            {
                seq.Add((0x0684, bandwidth, "Bandwidth (DTMB)"));
                seq.Add((0x0692, coderate,  "CodeRate (DTMB, 続報22: immediately overwritten by Carrier below)"));
                seq.Add((0x0692, carrier,   "Carrier (DTMB, 続報22: overwrites CodeRate's write to the same address)"));
                seq.Add((0x0694, frame,     "Frame (DTMB)"));
                seq.Add((0x0691, interleave, "Interleave (DTMB)"));
            }
            seq.Add((0x0600, 1,          "unidentified (transitional state?)"));
            seq.Add((0x1228, 0,          "RF power bank, always 0"));
            seq.Add((0x1229, dacPacked,  "DACGain"));
            seq.Add((0x1221, 2,          "RF power bank, hardcoded literal 2"));
            seq.Add((0x1290, extReg,     "EXT-tagged register, meaning unknown"));
            seq.Add((0x1220, 0x78122901, "commit/strobe trigger (LSB=1)"));
            seq.Add((0x0629, 0,          "unidentified (post-commit)"));
            seq.Add((0x0629, 0,          "unidentified (post-commit, repeat)"));

            foreach (var (addr, data, label) in seq)
            {
                SetAddress(addr);
                Thread.Sleep(20);
                WriteRegister(data);
                Console.WriteLine($"  0x{addr:X4} <= 0x{data:X8}   ({label})");
                Console.Out.Flush();
                Thread.Sleep(20);
            }

            Console.WriteLine("Configure sequence complete. Reading back key registers...");
            foreach (var addr in new ushort[] { 0x1202, 0x0690, 0x0684, 0x0691, 0x0693, 0x0692, 0x0694, 0x1229, 0x1220 })
            {
                SetAddress(addr);
                Thread.Sleep(20);
                var (echoAddr, data) = ReadRegister();
                Console.WriteLine($"  0x{addr:X4} -> 0x{data:X8}");
                Console.Out.Flush();
                Thread.Sleep(20);
            }
        }

        /// <summary>
        /// 2026-07-26: mnservice.exe's CmdChannelStop was observed to write 0x0600=0x2000
        /// (docs/protocol/modulation_capabilities.md "続報9" register-lifecycle table labeled it
        /// "ChannelStop/teardown"). Sent standalone via tools/custom_sender's DirectUsbSession and
        /// confirmed via RTL-SDR that it actually cuts RF output (docs/protocol/
        /// modulation_capabilities.md "続報15" -- +44dB active plateau dropped to noise-floor
        /// level afterward). This is the CLI counterpart of that same single write, so
        /// --configure's RF output can be turned off without needing custom_sender's GUI.
        /// </summary>
        private static void RunStopSequence()
        {
            Console.WriteLine("  0x0600 <= 0x00002000   (ChannelStop/teardown, confirmed via RTL-SDR to cut RF output)");
            Console.Out.Flush();
            SetAddress(0x0600);
            Thread.Sleep(20);
            WriteRegister(0x2000);
            Thread.Sleep(20);
            Console.WriteLine("  Stop command sent.");
        }

        /// <summary>
        /// Sends a burst of synthetic null-TS-packet slices over the bulk OUT endpoint, WITHOUT
        /// replicating the periodic 0x4A/0x4E flow-control notify/readback pair that mnservice.exe
        /// interleaves with real streaming (tools/usb_capture/README.md "続報"/"続報2" -- that
        /// notify's exact address-encoding semantics aren't fully understood yet). This is a
        /// deliberately narrow experiment: does bulk-OUT payload alone (on top of the register
        /// configuration already sent by RunConfigureSequence) change anything observable, or does
        /// the device require the notify handshake before it treats bulk data as real? Either
        /// answer is informative; see tools/direct_usb/README.md for the result once run.
        /// Packets use the standard MPEG-TS "null packet" convention (PID=0x1FFF) so any real ISDB-T
        /// demux downstream would recognize them as stuffing rather than malformed data.
        /// </summary>
        /// <summary>
        /// 2026-07-27: interleaves 0x0629 polling (control transfers) with the bulk-OUT TS slice
        /// writes. 0x0629 was already characterized in three static states (tools/usb_capture/
        /// README.md 続報9): idle=439(0x1B7), configured-but-no-TS=0, streaming=observed varying
        /// 7-472. This logs a time series during an actual --stream run from this tool, to see
        /// whether the value's behavior over time looks like a ring-buffer occupancy gauge
        /// (bounded, fluctuating around some steady-state) versus something else (monotonic growth
        /// suggesting overflow, a fixed constant unrelated to the data volume, etc.).
        /// </summary>
        private static void RunStreamTest(int seconds)
        {
            Console.WriteLine($"  Streaming synthetic null-TS slices over bulk OUT (pipe 0x{PIPE_ID_BULK_OUT:X2}) for {seconds}s, polling 0x0629 concurrently...");
            Console.Out.Flush();

            byte[] slice = BuildNullTsSlice();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pollSw = System.Diagnostics.Stopwatch.StartNew();
            long slicesSent = 0;
            long bytesSent = 0;

            while (sw.Elapsed.TotalSeconds < seconds)
            {
                uint transferred;
                bool ok = WinUsb_WritePipe(_winusbHandle, PIPE_ID_BULK_OUT, slice, (uint)slice.Length, out transferred, IntPtr.Zero);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    Console.WriteLine($"  WinUsb_WritePipe failed after {slicesSent} slices, Win32 error 0x{err:X} ({err}). Stopping stream test.");
                    return;
                }
                slicesSent++;
                bytesSent += transferred;

                if (pollSw.Elapsed.TotalMilliseconds >= 200)
                {
                    // mnservice.exe's own observed pattern (tools/usb_capture/README.md 続報9) pairs
                    // a 0x0629=0 WRITE with each read, ~19ms apart -- replicate that pairing here in
                    // case the write itself is what triggers a fresh sample/latch.
                    SetAddress(0x0629);
                    WriteRegister(0);
                    SetAddress(0x0629);
                    var (echoAddr, data) = ReadRegister();
                    Console.WriteLine($"  t={sw.Elapsed.TotalSeconds:F2}s slices={slicesSent} bytes={bytesSent} 0x0629={data} (0x{data:X})");
                    Console.Out.Flush();
                    pollSw.Restart();
                }
            }

            Console.WriteLine($"  Stream test done: {slicesSent} slices / {bytesSent} bytes sent in {sw.Elapsed.TotalSeconds:F1}s, no pipe errors.");
            Console.Out.Flush();
        }

        private static byte[] BuildNullTsSlice()
        {
            byte[] slice = new byte[SLICE_SIZE_BYTES];
            byte cc = 0;
            for (int p = 0; p < TS_PACKETS_PER_SLICE; p++)
            {
                int off = p * TS_PACKET_SIZE;
                slice[off + 0] = 0x47;                     // sync byte
                slice[off + 1] = 0x1F;                     // TEI=0, PUSI=0, priority=0, PID[12:8]=0x1F
                slice[off + 2] = 0xFF;                      // PID[7:0] -> PID=0x1FFF (standard null packet)
                slice[off + 3] = (byte)(0x10 | (cc & 0x0F)); // no scrambling, payload-only, continuity counter
                for (int b = 4; b < TS_PACKET_SIZE; b++)
                {
                    slice[off + b] = 0xFF;                  // standard null-packet stuffing payload
                }
                cc = (byte)((cc + 1) & 0x0F);
            }
            return slice;
        }

        private static void SetAddress(ushort addr)
        {
            var pkt = new RawSetupPacket
            {
                RequestType = BM_HOST_TO_DEVICE_VENDOR_DEVICE,
                Request = REQ_SET_ADDRESS,
                Value0 = 0, Value1 = 0,
                Index0 = (byte)((addr >> 8) & 0xFF),
                Index1 = (byte)(addr & 0xFF),
                Length0 = 0, Length1 = 0
            };
            SendControlTransfer(pkt, null);
        }

        private static void WriteRegister(uint data)
        {
            var pkt = new RawSetupPacket
            {
                RequestType = BM_HOST_TO_DEVICE_VENDOR_DEVICE,
                Request = REQ_WRITE,
                Value0 = (byte)((data >> 24) & 0xFF),
                Value1 = (byte)((data >> 16) & 0xFF),
                Index0 = (byte)((data >> 8) & 0xFF),
                Index1 = (byte)(data & 0xFF),
                Length0 = 0, Length1 = 0
            };
            SendControlTransfer(pkt, null);
        }

        private static Tuple<uint, uint> ReadRegister()
        {
            var pkt = new RawSetupPacket
            {
                RequestType = BM_DEVICE_TO_HOST_VENDOR_DEVICE,
                Request = REQ_READ,
                Value0 = 0, Value1 = 0,
                Index0 = 0, Index1 = 0,
                Length0 = 8, Length1 = 0
            };
            byte[] buf = new byte[8];
            SendControlTransfer(pkt, buf);
            uint echoAddr = (uint)((buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3]);
            uint data = (uint)((buf[4] << 24) | (buf[5] << 16) | (buf[6] << 8) | buf[7]);
            return Tuple.Create(echoAddr, data);
        }

        private static void SendControlTransfer(RawSetupPacket pkt, byte[] buffer)
        {
            uint lengthTransferred;
            bool ok;
            if (buffer != null)
            {
                ok = WinUsb_ControlTransfer(_winusbHandle, pkt, buffer, (uint)buffer.Length, out lengthTransferred, IntPtr.Zero);
            }
            else
            {
                ok = WinUsb_ControlTransfer(_winusbHandle, pkt, null, 0, out lengthTransferred, IntPtr.Zero);
            }
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"WinUsb_ControlTransfer failed, Win32 error 0x{err:X} ({err})");
            }
        }

        private static bool OpenDevice()
        {
            string devicePath = FindDevicePath(DeviceInterfaceGuid);
            if (devicePath == null)
            {
                Console.WriteLine("  Device path not found for interface GUID " + DeviceInterfaceGuid);
                return false;
            }
            Console.WriteLine("  Device path: " + devicePath);

            _fileHandle = CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (_fileHandle.IsInvalid)
            {
                Console.WriteLine("  CreateFile failed, Win32 error " + Marshal.GetLastWin32Error());
                return false;
            }

            if (!WinUsb_Initialize(_fileHandle, out _winusbHandle))
            {
                Console.WriteLine("  WinUsb_Initialize failed, Win32 error " + Marshal.GetLastWin32Error());
                return false;
            }

            Console.WriteLine("  WinUSB handle opened successfully.");
            return true;
        }

        private static void CloseDevice()
        {
            if (_winusbHandle != IntPtr.Zero)
            {
                WinUsb_Free(_winusbHandle);
                _winusbHandle = IntPtr.Zero;
            }
            if (_fileHandle != null && !_fileHandle.IsInvalid)
            {
                _fileHandle.Close();
            }
        }

        private static string FindDevicePath(Guid interfaceGuid)
        {
            IntPtr hDevInfo = SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (hDevInfo == IntPtr.Zero || hDevInfo.ToInt64() == -1)
            {
                return null;
            }

            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);

                if (!SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref interfaceGuid, 0, ref ifData))
                {
                    return null;
                }

                int requiredSize = 0;
                SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);

                var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                // Well-known P/Invoke workaround: cbSize must be set to this fixed value
                // (NOT Marshal.SizeOf(detail), which is wrong here due to struct packing around
                // the embedded char array) -- 8 on x64, 6 on x86 (4-byte DWORD + 2-byte char
                // alignment), regardless of the ByValTStr buffer length declared below.
                detail.cbSize = IntPtr.Size == 8 ? 8 : 6;

                if (!SetupDiGetDeviceInterfaceDetail2(hDevInfo, ref ifData, ref detail, requiredSize, ref requiredSize, IntPtr.Zero))
                {
                    return null;
                }
                return detail.DevicePath;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }
        }

        // ---- P/Invoke declarations ----

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct RawSetupPacket
        {
            public byte RequestType;
            public byte Request;
            public byte Value0, Value1;
            public byte Index0, Index1;
            public byte Length0, Length1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public int cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string DevicePath;
        }

        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto, EntryPoint = "SetupDiGetDeviceInterfaceDetail")]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto, EntryPoint = "SetupDiGetDeviceInterfaceDetail")]
        private static extern bool SetupDiGetDeviceInterfaceDetail2(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            ref SP_DEVICE_INTERFACE_DETAIL_DATA deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ControlTransfer(IntPtr interfaceHandle, RawSetupPacket setupPacket,
            byte[] buffer, uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_WritePipe(IntPtr interfaceHandle, byte pipeId,
            byte[] buffer, uint bufferLength, out uint lengthTransferred, IntPtr overlapped);
    }

    internal sealed class SafeFileHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFileHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandleNative(handle);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
        private static extern bool CloseHandleNative(IntPtr handle);
    }
}
