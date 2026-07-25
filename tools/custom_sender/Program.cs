using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using mnFramework.grpc;

namespace XHeadSender
{
    /// <summary>
    /// XHEAD-STUDIO のバックグラウンドサービス (mnservice.exe) へ、公式GUIを介さず
    /// 直接 gRPC 接続して疎通確認を行う最小スケルトン。
    /// 事前条件: XHEAD-STUDIO をインストール後、mnservice.exe (または xhead_studio.exe) が
    /// 起動しており、localhost:50051 で待ち受けていること。
    /// </summary>
    /// <summary>
    /// Consumes the subscribeService server-streaming event feed in the background, mirroring
    /// mnClient.handleClientProcess()'s "await reader_.ResponseStream.MoveNext()" loop. Keeps the
    /// latest msSource seen per HandleID so callers can poll for async readiness (e.g. a URL
    /// source finishing probing a file) without a dedicated "get source" RPC, which doesn't exist.
    /// </summary>
    internal sealed class EventWatcher
    {
        private readonly ConcurrentDictionary<uint, msSource> _latestSources = new ConcurrentDictionary<uint, msSource>();
        private readonly ConcurrentDictionary<uint, msEventStatus> _latestStatus = new ConcurrentDictionary<uint, msEventStatus>();
        private Task _pump;

