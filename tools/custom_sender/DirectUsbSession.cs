using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace XHeadSender
{
    /// <summary>
    /// mnservice.exe を一切経由せず、WinUSB 経由で XHEAD-USB 実機に直接コントロール転送を送る
    /// GUIセッション。tools/direct_usb (XHeadDirectUsb.exe) の --configure 相当のロジックを
    /// GUIから使えるインスタンスメソッドとして移植したもの(ロジック自体は同一、実機で検証済み
    /// -- tools/direct_usb/README.md「マイルストーン」節、RTL-SDRで+33〜34dBのRF出力を実測済み)。
    ///
    /// GuiSession(mnservice.exe経由)と対になる、もう一方の送出バックエンド。対応範囲は
    /// レジスタで表現できる変調パラメータ + RF電力設定のみ -- Source添付(映像/音声)や
    /// チャンネル/番組メタデータ(PSI/SI生成はmnservice.exeのソフトウェア側の仕事であり、
    /// レジスタに対応物が無いことをtools/usb_capture/README.md「続報8」で確認済み)は
    /// このバックエンドでは扱えない。
    ///
    /// mnservice.exe はWinUSBインターフェースを排他的に保持するため、このセッションを使う際は
    /// 事前に mnservice.exe / xhead_studio.exe を停止しておくこと(Open()は掴めなければ
    /// 素直に失敗する)。
    /// </summary>
    internal sealed class DirectUsbSession
    {
        private static readonly Guid DeviceInterfaceGuid = new Guid("2F110364-7C93-4684-B4DC-46D95D5B3A9D");

        private const byte REQ_SET_ADDRESS = 0x4A;
        private const byte REQ_READ = 0x4E;
        private const byte REQ_WRITE = 0x4F;
        private const byte BM_HOST_TO_DEVICE_VENDOR_DEVICE = 0x40;
        private const byte BM_DEVICE_TO_HOST_VENDOR_DEVICE = 0xC0;

        private SafeUsbFileHandle _fileHandle;
        private IntPtr _winusbHandle = IntPtr.Zero;

        public bool DeviceOpen => _winusbHandle != IntPtr.Zero;
        public bool ChannelStarted { get; private set; }

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
        /// 同じ基準で切り替える。GUIの選択肢はcfg.Modeが実機で安全と確認済みの4値
        /// (0=DVB_T/2=ATSC/3=J83B/5=ISDB_T)に限定しているため、それ以外の値は想定しない。
        /// </summary>
        public void StartChannel(ModulationConfig cfg)
        {
            if (!DeviceOpen) throw new InvalidOperationException("先に開いてください。");
            if (ChannelStarted) throw new InvalidOperationException("既に送出中です。先に停止してください。");

            byte dacByte = unchecked((byte)cfg.DACGain);
            uint dacPacked = (uint)((dacByte << 8) | dacByte);
            uint extReg = 0x45585400u | 0x02; // 全キャプチャで定数として観測(意味未解明)

            bool hasOfdmFields = cfg.Mode == 0 || cfg.Mode == 5;   // DVB_T, ISDB_T
            bool hasTimeInterleave = cfg.Mode == 5;                 // ISDB_T のみ

            Console.WriteLine($"[DirectUSB] ChannelStart: Mode={cfg.Mode} Frequency={cfg.Frequency}kHz Constellation={cfg.Constellation}" +
                (hasOfdmFields ? $" Bandwidth={cfg.Bandwidth} FFT={cfg.FFT} CodeRate={cfg.CodeRate} GuardInterval={cfg.GuardInterval}" : "") +
                (hasTimeInterleave ? $" TimeInterleavce={cfg.TimeInterleavce}" : "") +
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
            seq.Add((0x0600, 1));
            seq.Add((0x1228, 0));
            seq.Add((0x1229, dacPacked));
            seq.Add((0x1221, 2));
            seq.Add((0x1290, extReg));
            seq.Add((0x1220, 0x78122901));
            seq.Add((0x0629, 0));
            seq.Add((0x0629, 0));

            foreach (var (addr, data) in seq)
            {
                SetAddress(addr);
                Thread.Sleep(20);
                WriteRegister(data);
                Thread.Sleep(20);
            }

            ChannelStarted = true;
            Console.WriteLine("[DirectUSB] *** レジスタ書き込み完了。実機が設定した周波数でRFを出力しているはずです。 ***");
        }

        /// <summary>
        /// 実験的: mnservice.exe側のChannelStop時に観測された「0x0600=0x2000(teardown)」を
        /// 送信してみるが、direct_usb経路単独での効果は未検証。確実な「送出停止」手段が
        /// まだ判明していないため、最終手段は Close()(ハンドルを閉じるのみ、RFはそのまま
        /// 出続ける可能性がある)。
        /// </summary>
        public void StopChannel()
        {
            if (!ChannelStarted) return;
            Console.WriteLine("[DirectUSB] ChannelStop相当を試行(実験的、効果未検証: 0x0600=0x2000)...");
            SetAddress(0x0600);
            Thread.Sleep(20);
            WriteRegister(0x2000);
            ChannelStarted = false;
            Console.WriteLine("[DirectUSB] *** 停止コマンドを送信しました。RFが実際に止まったかは未検証です。 ***");
        }

        public void Close()
        {
            CloseInternal();
        }

        private void CloseInternal()
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
                ifData.cbSize = Marshal.SizeOf(ifData);
                if (!SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref interfaceGuid, 0, ref ifData)) return null;

                int requiredSize = 0;
                SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);

                var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                detail.cbSize = IntPtr.Size == 8 ? 8 : 6;
                if (!SetupDiGetDeviceInterfaceDetail2(hDevInfo, ref ifData, ref detail, requiredSize, ref requiredSize, IntPtr.Zero)) return null;
                return detail.DevicePath;
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
    }

    internal sealed class SafeUsbFileHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeUsbFileHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandleNative(handle);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
        private static extern bool CloseHandleNative(IntPtr handle);
    }
}
