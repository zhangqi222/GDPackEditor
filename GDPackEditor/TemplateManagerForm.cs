using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace GDPackEditor
{
    public class TemplateManagerForm : AntdUI.BaseForm
    {
        private AntdUI.Table _table;
        private List<TemplateEntry> _entries;
        private readonly AntdUI.BaseForm _owner;
        private AntdUI.Panel _root;

        /// <summary>
        /// 内容面板（供设置窗「模板设置」页签直接承载）
        /// </summary>
        public Control Content => _root;

        public TemplateManagerForm(AntdUI.BaseForm owner = null)
        {
            _owner = owner;
            Text = LanguageManager.Instance.GetText("TemplateManager.Title", "模板管理器");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            MinimumSize = new Size(720, 480);
            ClientSize = new Size(800, 540);

            // 复制当前配置中的模板条目到本地列表
            _entries = SettingsManager.TemplateEntries.Select(e => new TemplateEntry
            {
                Id = e.Id,
                Note = e.Note,
                Path = e.Path,
                Type = e.Type,
                IsChecked = e.IsChecked,
                BuiltInVersion = e.BuiltInVersion
            }).ToList();

            // ═══ 使用 AntdUI.Panel + Dock 布局（与设置窗其它页签一致的内容区左右缩进）═══
            _root = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent,
                Padding = new Padding(25, 10, 25, 10)
            };

            // ── 顶部工具行（AntdUI 原生布局）──
            var toolPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Back = Color.Transparent
            };
            var hintPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 74,
                Back = Color.Transparent
            };
            var hintLabel = new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("TemplateManager.MultiHint", "可多选，从上到下依次生效"),
                ForeColor = ThemeManager.Modified,
                AutoSize = true,
                Dock = DockStyle.Top,
                Font = new Font(FontHelper.UiFont(9f), FontStyle.Bold),
                Padding = new Padding(10, 2, 0, 0)
            };
            var folderHintLabel = new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("TemplateManager.FolderNameHint", "文件夹模板的文件夹名必须以 template 开头"),
                ForeColor = ThemeManager.Modified,
                AutoSize = true,
                Dock = DockStyle.Top,
                Font = new Font(FontHelper.UiFont(9f), FontStyle.Bold),
                Padding = new Padding(10, 0, 0, 0)
            };
            var sourceHintLabel = new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("TemplateManager.SourceLabel", "内置模板来源：GD小白丁（TT300）"),
                ForeColor = ThemeManager.TextTertiary,
                AutoSize = true,
                Dock = DockStyle.Top,
                Font = new Font(FontHelper.UiFont(9f), FontStyle.Bold),
                Padding = new Padding(10, 0, 0, 0)
            };
            // Dock=Top：后添加的在上方（提示行从上到下 = hint → folder → source）
            hintPanel.Controls.Add(sourceHintLabel);
            hintPanel.Controls.Add(folderHintLabel);
            hintPanel.Controls.Add(hintLabel);
            // 字体与高宽风格与设置窗其它按钮统一；宽度按文本自适应，避免英文等多语言文案显示不全
            var btnAddArc = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("TemplateManager.AddArcTemplate", "导入ARC模板"),
                Font = FontHelper.UiFont(9),
                
                AutoSizeMode = AntdUI.TAutoSize.Width,
                Dock = DockStyle.Left,
                Margin = new Padding(4, 6, 0, 0),
                Type = AntdUI.TTypeMini.Primary
            };
            btnAddArc.Click += OnAddArcClick;
            var btnAddFolder = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("TemplateManager.AddFolderTemplate", "导入文件夹模板"),
                Font = FontHelper.UiFont(9),
                
                AutoSizeMode = AntdUI.TAutoSize.Width,
                Dock = DockStyle.Left,
                Margin = new Padding(6, 6, 0, 0),
                Type = AntdUI.TTypeMini.Primary
            };
            btnAddFolder.Click += OnAddFolderClick;
            // Dock=Left：后添加的在左侧（视觉顺序 = 导入ARC → 导入文件夹）
            toolPanel.Controls.Add(btnAddFolder);
            toolPanel.Controls.Add(btnAddArc);

            // ── 表格 ──
            _table = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                MultipleRows = false,
                VisibleHeader = true,
                EnableHeaderResizing = true,
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill,
                Bordered = true,
                LostFocusClearSelection = false,
                EditMode = AntdUI.TEditMode.DoubleClick,
                Font = FontHelper.UiFont(9),
                AllowDrop = true
            };

            // Drag handle column
            _table.Columns.Add(new AntdUI.ColumnSort { Width = "5%" });

            // Row number column
            _table.Columns.Add(new AntdUI.Column("RowNo",
                LanguageManager.Instance.GetText("Common.Index", "序号"))
            {
                Width = "8%",
                Align = AntdUI.ColumnAlign.Center
            });

            // Enable checkbox
            _table.Columns.Add(new AntdUI.ColumnCheck("IsChecked",
                LanguageManager.Instance.GetText("TemplateManager.ColActive", "启用")) { Width = "8%" });

            // Note column (editable via double-click)
            _table.Columns.Add(new AntdUI.Column("DisplayNote",
                LanguageManager.Instance.GetText("TemplateManager.ColNote", "备注")) { Width = "20%", LineBreak = true });

            // Type column
            _table.Columns.Add(new AntdUI.Column("DisplayType",
                LanguageManager.Instance.GetText("TemplateManager.ColType", "类型")) { Width = "10%" });

            // Path column
            _table.Columns.Add(new AntdUI.Column("Path",
                LanguageManager.Instance.GetText("TemplateManager.ColPath", "路径"))
            {
                Width = "35%",
                LineBreak = true
            });

            // Operation column with buttons
            _table.Columns.Add(new AntdUI.Column("CellLinks",
                LanguageManager.Instance.GetText("Common.Operation", "操作"))
            {
                ColAlign = AntdUI.ColumnAlign.Center
            });

            void RefreshTable()
            {
                _table.DataSource = null;
                _table.DataSource = BuildDisplayRows(_entries);
            }

            // Cell end edit: sync DisplayNote -> entry.Note (skip BuiltIn)
            _table.CellEndEdit += (s, ea) =>
            {
                if (ea.RowIndex >= 1 && ea.RowIndex <= _entries.Count)
                {
                    var entry = _entries[ea.RowIndex - 1];
                    var col = _table.Columns[ea.ColumnIndex];
                    if (col.Key == "DisplayNote" && ea.Value is string note)
                    {
                        if (entry.Type == "BuiltIn") return true;
                        // 去掉换行并 Trim：备注会被拼进"模板来源：…"这类**单行日志**，
                        // 带换行会把一条日志渲染成多行（日志行号错位 → 双击跳转打到下一行）。
                        entry.Note = Program.SanitizeSingleLine(note);
                        RefreshTable();
                    }
                }
                return true;
            };

            // Prevent editing on non-DisplayNote columns and BuiltIn templates
            _table.CellBeginEdit += (s, ea) =>
            {
                if (ea.RowIndex >= 1 && ea.RowIndex <= _entries.Count)
                {
                    var entry = _entries[ea.RowIndex - 1];
                    var col = _table.Columns[ea.ColumnIndex];
                    if (col.Key != "DisplayNote")
                        return false;
                    if (entry.Type == "BuiltIn")
                        return false;
                }
                return true;
            };

            // Drag sort: sync _entries order
            _table.SortRows += (s, ea) =>
            {
                var sortIndex = _table.SortIndex();
                if (sortIndex == null || sortIndex.Length != _entries.Count) return;
                var reordered = new List<TemplateEntry>(_entries.Count);
                foreach (int originalIdx in sortIndex)
                {
                    if (originalIdx >= 0 && originalIdx < _entries.Count)
                        reordered.Add(_entries[originalIdx]);
                }
                if (reordered.Count == _entries.Count)
                {
                    _entries.Clear();
                    _entries.AddRange(reordered);
                    RefreshTable();
                }
            };

            // Cell button click: browse / clear
            _table.CellButtonClick += (s, ea) =>
            {
                if (ea.RowIndex < 1 || ea.RowIndex > _entries.Count) return;
                var entry = _entries[ea.RowIndex - 1];
                var btnId = ea.Btn?.Id;
                if (btnId == "browse")
                    OnBrowseClick(entry);
                else if (btnId == "clear")
                    OnClearClick(entry);
            };

            // Checkbox changed: sync to _entries
            _table.CheckedChanged += (s, ea) =>
            {
                if (ea.RowIndex >= 1 && ea.RowIndex <= _entries.Count)
                    _entries[ea.RowIndex - 1].IsChecked = ea.Value;
            };

            // Cell style: 路径列缺失时置灰色 + 删除线（只作用于路径单元格）
            _table.CellPaintBegin += (s, ea) =>
            {
                if (ea.Record is TemplateDisplayRow row)
                    PathCheckHelper.ApplyMissingCellStyle(ea, "Path", !row.Exists, _table.Font);
            };

            RefreshTable();

            // Dock 布局：Fill 先加，Top 后加（最后添加的在上方：按钮行 → 提示行）
            _root.Controls.Add(_table);
            _root.Controls.Add(hintPanel);
            _root.Controls.Add(toolPanel);
        }

        private List<TemplateDisplayRow> BuildDisplayRows(List<TemplateEntry> entries)
        {
            return entries.Select((e, idx) =>
            {
                // 备注压成单行显示（老配置里可能存着带换行的值）
                string displayNote = !string.IsNullOrEmpty(e.Note)
                    ? Program.SanitizeSingleLine(e.Note)
                    : GetDefaultNote(e);
                bool pathExists = true;
                if (e.Type != "BuiltIn" && !string.IsNullOrEmpty(e.Path))
                {
                    pathExists = PathCheckHelper.TemplateExists(e.Type, e.Path);
                }
                var links = new List<AntdUI.CellLink>();
                if (e.Type == "Arc")
                {
                    links.Add(new AntdUI.CellButton("browse",
                        LanguageManager.Instance.GetText("Common.Browse", "浏览"),
                        AntdUI.TTypeMini.Primary));
                    links.Add(new AntdUI.CellButton("clear",
                        LanguageManager.Instance.GetText("Common.Clear", "清除")));
                }
                else if (e.Type == "Folder")
                {
                    links.Add(new AntdUI.CellButton("browse",
                        LanguageManager.Instance.GetText("Common.Browse", "浏览"),
                        AntdUI.TTypeMini.Primary));
                    links.Add(new AntdUI.CellButton("clear",
                        LanguageManager.Instance.GetText("Common.Clear", "清除")));
                }
                return new TemplateDisplayRow
                {
                    RowNo = idx + 1,
                    IsChecked = e.IsChecked,
                    DisplayNote = displayNote,
                    DisplayType = GetTypeDisplay(e.Type),
                    Path = e.Path ?? "",
                    Exists = pathExists,
                    CellLinks = links.Count > 0 ? links.ToArray() : null
                };
            }).ToList();
        }

        /// <summary>ARC 文件对话框过滤器（走多语言，避免英文界面出现中文）</summary>
        private static string ArcFileFilter()
            => LanguageManager.Instance.GetText("Editor.ArcFileFilter", "ARC 文件|*.arc");

        /// <summary>ARC + 所有文件 过滤器</summary>
        private static string ArcAllFileFilter()
            => ArcFileFilter() + "|" + LanguageManager.Instance.GetText("Editor.AllFilesFilter", "所有文件|*.*");

        private string GetDefaultNote(TemplateEntry e)
        {
            if (e.Type == "BuiltIn")
                return SettingsManager.BuiltInTemplateDefaultNote(e.BuiltInVersion);   // 文件名短名（如 01-1.2.1.6-tt300）
            if (e.Type == "Arc")
            {
                try { return Path.GetFileName(e.Path); } catch { return e.Path; }
            }
            if (e.Type == "Folder")
            {
                try { return Path.GetFileName(e.Path); } catch { return e.Path; }
            }
            return e.Path;
        }

        private string GetTypeDisplay(string type)
        {
            if (type == "BuiltIn")
                return LanguageManager.Instance.GetText("TemplateManager.BuiltInType", "内置");
            if (type == "Arc")
                return LanguageManager.Instance.GetText("TemplateManager.ArcType", "ARC");
            if (type == "Folder")
                return LanguageManager.Instance.GetText("TemplateManager.FolderType", "文件夹");
            return type;
        }

        private void OnAddArcClick(object sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = ArcAllFileFilter(),
                Title = LanguageManager.Instance.GetText("TemplateManager.SelectArcFile", "选择模板ARC文件")
            };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                string path = ofd.FileName;
                _entries.Add(new TemplateEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    Note = Path.GetFileName(path),
                    Path = path,
                    Type = "Arc",
                    IsChecked = true
                });
                _table.DataSource = BuildDisplayRows(_entries);
            }
        }

        private void OnAddFolderClick(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            fbd.Description = LanguageManager.Instance.GetText("TemplateManager.SelectFolder", "选择模板文件夹");
            if (fbd.ShowDialog() == DialogResult.OK)
            {
                string path = fbd.SelectedPath;
                string folderName = Path.GetFileName(path);
                // 前缀放宽为 "template"（不带 s，2026-10-14 用户决策）；"templates" 开头依然匹配，旧命名不受影响
                if (!folderName.StartsWith("template", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(_owner,
                        LanguageManager.Instance.GetText("TemplateManager.FolderNameError", "文件夹模板的文件夹名必须以 template 开头，请重新选择"),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _entries.Add(new TemplateEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    Note = folderName,
                    Path = path,
                    Type = "Folder",
                    IsChecked = true
                });
                _table.DataSource = BuildDisplayRows(_entries);
            }
        }

        private void OnBrowseClick(TemplateEntry entry)
        {
            if (entry.Type == "Arc")
            {
                using var ofd = new OpenFileDialog
                {
                    Title = LanguageManager.Instance.GetText("TemplateManager.SelectArcFile", "选择模板ARC文件"),
                    Filter = ArcFileFilter(),
                    FileName = entry.Path
                };
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    entry.Path = ofd.FileName;
                    if (string.IsNullOrEmpty(entry.Note))
                        entry.Note = Path.GetFileName(ofd.FileName);
                    _table.DataSource = BuildDisplayRows(_entries);
                }
            }
            else if (entry.Type == "Folder")
            {
                using var fbd = new FolderBrowserDialog();
                fbd.Description = LanguageManager.Instance.GetText("TemplateManager.SelectFolder", "选择模板文件夹");
                fbd.SelectedPath = entry.Path;
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    string folderName = Path.GetFileName(fbd.SelectedPath);
                    if (!folderName.StartsWith("template", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(_owner,
                            LanguageManager.Instance.GetText("TemplateManager.FolderNameError", "文件夹模板的文件夹名必须以 template 开头，请重新选择"),
                            LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    entry.Path = fbd.SelectedPath;
                    if (string.IsNullOrEmpty(entry.Note))
                        entry.Note = folderName;
                    _table.DataSource = BuildDisplayRows(_entries);
                }
            }
        }

        private void OnClearClick(TemplateEntry entry)
        {
            if (MessageBox.Show(_owner,
                string.Format(LanguageManager.Instance.GetText("TemplateManager.DeleteConfirm",
                    "确定删除模板 \"{0}\"？"),
                    !string.IsNullOrEmpty(entry.Note) ? entry.Note : entry.Path),
                LanguageManager.Instance.GetText("Common.Confirm", "确认"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _entries.Remove(entry);
            _table.DataSource = BuildDisplayRows(_entries);
        }

        /// <summary>
        /// 保存时将本地修改写回 SettingsManager
        /// </summary>
        public void ApplyChanges()
        {
            SettingsManager.TemplateEntries.Clear();
            foreach (var e in _entries)
            {
                SettingsManager.TemplateEntries.Add(new TemplateEntry
                {
                    Id = e.Id,
                    Note = e.Note,
                    Path = e.Path,
                    Type = e.Type,
                    IsChecked = e.IsChecked,
                    BuiltInVersion = e.BuiltInVersion
                });
            }
            SettingsManager.Save();
        }
    }

    // 模板表格行显示模型
    public class TemplateDisplayRow
    {
        public int RowNo { get; set; }
        public bool IsChecked { get; set; }
        public string DisplayNote { get; set; }
        public string DisplayType { get; set; }
        public string Path { get; set; }
        public bool Exists { get; set; }
        public AntdUI.CellLink[] CellLinks { get; set; }
    }
}