using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace GDPackEditor
{
    /// <summary>
    /// DBR 数组编辑器独立窗体（MergeCompareDialog / ArzArcEditorForm 共用）。
    /// 核心抽象：读入 currentValue（数据来源，; 分隔），输出 Result（数据目标，; 分隔；取消=null）。
    /// </summary>
    public class ArrayEditorDialog : AntdUI.BaseForm
    {
        /// <summary>编辑结果（; 分隔），取消返回 null。</summary>
        public string Result { get; private set; }

        private readonly string _fieldName;
        private readonly Func<string, string> _fileDbrBrowser;

        private class ArrayEditorRow : INotifyPropertyChanged
        {
            private bool _checked;
            public bool Checked
            {
                get => _checked;
                set { _checked = value; OnPropertyChanged(nameof(Checked)); }
            }
            private string _value;
            public string Value
            {
                get => _value;
                set { _value = value; OnPropertyChanged(nameof(Value)); }
            }
            public AntdUI.CellLink[] CellLinks { get; set; }
            public event PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged(string name) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public ArrayEditorDialog(string fieldName, string currentValue, Func<string, string> fileDbrBrowser = null)
        {
            _fieldName = fieldName;
            _fileDbrBrowser = fileDbrBrowser;

            Text = string.Format(
                LanguageManager.Instance.GetText("CompareDialog.ArrayEditTitle", "数组: {0}"), fieldName);
            Size = new Size(680, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(245, 245, 245);

            bool isFileDbr = fileDbrBrowser != null;

            var mainPanel = new AntdUI.Panel { Dock = DockStyle.Fill, Back = Color.Transparent, Padding = new Padding(12) };

            // 表格
            var dgv = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = true,
                Bordered = true,
                MultipleRows = false,
                EditMode = AntdUI.TEditMode.DoubleClick,
                LostFocusClearSelection = false,
                Font = FontHelper.UiFont(9),
                AllowDrop = true,
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill,
                DragHandleSize = 24,
                DragHandleIconSize = 14,
                RowHeight = 30
            };
            dgv.Columns.Add(new AntdUI.ColumnCheck("Checked", "") { Width = "40" });
            dgv.Columns.Add(new AntdUI.ColumnSort() { Width = "30" });
            dgv.Columns.Add(new AntdUI.Column("Value",
                LanguageManager.Instance.GetText("Editor.ArrayItemValue", "数值")));
            if (isFileDbr)
            {
                dgv.Columns.Add(new AntdUI.Column("CellLinks",
                    LanguageManager.Instance.GetText("Editor.Operations", "操作")) { Width = "60", ColAlign = AntdUI.ColumnAlign.Center });
            }

            var rows = new BindingList<ArrayEditorRow>();
            if (!string.IsNullOrEmpty(currentValue))
            {
                foreach (var item in currentValue.Split(';'))
                    rows.Add(new ArrayEditorRow
                    {
                        Checked = false,
                        Value = item,
                        CellLinks = isFileDbr ? DbrFieldHelper.GetDbrBrowseLinks() : null
                    });
            }
            dgv.Binding(rows);

            if (isFileDbr)
            {
                dgv.CellButtonClick += (s, e) =>
                {
                    if (e.Record is not ArrayEditorRow row) return;
                    string picked = _fileDbrBrowser(row.Value);
                    if (picked != null)
                    {
                        row.Value = picked;
                        dgv.Refresh();
                    }
                };
            }

            // 缓存当前显示顺序（ColumnSort拖拽后会变化）
            int[] displayOrder = null;
            ArrayEditorRow selectedRow = null;
            dgv.SortRows += (s, ea) => { displayOrder = dgv.SortIndex(); };
            rows.ListChanged += (s, ea) => { displayOrder = null; };

            dgv.CellClick += (s, e) =>
            {
                int idx = e.RowIndex - 1;
                if (idx >= 0 && idx < rows.Count) selectedRow = rows[idx];
            };
            dgv.CellDoubleClick += (s, e) =>
            {
                int idx = e.RowIndex - 1;
                if (idx >= 0 && idx < rows.Count) selectedRow = rows[idx];
            };

            // 在 ColumnSort 列上绘制行号（主按钮蓝色背景+白字居中）
            dgv.CellPaintBegin += (s, e) =>
            {
                if (e.Column is AntdUI.ColumnSort && e.RowType == AntdUI.RowType.None)
                {
                    e.Handled = true;
                    var g = e.g;
                    g.Fill(AntdUI.Style.Db.Primary, e.Rect);
                    g.String(e.RowIndex.ToString(),
                        FontHelper.UiFont(8), ThemeManager.PrimaryColor,
                        e.Rect, AntdUI.FormatFlags.Center);
                }
            };

            dgv.SetRowStyle += (s, e) =>
            {
                if (e.RowType == AntdUI.RowType.None && e.RowIndex % 2 == 0)
                    return new AntdUI.Table.CellStyleInfo { BackColor = ThemeManager.OddRowBack };
                return null;
            };

            // 数组元素级校验：逗号 / 分号 / 换行 / 控制字符都会破坏数组结构（写包后丢数据或错位）→ 拦下并回滚
            dgv.CellEndEdit += (s, ea) =>
            {
                if (ea.Record is not ArrayEditorRow row) return true;
                string v = (ea.Value ?? row.Value ?? "").ToString();
                string bad = null;
                if (v.IndexOf(',') >= 0) bad = "半角逗号（,）";
                else if (v.IndexOf(';') >= 0) bad = "半角分号（;）";
                else if (v.IndexOfAny(new[] { '\r', '\n' }) >= 0) bad = "换行";
                else if (v.Any(char.IsControl)) bad = "控制字符";
                if (bad == null) return true;

                MessageBox.Show(this,
                    string.Format(LanguageManager.Instance.GetText("Editor.ArrayItemInvalid",
                        "数组元素里不能包含{0}（会破坏数组结构），请修改"), bad),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;   // 不写值，单元格回滚
            };

            // 按钮
            var deselectAllBtn = CreateToolButton("Editor.DeselectAll", "取消选择");
            var selectAllBtn = CreateToolButton("Editor.SelectAll", "全选");
            var addBtn = CreateToolButton("Editor.AddItem", "增加一行");
            var removeBtn = CreateToolButton("Editor.RemoveItem", "删除");
            var generateBtn = CreateToolButton("Editor.GenerateArray", "生成数组");
            var upBtn = CreateToolButton(null, "↑ " + LanguageManager.Instance.GetText("Common.Up", "上移"));
            var downBtn = CreateToolButton(null, "↓ " + LanguageManager.Instance.GetText("Common.Down", "下移"));

            selectAllBtn.Click += (s, ea) => { foreach (var r in rows) r.Checked = true; };
            deselectAllBtn.Click += (s, ea) => { foreach (var r in rows) r.Checked = false; };

            upBtn.Click += (s, ea) =>
            {
                if (selectedRow == null) return;
                int idx = rows.IndexOf(selectedRow);
                if (idx <= 0) return;
                rows[idx] = rows[idx - 1];
                rows[idx - 1] = selectedRow;
                dgv.SelectedIndex = idx;
            };
            downBtn.Click += (s, ea) =>
            {
                if (selectedRow == null) return;
                int idx = rows.IndexOf(selectedRow);
                if (idx < 0 || idx >= rows.Count - 1) return;
                rows[idx] = rows[idx + 1];
                rows[idx + 1] = selectedRow;
                dgv.SelectedIndex = idx + 2;
            };
            generateBtn.Click += (s, ea) =>
            {
                var values = ShowGenerateArrayDialog();
                if (values != null)
                {
                    rows.Clear();
                    foreach (var v in values)
                        rows.Add(new ArrayEditorRow { Checked = false, Value = v, CellLinks = isFileDbr ? DbrFieldHelper.GetDbrBrowseLinks() : null });
                }
            };
            removeBtn.Click += (s, ea) =>
            {
                var checkedRows = rows.Select((r, i) => new { r.Checked, Index = i })
                    .Where(x => x.Checked).OrderByDescending(x => x.Index).ToList();
                if (checkedRows.Count > 0)
                    foreach (var r in checkedRows) rows.RemoveAt(r.Index);
                else if (selectedRow != null)
                {
                    int idx = rows.IndexOf(selectedRow);
                    if (idx >= 0) rows.RemoveAt(idx);
                }
            };
            addBtn.Click += (s, ea) =>
            {
                var checkedIndices = rows.Select((r, i) => new { r.Checked, Index = i })
                    .Where(x => x.Checked).Select(x => x.Index).OrderByDescending(i => i).ToList();
                if (checkedIndices.Count > 0)
                {
                    int lastInserted = -1;
                    foreach (var idx in checkedIndices)
                    {
                        int insertAt = idx + 1;
                        rows.Insert(insertAt, new ArrayEditorRow { Checked = false, Value = "", CellLinks = isFileDbr ? DbrFieldHelper.GetDbrBrowseLinks() : null });
                        lastInserted = insertAt;
                    }
                    if (lastInserted >= 0)
                    {
                        dgv.SelectedIndex = lastInserted + 1;
                        dgv.EnterEditMode(lastInserted + 1, 2);
                    }
                }
                else if (selectedRow != null)
                {
                    int idx = rows.IndexOf(selectedRow);
                    int insertAt = idx + 1;
                    var newRow = new ArrayEditorRow { Checked = false, Value = "", CellLinks = isFileDbr ? DbrFieldHelper.GetDbrBrowseLinks() : null };
                    rows.Insert(insertAt, newRow);
                    selectedRow = newRow;
                    dgv.SelectedIndex = insertAt + 1;
                    dgv.EnterEditMode(insertAt + 1, 2);
                }
                else
                {
                    var newRow = new ArrayEditorRow { Checked = false, Value = "", CellLinks = isFileDbr ? DbrFieldHelper.GetDbrBrowseLinks() : null };
                    rows.Add(newRow);
                    selectedRow = newRow;
                    dgv.SelectedIndex = rows.Count;
                    dgv.EnterEditMode(rows.Count, 2);
                }
            };

            // GridPanel 栅格布局
            var grid = new AntdUI.GridPanel
            {
                Span = "fill;44:fill fill 20%_N 20%_N 20%_N;44:fill fill fill fill fill",
                Gap = 4,
                BackColor = Color.Transparent,
                Dock = DockStyle.Fill
            };
            grid.SetIndex(dgv, 0);
            grid.SetIndex(deselectAllBtn, 1);
            grid.SetIndex(selectAllBtn, 2);
            grid.SetIndex(addBtn, 6);
            grid.SetIndex(removeBtn, 7);
            grid.SetIndex(generateBtn, 8);
            grid.SetIndex(upBtn, 9);
            grid.SetIndex(downBtn, 10);
            grid.Controls.Add(dgv);
            grid.Controls.Add(deselectAllBtn);
            grid.Controls.Add(selectAllBtn);
            grid.Controls.Add(addBtn);
            grid.Controls.Add(removeBtn);
            grid.Controls.Add(generateBtn);
            grid.Controls.Add(upBtn);
            grid.Controls.Add(downBtn);

            mainPanel.Controls.Add(grid);

            // 底部按钮栏
            var bottomPanel = new AntdUI.Panel { Dock = DockStyle.Bottom, Height = 44, Back = Color.Transparent };
            var okBtn = new AntdUI.Button
            {
AutoSize = true,
                Text = LanguageManager.Instance.GetText("Common.OK", "确定"),
                Type = AntdUI.TTypeMini.Primary,
                Dock = DockStyle.Right, 
                Font = FontHelper.UiFont(9)
            };
            okBtn.Click += (s, ea) =>
            {
                int[] order = displayOrder;
                var sb = new System.Text.StringBuilder();
                bool first = true;
                int count = rows.Count;
                for (int i = 0; i < count; i++)
                {
                    int realIdx = order != null && i < order.Length ? order[i] : i;
                    var val = rows[realIdx].Value ?? "";
                    if (string.IsNullOrEmpty(val)) continue;
                    if (!first) sb.Append(';');
                    sb.Append(val.Trim());
                    first = false;
                }
                Result = sb.ToString();
                DialogResult = DialogResult.OK;
                Close();
            };
            var cancelBtn = new AntdUI.Button
            {
AutoSize = true,
                Text = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                Type = AntdUI.TTypeMini.Default,
                Dock = DockStyle.Right, 
                Font = FontHelper.UiFont(9)
            };
            cancelBtn.Click += (s, ea) => { DialogResult = DialogResult.Cancel; Close(); };
            bottomPanel.Controls.Add(cancelBtn);
            bottomPanel.Controls.Add(okBtn);

            mainPanel.Controls.Add(bottomPanel);
            Controls.Add(mainPanel);
        }

        private static AntdUI.Button CreateToolButton(string langKey, string fallback)
        {
            string text = string.IsNullOrEmpty(langKey)
                ? fallback
                : LanguageManager.Instance.GetText(langKey, fallback);
            return new AntdUI.Button { AutoSize = true, Text = text, BorderWidth = 1f, Font = FontHelper.UiFont(9) };
        }

        /// <summary>
        /// 生成数组对话框：前缀 + 基准值 + 公式(u/n 递推) + 取整方式 + 补位 + 后缀 + 个数，带实时预览。
        /// 递推语义：u = 上一项最终输出值（第一项取基准值），n = 序号（从 1 开始）。
        /// </summary>
        private string[] ShowGenerateArrayDialog()
        {
            string optional = LanguageManager.Instance.GetText("Common.Optional", "选填");
            string[] resultValues = null;

            var txtPrefix = new AntdUI.Input
            {
                PrefixText = LanguageManager.Instance.GetText("Editor.Prefix", "数值前缀") + "：",
                AllowClear = true
            };
            txtPrefix.Enter += (s, e) => txtPrefix.SelectAll();

            var txtStart = new AntdUI.Input
            {
                Text = "0",
                PrefixText = LanguageManager.Instance.GetText("Editor.BaseValue", "基准值[u]") + "：",
                AllowClear = true
            };
            txtStart.Enter += (s, e) => BeginInvoke(() => txtStart.Select(0, txtStart.Text.Length));

            // 公式行：输入框 + "示例▾"快捷输入 + 红色"?"帮助按钮（GridPanel 三列格子）
            var txtFormula = new AntdUI.Input
            {
                Text = "u + n",
                PrefixText = LanguageManager.Instance.GetText("Editor.Formula", "步进公式") + "：",
                PlaceholderText = "u * 1.06",
                AllowClear = true
            };
            txtFormula.Enter += (s, e) => BeginInvoke(() => txtFormula.Select(0, txtFormula.Text.Length));
            // 常用公式 ▾：与批量编辑弹窗同一公共来源（DbrArrayFormula.CommonFormulas）
            var ddCommonFormula = new AntdUI.Dropdown
            {
                Text = LanguageManager.Instance.GetText("Editor.BatchFormulaExamples", "示例"),
                Font = FontHelper.UiFont(9),
                ListAutoWidth = true, MaxCount = 13,
                ShowArrow = true,
                Type = AntdUI.TTypeMini.Primary
            };
            foreach (var (display, formula) in DbrArrayFormula.CommonFormulas)
                ddCommonFormula.Items.Add(new AntdUI.SelectItem(display, formula));
            ddCommonFormula.SelectedValueChanged += (s, e) =>
            {
                var v = ddCommonFormula.SelectedValue as string;
                if (!string.IsNullOrEmpty(v)) txtFormula.Text = v;
            };
            var btnFormulaHelp = new AntdUI.Button
            {
AutoSize = true,
                Text = "?",
                Type = AntdUI.TTypeMini.Error,
                Font = FontHelper.UiFont(9, FontStyle.Bold)
            };

            // 取整已移入公式（round/floor/ceiling），不再有独立取整下拉（2026-09 定案）

            var nudPadding = new AntdUI.InputNumber
            { Minimum = 0, Maximum = 10, Value = 0,
                PrefixText = LanguageManager.Instance.GetText("Editor.ZeroPadding", "数值补位") + string.Format("（{0}）：", optional) };
            nudPadding.Enter += (s, e) => BeginInvoke(() => nudPadding.Select(0, nudPadding.Text.Length));

            var txtSuffix = new AntdUI.Input
            {
                PrefixText = LanguageManager.Instance.GetText("Editor.Suffix", "数值后缀") + "：",
                AllowClear = true
            };
            txtSuffix.Enter += (s, e) => txtSuffix.SelectAll();

            var nudCount = new AntdUI.InputNumber
            { Minimum = 1, Maximum = int.MaxValue, Value = 26,
                PrefixText = LanguageManager.Instance.GetText("Editor.GenerateCount", "生成总数[n]") + "：" };
            nudCount.Enter += (s, e) => BeginInvoke(() => nudCount.Select(0, nudCount.Text.Length));

            var previewDivider = new AntdUI.Divider { BackColor = Color.Transparent };
            var lblPreviewTitle = new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("Editor.Preview", "预览"),
                AutoSize = true, BackColor = Color.Transparent,
                Padding = new Padding(0, 4, 0, 0)
            };
            var txtPreview = new AntdUI.Input
            {
                Multiline = true,
                ReadOnly = true,
                AutoScroll = true,
                WordWrap = true,
                Font = FontHelper.UiFont(10),
                Height = 160
            };

            // ===== 求值与格式化（实现已抽取到 DbrArrayFormula，批量编辑共用同一套）=====

            double ParseDouble(AntdUI.Input txt, double fallback)
                => DbrArrayFormula.ParseDouble(txt.Text, fallback);

            // 递推生成：v(i) = eval(公式, u=v(i-1) 的原始计算值, n=i)；精确递推，取整在公式里做。
            // 返回 false 表示公式无效；无实解(NaN/Infinity)的行以 ! 标记且不更新上一项。
            // 求值本体在 DbrArrayFormula.GenerateStep（与批量编辑共用），这里只负责拼前后缀与缩进。
            bool TryGenerateLines(string prefix, string suffix, string formula, double baseVal, int count, int pad, int maxRows, List<string> lines)
            {
                lines.Clear();
                var step = DbrArrayFormula.GenerateStep(formula, baseVal, count, pad, maxRows);
                if (step.FormulaInvalid) return false;
                foreach (string val in step.Values)
                    lines.Add(val == null ? "  !" : "  " + prefix + val + suffix);
                return true;
            }

            void UpdatePreview()
            {
                string formula = (txtFormula.Text ?? "").Trim();
                if (string.IsNullOrEmpty(formula))
                {
                    txtPreview.Text = LanguageManager.Instance.GetText("Editor.FormulaInvalid", "公式无效");
                    return;
                }
                formula = formula.Replace("^", "**");
                double baseVal = ParseDouble(txtStart, 0);
                int pad = (int)nudPadding.Value;
                int count = (int)nudCount.Value;
                var lines = new List<string>();
                if (!TryGenerateLines(txtPrefix.Text ?? "", txtSuffix.Text ?? "", formula, baseVal, count, pad, 500, lines))
                {
                    txtPreview.Text = LanguageManager.Instance.GetText("Editor.FormulaInvalid", "公式无效");
                    return;
                }
                txtPreview.Text = string.Join("\r\n", lines);
            }

            txtPrefix.TextChanged += (s, e) => UpdatePreview();
            txtStart.TextChanged += (s, e) => UpdatePreview();
            txtFormula.TextChanged += (s, e) => UpdatePreview();
            nudPadding.ValueChanged += (s, e) => UpdatePreview();
            nudPadding.TextChanged += (s, e) => UpdatePreview();
            txtSuffix.TextChanged += (s, e) => UpdatePreview();
            nudCount.ValueChanged += (s, e) => UpdatePreview();
            nudCount.TextChanged += (s, e) => UpdatePreview();

            // 行定义：前缀 / 基准值 / 生成总数[n] / 公式+?按钮(双列) / 补位 / 后缀 / 分隔 / 标题 / 预览
            // 7 个输入行固定 48px：GridPanel 将子控件拉伸到格子大小并内缩 Gap×2（Gap=2 ⇒ 控件实际 44px，全部等高）；
            // （取整已移入公式，原取整行删除 → 格子少一行）
            // 预览行 fill 弹性填充：默认恰为 160px，窗口缩放时随内容区伸缩。
            // 注：u（基准值）与 n（生成总数）均置于公式行上方，填写公式时可直观对照。
            // 公式行为三列：输入框(100%) + 示例▾(66px) + ?帮助(44px)
            const string GridSpan = "48:100%;48:100%;48:100%;48:100% 66 44;48:100%;48:100%;20:100%;24:100%;fill";
            var grid = new AntdUI.GridPanel
            {
                Span = GridSpan,
                Gap = 2,
                BackColor = Color.Transparent,
                Font = FontHelper.UiFont(10),
                Dock = DockStyle.Fill
            };
            grid.SetIndex(txtPrefix, 0);
            grid.SetIndex(txtStart, 1);
            grid.SetIndex(nudCount, 2);
            grid.SetIndex(txtFormula, 3);
            grid.SetIndex(ddCommonFormula, 4);
            grid.SetIndex(btnFormulaHelp, 5);
            grid.SetIndex(nudPadding, 6);
            grid.SetIndex(txtSuffix, 7);
            grid.SetIndex(previewDivider, 8);
            grid.SetIndex(lblPreviewTitle, 9);
            grid.SetIndex(txtPreview, 10);
            grid.Controls.Add(txtPrefix);
            grid.Controls.Add(txtStart);
            grid.Controls.Add(nudCount);
            grid.Controls.Add(txtFormula);
            grid.Controls.Add(ddCommonFormula);
            grid.Controls.Add(btnFormulaHelp);
            grid.Controls.Add(nudPadding);
            grid.Controls.Add(txtSuffix);
            grid.Controls.Add(previewDivider);
            grid.Controls.Add(lblPreviewTitle);
            grid.Controls.Add(txtPreview);

            // 帮助侧栏：横向分栏（左=编辑区 / 右=公式说明），? 按钮或分割条箭头切换右栏收展，实现边看说明边输入
            // 说明区用公共 Markdown 渲染控件（与「更新日志/关于」弹窗同一套，自带滚动）
            var helpPanel = new AntdUI.Panel { Dock = DockStyle.Fill, Back = Color.Transparent, Padding = new Padding(2) };
            var lblHelpTitle = new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("Editor.FormulaHelpTitle", "公式使用说明"),
                Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent,
                Padding = new Padding(0, 2, 0, 4), Font = FontHelper.UiFont(10)
            };
            var txtHelp = MarkdownViewer.CreateMarkdownControl(
                MarkdownViewer.LoadMarkdown(MarkdownViewer.HelpResourceSuffix("!FormulaHelpCN.md", "!FormulaHelpEN.md")),
                out _);
            txtHelp.Dock = DockStyle.Fill;
            helpPanel.Controls.Add(txtHelp);
            helpPanel.Controls.Add(lblHelpTitle); // Dock 反向布局：后加的标题行先处理，占据顶部

            var helpSplit = new ArrowSplitter
            {
                CollapsePanel = AntdUI.Splitter.ADCollapsePanel.Panel2,
                CollapsedTextKey = "Editor.CollapseHelp", // 展开态显示（点击将收起说明）
                ExpandedTextKey = "Editor.ExpandHelp",    // 收起态显示（点击将展开说明）
                Lazy = false,
                // 去掉分割条中央的深灰指示条（SplitterBack 默认银灰色 4px 条，按本实例生效，不影响主窗体/MOD合并分栏）
                SplitterBack = Color.Transparent,
                Width = 830,
                Height = 540
            };
            helpSplit.Panel1.Controls.Add(grid);
            helpSplit.Panel2.Controls.Add(helpPanel);

            // 初始对半：构造期直接设为半宽。100% 缩放下 Modal 定界与构造尺寸相同、不会触发 SizeChanged，
            // 必须显式设置一次（否则停留在 WinForms 构造期等比换算的默认位置，约 33%）；
            // 非 100% 时该值随 DpiLS 等比缩放、窗口自由缩放时按比例保持（FixedPanel=None），无需兜底重设，
            // 也不在 SizeChanged 中重设——避免用户拖动窗口时手动调好的分栏比例被重置。
            try { helpSplit.SplitterDistance = (helpSplit.Width - helpSplit.SplitterWidth) / 2; } catch { }

            // ? 按钮：切换右侧说明栏展开/收起（与分割条箭头等效）
            btnFormulaHelp.Click += (s, e) => { helpSplit.Panel2Collapsed = !helpSplit.Panel2Collapsed; };

            UpdatePreview();

            var config = new AntdUI.Modal.Config(
                this,
                string.Format(LanguageManager.Instance.GetText("Editor.GenerateArrayTitle", "{0} - 生成数组"), _fieldName),
                helpSplit)
            {
                // 注：AntdUI 2.4.2 中 Modal 初始宽度由内容控件（helpSplit.Width）决定，此处与 helpSplit.Width 同步
                Width = 830,
                // 自由调整窗口大小：内容 Dock=Fill 跟随缩放；最小高度保证 7 个固定行(48×7+20+24=380)不被挤压、预览行有余量
                Resizable = true,
                MinimumSize = new Size(600, 560),
                Padding = new Size(20, 16),
                OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                OkType = AntdUI.TTypeMini.Primary,
                MaskClosable = false,
                Draggable = true,
                OnOk = cfg =>
                {
                    string formula = (txtFormula.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(formula)) return false;
                    formula = formula.Replace("^", "**");
                    double baseVal = ParseDouble(txtStart, 0);
                    int padding = (int)nudPadding.Value;
                    int count = (int)nudCount.Value;

                    var list = new List<string>();
                    if (!TryGenerateLines(txtPrefix.Text ?? "", txtSuffix.Text ?? "", formula, baseVal, count, padding, 0, list))
                        return false; // 公式无效：预览区已提示，阻止关闭
                    resultValues = list.ToArray();
                    return true;
                }
            };
            AntdUI.Modal.open(config);
            return resultValues;
        }
    }
}
