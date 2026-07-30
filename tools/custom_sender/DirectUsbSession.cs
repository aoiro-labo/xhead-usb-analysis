using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;

namespace XHeadSender
{
    /// <summary>
    /// mnservice.exe を一切経由せず、WinUSB 経由で XHEAD-USB 実機に直接コントロール転送を送る
    /// GUIセッション。tools/direct_usb (XHeadDirectUsb.exe) の --configure 相当のロジックを
    /// GUIから使えるインスタンスメソッドとして移植したもの(ロジック自体は同一、実機で検証済み
    /// -- tools/direct_usb/README.md「マイルストーン」節、RTL-SDRで+33〜34dBのRF出力を実測済み)。
    ///
    /// GuiSession(mnservice.exe経由)と対になる、もう一方の送出バックエンド。変調/RF設定と
    /// 完成済みTSのbulk送出を扱う。エンコードやPSI/SI生成自体はmnservice.exe側の機能なので
    /// 行わないが、映像・音声・字幕・EPG等を事前に多重化したTSはそのまま送出できる。
    ///
    /// mnservice.exe はWinUSBインターフェースを排他的に保持するため、このセッションを使う際は
    /// 事前に mnservice.exe / xhead_studio.exe を停止しておくこと(Open()は掴めなければ
    /// 素直に失敗する)。
    /// </summary>
    internal sealed class DirectUsbSession
    {
        private static readonly Guid DeviceInterfaceGuid = new Guid("DEE824EF-729B-4A0E-9C14-B7117D33A817");

        private const byte REQ_SET_ADDRESS = 0x4A;
        private const byte REQ_READ = 0x4E;
        private const byte REQ_WRITE = 0x4F;
        private const byte BM_HOST_TO_DEVICE_VENDOR_DEVICE = 0x40;
        private const byte BM_DEVICE_TO_HOST_VENDOR_DEVICE = 0xC0;
        private const byte PIPE_ID_BULK_OUT = 0x01;
        private const int TS_PACKET_SIZE = 188;
        private const int SLICE_SIZE_BYTES = 24064;

        private SafeUsbFileHandle _fileHandle;
        private IntPtr _winusbHandle = IntPtr.Zero;
        private Thread _streamThread;
        private UdpClient _udpClient;
        private Process _tsduckProcess;
        private string _temporaryEitFile;
        private volatile bool _streamStopRequested;
        private Exception _streamError;

        public bool DeviceOpen => _winusbHandle != IntPtr.Zero;
        public bool ChannelStarted { get; private set; }
        public bool StreamRunning => _streamThread != null && _streamThread.IsAlive;
        public Exception StreamError => _streamError;

        public void Open()
        {
            if (DeviceOpen) throw new InvalidOperationException("既に開いています。");

            string devicePath = FindDevicePath(DeviceInterfaceGuid);
            if (devicePath == null)
            {
                throw new InvalidOperationException("XHEAD-USBのデバイスパスが見つかりません。実機が接続されているか確認してください。");
            }
            Console.WriteLine("[DirectUSB] Device path: " + devicePath);

            _fileHandle = CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (_fileHandle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                _fileHandle = null;
                throw new InvalidOperationException(
                    $"CreateFile failed, Win32 error {err}。mnservice.exe/xhead_studio.exeがWinUSBインターフェースを" +
                    "排他保持している可能性があります。先に停止してください。");
            }

            if (!WinUsb_Initialize(_fileHandle, out _winusbHandle))
            {
                int err = Marshal.GetLastWin32Error();
                CloseInternal();
                throw new InvalidOperationException($"WinUsb_Initialize failed, Win32 error {err}");
            }

            Console.WriteLine("[DirectUSB] WinUSBハンドルを開きました(mnservice.exe非依存)。");
        }

