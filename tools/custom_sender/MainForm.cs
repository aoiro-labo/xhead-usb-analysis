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
        private readonly GuiSession _session = new GuiSession();

        private Button _btnConnect;
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnDisconnect;
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
            Width = 620;
            Height = 700;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(560, 500);

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
                Height = 48,
                Text = "注意: 「送出開始」を押すと実機のRF出力を実際に駆動します。周波数はプロトコル上"
                     + "0-1,000,000kHzまで受理されますが、宣言された範囲より実機が狭い場合があります"
                     + "(範囲外の値でmnservice.exeがクラッシュした実績あり)。",
                ForeColor = Color.DarkRed,
                Padding = new Padding(8),
            };
            Controls.Add(warnLabel);

            var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 4, 8, 4) };
            _btnConnect = new Button { Text = "接続", Width = 90 };
            _btnStart = new Button { Text = "送出開始", Width = 90, Enabled = false };
            _btnStop = new Button { Text = "送出停止", Width = 90, Enabled = false };
            _btnDisconnect = new Button { Text = "切断", Width = 90, Enabled = false };
            _btnConnect.Click += BtnConnect_Click;
            _btnStart.Click += BtnStart_Click;
            _btnStop.Click += BtnStop_Click;
            _btnDisconnect.Click += BtnDisconnect_Click;
            btnPanel.Controls.Add(_btnConnect);
            btnPanel.Controls.Add(_btnStart);
            btnPanel.Controls.Add(_btnStop);
            btnPanel.Controls.Add(_btnDisconnect);
            Controls.Add(btnPanel);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                Padding = new Padding(8),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));

            _numFrequency = AddNumeric(layout, "周波数 (kHz)", 0, 1000000, 473000);
            _cmbConstellation = AddCombo(layout, "Constellation", new[] { "0: DQPSK", "1: QPSK", "2: QAM16", "3: QAM64" }, 1);
            _numBandwidth = AddNumeric(layout, "Bandwidth (MHz)", 0, 10, 6);
            _cmbFFT = AddCombo(layout, "FFT", new[] { "0: 2k", "1: 8k", "2: 4k" }, 1);
            _cmbCodeRate = AddCombo(layout, "CodeRate", new[] { "0: 1/2", "1: 2/3", "2: 3/4", "3: 5/6", "4: 7/8" }, 3);
            _cmbGuardInterval = AddCombo(layout, "GuardInterval", new[] { "0: 1/32", "1: 1/16", "2: 1/8", "3: 1/4" }, 1);
            _cmbTimeInterleavce = AddCombo(layout, "TimeInterleavce", new[] { "1: Mode1", "2: Mode2", "3: Mode3" }, 2);
            _numLevel = AddNumeric(layout, "RF Level (80-100)", 80, 100, 90);
            _numPAGain = AddNumeric(layout, "PAGain", -128, 127, 2);
            _numDACGain = AddNumeric(layout, "DACGain", -128, 127, -10);
            Controls.Add(layout);

            var logLabel = new Label { Dock = DockStyle.Top, Height = 20, Text = "ログ:", Padding = new Padding(8, 4, 0, 0) };
            Controls.Add(logLabel);

            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 8.5f),
            };
            Controls.Add(_txtLog);

            // Dock=Fill の _txtLog が残り領域を占めるよう、他は全て Dock=Top で先に積む
            // (WinFormsのDockはZ-order優先のため、Fillは最後にAddすれば正しく余白を埋める)。
        }

        private NumericUpDown AddNumeric(TableLayoutPanel layout, string label, decimal min, decimal max, decimal value)
        {
            int row = layout.RowStyles.Count;
            layout.RowCount = row + 1;
            layout.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, row);
            var num = new NumericUpDown { Minimum = min, Maximum = max, Value = value, Width = 160 };
            layout.Controls.Add(num, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return num;
        }

        private ComboBox AddCombo(TableLayoutPanel layout, string label, string[] items, int selectedIndex)
        {
            int row = layout.RowStyles.Count;
            layout.RowCount = row + 1;
            layout.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, row);
            var cmb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            cmb.Items.AddRange(items);
            cmb.SelectedIndex = selectedIndex;
            layout.Controls.Add(cmb, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return cmb;
        }

        private static int ParseLeadingInt(string comboText)
        {
            int idx = comboText.IndexOf(':');
            return int.Parse(idx >= 0 ? comboText.Substring(0, idx) : comboText);
        }

        private ModulationConfig ReadConfigFromForm()
        {
            return new ModulationConfig
            {
                Frequency = (uint)_numFrequency.Value,
                Constellation = ParseLeadingInt(_cmbConstellation.Text),
                Bandwidth = (uint)_numBandwidth.Value,
                FFT = ParseLeadingInt(_cmbFFT.Text),
                CodeRate = ParseLeadingInt(_cmbCodeRate.Text),
                GuardInterval = ParseLeadingInt(_cmbGuardInterval.Text),
                TimeInterleavce = ParseLeadingInt(_cmbTimeInterleavce.Text),
                Level = (uint)_numLevel.Value,
                PAGain = (int)_numPAGain.Value,
                DACGain = (int)_numDACGain.Value,
            };
        }

        private async void BtnConnect_Click(object sender, EventArgs e)
        {
            _btnConnect.Enabled = false;
            try
            {
                await Task.Run(() => _session.Connect());
                _btnStart.Enabled = true;
                _btnDisconnect.Enabled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("接続失敗: " + ex.Message);
                _btnConnect.Enabled = true;
            }
        }

        private async void BtnStart_Click(object sender, EventArgs e)
        {
            _btnStart.Enabled = false;
            var cfg = ReadConfigFromForm();
            try
            {
                await Task.Run(() => _session.StartChannel(cfg));
                _btnStop.Enabled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("送出開始失敗: " + ex.Message);
                _btnStart.Enabled = true;
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
                _btnStart.Enabled = true;
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
                _btnConnect.Enabled = true;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { _session.Disconnect(); } catch { /* best-effort cleanup on exit */ }
            base.OnFormClosing(e);
        }
    }
}
