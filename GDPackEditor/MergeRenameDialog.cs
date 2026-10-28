using System;
using System.Drawing;
using System.Windows.Forms;

namespace GDPackEditor
{
    public class MergeRenameDialog : AntdUI.BaseForm
    {
        private readonly AntdUI.Input _newNameInput;
        private readonly AntdUI.Button _okButton;
        private readonly AntdUI.Button _cancelButton;
        private readonly string _originalName;
        private readonly Func<string, bool> _nameExistsChecker;

        public string NewName { get; private set; }
        public bool Confirmed { get; private set; }

        public MergeRenameDialog(string originalName, Func<string, bool> nameExistsChecker)
        {
            _originalName = originalName;
            _nameExistsChecker = nameExistsChecker;

            Text = LanguageManager.Instance.GetText("MergeRename.Title", "改名共存");
            Size = new Size(420, 220);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = ThemeManager.BgLayout;

            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = ThemeManager.BgLayout,
                Padding = new Padding(12, 0, 0, 0)
            };
            var titleLabel = new Label
            {
                Text = LanguageManager.Instance.GetText("MergeRename.Title", "改名共存"),
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(10, FontStyle.Bold),
                ForeColor = ThemeManager.Text,
                Padding = new Padding(0, 8, 0, 0)
            };
            headerPanel.Controls.Add(titleLabel);

            var contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeManager.BgContainer,
                Padding = new Padding(20, 10, 20, 10)
            };

            var originalLabel = new Label
            {
                Text = LanguageManager.Instance.GetText("MergeRename.OriginalName", "原名称:"),
                Dock = DockStyle.Top,
                Height = 24,
                Font = FontHelper.UiFont(9),
                ForeColor = ThemeManager.Text
            };

            var originalValueLabel = new Label
            {
                Text = _originalName,
                Dock = DockStyle.Top,
                Height = 22,
                Font = FontHelper.UiFont(8),
                ForeColor = ThemeManager.TextTertiary
            };

            var newLabel = new Label
            {
                Text = LanguageManager.Instance.GetText("MergeRename.NewName", "新名称:"),
                Dock = DockStyle.Top,
                Height = 28,
                Font = FontHelper.UiFont(9),
                ForeColor = ThemeManager.Text,
                Padding = new Padding(0, 6, 0, 0)
            };

            _newNameInput = new AntdUI.Input
            {
                Dock = DockStyle.Top,
                Height = 34,
                Text = _originalName,
                Font = FontHelper.UiFont(9)
            };
            _newNameInput.TextChanged += OnNewNameChanged;

            var hintLabel = new Label
            {
                Text = LanguageManager.Instance.GetText("MergeRename.Hint", "用于让同名资源共存"),
                Dock = DockStyle.Top,
                Height = 22,
                Font = FontHelper.UiFont(8),
                ForeColor = ThemeManager.TextTertiary,
                Padding = new Padding(0, 4, 0, 0)
            };

            var buttonPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 45,
                BackColor = ThemeManager.BgContainer,
                Padding = new Padding(0, 5, 10, 5)
            };

            _okButton = new AntdUI.Button
            {
AutoSize = true,
                Text = LanguageManager.Instance.GetText("Common.OK", "确定"),
                Type = AntdUI.TTypeMini.Primary,
                Dock = DockStyle.Right,
                
                
                Font = FontHelper.UiFont(9)
            };
            _okButton.Click += (s, e) =>
            {
                NewName = _newNameInput.Text.Trim();
                Confirmed = true;
                DialogResult = DialogResult.OK;
                Close();
            };

            _cancelButton = new AntdUI.Button
            {
AutoSize = true,
                Text = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                Type = AntdUI.TTypeMini.Default,
                Dock = DockStyle.Right,
                
                
                Font = FontHelper.UiFont(9)
            };
            _cancelButton.Click += (s, e) =>
            {
                Confirmed = false;
                DialogResult = DialogResult.Cancel;
                Close();
            };

            buttonPanel.Controls.Add(_okButton);
            buttonPanel.Controls.Add(_cancelButton);

            contentPanel.Controls.Add(buttonPanel);
            contentPanel.Controls.Add(hintLabel);
            contentPanel.Controls.Add(_newNameInput);
            contentPanel.Controls.Add(newLabel);
            contentPanel.Controls.Add(originalValueLabel);
            contentPanel.Controls.Add(originalLabel);

            Controls.Add(contentPanel);
            Controls.Add(headerPanel);

            OnNewNameChanged(null, EventArgs.Empty);
        }

        private void OnNewNameChanged(object sender, EventArgs e)
        {
            string newName = _newNameInput.Text?.Trim();
            bool exists = !string.IsNullOrEmpty(newName) && _nameExistsChecker?.Invoke(newName) == true;
            _okButton.Enabled = !exists && !string.IsNullOrEmpty(newName) && !newName.Equals(_originalName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
