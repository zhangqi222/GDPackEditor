using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Label = AntdUI.Label;
using Panel = AntdUI.Panel;

namespace GDPackEditor
{
    public class DiffTextDialog : AntdUI.BaseForm
    {
        private readonly string _recordName;
        private readonly string[] _lines1Original;
        private readonly string[] _lines2;
        private readonly string _tempFilePath;
        private readonly Action<string> _onEdited;
        private readonly Encoding _encoding;

        private ScintillaNET.Scintilla _editorLeft;
        private ScintillaNET.Scintilla _editorRight;
        private Label _legendLabel;
        private Label _infoLabel;
        private AntdUI.Radio _radioAuto;
        private AntdUI.Radio _radioOriginal;
        private AntdUI.Button _btnFilterAll;
        private AntdUI.Button _btnFilterDiff;
        private AntdUI.Button _btnFilterSame;
        private AntdUI.Segmented _viewSegmented;
        private AntdUI.Switch _wrapSwitch;
        private AntdUI.Input _searchInputLeft;
        private AntdUI.Input _searchInputRight;

        private bool _isDirty;
        private bool _isSyncingScroll;
        private bool _showWordHighlight = true;
        private DiffMatchMode _matchMode = DiffMatchMode.Auto;
        private DiffCompareResult _lastDiffResult;
        private int _currentFilterIndex = 0;
        private bool _isViewingPkg1 = false;
        private readonly System.Windows.Forms.Timer _debounceTimer;
        private int _lastLeftCaretLine = -1;
        private int _lastRightCaretLine = -1;
        private bool _activeEditorIsLeft = true;

        private static Color ColorDelete => ThemeManager.IsDark ? Color.FromArgb(80, 40, 40) : Color.FromArgb(255, 200, 200);
        private static Color ColorInsert => ThemeManager.IsDark ? Color.FromArgb(40, 80, 40) : Color.FromArgb(200, 255, 200);
        private static Color ColorModify => ThemeManager.IsDark ? Color.FromArgb(80, 80, 30) : Color.FromArgb(255, 255, 180);
        private static int LargeTextThreshold = 10000;

        private const int MarkerDelete = 24;
        private const int MarkerInsert = 25;
        private const int MarkerModify = 26;
        private const int MarkerActiveLine = 27;
        private const int IndicatorWordDiff = 10;
        private static Color ColorWordDiff => ThemeManager.IsDark ? Color.FromArgb(86, 156, 214) : Color.FromArgb(0, 120, 215);

        private const string SearchIconSvg = "<svg viewBox=\"0 0 1024 1024\"><path d=\"M909.6 854.5L649.9 594.8C690.2 542.7 712 479 712 412c0-80.2-31.3-155.4-87.9-212.1C567.4 143.3 492.2 112 412 112s-155.4 31.3-212.1 87.9C143.3 256.6 112 331.8 112 412c0 80.1 31.3 155.5 87.9 212.1C256.6 680.7 331.8 712 412 712c67 0 130.6-21.8 182.7-62l259.7 259.6c3.2 3.2 8.4 3.2 11.6 0l43.6-43.5c3.2-3.2 3.2-8.4 0-11.6zM570.4 570.4C528 612.7 471.8 636 412 636s-116-23.3-158.4-65.6C211.3 528 188 471.8 188 412s23.3-116 65.6-158.4C296 211.3 352.2 188 412 188s116 23.2 158.4 65.6C612.7 296 636 352.2 636 412s-23.3 116-65.6 158.4z\"/></svg>";

        public DiffTextDialog(
            string recordName,
            string[] lines1,
            string[] lines2,
            string tempFilePath,
            Action<string> onEdited,
            Encoding encoding = null)
        {
            _recordName = recordName;
            _lines1Original = lines1 ?? Array.Empty<string>();
            _lines2 = lines2 ?? Array.Empty<string>();
            _tempFilePath = tempFilePath;
            _onEdited = onEdited;
            _encoding = encoding ?? System.Text.Encoding.UTF8;

            _debounceTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                RunDiff();
            };

            InitializeUI();
        }

