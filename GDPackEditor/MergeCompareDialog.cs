using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Label = AntdUI.Label;

namespace GDPackEditor
{
    public class MergeCompareDialog : AntdUI.BaseForm
    {
        private readonly string _recordName;
        private readonly string[] _lines1;
        private readonly string[] _lines2;
        private readonly Dictionary<string, string> _dbr1;
        private readonly Dictionary<string, string> _dbr2;
        private readonly string _tempFilePath;
        private readonly Action<string> _onEdited;
        private readonly List<TemplateNode> _templates;
        private readonly string _pkg1Name;
        private readonly string _pkg2Name;

        private AntdUI.Tree _groupTree;
        private AntdUI.Table _fieldTable;
        private string _currentFilter = "__all__";
        private List<CompareFieldRow> _allRows = new List<CompareFieldRow>();
        private List<CompareFieldRow> _filteredRows = new List<CompareFieldRow>();
        private bool _isDirty;
        private TemplateNode _matchedTemplate;
        private Dictionary<string, TemplateNode> _templateDict;

        // 特化显示：最终值列粗体字体（复用实例避免渲染时反复创建）
        private static readonly Font FinalValueFont = FontHelper.UiFont(9, FontStyle.Bold);

        // 右键复制相关（AntdUI ContextMenuStrip）
        private AntdUI.IContextMenuStripItem[] _copyContextMenuItems;
        private int _copyRowIndex = -1;
        private string _copyColKey;

        // 左侧树面板切换相关（使用 ArrowSplitter）
        private ArrowSplitter _treeSplit;

        private class CompareFieldRow
        {
            public string FieldName { get; set; }
            public AntdUI.CellLink FieldNameCell { get; set; }
            public string FieldClass { get; set; }
            public string FieldType { get; set; }
            public string Value1 { get; set; }
            public string Value2 { get; set; }
            public string UseChoice { get; set; }
            public string FinalValue { get; set; }
            public bool IsOnlyIn1 { get; set; }
            public bool IsOnlyIn2 { get; set; }
            public bool IsDifferent => !string.Equals(Value1, Value2, StringComparison.OrdinalIgnoreCase);
            public bool IsSystemField => string.Equals(FieldName, "templateName", StringComparison.OrdinalIgnoreCase);
            public TemplateVariableInfo Tag { get; set; }
            public AntdUI.CellLink[] CellLinks { get; set; }
        }

        public MergeCompareDialog(
            string recordName,
            string[] lines1,
            string[] lines2,
            string tempFilePath,
            Action<string> onEdited,
            List<TemplateNode> templates,
            string pkg1Name = null,
            string pkg2Name = null)
        {
            _recordName = recordName;
            _lines1 = lines1 ?? Array.Empty<string>();
            _lines2 = lines2 ?? Array.Empty<string>();
            _tempFilePath = tempFilePath;
            _onEdited = onEdited;
            _templates = templates;
            _pkg1Name = pkg1Name;
            _pkg2Name = pkg2Name;

            _templateDict = DbrFieldHelper.BuildTemplateDict(templates);

            _dbr1 = TemplateHelper.ParseDbrLines(_lines1);
            _dbr2 = TemplateHelper.ParseDbrLines(_lines2);

            string tplName = null;
            if (_dbr1.TryGetValue("templateName", out string v1)) tplName = v1;
            else if (_dbr2.TryGetValue("templateName", out string v2)) tplName = v2;
            _matchedTemplate = DbrFieldHelper.MatchTemplate(_templateDict, tplName);

            InitializeUI();
            BuildFieldRows();
            BuildGroupTree();
            ApplyFilter();
        }

        private void InitializeUI()
        {
            Text = string.Format(
                LanguageManager.Instance.GetText("CompareDialog.Title", "对比编辑: {0}"),
                _recordName);
            Size = new Size(960, 640);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = ThemeManager.BgLayout;
            WindowState = FormWindowState.Maximized;

            var mainPanel = new AntdUI.Panel { Dock = DockStyle.Fill, Back = Color.Transparent, Padding = new Padding(10) };

            // 搜索栏
            var searchBar = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                Back = Color.Transparent
            };

