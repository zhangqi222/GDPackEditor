using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace GDPackEditor
{
    public class NewFileDialog : AntdUI.BaseForm
    {
        private AntdUI.Input _fileNameTextBox;
        private AntdUI.Select _templateCombo;
        private AntdUI.Button _okButton;
        private AntdUI.Button _cancelButton;
        private List<string> _allTemplateKeys;
        private bool _suppressFilter;
        private bool _navigating;

        public string FileName
        {
            get => _fileNameTextBox.Text.Trim();
            set => _fileNameTextBox.Text = value ?? "";
        }

        public string SelectedTemplate
        {
            get
            {
                string text = _templateCombo.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(text)) return null;
                var selected = _templateCombo.SelectedValue as string;
                if (!string.IsNullOrEmpty(selected)) return selected;
                return null;
            }
        }

        public NewFileDialog(string title, string prompt, IEnumerable<string> templateKeys)
        {
            Text = title;
            Size = new Size(520, templateKeys != null ? 220 : 140);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = true;

            _allTemplateKeys = templateKeys != null
                ? templateKeys.OrderBy(k => k).ToList()
                : new List<string>();

            var layout = new AntdUI.StackPanel
            {
                Dock = DockStyle.Fill,
                Vertical = true,
                Padding = new Padding(12),
                Gap = 8
            };

            layout.Add(new AntdUI.Label
            {
                Text = prompt,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            });

            _fileNameTextBox = new AntdUI.Input
            {
                Dock = DockStyle.Fill
            };
            layout.Add(_fileNameTextBox);

            if (templateKeys != null)
            {
                layout.Add(new AntdUI.Label
                {
                    Text = LanguageManager.Instance.GetText("Editor.NewFileTemplate", "模板（可选，仅ARZ中DBR文件使用）："),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Dock = DockStyle.Fill
                });

                _templateCombo = new AntdUI.Select
                {
                    Dock = DockStyle.Fill,
                    DropDownArrow = true,
                    MaxCount = 20,
                    List = true
                };
                RestoreAllItems();
                _templateCombo.SelectedIndex = 0;
                _templateCombo.TextChanged += OnTemplateTextChanged;
                _templateCombo.SelectedIndexChanged += OnTemplateSelectIndexChanged;
                _templateCombo.KeyDown += OnTemplateKeyDown;
                _templateCombo.KeyUp += OnTemplateKeyUp;
                layout.Add(_templateCombo);
            }

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

            _fileNameTextBox.TextChanged += (s, e) =>
            {
                _okButton.Enabled = !string.IsNullOrWhiteSpace(_fileNameTextBox.Text);
            };

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private void RestoreAllItems()
        {
            _suppressFilter = true;
            string prevText = _templateCombo.Text;
            _templateCombo.Items.Clear();
            _templateCombo.Items.Add(new AntdUI.SelectItem("", ""));
            foreach (var key in _allTemplateKeys)
                _templateCombo.Items.Add(new AntdUI.SelectItem(key, key));
            _templateCombo.Text = prevText;
            _suppressFilter = false;
        }

        private void OnTemplateTextChanged(object sender, EventArgs e)
        {
            if (_suppressFilter) return;
            if (_navigating) return;
            ApplyFilter();
        }

        private void OnTemplateSelectIndexChanged(object sender, EventArgs e)
        {
            string selectedText = _templateCombo.SelectedValue as string;
            if (!string.IsNullOrEmpty(selectedText))
            {
                _suppressFilter = true;
                _templateCombo.Text = selectedText;
                _suppressFilter = false;
            }
        }

        private void OnTemplateKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down ||
                e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown)
            {
                _navigating = true;
            }
        }

        private void OnTemplateKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down ||
                e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown)
            {
                _navigating = false;
            }
        }

        private void ApplyFilter()
        {
            string filter = _templateCombo.Text?.Trim() ?? "";
            _suppressFilter = true;

            _templateCombo.Items.Clear();
            _templateCombo.Items.Add(new AntdUI.SelectItem("", ""));

            if (string.IsNullOrEmpty(filter))
            {
                foreach (var key in _allTemplateKeys)
                    _templateCombo.Items.Add(new AntdUI.SelectItem(key, key));
                _templateCombo.Text = "";
            }
            else
            {
                var matches = _allTemplateKeys
                    .Where(k => k.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                foreach (var key in matches)
                    _templateCombo.Items.Add(new AntdUI.SelectItem(key, key));

                _templateCombo.Text = filter;

                if (matches.Count > 0)
                {
                    _templateCombo.ExpandDrop = true;
                }
            }

            _suppressFilter = false;
        }
    }
}