        /// <summary>
        /// tools/direct_usb の RunConfigureSequence と全く同じレジスタ書き込み列
        /// (CmdChannelStart時にmnservice.exeが実際に発行する順序、cdbで復元済み)を、
        /// GUIで指定された周波数/変調方式/RF電力で再生する。
        ///
        /// 続報19: 0x0680はMode(FieldID=42)のraw enum値をそのまま書き込む「モード選択」レジスタ
        /// である可能性が高いと判明した(以前はISDB_T固定の定数5をハードコードしていた)。
        /// あわせてATSC/J83Bは実機ネイティブキャプチャでConstellationしか書き込んでおらず、
        /// DVB_TはISDB_Tと違いTimeInterleavceを持たないことも確認済み -- cfg.Modeに応じて
        /// 書き込むフィールド集合をtools/direct_usb/Program.csのRunConfigureSequenceと
        /// 同じ基準で切り替える。GUIの選択肢はcfg.Modeが実機で安全と確認済みの7値
        /// (0=DVB_T/1=J83A/2=ATSC/3=J83B/4=DTMB/5=ISDB_T/6=J83C)に限定しているため、それ以外の値は
        /// 想定しない。DTMB(続報22)は独自のフィールド構成(Constellation/Bandwidth/CodeRate/
        /// Carrier/Frame/Interleave)を持ち、cdbで捕捉した実機ネイティブの書き込み順を
        /// そのまま再現する(CodeRateとCarrierが同一レジスタ0x0692へ連続して書き込まれ、
        /// Carrierの値で上書きされるという原因不明の挙動も含めて忠実に再現)。
        /// </summary>
        public void StartChannel(ModulationConfig cfg)
        {
            if (!DeviceOpen) throw new InvalidOperationException("先に開いてください。");
            if (ChannelStarted) throw new InvalidOperationException("既に送出中です。先に停止してください。");
            if (cfg.Mode > 6)
                throw new ArgumentOutOfRangeException(nameof(cfg.Mode), "GUI直接USBバックエンドで許可するModeは0〜6です。");

            byte dacByte = unchecked((byte)cfg.DACGain);
            uint dacPacked = (uint)((dacByte << 8) | dacByte);
            uint extReg = 0x45585400u | 0x02; // 全キャプチャで定数として観測(意味未解明)

            bool hasOfdmFields = cfg.Mode == 0 || cfg.Mode == 5;   // DVB_T, ISDB_T
            bool hasTimeInterleave = cfg.Mode == 5;                 // ISDB_T のみ
            bool isDtmb = cfg.Mode == 4;

            Console.WriteLine($"[DirectUSB] ChannelStart: Mode={cfg.Mode} Frequency={cfg.Frequency}kHz Constellation={cfg.Constellation}" +
                (hasOfdmFields ? $" Bandwidth={cfg.Bandwidth} FFT={cfg.FFT} CodeRate={cfg.CodeRate} GuardInterval={cfg.GuardInterval}" : "") +
                (hasTimeInterleave ? $" TimeInterleavce={cfg.TimeInterleavce}" : "") +
                (isDtmb ? $" Bandwidth={cfg.Bandwidth} CodeRate={cfg.CodeRate} Carrier={cfg.Carrier} Frame={cfg.Frame} Interleave={cfg.TimeInterleavce}" : "") +
                $" DACGain={cfg.DACGain}");

            var seq = new System.Collections.Generic.List<(ushort addr, uint data)>
            {
                (0x0602, 1), (0x0640, 3), (0x0642, 0), (0x0641, 1), (0x0601, 5),
                (0x1202, cfg.Frequency),
                (0x0600, 0x1000),
                (0x0681, 1), (0x0682, 0), (0x0683, 0),
                (0x1202, cfg.Frequency),
                (0x0681, 1), (0x0681, 1), (0x0682, 0), (0x0683, 0),
                (0x0680, cfg.Mode),
                (0x0690, (uint)cfg.Constellation),
            };
            if (hasOfdmFields)
            {
                seq.Add((0x0684, cfg.Bandwidth));
                seq.Add((0x0691, (uint)cfg.FFT));
                seq.Add((0x0693, (uint)cfg.CodeRate));
                seq.Add((0x0692, (uint)cfg.GuardInterval));
            }
            if (hasTimeInterleave)
            {
                seq.Add((0x0694, (uint)cfg.TimeInterleavce));
            }
            if (isDtmb)
            {
                seq.Add((0x0684, cfg.Bandwidth));
                seq.Add((0x0692, (uint)cfg.CodeRate));  // 直後にCarrierで上書きされる(続報22)
                seq.Add((0x0692, cfg.Carrier));
                seq.Add((0x0694, cfg.Frame));
                seq.Add((0x0691, (uint)cfg.TimeInterleavce)); // DTMBのInterleaveはTimeInterleavceフィールドを流用
            }
            seq.Add((0x0600, 1));
            seq.Add((0x1228, 0));
            seq.Add((0x1229, dacPacked));
            seq.Add((0x1221, 2));
            seq.Add((0x1290, extReg));
            seq.Add((0x1220, 0x78122901));
            seq.Add((0x0629, 0));
            seq.Add((0x0629, 0));

            try
            {
                foreach (var (addr, data) in seq)
                {
                    SetAddress(addr);
                    Thread.Sleep(20);
                    WriteRegister(data);
                    if (addr == 0x0600 && (data == 0x1000 || data == 1))
                        WaitCommandFinish(data == 0x1000 ? "RFSTART" : "START");
                    else
                        Thread.Sleep(20);
                }
            }
            catch
            {
                // RFSTARTだけ成功してSTARTが拒否された場合もRF/デバイス状態を残さない。
                try { SendStopCommands(); }
                catch (Exception cleanupError)
                {
                    Console.WriteLine("[DirectUSB] 開始失敗後の停止処理エラー: " + cleanupError.Message);
                }
                throw;
            }

            ChannelStarted = true;
            Console.WriteLine("[DirectUSB] *** レジスタ書き込み完了。実機が設定した周波数でRFを出力しているはずです。 ***");
        }

