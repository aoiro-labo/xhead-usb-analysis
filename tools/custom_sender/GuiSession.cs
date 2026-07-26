using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Grpc.Core;
using mnFramework.grpc;

namespace XHeadSender
{
    /// <summary>
    /// GUI向けの接続・チャンネル制御。当初はChannelOpen->ProgramAdd/Commit->ChannelStartのみ
    /// だった(docs/protocol/modulation_capabilities.md の「続報3」で判明した通り、ChannelStart
    /// 単体で変調器を実際にRF駆動できる -- tools/direct_usb --configure が mnservice.exe 非依存で
    /// 同じことを実証済み)。2026-07-26、「STUDIOでできることは自分のツールでもできるように」
    /// という方針のもと、実ソース(デスクトップキャプチャ)の添付にも対応した -- 手順は
    /// tools/custom_sender の RunFullPipelineTest と同一(CaptureOpen/Start -> SourceOpen(Capture)
    /// -> ProgramApply -> SourceStart)。
    /// </summary>
    internal sealed class GuiSession
    {
        private Channel _channel;
        private msBroadcastService.msBroadcastServiceClient _client;
        private msClient _msClient;
        private EventWatcher _watcher;
        private uint _outputHandle;
        private uint _chHandle;
        private uint _capHandle;
        private uint _srcHandle;

        public bool Connected => _client != null;
        public bool ChannelStarted { get; private set; }
        public bool SourceStarted { get; private set; }

