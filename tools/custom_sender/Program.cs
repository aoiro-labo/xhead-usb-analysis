using System;
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
    }
}
