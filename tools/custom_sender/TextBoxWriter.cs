using System;
using System.Text;
using System.Windows.Forms;

namespace XHeadSender
{
    /// <summary>
    /// 既存コードベースの大量の Console.WriteLine をそのまま再利用するため、
    /// Console.SetOut でこれに差し替えてログをGUIのTextBoxへ転送する。呼び出しスレッドは
    /// gRPC呼び出し用のバックグラウンドスレッドのことがあるため、Invoke で必ずUIスレッドへ渡す。
    /// </summary>
    internal sealed class TextBoxWriter : System.IO.TextWriter
    {
        private readonly TextBox _target;

        public TextBoxWriter(TextBox target)
        {
            _target = target;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Append(value.ToString());

        public override void Write(string value) => Append(value);

        public override void WriteLine(string value) => Append(value + Environment.NewLine);

        private void Append(string text)
        {
            if (text == null || _target.IsDisposed) return;
            if (_target.InvokeRequired)
            {
                try { _target.BeginInvoke(new Action(() => AppendOnUiThread(text))); }
                catch (ObjectDisposedException) { /* form closing race, ignore */ }
                catch (InvalidOperationException) { /* handle not created yet, ignore */ }
            }
            else
            {
                AppendOnUiThread(text);
            }
        }

        private void AppendOnUiThread(string text)
        {
            if (_target.IsDisposed) return;
            _target.AppendText(text);
        }
    }
}
