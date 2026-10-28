using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace GDPackEditor
{
    public class SaveProgressDialog : AntdUI.BaseForm
    {
        private LogView _logView;
        private AntdUI.Progress _progressBar;
        private AntdUI.Button _actionButton;
        private CancellationTokenSource _cts;
        private bool _operationDone;
        private Action _onCloseAction;

        public CancellationTokenSource Cts => _cts;

        public SaveProgressDialog(string title)
        {
            Text = title;
            Size = new Size(640, 420);
            MinimumSize = new Size(400, 250);
            MinimizeBox = true;
            MaximizeBox = true;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            ControlBox = true;

            _cts = new CancellationTokenSource();

            // 日志区用公共组件：自带滚动条、过滤/清空/导出工具条与警告黄/错误红行级着色
            _logView = new LogView
            {
                Dock = DockStyle.Fill
            };

            _progressBar = new AntdUI.Progress
            {
                Dock = DockStyle.Bottom,
                Height = 12,
                Value = 0
            };

            _actionButton = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                Dock = DockStyle.Bottom,
                Enabled = true,
                
                Padding = new Padding(10, 0, 10, 0)
            };
            _actionButton.Click += OnActionButtonClick;

            Controls.Add(_logView);
            Controls.Add(_progressBar);
            Controls.Add(_actionButton);

            Shown += OnShown;
        }

        private void OnShown(object sender, EventArgs e)
        {
            _logView.Clear();
            if (Owner != null)
                Location = new Point(
                    Owner.Location.X + (Owner.Width - Width) / 2,
                    Owner.Location.Y + (Owner.Height - Height) / 2);
        }

        private void OnActionButtonClick(object sender, EventArgs e)
        {
            if (_operationDone)
            {
                Close();
                return;
            }
            _cts.Cancel();
            _actionButton.Enabled = false;
            _actionButton.Text = LanguageManager.Instance.GetText("Common.Cancelling", "正在取消...");
        }

        public AntdUI.Progress ProgressBar => _progressBar;
        public LogView LogView => _logView;

        public void OperationDone(Action onCloseAction = null, string buttonText = null)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => OperationDone(onCloseAction, buttonText)));
                return;
            }
            _operationDone = true;
            _onCloseAction = onCloseAction;
            _progressBar.Value = 100;
            _actionButton.Enabled = true;
            _actionButton.Text = buttonText ?? (onCloseAction != null
                ? LanguageManager.Instance.GetText("Editor.CloseAndExit", "确定并关闭编辑器")
                : LanguageManager.Instance.GetText("Common.Close", "关闭"));
            _actionButton.Focus();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_operationDone && !_cts.IsCancellationRequested)
            {
                e.Cancel = true;
                return;
            }
            var action = _onCloseAction;
            _onCloseAction = null;
            action?.Invoke();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _cts?.Dispose();
            base.Dispose(disposing);
        }
    }
}
