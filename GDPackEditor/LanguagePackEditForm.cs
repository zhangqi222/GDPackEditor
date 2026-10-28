using System;
using System.IO;
using System.Windows.Forms;

namespace GDPackEditor
{
    public class LanguagePackEditForm : AntdUI.BaseForm
    {
        private AntdUI.Input _txtPath;
        private AntdUI.Input _txtNote;
        private AntdUI.Button _btnBrowse;
        private AntdUI.Button _btnOk;
        private AntdUI.Button _btnCancel;

        public string ArcPath => _txtPath.Text.Trim();
        public string PackNote => _txtNote.Text.Trim();

        public LanguagePackEditForm(string existingPath = null, string existingNote = null)
        {
            Text = LanguageManager.Instance.GetText("Editor.LangPackEdit", "语言包");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new System.Drawing.Size(480, 140);

            var layout = new AntdUI.StackPanel
            {
                Dock = DockStyle.Fill,
                Vertical = true,
                Padding = new Padding(10),
                Gap = 8
            };

            // Row 0: path
            var row0 = new AntdUI.StackPanel
            {
                Dock = DockStyle.Fill,
                Vertical = false,
                Gap = 4
            };
            row0.Add(new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("Editor.LangPackArc", "语言 ARC 包："),
                AutoSize = true
            });
            _txtPath = new AntdUI.Input
            {
                Text = existingPath ?? "",
                Dock = DockStyle.Fill
            };
            _txtPath.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
            };
            _txtPath.DragDrop += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                    e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                    _txtPath.Text = files[0];
            };
            row0.Add(_txtPath);
            _btnBrowse = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Common.Browse", "浏览"),
                
                };
            _btnBrowse.Click += (s, e) =>
            {
                using var dlg = new OpenFileDialog
                {
                    Title = LanguageManager.Instance.GetText("Editor.SelectLangPack", "选择语言包 ARC 文件"),
                    Filter = "ARC 文件|*.arc",
                    Multiselect = false
                };
                if (dlg.ShowDialog() == DialogResult.OK)
                    _txtPath.Text = dlg.FileName;
            };
            row0.Add(_btnBrowse);
            layout.Add(row0);

            // Row 1: note
            var row1 = new AntdUI.StackPanel
            {
                Dock = DockStyle.Fill,
                Vertical = false,
                Gap = 4
            };
            row1.Add(new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("Editor.LangPackNote", "语言包备注："),
                AutoSize = true
            });
            _txtNote = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Text = existingNote ?? ""
            };
            row1.Add(_txtNote);
            layout.Add(row1);

            // Row 2: buttons
            var btnPanel = new AntdUI.FlowPanel
            {
                Dock = DockStyle.Right
            };
            _btnCancel = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                
                
                DialogResult = DialogResult.Cancel
            };
            _btnOk = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Common.Confirm", "确定"),
                
                
                DialogResult = DialogResult.OK
            };
            btnPanel.Controls.Add(_btnOk);
            btnPanel.Controls.Add(_btnCancel);
            layout.Add(btnPanel);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            Controls.Add(layout);
        }
    }
}