        // msEvent.Status is an msEventStatus WRAPPER, not a bare msStatus -- and critically it has
        // its own Content (msContent) field (oneof branch 10). mnClient.handleSource() in the
        // official GUI only ever reads .Status.Status and silently discards .Status.Content, which
        // is why reading the decompiled wrapper code made it look like this event never carries
        // Content. It does, in the raw protobuf -- just read it directly instead of going through
        // the wrapper's narrower accessor.
        public msEventStatus WaitForStatusReady(uint handle, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_latestStatus.TryGetValue(handle, out var s) && s.Status == msStatus.StatusReady) return s;
                Thread.Sleep(200);
            }
            _latestStatus.TryGetValue(handle, out var last);
            return last;
        }

        public void Start(msBroadcastService.msBroadcastServiceClient client, uint clientId)
        {
            var req = new msRequest { Cmd = msServiceCmd.CmdSubscribe, ClientID = clientId };
            var call = client.subscribeService(req);
            _pump = Task.Run(async () =>
            {
                try
                {
                    while (await call.ResponseStream.MoveNext(CancellationToken.None))
                    {
                        var ev = call.ResponseStream.Current;
                        if (ev.ParamCase == msEvent.ParamOneofCase.Source)
                        {
                            _latestSources[ev.HandleID] = ev.Source;
                            Console.WriteLine($"  [event] {ev.ID} HandleID={ev.HandleID} Source.Status={ev.Source.Status} Programs={ev.Source.Content?.Programs.Count ?? 0}");
                        }
                        else if (ev.ParamCase == msEvent.ParamOneofCase.Update)
                        {
                            Console.WriteLine($"  [event] {ev.ID} HandleID={ev.HandleID} Update.Status={ev.Update.Status} Update.Properties={ev.Update.Properties.Count}");
                            foreach (var p in ev.Update.Properties)
                            {
                                Console.WriteLine($"    Property \"{p.Property.Name}\" values={p.Param.Values.Count}");
                            }
                        }
                        else if (ev.ParamCase == msEvent.ParamOneofCase.Status)
                        {
                            if (ev.HandleID != 0) _latestStatus[ev.HandleID] = ev.Status;
                            Console.WriteLine($"  [event] {ev.ID} HandleID={ev.HandleID} Status={ev.Status.Status} ContentPrograms={ev.Status.Content?.Programs.Count ?? 0}");
                        }
                        else if (ev.ParamCase != msEvent.ParamOneofCase.Profiler)
                        {
                            Console.WriteLine($"  [event] {ev.ID} HandleID={ev.HandleID} ParamCase={ev.ParamCase}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [event] stream ended: {ex.Message}");
                }
            });
        }

        public msSource WaitForReadySource(uint sourceHandle, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_latestSources.TryGetValue(sourceHandle, out var src) && (src.Content?.Programs.Count ?? 0) > 0)
                {
                    return src;
                }
                Thread.Sleep(200);
            }
            _latestSources.TryGetValue(sourceHandle, out var last);
            return last;
        }
    }

    internal static class Program
    {
        private const string ServiceAddress = "localhost:50051";

        private static int Main(string[] args)
        {
            // Grpc.Core (legacy grpc-csharp) はネイティブ拡張 grpc_csharp_ext.x64/x86.dll を
            // P/Invoke でロードする。当該DLLは自前配布せず、既存の XHEAD-STUDIO インストールの
            // ものをそのまま使うため、検索パスに追加しておく。
            string xheadDir = Environment.GetEnvironmentVariable("XHEAD_STUDIO_DIR")
                               ?? @"C:\Program Files\Micomsoft\XHEAD-STUDIO";
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", xheadDir + ";" + path);

            Console.WriteLine($"[XHeadSender] connecting to {ServiceAddress} ...");

            var channel = new Channel(ServiceAddress, ChannelCredentials.Insecure);
            var client = new msBroadcastService.msBroadcastServiceClient(channel);

            try
            {
                var request = new msRequest
                {
                    Cmd = msServiceCmd.CmdConnect,
                    ClientID = 0,
                    Client = new msClientParam
                    {
                        Name = "XHeadSender",
                        Privilege = msPrivilege.PrivilegeControl
                    }
                };

                var deadline = DateTime.UtcNow.AddSeconds(5);
                msResponse response = client.connectService(request, deadline: deadline);

                Console.WriteLine($"  Cmd    = {response.Cmd}");
                Console.WriteLine($"  Status = {response.Status}");
                Console.WriteLine($"  Result = {response.Result}");
                switch (response.ParamCase)
                {
                    case msResponse.ParamOneofCase.Client:
                        Console.WriteLine($"  Client.HandleID   = {response.Client.HandleID}");
                        Console.WriteLine($"  Client.Name       = {response.Client.Name}");
                        Console.WriteLine($"  Client.Privlege   = {response.Client.Privlege}");
                        Console.WriteLine($"  Client.Engines    = {response.Client.Engines.Count}");
                        Console.WriteLine($"  Client.Captures   = {response.Client.Captures.Count}");
                        Console.WriteLine($"  Client.Channels   = {response.Client.Channels.Count}");
                        Console.WriteLine($"  Client.Sources    = {response.Client.Sources.Count}");
                        Console.WriteLine($"  Client.Outputs    = {response.Client.Outputs.Count}");
                        break;
                    case msResponse.ParamOneofCase.ErrMessage:
                        Console.WriteLine($"  ErrMessage = {response.ErrMessage}");
                        break;
                }

                if (response.Result == msResult.ResultSuccess)
                {
                    Console.WriteLine("[XHeadSender] connectService OK.");

                    var watcher = new EventWatcher();
                    watcher.Start(client, response.Client.HandleID);

                    var msClient = response.Client;
                    uint firstModulationOutputHandle = 0;
                    Console.WriteLine();
                    Console.WriteLine("=== Outputs ===");
                    foreach (var output in msClient.Outputs)
                    {
                        Console.WriteLine($"[Output] HandleID={output.HandleID} ObjectID={output.ObjectID} ObjectType={output.ObjectType} Name={output.Name} Path={output.Path}");
                        if (output.ObjectType == msObjectType.ObjectOutputModulation && firstModulationOutputHandle == 0)
                        {
                            firstModulationOutputHandle = output.HandleID;
                        }
                        foreach (var prop in output.Properties)
                        {
                            DumpProperty(prop, 1);
                        }
                    }

                    Console.WriteLine();
                    Console.WriteLine("=== Captures (pre-existing hardware capture devices) ===");
                    foreach (var cap in msClient.Captures)
                    {
                        Console.WriteLine($"[Capture] HandleID={cap.HandleID} ObjectType={cap.ObjectType} Name={cap.Name} Path={cap.Path} CaptureType={cap.CaptureType} Status={cap.Status} Programs={cap.Content?.Programs.Count ?? 0}");
                        foreach (var p in cap.Content?.Programs ?? new Google.Protobuf.Collections.RepeatedField<msContent.Types.Program>())
                        {
                            Console.WriteLine($"    Program ID={p.ID} Streams={p.Streams.Count}");
                            foreach (var s in p.Streams)
                            {
                                Console.WriteLine($"      Stream Index={s.Index} ID={s.ID} Format={s.Format}");
                            }
                        }
                    }

                    Console.WriteLine();
                    Console.WriteLine("=== Channels ===");
                    foreach (var ch in msClient.Channels)
                    {
                        Console.WriteLine($"[Channel] HandleID={ch.HandleID} Name={ch.Name}");
                    }

                    Console.WriteLine();
                    Console.WriteLine("=== Sources ===");
                    foreach (var src in msClient.Sources)
                    {
                        Console.WriteLine($"[Source] HandleID={src.HandleID}");
                    }

                    Console.WriteLine();
                    Console.WriteLine("=== CmdChannelOpen probe ===");
                    try
                    {
                        var openReq = new msRequest
                        {
                            Cmd = msServiceCmd.CmdChannelOpen,
                            ClientID = msClient.HandleID,
                            HandleID = firstModulationOutputHandle,
                            Channel = new msChannelParam { Name = "XHeadSenderProbe" }
                        };
                        var openResp = client.sendRequest(openReq, deadline: DateTime.UtcNow.AddSeconds(5));
                        Console.WriteLine($"  Result={openResp.Result} ParamCase={openResp.ParamCase}");
                        if (openResp.ParamCase == msResponse.ParamOneofCase.Channel)
                        {
                            var newCh = openResp.Channel;
                            Console.WriteLine($"[Channel] HandleID={newCh.HandleID} ObjectID={newCh.ObjectID} OutputID={newCh.OutputID} Name={newCh.Name} ObjectType={newCh.ObjectType} Status={newCh.Status}");
                            foreach (var prop in newCh.Properties)
                            {
                                DumpProperty(prop, 1);
                            }

                            Console.WriteLine();
                            Console.WriteLine("=== Flat Param.Values (raw, no tree) ===");
                            foreach (var prop in newCh.Properties)
                            {
                                Console.WriteLine($"  [{prop.Property.Name}] {prop.Param.Values.Count} value(s):");
                                foreach (var v in prop.Param.Values)
                                {
                                    Console.WriteLine($"    FieldID={v.FieldID} {DumpVariant(v)}");
                                }
                            }

                            var closeReq = new msRequest
                            {
                                Cmd = msServiceCmd.CmdChannelClose,
                                ClientID = msClient.HandleID,
                                HandleID = newCh.HandleID
                            };
                            var closeResp = client.sendRequest(closeReq, deadline: DateTime.UtcNow.AddSeconds(5));
                            Console.WriteLine($"  CmdChannelClose Result={closeResp.Result}");

                            // CmdApplyConfig (sendRequest) returned "unhandled command : [5]", and
                            // embedding Properties directly in CmdChannelOpen returned
                            // "unknown property" (Properties at Open-time are scoped to the OUTPUT,
                            // which has none). Reading xTaskStartChannel.cs / xHeadConfig.applyChannel
                            // in the decompiled GUI shows the real flow: modulation/channel/codec/EPG
                            // properties ride along with CmdChannelStart, not Open. Calling
                            // ChannelStart with no Source/Content attached crashes mnservice.exe
                            // natively (confirmed 2026-07-24) -- so wire up a real Source first.
                            RunFullPipelineTest(client, msClient, firstModulationOutputHandle, watcher);
                        }
                        else if (openResp.ParamCase == msResponse.ParamOneofCase.ErrMessage)
                        {
                            Console.WriteLine($"  ErrMessage={openResp.ErrMessage}");
                        }
                    }
                    catch (RpcException ex)
                    {
                        Console.WriteLine($"  CmdChannelOpen RPC error: {ex.Status}");
                    }

                    var disconnect = new msRequest { Cmd = msServiceCmd.CmdDisconnect, ClientID = 0 };
                    client.disconnectService(disconnect, deadline: DateTime.UtcNow.AddSeconds(5));
                    Console.WriteLine("[XHeadSender] disconnectService OK.");
                    return 0;
                }

                Console.Error.WriteLine("[XHeadSender] connectService failed (see Result/ErrMessage above).");
                return 1;
            }
            catch (RpcException ex)
            {
                Console.Error.WriteLine($"[XHeadSender] gRPC error: {ex.Status}");
                Console.Error.WriteLine("mnservice.exe が起動していない、または localhost:50051 で待ち受けていない可能性があります。");
                Console.Error.WriteLine("XHEAD-STUDIO (xhead_studio.exe) を一度起動してサービスを立ち上げてから再実行してください。");
                return 2;
            }
            finally
            {
                channel.ShutdownAsync().Wait();
            }
        }

        private const string TestSourceFile = @"C:\Users\aoiro\Videos\ts\test_clip_10s.ts";

        /// <summary>
        /// Quick standalone check: does opening a SourceCapture (referencing an already-enumerated
        /// hardware capture device, e.g. desktop capture) populate msSource.Content synchronously
        /// in the CmdSourceOpen response, unlike SourceUrl which always comes back empty/StatusPrepare?
        /// Opens, dumps, and immediately closes -- does not proceed to ProgramApply/ChannelStart.
        /// </summary>
        private static void RunCaptureSourceProbe(msBroadcastService.msBroadcastServiceClient client, msClient msClient, EventWatcher watcher)
        {
            Console.WriteLine();
            Console.WriteLine("=== SourceCapture probe (desktop capture, should be format-instant) ===");

            msCapture desktopCap = null;
            foreach (var cap in msClient.Captures)
            {
                if (cap.CaptureType == msCaptureType.Dxgidesktop) { desktopCap = cap; break; }
            }
            if (desktopCap == null)
            {
                Console.WriteLine("  No Dxgidesktop capture found, skipping.");
                return;
            }
            Console.WriteLine($"  Using capture: {desktopCap.Name} ({desktopCap.Path}) HandleID={desktopCap.HandleID}");

            var capOpenReq = new msRequest { Cmd = msServiceCmd.CmdCaptureOpen, ClientID = msClient.HandleID, HandleID = desktopCap.HandleID };
            var capOpenResp = client.sendRequest(capOpenReq, deadline: DateTime.UtcNow.AddSeconds(8));
            Console.WriteLine($"  CaptureOpen: Result={capOpenResp.Result} Status={capOpenResp.Status} ParamCase={capOpenResp.ParamCase}" +
                (capOpenResp.HasErrMessage ? $" ErrMessage={capOpenResp.ErrMessage}" : ""));

            Console.WriteLine("  Waiting 3s for capture to reach Ready...");
            Thread.Sleep(3000);

            var capStartReq = new msRequest { Cmd = msServiceCmd.CmdCaptureStart, ClientID = msClient.HandleID, HandleID = desktopCap.HandleID };
            var capStartResp = client.sendRequest(capStartReq, deadline: DateTime.UtcNow.AddSeconds(8));
            Console.WriteLine($"  CaptureStart: Result={capStartResp.Result} Status={capStartResp.Status} ParamCase={capStartResp.ParamCase}" +
                (capStartResp.HasErrMessage ? $" ErrMessage={capStartResp.ErrMessage}" : ""));

            Console.WriteLine("  Waiting 2s then peeking Captures via a second connection (Captures are shared, unlike Sources)...");
            Thread.Sleep(2000);
            var peekedCap = PeekCaptureViaSecondaryConnection(desktopCap.HandleID);
            if (peekedCap != null)
            {
                Console.WriteLine($"  Peeked capture: Status={peekedCap.Status} Programs={peekedCap.Content?.Programs.Count ?? 0}");
                foreach (var p in peekedCap.Content?.Programs ?? new Google.Protobuf.Collections.RepeatedField<msContent.Types.Program>())
                {
                    Console.WriteLine($"    Program ID={p.ID} Streams={p.Streams.Count}");
                    foreach (var s in p.Streams) Console.WriteLine($"      Stream Index={s.Index} ID={s.ID} Format={s.Format}");
                }
            }

            var capParam = new msCaptureParam();
            capParam.Content.Add(new msCaptureParam.Types.Capture { HandleID = desktopCap.HandleID, ProgramID = 0, StreamIndex = 0 });

            var req = new msRequest
            {
                Cmd = msServiceCmd.CmdSourceOpen,
                ClientID = msClient.HandleID,
                Source = new msSourceParam { Mode = msSourceMode.SourceCapture, Name = "XHeadSenderCaptureProbe", Capture = capParam }
            };
            msResponse resp;
            try
            {
                resp = client.sendRequest(req, deadline: DateTime.UtcNow.AddSeconds(8));
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"  SourceOpen(Capture) RPC error: {ex.Status}");
                return;
            }
            Console.WriteLine($"  Result={resp.Result} ParamCase={resp.ParamCase}" + (resp.HasErrMessage ? $" ErrMessage={resp.ErrMessage}" : ""));
            if (resp.ParamCase == msResponse.ParamOneofCase.Source)
            {
                var src = resp.Source;
                Console.WriteLine($"  Source: HandleID={src.HandleID} Status={src.Status} Programs={src.Content?.Programs.Count ?? 0}");
                foreach (var p in src.Content?.Programs ?? new Google.Protobuf.Collections.RepeatedField<msContent.Types.Program>())
                {
                    Console.WriteLine($"    Program ID={p.ID} Streams={p.Streams.Count}");
                    foreach (var s in p.Streams) Console.WriteLine($"      Stream Index={s.Index} ID={s.ID} Format={s.Format}");
                }
                var closeReq = new msRequest { Cmd = msServiceCmd.CmdSourceClose, ClientID = msClient.HandleID, HandleID = src.HandleID };
                var closeResp = client.sendRequest(closeReq, deadline: DateTime.UtcNow.AddSeconds(5));
                Console.WriteLine($"  SourceClose: Result={closeResp.Result}");
            }

            var capStopReq = new msRequest { Cmd = msServiceCmd.CmdCaptureStop, ClientID = msClient.HandleID, HandleID = desktopCap.HandleID };
            client.sendRequest(capStopReq, deadline: DateTime.UtcNow.AddSeconds(5));
            var capCloseReq = new msRequest { Cmd = msServiceCmd.CmdCaptureClose, ClientID = msClient.HandleID, HandleID = desktopCap.HandleID };
            var capCloseResp = client.sendRequest(capCloseReq, deadline: DateTime.UtcNow.AddSeconds(5));
            Console.WriteLine($"  CaptureClose: Result={capCloseResp.Result}");
        }

        private static void RunFullPipelineTest(msBroadcastService.msBroadcastServiceClient client, msClient msClient, uint outputHandle, EventWatcher watcher)
        {
            uint clientId = msClient.HandleID;
            Console.WriteLine();
            Console.WriteLine("=== Full pipeline test: ChannelOpen -> ProgramAdd/Commit -> SourceOpen -> ProgramApply -> SourceStart -> ChannelStart ===");
            Console.Out.Flush();

            var openReq = new msRequest
            {
                Cmd = msServiceCmd.CmdChannelOpen,
                ClientID = clientId,
                HandleID = outputHandle,
                Channel = new msChannelParam { Name = "XHeadSenderFullTest" }
            };
            var openResp = client.sendRequest(openReq, deadline: DateTime.UtcNow.AddSeconds(5));
            Console.WriteLine($"  ChannelOpen: Result={openResp.Result} ParamCase={openResp.ParamCase}" +
                (openResp.HasErrMessage ? $" ErrMessage={openResp.ErrMessage}" : ""));
            Console.Out.Flush();
            if (openResp.ParamCase != msResponse.ParamOneofCase.Channel) return;
            uint chHandle = openResp.Channel.HandleID;

            // xTaskCreateChannel.cs (decompiled GUI) revealed the official app calls
            // CmdChannelStart with a much larger property set than just mModulationParam --
            // also mPSRFPowerAdjust, mMTSChannelParam, mPSEncodeParam (the encoder config!) and
            // mEPGSimpleParam -- and calls it once at device-connect time, BEFORE any Source ever
            // exists. msChannel (ChannelOpen's response type) has its own Properties field we've
            // never dumped before; check whether it carries descriptors/defaults for these groups
            // the same way ProgramAdd's response carried mModulationParam's.
            Console.WriteLine($"  Channel.Properties = {openResp.Channel.Properties.Count}");
            foreach (var prop in openResp.Channel.Properties) DumpProperty(prop, 2);
            Console.Out.Flush();

            var addReq = new msRequest { Cmd = msServiceCmd.CmdProgramAdd, ClientID = clientId, HandleID = chHandle };
            var addResp = client.sendRequest(addReq, deadline: DateTime.UtcNow.AddSeconds(5));
            Console.WriteLine($"  ProgramAdd: Result={addResp.Result} ParamCase={addResp.ParamCase}" +
                (addResp.HasErrMessage ? $" ErrMessage={addResp.ErrMessage}" : ""));
            Console.Out.Flush();
            if (addResp.ParamCase != msResponse.ParamOneofCase.Program)
            {
                CloseChannel(client, clientId, chHandle);
                return;
            }
            int programIndex = addResp.Program.Index;
            Console.WriteLine($"  Program.Properties = {addResp.Program.Properties.Count}");
            foreach (var prop in addResp.Program.Properties) DumpProperty(prop, 2);
            Console.Out.Flush();

            var commitReq = new msRequest { Cmd = msServiceCmd.CmdProgramCommit, ClientID = clientId, HandleID = chHandle, Index = programIndex };
            foreach (var prop in addResp.Program.Properties)
            {
                commitReq.Properties.Add(new msPropertyParam { Name = prop.Property.Name, Values = { prop.Param.Values } });
            }
            var commitResp = client.sendRequest(commitReq, deadline: DateTime.UtcNow.AddSeconds(5));
            Console.WriteLine($"  ProgramCommit: Result={commitResp.Result}" +
                (commitResp.HasErrMessage ? $" ErrMessage={commitResp.ErrMessage}" : ""));
            Console.Out.Flush();

            if (commitResp.Result != msResult.ResultSuccess)
            {
                CloseChannel(client, clientId, chHandle);
                return;
            }

            // MAJOR revision: decompiled xTaskCreateChannel.cs shows the official app calls
            // CmdChannelStart once, right here (ChannelOpen -> ProgramAdd -> ProgramCommit ->
            // ChannelStart), BEFORE any Source/Program-apply ever happens -- ChannelStart powers
            // up the modulator + encoder pipeline; ProgramApply/SourceStart later just attaches a
            // live source to the already-running channel. Earlier "ChannelStart with no source
            // crashes" tests only ever sent an empty or mModulationParam-only property set --
            // xHeadConfig.applyChannel() (decompiled) shows the real payload also needs
            // mPSRFPowerAdjust, mMTSChannelParam, mPSEncodeParam (the encoder config -- this is
            // almost certainly what actually initializes the mPSEncoder object that live cdb
            // debugging showed stuck at Status=0) and mEPGSimpleParam. Echo back everything the
            // server just handed us in Channel.Properties unchanged first (safest starting point
            // -- confirm this doesn't crash and lets ProgramApply through before tuning any
            // individual value).
            var channelStartProps = new List<msPropertyParam>();
            foreach (var prop in openResp.Channel.Properties)
            {
                channelStartProps.Add(new msPropertyParam { Name = prop.Property.Name, Values = { prop.Param.Values } });
            }

            // Now that the echo-back-unchanged case is confirmed working end-to-end, verify we can
            // actually CHANGE values and have them take effect (the whole point of this tool vs.
            // the official GUI). Constellation is a visible/verifiable flip (QAM64(3) -> QPSK(1),
            // confirmed live FieldID=19 under mModulationParam.Mode=ISDB_T).
            //
            // First attempt at raising RF power (Level=30) had ZERO effect ("adjust power :
            // [00:00]" unchanged in the native log) -- decompiled xPowerLevel.cs revealed why:
            // Level is not a direct gain value, it indexes a per-frequency lookup table via
            // `level - 80` (valid range is only 80..100, 21 entries), and the *actual* physical
            // knobs are PAGain/DACGain, computed client-side from that table and sent alongside
            // Level -- setting Level alone without the matching PAGain/DACGain does nothing.
            // Frequency 473000kHz -> RFPower473 table; Level=90 -> index (90-80)=10 ->
            // PowerGain(PAGain=2, DACGain=-10).
            // Tried switching Mode away from ISDB_T (see docs/protocol/modulation_capabilities.md
            // and tools/usb_capture/README.md for the full writeup) -- server rejected it cleanly
            // with "field [Constellation] not exists" before ever touching hardware. Reverted to
            // the known-working ISDB_T path.
            SetPropertyValue(channelStartProps, "mModulationParam", 19, v => v.IntVal = 1);
            SetPropertyValue(channelStartProps, "mPSRFPowerAdjust", 0, v => v.UintVal = 90);
            SetPropertyValue(channelStartProps, "mPSRFPowerAdjust", 1, v => v.IntVal = 2);
            SetPropertyValue(channelStartProps, "mPSRFPowerAdjust", 2, v => v.IntVal = -10);
            // Tried a marker-value test on mMTSChannelParam.RegionID (FieldID=4, default 23,
            // range 0..63) on 2026-07-26 to see whether it lands on any of the still-unidentified
            // stable registers (0x0601/0x0602/0x0640-0x0642/0x0680-0x0683). Result: it did NOT
            // appear anywhere in the register-bus write capture at all -- RegionID (and, by
            // implication, likely all of mMTSChannelParam) is not written to the modulator's
            // register bus, consistent with it being a TS/PSI-SI multiplexing parameter handled
            // entirely in software rather than a hardware modulation setting. See
            // tools/usb_capture/README.md "続報8" for the full writeup. Reverted here to the
            // known-good baseline.
            Console.WriteLine("  Overriding before ChannelStart: mModulationParam.Constellation=QPSK(1), " +
                "mPSRFPowerAdjust.Level=90/PAGain=2/DACGain=-10 (473000kHz table entry)");
            Console.Out.Flush();

            var earlyStartReq = new msRequest { Cmd = msServiceCmd.CmdChannelStart, ClientID = clientId, HandleID = chHandle };
            earlyStartReq.Properties.AddRange(channelStartProps);
            msResponse earlyStartResp;
            try
            {
                Console.WriteLine("  Calling CmdChannelStart EARLY (before any Source exists), echoing all 6 property groups unchanged...");
                Console.Out.Flush();
                earlyStartResp = client.sendRequest(earlyStartReq, deadline: DateTime.UtcNow.AddSeconds(10));
                Console.WriteLine($"  ChannelStart(early): Result={earlyStartResp.Result} Status={earlyStartResp.Status} ParamCase={earlyStartResp.ParamCase}" +
                    (earlyStartResp.HasErrMessage ? $" ErrMessage={earlyStartResp.ErrMessage}" : ""));
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"  ChannelStart(early) RPC error: {ex.Status}");
                earlyStartResp = null;
            }
            Console.Out.Flush();
            if (earlyStartResp == null || earlyStartResp.Result != msResult.ResultSuccess)
            {
                CloseChannel(client, clientId, chHandle);
                return;
            }
            Console.WriteLine("  *** ChannelStart(early) SUCCEEDED -- channel/encoder pipeline should now be live. Proceeding to Source setup. ***");
            Console.Out.Flush();

            //
            // SourceUrl (file) proved to be a dead end from the client side: CmdSourceOpen always
            // returns before async Media Foundation probing finishes, and nothing (event or a
            // second connection) ever delivers the resulting Content back to us -- see
            // docs/protocol/modulation_capabilities.md "Source接続時の追加調査". SourceCapture is
            // different: the underlying msCapture is a SHARED/global object (unlike per-session
            // Sources), so a second connection's connectService snapshot *does* show its Content
            // once CmdCaptureStart finishes. Use that here: open+start the desktop capture, peek
            // its real Program/Stream layout from a second connection, then open a SourceCapture
            // against it using that now-known layout.
            msCapture desktopCap = null;
            foreach (var cap in msClient.Captures)
            {
                if (cap.CaptureType == msCaptureType.Dxgidesktop) { desktopCap = cap; break; }
            }
            if (desktopCap == null)
            {
                Console.WriteLine("  No Dxgidesktop capture available -- aborting.");
                CloseChannel(client, clientId, chHandle);
                return;
            }
            Console.WriteLine($"  Using capture: {desktopCap.Name} HandleID={desktopCap.HandleID}");

            client.sendRequest(new msRequest { Cmd = msServiceCmd.CmdCaptureOpen, ClientID = clientId, HandleID = desktopCap.HandleID }, deadline: DateTime.UtcNow.AddSeconds(8));
            Thread.Sleep(3000);
            var capStartResp = client.sendRequest(new msRequest { Cmd = msServiceCmd.CmdCaptureStart, ClientID = clientId, HandleID = desktopCap.HandleID }, deadline: DateTime.UtcNow.AddSeconds(8));
            Console.WriteLine($"  CaptureStart: Result={capStartResp.Result}" + (capStartResp.HasErrMessage ? $" ErrMessage={capStartResp.ErrMessage}" : ""));
            Thread.Sleep(2000);
            var peekedCap = PeekCaptureViaSecondaryConnection(desktopCap.HandleID);
            if (peekedCap == null || (peekedCap.Content?.Programs.Count ?? 0) == 0)
            {
                Console.WriteLine("  Capture has no probed content -- aborting.");
                CloseChannel(client, clientId, chHandle);
                return;
            }
            var capProgram = peekedCap.Content.Programs[0];
            Console.WriteLine($"  Capture ready: Program ID={capProgram.ID} Streams={capProgram.Streams.Count}");
            foreach (var s in capProgram.Streams) Console.WriteLine($"    Stream Index={s.Index} ID={s.ID} Format={s.Format}");
            Console.Out.Flush();

            var capParamForSource = new msCaptureParam();
            foreach (var s in capProgram.Streams)
            {
                capParamForSource.Content.Add(new msCaptureParam.Types.Capture { HandleID = desktopCap.HandleID, ProgramID = capProgram.ID, StreamIndex = s.Index });
            }
            var sourceOpenReq = new msRequest
            {
                Cmd = msServiceCmd.CmdSourceOpen,
                ClientID = clientId,
                Source = new msSourceParam { Mode = msSourceMode.SourceCapture, Name = "XHeadSenderCaptureSource", Capture = capParamForSource }
            };
            var sourceResp = client.sendRequest(sourceOpenReq, deadline: DateTime.UtcNow.AddSeconds(10));
            Console.WriteLine($"  SourceOpen(Capture): Result={sourceResp.Result} ParamCase={sourceResp.ParamCase}" +
                (sourceResp.HasErrMessage ? $" ErrMessage={sourceResp.ErrMessage}" : ""));
            Console.Out.Flush();
            if (sourceResp.ParamCase != msResponse.ParamOneofCase.Source)
            {
                CloseChannel(client, clientId, chHandle);
                return;
            }
            var src = sourceResp.Source;
            Console.WriteLine($"  Source: HandleID={src.HandleID} Status={src.Status} Mode={src.Mode}");
            Console.WriteLine("  Waiting for the source's own EventSourceStatus to reach StatusReady (up to 10s)...");
            var finalStatus = watcher.WaitForStatusReady(src.HandleID, TimeSpan.FromSeconds(10));
            Console.WriteLine($"  Source status after wait: {finalStatus?.Status} ContentPrograms={finalStatus?.Content?.Programs.Count ?? 0}");
            Console.Out.Flush();

            // Use the SOURCE's own reported Program/Stream numbering (now that the EventStatus fix
            // actually gives it to us), not the underlying Capture's -- they are different objects
            // and may number things differently even if they usually happen to match.
            if ((finalStatus?.Content?.Programs.Count ?? 0) == 0)
            {
                Console.WriteLine("  Source never reported Content -- aborting.");
                CloseSource(client, clientId, src.HandleID);
                CloseChannel(client, clientId, chHandle);
                return;
            }
            var srcProgram = finalStatus.Content.Programs[0];
            Console.WriteLine($"  Source's own Program ID={srcProgram.ID} Streams={srcProgram.Streams.Count}");
            foreach (var s in srcProgram.Streams) Console.WriteLine($"    Stream Index={s.Index} ID={s.ID} Format={s.Format}");

            Console.WriteLine($"  Available engines ({msClient.Engines.Count}):");
            foreach (var eng in msClient.Engines)
            {
                Console.WriteLine($"    HandleID={eng.HandleID} Name={eng.Name} Desc={eng.Desc}");
            }
            // Live cdb inspection (breakpoint on the real ProgramApply-precondition check function,
            // FUN_14009a130 at mnservice+0x9a130) showed the object being checked is an
            // "mPSEncoder" (namespace mazo::micomsoft) with its internal Status field == 0
            // (uninitialized), not 3 (Ready) -- i.e. the chosen Engine's encoder was never
            // initialized. We were blindly picking Engines[0] (observed to be
            // "microsoft_d3d11va", a Media Foundation engine that may not support encoding this
            // capture's format); the official app instead does
            // mnClient.Engine.findEngine(HWAccel) first. Try preferring an NVIDIA/NVENC engine
            // (observed second in the list, "nvidia_cuvid") since that's the one actually
            // plausible for real hardware encode -- see docs/protocol/modulation_capabilities.md.
            var chosenEngine = msClient.Engines.FirstOrDefault(e =>
                (e.Name?.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (e.Name?.IndexOf("cuvid", StringComparison.OrdinalIgnoreCase) >= 0))
                ?? (msClient.Engines.Count > 0 ? msClient.Engines[0] : null);
            Console.WriteLine($"  Chosen engine: HandleID={chosenEngine?.HandleID} Name={chosenEngine?.Name}");

            var content = new msMediaContent
            {
                Index = 0,
                Param = new msMediaParam { Functions = msMediaFunction.MediaNone },
                SourceID = src.HandleID,
                ProgramID = srcProgram.ID,
                EngineID = chosenEngine?.HandleID ?? 0
            };
            foreach (var s in srcProgram.Streams)
            {
                var contentStream = new msMediaContent.Types.Stream { Index = s.Index };
                contentStream.Nodes.Add(new msMediaContent.Types.Node { Mode = msMediaContent.Types.NodeMode.NodePassthrough });
                content.Streams.Add(contentStream);
            }
            Console.WriteLine($"  Built msMediaContent: SourceID={content.SourceID} ProgramID={content.ProgramID} EngineID={content.EngineID} Streams={content.Streams.Count}");
            Console.Out.Flush();

            // msServiceCmd.CmdEngineApply = 60 exists in the wire protocol (decompiled
            // mnClientDotNet/mnFramework.grpc/msServiceCmd.cs) but is NEVER referenced anywhere in
            // the decompiled GUI -- confirmed empirically that calling it (HandleID=channel,
            // Content=same msMediaContent as ProgramApply) crashes mnservice.exe outright ("Stream
            // removed" / native process exit). Device/service recover fine on restart, but this is
            // NOT a viable path with this payload shape -- do not call it. See
            // docs/protocol/modulation_capabilities.md for the full writeup.

            // Live cdb breakpoint on absl::FailedPreconditionError (mnservice+0x36ed79), triggered
            // right as our ProgramApply call fails, captured the real call chain (not the earlier
            // misattributed CmdConnect finding): mnbridge dispatch -> FUN_140096ce0 (walks the
            // Channel's Program list, invokes Program::Apply virtual method) -> FUN_14008c4b0
            // (Program::Apply) -> FUN_14009a130, which checks `*(SomeObj + 0x58) == 3` (3 ==
            // msStatus.StatusReady) and returns exactly our "bad status" FailedPreconditionError
            // if not. Tried calling SourceStart before ProgramApply (Source ends up StatusRunning
            // instead of StatusReady) -- identical failure, so the checked object is NOT the
            // Source. Reverted to the official app's confirmed order (xTaskStartChannel.cs:
            // applyContent() strictly before source_.startSource()) -- see
            // docs/protocol/modulation_capabilities.md for the live-debugging writeup.
            var applyReq = new msRequest { Cmd = msServiceCmd.CmdProgramApply, ClientID = clientId, HandleID = chHandle, Content = content };
            msResponse applyResp;
            try
            {
                applyResp = client.sendRequest(applyReq, deadline: DateTime.UtcNow.AddSeconds(8));
                Console.WriteLine($"  ProgramApply: Result={applyResp.Result}" +
                    (applyResp.HasErrMessage ? $" ErrMessage={applyResp.ErrMessage}" : ""));
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"  ProgramApply RPC error: {ex.Status}");
                applyResp = null;
            }
            Console.Out.Flush();
            if (applyResp == null || applyResp.Result != msResult.ResultSuccess)
            {
                CloseSource(client, clientId, src.HandleID);
                CloseChannel(client, clientId, chHandle);
                return;
            }

            var sourceStartReq = new msRequest { Cmd = msServiceCmd.CmdSourceStart, ClientID = clientId, HandleID = src.HandleID };
            msResponse sourceStartResp;
            try
            {
                sourceStartResp = client.sendRequest(sourceStartReq, deadline: DateTime.UtcNow.AddSeconds(8));
                Console.WriteLine($"  SourceStart: Result={sourceStartResp.Result} Status={sourceStartResp.Status}" +
                    (sourceStartResp.HasErrMessage ? $" ErrMessage={sourceStartResp.ErrMessage}" : ""));
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"  SourceStart RPC error: {ex.Status}");
                sourceStartResp = null;
            }
            Console.Out.Flush();

            if (sourceStartResp != null && sourceStartResp.Result == msResult.ResultSuccess)
            {
                // Channel was already started earlier (before Source even existed), matching the
                // official app's real architecture (xTaskCreateChannel.cs) -- no second
                // CmdChannelStart here. Source is now live and attached to the already-running
                // channel/encoder pipeline; just give it a moment to actually flow before cleanup.
                Console.WriteLine("  *** Source is running with real content attached to the already-started channel! Check RTL-SDR now. Waiting 8s... ***");
                Console.Out.Flush();
                Thread.Sleep(8000);
            }

            var stopChReq = new msRequest { Cmd = msServiceCmd.CmdChannelStop, ClientID = clientId, HandleID = chHandle };
            var stopChResp = client.sendRequest(stopChReq, deadline: DateTime.UtcNow.AddSeconds(5));
            Console.WriteLine($"  ChannelStop: Result={stopChResp.Result}");

            var sourceStopReq = new msRequest { Cmd = msServiceCmd.CmdSourceStop, ClientID = clientId, HandleID = src.HandleID };
            client.sendRequest(sourceStopReq, deadline: DateTime.UtcNow.AddSeconds(5));
            CloseSource(client, clientId, src.HandleID);
            CloseChannel(client, clientId, chHandle);
        }

        /// <summary>
        /// Opens a brand-new gRPC connection with PrivilegeDebug (non-exclusive -- does not
        /// conflict with an existing PrivilegeControl controller) purely to read a fresh
        /// msClient.Sources snapshot, then disconnects. Used to check whether a source's Content
        /// has been populated server-side after async probing, since no other RPC exposes this.
        /// </summary>
        private static msSource PeekSourceViaSecondaryConnection(uint sourceHandle)
        {
            var peekChannel = new Channel(ServiceAddress, ChannelCredentials.Insecure);
            var peekClient = new msBroadcastService.msBroadcastServiceClient(peekChannel);
            try
            {
                var req = new msRequest
                {
                    Cmd = msServiceCmd.CmdConnect,
                    ClientID = 0,
                    Client = new msClientParam { Name = "XHeadSenderPeek", Privilege = msPrivilege.PrivilegeDebug }
                };
                var resp = peekClient.connectService(req, deadline: DateTime.UtcNow.AddSeconds(5));
                Console.WriteLine($"  [peek] connect Result={resp.Result} Sources={resp.Client?.Sources.Count ?? 0}");
                if (resp.Result != msResult.ResultSuccess || resp.ParamCase != msResponse.ParamOneofCase.Client)
                {
                    return null;
                }
                msSource found = null;
                foreach (var s in resp.Client.Sources)
                {
                    Console.WriteLine($"  [peek] Source HandleID={s.HandleID} Status={s.Status} Programs={s.Content?.Programs.Count ?? 0}");
                    if (s.HandleID == sourceHandle) found = s;
                }
                var disc = new msRequest { Cmd = msServiceCmd.CmdDisconnect, ClientID = resp.Client.HandleID };
                peekClient.disconnectService(disc, deadline: DateTime.UtcNow.AddSeconds(5));
                return found;
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"  [peek] RPC error: {ex.Status}");
                return null;
            }
            finally
            {
                peekChannel.ShutdownAsync().Wait();
            }
        }

        /// <summary>Same idea as PeekSourceViaSecondaryConnection, but for Captures, which are
        /// shared/global (visible to every connection) rather than session-private like Sources.</summary>
        private static msCapture PeekCaptureViaSecondaryConnection(uint captureHandle)
        {
            var peekChannel = new Channel(ServiceAddress, ChannelCredentials.Insecure);
            var peekClient = new msBroadcastService.msBroadcastServiceClient(peekChannel);
            try
            {
                var req = new msRequest
                {
                    Cmd = msServiceCmd.CmdConnect,
                    ClientID = 0,
                    Client = new msClientParam { Name = "XHeadSenderPeekCap", Privilege = msPrivilege.PrivilegeDebug }
                };
                var resp = peekClient.connectService(req, deadline: DateTime.UtcNow.AddSeconds(5));
                if (resp.Result != msResult.ResultSuccess || resp.ParamCase != msResponse.ParamOneofCase.Client) return null;
                msCapture found = null;
                foreach (var c in resp.Client.Captures)
                {
                    if (c.HandleID == captureHandle) found = c;
                }
                var disc = new msRequest { Cmd = msServiceCmd.CmdDisconnect, ClientID = resp.Client.HandleID };
                peekClient.disconnectService(disc, deadline: DateTime.UtcNow.AddSeconds(5));
                return found;
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"  [peek-cap] RPC error: {ex.Status}");
                return null;
            }
            finally
            {
                peekChannel.ShutdownAsync().Wait();
            }
        }

        private static void CloseChannel(msBroadcastService.msBroadcastServiceClient client, uint clientId, uint chHandle)
        {
            var closeReq = new msRequest { Cmd = msServiceCmd.CmdChannelClose, ClientID = clientId, HandleID = chHandle };
            var closeResp = client.sendRequest(closeReq, deadline: DateTime.UtcNow.AddSeconds(5));
            Console.WriteLine($"  ChannelClose: Result={closeResp.Result}");
        }

        private static void CloseSource(msBroadcastService.msBroadcastServiceClient client, uint clientId, uint srcHandle)
        {
            var closeReq = new msRequest { Cmd = msServiceCmd.CmdSourceClose, ClientID = clientId, HandleID = srcHandle };
            var closeResp = client.sendRequest(closeReq, deadline: DateTime.UtcNow.AddSeconds(5));
            Console.WriteLine($"  SourceClose: Result={closeResp.Result}");
        }

        private static void TrySetProperty(msBroadcastService.msBroadcastServiceClient client, uint clientId, uint handleId,
            string propertyName, uint fieldId, msVariantType type, int intVal = 0, uint uintVal = 0, string strVal = null)
        {
            var variant = new msVariant { Type = type, FieldID = fieldId };
            switch (type)
            {
                case msVariantType.VariantInt: variant.IntVal = intVal; break;
                case msVariantType.VariantUint: variant.UintVal = uintVal; break;
                case msVariantType.VariantString: variant.StrVal = strVal ?? ""; break;
            }

            var req = new msRequest
            {
                Cmd = msServiceCmd.CmdApplyConfig,
                ClientID = clientId,
                HandleID = handleId,
            };
            req.Properties.Add(new msPropertyParam
            {
                Name = propertyName,
                Values = { variant }
            });

            try
            {
                var resp = client.sendRequest(req, deadline: DateTime.UtcNow.AddSeconds(5));
                Console.WriteLine($"  Set {propertyName}.FieldID={fieldId} -> Result={resp.Result} Status={resp.Status} ParamCase={resp.ParamCase}" +
                    (resp.HasErrMessage ? $" ErrMessage={resp.ErrMessage}" : ""));
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"  Set {propertyName}.FieldID={fieldId} -> RPC error: {ex.Status}");
            }
        }

        /// <summary>
        /// Mutates a single msVariant (by group name + FieldID) in place within an
        /// echoed-back property list, leaving every other field untouched. Throws if the
        /// group/field isn't present -- callers should only target fields confirmed to exist via
        /// a live property dump first.
        /// </summary>
        private static void SetPropertyValue(List<msPropertyParam> props, string groupName, uint fieldId, Action<msVariant> setter)
        {
            var group = props.First(p => p.Name == groupName);
            var variant = group.Values.First(v => v.FieldID == fieldId);
            setter(variant);
        }

        /// <summary>
        /// Appends a brand-new msVariant (by group name + FieldID) to an echoed-back property
        /// list. Needed for Mode-specific fields (e.g. DVB_T's own Constellation/Bandwidth/etc,
        /// FieldIDs 5-9) that the server never echoes back while Mode=ISDB_T is active, so
        /// SetPropertyValue's lookup would find nothing to mutate.
        /// </summary>
        private static void AddPropertyValue(List<msPropertyParam> props, string groupName, msVariant variant)
        {
            var group = props.First(p => p.Name == groupName);
            group.Values.Add(variant);
        }

        private static void DumpProperty(msProperty prop, int indent)
        {
            string pad = new string(' ', indent * 2);
            var desc = prop.Property;
            var param = prop.Param;
            Console.WriteLine($"{pad}Property \"{desc.Name}\" (size={desc.Size}, fieldNums={desc.FieldNums})");
            DumpDescriptor(desc, param, indent + 1);
        }

        private static void DumpDescriptor(msDescriptor desc, msPropertyParam param, int indent)
        {
            string pad = new string(' ', indent * 2);
            foreach (var field in desc.Fields)
            {
                string valueStr = "";
                if (param != null)
                {
                    foreach (var v in param.Values)
                    {
                        if (v.FieldID == field.FieldID)
                        {
                            valueStr = DumpVariant(v);
                            break;
                        }
                    }
                }

                string rangeStr = DumpRange(field.Range);

                Console.WriteLine($"{pad}- {field.Name} (FieldID={field.FieldID}, Type={field.Type}, Offset={field.Offset}, Size={field.Size}, IsSubGroup={field.IsSubGroup}) value=[{valueStr}] range=[{rangeStr}]");

                if (field.Range != null && field.Range.RangeGroup != null && field.Range.RangeGroup.StructDesc != null)
                {
                    DumpDescriptor(field.Range.RangeGroup.StructDesc, param, indent + 1);
                }

                if (field.Range != null && field.Range.RangeValues != null)
                {
                    foreach (var rv in field.Range.RangeValues.Values)
                    {
                        if (rv.StructDesc != null)
                        {
                            Console.WriteLine($"{pad}  [when {field.Name}={rv.Name}]");
                            DumpDescriptor(rv.StructDesc, param, indent + 2);
                        }
                    }
                }
            }
        }

        private static string DumpVariant(msVariant v)
        {
            switch (v.ParamCase)
            {
                case msVariant.ParamOneofCase.IntVal: return $"int:{v.IntVal}";
                case msVariant.ParamOneofCase.UintVal: return $"uint:{v.UintVal}";
                case msVariant.ParamOneofCase.StrVal: return $"str:{v.StrVal}";
                case msVariant.ParamOneofCase.RawVal: return $"raw[{v.RawVal.Length}bytes]";
                default: return "(none)";
            }
        }

        private static string DumpRange(msPropertyRange r)
        {
            if (r == null) return "";
            switch (r.ParamCase)
            {
                case msPropertyRange.ParamOneofCase.RangeInt:
                    return $"int {r.RangeInt.Min}..{r.RangeInt.Max} (default {r.RangeInt.Default})";
                case msPropertyRange.ParamOneofCase.RangeUint:
                    return $"uint 0..{r.RangeUint.Max} (default {r.RangeUint.Default}, hex={r.RangeUint.IsHex})";
                case msPropertyRange.ParamOneofCase.RangeValues:
                    {
                        var names = new System.Collections.Generic.List<string>();
                        foreach (var rv in r.RangeValues.Values)
                        {
                            string tag = rv.StructDesc != null ? $"{rv.Value}={rv.Name}(+struct)" : $"{rv.Value}={rv.Name}";
                            names.Add(tag);
                        }
                        return $"enum[{string.Join(", ", names)}] (default {r.RangeValues.Default})";
                    }
                case msPropertyRange.ParamOneofCase.RangeString:
                    return $"string maxlen={r.RangeString.Length}";
                case msPropertyRange.ParamOneofCase.RangeBuffer:
                    return $"buffer maxsize={r.RangeBuffer.Size}";
                case msPropertyRange.ParamOneofCase.RangeGroup:
                    return "group";
                default:
                    return "";
            }
        }
    }
}
