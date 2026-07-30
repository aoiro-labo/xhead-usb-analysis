using System;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace XHeadSender
{
    /// <summary>
    /// 変調パラメータ + RF電力を自由に設定して ChannelStart で送出/停止するだけの、
    /// 最小限のWinForms GUI。Source/Capture/エンコーダは一切構築しない --
    /// tools/direct_usb --configure と同じ「ChannelStartだけで変調器をRF駆動する」考え方を、
    /// mnservice.exe/gRPC経由(公式サービスのプロパティ検証つき)で行う版。
    /// 実際の映像/音声ストリーム送出(SourceOpen以降)が必要な場合は既存のCLI
    /// (`dotnet run` 引数なし)の RunFullPipelineTest を使うこと。
    /// </summary>
    internal sealed class MainForm : Form
    {
        /// <summary>ComboBoxの表示文字列(日本語)と実際に送信する値を分離するための入れ物。</summary>
        private readonly struct ComboItem
        {
            public readonly int Value;
            public readonly string Label;
            public ComboItem(int value, string label) { Value = value; Label = label; }
            public override string ToString() => Label;
        }

        private readonly GuiSession _session = new GuiSession();
        private readonly DirectUsbSession _directSession = new DirectUsbSession();
        private readonly ToolTip _toolTip = new ToolTip
        {
            AutoPopDelay = 8000,
            InitialDelay = 400,
            ReshowDelay = 100,
            ShowAlways = true
        };

        private RadioButton _rbBackendService;
        private RadioButton _rbBackendDirect;
        private bool UseDirectBackend => _rbBackendDirect.Checked;

        private Button _btnConnect;
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnDisconnect;
        private RadioButton _rbSourceNone;
        private RadioButton _rbSourceCapture;
        private RadioButton _rbSourceColorbar;
        private RadioButton _rbSourceUrl;
        private TextBox _txtUrlPath;
        private Button _btnBrowseUrl;
        private Label _lblStatus;
        private NumericUpDown _numFrequency;
        private ComboBox _cmbMode;
        private ComboBox _cmbConstellation;
        private NumericUpDown _numBandwidth;
        private ComboBox _cmbFFT;
        private ComboBox _cmbCodeRate;
        private ComboBox _cmbGuardInterval;
        private ComboBox _cmbTimeInterleavce;
        private ComboBox _cmbCarrier;
        private ComboBox _cmbFrame;
        private NumericUpDown _numLevel;
        private NumericUpDown _numPAGain;
        private NumericUpDown _numDACGain;
        private TextBox _txtNetworkName;
        private TextBox _txtTSName;
        private TextBox _txtServiceName;
        private NumericUpDown _numRegionID;
        private NumericUpDown _numBroadcasterID;
        private NumericUpDown _numRemoteControlKeyID;
        private NumericUpDown _numServiceNo;
        private ComboBox _cmbCopyFlag;
        private NumericUpDown _numPcrPid;
        private NumericUpDown _numPmtPid;
        private ComboBox _cmbEPGMode;
        private NumericUpDown _numEPGIntervalHours;
        private NumericUpDown _numEPGEventID;
        private ComboBox _cmbEPGType;
        private TextBox _txtEPGTitle;
        private TextBox _txtEPGDescriptor;
        private ComboBox _cmbEncodePerformance;
        private NumericUpDown _numVideoPID;
        private NumericUpDown _numAudioPID;
        private NumericUpDown _numLatency;
        private NumericUpDown _numQueueTime;
        private ComboBox _cmbVideoResolution;
        private ComboBox _cmbVideoAspectRatio;
        private ComboBox _cmbVideoFrameRate;
        private ComboBox _cmbAudioChannel;
        private NumericUpDown _numAudioSampleRate;
        private NumericUpDown _numAudioBitrate;
        private ComboBox _cmbQualityMode;
        private NumericUpDown _numGOPLength;
        private TextBox _txtBMLFile;
        private Button _btnBrowseBML;
        private ComboBox _cmbVideoField;
        private ComboBox _cmbVideoFormat;
        private ComboBox _cmbColorPrimaries;
        private ComboBox _cmbTransferCharacteristics;
        private ComboBox _cmbMatrixCoefficients;
        private NumericUpDown _numBitrateRatio;
        private NumericUpDown _numMinBitrateRatio;
        private NumericUpDown _numMaxBitrateRatio;
        private NumericUpDown _numBFrameCount;
        private NumericUpDown _numQualityRatio;
        private NumericUpDown _numGOPMinLength;
        private NumericUpDown _numGOPMaxLength;
        private CheckBox _chkEnableDetechSceneChange;
        private CheckBox _chkEnableTwoPass;
        private CheckBox _chkEnableDebugFunction;
        private TextBox _txtLog;
        private TabPage _sourceTab;
        private TabPage _metaTab;
        private TabPage _epgTab;
        private TabPage _mediaTab;
        private TabPage _codecTab;
        private bool _updatingSourceRadios;

        public MainForm()
        {
            Text = "XHeadSender GUI -- 直接送出ツール（mnservice.exe経由 / 直接USB 選択可）";
            Width = 700;
            Height = 780;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(640, 560);
            Font = new Font("Yu Gothic UI", 9f);

            BuildControls();

            Console.SetOut(new TextBoxWriter(_txtLog));
            Console.WriteLine("XHeadSender GUI 起動。");
            Console.WriteLine("接続方式「mnservice.exe経由」を使う場合は、事前に XHEAD-STUDIO (xhead_studio.exe) を" +
                "起動してサービスを立ち上げておくこと。");
            Console.WriteLine("接続方式「直接USB」を使う場合は、逆に xhead_studio.exe / mnservice.exe が" +
                "起動していないことを確認すること(WinUSBインターフェースを排他保持するため)。");
            Console.WriteLine("接続方式を選んだら「接続」を押してください。");
        }

        private void BuildControls()
        {
            var warnLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 44,
                Text = "注意: 「送出開始」を押すと実機のRF出力を実際に駆動します。周波数は宣言上0〜1,000,000kHzまで"
                     + "受理されますが、実機が対応する範囲より広く、範囲外の値でmnservice.exeがクラッシュした実績があります。",
                ForeColor = Color.White,
                BackColor = Color.FromArgb(170, 40, 40),
                Padding = new Padding(8, 6, 8, 6),
            };

            _lblStatus = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = "状態: 未接続",
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 4, 0, 0),
            };

            var backendPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(8, 2, 8, 2) };
            var backendLabel = new Label { Text = "接続方式:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 6, 0) };
            _rbBackendService = new RadioButton { Text = "mnservice.exe経由（既定、全機能）", AutoSize = true, Checked = true, Anchor = AnchorStyles.Left };
            _rbBackendDirect = new RadioButton { Text = "直接USB（mnservice.exe不要、変調/RF電力のみ）", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 0, 0, 0) };
            SetHelpTip(_rbBackendService, "映像・音声、番組情報、EPGを含む全機能を利用できます。");
            SetHelpTip(_rbBackendDirect,
                "mnservice.exeを使わず直接制御します。使用前にSTUDIOとmnservice.exeを終了してください。");
            _rbBackendService.CheckedChanged += BackendChanged;
            _rbBackendDirect.CheckedChanged += BackendChanged;
            backendPanel.Controls.Add(backendLabel);
            backendPanel.Controls.Add(_rbBackendService);
            backendPanel.Controls.Add(_rbBackendDirect);

            var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 4, 8, 4) };
            _btnConnect = new Button { Text = "① 接続", Width = 100 };
            _btnStart = new Button { Text = "② 送出開始", Width = 100, Enabled = false };
            _btnStop = new Button { Text = "③ 送出停止", Width = 100, Enabled = false };
            _btnDisconnect = new Button { Text = "④ 切断", Width = 100, Enabled = false };
            _btnConnect.Click += BtnConnect_Click;
            _btnStart.Click += BtnStart_Click;
            _btnStop.Click += BtnStop_Click;
            _btnDisconnect.Click += BtnDisconnect_Click;
            btnPanel.Controls.Add(_btnConnect);
            btnPanel.Controls.Add(_btnStart);
            btnPanel.Controls.Add(_btnStop);
            btnPanel.Controls.Add(_btnDisconnect);

            var sourceLayout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, Padding = new Padding(10, 10, 6, 10) };
            sourceLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sourceLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sourceLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sourceLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _rbSourceNone = new RadioButton { Text = "RFのみ（既定、送出内容なし）", AutoSize = true, Checked = true, Margin = new Padding(0, 4, 0, 2) };
            SetHelpTip(_rbSourceNone, "RFだけを出力します。映像・音声は含みません。");

            _rbSourceCapture = new RadioButton { Text = "デスクトップキャプチャ（実際の画面を送出）", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            SetHelpTip(_rbSourceCapture, "デスクトップ画面をエンコードして送出します。");

            _rbSourceColorbar = new RadioButton { Text = "カラーバー（STUDIO同等の自己完結テストパターン、注意）", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            SetHelpTip(_rbSourceColorbar,
                "自己完結テスト信号です。応答停止時はmnservice.exeを再起動してください。");

            var urlRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 2, 0, 4) };
            _rbSourceUrl = new RadioButton { Text = "動画ファイル:", AutoSize = true, Anchor = AnchorStyles.Left };
            _txtUrlPath = new TextBox { Width = 320, Anchor = AnchorStyles.Left, Margin = new Padding(4, 3, 4, 3) };
            _btnBrowseUrl = new Button { Text = "参照...", Width = 70, Anchor = AnchorStyles.Left };
            _btnBrowseUrl.Click += BtnBrowseUrl_Click;
            SetHelpTip(_rbSourceUrl, "指定した動画またはTSファイルを送出します。");
            urlRow.Controls.Add(_rbSourceUrl);
            urlRow.Controls.Add(_txtUrlPath);
            urlRow.Controls.Add(_btnBrowseUrl);

            // WinFormsのRadioButtonは「直接の親コンテナが同じ」場合のみ自動的に排他選択される。
            // _rbSourceUrlだけ別のコンテナ(urlRow、テキストボックス+参照ボタンと横並びにするため)に
            // 入っているため、_rbSourceNone/_rbSourceCaptureとは自動排他の対象外になってしまう
            // (バグ: 動画ファイルを選んだ後に他を選んでも動画ファイル側がONのまま残る)。
            // コンテナ構成に関わらず確実に排他にするため、明示的にハンドラで管理する。
            _rbSourceNone.CheckedChanged += SourceRadioChanged;
            _rbSourceCapture.CheckedChanged += SourceRadioChanged;
            _rbSourceColorbar.CheckedChanged += SourceRadioChanged;
            _rbSourceUrl.CheckedChanged += SourceRadioChanged;

            sourceLayout.Controls.Add(_rbSourceNone, 0, 0);
            sourceLayout.Controls.Add(_rbSourceCapture, 0, 1);
            sourceLayout.Controls.Add(_rbSourceColorbar, 0, 2);
            sourceLayout.Controls.Add(urlRow, 0, 3);

            var metaLayout = NewParamTable();
            metaLayout.Padding = new Padding(10, 10, 6, 10);
            _txtServiceName = AddTextBox(metaLayout, "サービス名 (チャンネル名)", "VAT-01", 16,
                "受信機のチャンネル一覧・EPGに表示される名前。実受信機での見え方を確認する際はここを変える。");
            _txtNetworkName = AddTextBox(metaLayout, "ネットワーク名", "VAT-01", 16,
                "所属ネットワーク名(mMTSChannelParam.NetworkName)。");
            _txtTSName = AddTextBox(metaLayout, "TS名", "VAT-01", 16,
                "トランスポートストリーム名(mMTSChannelParam.TSName)。");
            _numRegionID = AddNumeric(metaLayout, "地域識別 (RegionID)", 0, 63, 23,
                "ARIB STD-B10の県域識別番号。既定23は動作実績のある値(実際の地域コードとは対応していない可能性がある)。");
            _numBroadcasterID = AddNumeric(metaLayout, "放送事業者ID", 0, 15, 1,
                "mMTSChannelParam.BroadcasterID。");
            _numRemoteControlKeyID = AddNumeric(metaLayout, "リモコン番号", 0, 12, 1,
                "実受信機のリモコンの数字キーに割り当てられるチャンネル番号。");
            _numServiceNo = AddNumeric(metaLayout, "サービス番号", 0, 8, 1,
                "mMTSProgramParam.ServiceNo。");
            _cmbCopyFlag = AddCombo(metaLayout, "コピー制御", new[]
            {
                new ComboItem(0, "Free (既定)"),
                new ComboItem(2, "CopyOnce"),
                new ComboItem(3, "Forbidden"),
            }, 0, "コピー制御記述子(mMTSProgramParam.CopyFlag)。");
            _numPcrPid = AddNumericHex(metaLayout, "PCR PID", 0, 65535, 0x0100,
                "mMTSProgramParam.PCR_PID。STUDIO本体の「放送設定」で確認済み(続報21)、従来は未設定だった。");
            _numPmtPid = AddNumericHex(metaLayout, "PMT PID", 0, 65535, 0x0101,
                "mMTSProgramParam.PMT_PID。STUDIO本体の「放送設定」で確認済み(続報21)、従来は未設定だった。");

            var epgLayout = NewParamTable();
            epgLayout.Padding = new Padding(10, 10, 6, 10);
            _cmbEPGMode = AddCombo(epgLayout, "EPGモード", new[]
            {
                new ComboItem(0, "Disable"),
                new ComboItem(1, "PresentFollowingOnly"),
                new ComboItem(256, "AribPresentFollowingOnly"),
                new ComboItem(257, "AribSchedule_8Days (既定)"),
            }, 3, "mEPGSimpleParam.Mode。1件の番組情報をこのモードに従って繰り返し配信する" +
                "(複数番組の直接設定手段はハードウェア側に無いことを確認済み、続報11参照)。");
            _numEPGIntervalHours = AddNumeric(epgLayout, "配信間隔 (時間)", 0, 8, 1,
                "mEPGSimpleParam.IntervalHours。");
            _numEPGEventID = AddNumeric(epgLayout, "イベントID", 0, 65535, 4096,
                "mEPGSimpleParam.EventID。");
            _cmbEPGType = AddCombo(epgLayout, "ジャンル", new[]
            {
                new ComboItem(0, "Undefine (既定)"),
                new ComboItem(1, "News"), new ComboItem(2, "Sport"), new ComboItem(3, "Movie"),
                new ComboItem(4, "Drama"), new ComboItem(5, "Music"), new ComboItem(6, "Tabloidshow"),
                new ComboItem(7, "Varietyshow"), new ComboItem(8, "Animation"), new ComboItem(9, "Documentary"),
                new ComboItem(10, "Performance"), new ComboItem(11, "Education"), new ComboItem(12, "Welfare"),
                new ComboItem(255, "Others"),
            }, 0, "mEPGSimpleParam.Type。");
            _txtEPGTitle = AddTextBox(epgLayout, "タイトル", "VA-TV", 256,
                "mEPGSimpleParam.Title。番組タイトル。");
            _txtEPGDescriptor = AddTextBox(epgLayout, "番組内容", "VA-TV", 256,
                "mEPGSimpleParam.Descriptor。番組内容の説明文。");

            var mediaLayout = NewParamTable();
            mediaLayout.Padding = new Padding(10, 10, 6, 4);
            _cmbEncodePerformance = AddCombo(mediaLayout, "エンコード速度", new[]
            {
                new ComboItem(2, "Fast (既定)"), new ComboItem(3, "Standard"),
                new ComboItem(4, "Slow"), new ComboItem(5, "Slower"),
            }, 0, "mPSEncodeParam.Performance。遅いほど高品質になる想定(未検証)。");
            _numVideoPID = AddNumericHex(mediaLayout, "Video PID", 0, 8191, 0x0110,
                "mPSEncodeParam.VIDEO_PID。STUDIOのDebugモードの「メディア設定」で見えるものと同じ項目。");
            _numAudioPID = AddNumericHex(mediaLayout, "Audio PID", 0, 8191, 0x0120,
                "mPSEncodeParam.AUDIO_PID。");
            _numLatency = AddNumeric(mediaLayout, "レイテンシ (ms)", 0, 1000, 500,
                "mPSEncodeParam.Latency。");
            _numQueueTime = AddNumeric(mediaLayout, "キュー時間 (秒)", 0, 30, 1,
                "mPSEncodeParam.QueueTime。");
            _cmbVideoResolution = AddCombo(mediaLayout, "映像解像度", new[]
            {
                new ComboItem(0, "1080P"), new ComboItem(1, "1080I (既定)"), new ComboItem(2, "1440P"),
                new ComboItem(3, "1440I"), new ComboItem(4, "720P"), new ComboItem(5, "480P"), new ComboItem(6, "480I"),
            }, 1, "mPSEncodeParam.Video.Resolution。");
            _cmbVideoAspectRatio = AddCombo(mediaLayout, "アスペクト比", new[]
            {
                new ComboItem(5, "1:1"), new ComboItem(6, "4:3"), new ComboItem(7, "16:9 (既定)"), new ComboItem(8, "2.21:1"),
            }, 2, "mPSEncodeParam.Video.AspectRatio。");
            _cmbVideoFrameRate = AddCombo(mediaLayout, "フレームレート", new[]
            {
                new ComboItem(0, "23.97"), new ComboItem(1, "24"), new ComboItem(2, "25"),
                new ComboItem(3, "29.97 (既定)"), new ComboItem(4, "30"), new ComboItem(5, "50"),
                new ComboItem(6, "59.94"), new ComboItem(7, "60"),
            }, 3, "mPSEncodeParam.Video.FrameRate。");
            _cmbAudioChannel = AddCombo(mediaLayout, "音声チャンネル", new[]
            {
                new ComboItem(0, "Stereo (既定)"), new ComboItem(2, "DualChannel"), new ComboItem(3, "Mono"),
            }, 0, "mPSEncodeParam.Audio.Channel。");
            _numAudioSampleRate = AddNumeric(mediaLayout, "音声サンプルレート (Hz)", 32000, 48000, 48000,
                "mPSEncodeParam.Audio.SampleRate。");
            _numAudioBitrate = AddNumeric(mediaLayout, "音声ビットレート (bps)", 128000, 384000, 128000,
                "mPSEncodeParam.Audio.Bitrate。");
            _cmbQualityMode = AddCombo(mediaLayout, "レート制御方式", new[]
            {
                new ComboItem(0, "CBR (既定)"), new ComboItem(1, "VBRAvgBitRate"), new ComboItem(2, "VBRQuality"),
            }, 0, "mPSEncodeParam.Quality.Mode。");
            _numGOPLength = AddNumeric(mediaLayout, "GOP長", 0, 60, 18,
                "mPSEncodeParam.Quality.GOPLength。");
            var bmlRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 2, 0, 4) };
            _txtBMLFile = new TextBox { Width = 220, Anchor = AnchorStyles.Left, Margin = new Padding(4, 3, 4, 3) };
            _btnBrowseBML = new Button { Text = "参照...", Width = 70, Anchor = AnchorStyles.Left };
            _btnBrowseBML.Click += BtnBrowseBML_Click;
            bmlRow.Controls.Add(_txtBMLFile);
            bmlRow.Controls.Add(_btnBrowseBML);
            int bmlRow_ = mediaLayout.RowStyles.Count;
            mediaLayout.RowCount = bmlRow_ + 1;
            var bmlLabel = new Label { Text = "BMLファイル (.xbml)", Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            mediaLayout.Controls.Add(bmlLabel, 0, bmlRow_);
            mediaLayout.Controls.Add(bmlRow, 1, bmlRow_);
            mediaLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            SetHelpTip(bmlLabel, "データ放送・字幕用の独自.xbmlです。通常の.tsは指定できません。");
            SetHelpTip(_txtBMLFile, "空欄なら使用しません。");

            // 2026-07-27 (続報21): STUDIO本体のGUIを一通り操作して発見した、これまで未実装
            // だった詳細エンコーダ設定。STUDIO側も「メディア設定」(上のmediaLayout相当)とは
            // 別の「コーデック設定」サブページとして分けているため、GUIでもタブを分ける。
            var codecLayout = NewParamTable();
            codecLayout.Padding = new Padding(10, 10, 6, 4);
            _cmbVideoFormat = AddCombo(codecLayout, "映像信号", new[]
            {
                new ComboItem(0, "Component"), new ComboItem(1, "NTSC"), new ComboItem(2, "PAL"),
                new ComboItem(3, "SECAM"), new ComboItem(4, "MAC"), new ComboItem(5, "Unspecified"),
                new ComboItem(6, "Automatic (既定)"),
            }, 6, "mPSEncodeParam.Video.VideoFormat。STUDIOの「映像信号」に相当。");
            _cmbVideoField = AddCombo(codecLayout, "フィールドオーダー", new[]
            {
                new ComboItem(0, "TopFieldFirst (既定)"), new ComboItem(1, "BottomFieldFirst"),
            }, 0, "mPSEncodeParam.Video.Field。STUDIOの「フィールドオーダー」に相当。");
            _cmbColorPrimaries = AddCombo(codecLayout, "カラープライマリー", new[]
            {
                new ComboItem(0, "Automatic (既定)"), new ComboItem(1, "ITU_R_BT_709"), new ComboItem(2, "Unspecified"),
                new ComboItem(3, "ITU_R_BT_470_2_System_M"), new ComboItem(4, "ITU_R_BT_470_2_System_B_G"),
                new ComboItem(5, "SMPTE_170M"), new ComboItem(6, "SMPTE_240M"),
            }, 0, "mPSEncodeParam.Video.ColorPrimaries。");
            _cmbTransferCharacteristics = AddCombo(codecLayout, "変換特性", new[]
            {
                new ComboItem(0, "Automatic (既定)"), new ComboItem(1, "ITU_R_BT_709"), new ComboItem(2, "Unspecified"),
                new ComboItem(3, "ITU_R_BT_470_2_System_M"), new ComboItem(4, "ITU_R_BT_470_2_System_B_G"),
                new ComboItem(5, "SMPTE_170M"), new ComboItem(6, "SMPTE_240M"),
            }, 0, "mPSEncodeParam.Video.TransferCharacteristics。");
            _cmbMatrixCoefficients = AddCombo(codecLayout, "マトリクス係数", new[]
            {
                new ComboItem(0, "Automatic (既定)"), new ComboItem(1, "ITU_R_BT_709"), new ComboItem(2, "Unspecified"),
                new ComboItem(3, "FCC"), new ComboItem(4, "ITU_R_BT_470_2_System_B_G"),
                new ComboItem(5, "SMPTE_170M"), new ComboItem(6, "SMPTE_240M"),
            }, 0, "mPSEncodeParam.Video.MatrixCoefficients。");
            _numGOPMinLength = AddNumeric(codecLayout, "GOP最小フレーム数", 0, 60, 0,
                "mPSEncodeParam.Quality.GOPMinLength。");
            _numGOPMaxLength = AddNumeric(codecLayout, "GOP最大フレーム数", 0, 60, 0,
                "mPSEncodeParam.Quality.GOPMaxLength。");
            _numBFrameCount = AddNumeric(codecLayout, "GOP内連続Bピクチャ最大数", 0, 2, 2,
                "mPSEncodeParam.Quality.BFrameCount。");
            _chkEnableDetechSceneChange = AddCheckBox(codecLayout, "シーンチェンジ検出を有効にする", true,
                "mPSEncodeParam.Quality.Functions flag1(EnableDetechSceneChange)。STUDIOは既定ON。");
            _chkEnableTwoPass = AddCheckBox(codecLayout, "TwoPass", false,
                "mPSEncodeParam.Quality.Functions flag2(EnableTwoPass)。STUDIOは既定OFF。");
            _numBitrateRatio = AddNumeric(codecLayout, "映像レート", 0, 255, 50,
                "mPSEncodeParam.Quality.BitrateRatio。STUDIOの「映像レート」スライダに相当(0-255)。");
            _numMinBitrateRatio = AddNumeric(codecLayout, "ビットレート最低値", 0, 255, 50,
                "mPSEncodeParam.Quality.MinBitrateRatio。");
            _numMaxBitrateRatio = AddNumeric(codecLayout, "ビットレート最高値", 0, 255, 50,
                "mPSEncodeParam.Quality.MaxBitrateRatio。");
            _numQualityRatio = AddNumeric(codecLayout, "画質レベル", 0, 100, 50,
                "mPSEncodeParam.Quality.QualityRatio。STUDIOの「画質レベル」に相当(0-100)。");
            _chkEnableDebugFunction = AddCheckBox(codecLayout, "デバッグ機能を有効にする", false,
                "mPSEncodeParam.Functions flag1(EnableDebug)。STUDIOは既定OFF、通常は変更不要。");

            var modLayout = NewParamTable();
            modLayout.Padding = new Padding(10, 10, 6, 4);
            _numFrequency = AddNumeric(modLayout, "周波数 (kHz)", 0, 1000000, 473000,
                "送出する中心周波数(kHz)。既定の473000kHzはUHF473MHz(ISDB-Tの標準チャンネルの1つ)。");
            _cmbMode = AddCombo(modLayout, "Mode（直接USB専用）", new[]
            {
                new ComboItem(0, "DVB_T"),
                new ComboItem(1, "J83A"),
                new ComboItem(2, "ATSC"),
                new ComboItem(3, "J83B"),
                new ComboItem(4, "DTMB（注意）"),
                new ComboItem(5, "ISDB_T (既定)"),
                new ComboItem(6, "J83C（注意）"),
            }, 5, "変調方式のMode切替(続報12・13・19・22・24)。「直接USB」接続方式でのみ有効 -- " +
                "mnservice.exe経由バックエンドはISDB_T固定。ここに出ている7つは実機で安全に" +
                "送出できると確認済みのModeのみ。DTMB/J83Cは「直接USB」バックエンドでは正常に" +
                "動作するが、mnservice.exe経由（「mnservice.exe経由」バックエンド選択時）だと" +
                "サービス全体をハングさせる既知のバグがあるため、そちらでは絶対に選ばないこと" +
                "(そもそもMode切替はmnservice.exe経由バックエンドでは無効化されている)。" +
                "DVB_T2は最小Mode 7列のみ確認済みで固有レジスタが未解読のためGUIには意図的に出していない。");
            _cmbMode.SelectedIndexChanged += ModeChanged;
            _cmbConstellation = AddCombo(modLayout, "変調方式", new[]
            {
                new ComboItem(0, "DQPSK"),
                new ComboItem(1, "QPSK (既定)"),
                new ComboItem(2, "16QAM"),
                new ComboItem(3, "64QAM"),
            }, 1, "変調方式のキャリア変調(Constellation)。選択したModeによって有効な値が変わる。");
            _numBandwidth = AddNumeric(modLayout, "帯域幅 (MHz)", 0, 10, 6,
                "占有帯域幅(MHz)。日本のISDB-Tは通常6MHz固定。");
            _cmbFFT = AddCombo(modLayout, "FFTモード", new[]
            {
                new ComboItem(0, "2k"),
                new ComboItem(1, "8k (既定)"),
                new ComboItem(2, "4k"),
            }, 1, "OFDMのFFTサイズ(モード)。日本の地上デジタル放送は通常モード3(8k)。");
            _cmbCodeRate = AddCombo(modLayout, "符号化率", new[]
            {
                new ComboItem(0, "1/2"),
                new ComboItem(1, "2/3"),
                new ComboItem(2, "3/4"),
                new ComboItem(3, "5/6 (既定)"),
                new ComboItem(4, "7/8"),
            }, 3, "畳み込み符号の符号化率。値が大きいほど伝送効率は上がるが誤り耐性は下がる。");
            _cmbGuardInterval = AddCombo(modLayout, "ガードインターバル", new[]
            {
                new ComboItem(0, "1/32"),
                new ComboItem(1, "1/16 (既定)"),
                new ComboItem(2, "1/8"),
                new ComboItem(3, "1/4"),
            }, 1, "シンボル間のガードインターバル比。マルチパス耐性と伝送効率のトレードオフ。");
            _cmbTimeInterleavce = AddCombo(modLayout, "時間インターリーブ", new[]
            {
                new ComboItem(1, "モード1"),
                new ComboItem(2, "モード2"),
                new ComboItem(3, "モード3 (既定)"),
            }, 2, "時間インターリーブの深さ(ISDB_T)。DTMB選択時はInterleave(TI_240/TI_720)として" +
                "流用される。深いほどバースト誤りに強いが遅延が増える。");
            _cmbCarrier = AddCombo(modLayout, "Carrier（DTMB専用）", new[]
            {
                new ComboItem(0, "CARRIER_3780 (既定)"),
                new ComboItem(1, "CARRIER_1"),
            }, 0, "DTMB専用のキャリア方式。DTMB以外のModeでは無効。");
            _cmbFrame = AddCombo(modLayout, "Frame（DTMB専用）", new[]
            {
                new ComboItem(0, "FRAME_420"),
                new ComboItem(1, "FRAME_945 (既定)"),
                new ComboItem(2, "FRAME_595"),
            }, 1, "DTMB専用のフレーム構成。DTMB以外のModeでは無効。");

            // RF電力設定は変調パラメータと同じタブにまとめる(どちらもRF/OFDM物理層の設定なので)。
            // 区切りを分かるよう小見出しラベルを挟む。
            var powerHeader = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 24,
                Text = "RF電力設定",
                Font = new Font(Font, FontStyle.Bold),
                Padding = new Padding(10, 8, 0, 0),
            };
            var powerLayout = NewParamTable();
            powerLayout.Padding = new Padding(10, 4, 6, 10);
            _numLevel = AddNumeric(powerLayout, "Level (80〜100)", 80, 100, 90,
                "周波数ごとのPA/DACゲイン表を引く際の添字(80〜100)。Level単体では出力に変化なし --"
                + "PAGain/DACGainと必ず一緒に送ること。");
            _numPAGain = AddNumeric(powerLayout, "PAGain", -128, 127, 2,
                "パワーアンプの生ゲイン値(int8)。実機ログでは物理的な効果は未確認 -- "
                + "2026-07-26の解析でmCalibrationという較正テーブルと突き合わせて使われている可能性が判明。");
            _numDACGain = AddNumeric(powerLayout, "DACGain", -128, 127, -10,
                "DACの生ゲイン値(int8)。RF出力電力に直接反映されることを実機ログとRTL-SDRで確認済み。");

            // 変調パラメータ + RF電力設定の1タブ分。同じDockStyle.Topの重なり順の都合上、
            // ここも逆順でAddする(modLayoutが一番上、powerLayoutが一番下に来てほしい)。
            var modTab = new TabPage("変調/RF電力設定") { AutoScroll = true };
            modTab.Controls.Add(powerLayout);
            modTab.Controls.Add(powerHeader);
            modTab.Controls.Add(modLayout);

            var metaTab = new TabPage("チャンネル/番組情報") { AutoScroll = true };
            metaTab.Controls.Add(metaLayout);

            var epgTab = new TabPage("EPG") { AutoScroll = true };
            epgTab.Controls.Add(epgLayout);

            var mediaTab = new TabPage("メディア/コーデック") { AutoScroll = true };
            mediaTab.Controls.Add(mediaLayout);

            var codecTab = new TabPage("詳細コーデック") { AutoScroll = true };
            codecTab.Controls.Add(codecLayout);

            var sourceTab = new TabPage("ソース") { AutoScroll = true };
            sourceTab.Controls.Add(sourceLayout);

            var tabControl = new TabControl { Dock = DockStyle.Fill };
            tabControl.TabPages.Add(sourceTab);
            tabControl.TabPages.Add(metaTab);
            tabControl.TabPages.Add(epgTab);
            tabControl.TabPages.Add(mediaTab);
            tabControl.TabPages.Add(codecTab);
            tabControl.TabPages.Add(modTab);

            var logLabel = new Label { Dock = DockStyle.Top, Height = 22, Text = "ログ:", Padding = new Padding(8, 6, 0, 0) };
            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 8.5f),
            };
            var logPanel = new Panel { Dock = DockStyle.Bottom, Height = 180 };
            logPanel.Controls.Add(_txtLog);
            logPanel.Controls.Add(logLabel);

            // 実際の画面上での見た目(上から): 警告 -> 状態 -> ボタン -> [タブ(ソース/チャンネル
            // 番組情報/変調・RF電力設定)が残り領域いっぱい] -> ログ欄(下端に固定高さで常時表示)。
            // 注意: 同じDockStyle.Top同士では、後からControls.Addした方が画面上は上に来る
            // (直感と逆)。Dock=Fillは、Top/Bottomが確保した後の残り領域を埋めるため、一番最初に
            // Addする必要がある。そのためこの一括Addは視覚順とは逆順で書く。
            Controls.Add(tabControl);
            Controls.Add(logPanel);
            Controls.Add(btnPanel);
            Controls.Add(backendPanel);
            Controls.Add(_lblStatus);
            Controls.Add(warnLabel);

            // タブの参照をフィールドに保持しておき、接続方式切り替え時に有効/無効を切り替える。
            _sourceTab = sourceTab;
            _metaTab = metaTab;
            _epgTab = epgTab;
            _mediaTab = mediaTab;
            _codecTab = codecTab;
        }

        private static TableLayoutPanel NewParamTable()
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                Width = 280,
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            return t;
        }

        private void SetHelpTip(Control control, string text)
        {
            const int columns = 38;
            var formatted = new StringBuilder(text.Length + text.Length / columns);
            int lineLength = 0;
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n')
                {
                    formatted.AppendLine();
                    lineLength = 0;
                    continue;
                }
                if (lineLength >= columns && (c == ' ' || c == '、' || c == '。' || c == '('))
                {
                    formatted.AppendLine();
                    lineLength = 0;
                    if (c == ' ') continue;
                }
                formatted.Append(c);
                lineLength++;
            }
            _toolTip.SetToolTip(control, formatted.ToString());
        }

        private NumericUpDown AddNumeric(TableLayoutPanel layout, string label, decimal min, decimal max, decimal value, string tooltip)
        {
            int row = layout.RowStyles.Count;
            layout.RowCount = row + 1;
            var lbl = new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            layout.Controls.Add(lbl, 0, row);
            var num = new NumericUpDown { Minimum = min, Maximum = max, Value = value, Width = 110, Anchor = AnchorStyles.Left };
            layout.Controls.Add(num, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            SetHelpTip(lbl, tooltip);
            SetHelpTip(num, tooltip);
            return num;
        }

        private NumericUpDown AddNumericHex(TableLayoutPanel layout, string label, decimal min, decimal max, decimal value, string tooltip)
        {
            var num = AddNumeric(layout, label, min, max, value, tooltip);
            num.Hexadecimal = true;
            return num;
        }

        private ComboBox AddCombo(TableLayoutPanel layout, string label, ComboItem[] items, int selectedIndex, string tooltip)
        {
            int row = layout.RowStyles.Count;
            layout.RowCount = row + 1;
            var lbl = new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            layout.Controls.Add(lbl, 0, row);
            var cmb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Anchor = AnchorStyles.Left };
            foreach (var item in items) cmb.Items.Add(item);
            cmb.SelectedIndex = selectedIndex;
            layout.Controls.Add(cmb, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            SetHelpTip(lbl, tooltip);
            SetHelpTip(cmb, tooltip);
            return cmb;
        }

        private TextBox AddTextBox(TableLayoutPanel layout, string label, string value, int maxLength, string tooltip)
        {
            int row = layout.RowStyles.Count;
            layout.RowCount = row + 1;
            var lbl = new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            layout.Controls.Add(lbl, 0, row);
            var txt = new TextBox { Text = value, MaxLength = maxLength, Width = 110, Anchor = AnchorStyles.Left };
            layout.Controls.Add(txt, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            SetHelpTip(lbl, tooltip);
            SetHelpTip(txt, tooltip);
            return txt;
        }

        private CheckBox AddCheckBox(TableLayoutPanel layout, string label, bool value, string tooltip)
        {
            int row = layout.RowStyles.Count;
            layout.RowCount = row + 1;
            var lbl = new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            layout.Controls.Add(lbl, 0, row);
            var chk = new CheckBox { Checked = value, AutoSize = true, Anchor = AnchorStyles.Left };
            layout.Controls.Add(chk, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            SetHelpTip(lbl, tooltip);
            SetHelpTip(chk, tooltip);
            return chk;
        }

        private static int SelectedValue(ComboBox cmb) => ((ComboItem)cmb.SelectedItem).Value;

        private ModulationConfig ReadConfigFromForm()
        {
            return new ModulationConfig
            {
                Frequency = (uint)_numFrequency.Value,
                Mode = (uint)SelectedValue(_cmbMode),
                Constellation = SelectedValue(_cmbConstellation),
                Bandwidth = (uint)_numBandwidth.Value,
                FFT = SelectedValue(_cmbFFT),
                CodeRate = SelectedValue(_cmbCodeRate),
                GuardInterval = SelectedValue(_cmbGuardInterval),
                TimeInterleavce = SelectedValue(_cmbTimeInterleavce),
                Carrier = (uint)SelectedValue(_cmbCarrier),
                Frame = (uint)SelectedValue(_cmbFrame),
                Level = (uint)_numLevel.Value,
                PAGain = (int)_numPAGain.Value,
                DACGain = (int)_numDACGain.Value,
                RegionID = (uint)_numRegionID.Value,
                BroadcasterID = (uint)_numBroadcasterID.Value,
                RemoteControlKeyID = (uint)_numRemoteControlKeyID.Value,
                NetworkName = _txtNetworkName.Text,
                TSName = _txtTSName.Text,
                ServiceNo = (uint)_numServiceNo.Value,
                ServiceName = _txtServiceName.Text,
                CopyFlag = SelectedValue(_cmbCopyFlag),
                PcrPid = (uint)_numPcrPid.Value,
                PmtPid = (uint)_numPmtPid.Value,
                EPGMode = SelectedValue(_cmbEPGMode),
                EPGIntervalHours = (uint)_numEPGIntervalHours.Value,
                EPGEventID = (uint)_numEPGEventID.Value,
                EPGType = SelectedValue(_cmbEPGType),
                EPGTitle = _txtEPGTitle.Text,
                EPGDescriptor = _txtEPGDescriptor.Text,
                EncodePerformance = SelectedValue(_cmbEncodePerformance),
                VideoPID = (uint)_numVideoPID.Value,
                AudioPID = (uint)_numAudioPID.Value,
                Latency = (uint)_numLatency.Value,
                QueueTime = (uint)_numQueueTime.Value,
                VideoResolution = SelectedValue(_cmbVideoResolution),
                VideoAspectRatio = SelectedValue(_cmbVideoAspectRatio),
                VideoFrameRate = SelectedValue(_cmbVideoFrameRate),
                AudioChannel = SelectedValue(_cmbAudioChannel),
                AudioSampleRate = (int)_numAudioSampleRate.Value,
                AudioBitrate = (int)_numAudioBitrate.Value,
                QualityMode = SelectedValue(_cmbQualityMode),
                GOPLength = (uint)_numGOPLength.Value,
                BMLFile = _txtBMLFile.Text,
                VideoField = SelectedValue(_cmbVideoField),
                VideoFormat = SelectedValue(_cmbVideoFormat),
                ColorPrimaries = SelectedValue(_cmbColorPrimaries),
                TransferCharacteristics = SelectedValue(_cmbTransferCharacteristics),
                MatrixCoefficients = SelectedValue(_cmbMatrixCoefficients),
                EnableDebugFunction = _chkEnableDebugFunction.Checked,
                BitrateRatio = (uint)_numBitrateRatio.Value,
                MinBitrateRatio = (uint)_numMinBitrateRatio.Value,
                MaxBitrateRatio = (uint)_numMaxBitrateRatio.Value,
                BFrameCount = (uint)_numBFrameCount.Value,
                QualityRatio = (uint)_numQualityRatio.Value,
                GOPMinLength = (uint)_numGOPMinLength.Value,
                GOPMaxLength = (uint)_numGOPMaxLength.Value,
                EnableDetechSceneChange = _chkEnableDetechSceneChange.Checked,
                EnableTwoPass = _chkEnableTwoPass.Checked,
            };
        }

        private void SetStatus(string text, Color color)
        {
            _lblStatus.Text = "状態: " + text;
            _lblStatus.ForeColor = color;
        }

        private void SourceRadioChanged(object sender, EventArgs e)
        {
            if (_updatingSourceRadios) return;
            var changed = (RadioButton)sender;
            if (!changed.Checked) return;
            _updatingSourceRadios = true;
            try
            {
                if (changed != _rbSourceNone) _rbSourceNone.Checked = false;
                if (changed != _rbSourceCapture) _rbSourceCapture.Checked = false;
                if (changed != _rbSourceColorbar) _rbSourceColorbar.Checked = false;
                if (changed != _rbSourceUrl) _rbSourceUrl.Checked = false;
            }
            finally
            {
                _updatingSourceRadios = false;
            }
        }

        private void BackendChanged(object sender, EventArgs e)
        {
            bool direct = UseDirectBackend;
            _sourceTab.Enabled = !direct;
            _metaTab.Enabled = !direct;
            _epgTab.Enabled = !direct;
            _mediaTab.Enabled = !direct;
            _codecTab.Enabled = !direct;
            _cmbMode.Enabled = direct;
            if (!direct)
            {
                // GuiSession(mnservice.exe経由)はISDB_T固定 -- Mode切替は続報19時点で未対応。
                SelectComboValue(_cmbMode, 5);
            }
            if (direct)
            {
                Console.WriteLine("[GUI] 直接USBモードを選択 -- Source添付・チャンネル/番組メタデータ・EPG・" +
                    "メディア/コーデック設定は利用できません(いずれもmnservice.exe側のソフトウェア機能で、" +
                    "レジスタバスに対応物が無いため)。mnservice.exe/xhead_studio.exeを事前に停止しておいてください。");
            }
        }

        private static void SelectComboValue(ComboBox cmb, int value)
        {
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                if (((ComboItem)cmb.Items[i]).Value == value) { cmb.SelectedIndex = i; return; }
            }
        }

        /// <summary>
        /// 続報19・22: Mode(直接USB専用)を切り替えたら、変調方式(Constellation)の選択肢と、
        /// そのModeが実際に使わないフィールド(Bandwidth/FFT/CodeRate/GuardInterval/
        /// TimeInterleavce/Carrier/Frame)の有効/無効を実機ネイティブキャプチャの結果に合わせて
        /// 更新する(docs/protocol/modulation_capabilities.md「続報19・22」-- ATSC/J83B/J83Cは
        /// Constellationのみ、DVB_TはTimeInterleavceを持たない、DTMBは独自のフィールド構成)。
        /// </summary>
        private void ModeChanged(object sender, EventArgs e)
        {
            uint mode = (uint)SelectedValue(_cmbMode);
            ComboItem[] items;
            int selectedIndex;
            switch (mode)
            {
                case 0: // DVB_T
                    items = new[] { new ComboItem(0, "QPSK"), new ComboItem(2, "16QAM"), new ComboItem(4, "64QAM (既定)") };
                    selectedIndex = 2;
                    break;
                case 1: // J83A
                    items = new[] { new ComboItem(0, "16QAM"), new ComboItem(1, "32QAM"),
                        new ComboItem(2, "64QAM (既定)"), new ComboItem(3, "128QAM"), new ComboItem(4, "256QAM") };
                    selectedIndex = 2;
                    break;
                case 2: // ATSC
                    items = new[] { new ComboItem(0, "8VSB (既定)") };
                    selectedIndex = 0;
                    break;
                case 3: // J83B
                    items = new[] { new ComboItem(1, "64QAM (既定)"), new ComboItem(3, "256QAM") };
                    selectedIndex = 0;
                    break;
                case 4: // DTMB
                    items = new[] { new ComboItem(0, "QPSK"), new ComboItem(1, "16QAM"), new ComboItem(2, "64QAM (既定)"),
                        new ComboItem(3, "QAM4_NR"), new ComboItem(4, "32QAM") };
                    selectedIndex = 2;
                    break;
                case 6: // J83C
                    items = new[] { new ComboItem(2, "64QAM (既定)"), new ComboItem(4, "256QAM") };
                    selectedIndex = 0;
                    break;
                default: // 5 = ISDB_T
                    items = new[] { new ComboItem(0, "DQPSK"), new ComboItem(1, "QPSK (既定)"), new ComboItem(2, "16QAM"), new ComboItem(3, "64QAM") };
                    selectedIndex = 1;
                    break;
            }
            _cmbConstellation.Items.Clear();
            foreach (var item in items) _cmbConstellation.Items.Add(item);
            _cmbConstellation.SelectedIndex = selectedIndex;

            bool hasOfdmFields = mode == 0 || mode == 5;   // DVB_T, ISDB_T
            bool hasTimeInterleave = mode == 5;             // ISDB_T のみ
            bool isDtmb = mode == 4;
            _numBandwidth.Enabled = hasOfdmFields || isDtmb;
            _cmbFFT.Enabled = hasOfdmFields;
            _cmbGuardInterval.Enabled = hasOfdmFields;
            _cmbCarrier.Enabled = isDtmb;
            _cmbFrame.Enabled = isDtmb;

            if (isDtmb)
            {
                // DTMB専用のCodeRate(CR_0_4/0_6/0_8)・Interleave(TI_240/TI_720)選択肢に差し替える。
                _cmbCodeRate.Items.Clear();
                _cmbCodeRate.Items.Add(new ComboItem(0, "CR_0_4"));
                _cmbCodeRate.Items.Add(new ComboItem(1, "CR_0_6"));
                _cmbCodeRate.Items.Add(new ComboItem(2, "CR_0_8 (既定)"));
                _cmbCodeRate.SelectedIndex = 2;
                _cmbTimeInterleavce.Items.Clear();
                _cmbTimeInterleavce.Items.Add(new ComboItem(2, "TI_240"));
                _cmbTimeInterleavce.Items.Add(new ComboItem(3, "TI_720 (既定)"));
                _cmbTimeInterleavce.SelectedIndex = 1;
                _cmbCodeRate.Enabled = true;
                _cmbTimeInterleavce.Enabled = true;
            }
            else
            {
                _cmbCodeRate.Items.Clear();
                _cmbCodeRate.Items.Add(new ComboItem(0, "1/2"));
                _cmbCodeRate.Items.Add(new ComboItem(1, "2/3"));
                _cmbCodeRate.Items.Add(new ComboItem(2, "3/4"));
                _cmbCodeRate.Items.Add(new ComboItem(3, "5/6 (既定)"));
                _cmbCodeRate.Items.Add(new ComboItem(4, "7/8"));
                _cmbCodeRate.SelectedIndex = 3;
                _cmbTimeInterleavce.Items.Clear();
                _cmbTimeInterleavce.Items.Add(new ComboItem(1, "モード1"));
                _cmbTimeInterleavce.Items.Add(new ComboItem(2, "モード2"));
                _cmbTimeInterleavce.Items.Add(new ComboItem(3, "モード3 (既定)"));
                _cmbTimeInterleavce.SelectedIndex = 2;
                _cmbCodeRate.Enabled = hasOfdmFields;
                _cmbTimeInterleavce.Enabled = hasTimeInterleave;
            }
        }

        private async void BtnConnect_Click(object sender, EventArgs e)
        {
            _btnConnect.Enabled = false;
            _rbBackendService.Enabled = false;
            _rbBackendDirect.Enabled = false;
            try
            {
                if (UseDirectBackend)
                {
                    await Task.Run(() => _directSession.Open());
                }
                else
                {
                    await Task.Run(() => _session.Connect());
                }
                SetStatus("接続済み（送出停止中）", Color.SteelBlue);
                _btnStart.Enabled = true;
                _btnDisconnect.Enabled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("接続失敗: " + ex.Message);
                SetStatus("未接続（接続失敗）", Color.Firebrick);
                _btnConnect.Enabled = true;
                _rbBackendService.Enabled = true;
                _rbBackendDirect.Enabled = true;
            }
        }

        private void SetSourceControlsEnabled(bool enabled)
        {
            _rbSourceNone.Enabled = enabled;
            _rbSourceCapture.Enabled = enabled;
            _rbSourceColorbar.Enabled = enabled;
            _rbSourceUrl.Enabled = enabled;
            _txtUrlPath.Enabled = enabled;
            _btnBrowseUrl.Enabled = enabled;
        }

        private void BtnBrowseUrl_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog { Filter = "TS/動画ファイル (*.ts;*.m2ts;*.mp4)|*.ts;*.m2ts;*.mp4|すべてのファイル (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtUrlPath.Text = dlg.FileName;
                    _rbSourceUrl.Checked = true;
                }
            }
        }

        private void BtnBrowseBML_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog { Filter = "BMLコンテナ (*.xbml)|*.xbml|すべてのファイル (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtBMLFile.Text = dlg.FileName;
                }
            }
        }

        private async void BtnStart_Click(object sender, EventArgs e)
        {
            _btnStart.Enabled = false;
            SetSourceControlsEnabled(false);
            var cfg = ReadConfigFromForm();

            if (UseDirectBackend)
            {
                try
                {
                    await Task.Run(() => _directSession.StartChannel(cfg));
                    SetStatus("送出中（直接USB、RFのみ）", Color.SeaGreen);
                    _btnStop.Enabled = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("送出開始失敗: " + ex.Message);
                    SetStatus("接続済み（送出失敗）", Color.Firebrick);
                    _btnStart.Enabled = true;
                    SetSourceControlsEnabled(true);
                }
                return;
            }

            bool attachCapture = _rbSourceCapture.Checked;
            bool attachColorbar = _rbSourceColorbar.Checked;
            bool attachUrl = _rbSourceUrl.Checked;
            string urlPath = _txtUrlPath.Text;
            try
            {
                await Task.Run(() =>
                {
                    _session.StartChannel(cfg);
                    if (attachCapture)
                    {
                        _session.StartCaptureSource();
                    }
                    else if (attachColorbar)
                    {
                        _session.StartColorbarSource();
                    }
                    else if (attachUrl)
                    {
                        _session.StartUrlSource(urlPath);
                    }
                });
                string label = attachCapture ? "送出中（デスクトップキャプチャ添付）" :
                    attachColorbar ? "送出中（カラーバー添付）" :
                    attachUrl ? "送出中（動画ファイル添付）" : "送出中（RFのみ）";
                SetStatus(label, Color.SeaGreen);
                _btnStop.Enabled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("送出開始失敗: " + ex.Message);
                // ChannelStartだけ成功してソース添付側で失敗した場合、RFは出続けているので
                // 「送出失敗」ではなく実情に合わせた表示にする。
                if (_session.ChannelStarted)
                {
                    SetStatus("送出中（ソース添付失敗、RFのみ）", Color.DarkOrange);
                    _btnStop.Enabled = true;
                }
                else
                {
                    SetStatus("接続済み（送出失敗）", Color.Firebrick);
                    _btnStart.Enabled = true;
                    SetSourceControlsEnabled(true);
                }
            }
        }

        private async void BtnStop_Click(object sender, EventArgs e)
        {
            _btnStop.Enabled = false;
            try
            {
                if (UseDirectBackend)
                {
                    await Task.Run(() => _directSession.StopChannel());
                }
                else
                {
                    await Task.Run(() => _session.StopChannel());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("送出停止エラー: " + ex.Message);
            }
            finally
            {
                SetStatus("接続済み（送出停止中）", Color.SteelBlue);
                _btnStart.Enabled = true;
                SetSourceControlsEnabled(true);
            }
        }

        private async void BtnDisconnect_Click(object sender, EventArgs e)
        {
            _btnDisconnect.Enabled = false;
            _btnStart.Enabled = false;
            _btnStop.Enabled = false;
            try
            {
                if (UseDirectBackend)
                {
                    await Task.Run(() => _directSession.Close());
                }
                else
                {
                    await Task.Run(() => _session.Disconnect());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("切断エラー: " + ex.Message);
            }
            finally
            {
                SetStatus("未接続", Color.DimGray);
                _btnConnect.Enabled = true;
                _rbBackendService.Enabled = true;
                _rbBackendDirect.Enabled = true;
                SetSourceControlsEnabled(true);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { _session.Disconnect(); } catch { /* best-effort cleanup on exit */ }
            try { _directSession.Close(); } catch { /* best-effort cleanup on exit */ }
            base.OnFormClosing(e);
        }
    }
}
