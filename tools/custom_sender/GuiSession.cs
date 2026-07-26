using System;
using System.Collections.Generic;
using Grpc.Core;
using mnFramework.grpc;

namespace XHeadSender
{
    /// <summary>
    /// GUI向けの接続・チャンネル制御。CLIの RunFullPipelineTest とは異なり、Source/Capture/
    /// エンコーダは一切構築せず、ChannelOpen -> ProgramAdd/Commit -> ChannelStart のみ行う
    /// (docs/protocol/modulation_capabilities.md の「続報3」で判明した通り、ChannelStart単体で
    /// 変調器を実際にRF駆動できる -- tools/direct_usb --configure が mnservice.exe 非依存で
    /// 同じことを実証済み)。ボタンごとに呼ばれる想定で、状態は接続中/チャンネル開始中のみ保持する。
    /// </summary>
    internal sealed class GuiSession
    {
        private Channel _channel;
        private msBroadcastService.msBroadcastServiceClient _client;
        private msClient _msClient;
        private uint _outputHandle;
        private uint _chHandle;

        public bool Connected => _client != null;
        public bool ChannelStarted { get; private set; }

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

        public void StopChannel()
        {
            if (!ChannelStarted) return;

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