            var searchInput = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(10),
                PrefixText = LanguageManager.Instance.GetText("Common.SearchLabel", "搜索："),
                AllowClear = true
            };
            searchInput.TextChanged += (s, e) =>
            {
                string filter = searchInput.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(filter))
                    ApplyFilter();
                else
                    ApplyFilterWithSearch(filter);
            };
            searchBar.Controls.Add(searchInput);

            // 提示文字（红色、居中）
            var hintPanel = new AntdUI.Panel { Dock = DockStyle.Top, Height = 28, Back = Color.Transparent };
            var hintLabel = new Label
            {
                Text = LanguageManager.Instance.GetText("CompareDialog.EditHint", "建议点击【差异字段】进行编辑"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = FontHelper.UiFont(9, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 80, 80)
            };
            hintPanel.Controls.Add(hintLabel);

            // 右侧内容区：使用 ArrowSplitter 实现左树+右表，支持点击折叠/展开
            var contentPanel = new AntdUI.Panel { Dock = DockStyle.Fill, Back = Color.Transparent };

            _groupTree = new AntdUI.Tree
            {
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(9)
            };
            _groupTree.SelectChanged += OnGroupSelected;

            _fieldTable = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = true,
                Bordered = true,
                VirtualMode = true,
                EnableHeaderResizing = true,
                GapCell = 4,
                EditMode = AntdUI.TEditMode.DoubleClick,
                LostFocusClearSelection = false,
                Font = FontHelper.UiFont(9),
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Auto,
                Gap = 4
            };
            _fieldTable.Columns.Add(new AntdUI.Column("FieldNameCell",
                LanguageManager.Instance.GetText("CompareDialog.ColFieldName", "字段名")) { SortOrder = true });
            _fieldTable.Columns.Add(new AntdUI.Column("FieldClass",
                LanguageManager.Instance.GetText("CompareDialog.ColFieldClass", "字段分类")));
            _fieldTable.Columns.Add(new AntdUI.Column("FieldType",
                LanguageManager.Instance.GetText("CompareDialog.ColFieldType", "数值类型")));

            // 包1值 / 包2值：直接绑定字符串属性（无 Render），AntdUI 自动 CellText + 溢出 Tooltip
            string colVal1Text = LanguageManager.Instance.GetText("CompareDialog.ColValue1", "包1值");
            if (!string.IsNullOrEmpty(_pkg1Name)) colVal1Text += $" ({_pkg1Name})";
            _fieldTable.Columns.Add(new AntdUI.Column("Value1", colVal1Text) { Width = "16%", LineBreak = false });

            string colVal2Text = LanguageManager.Instance.GetText("CompareDialog.ColValue2", "包2值");
            if (!string.IsNullOrEmpty(_pkg2Name)) colVal2Text += $" ({_pkg2Name})";
            _fieldTable.Columns.Add(new AntdUI.Column("Value2", colVal2Text) { Width = "16%", LineBreak = false });

            // 采用列放在最终采用值列前面
            var choiceCol = new AntdUI.Column("CellLinks",
                LanguageManager.Instance.GetText("CompareDialog.ColUse", "采用")) { Width = "auto", ColAlign = AntdUI.ColumnAlign.Center };
            _fieldTable.Columns.Add(choiceCol);

            // 最终采用值（fill 自动填充剩余宽度）
            var valueCol = new AntdUI.Column("FieldValue",
                LanguageManager.Instance.GetText("CompareDialog.ColFinal", "最终采用值")) { Width = "fill" };
            valueCol.Render = BuildValueCellRender;
            _fieldTable.Columns.Add(valueCol);

            _fieldTable.CellButtonClick += OnCellButtonClick;
            _fieldTable.SetRowStyle += OnTableSetRowStyle;
            _fieldTable.CellBeginEdit += OnFieldCellBeginEdit;
            _fieldTable.CellBeginEditInputStyle += OnFieldCellBeginEditInputStyle;
            _fieldTable.CellEndEdit += OnFieldCellEndEdit;

            // 右键菜单：复制单元格内容（AntdUI）
            _copyContextMenuItems = new AntdUI.IContextMenuStripItem[]
            {
                new AntdUI.ContextMenuStripItem(
                    LanguageManager.Instance.GetText("Common.Copy", "复制")) { Tag = "copy" }
            };

            _fieldTable.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var pt = _fieldTable.PointToClient(Cursor.Position);
                var cell = _fieldTable.HitTest(pt.X, pt.Y, out int iRow, out int iCel);
                if (cell == null || iRow < 1 || iRow > _filteredRows.Count) return;
                _copyRowIndex = iRow - 1;
                _copyColKey = cell.COLUMN?.Key;
                AntdUI.ContextMenuStrip.open(_fieldTable, it =>
                {
                    if ((it.Tag as string) == "copy") CopySelectedCell();
                }, _copyContextMenuItems);
            };

            // 布局：ArrowSplitter(P1=tree, P2=table)
            _treeSplit = new ArrowSplitter
            {
                Dock = DockStyle.Fill,
                CollapsePanel = AntdUI.Splitter.ADCollapsePanel.Panel1,
                CollapsedTextKey = "CompareDialog.HideTree",
                ExpandedTextKey = "CompareDialog.ShowTree",
                Lazy = false,
                Panel1MinSize = (int)(200 * DpiManager.Scale),
                Panel1MaxSize = (int)(200 * DpiManager.Scale)
            };
            _treeSplit.SplitterDistance = (int)(200 * DpiManager.Scale);
            _treeSplit.SplitPanelStateChanged += (s, e) =>
            {
                if (!e.Value)
                {
                    _treeSplit.Panel1MinSize = 0;
                    _treeSplit.SplitterDistance = 0;
                }
                else
                {
                    int fixedWidth = (int)(200 * DpiManager.Scale);
                    _treeSplit.Panel1MinSize = fixedWidth;
                    _treeSplit.SplitterDistance = fixedWidth;
                }
            };
            contentPanel.Controls.Add(_treeSplit);
            _treeSplit.Panel1.Controls.Add(_groupTree);
            _treeSplit.Panel2.Controls.Add(_fieldTable);

            // 窗口大小变化时钳位左侧分栏宽度在 [MinSize, MaxSize] 范围内
            this.SizeChanged += (s, e) =>
            {
                if (_treeSplit == null || _treeSplit.Panel1MinSize <= 0) return;
                int maxW = _treeSplit.Panel1MaxSize;
                if (maxW > 0)
                    _treeSplit.SplitterDistance = Math.Min(_treeSplit.SplitterDistance, maxW);
                _treeSplit.SplitterDistance = Math.Max(_treeSplit.SplitterDistance, _treeSplit.Panel1MinSize);
            };

            // 操作按钮栏（搜索框下方，靠右对齐）
            var actionRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = 38, Back = Color.Transparent };

            var resetBtn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("CompareDialog.ResetDefault", "恢复默认"),
                Type = AntdUI.TTypeMini.Default,
                Dock = DockStyle.Right, AutoSize = true, 
                Font = FontHelper.UiFont(9)
            };
            resetBtn.Click += (s, e) => ResetToDefault();

            var useAll2Btn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("CompareDialog.UseAll2", "全部用2"),
                Type = AntdUI.TTypeMini.Success,
                Dock = DockStyle.Right, AutoSize = true, 
                Font = FontHelper.UiFont(9)
            };
            useAll2Btn.Click += (s, e) => SetAllUse("2");

            var useAll1Btn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("CompareDialog.UseAll1", "全部用1"),
                Type = AntdUI.TTypeMini.Primary,
                Dock = DockStyle.Right, AutoSize = true, 
                Font = FontHelper.UiFont(9)
            };
            useAll1Btn.Click += (s, e) => SetAllUse("1");

            // actionRow 加入顺序：resetBtn(Right) → useAll1Btn(Right) → useAll2Btn(Right)
            // （右侧反向布局 ⇒ 视觉为 … | 恢复默认 | 全部用1 | 全部用2）
            actionRow.Controls.Add(resetBtn);
            actionRow.Controls.Add(useAll1Btn);
            actionRow.Controls.Add(useAll2Btn);

            // 底部按钮栏（仅保存/取消）
            var bottomPanel = new AntdUI.Panel { Dock = DockStyle.Bottom, Height = 50, Back = Color.Transparent };

            var saveBtn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Common.Save", "保存"),
                Type = AntdUI.TTypeMini.Primary,
                Dock = DockStyle.Right, AutoSize = true, 
                Font = FontHelper.UiFont(9)
            };
            saveBtn.Click += OnSaveClick;

            var cancelBtn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                Type = AntdUI.TTypeMini.Error,
                Dock = DockStyle.Right, AutoSize = true, 
                Font = FontHelper.UiFont(9)
            };
            cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            bottomPanel.Controls.Add(cancelBtn);
            bottomPanel.Controls.Add(saveBtn);

            // 主面板布局：bottomPanel(Bottom) → searchBar(Top) → hintPanel(Top) → actionRow(Top) → contentPanel(Fill)
            // WinForms: 后添加的 Top 控件先停靠（显示在最上面）
            mainPanel.Controls.Add(contentPanel);
            mainPanel.Controls.Add(actionRow);
            mainPanel.Controls.Add(searchBar);
            mainPanel.Controls.Add(hintPanel);
            mainPanel.Controls.Add(bottomPanel);
            Controls.Add(mainPanel);
        }

        private void BuildFieldRows()
        {
            _allRows.Clear();
            var allFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in _dbr1.Keys) allFieldNames.Add(k);
            foreach (var k in _dbr2.Keys) allFieldNames.Add(k);

            // 通过模板获取变量信息
            Dictionary<string, TemplateVariableInfo> varMap = null;
            if (_matchedTemplate != null)
            {
                var allVars = TemplateHelper.CollectAllVariables(_matchedTemplate, _dbr1);
                varMap = new Dictionary<string, TemplateVariableInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in allVars)
                {
                    if (v != null && !string.IsNullOrEmpty(v.Name))
                        varMap[v.Name] = v;
                }
            }

            foreach (var fieldName in allFieldNames)
            {
                bool in1 = _dbr1.ContainsKey(fieldName);
                bool in2 = _dbr2.ContainsKey(fieldName);
                string val1 = in1 ? _dbr1[fieldName] : "";
                string val2 = in2 ? _dbr2[fieldName] : "";

                string useDefault;
                if (in1 && in2)
                    useDefault = "2";   // 双包都有：默认采用包2（最终采用值列随之显示包2值）
                else if (in2)
                    useDefault = "2";
                else
                    useDefault = "1";   // 仅包1有：只能采用包1（包2无此字段，避免值被清空）

                TemplateVariableInfo tag = null;
                string fieldClass = "";
                string fieldType = "";
                if (varMap != null && varMap.TryGetValue(fieldName, out var tplVar))
                {
                    tag = tplVar;
                    fieldClass = tplVar.Class ?? "";
                    fieldType = tplVar.Type ?? "";
                }

                var row = new CompareFieldRow
                {
                    FieldName = fieldName,
                    FieldNameCell = DbrFieldHelper.CreateFieldNameCell(fieldName),
                    FieldClass = fieldClass,
                    FieldType = fieldType,
                    Value1 = val1,
                    Value2 = val2,
                    UseChoice = useDefault,
                    FinalValue = useDefault == "1" ? val1 : val2,
                    IsOnlyIn1 = in1 && !in2,
                    IsOnlyIn2 = !in1 && in2,
                    Tag = tag
                };

                if (row.IsOnlyIn2)
                {
                    row.UseChoice = "2";
                    row.FinalValue = val2;
                }

                _allRows.Add(row);
            }

            if (File.Exists(_tempFilePath))
            {
                var savedLines = File.ReadAllLines(_tempFilePath);
                var savedDict = TemplateHelper.ParseDbrLines(savedLines);
                foreach (var row in _allRows)
                {
                    if (savedDict.TryGetValue(row.FieldName, out string savedVal))
                    {
                        row.FinalValue = savedVal;
                        if (string.Equals(savedVal, row.Value1, StringComparison.OrdinalIgnoreCase))
                            row.UseChoice = "1";
                        else if (string.Equals(savedVal, row.Value2, StringComparison.OrdinalIgnoreCase))
                            row.UseChoice = "2";
                    }
                }
            }
        }

        private void BuildGroupTree()
        {
            _groupTree.Items.Clear();

            var allItem = new AntdUI.TreeItem(
                LanguageManager.Instance.GetText("CompareDialog.GroupAll", "全部字段") +
                $" ({_allRows.Count})")
            {
                Tag = "__all__"
            };
            _groupTree.Items.Add(allItem);

            int diffCount = _allRows.Count(r => r.IsDifferent);
            var diffItem = new AntdUI.TreeItem(
                LanguageManager.Instance.GetText("CompareDialog.GroupDiff", "差异字段") +
                $" ({diffCount})")
            {
                Tag = "__diff__"
            };
            _groupTree.Items.Add(diffItem);

            int only1Count = _allRows.Count(r => r.IsOnlyIn1);
            var only1Item = new AntdUI.TreeItem(
                LanguageManager.Instance.GetText("CompareDialog.GroupOnly1", "仅包1独有") +
                $" ({only1Count})")
            {
                Tag = "__only1__"
            };
            _groupTree.Items.Add(only1Item);

            int only2Count = _allRows.Count(r => r.IsOnlyIn2);
            var only2Item = new AntdUI.TreeItem(
                LanguageManager.Instance.GetText("CompareDialog.GroupOnly2", "仅包2独有") +
                $" ({only2Count})")
            {
                Tag = "__only2__"
            };
            _groupTree.Items.Add(only2Item);

            // 模板分组
            if (_matchedTemplate != null)
            {
                var groups = TemplateHelper.CollectGroups(_matchedTemplate, _dbr1);
                foreach (var g in groups)
                {
                    if (string.IsNullOrEmpty(g.Name)) continue;
                    int cnt = g.Variables.Count(v => _allRows.Any(r => r.FieldName == v.Name));
                    if (cnt == 0) continue;
                    var groupItem = new AntdUI.TreeItem($"{g.Name} ({cnt})")
                    {
                        Tag = "group:" + g.Name
                    };
                    _groupTree.Items.Add(groupItem);
                }
            }

            _groupTree.SelectItem = allItem;
        }

        private void OnGroupSelected(object sender, AntdUI.TreeSelectEventArgs e)
        {
            if (e.Item?.Tag is string tag)
            {
                _currentFilter = tag;
                ApplyFilter();
            }
        }

        private void ApplyFilter()
        {
            _filteredRows = _currentFilter switch
            {
                "__diff__" => _allRows.Where(r => r.IsDifferent || r.IsOnlyIn1 || r.IsOnlyIn2).ToList(),
                "__only1__" => _allRows.Where(r => r.IsOnlyIn1).ToList(),
                "__only2__" => _allRows.Where(r => r.IsOnlyIn2).ToList(),
                _ when _currentFilter.StartsWith("group:", StringComparison.Ordinal)
                    => FilterByGroup(_currentFilter),
                _ => _allRows.ToList()
            };
            RefreshTable(keepScroll: false);
        }

        private void ApplyFilterWithSearch(string searchText)
        {
            var baseRows = _currentFilter switch
            {
                "__diff__" => _allRows.Where(r => r.IsDifferent || r.IsOnlyIn1 || r.IsOnlyIn2),
                "__only1__" => _allRows.Where(r => r.IsOnlyIn1),
                "__only2__" => _allRows.Where(r => r.IsOnlyIn2),
                _ when _currentFilter.StartsWith("group:", StringComparison.Ordinal)
                    => FilterByGroup(_currentFilter).AsEnumerable(),
                _ => _allRows.AsEnumerable()
            };
            _filteredRows = baseRows.Where(r =>
                r.FieldName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0
                || (r.Value1 ?? "").IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0
                || (r.Value2 ?? "").IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0
            ).ToList();
            RefreshTable();
        }

        private List<CompareFieldRow> FilterByGroup(string filter)
        {
            if (_matchedTemplate == null) return new List<CompareFieldRow>();
            string grp = filter.Substring("group:".Length);
            var groups = TemplateHelper.CollectGroups(_matchedTemplate, _dbr1);
            var g = groups.FirstOrDefault(x => x.Name == grp);
            if (g == null) return new List<CompareFieldRow>();
            var fieldNames = new HashSet<string>(g.Variables.Select(v => v.Name));
            return _allRows.Where(r => fieldNames.Contains(r.FieldName)).ToList();
        }

        private void RefreshTable(bool keepScroll = true)
        {
            foreach (var row in _filteredRows)
            {
                var links = new List<AntdUI.CellLink>();

                if (row.IsSystemField)
                {
                    links.Add(new AntdUI.CellButton("locked_" + row.FieldName, "—", AntdUI.TTypeMini.Default));
                }
                else
                {
                    // 恒显包1值/包2值双钮（独有行亦显示，点击空侧则最终值置空）
                    bool is1 = row.UseChoice == "1";
                    var btn1 = new AntdUI.CellButton("use1_" + row.FieldName,
                        LanguageManager.Instance.GetText("CompareDialog.UsePkg1Btn", "包1值"), AntdUI.TTypeMini.Default);
                    if (is1)
                    {
                        btn1.DefaultBack = Pkg1Color;
                        btn1.Fore = Color.White;
                    }
                    links.Add(btn1);

                    bool is2 = row.UseChoice == "2";
                    var btn2 = new AntdUI.CellButton("use2_" + row.FieldName,
                        LanguageManager.Instance.GetText("CompareDialog.UsePkg2Btn", "包2值"), AntdUI.TTypeMini.Default);
                    if (is2)
                    {
                        btn2.DefaultBack = Pkg2Color;
                        btn2.Fore = Color.White;
                    }
                    links.Add(btn2);
                }

                row.CellLinks = links.ToArray();
            }

            int savedY = keepScroll ? _fieldTable.ScrollBar?.ValueY ?? 0 : 0;
            _fieldTable.DataSource = null;
            _fieldTable.DataSource = _filteredRows;
            if (keepScroll && savedY > 0 && _fieldTable.ScrollBar != null)
                _fieldTable.ScrollBar.ValueY = savedY;
        }

        private static Color Pkg1Color => ThemeManager.IsDark
            ? Color.FromArgb(100, 160, 255) : Color.FromArgb(22, 119, 255);
        private static Color Pkg2Color => ThemeManager.IsDark
            ? Color.FromArgb(80, 200, 120) : Color.FromArgb(0, 150, 60);
        private static Color ConflictColor => ThemeManager.IsDark
            ? Color.FromArgb(255, 120, 120) : Color.FromArgb(220, 80, 80);

        private object BuildValueCellRender(object value, object record, int index)
        {
            if (record is not CompareFieldRow row) return value;
            var cells = new List<AntdUI.ICell>();
            var v = row.Tag;
            string finalVal = row.FinalValue ?? "";

            // 根据字段类型显示操作按钮（参照编辑器模式）
            if (!row.IsSystemField)
            {
                // 全文按钮（始终显示，排在最前面）
                cells.Add(new AntdUI.CellButton("ftxt_" + row.FieldName,
                    LanguageManager.Instance.GetText("CompareDialog.FullText", "全文"), AntdUI.TTypeMini.Default));

                bool isArray = v != null && v.IsArray;
                bool isPicklist = DbrFieldHelper.IsPicklistField(v);

                // 无模板时按值特征推断：含分号视为数组
                if (v == null && finalVal.Contains(';'))
                    isArray = true;

                if (isArray)
                    cells.Add(new AntdUI.CellButton("arr_" + row.FieldName,
                        LanguageManager.Instance.GetText("Editor.ArrayEdit", "数组"), AntdUI.TTypeMini.Default));
                else if (isPicklist)
                    cells.Add(new AntdUI.CellButton("drop_" + row.FieldName,
                        LanguageManager.Instance.GetText("Editor.DropdownSelect", "下拉▼"), AntdUI.TTypeMini.Default));

                // 普通字段 / file_dbr 字段：不显示按钮，双击即可编辑（自行输入）
                if (cells.Count > 0)
                    cells.Add(new AntdUI.CellText("\u2002"));
            }

            // 值列文本：按来源关系着色
            string displayVal = row.FinalValue ?? "";
            var text = new AntdUI.CellText(displayVal)
            {
                Font = FinalValueFont
            };
            if (row.IsOnlyIn1)
                text.Fore = Pkg1Color;                     // 仅包1 = 蓝色
            else if (row.IsOnlyIn2)
                text.Fore = Pkg2Color;                     // 仅包2 = 绿色
            else if (row.IsDifferent)
                text.Fore = ConflictColor;                 // 差异 = 红色
            else
                text.Fore = Color.Gray;                     // 相同 = 灰色

            cells.Add(text);
            return cells.ToArray();
        }

        private AntdUI.Table.CellStyleInfo OnTableSetRowStyle(object sender, AntdUI.TableSetRowStyleEventArgs e)
        {
            if (e.RowType != AntdUI.RowType.None) return null;
            if (e.Record is not CompareFieldRow row) return null;

            if (row.IsSystemField)
                return new AntdUI.Table.CellStyleInfo { BackColor = Color.Transparent };

            return null;
        }

        private void OnCellButtonClick(object sender, AntdUI.TableButtonEventArgs e)
        {
            if (e.Btn?.Id == null) return;
            if (e.Record is not CompareFieldRow row) return;

            string id = e.Btn.Id;

            if (id.StartsWith("use1_", StringComparison.Ordinal))
            {
                // 独有行空侧也可采用：最终值取该侧值（无则空）
                row.UseChoice = "1";
                row.FinalValue = row.Value1 ?? "";
                _isDirty = true;
                RefreshTable();
            }
            else if (id.StartsWith("use2_", StringComparison.Ordinal))
            {
                row.UseChoice = "2";
                row.FinalValue = row.Value2 ?? "";
                _isDirty = true;
                RefreshTable();
            }
            else if (id.StartsWith("drop_", StringComparison.Ordinal))
            {
                var v = row.Tag;
                if (v == null) return;
                var cellPt = _fieldTable.PointToScreen(new Point(e.Rect.X, e.Rect.Bottom));
                DbrFieldHelper.ShowPicklistDropdown(this, cellPt, v, row.FinalValue ?? "", result =>
                {
                    row.FinalValue = result;
                    row.UseChoice = "__edit__";
                    _isDirty = true;
                    RefreshTable();
                });
            }
            else if (id.StartsWith("arr_", StringComparison.Ordinal))
            {
                ShowArrayEditorForField(row);
            }
            else if (id.StartsWith("ftxt_", StringComparison.Ordinal))
            {
                ShowFullTextEditDialog(row);
            }
        }

        private bool OnFieldCellBeginEdit(object sender, AntdUI.TableEventArgs e)
        {
            if (e.Record is not CompareFieldRow row) return false;
            if (row.IsSystemField) return false;

            // 仅最终值列可编辑，包1值/包2值列只读（通过列 Key 判断，避免索引基准问题）
            string key = e.Column?.Key;
            return key == "FieldValue";
        }

        private void OnFieldCellBeginEditInputStyle(object sender, AntdUI.TableBeginEditInputStyleEventArgs e)
        {
            if (e.Record is not CompareFieldRow row) return;
            if (e.Column?.Key != "FieldValue") return;
            // 默认填充原内容，便于修改
            e.Input.Text = row.FinalValue ?? "";
        }

        private bool OnFieldCellEndEdit(object sender, AntdUI.TableEndEditEventArgs e)
        {
            if (e.Record is not CompareFieldRow row) return false;
            if (row.IsSystemField) return false;

            string newVal = e.Value ?? "";

            // 值门禁（与批量编辑 / 单字段编辑同口径）：换行 / 逗号 / 控制字符会破坏 DBR 结构 → 拦下
            var issues = DbrValueGuard.Validate(newVal, new DbrValueContext { FieldName = row.FieldName });
            if (DbrValueGuard.HasError(issues))
            {
                var primary = DbrValueGuard.Primary(issues);
                MessageBox.Show(this, primary != null ? primary.Text : newVal,
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;   // 不写值（AntdUI 自动回滚单元格）
            }

            if (!string.Equals(newVal, row.FinalValue, StringComparison.Ordinal))
            {
                row.FinalValue = newVal;
                row.UseChoice = "__edit__";
                _isDirty = true;
                RefreshTable();
            }
            return true;
        }

        private void ShowArrayEditorForField(CompareFieldRow row)
        {
            var v = row.Tag;
            // Merge 场景无包文件树，不提供 file_dbr 浏览
            Func<string, string> browser = null;

            var dlg = new ArrayEditorDialog(row.FieldName, row.FinalValue ?? "", browser);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
            {
                row.FinalValue = dlg.Result;
                row.UseChoice = "__edit__";
                _isDirty = true;
                RefreshTable();
            }
        }

        private void ShowFullTextEditDialog(CompareFieldRow row)
        {
            string title = string.Format(
                LanguageManager.Instance.GetText("FullTextDialog.Title", "全文: {0}"),
                row.FieldName);

            var input = new AntdUI.Input
            {
                Multiline = true,
                WordWrap = true,
                AutoScroll = true,
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(9),
                Text = row.FinalValue ?? "",
                AcceptsTab = true
            };

            var wrapper = new AntdUI.Panel
            {
                Back = Color.Transparent,
                Width = 600,
                Height = 400
            };
            wrapper.Controls.Add(input);

            var config = new AntdUI.Modal.Config(this, title, wrapper)
            {
                Width = 600,
                MaskClosable = false,
                OkText = LanguageManager.Instance.GetText("Common.Save", "保存"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                OnOk = cfg =>
                {
                    row.FinalValue = input.Text ?? "";
                    row.UseChoice = "__edit__";
                    _isDirty = true;
                    RefreshTable();
                    return true;
                }
            };
            AntdUI.Modal.open(config);
        }

        private void SetAllUse(string choice)
        {
            foreach (var row in _allRows)
            {
                // 独有行空侧也采用：最终值取该侧值（无则空）
                row.UseChoice = choice;
                row.FinalValue = choice == "1" ? row.Value1 : row.Value2;
                row.FinalValue = row.FinalValue ?? "";
            }
            _isDirty = true;
            ApplyFilter();
        }

        private void ResetToDefault()
        {
            foreach (var row in _allRows)
            {
                if (row.IsOnlyIn1)
                {
                    row.UseChoice = "1";   // 仅包1有：只能采用包1
                    row.FinalValue = row.Value1;
                }
                else
                {
                    row.UseChoice = "2";   // 默认采用包2（双包都有/仅包2有）
                    row.FinalValue = row.Value2;
                }
                row.FinalValue = row.FinalValue ?? "";
            }
            _isDirty = true;
            ApplyFilter();
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            var lines = new List<string>();
            foreach (var row in _allRows)
            {
                if (!string.IsNullOrEmpty(row.FieldName) && row.FinalValue != null)
                    lines.Add($"{row.FieldName},{row.FinalValue},");
            }

            try
            {
                string dir = Path.GetDirectoryName(_tempFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllLines(_tempFilePath, lines.ToArray(), new UTF8Encoding(false));
                _onEdited?.Invoke(_recordName);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, LanguageManager.Instance.GetText("CompareDialog.SaveError", "保存失败: ") + ex.Message);
            }
        }

        private void CopySelectedCell()
        {
            if (_copyRowIndex < 0 || _copyRowIndex >= _filteredRows.Count) return;
            var row = _filteredRows[_copyRowIndex];
            string text = _copyColKey switch
            {
                "FieldNameCell" => row.FieldName,
                "FieldClass" => row.FieldClass,
                "FieldType" => row.FieldType,
                "Value1" => row.Value1 ?? "",
                "Value2" => row.Value2 ?? "",
                "FieldValue" => row.FinalValue ?? "",
                _ => ""
            };
            if (!string.IsNullOrEmpty(text))
            {
                try { Clipboard.SetText(text); }
                catch { }
            }
            _copyRowIndex = -1;
            _copyColKey = null;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult != DialogResult.OK && _isDirty)
            {
                var config = new AntdUI.Modal.Config(
                    LanguageManager.Instance.GetText("Common.Confirm", "确认"),
                    LanguageManager.Instance.GetText("CompareDialog.ConfirmDiscard", "有未保存的修改，是否放弃？"))
                {
                    OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                    CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                    MaskClosable = false
                };
                AntdUI.Modal.open(config);
            }
            base.OnFormClosing(e);
        }
    }
}
