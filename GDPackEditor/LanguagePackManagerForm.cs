using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace GDPackEditor
{
    public class LanguagePackManagerForm : AntdUI.BaseForm
    {
        private AntdUI.Table _table;
        private AntdUI.Button _btnSave;
        private AntdUI.Button _btnCancel;
        private AntdUI.Button _btnUp;
        private AntdUI.Button _btnDown;
        private List<LanguagePackEntry> _packs;

        public LanguagePackManagerForm()
        {
            Text = LanguageManager.Instance.GetText("Editor.LangPackMgr", "语言包管理");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            MinimumSize = new Size(580, 420);
            ClientSize = new Size(580, 460);

            _packs = SettingsManager.ImportedPacks.Select(p => new LanguagePackEntry
            {
                Name = p.Name,
                Note = p.Note,
                FilePath = p.FilePath
            }).ToList();

            var layout = new AntdUI.StackPanel
            {
                Dock = DockStyle.Fill,
                Vertical = true,
                Padding = new Padding(10),
                Gap = 8
            };

            // Row 0: hint text
            var hintLabel = new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("Editor.LangPackMultiHint", "可多选，从上到下依次生效"),
                ForeColor = ThemeManager.Modified,
                AutoSize = true,
                Font = new Font(FontHelper.UiFont(9f), FontStyle.Bold)
            };
            layout.Add(hintLabel);

            // Row 1: tool bar
            var toolPanel = new AntdUI.FlowPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true
            };
            var btnAdd = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Editor.LangPackAdd", "添加"),
                };
            btnAdd.Click += OnAddClick;
            var btnDelete = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Common.Delete", "删除"),
                };
            btnDelete.Click += OnDeleteClick;
            _btnUp = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = "↑ " + LanguageManager.Instance.GetText("Common.Up", "上移"),
                };
            _btnUp.Click += OnUpClick;
            _btnDown = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = "↓ " + LanguageManager.Instance.GetText("Common.Down", "下移"),
                };
            _btnDown.Click += OnDownClick;
            toolPanel.Controls.Add(btnAdd);
            toolPanel.Controls.Add(btnDelete);
            toolPanel.Controls.Add(_btnUp);
            toolPanel.Controls.Add(_btnDown);
            layout.Add(toolPanel);

            // Row 2: table with checkboxes
            _table = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                MultipleRows = false,
                VisibleHeader = true,
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill,
                Bordered = false,
                LostFocusClearSelection = false
            };
            _table.Columns.Add(new AntdUI.Column("IsChecked",
                LanguageManager.Instance.GetText("Editor.LangPackActive", "使用")) { Width = "50" });
            _table.Columns.Add(new AntdUI.Column("DisplayNote",
                LanguageManager.Instance.GetText("Editor.LangPackNote", "备注")) { Width = "140" });
            _table.Columns.Add(new AntdUI.Column("FilePath",
                LanguageManager.Instance.GetText("Common.Path", "路径")));
            _table.CellClick += OnTableCellClick;

            RestoreCheckStates();
            RefreshTable();
            layout.Add(_table);

            // Row 3: bottom buttons
            var bottomPanel = new AntdUI.FlowPanel
            {
                Dock = DockStyle.Fill
            };
            _btnCancel = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                
                DialogResult = DialogResult.Cancel
            };
            _btnSave = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Common.Save", "保存"),
                
                DialogResult = DialogResult.OK
            };
            bottomPanel.Controls.Add(_btnSave);
            bottomPanel.Controls.Add(_btnCancel);
            layout.Add(bottomPanel);

            AcceptButton = _btnSave;
            CancelButton = _btnCancel;

            Controls.Add(layout);
        }

        private void RestoreCheckStates()
        {
            var activeKeys = SettingsManager.ActivePackKeys;
            for (int i = 0; i < _packs.Count; i++)
                _packs[i].IsChecked = activeKeys.Contains(i);
        }

        private void RefreshTable()
        {
            _table.DataSource = null;
            _table.DataSource = _packs;
        }

        private void OnTableCellClick(object sender, AntdUI.TableClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.RowIndex < _packs.Count)
            {
                _table.SelectedIndex = e.RowIndex + 1;
                AntdUI.ContextMenuStrip.open(_table, item =>
                {
                    if (item.ID == "edit") OnEditClick(null, null);
                    else if (item.ID == "delete") OnDeleteClick(null, null);
                }, new AntdUI.IContextMenuStripItem[]
                {
                    new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("Common.Edit", "编辑"))
                        { ID = "edit" },
                    new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("Common.Delete", "删除"))
                        { ID = "delete" }
                });
            }
        }

        private void OnAddClick(object sender, EventArgs e)
        {
            using var editForm = new LanguagePackEditForm();
            if (editForm.ShowDialog(this) == DialogResult.OK)
            {
                string path = editForm.ArcPath;
                if (string.IsNullOrEmpty(path)) return;
                if (!System.IO.File.Exists(path))
                {
                    MessageBox.Show(this,
                        LanguageManager.Instance.GetText("Editor.PackFileNotFound", "文件不存在"));
                    return;
                }
                if (_packs.Any(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(this,
                        LanguageManager.Instance.GetText("Editor.PackAlreadyImported", "该语言包已导入"));
                    return;
                }
                string name = System.IO.Path.GetFileName(path);
                _packs.Add(new LanguagePackEntry
                {
                    Name = name,
                    Note = editForm.PackNote,
                    FilePath = path,
                    IsChecked = true
                });
                RefreshTable();
            }
        }

        private LanguagePackEntry GetSelectedEntry()
        {
            if (_table.SelectedIndex > 0 && _table.SelectedIndex <= _packs.Count)
                return _packs[_table.SelectedIndex - 1];
            return null;
        }

        private void OnEditClick(object sender, EventArgs e)
        {
            var entry = GetSelectedEntry();
            if (entry == null) return;

            using var editForm = new LanguagePackEditForm(entry.FilePath, entry.Note);
            if (editForm.ShowDialog(this) == DialogResult.OK)
            {
                if (!string.IsNullOrEmpty(editForm.ArcPath) &&
                    !string.Equals(entry.FilePath, editForm.ArcPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (_packs.Any(p => string.Equals(p.FilePath, editForm.ArcPath, StringComparison.OrdinalIgnoreCase)
                        && p != entry))
                    {
                        MessageBox.Show(this,
                            LanguageManager.Instance.GetText("Editor.PackAlreadyImported", "该语言包已导入"));
                        return;
                    }
                }
                entry.Name = System.IO.Path.GetFileName(editForm.ArcPath);
                entry.Note = editForm.PackNote;
                entry.FilePath = editForm.ArcPath;
                RefreshTable();
            }
        }

        private void OnDeleteClick(object sender, EventArgs e)
        {
            var entry = GetSelectedEntry();
            if (entry == null) return;
            if (MessageBox.Show(this,
                string.Format(LanguageManager.Instance.GetText("Editor.LangPackDeleteConfirm",
                    "确定删除语言包 \"{0}\"？"),
                    !string.IsNullOrEmpty(entry.Note) ? entry.Note : entry.Name)
                + "\n" + LanguageManager.Instance.GetText("Editor.LangPackDeleteHint",
                    "仅删除记录，不删除实体文件，请放心！"),
                LanguageManager.Instance.GetText("Common.Confirm", "确认"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _packs.Remove(entry);
            RefreshTable();
        }

        private void OnUpClick(object sender, EventArgs e)
        {
            if (_table.SelectedIndex <= 1) return;
            int idx = _table.SelectedIndex - 1;
            var pack = _packs[idx];
            _packs.RemoveAt(idx);
            _packs.Insert(idx - 1, pack);
            RefreshTable();
            _table.SelectedIndex = idx; // selection moved up
        }

        private void OnDownClick(object sender, EventArgs e)
        {
            if (_table.SelectedIndex <= 0 || _table.SelectedIndex >= _packs.Count) return;
            int idx = _table.SelectedIndex - 1;
            if (idx >= _packs.Count - 1) return;
            var pack = _packs[idx];
            _packs.RemoveAt(idx);
            _packs.Insert(idx + 1, pack);
            RefreshTable();
            _table.SelectedIndex = idx + 2;
        }

        public List<LanguagePackEntry> GetPackList()
        {
            return _packs.Select(p => new LanguagePackEntry
            {
                Name = p.Name,
                Note = p.Note,
                FilePath = p.FilePath
            }).ToList();
        }

        public List<int> GetActiveKeys()
        {
            var keys = new List<int>();
            for (int i = 0; i < _packs.Count; i++)
            {
                if (_packs[i].IsChecked)
                    keys.Add(i);
            }
            return keys;
        }
    }
}
