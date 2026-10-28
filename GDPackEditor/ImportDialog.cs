using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace GDPackEditor
{
    public class ImportDialog : AntdUI.Panel
    {
        private AntdUI.Tree _folderTree;
        private readonly string _archiveName;
        private AntdUI.Button _addFileButton;
        private AntdUI.Button _addFolderButton;
        private AntdUI.Button _clearButton;
        private AntdUI.Table _fileTable;
        private AntdUI.Checkbox _overwriteCheckBox;
        private readonly HashSet<string> _existingPaths;
        private Image _closedFolderIcon;
        private Image _openFolderIcon;

        public List<(string sourcePath, string relPath)> Files { get; private set; }

        public bool OverwriteExisting => _overwriteCheckBox?.Checked ?? true;

        public bool HasItems => _items.Count > 0;

        public bool CanStart => _overwriteCheckBox?.Checked == true ? _items.Count > 0 : _items.Any(i =>
        {
            string finalPath = GetFinalPath(i);
            return !_existingPaths.Contains(finalPath);
        });

        private class FileItemDisplay
        {
            public string FinalPath { get; set; }
            public bool IsExisting { get; set; }
            public FileItem Source { get; set; }
            public AntdUI.CellLink[] CellLinks { get; set; }
        }

        private class FileItem
        {
            public string SourcePath { get; set; }
            public string BaseRelPath { get; set; }
        }

        private readonly List<FileItem> _items = new List<FileItem>();

        private string SelectedFolder
        {
            get
            {
                var item = _folderTree.SelectItem;
                if (item == null) return "";
                var tag = item.Tag as string;
                return tag ?? "";
            }
        }

        public ImportDialog(HashSet<string> existingRelPaths, List<string> allFolderPaths, string defaultFolder = null, string archiveFilePath = null)
        {
            _existingPaths = existingRelPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Files = new List<(string, string)>();
            _archiveName = archiveFilePath != null ? Path.GetFileName(archiveFilePath) : null;

            Dock = DockStyle.Fill;
            BackColor = Color.Transparent;

            // ========== Splitter 分隔面板（Fill，占满整个 ImportDialog） ==========
            // 注意：SplitterDistance / Panel1MinSize / Panel2MinSize 不能在构造时设置，
            // 因为控件 Width=0 时内部校验必定失败，必须延迟到有尺寸后再设置
            var splitter = new AntdUI.Splitter
            {
                Dock = DockStyle.Fill,
                Panel1MinSize = 0,
                Panel2MinSize = 0,
                SplitterWidth = 5,
                FixedPanel = FixedPanel.Panel1
            };
            // 延迟到控件首次获得足够宽度时，一次性设置 MinSize 和 SplitterDistance
            bool splitterInited = false;
            splitter.SizeChanged += (s, e) =>
            {
                if (splitterInited) return;
                // 需要 Width 足够容纳 Panel1MinSize + SplitterWidth + Panel2MinSize + 余量
                int needed = 150 + 5 + 200 + 50;
                if (splitter.Width < needed) return;
                splitterInited = true;
                splitter.SplitterDistance = (int)(splitter.Width * 0.3);
            };

            // ========== Panel1（左侧）：文件夹树 ==========
            var leftLabel = new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("Editor.ImportTo", "导入到："),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = true,
                Dock = DockStyle.Top,
                Padding = new Padding(0, 0, 0, 4)
            };

            _folderTree = new AntdUI.Tree
            {
                Dock = DockStyle.Fill,
                BlockNode = true,
                Gap = 4
            };
            _folderTree.SelectChanged += OnFolderTreeSelectChanged;
            // 展开走 AntdUI 默认（单击选定、双击/箭头展开，与 ARZ 编辑器文件树一致），这里只负责切换文件夹图标
            _folderTree.BeforeExpand += (s, e) => { if (_openFolderIcon != null) e.Item.Icon = _openFolderIcon; };
            BuildFolderTree(allFolderPaths, defaultFolder);

            // Controls.Add 顺序：Fill先加→Top后加
            splitter.Panel1.Controls.Add(_folderTree);
            splitter.Panel1.Controls.Add(leftLabel);

            // ========== Panel2（右侧）：导入文件列表 ==========
            var rightPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent
            };

            // 文件表格（Fill，先加入）
            _fileTable = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                MultipleRows = true,
                VisibleHeader = true,
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill,
                Bordered = false,
                AllowDrop = true,
                GapCell = 2,
                Font = FontHelper.UiFont(9)
            };
            _fileTable.Columns.Add(new AntdUI.Column("FinalPath",
                LanguageManager.Instance.GetText("Editor.ImportColRelPath", "预计导入路径")));
            _fileTable.Columns.Add(new AntdUI.Column("CellLinks",
                LanguageManager.Instance.GetText("Editor.Operations", "操作")) { Width = "100", ColAlign = AntdUI.ColumnAlign.Center });
            _fileTable.SetRowStyle += OnFileTableSetRowStyle;
            _fileTable.KeyDown += OnFileTableKeyDown;
            _fileTable.DragEnter += OnFileTableDragEnter;
            _fileTable.DragDrop += OnFileTableDragDrop;
            _fileTable.CellClick += OnFileTableCellClick;
            _fileTable.CellButtonClick += OnFileTableCellButtonClick;
            _fileTable.SelectIndexChanged += OnFileTableSelectIndexChanged;
            rightPanel.Controls.Add(_fileTable);

            // 底部清空按钮（Bottom）
            var bottomRow = new AntdUI.FlowPanel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                Align = AntdUI.TAlignFlow.LeftCenter,
                Padding = new Padding(4)
            };
            _clearButton = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Editor.ImportClearList", "清空列表"),
                
                Enabled = false,
                Type = AntdUI.TTypeMini.Error
            };
            _clearButton.Click += OnClearClick;
            bottomRow.Controls.Add(_clearButton);
            rightPanel.Controls.Add(bottomRow);
            AdaptRowHeightToContent(bottomRow, _clearButton);

            // 提示标签（Top）
            rightPanel.Controls.Add(new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("Editor.ImportSaveHint", "导入文件后，需保存才能生效。"),
                AutoSize = true,
                ForeColor = ThemeManager.TextTertiary,
                Dock = DockStyle.Top,
                Padding = new Padding(4, 4, 0, 4)
            });

            // 按钮行 + 覆盖复选框（Top）
            var topRow = new AntdUI.FlowPanel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Align = AntdUI.TAlignFlow.LeftCenter,
                Gap = 8,
                Padding = new Padding(4)
            };
            _addFileButton = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Editor.ImportAddFile", "导入文件..."),
                
                Type = AntdUI.TTypeMini.Primary
            };
            _addFileButton.Click += OnAddFileClick;
            _addFolderButton = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Editor.ImportAddFolder", "导入文件夹..."),
                
                Type = AntdUI.TTypeMini.Primary
            };
            _addFolderButton.Click += OnAddFolderClick;
            _overwriteCheckBox = new AntdUI.Checkbox
            {
                Text = LanguageManager.Instance.GetText("Editor.ImportOverwrite", "覆盖同名文件"),
                AutoSize = true,
                Checked = true,
                Margin = new Padding(8, 0, 0, 0)
            };
            _overwriteCheckBox.CheckedChanged += (s, ev) => RefreshFileTable();
            topRow.Controls.Add(_addFileButton);
            topRow.Controls.Add(_addFolderButton);
            topRow.Controls.Add(_overwriteCheckBox);
            rightPanel.Controls.Add(topRow);
            AdaptRowHeightToContent(topRow, _addFileButton, _addFolderButton, _overwriteCheckBox);

            // Controls.Add 顺序：Fill先加→Top后加
            splitter.Panel2.Controls.Add(rightPanel);

            // Splitter 放入 ImportDialog 根容器
            Controls.Add(splitter);

            // 文件夹图标
            SetupFolderIcons();
        }

        /// <summary>
        /// 按钮行高度自适应内容自然高度：行内 AntdUI 控件（AutoSize 按钮/复选框）的实际高度
        /// 随字体与 DPI 变化，固定 36px 行高会把较高的控件裁掉底部。
        /// 监听各子控件 SizeChanged，把行撑到「最高子控件高度 + 上下留白」。
        /// </summary>
        private void AdaptRowHeightToContent(AntdUI.FlowPanel row, params System.Windows.Forms.Control[] children)
        {
            EventHandler adapt = (s, e) =>
            {
                int maxH = 0;
                foreach (var c in children)
                    if (c != null && c.Height > maxH) maxH = c.Height;
                if (maxH <= 0) return;
                int h = maxH + DpiManager.S(8);
                if (row.Height != h) row.Height = h;
            };
            foreach (var c in children)
            {
                if (c == null) continue;
                c.SizeChanged += adapt;
            }
            adapt(null, EventArgs.Empty);
        }

        private void SetupFolderIcons()
        {            try
            {
                float dpiScale = DpiManager.Scale;
                int iconSize = (int)(16 * dpiScale);
                var imageList = TreeHelper.CreateSystemFolderIcons(iconSize);
                _closedFolderIcon = imageList.Images[0];
                _openFolderIcon = imageList.Images.Count > 1 ? imageList.Images[1] : imageList.Images[0];
                imageList.Dispose();
                SetFolderIconsRecursive(_folderTree.Items, false);
            }
            catch { }
        }

        private void SetFolderIconsRecursive(AntdUI.TreeItemCollection items, bool expanded)
        {
            foreach (var item in items)
            {
                item.Icon = expanded && item.Expand ? _openFolderIcon : _closedFolderIcon;
                if (item.Sub != null && item.Sub.Count > 0)
                    SetFolderIconsRecursive(item.Sub, expanded || item.Expand);
            }
        }

        private void OnFolderTreeSelectChanged(object sender, AntdUI.TreeSelectEventArgs e)
        {
            // 单击仅选定（AntdUI 默认行为，与 ARZ 编辑器文件树一致，不再手动切换展开）；
            // 展开由默认双击/箭头处理
            RefreshFileTable();
        }

        private void BuildFolderTree(List<string> allFolderPaths, string defaultFolder)
        {
            _folderTree.Items.Clear();

            var rootItem = new AntdUI.TreeItem(_archiveName ?? "\\")
            {
                Tag = "",
                ID = "",
                Expand = true
            };
            _folderTree.Items.Add(rootItem);

            if (allFolderPaths != null && allFolderPaths.Count > 0)
            {
                var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in allFolderPaths)
                {
                    string normalized = path.Replace('\\', '/').Trim('/');
                    if (string.IsNullOrEmpty(normalized)) continue;
                    string[] parts = normalized.Split('/');
                    for (int i = 0; i < parts.Length; i++)
                        dirSet.Add(string.Join("/", parts, 0, i + 1));
                }
                var sorted = dirSet.OrderBy(d => d).ToList();

                foreach (string dir in sorted)
                {
                    string[] parts = dir.Split('/');
                    AntdUI.TreeItem parent = rootItem;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string currentPath = string.Join("/", parts, 0, i + 1);
                        var existing = FindTreeChild(parent, parts[i]);
                        if (existing == null)
                        {
                            existing = new AntdUI.TreeItem(parts[i])
                            {
                                Tag = currentPath,
                                ID = currentPath
                            };
                            parent.Sub.Add(existing);
                        }
                        parent = existing;
                    }
                }
            }

            rootItem.Expand = true;

            // 自动展开并选中 defaultFolder 对应的节点
            if (!string.IsNullOrEmpty(defaultFolder))
            {
                string normalized = defaultFolder.Replace('\\', '/').Trim('/');
                if (!string.IsNullOrEmpty(normalized))
                {
                    string[] parts = normalized.Split('/');
                    AntdUI.TreeItem cur = rootItem;
                    AntdUI.TreeItem target = null;
                    foreach (string part in parts)
                    {
                        target = FindTreeChild(cur, part);
                        if (target == null) break;
                        target.Expand = true;
                        cur = target;
                    }
                    if (target != null)
                        _folderTree.SelectItem = target;
                }
            }
        }

        private static AntdUI.TreeItem FindTreeChild(AntdUI.TreeItem parent, string name)
        {
            if (parent.Sub == null) return null;
            foreach (var child in parent.Sub)
            {
                if (string.Equals(child.Text, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            }
            return null;
        }

        private string GetFinalPath(FileItem fi)
        {
            string folder = SelectedFolder;
            if (string.IsNullOrEmpty(folder))
                return fi.BaseRelPath;
            return folder + "/" + fi.BaseRelPath;
        }

        private AntdUI.Table.CellStyleInfo OnFileTableSetRowStyle(object sender, AntdUI.TableSetRowStyleEventArgs e)
        {
            if (e.Record is FileItemDisplay row && row.IsExisting)
            {
                return new AntdUI.Table.CellStyleInfo
                {
                    ForeColor = ThemeManager.TextTertiary
                };
            }
            return null;
        }

        private void OnFileTableDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        private void OnFileTableDragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;
            AddPaths(paths);
        }

        private void OnFileTableCellClick(object sender, AntdUI.TableClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right && _items.Count > 0)
            {
                if (e.RowIndex >= 1)
                    _fileTable.SelectedIndex = e.RowIndex;

                AntdUI.ContextMenuStrip.open(_fileTable, item =>
                {
                    if (item.ID != "remove") return;
                    var toRemove = new List<FileItem>();
                    foreach (var idx in _fileTable.SelectedIndexs)
                    {
                        if (idx >= 1 && idx <= _fileTable.RowCount)
                        {
                            var row = _fileTable.GetRow(idx - 1);
                            if (row?.record is FileItemDisplay fd)
                                toRemove.Add(fd.Source);
                        }
                    }
                    foreach (var fi in toRemove)
                        _items.Remove(fi);
                    RefreshFileTable();
                }, new AntdUI.IContextMenuStripItem[]
                {
                    new AntdUI.ContextMenuStripItem(
                        LanguageManager.Instance.GetText("Editor.ImportRemoveItem", "清除"))
                        { ID = "remove" }
                });
            }
        }

        private void OnFileTableSelectIndexChanged(object sender, EventArgs e)
        {
            UpdateButtonStates();
        }

        private void OnFileTableCellButtonClick(object sender, AntdUI.TableButtonEventArgs e)
        {
            if (e.Btn?.Id == "remove" && e.Record is FileItemDisplay fd)
            {
                _items.Remove(fd.Source);
                RefreshFileTable();
            }
        }

        private void OnAddFileClick(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Multiselect = true;
                dlg.Filter = LanguageManager.Instance.GetText("Editor.AllFilesFilter", "所有文件|*.*");
                dlg.Title = LanguageManager.Instance.GetText("Editor.ImportFileTitle", "选择文件");
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                AddPaths(dlg.FileNames);
            }
        }

        private void OnAddFolderClick(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = LanguageManager.Instance.GetText("Editor.ImportFolderTitle", "选择要导入的文件夹");
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                AddPaths(new[] { dlg.SelectedPath });
            }
        }

        private void OnClearClick(object sender, EventArgs e)
        {
            _items.Clear();
            RefreshFileTable();
        }

        private void OnFileTableKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete && _fileTable.SelectedIndexs.Length > 0)
            {
                var toRemove = new List<FileItem>();
                foreach (var idx in _fileTable.SelectedIndexs)
                {
                    if (idx >= 1 && idx <= _fileTable.RowCount)
                    {
                        var row = _fileTable.GetRow(idx - 1);
                        if (row?.record is FileItemDisplay fd)
                            toRemove.Add(fd.Source);
                    }
                }
                foreach (var fi in toRemove)
                    _items.Remove(fi);
                RefreshFileTable();
            }
        }

        public bool ConfirmStart()
        {
            if (_items.Count == 0) return false;

            var fileList = _items.Select(i => (i.SourcePath, GetFinalPath(i)))
                .Where(t => OverwriteExisting || !_existingPaths.Contains(t.Item2))
                .ToList();

            if (fileList.Count == 0) return false;

            Files = fileList;
            return true;
        }

        private void AddPaths(string[] paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    var attr = File.GetAttributes(path);
                    if ((attr & FileAttributes.Directory) == FileAttributes.Directory)
                        AddFolder(path);
                    else
                        AddFile(path);
                }
                catch { }
            }
            RefreshFileTable();
        }

        private void AddFolder(string folderPath)
        {
            string parentDir = Path.GetDirectoryName(folderPath.TrimEnd('\\', '/'));

            string[] allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);
            foreach (string file in allFiles)
            {
                string baseRel = file.Substring(parentDir.Length + 1).Replace('\\', '/');
                AddItem(file, baseRel);
            }
        }

        private void AddFile(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            AddItem(filePath, fileName);
        }

        private void AddItem(string sourcePath, string baseRelPath)
        {
            if (_items.Any(i => i.BaseRelPath.Equals(baseRelPath, StringComparison.OrdinalIgnoreCase)))
                return;
            _items.Add(new FileItem { SourcePath = sourcePath, BaseRelPath = baseRelPath });
        }

        private void RefreshFileTable()
        {
            if (_fileTable == null || _overwriteCheckBox == null) return;
            var clearText = LanguageManager.Instance.GetText("Editor.ImportRemoveItem", "清除");
            var displayItems = _items.Select(i =>
            {
                string finalPath = GetFinalPath(i);
                bool exists = _existingPaths.Contains(finalPath);
                return new FileItemDisplay
                {
                    FinalPath = finalPath,
                    IsExisting = exists && !_overwriteCheckBox.Checked,
                    Source = i,
                    CellLinks = new AntdUI.CellLink[]
                    {
                        new AntdUI.CellButton("remove", clearText, AntdUI.TTypeMini.Error)
                    }
                };
            }).ToList();

            _fileTable.DataSource = displayItems;
            UpdateButtonStates();
        }

        private void UpdateButtonStates()
        {
            _clearButton.Enabled = _items.Count > 0;
        }
    }
}