        private void InitializeUI()
        {
            Text = string.Format(
                LanguageManager.Instance.GetText("TextDiffDialog.Title", "对比编辑: {0}"),
                _recordName);
            Size = new Size(1100, 700);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = ThemeManager.BgLayout;
            WindowState = FormWindowState.Maximized;

            var mainPanel = new Panel { Dock = DockStyle.Fill, Back = Color.Transparent, Padding = new Padding(10) };

            // ===================== 顶部工具栏 =====================
            var topBar = new Panel { Dock = DockStyle.Top, Height = 36, Back = Color.Transparent };

            // 匹配模式
            var matchModePanel = new Panel { Dock = DockStyle.Left, Width = 200, Back = Color.Transparent };
            _radioAuto = new AntdUI.Radio
            {
                Text = LanguageManager.Instance.GetText("TextDiffDialog.AutoMatch", "自动对比"),
                Checked = true,
                Dock = DockStyle.Left,
                AutoSize = true,
                Font = FontHelper.UiFont(8),
                Padding = new Padding(0, 8, 0, 0)
            };
            _radioAuto.CheckedChanged += (s, e) => { if (_radioAuto.Checked) { _matchMode = DiffMatchMode.Auto; RunDiff(); } };
            _radioOriginal = new AntdUI.Radio
            {
                Text = LanguageManager.Instance.GetText("TextDiffDialog.OriginalMatch", "原始对比"),
                Dock = DockStyle.Left,
                AutoSize = true,
                Font = FontHelper.UiFont(8),
                Padding = new Padding(0, 8, 0, 0)
            };
            _radioOriginal.CheckedChanged += (s, e) => { if (_radioOriginal.Checked) { _matchMode = DiffMatchMode.Original; RunDiff(); } };
            matchModePanel.Controls.Add(_radioOriginal);
            matchModePanel.Controls.Add(_radioAuto);

            // 过滤按钮组
            var filterPanel = new Panel { Dock = DockStyle.Left, Width = 280, Back = Color.Transparent };
            var filterLabel = new Label
            {
                Text = LanguageManager.Instance.GetText("TextDiffDialog.Filter", "显示："),
                Dock = DockStyle.Left, AutoSize = true,
                Font = FontHelper.UiFont(8), ForeColor = Color.Gray,
                Padding = new Padding(10, 8, 0, 0)
            };
            _btnFilterAll = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("TextDiffDialog.FilterAll", "全部"),
                Type = AntdUI.TTypeMini.Primary,
                Dock = DockStyle.Left, AutoSize = true, 
                Font = FontHelper.UiFont(9)
            };
            _btnFilterAll.Click += (s, e) => SetFilter(0);
            _btnFilterDiff = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("TextDiffDialog.FilterDiff", "仅差别"),
                Type = AntdUI.TTypeMini.Default,
                Dock = DockStyle.Left, AutoSize = true, 
                Font = FontHelper.UiFont(9)
            };
            _btnFilterDiff.Click += (s, e) => SetFilter(1);
            _btnFilterSame = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("TextDiffDialog.FilterSame", "仅相同"),
                Type = AntdUI.TTypeMini.Default,
                Dock = DockStyle.Left, AutoSize = true, 
                Font = FontHelper.UiFont(9)
            };
            _btnFilterSame.Click += (s, e) => SetFilter(2);
            filterPanel.Controls.Add(_btnFilterSame);
            filterPanel.Controls.Add(_btnFilterDiff);
            filterPanel.Controls.Add(_btnFilterAll);
            filterPanel.Controls.Add(filterLabel);

            // 自动换行开关
            var wrapPanel = new Panel { Dock = DockStyle.Left, Width = 150, Back = Color.Transparent };
            var wrapLabel = new Label
            {
                Text = LanguageManager.Instance.GetText("TextDiffDialog.WordWrap", "自动换行"),
                Dock = DockStyle.Left, AutoSize = true,
                Font = FontHelper.UiFont(8), ForeColor = Color.Gray,
                Padding = new Padding(6, 8, 0, 0)
            };
            _wrapSwitch = new AntdUI.Switch
            {
                Dock = DockStyle.Left,
                Size = new Size(48, 22),
                Padding = new Padding(8, 6, 0, 0)
            };
            _wrapSwitch.CheckedChanged += (s, e) =>
            {
                var mode = _wrapSwitch.Checked ? ScintillaNET.WrapMode.Word : ScintillaNET.WrapMode.None;
                _editorLeft.WrapMode = mode;
                _editorRight.WrapMode = mode;
            };
            wrapPanel.Controls.Add(wrapLabel);
            wrapPanel.Controls.Add(_wrapSwitch);

            // 右侧信息
            _legendLabel = new Label
            {
                Text = LanguageManager.Instance.GetText("TextDiffDialog.Legend",
                    "■ 红=删除  ■ 绿=新增  ■ 黄=修改"),
                Dock = DockStyle.Right, AutoSize = true,
                Font = FontHelper.UiFont(8), ForeColor = Color.Gray,
                Padding = new Padding(0, 8, 10, 0)
            };
            _infoLabel = new Label
            {
                Text = "", Dock = DockStyle.Right, AutoSize = true,
                Font = FontHelper.UiFont(8), ForeColor = Color.Gray,
                Padding = new Padding(0, 8, 10, 0)
            };
            topBar.Controls.Add(_legendLabel);
            topBar.Controls.Add(_infoLabel);
            topBar.Controls.Add(wrapPanel);
            topBar.Controls.Add(filterPanel);
            topBar.Controls.Add(matchModePanel);

            // ===================== 左侧编辑器区 =====================
            _editorLeft = CreateScintillaEditor(true);
            var leftSearchBar = CreateSearchBar(_editorLeft, out _searchInputLeft);

            var leftHeader = new Label
            {
                Text = LanguageManager.Instance.GetText("TextDiffDialog.LeftTitle", "1 基准（可编辑）"),
                Dock = DockStyle.Top, Height = 22,
                Font = FontHelper.UiFont(9), ForeColor = ThemeManager.Text,
                Padding = new Padding(6, 0, 0, 0)
            };

            var leftSegmented = new AntdUI.Segmented
            {
                Dock = DockStyle.Top,
                Height = 30,
                Font = FontHelper.UiFont(8),
                Enabled = false
            };
            leftSegmented.Items.Add(new AntdUI.SegmentedItem().SetText(
                LanguageManager.Instance.GetText("TextDiffDialog.ViewSegmentedPkg1", "包1原始")));

            var leftPanel = new Panel { Dock = DockStyle.Fill, Back = Color.Transparent };
            leftPanel.Controls.Add(_editorLeft);
            leftPanel.Controls.Add(leftSearchBar);
            leftPanel.Controls.Add(leftSegmented);
            leftPanel.Controls.Add(leftHeader);

            // ===================== 右侧编辑器区 =====================
            _editorRight = CreateScintillaEditor(false);
            var rightSearchBar = CreateSearchBar(_editorRight, out _searchInputRight);

            var rightHeader = new Label
            {
                Text = LanguageManager.Instance.GetText("TextDiffDialog.RightTitle", "2 参考（只读）"),
                Dock = DockStyle.Top, Height = 22,
                Font = FontHelper.UiFont(9), ForeColor = ThemeManager.Text,
                Padding = new Padding(6, 0, 0, 0)
            };

            _viewSegmented = new AntdUI.Segmented
            {
                Dock = DockStyle.Top,
                Height = 30,
                Font = FontHelper.UiFont(8)
            };
            _viewSegmented.Items.Add(new AntdUI.SegmentedItem().SetText(
                LanguageManager.Instance.GetText("TextDiffDialog.ViewSegmentedPkg2", "包2文件")));
            _viewSegmented.Items.Add(new AntdUI.SegmentedItem().SetText(
                LanguageManager.Instance.GetText("TextDiffDialog.ViewSegmentedPkg1", "包1原始")));
            _viewSegmented.SelectIndexChanged += OnViewSegmentedChanged;

            var rightPanel = new Panel { Dock = DockStyle.Fill, Back = Color.Transparent };
            rightPanel.Controls.Add(_editorRight);
            rightPanel.Controls.Add(rightSearchBar);
            rightPanel.Controls.Add(_viewSegmented);
            rightPanel.Controls.Add(rightHeader);

            // ===================== 事件绑定 =====================
            _editorLeft.UpdateUI += OnLeftUpdateUI;
            _editorRight.UpdateUI += OnRightUpdateUI;
            _editorLeft.TextChanged += (s, e) =>
            {
                _isDirty = true;
                _showWordHighlight = false;
                ClearWordHighlights(_editorLeft);
                ClearWordHighlights(_editorRight);
                _debounceTimer.Stop();
                _debounceTimer.Start();
            };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeManager.BgLayout,
                SplitterWidth = 5,
                IsSplitterFixed = false,
                FixedPanel = FixedPanel.None,
                Panel1 = { BackColor = Color.Transparent },
                Panel2 = { BackColor = Color.Transparent },
                SplitterIncrement = 10
            };
            split.Panel1.Controls.Add(leftPanel);
            split.Panel2.Controls.Add(rightPanel);

            mainPanel.Controls.Add(split);
            mainPanel.Controls.Add(topBar);

            // ===================== 底部按钮栏 =====================
            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 42, Back = Color.Transparent };

            var resetBtn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("TextDiffDialog.Reset", "重置为原始包1"),
                Type = AntdUI.TTypeMini.Default, Dock = DockStyle.Left, AutoSize = true, 
                Font = FontHelper.UiFont(9)
            };
            resetBtn.Click += OnResetClick;

            var saveBtn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Common.Save", "保存"),
                Type = AntdUI.TTypeMini.Primary, Dock = DockStyle.Right, AutoSize = true, 
                Font = FontHelper.UiFont(9)
            };
            saveBtn.Click += OnSaveClick;

            var cancelBtn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                Type = AntdUI.TTypeMini.Error, Dock = DockStyle.Right, AutoSize = true, 
                Font = FontHelper.UiFont(9)
            };
            cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            bottomPanel.Controls.Add(cancelBtn);
            bottomPanel.Controls.Add(saveBtn);
            bottomPanel.Controls.Add(resetBtn);

            mainPanel.Controls.Add(bottomPanel);
            Controls.Add(mainPanel);

            this.Shown += (s, e) =>
            {
                if (split.Width > 100)
                {
                    split.Panel1MinSize = 200;
                    split.Panel2MinSize = 200;
                    split.SplitterDistance = split.Width / 2;
                }
                LoadContent();
                RunDiff();
            };
        }

        // ===================== 创建查找栏 =====================
        private Panel CreateSearchBar(ScintillaNET.Scintilla editor, out AntdUI.Input searchInput)
        {
            var searchBar = new Panel { Dock = DockStyle.Top, Height = 32, Back = Color.Transparent };

            var input = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(8),
                PrefixSvg = SearchIconSvg,
                PrefixFore = Color.Gray,
                AllowClear = true,
                PlaceholderText = LanguageManager.Instance.GetText("TextDiffDialog.SearchPlaceholder", "查找...")
            };
            input.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    FindNext(editor, input.Text);
                    e.SuppressKeyPress = true;
                }
            };

            var btnNext = new AntdUI.Button
            {
AutoSize = true,
                Text = "\u25BC",
                Type = AntdUI.TTypeMini.Default,
                Dock = DockStyle.Right, 
                Font = FontHelper.UiFont(9)
            };
            btnNext.Click += (s, e) => FindNext(editor, input.Text);

            var btnPrev = new AntdUI.Button
            {
AutoSize = true,
                Text = "\u25B2",
                Type = AntdUI.TTypeMini.Default,
                Dock = DockStyle.Right, 
                Font = FontHelper.UiFont(9)
            };
            btnPrev.Click += (s, e) => FindPrev(editor, input.Text);

            searchBar.Controls.Add(input);
            searchBar.Controls.Add(btnNext);
            searchBar.Controls.Add(btnPrev);
            searchInput = input;
            return searchBar;
        }

        // ===================== 查找功能 =====================
        private void FindNext(ScintillaNET.Scintilla editor, string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return;
            editor.SearchFlags = ScintillaNET.SearchFlags.None;
            editor.TargetStart = editor.CurrentPosition;
            editor.TargetEnd = editor.TextLength;
            if (editor.SearchInTarget(keyword) >= 0)
            {
                editor.SetSel(editor.TargetStart, editor.TargetEnd);
                editor.ScrollCaret();
            }
            else
            {
                editor.TargetStart = 0;
                editor.TargetEnd = editor.CurrentPosition;
                if (editor.SearchInTarget(keyword) >= 0)
                {
                    editor.SetSel(editor.TargetStart, editor.TargetEnd);
                    editor.ScrollCaret();
                }
            }
        }

        private void FindPrev(ScintillaNET.Scintilla editor, string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return;
            editor.SearchFlags = ScintillaNET.SearchFlags.None;
            editor.TargetStart = editor.CurrentPosition;
            editor.TargetEnd = 0;
            if (editor.SearchInTarget(keyword) >= 0)
            {
                editor.SetSel(editor.TargetStart, editor.TargetEnd);
                editor.ScrollCaret();
            }
            else
            {
                editor.TargetStart = editor.TextLength;
                editor.TargetEnd = editor.CurrentPosition;
                if (editor.SearchInTarget(keyword) >= 0)
                {
                    editor.SetSel(editor.TargetStart, editor.TargetEnd);
                    editor.ScrollCaret();
                }
            }
        }

        // ===================== 创建编辑器 =====================
        private ScintillaNET.Scintilla CreateScintillaEditor(bool editable)
        {
            var scintilla = new ScintillaNET.Scintilla
            {
                Dock = DockStyle.Fill,
                TabWidth = 4,
                UseTabs = false,
                TabIndents = true,
                WrapMode = ScintillaNET.WrapMode.None,
                ReadOnly = !editable,
                ExtraAscent = 2,
                ExtraDescent = 2,
                Technology = ScintillaNET.Technology.Default,  // GDI 渲染（定稿）：DirectWrite 模式下缺字形的字体回退常落日文字形，GDI 走字体链接回退宋体无此问题
                FontQuality = ScintillaNET.FontQuality.LcdOptimized   // ClearType 抗锯齿：GDI 下的最佳清晰度（仅 GDI 生效，DirectWrite 模式忽略此项）
            };

            scintilla.Styles[ScintillaNET.Style.Default].Font = FontHelper.MonoFontName;
            scintilla.Styles[ScintillaNET.Style.Default].Size = (int)(10f * DpiManager.Scale);   // 与编辑器/日志口径一致（跟随自动缩放开关）
            scintilla.Styles[ScintillaNET.Style.Default].BackColor = ThemeManager.EditorBack;
            scintilla.Styles[ScintillaNET.Style.Default].ForeColor = ThemeManager.EditorFore;
            scintilla.StyleClearAll();
            scintilla.Margins[0].Type = ScintillaNET.MarginType.Number;
            scintilla.Margins[0].Width = (int)(40 * DpiManager.Scale);   // 同上：跟随自动缩放开关
            // 行号样式与边距背景统一走 ScintillaSyntax（此前此处单独设样式，两套写法容易分叉）
            ScintillaSyntax.SetLineNumber(scintilla);

            scintilla.BackColor = ThemeManager.EditorBack;
            scintilla.ForeColor = ThemeManager.EditorFore;
            scintilla.CaretForeColor = ThemeManager.EditorFore;
            scintilla.CaretLineBackColor = ThemeManager.EditorCurrentLine;
            scintilla.SelectionBackColor = ThemeManager.EditorSelectionBack;
            scintilla.SelectionTextColor = ThemeManager.EditorSelectionFore;
            scintilla.SelectionAdditionalBackColor = ThemeManager.EditorSelectionBack;
            scintilla.SelectionAdditionalTextColor = ThemeManager.EditorSelectionFore;
            scintilla.SelectionInactiveBackColor = ThemeManager.EditorSelectionBack;
            scintilla.SelectionInactiveTextColor = ThemeManager.EditorSelectionFore;

            scintilla.CaretLineBackColor = ThemeManager.EditorCurrentLine;

            scintilla.Markers[MarkerDelete].Symbol = ScintillaNET.MarkerSymbol.Background;
            scintilla.Markers[MarkerDelete].SetBackColor(ColorDelete);
            scintilla.Markers[MarkerInsert].Symbol = ScintillaNET.MarkerSymbol.Background;
            scintilla.Markers[MarkerInsert].SetBackColor(ColorInsert);
            scintilla.Markers[MarkerModify].Symbol = ScintillaNET.MarkerSymbol.Background;
            scintilla.Markers[MarkerModify].SetBackColor(ColorModify);

            scintilla.Markers[MarkerActiveLine].Symbol = ScintillaNET.MarkerSymbol.Background;
            scintilla.Markers[MarkerActiveLine].SetBackColor(
                ThemeManager.IsDark ? Color.FromArgb(60, 90, 140) : Color.FromArgb(200, 220, 255));

            scintilla.IndicatorCurrent = IndicatorWordDiff;
            scintilla.Indicators[IndicatorWordDiff].Style = ScintillaNET.IndicatorStyle.FullBox;
            scintilla.Indicators[IndicatorWordDiff].Under = true;
            scintilla.Indicators[IndicatorWordDiff].ForeColor = ColorWordDiff;

            return scintilla;
        }

        // ===================== 内容加载 =====================
        private void LoadContent()
        {
            string text1;
            if (File.Exists(_tempFilePath))
                text1 = File.ReadAllText(_tempFilePath, _encoding);
            else
                text1 = string.Join("\r\n", _lines1Original);

            string text2 = string.Join("\r\n", _lines2);

            _editorLeft.Text = text1;
            bool ro = _editorRight.ReadOnly;
            _editorRight.ReadOnly = false;
            _editorRight.Text = text2;
            _editorRight.ReadOnly = ro;

            ScintillaSyntax.Apply(_editorLeft, _recordName, FontHelper.MonoFontName, 10);
            ScintillaSyntax.Apply(_editorRight, _recordName, FontHelper.MonoFontName, 10);

            // Apply 内部调用 StyleClearAll 会清空 diff 标记行（24-27 槽）的背景色，此处重新应用
            ApplyDiffMarkerColors(_editorLeft);
            ApplyDiffMarkerColors(_editorRight);

            _editorLeft.SetSavePoint();
            _isDirty = false;
        }

        private void ApplyDiffMarkerColors(ScintillaNET.Scintilla ed)
        {
            ed.Markers[MarkerDelete].SetBackColor(ColorDelete);
            ed.Markers[MarkerInsert].SetBackColor(ColorInsert);
            ed.Markers[MarkerModify].SetBackColor(ColorModify);
            ed.Markers[MarkerActiveLine].SetBackColor(
                ThemeManager.IsDark ? Color.FromArgb(60, 90, 140) : Color.FromArgb(200, 220, 255));
        }

        private void SetFilter(int index)
        {
            _currentFilterIndex = index;
            _btnFilterAll.Type = index == 0 ? AntdUI.TTypeMini.Primary : AntdUI.TTypeMini.Default;
            _btnFilterDiff.Type = index == 1 ? AntdUI.TTypeMini.Primary : AntdUI.TTypeMini.Default;
            _btnFilterSame.Type = index == 2 ? AntdUI.TTypeMini.Primary : AntdUI.TTypeMini.Default;
            ApplyDisplayFilter();
        }

        // ===================== Diff 计算与应用 =====================
        private void RunDiff()
        {
            ClearAllMarkers(_editorLeft);
            ClearAllMarkers(_editorRight);
            ClearWordHighlights(_editorLeft);
            ClearWordHighlights(_editorRight);

            string text1 = _editorLeft.Text;
            string text2 = _editorRight.Text;

            if (text1.Length > LargeTextThreshold || text2.Length > LargeTextThreshold)
            {
                RunDiffBackground(text1, text2);
                return;
            }

            var diffResult = TextDiffEngine.ComputeDiff(text1, text2, _matchMode);
            _lastDiffResult = diffResult;
            ApplyDiffResult(diffResult);
        }

        private async void RunDiffBackground(string text1, string text2)
        {
            try
            {
                var diffResult = await Task.Run(() => TextDiffEngine.ComputeDiff(text1, text2, _matchMode));

                _lastDiffResult = diffResult;
                ApplyDiffResult(diffResult);
            }
            catch (Exception ex)
            {
                // async void 的异常不能穿透事件循环：大文本比较/着色失败时保留原内容，只记日志
                LogService.Error($"[diff] background compare failed: {ex.Message}");
            }
        }

        private void ApplyDiffResult(DiffCompareResult diffResult)
        {
            ApplyMarkersFromDiff(diffResult);
            ApplyDisplayFilter();
            UpdateInfoLabel();
        }

        private void ApplyMarkersFromDiff(DiffCompareResult diffResult)
        {
            ClearAllMarkers(_editorLeft);
            ClearAllMarkers(_editorRight);

            int maxLeft = Math.Min(diffResult.LeftLines.Count, _editorLeft.Lines.Count);
            for (int i = 0; i < maxLeft; i++)
            {
                var leftLine = diffResult.LeftLines[i];
                switch (leftLine.Type)
                {
                    case DiffLineType.Deleted:
                        _editorLeft.Lines[i].MarkerAdd(MarkerDelete);
                        break;
                    case DiffLineType.Modified:
                        _editorLeft.Lines[i].MarkerAdd(MarkerModify);
                        break;
                }
            }
            int maxRight = Math.Min(diffResult.RightLines.Count, _editorRight.Lines.Count);
            for (int i = 0; i < maxRight; i++)
            {
                var rightLine = diffResult.RightLines[i];
                switch (rightLine.Type)
                {
                    case DiffLineType.Inserted:
                        _editorRight.Lines[i].MarkerAdd(MarkerInsert);
                        break;
                    case DiffLineType.Modified:
                        _editorRight.Lines[i].MarkerAdd(MarkerModify);
                        break;
                }
            }

            if (_showWordHighlight && _matchMode == DiffMatchMode.Auto)
                ApplyWordHighlights(diffResult);
        }

        private void ApplyWordHighlights(DiffCompareResult diffResult)
        {
            ClearWordHighlights(_editorLeft);
            ClearWordHighlights(_editorRight);

            if (_lastDiffResult == null) return;

            for (int i = 0; i < _lastDiffResult.LeftLines.Count; i++)
            {
                var leftLine = _lastDiffResult.LeftLines[i];
                if (leftLine.Type != DiffLineType.Modified || leftLine.SubPieces == null) continue;
                if (i >= _editorLeft.Lines.Count) continue;
                int lineStart = _editorLeft.Lines[i].Position;
                foreach (var piece in leftLine.SubPieces)
                {
                    if (!piece.IsDifferent) continue;
                    int start = lineStart + piece.Start;
                    if (start >= 0 && start + piece.Length <= _editorLeft.TextLength)
                        _editorLeft.IndicatorFillRange(start, piece.Length);
                }
            }
            for (int i = 0; i < _lastDiffResult.RightLines.Count; i++)
            {
                var rightLine = _lastDiffResult.RightLines[i];
                if (rightLine.Type != DiffLineType.Modified || rightLine.SubPieces == null) continue;
                if (i >= _editorRight.Lines.Count) continue;
                int lineStart = _editorRight.Lines[i].Position;
                foreach (var piece in rightLine.SubPieces)
                {
                    if (!piece.IsDifferent) continue;
                    int start = lineStart + piece.Start;
                    if (start >= 0 && start + piece.Length <= _editorRight.TextLength)
                        _editorRight.IndicatorFillRange(start, piece.Length);
                }
            }
        }

        private void ClearWordHighlights(ScintillaNET.Scintilla editor)
        {
            if (editor.TextLength > 0)
                editor.IndicatorClearRange(0, editor.TextLength);
        }

        private void ClearAllMarkers(ScintillaNET.Scintilla editor)
        {
            editor.MarkerDeleteAll(MarkerDelete);
            editor.MarkerDeleteAll(MarkerInsert);
            editor.MarkerDeleteAll(MarkerModify);
            editor.MarkerDeleteAll(MarkerActiveLine);
        }

        private void ApplyDisplayFilter()
        {
            if (_lastDiffResult == null) return;

            if (_currentFilterIndex == 0)
            {
                ShowAllLines();
                return;
            }

            var leftLines = _lastDiffResult.LeftLines;
            var rightLines = _lastDiffResult.RightLines;
            int count = Math.Min(leftLines.Count, rightLines.Count);
            int editorCount = _editorLeft.Lines.Count;

            ShowAllLines();

            for (int i = 0; i < count && i < editorCount; i++)
            {
                bool isUnchanged = leftLines[i].Type == DiffLineType.Unchanged && rightLines[i].Type == DiffLineType.Unchanged;
                bool shouldHide = (_currentFilterIndex == 1 && isUnchanged) || (_currentFilterIndex == 2 && !isUnchanged);

                if (shouldHide)
                {
                    _editorLeft.HideLines(i, i);
                    if (i < _editorRight.Lines.Count)
                        _editorRight.HideLines(i, i);
                }
            }
        }

        private void ShowAllLines()
        {
            int leftCount = _editorLeft.Lines.Count;
            if (leftCount > 0)
                _editorLeft.ShowLines(0, leftCount - 1);

            int rightCount = _editorRight.Lines.Count;
            if (rightCount > 0)
                _editorRight.ShowLines(0, rightCount - 1);
        }

        private void UpdateInfoLabel()
        {
            int leftLines = _editorLeft.Lines.Count;
            int rightLines = _editorRight.Lines.Count;
            int diffCount = _lastDiffResult?.DiffCount ?? 0;
            _infoLabel.Text = string.Format(
                LanguageManager.Instance.GetText("TextDiffDialog.Info", "左: {0} 行 | 右: {1} 行 | 差异: {2} 处"),
                leftLines, rightLines, diffCount);
        }

        // ===================== 滚动同步 + 光标行同步 =====================
        private void OnLeftUpdateUI(object sender, ScintillaNET.UpdateUIEventArgs e)
        {
            if (_isSyncingScroll) return;

            bool vScroll = (e.Change & ScintillaNET.UpdateChange.VScroll) != 0;
            int currentLine = _editorLeft.LineFromPosition(_editorLeft.CurrentPosition);
            bool caretChanged = currentLine != _lastLeftCaretLine;

            if (vScroll || caretChanged)
                _activeEditorIsLeft = true;

            if (!vScroll && !caretChanged) return;
            if (!_activeEditorIsLeft) return;

            _lastLeftCaretLine = currentLine;

            if (vScroll)
            {
                _isSyncingScroll = true;
                try
                {
                    _editorRight.FirstVisibleLine = _editorLeft.FirstVisibleLine;
                    HighlightCorrespondingLine(_editorRight, currentLine, _lastRightSyncLine);
                    _lastRightSyncLine = currentLine;
                }
                finally { _isSyncingScroll = false; }
            }
            else if (caretChanged)
            {
                HighlightCorrespondingLine(_editorRight, currentLine, _lastRightSyncLine);
                _lastRightSyncLine = currentLine;
            }
        }

        private void OnRightUpdateUI(object sender, ScintillaNET.UpdateUIEventArgs e)
        {
            if (_isSyncingScroll) return;

            bool vScroll = (e.Change & ScintillaNET.UpdateChange.VScroll) != 0;
            int currentLine = _editorRight.LineFromPosition(_editorRight.CurrentPosition);
            bool caretChanged = currentLine != _lastRightCaretLine;

            if (vScroll || caretChanged)
                _activeEditorIsLeft = false;

            if (!vScroll && !caretChanged) return;
            if (_activeEditorIsLeft) return;

            _lastRightCaretLine = currentLine;

            if (vScroll)
            {
                _isSyncingScroll = true;
                try { _editorLeft.FirstVisibleLine = _editorRight.FirstVisibleLine; }
                finally { _isSyncingScroll = false; }
            }
        }

        private int _lastRightSyncLine = -1;

        private void HighlightCorrespondingLine(ScintillaNET.Scintilla target, int lineIndex, int prevLine)
        {
            if (prevLine >= 0 && prevLine < target.Lines.Count)
                target.Lines[prevLine].MarkerDelete(MarkerActiveLine);
            if (lineIndex >= 0 && lineIndex < target.Lines.Count)
                target.Lines[lineIndex].MarkerAdd(MarkerActiveLine);
        }

        // ===================== Segmented 切换 =====================
        private void OnViewSegmentedChanged(object sender, AntdUI.IntEventArgs e)
        {
            if (e.Value == 1)
            {
                _isViewingPkg1 = true;
                _editorRight.ReadOnly = false;
                _editorRight.Text = string.Join("\r\n", _lines1Original);
                _editorRight.ReadOnly = true;
                ClearAllMarkers(_editorLeft);
                ClearAllMarkers(_editorRight);
            }
            else
            {
                if (_isViewingPkg1)
                {
                    _isViewingPkg1 = false;
                    _editorLeft.ReadOnly = false;
                    _editorRight.ReadOnly = false;
                    _editorRight.Text = string.Join("\r\n", _lines2);
                    _editorRight.ReadOnly = true;
                    _showWordHighlight = true;
                    RunDiff();
                }
            }
        }

        // ===================== 重置/保存 =====================
        private void OnResetClick(object sender, EventArgs e)
        {
            _isViewingPkg1 = false;
            _viewSegmented.SelectIndex = 0;
            _editorLeft.Text = string.Join("\r\n", _lines1Original);
            _editorLeft.SetSavePoint();
            _isDirty = false;
            _showWordHighlight = true;
            _editorLeft.ReadOnly = false;
            _editorRight.Text = string.Join("\r\n", _lines2);
            _editorRight.ReadOnly = true;
            RunDiff();
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            try
            {
                string dir = Path.GetDirectoryName(_tempFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_tempFilePath, _editorLeft.Text, _encoding);
                _onEdited?.Invoke(_recordName);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    LanguageManager.Instance.GetText("TextDiffDialog.SaveError", "保存失败: ") + ex.Message);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _debounceTimer.Stop();
            _debounceTimer.Dispose();
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
