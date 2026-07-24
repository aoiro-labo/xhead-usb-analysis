using System;
using System.Collections.Concurrent;
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
        private Task _pump;

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
                        else
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

        private const string TestSourceFile = @"C:\Users\aoiro\Videos\ts\Record_20251109-210722.ts";

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

            var commitReq = new msRequest { Cmd = msServiceCmd.CmdProgramCommit, ClientID = clientId, HandleID = chHandle, Index = programIndex };
            var commitResp = client.sendRequest(commitReq, deadline: DateTime.UtcNow.AddSeconds(5));
            Console.WriteLine($"  ProgramCommit: Result={commitResp.Result}" +
                (commitResp.HasErrMessage ? $" ErrMessage={commitResp.ErrMessage}" : ""));
            Console.Out.Flush();
            if (commitResp.Result != msResult.ResultSuccess)
            {
                CloseChannel(client, clientId, chHandle);
                return;
            }

            Console.WriteLine($"  Opening source: {TestSourceFile}");
            var sourceOpenReq = new msRequest
            {
                Cmd = msServiceCmd.CmdSourceOpen,
                ClientID = clientId,
                Source = new msSourceParam
                {
                    Mode = msSourceMode.SourceUrl,
                    Name = "XHeadSenderSource",
                    URL = new msURLParam { Url = TestSourceFile, Mode = msURLMode.Local }
                }
            };
            var sourceResp = client.sendRequest(sourceOpenReq, deadline: DateTime.UtcNow.AddSeconds(10));
            Console.WriteLine($"  SourceOpen: Result={sourceResp.Result} ParamCase={sourceResp.ParamCase}" +
                (sourceResp.HasErrMessage ? $" ErrMessage={sourceResp.ErrMessage}" : ""));
            Console.Out.Flush();
            if (sourceResp.ParamCase != msResponse.ParamOneofCase.Source)
            {
                CloseChannel(client, clientId, chHandle);
                return;
            }
            var src = sourceResp.Source;
            Console.WriteLine($"  Source: HandleID={src.HandleID} Status={src.Status} Mode={src.Mode} Programs={src.Content?.Programs.Count ?? 0}");
            foreach (var p in src.Content?.Programs ?? new Google.Protobuf.Collections.RepeatedField<msContent.Types.Program>())
            {
                Console.WriteLine($"    Program ID={p.ID} Streams={p.Streams.Count}");
                foreach (var s in p.Streams)
                {
                    Console.WriteLine($"      Stream Index={s.Index} ID={s.ID} Format={s.Format}");
                }
            }
            Console.Out.Flush();

            // The GUI (xTaskStartChannel) polls source_.Status until StatusReady before applying
            // content. EventSourceStatus (confirmed via mnClient.handleSource()) only ever carries
            // a bare msStatus, never an updated Content -- so instead, open a second, independent
            // (non-controller / PrivilegeDebug) connection and call connectService again: its
            // response is a full state snapshot (msClient.Sources[]) that should reflect whatever
            // mnservice has finished probing server-side, without disturbing our primary session.
            if ((src.Content?.Programs.Count ?? 0) == 0)
            {
                Console.WriteLine("  Source not ready yet. Waiting ~10s for async probe, then peeking via a second connection...");
                Thread.Sleep(10000);
                var peeked = PeekSourceViaSecondaryConnection(src.HandleID);
                if (peeked != null)
                {
                    Console.WriteLine($"  Peeked source: Status={peeked.Status} Programs={peeked.Content?.Programs.Count ?? 0}");
                    src = peeked;
                }
                else
                {
                    Console.WriteLine("  Peek did not find the source.");
                }
            }

            if ((src.Content?.Programs.Count ?? 0) == 0)
            {
                Console.WriteLine("  Source has no probed programs/streams -- aborting before ProgramApply.");
                CloseSource(client, clientId, src.HandleID);
                CloseChannel(client, clientId, chHandle);
                return;
            }

            var program0 = src.Content.Programs[0];
            var content = new msMediaContent
            {
                Index = 0,
                SourceID = src.HandleID,
                ProgramID = (uint)programIndex,
                EngineID = msClient.Engines.Count > 0 ? msClient.Engines[0].HandleID : 0
            };
            foreach (var s in program0.Streams)
            {
                var contentStream = new msMediaContent.Types.Stream { Index = s.Index };
                contentStream.Nodes.Add(new msMediaContent.Types.Node { Mode = msMediaContent.Types.NodeMode.NodePassthrough });
                content.Streams.Add(contentStream);
            }
            Console.WriteLine($"  Built msMediaContent: SourceID={content.SourceID} ProgramID={content.ProgramID} EngineID={content.EngineID} Streams={content.Streams.Count}");
            Console.Out.Flush();

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

            // Full field set for mModulationParam, mirroring mnPropertiesParam.enumFields():
            // every leaf (Number/Select) field at its current/default value, Constellation changed.
            var modProp = new msPropertyParam { Name = "mModulationParam" };
            modProp.Values.Add(new msVariant { Type = msVariantType.VariantUint, FieldID = 0, UintVal = 473000 });   // Frequency
            modProp.Values.Add(new msVariant { Type = msVariantType.VariantInt, FieldID = 1, IntVal = 1 });          // DacCtrl.IFMode = Disable
            modProp.Values.Add(new msVariant { Type = msVariantType.VariantUint, FieldID = 2, UintVal = 0 });        // DacCtrl.IFFreq
            modProp.Values.Add(new msVariant { Type = msVariantType.VariantUint, FieldID = 3, UintVal = 0 });        // DacCtrl.GAIN
            modProp.Values.Add(new msVariant { Type = msVariantType.VariantInt, FieldID = 19, IntVal = 1 });         // Constellation: QAM64(3) -> QPSK(1)
            modProp.Values.Add(new msVariant { Type = msVariantType.VariantUint, FieldID = 20, UintVal = 6 });       // Bandwidth
            modProp.Values.Add(new msVariant { Type = msVariantType.VariantInt, FieldID = 21, IntVal = 1 });         // FFT = 8k
            modProp.Values.Add(new msVariant { Type = msVariantType.VariantInt, FieldID = 22, IntVal = 3 });         // CodeRate = CR_5_6
            modProp.Values.Add(new msVariant { Type = msVariantType.VariantInt, FieldID = 23, IntVal = 1 });         // GuardInterval = GI_1_16
            modProp.Values.Add(new msVariant { Type = msVariantType.VariantInt, FieldID = 24, IntVal = 3 });         // TimeInterleavce = Mode3

            var startReq = new msRequest { Cmd = msServiceCmd.CmdChannelStart, ClientID = clientId, HandleID = chHandle };
            startReq.Properties.Add(modProp);
            msResponse startResp;
            try
            {
                Console.WriteLine("  Calling ChannelStart now...");
                Console.Out.Flush();
                startResp = client.sendRequest(startReq, deadline: DateTime.UtcNow.AddSeconds(10));
                Console.WriteLine($"  ChannelStart: Result={startResp.Result} Status={startResp.Status} ParamCase={startResp.ParamCase}" +
                    (startResp.HasErrMessage ? $" ErrMessage={startResp.ErrMessage}" : ""));
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"  ChannelStart RPC error: {ex.Status}");
                startResp = null;
            }
            Console.Out.Flush();

            if (startResp != null && startResp.Result == msResult.ResultSuccess)
            {
                Console.WriteLine("  Channel started successfully! Waiting 5s (check RTL-SDR now) before stopping...");
                Console.Out.Flush();
                System.Threading.Thread.Sleep(5000);

                var stopReq = new msRequest { Cmd = msServiceCmd.CmdChannelStop, ClientID = clientId, HandleID = chHandle };
                var stopResp = client.sendRequest(stopReq, deadline: DateTime.UtcNow.AddSeconds(5));
                Console.WriteLine($"  ChannelStop: Result={stopResp.Result}");
            }

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
