using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            StartMnservice();

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
                    "mnservice.exe に接続できません。単体起動にも失敗した可能性があります。ログとlocalhost:50051を確認してください。");
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
                Disconnect();
                throw new InvalidOperationException("変調出力(ObjectOutputModulation)が見つかりません。実機が接続されているか確認してください。");
            }
            Console.WriteLine($"[GUI] connected. ClientHandle={_msClient.HandleID} ModulationOutput={_outputHandle}");
        }

        public static bool IsMnserviceRunning => Process.GetProcessesByName("mnservice").Any();

        public static void StartMnservice()
        {
            if (IsMnserviceRunning)
                return;

            string studioRoot = Environment.GetEnvironmentVariable("XHEAD_STUDIO_DIR")
                                ?? @"C:\Program Files\Micomsoft\XHEAD-STUDIO";
            string servicePath = Path.Combine(studioRoot, "service", "mnservice.exe");
            if (!File.Exists(servicePath))
                throw new FileNotFoundException("mnservice.exeが見つかりません。XHEAD_STUDIO_DIRまたはインストール先を確認してください。", servicePath);

            Console.WriteLine("[GUI] mnservice.exeを単体起動します...");
            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = servicePath,
                WorkingDirectory = Path.GetDirectoryName(servicePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null)
                throw new InvalidOperationException("mnservice.exeを起動できませんでした。");
            // The gRPC listener appears before USB Output registration has completed.
            // Give the standalone service the same initialization window STUDIO normally provides.
            Thread.Sleep(7000);
            if (process.HasExited)
                throw new InvalidOperationException($"mnservice.exeが起動直後に終了しました (exit={process.ExitCode})。");
            Console.WriteLine($"[GUI] mnservice.exe起動完了 (PID={process.Id})。");
        }

        public static void StopMnservice()
        {
            Process[] processes = Process.GetProcessesByName("mnservice");
            foreach (Process process in processes)
            {
                Console.WriteLine($"[GUI] mnservice.exeを停止します (PID={process.Id})...");
                process.Kill();
                if (!process.WaitForExit(5000))
                    throw new TimeoutException($"mnservice.exe PID={process.Id}が5秒以内に終了しませんでした。");
                process.Dispose();
            }
            Console.WriteLine(processes.Length == 0
                ? "[GUI] mnservice.exeは起動していません。"
                : "[GUI] mnservice.exeを停止しました。");
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

                // チャンネル/番組メタデータ (Spec=ARIB_STD_B10 前提 -- ChannelOpenの既定値も常に
                // このSpecで返ってくるため、他Specのフィールドは触らない)。2026-07-26に一度
                // 「どのフィールドを触ってもChannelStartがハングする」問題を検出し撤去したが、
                // 実機USB接続の劣化(長時間の強制終了・生レジスタ操作の繰り返しが原因)による
                // ものと判明し、物理的な抜き差しで解消・再検証済み
                // (docs/protocol/modulation_capabilities.md「続報14」)。
                Program.SetPropertyValue(channelStartProps, "mMTSChannelParam", 4, v => v.UintVal = cfg.RegionID);
                Program.SetPropertyValue(channelStartProps, "mMTSChannelParam", 5, v => v.UintVal = cfg.BroadcasterID);
                Program.SetPropertyValue(channelStartProps, "mMTSChannelParam", 6, v => v.UintVal = cfg.RemoteControlKeyID);
                Program.SetPropertyValue(channelStartProps, "mMTSChannelParam", 7, v => v.StrVal = cfg.NetworkName ?? "");
                Program.SetPropertyValue(channelStartProps, "mMTSChannelParam", 8, v => v.StrVal = cfg.TSName ?? "");
                Program.SetPropertyValue(channelStartProps, "mMTSProgramParam", 8, v => v.UintVal = cfg.ServiceNo);
                Program.SetPropertyValue(channelStartProps, "mMTSProgramParam", 11, v => v.IntVal = cfg.CopyFlag);
                Program.SetPropertyValue(channelStartProps, "mMTSProgramParam", 12, v => v.StrVal = cfg.ServiceName ?? "");
                Program.SetPropertyValue(channelStartProps, "mMTSProgramParam", 0, v => v.UintVal = cfg.PcrPid);
                Program.SetPropertyValue(channelStartProps, "mMTSProgramParam", 1, v => v.UintVal = cfg.PmtPid);

                // EPG (mEPGSimpleParam) -- STUDIOの「EPG設定」タブ相当。1件のみ・繰り返し配信という
                // 制約はハードウェア/ファームウェア側の仕様(続報11で確認済み)。
                Program.SetPropertyValue(channelStartProps, "mEPGSimpleParam", 0, v => v.IntVal = cfg.EPGMode);
                Program.SetPropertyValue(channelStartProps, "mEPGSimpleParam", 1, v => v.UintVal = cfg.EPGIntervalHours);
                Program.SetPropertyValue(channelStartProps, "mEPGSimpleParam", 2, v => v.UintVal = cfg.EPGEventID);
                Program.SetPropertyValue(channelStartProps, "mEPGSimpleParam", 3, v => v.IntVal = cfg.EPGType);
                Program.SetPropertyValue(channelStartProps, "mEPGSimpleParam", 4, v => v.StrVal = cfg.EPGTitle ?? "");
                Program.SetPropertyValue(channelStartProps, "mEPGSimpleParam", 5, v => v.StrVal = cfg.EPGDescriptor ?? "");

                // メディア/コーデック設定 (mPSEncodeParam) -- STUDIOの「メディア設定」(Video/Audio
                // PID)・「コーデック設定」タブ相当。Video(FieldID=16)/Audio(22)/Quality(36)は
                // FieldGroupだが、msVariantの配線上は子フィールドがフラットな兄弟エントリとして
                // 同じValuesリストに載るため、直接FieldIDで指定する。
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 0, v => v.IntVal = cfg.EncodePerformance);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 2, v => v.UintVal = cfg.VideoPID);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 3, v => v.UintVal = cfg.AudioPID);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 4, v => v.UintVal = cfg.Latency);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 5, v => v.UintVal = cfg.QueueTime);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 7, v => v.IntVal = cfg.VideoResolution);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 8, v => v.IntVal = cfg.VideoAspectRatio);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 11, v => v.IntVal = cfg.VideoFrameRate);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 18, v => v.IntVal = cfg.AudioChannel);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 19, v => v.IntVal = cfg.AudioSampleRate);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 20, v => v.IntVal = cfg.AudioBitrate);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 23, v => v.IntVal = cfg.QualityMode);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 33, v => v.UintVal = cfg.GOPLength);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 37, v => v.StrVal = cfg.DebugFile ?? "");
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 38, v => v.StrVal = cfg.BMLFile ?? "");

                // 2026-07-27 (続報21): STUDIO本体の「コーデック設定」サブページで発見した詳細設定。
                // FieldFlags型(先頭Functions・Quality.Functions)はビットORで組み立てる。
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 1, v => v.UintVal = cfg.EnableDebugFunction ? 1u : 0u);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 10, v => v.IntVal = cfg.VideoField);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 12, v => v.IntVal = cfg.VideoFormat);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 13, v => v.IntVal = cfg.ColorPrimaries);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 14, v => v.IntVal = cfg.TransferCharacteristics);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 15, v => v.IntVal = cfg.MatrixCoefficients);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 24, v => v.UintVal =
                    (cfg.EnableDetechSceneChange ? 1u : 0u) | (cfg.EnableTwoPass ? 2u : 0u));
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 26, v => v.UintVal = cfg.BitrateRatio);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 27, v => v.UintVal = cfg.MinBitrateRatio);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 28, v => v.UintVal = cfg.MaxBitrateRatio);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 29, v => v.UintVal = cfg.BFrameCount);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 30, v => v.UintVal = cfg.QualityRatio);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 34, v => v.UintVal = cfg.GOPMinLength);
                Program.SetPropertyValue(channelStartProps, "mPSEncodeParam", 35, v => v.UintVal = cfg.GOPMaxLength);

                Console.WriteLine($"[GUI] ChannelStart: Frequency={cfg.Frequency}kHz Constellation={cfg.Constellation} " +
                    $"Bandwidth={cfg.Bandwidth} FFT={cfg.FFT} CodeRate={cfg.CodeRate} GuardInterval={cfg.GuardInterval} " +
                    $"TimeInterleavce={cfg.TimeInterleavce} Level={cfg.Level} PAGain={cfg.PAGain} DACGain={cfg.DACGain}");
                Console.WriteLine($"[GUI] Channel/Program: NetworkName={cfg.NetworkName} TSName={cfg.TSName} " +
                    $"RegionID={cfg.RegionID} BroadcasterID={cfg.BroadcasterID} RemoteControlKeyID={cfg.RemoteControlKeyID} " +
                    $"ServiceNo={cfg.ServiceNo} ServiceName={cfg.ServiceName} CopyFlag={cfg.CopyFlag}");
                Console.WriteLine($"[GUI] EPG: Mode={cfg.EPGMode} IntervalHours={cfg.EPGIntervalHours} " +
                    $"EventID={cfg.EPGEventID} Type={cfg.EPGType} Title={cfg.EPGTitle} Descriptor={cfg.EPGDescriptor}");
                Console.WriteLine($"[GUI] Media/Codec: Performance={cfg.EncodePerformance} VideoPID=0x{cfg.VideoPID:X} " +
                    $"AudioPID=0x{cfg.AudioPID:X} Latency={cfg.Latency} QueueTime={cfg.QueueTime} " +
                    $"Resolution={cfg.VideoResolution} AspectRatio={cfg.VideoAspectRatio} FrameRate={cfg.VideoFrameRate} " +
                    $"AudioChannel={cfg.AudioChannel} SampleRate={cfg.AudioSampleRate} AudioBitrate={cfg.AudioBitrate} " +
                    $"QualityMode={cfg.QualityMode} GOPLength={cfg.GOPLength} BMLFile={cfg.BMLFile}");

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
                AttachSourceToChannel(src, TimeSpan.FromSeconds(10));
                Console.WriteLine("[GUI] *** デスクトップキャプチャの送出を開始しました。 ***");
            }
            catch
            {
                StopCaptureSourceInternal();
                throw;
            }
        }

        /// <summary>
        /// 2026-07-26: SourceUrl(動画ファイル指定)への再挑戦。以前は`CmdSourceOpen`後
        /// Contentが空のまま返ってきて断念していたが、原因はクライアント側の実装ミス
        /// （`msEvent.Status`は`msEventStatus`という、Statusに加えてContentも運ぶラッパー型
        /// なのに、Statusだけ読んでContentを読んでいなかった）と判明済み（続報、
        /// docs/protocol/modulation_capabilities.md）。この修正は元々Captureの調査で
        /// 見つかったものでSourceUrl側では一度も再検証していなかった -- 実際に試したところ
        /// 一発で成功し、STUDIOと同じ「動画ファイルを指定して送出」がこのGUIでもできる
        /// ようになった。tools/custom_sender の RunSourceUrlTest と同一のRPC列を踏襲する。
        /// </summary>
        public void StartUrlSource(string filePath)
        {
            if (!ChannelStarted) throw new InvalidOperationException("先に送出を開始してください。");
            if (SourceStarted) throw new InvalidOperationException("既にソースが接続されています。");
            if (string.IsNullOrWhiteSpace(filePath)) throw new InvalidOperationException("ファイルパスを指定してください。");

            uint clientId = _msClient.HandleID;
            var sourceOpenReq = new msRequest
            {
                Cmd = msServiceCmd.CmdSourceOpen,
                ClientID = clientId,
                Source = new msSourceParam
                {
                    Mode = msSourceMode.SourceUrl,
                    Name = "XHeadSenderGUIUrl",
                    URL = new msURLParam { Url = filePath, Mode = msURLMode.UrlAuto, QueueTime = 30000, Timeout = 5000 }
                }
            };
            var sourceResp = _client.sendRequest(sourceOpenReq, deadline: DateTime.UtcNow.AddSeconds(10));
            Console.WriteLine($"[GUI] SourceOpen(Url) Result={sourceResp.Result} ParamCase={sourceResp.ParamCase}" +
                (sourceResp.HasErrMessage ? $" ErrMessage={sourceResp.ErrMessage}" : ""));
            if (sourceResp.ParamCase != msResponse.ParamOneofCase.Source)
            {
                throw new InvalidOperationException("SourceOpen failed: " + sourceResp.Result +
                    (sourceResp.HasErrMessage ? " " + sourceResp.ErrMessage : ""));
            }
            var src = sourceResp.Source;
            _srcHandle = src.HandleID;

            try
            {
                // 実ファイルは非同期プロービングが必要(46MBのTSファイルで実測約9秒) -- 合成ソース
                // (Capture)より長めに待つ。
                Console.WriteLine("[GUI] Waiting for EventSourceStatus to reach StatusReady (up to 20s, async file probing)...");
                AttachSourceToChannel(src, TimeSpan.FromSeconds(20));
                Console.WriteLine("[GUI] *** 動画ファイルの送出を開始しました。 ***");
            }
            catch
            {
                StopCaptureSourceInternal();
                throw;
            }
        }

        public void SwitchUrlSource(string filePath)
        {
            if (!ChannelStarted) throw new InvalidOperationException("先に送出を開始してください。");
            if (SourceStarted) StopCaptureSourceInternal();
            StartUrlSource(filePath);
        }

        /// <summary>
        /// 2026-07-26: 自己完結カラーバー/サイントーン(SourceTranscode)。外部ファイル・
        /// キャプチャデバイス不要でRF出力を確認できる、STUDIO同等の機能。
        ///
        /// 既知の問題: `SourceOpen`が`RpcException(Unknown, "Unexpected error in RPC handling")`
        /// を確実に投げる。`--verbose-grpc`でgRPCの詳細ログを取ったところ、これは
        /// クライアント側の応答パース失敗ではなく**`mnservice.exe`自身がgRPCレベルで
        /// UNKNOWNステータスを明示的に返している**ことが判明した(続報8追記)——`SourceTranscode`
        /// はSTUDIO自身の通常のGUIフローでは使われない経路のため、ネイティブ側に実装の粗さが
        /// 残っていると見られる。ただし実機のRFは確かに出力される(RTL-SDRで+34〜35dB実測済み)。
        /// クライアント側で直せる問題ではないため、例外は「ソース添付失敗」として上位に伝播させ、
        /// 呼び出し元(MainForm)の既存フォールバック("送出中（ソース添付失敗、RFのみ）")に任せる
        /// ——ChannelStartは既に成功しているのでRFは出続けている。
        ///
        /// 追加の既知の問題(続報18): この例外の後、`mnservice.exe`のgRPCサービス全体が
        /// 新規リクエストを受け付けなくなる(DTMB/J83Cのハングと同じ"wait service timeout"症状)
        /// ことがあると確認済み。単にこのSourceが孤立するだけでなく、サービス全体の再起動が
        /// 必要になる場合がある。
        /// </summary>
        public void StartColorbarSource()
        {
            if (!ChannelStarted) throw new InvalidOperationException("先に送出を開始してください。");
            if (SourceStarted) throw new InvalidOperationException("既にソースが接続されています。");

            uint clientId = _msClient.HandleID;
            var chosenEngine = _msClient.Engines.FirstOrDefault(e =>
                (e.Name?.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (e.Name?.IndexOf("cuvid", StringComparison.OrdinalIgnoreCase) >= 0))
                ?? (_msClient.Engines.Count > 0 ? _msClient.Engines[0] : null);
            Console.WriteLine($"[GUI] Chosen engine: HandleID={chosenEngine?.HandleID} Name={chosenEngine?.Name}");

            var transcode = new msTranscodeParam
            {
                Engine = chosenEngine?.HandleID ?? 0,
                QueueTime = 1000,
                Colorbar = msColorbarMode.ColorbarSmptehdbars,
                Video = new msVideo
                {
                    Codec = msVideoCodec.H264,
                    Width = 1920,
                    Height = 1080,
                    FrameStruct = msFrameStructure.Interlaced,
                    FrameRate = msFrameRate.Fps2997,
                },
                VideoBitrate = 15000000,
                AudioCount = 1,
                SineTone = msSineToneMode.SineToneNoBeep,
                Audio = new msAudio
                {
                    Codec = msAudioCodec.Mp1L2,
                    SampleRate = 48000,
                    Channel = msAudioChannel.Stereo,
                },
                AudioBitrate = 128000,
            };
            var sourceOpenReq = new msRequest
            {
                Cmd = msServiceCmd.CmdSourceOpen,
                ClientID = clientId,
                Source = new msSourceParam { Mode = msSourceMode.SourceTranscode, Name = "XHeadSenderGUIColorbar", Transcode = transcode }
            };
            msResponse sourceResp;
            try
            {
                sourceResp = _client.sendRequest(sourceOpenReq, deadline: DateTime.UtcNow.AddSeconds(10));
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"[GUI] SourceOpen(Transcode) RPC error: {ex.Status}");
                throw new InvalidOperationException(
                    "カラーバーソースの起動で既知のサーバー側エラーが発生しました(mnservice.exe内部の問題、" +
                    "続報8参照)。RFはChannelStartの時点で既に出力中のため、このまま「RFのみ」で送出は継続します。" +
                    "注意: このエラーの後、mnservice.exeのgRPCサービスが無応答になることがあります(続報18)。" +
                    "以降の操作が『wait service timeout』で失敗する場合はmnservice.exeを再起動してください。");
            }
            Console.WriteLine($"[GUI] SourceOpen(Transcode) Result={sourceResp.Result} ParamCase={sourceResp.ParamCase}" +
                (sourceResp.HasErrMessage ? $" ErrMessage={sourceResp.ErrMessage}" : ""));
            if (sourceResp.ParamCase != msResponse.ParamOneofCase.Source)
            {
                throw new InvalidOperationException("SourceOpen failed: " + sourceResp.Result +
                    (sourceResp.HasErrMessage ? " " + sourceResp.ErrMessage : ""));
            }
            var src = sourceResp.Source;
            _srcHandle = src.HandleID;

            try
            {
                AttachSourceToChannel(src, TimeSpan.FromSeconds(10));
                Console.WriteLine("[GUI] *** カラーバーの送出を開始しました。 ***");
            }
            catch
            {
                StopCaptureSourceInternal();
                throw;
            }
        }

        /// <summary>
        /// StartCaptureSource/StartUrlSourceで共通の後半処理: EventSourceStatus待機 -> エンジン
        /// 選択 -> ProgramApply -> SourceStart。呼び出し前に _srcHandle をセットしておくこと。
        /// </summary>
        private void AttachSourceToChannel(msSource src, TimeSpan waitTimeout)
        {
            uint clientId = _msClient.HandleID;
            var finalStatus = _watcher.WaitForStatusReady(src.HandleID, waitTimeout);
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
                    var disconnect = new msRequest { Cmd = msServiceCmd.CmdDisconnect, ClientID = _msClient?.HandleID ?? 0 };
                    _client.disconnectService(disconnect, deadline: DateTime.UtcNow.AddSeconds(5));
                    Console.WriteLine("[GUI] disconnectService OK.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GUI] disconnect error: {ex.Message}");
                }
                if (_channel != null && !_channel.ShutdownAsync().Wait(TimeSpan.FromSeconds(5)))
                    Console.WriteLine("[GUI] gRPC channel shutdown timed out after 5 seconds.");
            }
            _client = null;
            _channel = null;
            _msClient = null;
        }
    }
}
