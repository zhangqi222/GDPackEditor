using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace GDPackEditor
{
    /// <summary>
    /// DBR 字段名本地化管理面板（原 DbrKeyMappingDialog 弹窗内容搬入设置窗「本地化模板字段」页签）。
    /// 管理方式与"文件夹显示本地化"（FolderMappingPanel）一致：内置/浏览加载外部TXT、备注、排序、更换、清除、持久化。
    /// 保存通过 Apply() 统一在设置窗保存时触发。
    /// </summary>
    internal class DbrKeyMappingPanel : AntdUI.Panel
    {
        private readonly AntdUI.BaseForm _owner;
        private readonly List<DbrKeyMappingEntry> _packs;
        private readonly AntdUI.Table _table;
        private readonly Func<List<DbrKeyMappingEntry>, List<DbrKeyMappingDisplayRow>> _buildDataSource;

        public DbrKeyMappingPanel(AntdUI.BaseForm owner)
        {
            _owner = owner;
            Dock = DockStyle.Fill;
            Back = Color.Transparent;
            Padding = new Padding(25, 0, 25, 0);   // 内容区左右缩进（与设置窗其它页签一致）

            _packs = SettingsManager.DbrKeyMappingPacks.Select(p => new DbrKeyMappingEntry
            {
                Id = p.Id,
                Note = p.Note,
                FilePath = p.FilePath,
                Type = p.Type,
                IsChecked = p.IsChecked
            }).ToList();

            // 迁移旧版单条内置 → 删除，改为每条内嵌文件独立一行
            _packs.RemoveAll(p => p.Type == "BuiltIn" && p.Id == "builtin-dbrkey-cn");
            // 确保每个内嵌资源都有对应的独立内置条目
            var builtInFileNames = DbrKeyMapping.GetBuiltInFileNames();
            foreach (var fname in builtInFileNames)
            {
                if (!_packs.Any(p => p.Type == "BuiltIn" && string.Equals(p.FilePath, fname, StringComparison.OrdinalIgnoreCase)))
                {
                    _packs.Add(new DbrKeyMappingEntry
                    {
                        Id = "builtin-" + fname,
                        Note = LanguageManager.Instance.GetText("Editor.DbrKeyMappingBuiltInNote", "内置字段名字典"),
                        FilePath = fname,
                        Type = "BuiltIn",
                        // 默认不启用（与 SettingsManager.EnsureDefaultDbrKeyMappingEntries 一致），由用户自行勾选开启
                        IsChecked = false
                    });
                }
            }

            _buildDataSource = (list) =>
            {
                return list.Select((p, idx) =>
                {
                    bool exists = p.Type == "BuiltIn" || PathCheckHelper.Exists(p.FilePath);
                    return new DbrKeyMappingDisplayRow
                    {
                        RowNo = idx + 1,
                        IsChecked = p.IsChecked,
                        DisplayNote = p.DisplayNote,
                        DisplayType = p.DisplayType,
                        FilePath = p.FilePath ?? "",
                        Exists = exists,
                        CellLinks = p.Type == "BuiltIn"
                            ? new AntdUI.CellLink[]
                            {
                                new AntdUI.CellButton("saveAs", LanguageManager.Instance.GetText("Editor.DbrKeyMappingSaveAs", "另存为"), AntdUI.TTypeMini.Primary)
                            }
                            : new AntdUI.CellLink[]
                            {
                                new AntdUI.CellButton("browse", LanguageManager.Instance.GetText("TemplateManager.ChangeButton", "更改"), AntdUI.TTypeMini.Primary),
                                new AntdUI.CellButton("clear", LanguageManager.Instance.GetText("Common.Clear", "清除"))
                            }
                    };
                }).ToList();
            };

            // ---- 顶部：说明文字（正常黑色，置于按钮上方） ----
            var introLabel = new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("Settings.LocalizeFieldIntro",
                    "本功能仅可将dbr编辑器中的字段名本地化\n仅内置中文，可将内置字典另存后，自行修改并导入，实现自定义"),
                Dock = DockStyle.Top,
                Height = 50,
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
                Text = LanguageManager.Instance.GetText("Editor.DbrKeyMappingAddButton", "添加"),
                Type = AntdUI.TTypeMini.Primary,
                Font = FontHelper.UiFont(9),
                
                AutoSizeMode = AntdUI.TAutoSize.Width,   // 宽度按文本自适应，避免英文文案显示不全
                Dock = DockStyle.Left
            };
            toolPanel.Controls.Add(btnAdd);

            // ---- 表格：拖拽排序 + 启用 + 类型 + 备注 + 路径 + 操作 ----
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

            _table.Columns.Add(new AntdUI.ColumnSort { Width = "5%" });
            _table.Columns.Add(new AntdUI.Column("RowNo",
                LanguageManager.Instance.GetText("Common.Index", "序号"))
            {
                Width = "6%",
                Align = AntdUI.ColumnAlign.Center
            });
            _table.Columns.Add(new AntdUI.ColumnCheck("IsChecked",
                LanguageManager.Instance.GetText("Editor.DbrKeyMappingColActive", "启用")) { Width = "7%" });
            _table.Columns.Add(new AntdUI.Column("DisplayType",
                LanguageManager.Instance.GetText("Editor.DbrKeyMappingTypeColumn", "类型")) { Width = "10%" });
            _table.Columns.Add(new AntdUI.Column("DisplayNote",
                LanguageManager.Instance.GetText("Editor.DbrKeyMappingNoteColumn", "备注")) { Width = "22%", LineBreak = true });
            _table.Columns.Add(new AntdUI.Column("FilePath",
                LanguageManager.Instance.GetText("Common.Path", "路径")) { Width = "25%", LineBreak = true });
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
                var sortIndex = _table.SortIndex();
                if (sortIndex == null || sortIndex.Length != _packs.Count) return;
                var reordered = new List<DbrKeyMappingEntry>(_packs.Count);
                foreach (int originalIdx in sortIndex)
                {
                    if (originalIdx >= 0 && originalIdx < _packs.Count)
                        reordered.Add(_packs[originalIdx]);
                }
                if (reordered.Count == _packs.Count)
                {
                    _packs.Clear();
                    _packs.AddRange(reordered);
                }
                _table.DataSource = _buildDataSource(_packs);
            };

            // CellButton 点击（浏览/清除）
            _table.CellButtonClick += (s, ea) =>
            {
                if (ea.RowIndex < 1 || ea.RowIndex > _packs.Count) return;
                var pack = _packs[ea.RowIndex - 1];
                var btnId = ea.Btn?.Id;
                if (btnId == "saveAs") SaveBuiltInAs(pack);
                else if (btnId == "browse") BrowsePath(pack);
                else if (btnId == "clear") ClearPath(pack);
            };

            _table.CellPaintBegin += (s, ea) =>
            {
                if (ea.Record is DbrKeyMappingDisplayRow row)
                    PathCheckHelper.ApplyMissingCellStyle(ea, "FilePath", !row.Exists, _table.Font);
            };

            btnAdd.Click += (s, ea) => AddPack();

            // Dock 布局：Fill 先加 → 按钮行 → 说明文字最后加（WinForms 从后往前处理 Dock，说明文字在最上）
            // 原「另存为」独立提示已合并进说明文字，不再重复显示
            Controls.Add(_table);
            Controls.Add(toolPanel);
            Controls.Add(introLabel);
        }

        /// <summary>
        /// 将本地修改写回 SettingsManager 并重建字段名映射缓存（设置窗保存时统一调用）。
        /// </summary>
        public void Apply()
        {
            SyncCheckState();
            // 备注保存前过滤换行符（压成单行），避免持久化后污染日志/界面展示
            foreach (var p in _packs)
                if (!string.IsNullOrEmpty(p.Note)) p.Note = Program.SanitizeSingleLine(p.Note);
            SettingsManager.DbrKeyMappingPacks = _packs;
            SettingsManager.Save();
            DbrKeyMapping.Rebuild();
        }

        /// <summary>TXT + 所有文件 过滤器（走多语言，避免英文界面出现中文）</summary>
        private static string TxtAllFileFilter()
            => LanguageManager.Instance.GetText("Editor.TxtFileFilter", "TXT 字典文件|*.txt")
               + "|" + LanguageManager.Instance.GetText("Editor.AllFilesFilter", "所有文件|*.*");

        private void AddPack()
        {
            SyncCheckState();
            using var dlg = new OpenFileDialog
            {
                Filter = TxtAllFileFilter(),
                Title = LanguageManager.Instance.GetText("Editor.DbrKeyMappingSelectTxt", "选择字段名字典文件")
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
                MessageBox.Show(_owner, LanguageManager.Instance.GetText("Editor.PackAlreadyImported", "该文件已导入"));
                return;
            }
            _packs.Add(new DbrKeyMappingEntry
            {
                Id = Guid.NewGuid().ToString(),
                Note = Path.GetFileName(path),
                FilePath = path,
                Type = "External",
                IsChecked = true
            });
            _table.DataSource = _buildDataSource(_packs);
        }

        private void BrowsePath(DbrKeyMappingEntry target)
        {
            SyncCheckState();
            using var dlg = new OpenFileDialog
            {
                Filter = TxtAllFileFilter(),
                Title = LanguageManager.Instance.GetText("Editor.DbrKeyMappingSelectTxt", "选择字段名字典文件")
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
                MessageBox.Show(_owner, LanguageManager.Instance.GetText("Editor.PackAlreadyImported", "该文件已导入"));
                return;
            }

            target.FilePath = path;
            if (string.IsNullOrEmpty(target.Note))
                target.Note = Path.GetFileName(path);
            _table.DataSource = _buildDataSource(_packs);
        }

        private void ClearPath(DbrKeyMappingEntry target)
        {
            SyncCheckState();
            _packs.Remove(target);
            _table.DataSource = _buildDataSource(_packs);
        }

        /// <summary>
        /// 将内置内嵌字典另存为用户选定的位置。
        /// </summary>
        private void SaveBuiltInAs(DbrKeyMappingEntry target)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = TxtAllFileFilter(),
                FileName = Path.GetFileName(target.FilePath ?? "DbrKeyMapping_cn.txt"),
                Title = LanguageManager.Instance.GetText("Editor.DbrKeyMappingSaveAsTitle", "保存内置字典为")
            };
            if (dlg.ShowDialog(_owner) != DialogResult.OK) return;

            try
            {
                string content = DbrKeyMapping.GetBuiltInContent(target.FilePath);
                if (content == null)
                {
                    MessageBox.Show(_owner, string.Format(
                        LanguageManager.Instance.GetText("Editor.DbrKeyMappingSaveFail", "保存失败: {0}"),
                        LanguageManager.Instance.GetText("Editor.PackFileNotFound", "文件不存在")));
                    return;
                }
                File.WriteAllText(dlg.FileName, content, new UTF8Encoding(false));
                MessageBox.Show(_owner, string.Format(
                    LanguageManager.Instance.GetText("Editor.DbrKeyMappingSaveSuccess", "已保存到: {0}"), dlg.FileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show(_owner, string.Format(
                    LanguageManager.Instance.GetText("Editor.DbrKeyMappingSaveFail", "保存失败: {0}"), ex.Message));
            }
        }

        private void SyncCheckState()
        {
            for (int i = 0; i < _packs.Count && i < _table.RowCount; i++)
            {
                var row = _table.GetRow(i);
                if (row?.record is DbrKeyMappingDisplayRow displayRow)
                    _packs[i].IsChecked = displayRow.IsChecked;
            }
        }
    }

    internal class DbrKeyMappingDisplayRow
    {
        public bool IsChecked { get; set; }
        public string DisplayNote { get; set; }
        public string DisplayType { get; set; }
        public string FilePath { get; set; }
        public bool Exists { get; set; }
        public AntdUI.CellLink[] CellLinks { get; set; }
        public int RowNo { get; set; }
    }
}