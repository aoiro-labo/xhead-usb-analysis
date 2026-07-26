using System;
using System.Drawing;
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
        private readonly ToolTip _toolTip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300, ReshowDelay = 100 };

        private Button _btnConnect;
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnDisconnect;
        private CheckBox _chkAttachCapture;
        private Label _lblStatus;
        private NumericUpDown _numFrequency;
        private ComboBox _cmbConstellation;
        private NumericUpDown _numBandwidth;
        private ComboBox _cmbFFT;
        private ComboBox _cmbCodeRate;
        private ComboBox _cmbGuardInterval;
        private ComboBox _cmbTimeInterleavce;
        private NumericUpDown _numLevel;
        private NumericUpDown _numPAGain;
        private NumericUpDown _numDACGain;
        private TextBox _txtLog;

        public MainForm()
        {
            Text = "XHeadSender GUI -- mnservice.exe経由の直接送出ツール";
            Width = 700;
            Height = 780;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(640, 560);
            Font = new Font("Yu Gothic UI", 9f);

            BuildControls();

            Console.SetOut(new TextBoxWriter(_txtLog));
            Console.WriteLine("XHeadSender GUI 起動。");
            Console.WriteLine("事前に XHEAD-STUDIO (xhead_studio.exe) を起動してサービスを立ち上げておくこと。");
            Console.WriteLine("まず「接続」を押してください。");
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

            var capPanel = new Panel { Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 2, 8, 0) };
            _chkAttachCapture = new CheckBox
            {
                Text = "デスクトップキャプチャを送出する（実映像を乗せる）",
                AutoSize = true,
                Dock = DockStyle.Left,
            };
            _toolTip.SetToolTip(_chkAttachCapture,
                "オフの場合は変調器のRF出力のみ(ChannelStartだけ)。オンにすると実際にデスクトップ画面を" +
                "キャプチャして送出内容として乗せる(tools/custom_sender の RunFullPipelineTest と同じ経路、動作実証済み)。");
            capPanel.Controls.Add(_chkAttachCapture);

            var logLabel = new Label { Dock = DockStyle.Top, Height = 22, Text = "ログ:", Padding = new Padding(8, 6, 0, 0) };

            var modGroup = new GroupBox
            {
                Text = "変調パラメータ",
                Dock = DockStyle.Top,
                Padding = new Padding(10, 4, 6, 10),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            var modLayout = NewParamTable();
            _numFrequency = AddNumeric(modLayout, "周波数 (kHz)", 0, 1000000, 473000,
                "送出する中心周波数(kHz)。既定の473000kHzはUHF473MHz(ISDB-Tの標準チャンネルの1つ)。");
            _cmbConstellation = AddCombo(modLayout, "変調方式", new[]
            {
                new ComboItem(0, "DQPSK"),
                new ComboItem(1, "QPSK (既定)"),
                new ComboItem(2, "16QAM"),
                new ComboItem(3, "64QAM"),
            }, 1, "ISDB-Tのキャリア変調方式。値が大きいほど高速だが電波状況に弱くなる。");
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
            }, 2, "時間インターリーブの深さ。深いほどバースト誤りに強いが遅延が増える。");
            modGroup.Controls.Add(modLayout);

            var powerGroup = new GroupBox
            {
                Text = "RF電力設定",
                Dock = DockStyle.Top,
                Padding = new Padding(10, 4, 6, 10),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            var powerLayout = NewParamTable();
            _numLevel = AddNumeric(powerLayout, "Level (80〜100)", 80, 100, 90,
                "周波数ごとのPA/DACゲイン表を引く際の添字(80〜100)。Level単体では出力に変化なし --"
                + "PAGain/DACGainと必ず一緒に送ること。");
            _numPAGain = AddNumeric(powerLayout, "PAGain", -128, 127, 2,
                "パワーアンプの生ゲイン値(int8)。実機ログでは物理的な効果は未確認 -- "
                + "2026-07-26の解析でmCalibrationという較正テーブルと突き合わせて使われている可能性が判明。");
            _numDACGain = AddNumeric(powerLayout, "DACGain", -128, 127, -10,
                "DACの生ゲイン値(int8)。RF出力電力に直接反映されることを実機ログとRTL-SDRで確認済み。");
            powerGroup.Controls.Add(powerLayout);

            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 8.5f),
            };

            // 実際の画面上での見た目(上から): 警告 -> 状態 -> ボタン -> キャプチャ添付チェック
            // -> 変調パラメータ -> RF電力設定 -> "ログ:" -> ログ欄。注意: 同じDockStyle.Top
            // 同士では、後からControls.Addした方が画面上は上に来る(直感と逆)。そのためこの
            // 一括Addは視覚順とは逆順で書く。
            Controls.Add(_txtLog);
            Controls.Add(logLabel);
            Controls.Add(powerGroup);
            Controls.Add(modGroup);
            Controls.Add(capPanel);
            Controls.Add(btnPanel);
            Controls.Add(_lblStatus);
            Controls.Add(warnLabel);
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

        private NumericUpDown AddNumeric(TableLayoutPanel layout, string label, decimal min, decimal max, decimal value, string tooltip)
        {
            int row = layout.RowStyles.Count;
            layout.RowCount = row + 1;
            var lbl = new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            layout.Controls.Add(lbl, 0, row);
            var num = new NumericUpDown { Minimum = min, Maximum = max, Value = value, Width = 110, Anchor = AnchorStyles.Left };
            layout.Controls.Add(num, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _toolTip.SetToolTip(lbl, tooltip);
            _toolTip.SetToolTip(num, tooltip);
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
            _toolTip.SetToolTip(lbl, tooltip);
            _toolTip.SetToolTip(cmb, tooltip);
            return cmb;
        }

        private static int SelectedValue(ComboBox cmb) => ((ComboItem)cmb.SelectedItem).Value;

        private ModulationConfig ReadConfigFromForm()
        {
            return new ModulationConfig
            {
                Frequency = (uint)_numFrequency.Value,
                Constellation = SelectedValue(_cmbConstellation),
                Bandwidth = (uint)_numBandwidth.Value,
                FFT = SelectedValue(_cmbFFT),
                CodeRate = SelectedValue(_cmbCodeRate),
                GuardInterval = SelectedValue(_cmbGuardInterval),
                TimeInterleavce = SelectedValue(_cmbTimeInterleavce),
                Level = (uint)_numLevel.Value,
                PAGain = (int)_numPAGain.Value,
                DACGain = (int)_numDACGain.Value,
            };
        }

        private void SetStatus(string text, Color color)
        {
            _lblStatus.Text = "状態: " + text;
            _lblStatus.ForeColor = color;
        }

        private async void BtnConnect_Click(object sender, EventArgs e)
        {
            _btnConnect.Enabled = false;
            try
            {
                await Task.Run(() => _session.Connect());
                SetStatus("接続済み（送出停止中）", Color.SteelBlue);
                _btnStart.Enabled = true;
                _btnDisconnect.Enabled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("接続失敗: " + ex.Message);
                SetStatus("未接続（接続失敗）", Color.Firebrick);
                _btnConnect.Enabled = true;
            }
        }

        private async void BtnStart_Click(object sender, EventArgs e)
        {
            _btnStart.Enabled = false;
            _chkAttachCapture.Enabled = false;
            var cfg = ReadConfigFromForm();
            bool attachCapture = _chkAttachCapture.Checked;
            try
            {
                await Task.Run(() =>
                {
                    _session.StartChannel(cfg);
                    if (attachCapture)
                    {
                        _session.StartCaptureSource();
                    }
                });
                SetStatus(attachCapture ? "送出中（デスクトップキャプチャ添付）" : "送出中（RFのみ）", Color.SeaGreen);
                _btnStop.Enabled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("送出開始失敗: " + ex.Message);
                // ChannelStartだけ成功してCaptureSource側で失敗した場合、RFは出続けているので
                // 「送出失敗」ではなく実情に合わせた表示にする。
                if (_session.ChannelStarted)
                {
                    SetStatus("送出中（キャプチャ添付失敗、RFのみ）", Color.DarkOrange);
                    _btnStop.Enabled = true;
                }
                else
                {
                    SetStatus("接続済み（送出失敗）", Color.Firebrick);
                    _btnStart.Enabled = true;
                    _chkAttachCapture.Enabled = true;
                }
            }
        }

        private async void BtnStop_Click(object sender, EventArgs e)
        {
            _btnStop.Enabled = false;
            try
            {
                await Task.Run(() => _session.StopChannel());
            }
            catch (Exception ex)
            {
                Console.WriteLine("送出停止エラー: " + ex.Message);
            }
            finally
            {
                SetStatus("接続済み（送出停止中）", Color.SteelBlue);
                _btnStart.Enabled = true;
                _chkAttachCapture.Enabled = true;
            }
        }

        private async void BtnDisconnect_Click(object sender, EventArgs e)
        {
            _btnDisconnect.Enabled = false;
            _btnStart.Enabled = false;
            _btnStop.Enabled = false;
            try
            {
                await Task.Run(() => _session.Disconnect());
            }
            catch (Exception ex)
            {
                Console.WriteLine("切断エラー: " + ex.Message);
            }
            finally
            {
                SetStatus("未接続", Color.DimGray);
                _btnConnect.Enabled = true;
                _chkAttachCapture.Enabled = true;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { _session.Disconnect(); } catch { /* best-effort cleanup on exit */ }
            base.OnFormClosing(e);
        }
    }
}