        /// <summary>
        /// 188-byte MPEG-TSファイルをWinUSB bulk OUTへ直接送る。mnservice.exeの
        /// エンコーダ/マルチプレクサを一切使わない経路。EOFで先頭へ戻り、指定ビットレートで
        /// ペーシングする。呼び出しは即時に戻り、送信は専用スレッドで継続する。
        /// </summary>
        public void StartTsStream(string path, long bitrate = 20000000)
        {
            if (!DeviceOpen || !ChannelStarted) throw new InvalidOperationException("先に直接USB送出を開始してください。");
            if (StreamRunning) throw new InvalidOperationException("TSストリームは既に実行中です。");
            if (bitrate <= 0) throw new ArgumentOutOfRangeException(nameof(bitrate));
            ValidateTsFile(path);

            string fullPath = Path.GetFullPath(path);
            _streamStopRequested = false;
            _streamError = null;
            _streamThread = new Thread(() => StreamTsWorker(fullPath, bitrate))
            {
                IsBackground = true,
                Name = "XHEAD Direct USB TS"
            };
            _streamThread.Start();
            Console.WriteLine($"[DirectUSB] TS送信開始: {fullPath}, {bitrate:N0} bit/s");
        }

        /// <summary>
        /// TSDuck等からlocalhostのplain UDPで受けた188-byte MPEG-TSをbulk OUTへ送る。
        /// UDPデータグラムはTSパケットの整数個でなければならず、RTP/RS204には対応しない。
        /// データグラム境界をまたいで128 TS packet (24064 bytes)のUSBスライスへ再構成する。
        /// </summary>
        public void StartUdpTsStream(int port)
        {
            if (!DeviceOpen || !ChannelStarted) throw new InvalidOperationException("先に直接USB送出を開始してください。");
            if (StreamRunning) throw new InvalidOperationException("TSストリームは既に実行中です。");
            if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));

            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            _streamStopRequested = false;
            _streamError = null;
            _streamThread = new Thread(() => StreamUdpWorker(port))
            {
                IsBackground = true,
                Name = "XHEAD Direct USB UDP TS"
            };
            _streamThread.Start();
            Console.WriteLine($"[DirectUSB] UDP TS待受開始: 127.0.0.1:{port} (plain UDP, 188-byte TS)");
        }

        /// <summary>
        /// オプションのTSDuck tspを子プロセスとして起動し、ファイルを解析・ペーシングして
        /// localhost UDP入力へ渡す。TSDuck未導入時もStartTsStreamは単独で利用できる。
        /// </summary>
        public void StartTSDuckFileStream(string path, int port, long bitrate = 20000000)
        {
            StartTSDuckFileStream(path, port, bitrate, null);
        }

        public void StartTSDuckFileStream(string path, int port, long bitrate, ModulationConfig metadata)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("TSファイルが見つかりません。", path);
            if (bitrate <= 0) throw new ArgumentOutOfRangeException(nameof(bitrate));
            string tspPath = FindTSDuck();
            if (tspPath == null)
                throw new FileNotFoundException("TSDuckのtsp.exeが見つかりません。TSDuckをインストールするか--ts-fileを使用してください。");

            StartUdpTsStream(port);
            try
            {
                string fullPath = Path.GetFullPath(path);
                string plugins = "";
                if (metadata != null)
                {
                    plugins += $" -P svrename --japan --name {QuoteArgument(metadata.ServiceName)}" +
                        $" --provider {QuoteArgument(metadata.NetworkName)} --id {Math.Max(1, metadata.ServiceNo)}";
                    if (metadata.EPGMode != 0)
                        plugins += $" -P sdt --japan --service-id {Math.Max(1, metadata.ServiceNo)} --eit-pf 1" +
                            $" --eit-schedule {(metadata.EPGMode == 257 ? 1 : 0)}";
                    plugins += $" -P nit --create --network-name {QuoteArgument(metadata.NetworkName)}";
                    if (metadata.EPGMode != 0)
                    {
                        _temporaryEitFile = CreateEitFile(metadata);
                        plugins += $" -P eitinject --japan --actual --wait-first-batch --time system" +
                            $" --files {QuoteArgument(_temporaryEitFile)} --cycle-pf-actual 1" +
                            " --cycle-schedule-actual-prime 1 --cycle-schedule-actual-later 1";
                    }
                }
                var start = new ProcessStartInfo
                {
                    FileName = tspPath,
                    Arguments = $"--japan --add-input-stuffing 1/20 -I file --infinite {QuoteArgument(fullPath)}" +
                        plugins + $" -P regulate --bitrate {bitrate} -O ip --packet-burst 7 127.0.0.1:{port}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                _tsduckProcess = Process.Start(start);
                if (_tsduckProcess == null) throw new InvalidOperationException("tsp.exeを起動できませんでした。");
                Console.WriteLine($"[DirectUSB] TSDuck起動: PID={_tsduckProcess.Id}, {fullPath}, {bitrate:N0} bit/s");
            }
            catch
            {
                StopTsStream();
                throw;
            }
        }

        public void StopTsStream()
        {
            Thread thread = _streamThread;
            if (thread == null) return;
            _streamStopRequested = true;
            Process tsduck = _tsduckProcess;
            _tsduckProcess = null;
            if (tsduck != null)
            {
                try
                {
                    if (!tsduck.HasExited) tsduck.Kill();
                    tsduck.WaitForExit(2000);
                }
                finally
                {
                    tsduck.Dispose();
                }
            }
            if (_temporaryEitFile != null)
            {
                try { File.Delete(_temporaryEitFile); } catch { }
                _temporaryEitFile = null;
            }
            // Release a worker waiting in Receive(). The resulting SocketException/ObjectDisposedException
            // is an expected part of shutdown and is suppressed by StreamUdpWorker.
            UdpClient udp = _udpClient;
            _udpClient = null;
            if (udp != null) udp.Close();
            // A synchronous WinUsb_WritePipe can wait indefinitely when the device-side ring is
            // full. Abort only the bulk OUT pipe to release that wait; endpoint-0 control
            // transfers remain usable for the RF stop command below.
            WinUsb_AbortPipe(_winusbHandle, PIPE_ID_BULK_OUT);
            if (thread.IsAlive && !thread.Join(5000))
                throw new TimeoutException("直接USB TS送信スレッドが5秒以内に停止しませんでした。");
            _streamThread = null;
            WinUsb_ResetPipe(_winusbHandle, PIPE_ID_BULK_OUT);
            Console.WriteLine("[DirectUSB] TS送信停止。");
            if (_streamError != null) throw new InvalidOperationException("直接USB TS送信中にエラーが発生しました。", _streamError);
        }

        private static string FindTSDuck()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string installed = Path.Combine(programFiles, "TSDuck", "bin", "tsp.exe");
            if (File.Exists(installed)) return installed;
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "tsp.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        private static string CreateEitFile(ModulationConfig cfg)
        {
            DateTime start = DateTime.Now.AddSeconds(5);
            TimeSpan duration = TimeSpan.FromHours(Math.Max(1, cfg.EPGIntervalHours));
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<tsduck>\r\n" +
                $"  <EIT type=\"pf\" actual=\"true\" service_id=\"{Math.Max(1, cfg.ServiceNo)}\" transport_stream_id=\"1\" original_network_id=\"1\">\r\n" +
                $"    <event event_id=\"{cfg.EPGEventID}\" start_time=\"{start:yyyy-MM-dd HH:mm:ss}\" duration=\"{duration:hh\\:mm\\:ss}\" running_status=\"running\">\r\n" +
                "      <short_event_descriptor language_code=\"jpn\">\r\n" +
                $"        <event_name>{SecurityElement.Escape(cfg.EPGTitle ?? "")}</event_name>\r\n" +
                $"        <text>{SecurityElement.Escape(cfg.EPGDescriptor ?? "")}</text>\r\n" +
                "      </short_event_descriptor>\r\n    </event>\r\n  </EIT>\r\n</tsduck>\r\n";
            string path = Path.Combine(Path.GetTempPath(), "xhead-eit-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(path, xml, new UTF8Encoding(false));
            return path;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private void StreamTsWorker(string path, long bitrate)
        {
            try
            {
                byte[] slice = new byte[SLICE_SIZE_BYTES];
                long bytesSent = 0;
                var timer = Stopwatch.StartNew();
                using (var input = File.OpenRead(path))
                {
                    while (!_streamStopRequested)
                    {
                        FillSlice(input, slice);
                        SwapUsbWordsInPlace(slice);
                        uint transferred;
                        if (!WinUsb_WritePipe(_winusbHandle, PIPE_ID_BULK_OUT, slice,
                            (uint)slice.Length, out transferred, IntPtr.Zero))
                        {
                            if (_streamStopRequested) return;
                            int error = Marshal.GetLastWin32Error();
                            throw new InvalidOperationException($"WinUsb_WritePipe failed: 0x{error:X} ({error})");
                        }
                        if (transferred != slice.Length)
                            throw new IOException($"Short bulk write: {transferred}/{slice.Length} bytes");

                        bytesSent += transferred;
                        double targetSeconds = bytesSent * 8.0 / bitrate;
                        while (!_streamStopRequested && timer.Elapsed.TotalSeconds + 0.001 < targetSeconds)
                            Thread.Sleep(1);
                    }
                }
            }
            catch (Exception ex)
            {
                _streamError = ex;
                Console.WriteLine("[DirectUSB] TS送信エラー: " + ex.Message);
            }
        }

        private void StreamUdpWorker(int port)
        {
            long datagrams = 0;
            long packets = 0;
            long slices = 0;
            try
            {
                byte[] slice = new byte[SLICE_SIZE_BYTES];
                int sliceOffset = 0;
                var sender = new IPEndPoint(IPAddress.Any, 0);
                while (!_streamStopRequested)
                {
                    UdpClient udp = _udpClient;
                    if (udp == null) return;
                    byte[] datagram = udp.Receive(ref sender);
                    if (datagram.Length == 0) continue;
                    ValidateUdpDatagram(datagram, sender);
                    datagrams++;
                    packets += datagram.Length / TS_PACKET_SIZE;

                    int sourceOffset = 0;
                    while (sourceOffset < datagram.Length && !_streamStopRequested)
                    {
                        int copy = Math.Min(slice.Length - sliceOffset, datagram.Length - sourceOffset);
                        Buffer.BlockCopy(datagram, sourceOffset, slice, sliceOffset, copy);
                        sourceOffset += copy;
                        sliceOffset += copy;
                        if (sliceOffset != slice.Length) continue;

                        SwapUsbWordsInPlace(slice);
                        uint transferred;
                        if (!WinUsb_WritePipe(_winusbHandle, PIPE_ID_BULK_OUT, slice,
                            (uint)slice.Length, out transferred, IntPtr.Zero))
                        {
                            if (_streamStopRequested) return;
                            int error = Marshal.GetLastWin32Error();
                            throw new InvalidOperationException($"WinUsb_WritePipe failed: 0x{error:X} ({error})");
                        }
                        if (transferred != slice.Length)
                            throw new IOException($"Short bulk write: {transferred}/{slice.Length} bytes");
                        slices++;
                        sliceOffset = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_streamStopRequested)
                {
                    _streamError = ex;
                    Console.WriteLine("[DirectUSB] UDP TS送信エラー: " + ex.Message);
                }
            }
            finally
            {
                Console.WriteLine($"[DirectUSB] UDP統計: datagrams={datagrams:N0}, packets={packets:N0}, USB slices={slices:N0}");
            }
        }

        private static void ValidateUdpDatagram(byte[] datagram, IPEndPoint sender)
        {
            if (datagram.Length % TS_PACKET_SIZE != 0)
                throw new InvalidDataException(
                    $"UDP datagram from {sender} is {datagram.Length} bytes; plain TS requires a multiple of {TS_PACKET_SIZE}. RTP/RS204 is not supported.");
            for (int offset = 0; offset < datagram.Length; offset += TS_PACKET_SIZE)
            {
                if (datagram[offset] != 0x47)
                    throw new InvalidDataException($"TS sync byte missing in UDP datagram from {sender}, offset={offset}.");
            }
        }

        private static void ValidateTsFile(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("TSファイルが見つかりません。", path);
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length % TS_PACKET_SIZE != 0)
                throw new InvalidDataException($"TSファイルサイズは{TS_PACKET_SIZE}の倍数である必要があります。");
            using (var input = File.OpenRead(path))
            {
                int packets = (int)Math.Min(32, info.Length / TS_PACKET_SIZE);
                for (int packet = 0; packet < packets; packet++)
                {
                    if (input.ReadByte() != 0x47)
                        throw new InvalidDataException($"TS同期バイトがありません: packet={packet}");
                    input.Position += TS_PACKET_SIZE - 1;
                }
            }
        }

        private static void FillSlice(FileStream input, byte[] slice)
        {
            int offset = 0;
            while (offset < slice.Length)
            {
                int read = input.Read(slice, offset, slice.Length - offset);
                if (read == 0)
                {
                    input.Position = 0;
                    continue;
                }
                offset += read;
            }
        }

        private static void SwapUsbWordsInPlace(byte[] buffer)
        {
            for (int offset = 0; offset < buffer.Length; offset += 4)
            {
                byte b0 = buffer[offset];
                byte b1 = buffer[offset + 1];
                buffer[offset] = buffer[offset + 3];
                buffer[offset + 1] = buffer[offset + 2];
                buffer[offset + 2] = b1;
                buffer[offset + 3] = b0;
            }
        }

        /// <summary>
        /// stopModulation(2)を完了させてからChannelStop(0x2000)を送る。
        /// </summary>
        public void StopChannel()
        {
            if (!ChannelStarted) return;
            Exception streamStopError = null;
            try { StopTsStream(); }
            catch (Exception ex) { streamStopError = ex; }
            SendStopCommands();
            ChannelStarted = false;
            Console.WriteLine("[DirectUSB] *** 停止シーケンスが完了しました。 ***");
            if (streamStopError != null)
                throw new InvalidOperationException("RF停止は完了しましたが、TS送信停止中にエラーが発生しました。", streamStopError);
        }

        private void SendStopCommands()
        {
            Console.WriteLine("[DirectUSB] stopModulationを送信(0x0600=2)...");
            SetAddress(0x0600);
            Thread.Sleep(20);
            WriteRegister(2);
            WaitCommandFinish("stopModulation");
            Console.WriteLine("[DirectUSB] ChannelStopを送信(0x0600=0x2000)...");
            SetAddress(0x0600);
            Thread.Sleep(20);
            WriteRegister(0x2000);
            WaitCommandFinish("ChannelStop");
        }

        public void Close()
        {
            if (ChannelStarted)
            {
                try { StopChannel(); }
                catch (Exception ex) { Console.WriteLine("[DirectUSB] Close時の送出停止エラー: " + ex.Message); }
            }
            CloseInternal();
        }

        private void CloseInternal()
        {
            if (StreamRunning)
            {
                try { StopTsStream(); }
                catch (Exception ex) { Console.WriteLine("[DirectUSB] TS停止中のエラー: " + ex.Message); }
            }
            if (_winusbHandle != IntPtr.Zero)
            {
                WinUsb_Free(_winusbHandle);
                _winusbHandle = IntPtr.Zero;
            }
            if (_fileHandle != null && !_fileHandle.IsInvalid)
            {
                _fileHandle.Close();
            }
            _fileHandle = null;
            ChannelStarted = false;
        }

        private void SetAddress(ushort addr)
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

        private void WriteRegister(uint data)
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

        private uint ReadRegister()
        {
            byte[] buffer = new byte[8];
            var pkt = new RawSetupPacket
            {
                RequestType = BM_DEVICE_TO_HOST_VENDOR_DEVICE,
                Request = REQ_READ,
                Length0 = 8,
                Length1 = 0
            };
            SendControlTransfer(pkt, buffer);
            return ((uint)buffer[4] << 24) | ((uint)buffer[5] << 16) |
                   ((uint)buffer[6] << 8) | buffer[7];
        }

        private void WaitCommandFinish(string commandName)
        {
            for (int elapsedMs = 200; elapsedMs <= 5000; elapsedMs += 200)
            {
                Thread.Sleep(200);
                SetAddress(0x0600);
                uint status = ReadRegister();
                if (status != 0) continue;
                SetAddress(0x0023);
                uint result = ReadRegister();
                if (result != 0)
                    throw new InvalidOperationException(
                        $"{commandName} failed with device result 0x{result:X8}");
                Console.WriteLine($"[DirectUSB] {commandName}完了 ({elapsedMs} ms)。");
                return;
            }
            throw new TimeoutException($"{commandName}が5秒以内に完了しませんでした。");
        }

        private void SendControlTransfer(RawSetupPacket pkt, byte[] buffer)
        {
            uint lengthTransferred;
            bool ok = buffer != null
                ? WinUsb_ControlTransfer(_winusbHandle, pkt, buffer, (uint)buffer.Length, out lengthTransferred, IntPtr.Zero)
                : WinUsb_ControlTransfer(_winusbHandle, pkt, null, 0, out lengthTransferred, IntPtr.Zero);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"WinUsb_ControlTransfer failed, Win32 error 0x{err:X} ({err})");
            }
        }

        private static string FindDevicePath(Guid interfaceGuid)
        {
            IntPtr hDevInfo = SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (hDevInfo == IntPtr.Zero || hDevInfo.ToInt64() == -1) return null;

            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA();
                for (uint memberIndex = 0; ; memberIndex++)
                {
                    ifData.cbSize = Marshal.SizeOf(ifData);
                    if (!SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref interfaceGuid,
                        memberIndex, ref ifData)) return null;

                    int requiredSize = 0;
                    SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, IntPtr.Zero, 0,
                        ref requiredSize, IntPtr.Zero);

                    var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                    detail.cbSize = IntPtr.Size == 8 ? 8 : 6;
                    if (!SetupDiGetDeviceInterfaceDetail2(hDevInfo, ref ifData, ref detail,
                        requiredSize, ref requiredSize, IntPtr.Zero)) continue;

                    // The vendor GUID is shared by unrelated WinUSB devices on this PC.
                    // Never send XHEAD register commands unless VID/PID identifies XHEAD-USB.
                    if (detail.DevicePath.IndexOf("vid_17a7&pid_0008",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                        return detail.DevicePath;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }
        }

        // ---- P/Invoke declarations (tools/direct_usb/Program.cs と同一) ----

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
        private static extern SafeUsbFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(SafeUsbFileHandle deviceHandle, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ControlTransfer(IntPtr interfaceHandle, RawSetupPacket setupPacket,
            byte[] buffer, uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_WritePipe(IntPtr interfaceHandle, byte pipeId,
            byte[] buffer, uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_AbortPipe(IntPtr interfaceHandle, byte pipeId);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ResetPipe(IntPtr interfaceHandle, byte pipeId);
    }

    internal sealed class SafeUsbFileHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeUsbFileHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandleNative(handle);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
        private static extern bool CloseHandleNative(IntPtr handle);
    }
}
