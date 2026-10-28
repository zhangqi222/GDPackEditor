using System;
using System.Drawing;
using System.Windows.Forms;

namespace GDPackEditor
{
    public class NewFolderDialog : AntdUI.BaseForm
    {
        private AntdUI.Input _folderNameTextBox;
        private AntdUI.Button _okButton;
        private AntdUI.Button _cancelButton;

        public string FolderName
        {
            get => _folderNameTextBox.Text.Trim();
            set => _folderNameTextBox.Text = value ?? "";
        }

        public NewFolderDialog(string title, string prompt, string description = null)
        {
            Text = title;
            Size = new Size(460, description != null ? 200 : 160);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = true;

            var layout = new AntdUI.StackPanel
            {
                Dock = DockStyle.Fill,
                Vertical = true,
                Padding = new Padding(12),
                Gap = 8
            };

            if (description != null)
            {
                layout.Add(new AntdUI.Label
                {
                    Text = description,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Dock = DockStyle.Fill,
                    ForeColor = ThemeManager.Modified,
                    Font = FontHelper.UiFont(9)
                });
            }

            layout.Add(new AntdUI.Label
            {
                Text = prompt,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            });

            _folderNameTextBox = new AntdUI.Input
            {
                Dock = DockStyle.Fill
            };
            layout.Add(_folderNameTextBox);

            var buttonPanel = new AntdUI.FlowPanel
            {
                Dock = DockStyle.Fill
            };

            _cancelButton = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                DialogResult = DialogResult.Cancel,
                
                };

            _okButton = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Common.OK", "确定"),
                DialogResult = DialogResult.OK,
                
                
                Enabled = false
            };

            buttonPanel.Controls.Add(_okButton);
            buttonPanel.Controls.Add(_cancelButton);
            layout.Add(buttonPanel);

            Controls.Add(layout);

            _folderNameTextBox.TextChanged += (s, e) =>
            {
                _okButton.Enabled = !string.IsNullOrWhiteSpace(_folderNameTextBox.Text);
            };

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }
    }
}
