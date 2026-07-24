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

                            var closeReq = new msRequest
                            {
                                Cmd = msServiceCmd.CmdChannelClose,
                                ClientID = msClient.HandleID,
                                HandleID = newCh.HandleID
                            };
                            var closeResp = client.sendRequest(closeReq, deadline: DateTime.UtcNow.AddSeconds(5));
                            Console.WriteLine($"  CmdChannelClose Result={closeResp.Result}");
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
                    DumpDescriptor(field.Range.RangeGroup.StructDesc, null, indent + 1);
                }

                if (field.Range != null && field.Range.RangeValues != null)
                {
                    foreach (var rv in field.Range.RangeValues.Values)
                    {
                        if (rv.StructDesc != null)
                        {
                            Console.WriteLine($"{pad}  [when {field.Name}={rv.Name}]");
                            DumpDescriptor(rv.StructDesc, null, indent + 2);
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
