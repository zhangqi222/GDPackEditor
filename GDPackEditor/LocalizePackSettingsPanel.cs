using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace GDPackEditor
{
    /// <summary>
    /// 本地化文件（语言包 ARC）管理面板（原编辑器内嵌语言包弹窗内容搬入设置窗「本地化文件」页签）。
    /// 管理方式：添加/浏览 ARC 语言包、备注、排序、清除、勾选生效；最上层生效（后者覆盖前者）。
    /// 保存时通过 Apply() 持久化 ImportedPacks + ActivePackKeys，并回调 ApplyLocalization 刷新已打开窗口。
    /// </summary>
    internal class LocalizePackSettingsPanel : AntdUI.Panel
    {
        private readonly AntdUI.BaseForm _owner;
        private readonly List<LanguagePackEntry> _packs;
        private bool[] _checks;
        private readonly AntdUI.Table _table;
        private readonly Func<List<LanguagePackEntry>, List<LangPackDisplayRow>> _buildDataSource;

        public LocalizePackSettingsPanel(AntdUI.BaseForm owner)
        {
            _owner = owner;
            Dock = DockStyle.Fill;
            Back = Color.Transparent;
            Padding = new Padding(25, 0, 25, 0);   // 内容区左右缩进（与设置窗其它页签一致）

            _packs = SettingsManager.ImportedPacks.Select(p => new LanguagePackEntry
            {
                Name = p.Name,
                Note = p.Note,
                FilePath = p.FilePath
            }).ToList();

            var activeKeys = SettingsManager.ActivePackKeys.ToList();
            for (int i = 0; i < _packs.Count; i++)
                _packs[i].IsChecked = activeKeys.Contains(i);

            _checks = _packs.Select(p => p.IsChecked).ToArray();

            _buildDataSource = (list) =>
            {
                return list.Select((p, idx) =>
                {
                    bool exists = PathCheckHelper.Exists(p.FilePath);
                    return new LangPackDisplayRow
                    {
                        RowNo = idx + 1,
                        IsChecked = idx < _checks.Length ? _checks[idx] : false,
                        DisplayNote = p.DisplayNote,
                        FilePath = p.FilePath,
                        Exists = exists,
                        CellLinks = new AntdUI.CellLink[]
                        {
                            new AntdUI.CellButton("browse", LanguageManager.Instance.GetText("Editor.LangPackChange", "更改"), AntdUI.TTypeMini.Primary),
                            new AntdUI.CellButton("clear", LanguageManager.Instance.GetText("Common.Clear", "清除"))
                        }
                    };
                }).ToList();
            };

            // ---- 顶部：说明文字（正常黑色，置于按钮上方） ----
            var introLabel = new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("Settings.LocalizeFileIntro",
                    "本功能仅可选语言包.arc文件，用于本地化显示dbr文件中的tagxxx内容\n显示位置：文件列表区域\n可多选，从上到下依次生效（后者覆盖前者）"),
                Dock = DockStyle.Top,
                Height = 68,
                ForeColor = ThemeManager.Text,
                Font = FontHelper.UiFont(9f),
                Padding = new Padding(0, 6, 0, 0)   // 左内边距由面板 Padding 统一提供，保证与按钮/表格左对齐
            };

            // ---- 工具栏（添加按钮） ----
            var toolPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Back = Color.Transparent
            };
            var btnAdd = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Editor.LangPackAdd", "添加"),
                Type = AntdUI.TTypeMini.Primary,
                Font = FontHelper.UiFont(9),
                
                AutoSizeMode = AntdUI.TAutoSize.Width,   // 宽度按文本自适应，避免英文文案显示不全
                Dock = DockStyle.Left
            };
            toolPanel.Controls.Add(btnAdd);

            // ---- 表格：拖拽排序 + 复选框 + 备注(可编辑) + 路径 + 操作 ----
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

            // 拖拽手柄列（用于行拖拽排序）
            _table.Columns.Add(new AntdUI.ColumnSort { Width = "5%" });

            // 行号列（绑定到 LangPackDisplayRow.RowNo，拖拽排序时保持原始序号不变）
            _table.Columns.Add(new AntdUI.Column("RowNo",
                LanguageManager.Instance.GetText("Common.Index", "序号"))
            {
                Width = "8%",
                Align = AntdUI.ColumnAlign.Center
            });

            // 启用复选框
            _table.Columns.Add(new AntdUI.ColumnCheck("IsChecked",
                LanguageManager.Instance.GetText("Editor.LangPackColActive", "生效")) { Width = "8%" });

            // 备注列（双击编辑）
            _table.Columns.Add(new AntdUI.Column("DisplayNote",
                LanguageManager.Instance.GetText("Editor.LangPackNote", "备注")) { Width = "20%", LineBreak = true });

            // 路径列（自动换行）
            _table.Columns.Add(new AntdUI.Column("FilePath",
                LanguageManager.Instance.GetText("Common.Path", "路径"))
            {
                Width = "41%",
                LineBreak = true
            });

            // 操作列：浏览 + 清除
            _table.Columns.Add(new AntdUI.Column("CellLinks",
                LanguageManager.Instance.GetText("Common.Operation", "操作"))
            {
                ColAlign = AntdUI.ColumnAlign.Center
            });

            _table.DataSource = _buildDataSource(_packs);

            // 编辑结束：同步 DisplayNote → Note
            _table.CellEndEdit += (s, ea) =>
            {
                if (ea.RowIndex >= 1 && ea.RowIndex <= _packs.Count)
                {
                    var pack = _packs[ea.RowIndex - 1];
                    var col = _table.Columns[ea.ColumnIndex];
                    if (col.Key == "DisplayNote" && ea.Value is string note)
                    {
                        pack.Note = note;
                        SyncCheckState();
                        _checks = _packs.Select(p => p.IsChecked).ToArray();
                        _table.DataSource = _buildDataSource(_packs);
                    }
                }
                return true;
            };

            // 阻止非 DisplayNote 列进入编辑
            _table.CellBeginEdit += (s, ea) =>
            {
                if (ea.RowIndex >= 1 && ea.RowIndex <= _packs.Count)
                {
                    var col = _table.Columns[ea.ColumnIndex];
                    if (col.Key != "DisplayNote")
                        return false;
                }
                return true;
            };

            // 行拖拽排序后同步 _packs 列表顺序
            _table.SortRows += (s, ea) =>
            {
                SyncCheckState();
                _checks = _packs.Select(p => p.IsChecked).ToArray();
                var sortIndex = _table.SortIndex();
                if (sortIndex == null || sortIndex.Length != _packs.Count) return;
                var reordered = new List<LanguagePackEntry>(_packs.Count);
                var reorderedChecks = new List<bool>(_packs.Count);
                foreach (int originalIdx in sortIndex)
                {
                    if (originalIdx >= 0 && originalIdx < _packs.Count)
                    {
                        reordered.Add(_packs[originalIdx]);
                        reorderedChecks.Add(_checks[originalIdx]);
                    }
                }
                if (reordered.Count == _packs.Count)
                {
                    _packs.Clear();
                    _packs.AddRange(reordered);
                    _checks = reorderedChecks.ToArray();
                }
            };

            // CellButton 点击（浏览/清除）
            _table.CellButtonClick += (s, ea) =>
            {
                if (ea.RowIndex < 1 || ea.RowIndex > _packs.Count) return;
                var pack = _packs[ea.RowIndex - 1];
                var btnId = ea.Btn?.Id;
                if (btnId == "browse") BrowsePath(pack);
                else if (btnId == "clear") ClearPath(pack);
            };

            _table.CellPaintBegin += (s, ea) =>
            {
                if (ea.Record is LangPackDisplayRow row)
                    PathCheckHelper.ApplyMissingCellStyle(ea, "FilePath", !row.Exists, _table.Font);
            };

            btnAdd.Click += (s, ea) => AddPack();

            // Controls.Add 顺序：Fill先加→按钮行→说明文字最后加（WinForms从后往前处理Dock，说明文字在最上）
            Controls.Add(_table);
            Controls.Add(toolPanel);
            Controls.Add(introLabel);
        }

        /// <summary>
        /// 保存本地修改：持久化 ImportedPacks + ActivePackKeys，并回调刷新已打开窗口（设置窗保存时统一调用）。
        /// </summary>
        public void Apply()
        {
            SyncCheckState();
            // 备注保存前过滤换行符（压成单行），避免持久化后污染日志/界面展示
            foreach (var p in _packs)
                if (!string.IsNullOrEmpty(p.Note)) p.Note = Program.SanitizeSingleLine(p.Note);
            SettingsManager.ImportedPacks = _packs;
            var newActiveKeys = new List<int>();
            for (int i = 0; i < _packs.Count; i++)
            {
                if (_packs[i].IsChecked)
                    newActiveKeys.Add(i);
            }
            SettingsManager.ActivePackKeys = newActiveKeys;
            SettingsManager.Save();
        }

        /// <summary>ARC + 所有文件 过滤器（走多语言，避免英文界面出现中文）</summary>
        private static string ArcAllFileFilter()
            => LanguageManager.Instance.GetText("Editor.ArcFileFilter", "ARC 文件|*.arc")
               + "|" + LanguageManager.Instance.GetText("Editor.AllFilesFilter", "所有文件|*.*");

        private void AddPack()
        {
            SyncCheckState();
            using var dlg = new OpenFileDialog
            {
                Filter = ArcAllFileFilter(),
                Title = LanguageManager.Instance.GetText("Editor.SelectArcFile", "选择ARC语言包文件")
            };
            if (dlg.ShowDialog(_owner) != DialogResult.OK) return;

            string path = dlg.FileName;
            if (!File.Exists(path))
            {
                MessageBox.Show(_owner, LanguageManager.Instance.GetText("Editor.PackFileNotFound", "文件不存在"));
                return;
            }
            if (_packs.Any(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(_owner, LanguageManager.Instance.GetText("Editor.PackAlreadyImported", "该语言包已导入"));
                return;
            }
            _packs.Add(new LanguagePackEntry
            {
                Name = Path.GetFileName(path),
                FilePath = path,
                IsChecked = true
            });
            _checks = _packs.Select(p => p.IsChecked).ToArray();
            _table.DataSource = _buildDataSource(_packs);
        }

        private void BrowsePath(LanguagePackEntry target)
        {
            SyncCheckState();
            using var dlg = new OpenFileDialog
            {
                Filter = ArcAllFileFilter(),
                Title = LanguageManager.Instance.GetText("Editor.SelectArcFile", "选择ARC语言包文件")
            };
            if (dlg.ShowDialog(_owner) != DialogResult.OK) return;

            string path = dlg.FileName;
            if (!File.Exists(path))
            {
                MessageBox.Show(_owner, LanguageManager.Instance.GetText("Editor.PackFileNotFound", "文件不存在"));
                return;
            }
            if (_packs.Any(p => p != target && string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(_owner, LanguageManager.Instance.GetText("Editor.PackAlreadyImported", "该语言包已导入"));
                return;
            }

            target.Name = Path.GetFileName(path);
            target.FilePath = path;
            _table.DataSource = _buildDataSource(_packs);
        }

        private void ClearPath(LanguagePackEntry target)
        {
            SyncCheckState();
            _packs.Remove(target);
            for (int i = 0; i < _checks.Length; i++)
                _checks[i] = i < _packs.Count ? _packs[i].IsChecked : false;
            _table.DataSource = _buildDataSource(_packs);
        }

        private void SyncCheckState()
        {
            for (int i = 0; i < _packs.Count && i < _table.RowCount; i++)
            {
                var row = _table.GetRow(i);
                if (row?.record is LangPackDisplayRow displayRow)
                {
                    _packs[i].IsChecked = displayRow.IsChecked;
                }
            }
        }
    }

    internal class LangPackDisplayRow
    {
        public bool IsChecked { get; set; }
        public string DisplayNote { get; set; }
        public string FilePath { get; set; }
        public bool Exists { get; set; }
        public AntdUI.CellLink[] CellLinks { get; set; }
        public int RowNo { get; set; }
    }
}