        public void Connect()
        {
            if (Connected) throw new InvalidOperationException("既に接続済みです。");

            Program.EnsureNativeDllPathConfigured();

            Console.WriteLine($"[GUI] connecting to {Program.ServiceAddress} ...");
            _channel = new Channel(Program.ServiceAddress, ChannelCredentials.Insecure);
            _client = new msBroadcastService.msBroadcastServiceClient(_channel);

            var request = new msRequest
            {
                Cmd = msServiceCmd.CmdConnect,
                ClientID = 0,
                Client = new msClientParam { Name = "XHeadSenderGUI", Privilege = msPrivilege.PrivilegeControl }
            };
            msResponse response;
            try
            {
                response = _client.connectService(request, deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch (RpcException)
            {
                _client = null;
                _channel = null;
                throw new InvalidOperationException(
                    "mnservice.exe に接続できません。XHEAD-STUDIO (xhead_studio.exe) を起動してサービスを立ち上げてください。");
            }
            Console.WriteLine($"[GUI] connectService Result={response.Result}");
            if (response.Result != msResult.ResultSuccess || response.ParamCase != msResponse.ParamOneofCase.Client)
            {
                _client = null;
                _channel = null;
                throw new InvalidOperationException("connectService failed: " + response.Result +
                    (response.HasErrMessage ? " " + response.ErrMessage : ""));
            }
            _msClient = response.Client;

            _watcher = new EventWatcher();
            _watcher.Start(_client, _msClient.HandleID);

            _outputHandle = 0;
            foreach (var output in _msClient.Outputs)
            {
                if (output.ObjectType == msObjectType.ObjectOutputModulation)
                {
                    _outputHandle = output.HandleID;
                    break;
                }
            }
            if (_outputHandle == 0)
            {
                _client = null;
                throw new InvalidOperationException("変調出力(ObjectOutputModulation)が見つかりません。実機が接続されているか確認してください。");
            }
            Console.WriteLine($"[GUI] connected. ClientHandle={_msClient.HandleID} ModulationOutput={_outputHandle}");
        }

        public void StartChannel(ModulationConfig cfg)
        {
            if (!Connected) throw new InvalidOperationException("先に接続してください。");
            if (ChannelStarted) throw new InvalidOperationException("既に送出中です。先に停止してください。");

            var openReq = new msRequest
            {
                Cmd = msServiceCmd.CmdChannelOpen,
                ClientID = _msClient.HandleID,
                HandleID = _outputHandle,
                Channel = new msChannelParam { Name = "XHeadSenderGUI" }
            };
            var openResp = _client.sendRequest(openReq, deadline: DateTime.UtcNow.AddSeconds(5));
            Console.WriteLine($"[GUI] ChannelOpen Result={openResp.Result}");
            if (openResp.ParamCase != msResponse.ParamOneofCase.Channel)
            {
                throw new InvalidOperationException("ChannelOpen failed: " + openResp.Result +
                    (openResp.HasErrMessage ? " " + openResp.ErrMessage : ""));
            }
            _chHandle = openResp.Channel.HandleID;

            try
            {
                var addReq = new msRequest { Cmd = msServiceCmd.CmdProgramAdd, ClientID = _msClient.HandleID, HandleID = _chHandle };
                var addResp = _client.sendRequest(addReq, deadline: DateTime.UtcNow.AddSeconds(5));
                Console.WriteLine($"[GUI] ProgramAdd Result={addResp.Result}");
                if (addResp.ParamCase != msResponse.ParamOneofCase.Program)
                {
                    throw new InvalidOperationException("ProgramAdd failed: " + addResp.Result);
                }
                int programIndex = addResp.Program.Index;

                var commitReq = new msRequest { Cmd = msServiceCmd.CmdProgramCommit, ClientID = _msClient.HandleID, HandleID = _chHandle, Index = programIndex };
                foreach (var prop in addResp.Program.Properties)
                {
                    commitReq.Properties.Add(new msPropertyParam { Name = prop.Property.Name, Values = { prop.Param.Values } });
                }
                var commitResp = _client.sendRequest(commitReq, deadline: DateTime.UtcNow.AddSeconds(5));
                Console.WriteLine($"[GUI] ProgramCommit Result={commitResp.Result}");
                if (commitResp.Result != msResult.ResultSuccess)
                {
                    throw new InvalidOperationException("ProgramCommit failed: " + commitResp.Result);
                }

                var channelStartProps = new List<msPropertyParam>();
                foreach (var prop in openResp.Channel.Properties)
                {
                    channelStartProps.Add(new msPropertyParam { Name = prop.Property.Name, Values = { prop.Param.Values } });
                }

                Program.SetPropertyValue(channelStartProps, "mModulationParam", 0, v => v.UintVal = cfg.Frequency);
                Program.SetPropertyValue(channelStartProps, "mModulationParam", 19, v => v.IntVal = cfg.Constellation);
                Program.SetPropertyValue(channelStartProps, "mModulationParam", 20, v => v.UintVal = cfg.Bandwidth);
                Program.SetPropertyValue(channelStartProps, "mModulationParam", 21, v => v.IntVal = cfg.FFT);
                Program.SetPropertyValue(channelStartProps, "mModulationParam", 22, v => v.IntVal = cfg.CodeRate);
                Program.SetPropertyValue(channelStartProps, "mModulationParam", 23, v => v.IntVal = cfg.GuardInterval);
                Program.SetPropertyValue(channelStartProps, "mModulationParam", 24, v => v.IntVal = cfg.TimeInterleavce);
                Program.SetPropertyValue(channelStartProps, "mPSRFPowerAdjust", 0, v => v.UintVal = cfg.Level);
                Program.SetPropertyValue(channelStartProps, "mPSRFPowerAdjust", 1, v => v.IntVal = cfg.PAGain);
                Program.SetPropertyValue(channelStartProps, "mPSRFPowerAdjust", 2, v => v.IntVal = cfg.DACGain);

                Console.WriteLine($"[GUI] ChannelStart: Frequency={cfg.Frequency}kHz Constellation={cfg.Constellation} " +
                    $"Bandwidth={cfg.Bandwidth} FFT={cfg.FFT} CodeRate={cfg.CodeRate} GuardInterval={cfg.GuardInterval} " +
                    $"TimeInterleavce={cfg.TimeInterleavce} Level={cfg.Level} PAGain={cfg.PAGain} DACGain={cfg.DACGain}");

                var startReq = new msRequest { Cmd = msServiceCmd.CmdChannelStart, ClientID = _msClient.HandleID, HandleID = _chHandle };
                startReq.Properties.AddRange(channelStartProps);
                var startResp = _client.sendRequest(startReq, deadline: DateTime.UtcNow.AddSeconds(10));
                Console.WriteLine($"[GUI] ChannelStart Result={startResp.Result} Status={startResp.Status}" +
                    (startResp.HasErrMessage ? $" ErrMessage={startResp.ErrMessage}" : ""));
                if (startResp.Result != msResult.ResultSuccess)
                {
                    throw new InvalidOperationException("ChannelStart failed: " + startResp.Result +
                        (startResp.HasErrMessage ? " " + startResp.ErrMessage : ""));
                }

                ChannelStarted = true;
                Console.WriteLine("[GUI] *** 送出開始。実機が設定した周波数でRFを出力しているはずです。 ***");
            }
            catch
            {
                CloseChannelInternal();
                throw;
            }
        }

        /// <summary>
        /// STUDIOの基本動作（画面を選んで送出開始）に相当する、実ソース(デスクトップキャプチャ)の
        /// 添付。ChannelStart済みであることが前提。tools/custom_sender の RunFullPipelineTest と
        /// 同一のRPC列（CaptureOpen -> 待機 -> CaptureStart -> 別接続でContent確認(Captureは
        /// 全クライアント共有なのでこれが可能 -- Sourceはクライアント毎プライベートなので同じ
        /// 手は使えない) -> SourceOpen(Capture) -> EventSourceStatus待機 -> ProgramApply ->
        /// SourceStart）を踏襲する。
        /// </summary>
        public void StartCaptureSource()
        {
            if (!ChannelStarted) throw new InvalidOperationException("先に送出を開始してください。");
            if (SourceStarted) throw new InvalidOperationException("既にソースが接続されています。");

            msCapture desktopCap = null;
            foreach (var cap in _msClient.Captures)
            {
                if (cap.CaptureType == msCaptureType.Dxgidesktop) { desktopCap = cap; break; }
            }
            if (desktopCap == null)
            {
                throw new InvalidOperationException("デスクトップキャプチャ(Dxgidesktop)が見つかりません。");
            }
            Console.WriteLine($"[GUI] Using capture: {desktopCap.Name} HandleID={desktopCap.HandleID}");

            uint clientId = _msClient.HandleID;
            _client.sendRequest(new msRequest { Cmd = msServiceCmd.CmdCaptureOpen, ClientID = clientId, HandleID = desktopCap.HandleID }, deadline: DateTime.UtcNow.AddSeconds(8));
            Thread.Sleep(3000);
            var capStartResp = _client.sendRequest(new msRequest { Cmd = msServiceCmd.CmdCaptureStart, ClientID = clientId, HandleID = desktopCap.HandleID }, deadline: DateTime.UtcNow.AddSeconds(8));
            Console.WriteLine($"[GUI] CaptureStart Result={capStartResp.Result}");
            Thread.Sleep(2000);
            _capHandle = desktopCap.HandleID;

            try
            {
                var peekedCap = Program.PeekCaptureViaSecondaryConnection(desktopCap.HandleID);
                if (peekedCap == null || (peekedCap.Content?.Programs.Count ?? 0) == 0)
                {
                    throw new InvalidOperationException("キャプチャのContentが取得できませんでした。");
                }
                var capProgram = peekedCap.Content.Programs[0];
                Console.WriteLine($"[GUI] Capture ready: Program ID={capProgram.ID} Streams={capProgram.Streams.Count}");

                var capParamForSource = new msCaptureParam();
                foreach (var s in capProgram.Streams)
                {
                    capParamForSource.Content.Add(new msCaptureParam.Types.Capture { HandleID = desktopCap.HandleID, ProgramID = capProgram.ID, StreamIndex = s.Index });
                }
                var sourceOpenReq = new msRequest
                {
                    Cmd = msServiceCmd.CmdSourceOpen,
                    ClientID = clientId,
                    Source = new msSourceParam { Mode = msSourceMode.SourceCapture, Name = "XHeadSenderGUICapture", Capture = capParamForSource }
                };
                var sourceResp = _client.sendRequest(sourceOpenReq, deadline: DateTime.UtcNow.AddSeconds(10));
                Console.WriteLine($"[GUI] SourceOpen(Capture) Result={sourceResp.Result} ParamCase={sourceResp.ParamCase}" +
                    (sourceResp.HasErrMessage ? $" ErrMessage={sourceResp.ErrMessage}" : ""));
                if (sourceResp.ParamCase != msResponse.ParamOneofCase.Source)
                {
                    throw new InvalidOperationException("SourceOpen failed: " + sourceResp.Result +
                        (sourceResp.HasErrMessage ? " " + sourceResp.ErrMessage : ""));
                }
                var src = sourceResp.Source;
                _srcHandle = src.HandleID;

                Console.WriteLine("[GUI] Waiting for EventSourceStatus to reach StatusReady (up to 10s)...");
                var finalStatus = _watcher.WaitForStatusReady(src.HandleID, TimeSpan.FromSeconds(10));
                if ((finalStatus?.Content?.Programs.Count ?? 0) == 0)
                {
                    throw new InvalidOperationException("ソースのContentが取得できませんでした。");
                }
                var srcProgram = finalStatus.Content.Programs[0];
                Console.WriteLine($"[GUI] Source's Program ID={srcProgram.ID} Streams={srcProgram.Streams.Count}");

                var chosenEngine = _msClient.Engines.FirstOrDefault(e =>
                    (e.Name?.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (e.Name?.IndexOf("cuvid", StringComparison.OrdinalIgnoreCase) >= 0))
                    ?? (_msClient.Engines.Count > 0 ? _msClient.Engines[0] : null);
                Console.WriteLine($"[GUI] Chosen engine: HandleID={chosenEngine?.HandleID} Name={chosenEngine?.Name}");

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

                var applyReq = new msRequest { Cmd = msServiceCmd.CmdProgramApply, ClientID = clientId, HandleID = _chHandle, Content = content };
                var applyResp = _client.sendRequest(applyReq, deadline: DateTime.UtcNow.AddSeconds(8));
                Console.WriteLine($"[GUI] ProgramApply Result={applyResp.Result}" +
                    (applyResp.HasErrMessage ? $" ErrMessage={applyResp.ErrMessage}" : ""));
                if (applyResp.Result != msResult.ResultSuccess)
                {
                    throw new InvalidOperationException("ProgramApply failed: " + applyResp.Result +
                        (applyResp.HasErrMessage ? " " + applyResp.ErrMessage : ""));
                }

                var sourceStartReq = new msRequest { Cmd = msServiceCmd.CmdSourceStart, ClientID = clientId, HandleID = src.HandleID };
                var sourceStartResp = _client.sendRequest(sourceStartReq, deadline: DateTime.UtcNow.AddSeconds(8));
                Console.WriteLine($"[GUI] SourceStart Result={sourceStartResp.Result} Status={sourceStartResp.Status}");
                if (sourceStartResp.Result != msResult.ResultSuccess)
                {
                    throw new InvalidOperationException("SourceStart failed: " + sourceStartResp.Result);
                }

                SourceStarted = true;
                Console.WriteLine("[GUI] *** デスクトップキャプチャの送出を開始しました。 ***");
            }
            catch
            {
                StopCaptureSourceInternal();
                throw;
            }
        }

        public void StopCaptureSource()
        {
            StopCaptureSourceInternal();
        }

        private void StopCaptureSourceInternal()
        {
            uint clientId = _msClient?.HandleID ?? 0;
            if (_srcHandle != 0 && _client != null)
            {
                try
                {
                    _client.sendRequest(new msRequest { Cmd = msServiceCmd.CmdSourceStop, ClientID = clientId, HandleID = _srcHandle }, deadline: DateTime.UtcNow.AddSeconds(5));
                    var closeResp = _client.sendRequest(new msRequest { Cmd = msServiceCmd.CmdSourceClose, ClientID = clientId, HandleID = _srcHandle }, deadline: DateTime.UtcNow.AddSeconds(5));
                    Console.WriteLine($"[GUI] SourceClose Result={closeResp.Result}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GUI] SourceClose error: {ex.Message}");
                }
            }
            if (_capHandle != 0 && _client != null)
            {
                try
                {
                    _client.sendRequest(new msRequest { Cmd = msServiceCmd.CmdCaptureStop, ClientID = clientId, HandleID = _capHandle }, deadline: DateTime.UtcNow.AddSeconds(5));
                    var closeResp = _client.sendRequest(new msRequest { Cmd = msServiceCmd.CmdCaptureClose, ClientID = clientId, HandleID = _capHandle }, deadline: DateTime.UtcNow.AddSeconds(5));
                    Console.WriteLine($"[GUI] CaptureClose Result={closeResp.Result}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GUI] CaptureClose error: {ex.Message}");
                }
            }
            _srcHandle = 0;
            _capHandle = 0;
            SourceStarted = false;
        }

        public void StopChannel()
        {
            if (!ChannelStarted) return;

            if (SourceStarted) StopCaptureSourceInternal();

            var stopReq = new msRequest { Cmd = msServiceCmd.CmdChannelStop, ClientID = _msClient.HandleID, HandleID = _chHandle };
            var stopResp = _client.sendRequest(stopReq, deadline: DateTime.UtcNow.AddSeconds(5));
            Console.WriteLine($"[GUI] ChannelStop Result={stopResp.Result}");
            CloseChannelInternal();
        }

        private void CloseChannelInternal()
        {
            if (_chHandle != 0 && _client != null)
            {
                try
                {
                    var closeReq = new msRequest { Cmd = msServiceCmd.CmdChannelClose, ClientID = _msClient.HandleID, HandleID = _chHandle };
                    var closeResp = _client.sendRequest(closeReq, deadline: DateTime.UtcNow.AddSeconds(5));
                    Console.WriteLine($"[GUI] ChannelClose Result={closeResp.Result}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GUI] ChannelClose error: {ex.Message}");
                }
            }
            _chHandle = 0;
            ChannelStarted = false;
        }

        public void Disconnect()
        {
            if (ChannelStarted) StopChannel();
            if (_client != null)
            {
                try
                {
                    var disconnect = new msRequest { Cmd = msServiceCmd.CmdDisconnect, ClientID = 0 };
                    _client.disconnectService(disconnect, deadline: DateTime.UtcNow.AddSeconds(5));
                    Console.WriteLine("[GUI] disconnectService OK.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GUI] disconnect error: {ex.Message}");
                }
                _channel?.ShutdownAsync().Wait();
            }
            _client = null;
            _channel = null;
            _msClient = null;
        }
    }
}
