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

        private static SafeFileHandle _fileHandle;
        private static IntPtr _winusbHandle = IntPtr.Zero;

        private static int Main(string[] args)
        {
            bool writeMode = false;
            bool configureMode = false;
            ushort? writeAddr = null;
            uint? writeData = null;

            uint freqKHz = 473000;
            uint constellation = 1;   // QPSK
            uint bandwidth = 6;
            uint fft = 1;
            uint coderate = 3;
            uint guardinterval = 1;
            uint timeinterleave = 3;
            int dacgain = -10;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--write") writeMode = true;
                else if (args[i] == "--addr") writeAddr = Convert.ToUInt16(args[++i], 16);
                else if (args[i] == "--data") writeData = Convert.ToUInt32(args[++i], 16);
                else if (args[i] == "--configure") configureMode = true;
                else if (args[i] == "--freq") freqKHz = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--constellation") constellation = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--bandwidth") bandwidth = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--fft") fft = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--coderate") coderate = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--guardinterval") guardinterval = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--timeinterleave") timeinterleave = Convert.ToUInt32(args[++i]);
                else if (args[i] == "--dacgain") dacgain = Convert.ToInt32(args[++i]);
            }

            Console.WriteLine("=== XHeadDirectUsb: raw WinUSB register-bus probe (bypasses mnservice.exe) ===");
            Console.WriteLine(writeMode ? "Mode: WRITE (explicit --write given)" :
                configureMode ? "Mode: CONFIGURE (full ChannelStart write-sequence replay, bypasses mnservice.exe entirely)" :
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
                if (configureMode)
                {
                    RunConfigureSequence(freqKHz, constellation, bandwidth, fft, coderate, guardinterval, timeinterleave, dacgain);
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
                        // Unidentified, stable across runs
                        0x0600, 0x0601, 0x0602, 0x0629, 0x0640, 0x0641, 0x0642,
                        0x0680, 0x0681, 0x0682, 0x0683,
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
        /// </summary>
        private static void RunConfigureSequence(uint freqKHz, uint constellation, uint bandwidth, uint fft,
            uint coderate, uint guardinterval, uint timeinterleave, int dacgain)
        {
            byte dacByte = unchecked((byte)dacgain);
            uint dacPacked = (uint)((dacByte << 8) | dacByte);
            uint extReg = 0x45585400u | 0x02; // observed as constant 0x45585402 in every capture; meaning unknown

            Console.WriteLine("  Frequency=" + freqKHz + "kHz Constellation=" + constellation + " Bandwidth=" + bandwidth +
                " FFT=" + fft + " CodeRate=" + coderate + " GuardInterval=" + guardinterval +
                " TimeInterleavce=" + timeinterleave + " DACGain=" + dacgain);
            Console.Out.Flush();

            var seq = new (ushort addr, uint data, string label)[]
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
                (0x0680, 5,          "unidentified (observed constant)"),
                (0x0690, constellation, "Constellation"),
                (0x0684, bandwidth,     "Bandwidth"),
                (0x0691, fft,           "FFT"),
                (0x0693, coderate,      "CodeRate"),
                (0x0692, guardinterval, "GuardInterval"),
                (0x0694, timeinterleave,"TimeInterleavce"),
                (0x0600, 1,          "unidentified (transitional state?)"),
                (0x1228, 0,          "RF power bank, always 0"),
                (0x1229, dacPacked,  "DACGain"),
                (0x1221, 2,          "RF power bank, hardcoded literal 2"),
                (0x1290, extReg,     "EXT-tagged register, meaning unknown"),
                (0x1220, 0x78122901, "commit/strobe trigger (LSB=1)"),
                (0x0629, 0,          "unidentified (post-commit)"),
                (0x0629, 0,          "unidentified (post-commit, repeat)"),
            };

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
    }

    internal sealed class SafeFileHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFileHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandleNative(handle);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
        private static extern bool CloseHandleNative(IntPtr handle);
    }
}
