using System;
using System.IO;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Label = AntdUI.Label;
using TextBox = AntdUI.Input;
using Panel = AntdUI.Panel;

namespace GDPackEditor
{
    public class ModMergeToolForm : AntdUI.BaseForm
    {
        private TextBox _basePathInput;
        private TextBox _modPathInput;
        private TextBox _outputPathInput;
        private Dictionary<string, string> _currentLanguageDict;
        private bool _isLanguagePackActive;
        private Panel _statsWrapPanel;
        private FlowLayoutPanel _statsTagsLine2;   // 统计行一：包1总/包1独有/包2总/包2独有
        private FlowLayoutPanel _statsTagsLine2b;  // 统计行二：同名/相同/冲突
        private AntdUI.Button _scanButton;
        private AntdUI.Button _startButton;
        private LogView _mergeLogView;   // 底部日志框（复用主界面公共 LogView）：仅收"开始合并"后的过程日志与报告
        private ArrowSplitter _logSplit; // 日志区展开/收缩分割条（与 ARZ 编辑器日志区同款：拖拽调高 + 点间隔条折叠/展开 + 状态持久化）
        private CancellationTokenSource _cts;
        private bool _isRunning;
        private string _tempDir;
        private List<AntdUI.Button> _filterButtons = new List<AntdUI.Button>();
        private string _currentFilterTag = "conflict";
        private AntdUI.Input _searchInput;
        private System.Windows.Forms.Timer _searchTimer;
        private string _searchText = "";
        private Panel _loadingOverlay;
        private Label _loadingLabel;
        // 冲突数强显区（扫描后显示，与统计tag并排整体居中）：数字用普通文字，红/绿区分
        private Panel _conflictBarPanel;
        private FlowLayoutPanel _conflictBarFlow;
        private Label _lblConflictNum;
        private Label _lblProcessedNum;
        private Label _lblStatus;
        private System.Windows.Forms.Panel _vSepPanel; // 统计区与冲突数区之间的 1px 竖分隔线
        // 扫描区统一父容器：内容「统计tag两行 | 竖分隔 | 冲突数大字」整体水平居中
        private Panel _scanArea;

        // 方案3：单文件树
        private AntdUI.Table _mergeTable;
        private AntdUI.Column _mergeNameCol;
        private AntdUI.ColumnCheck _adoptCheckCol;
        private List<NodeData> _mergeRootNodes;
        private List<MergeRow> _mergeRows;
        private bool _mergeFlatViewMode;
        private HashSet<string> _mergeExpandStash;
        private AntdUI.Button _btnMergeTreeMode;
        private AntdUI.Button _btnMergeFlatMode;
        private AntdUI.Button _btnMergeExpandAll;
        private AntdUI.Button _btnMergeCollapseAll;
        private AntdUI.Table _historyTable;
        private ArrowSplitter _historySplit;
        private ArrowSplitter _rightSplit;
        private AntdUI.Table _editList;
        private List<MergeEditRow> _editRows = new List<MergeEditRow>();
        private bool _syncingEditList;

        private bool _syncingChecks;
        private bool _relayoutScheduled;
        // 路径列文字颜色（浅色背景下可读）
        private static readonly Color PathBlue = Color.FromArgb(0x1f, 0x86, 0xdb);    // 1 浅蓝
        private static readonly Color PathGreen = Color.FromArgb(0x27, 0x9d, 0x4d);  // 2 浅绿
        private static readonly Color PathRed = Color.FromArgb(0xe0, 0x3a, 0x2d);    // 冲突 红
        private static readonly Color PathOrange = Color.FromArgb(0xe8, 0x8a, 0x00); // 已改名 橙
        private static readonly Color PathTeal = Color.FromArgb(0x0e, 0x9f, 0x9f);   // 都有 青（蓝绿混合）
        private static readonly Color PathOldNameGray = Color.FromArgb(0x8a, 0x8a, 0x8a); // 改名后旧名 灰
        private static readonly Color PathNewName2 = Color.FromArgb(0x0e, 0x9f, 0x9f); // 冲突双改名的包2新名 青

        // 扫描区 tag 标签配色（浅色底 + 深色字）
        private static readonly Color TagBlueBack = Color.FromArgb(0xe6, 0xf4, 0xff), TagBlueFore = Color.FromArgb(0x1f, 0x6f, 0xb8);
        private static readonly Color TagGreenBack = Color.FromArgb(0xe7, 0xf7, 0xee), TagGreenFore = Color.FromArgb(0x1b, 0x85, 0x47);
        private static readonly Color TagRedBack = Color.FromArgb(0xff, 0xf0, 0xef), TagRedFore = Color.FromArgb(0xc6, 0x2a, 0x24);
        private static readonly Color TagDefaultBack = Color.FromArgb(0xf2, 0xf3, 0xf5), TagDefaultFore = Color.FromArgb(0x5a, 0x60, 0x67);



        private List<CompareItem> _allItems = new List<CompareItem>();
        private List<CompareItem> _filteredItems = new List<CompareItem>();
        private string _packageType;
        private string _basePath;
        private string _modPath;
        private string _outputPath;
        private List<TemplateNode> _templates;

        private static readonly string[][] _filterEntries = new string[][] {
            new[] { "ModMergeTool.FilterAll", "all" },
            new[] { "ModMergeTool.FilterPkg1", "pkg1" },
            new[] { "ModMergeTool.FilterPkg2", "pkg2" },
            new[] { "ModMergeTool.FilterConflict", "conflict" },
            new[] { "ModMergeTool.FilterSame", "same" },
            new[] { "ModMergeTool.FilterCoexist", "coexist" },
            new[] { "ModMergeTool.FilterEdited", "edited" }
        };

        private static string GetFilterButtonText(string key)
        {
            return key switch
            {
                "ModMergeTool.FilterAll" => LanguageManager.Instance.GetText(key, "全部"),
                "ModMergeTool.FilterPkg1" => LanguageManager.Instance.GetText(key, "包1"),
                "ModMergeTool.FilterPkg2" => LanguageManager.Instance.GetText(key, "包2"),
                "ModMergeTool.FilterConflict" => LanguageManager.Instance.GetText(key, "冲突"),
                "ModMergeTool.FilterSame" => LanguageManager.Instance.GetText(key, "相同"),
                "ModMergeTool.FilterCoexist" => LanguageManager.Instance.GetText(key, "已改名"),
                "ModMergeTool.FilterEdited" => LanguageManager.Instance.GetText(key, "已编辑"),
                _ => key
            };
        }

        private static System.Windows.Forms.Label MakeSeparator()
            => new System.Windows.Forms.Label
            {
                Text = "|",
                Dock = DockStyle.Left,
                AutoSize = false,
                Width = 14,
                Height = 30,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = FontHelper.UiFont(8),
                ForeColor = Color.FromArgb(170, 170, 170),
                Margin = new Padding(0),
                Padding = Padding.Empty
            };

        public enum MergeDecision
        {
            None = 0,
            Use1 = 1,
            Use2 = 2,
            Merged = 3,
            Renamed = 4
        }

        public enum CompareStatus
        {
            None = 0,
            Only1 = 1,
            Only2 = 2,
            Same = 3,
            Conflict = 4,
            Renamed = 5
        }

        public class CompareItem
        {
            public bool IsChecked { get; set; } = true;
            public string FileName { get; set; }
            public CompareStatus Status { get; set; }
            public long Size1 { get; set; }
            public long Size2 { get; set; }
            public string Hash1 { get; set; }
            public string Hash2 { get; set; }
            public string RenamedPath { get; set; }
            public bool IsEdited { get; set; }
            public int RealIndex { get; set; } = -1;
            public string[] Lines1 { get; set; }
            public string[] Lines2 { get; set; }
            public string Description { get; set; } = "";
            public string Localized { get; set; } = "";   // 本地化展示文本（扫描/语言包保存后固化缓存，不实时字典匹配）
            public Dictionary<string, string> TagDict { get; set; }

            // 方案3新增：决策类型与临时/改名路径
            public MergeDecision MergeDecision { get; set; } = MergeDecision.None;
            public string EditedTempPath { get; set; }
            public string RenamedPath1 { get; set; }   // 包1改的新路径（空=未改）
            public string RenamedPath2 { get; set; }   // 包2改的新路径（空=未改）

            // P2：改名立即释放的临时文件缓存（以文件为准，打包直接引用，避免打包时再解包）
            public string RenamedTempPath1 { get; set; }   // 包1新名对应的临时文件
            public string RenamedTempPath2 { get; set; }   // 包2新名对应的临时文件
            public string RenamedOutputPath1 { get; set; } // 包1输出的相对路径（新名或原名）
            public string RenamedOutputPath2 { get; set; } // 包2输出的相对路径（新名或原名）

            public CompareStatus OrigStatus { get; set; }
            public bool IsMerged => !string.IsNullOrEmpty(EditedTempPath);
            public bool IsRenamed1 => !string.IsNullOrEmpty(RenamedPath1);
            public bool IsRenamed2 => !string.IsNullOrEmpty(RenamedPath2);
            public bool IsRenamed => IsRenamed1 || IsRenamed2;
            public bool CanCompare => Status == CompareStatus.Conflict && IsEditableFile(FileName);
            public string Size1Text => Size1 == 0 ? "-" : FormatSize(Size1);
            public string Size2Text => Size2 == 0 ? "-" : FormatSize(Size2);
            public AntdUI.CellLink[] CellLinks => CanCompare
                ? new AntdUI.CellLink[] { new AntdUI.CellButton("compare", LanguageManager.Instance.GetText("ModMergeTool.CompareButton", "对比编辑"), AntdUI.TTypeMini.Primary) }
                : null;

            public static bool IsEditableFile(string name)
            {
                if (name == null) return false;
                var ext = Path.GetExtension(name);
                return string.Equals(ext, ".dbr", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ext, ".lua", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ext, ".tpl", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsDbrFile(string name)
            {
                return name != null && name.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase);
            }

            public static bool IsTextFile(string name)
            {
                if (name == null) return false;
                var ext = Path.GetExtension(name);
                return string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ext, ".lua", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ext, ".tpl", StringComparison.OrdinalIgnoreCase);
            }

            private static string FormatSize(long bytes)
            {
                if (bytes < 1024) return bytes + " B";
                if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " K";
                return (bytes / (1024.0 * 1024)).ToString("F1") + " M";
            }
        }

        public class DualComparisonRow
        {
            public string LeftName { get; set; }
            public string RightName { get; set; }
            public string Status { get; set; }
            public bool IsDirectory { get; set; }
            public NodeData LeftNode { get; set; }
            public NodeData RightNode { get; set; }
            public CompareItem SourceItem { get; set; }
            public string Decision { get; set; }
            public string FullPath { get; set; }
            public List<DualComparisonRow> Children { get; set; }

            public bool IsEmptyLeft => LeftNode == null && string.IsNullOrEmpty(LeftName);
            public bool IsEmptyRight => RightNode == null && string.IsNullOrEmpty(RightName);
            public string DisplayNameLeft { get; set; } = "";
            public string DisplayNameRight { get; set; } = "";
            public string DescLeft { get; set; } = "";
            public string DescRight { get; set; } = "";
            public string TagLeft { get; set; } = "";
            public string TagRight { get; set; } = "";
            public bool Expand { get; set; }
        }

        // 方案3：单文件树行模型（替代 DualComparisonRow）
        public class MergeRow : INotifyPropertyChanged
        {
            public string Name { get; set; }            // 节点名（文件夹名 / 文件名）
            public string FullPath { get; set; }        // 全路径（= CompareItem.FileName 或文件夹路径）
            public bool IsDirectory { get; set; }
            public CompareStatus Status { get; set; }    // 仅1/仅2/相同/冲突/已改名
            public string OwnerIcon { get; set; }       // 1/2/1 2（归属，Alert浅色）
            public string DiffIcon { get; set; }        // =/X/合（差异，Alert浅色）
            public string DisplayName { get; set; }     // 📁/📄 + 名称
            public string Desc { get; set; }            // 说明（渲染预计算）
            public string Localized { get; set; }       // 本地化名称/标签文本（渲染预计算）
            public CompareItem Item { get; set; }       // 关联叶子（文件夹为 null）
            public List<MergeRow> Children { get; set; }
            public MergeRow Parent;                      // 父节点（勾选祖先链局部重算用）
            public bool Expand { get; set; }

            public event PropertyChangedEventHandler PropertyChanged;

            private bool _adopt;
            public bool Adopt
            {
                get => _adopt;
                set
                {
                    if (_adopt == value) return;
                    _adopt = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Adopt)));
                }
            }
        }

        private class MergeHistoryRow
        {
            public string Id { get; set; }
            public string BasePath { get; set; }
            public string ModPath { get; set; }
            public bool ExistsBase { get; set; }
            public bool ExistsMod { get; set; }
            public string OpenTimeText { get; set; }
            public DateTime OpenTime { get; set; }
            public AntdUI.ICell[] OpCells { get; set; }
        }

        private class MergeEditRow
        {
            public CompareItem Item { get; set; }
            public string Kind { get; set; }        // "改" / "合"
            public string FilePath { get; set; }    // 原始路径（定位用）
            public string Display { get; set; }     // 展示文本（改名含新名）
            public AntdUI.ICell[] OpCells { get; set; }
        }

        public ModMergeToolForm()
        {
            InitializeUI();
            // 语言切换刷新：本窗是独立顶层窗体，不在主窗体控件树内 → 需自行订阅
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
        }

        /// <summary>语言切换：刷新本窗全部静态文案（Name=语言key 约定 → UiTextRefresher）</summary>
        private void OnLanguageChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)ApplyLocalizedTexts); } catch { }
                return;
            }
            ApplyLocalizedTexts();
        }

        private void ApplyLocalizedTexts()
        {
            if (IsDisposed) return;
            UiTextRefresher.RefreshTree(this);
            // 搜索框：同时带前缀与占位符（刷新器一条 Name 只能管一个）→ 显式刷新两处
            try
            {
                if (_searchInput != null && !_searchInput.IsDisposed)
                {
                    _searchInput.PrefixText = LanguageManager.Instance.GetText("Common.SearchLabel", "搜索：");
                    _searchInput.PlaceholderText = LanguageManager.Instance.GetText("ModMergeTool.SearchPlaceholder", "搜索 文件名/说明/本地化...");
                }
            }
            catch { }
            // 空白占位（"无{筛选}数据"）按当前筛选与语言重算
            try { UpdateEmptyFilterText(); } catch { }
            // 表格列头与空数据文案（AntdUI.Table 的列/EmptyText 不在控件树内）
            try { RefreshTableLocalizedTexts(); } catch { }
        }

        /// <summary>
        /// 刷新三张表的**列头**与**空数据文案**：AntdUI.Table 的 Columns/EmptyText
        /// 不是子控件，UiTextRefresher 的控件树遍历覆盖不到，需按"列名→语言key"显式刷新。
        /// </summary>
        private void RefreshTableLocalizedTexts()
        {
            void SetCols(AntdUI.Table t, params (string Col, string Key, string Fallback)[] map)
            {
                if (t == null || t.IsDisposed) return;
                foreach (var (col, key, fallback) in map)
                {
                    foreach (var c in t.Columns)
                    {
                        if (c.Key != col) continue;
                        c.Title = LanguageManager.Instance.GetText(key, fallback);
                        break;
                    }
                }
                t.Invalidate();
            }

            SetCols(_mergeTable,
                ("Decision", "ModMergeTool.ColDecision", "处理"),
                ("Diff", "ModMergeTool.ColDiff", "差异"),
                ("Owner", "ModMergeTool.ColOwner", "归属"),
                ("Name", "ModMergeTool.ColName", "路径"),
                ("Desc", "ModMergeTool.ColDesc", "说明"),
                ("Localized", "ModMergeTool.ColLocalized", "本地化"));
            SetCols(_historyTable,
                ("BasePath", "ModMergeTool.HistoryBase", "基准包"),
                ("ModPath", "ModMergeTool.HistoryMod", "并入包"),
                ("OpenTimeText", "ModMergeTool.HistoryTime", "打开时间"),
                ("OpCells", "ModMergeTool.HistoryOp", "操作"));
            SetCols(_editList,
                ("Kind", "ModMergeTool.EditListStatus", "类型"),
                ("Display", "ModMergeTool.EditListFile", "文件"));

            // 空数据文案（AntdUI 内置默认是中文"暂无数据"，必须由本工具显式设置才能跟随语言）
            if (_editList != null && !_editList.IsDisposed)
                _editList.EmptyText = LanguageManager.Instance.GetText("Common.NoData", "暂无数据");
            if (_historyTable != null && !_historyTable.IsDisposed)
                _historyTable.EmptyText = LanguageManager.Instance.GetText("ModMergeTool.HistoryEmpty", "暂无历史记录");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { LanguageManager.Instance.LanguageChanged -= OnLanguageChanged; } catch { }
            base.OnFormClosed(e);
        }

        private void InitializeUI()
        {
            Name = "ModMergeTool.Title";   // Name=语言key：切语言时窗体标题由 UiTextRefresher 刷新
            Text = LanguageManager.Instance.GetText("ModMergeTool.Title", "MOD合并工具");
            Size = new Size(1100, 720);
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            BackColor = Color.FromArgb(245, 245, 245);
            MinimumSize = new Size(800, 500);

            var mainPanel = new Panel { Dock = DockStyle.Fill, Back = Color.Transparent, Padding = new Padding(15) };

            // hintLabel
            var hintLabel = new Label
            {
                Name = "ModMergeTool.Hint", Text = LanguageManager.Instance.GetText("ModMergeTool.Hint", "请选择两个同格式的包文件（同为 .arz 或同为 .arc），工具将自动识别格式并扫描对比。"),
                Dock = DockStyle.Top, Height = 30,
                Font = FontHelper.UiFont(9), ForeColor = Color.Gray,
                Padding = new Padding(0, 5, 0, 0)
            };

            // pathRow: 包1(基准) / 包2(并入) 横向布局，各占约半宽；第3列为扫描按钮区（宽度按按钮文本自动撑起）。
            // 每个半区是一张圆角卡片（包1蓝 / 包2绿），把 Alert 提示条 + 标签 + 输入框 + 浏览按钮包成一个整体。
            var pathRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 84,
                BackColor = Color.Transparent,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0, 3, 0, 0)
            };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); // 扫描按钮列，初始占位，测量按钮文本后自动调整
            pathRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            Panel BuildPackageInputPanel(string labelKey, string labelFallback, string placeholderKey, string placeholderFallback, bool isBase, bool leftHalf)
            {
                // 半区三件套：输入框(Fill) + 浏览(Right) + 标签(Left)，Dock 从后往前处理
                var panel = new Panel
                {
                    Dock = DockStyle.Fill, Back = Color.Transparent,
                    Padding = leftHalf ? new Padding(0, 0, 3, 0) : new Padding(3, 0, 0, 0)
                };
                var label = new Label
                {
                    Name = labelKey,   // Name=语言key：切语言时由 UiTextRefresher 刷新
                    Text = LanguageManager.Instance.GetText(labelKey, labelFallback),
                    Dock = DockStyle.Left, AutoSize = true,
                    Padding = new Padding(0, 10, 0, 0), Font = FontHelper.UiFont(10)
                };
                var input = new TextBox
                {
                    Name = placeholderKey,   // Name=key 语义为占位符（刷新器对 TextBox 只改 PlaceholderText）
                    Dock = DockStyle.Fill,
                    PlaceholderText = LanguageManager.Instance.GetText(placeholderKey, placeholderFallback),
                    Font = FontHelper.UiFont(9), AllowClear = true
                };
                input.TextChanged += OnPathChanged;
                EnableDragDropFile(input);
                var browseBtn = new AntdUI.Button
                {
                Name = "Common.Browse", Text = LanguageManager.Instance.GetText("Common.Browse", "浏览"),
                    Type = AntdUI.TTypeMini.Default, Dock = DockStyle.Right, AutoSize = true, Font = FontHelper.UiFont(9)
                };
                browseBtn.Click += (s, e) => BrowseFile(input);
                panel.Controls.Add(input);
                panel.Controls.Add(browseBtn);
                panel.Controls.Add(label);
                if (isBase) _basePathInput = input; else _modPathInput = input;
                return panel;
            }

            // 构建一张半区卡片：Alert(顶部提示) + 输入面板(下方)，整体被同色边框+浅色背景框住
            Func<Color, Color, AntdUI.TType, string, string, Panel, Panel> BuildPackageCard =
                (back, border, alertType, text, hint, inputPanel) =>
                {
                    var card = new Panel
                    {
                        Dock = DockStyle.Fill,
                        Back = back,
                        BorderWidth = 1,
                        BorderColor = border,
                        Radius = 8,
                        Padding = new Padding(12, 4, 12, 6)
                    };
                    var alert = new AntdUI.Alert
                    {
                        Name = text,   // Name=语言key：切语言时由 UiTextRefresher 刷新
                        Dock = DockStyle.Top,
                        Height = 28,
                        Icon = alertType,
                        Text = LanguageManager.Instance.GetText(text, hint),
                        Margin = new Padding(0, 2, 0, 4),
                        Radius = 4
                    };
                    // Dock 反向：先加输入面板(Fill) → 再加 Alert(Top)，使 Alert 显示在卡片顶部
                    card.Controls.Add(inputPanel);
                    card.Controls.Add(alert);
                    return card;
                };

            var cardBase = BuildPackageCard(
                AntdUI.Style.Get(AntdUI.Colour.InfoBg),
                AntdUI.Style.Get(AntdUI.Colour.InfoBorder),
                AntdUI.TType.Info,
                "ModMergeTool.AlertBase", "基准包（包1）：以本包数据为底，将包2的数据并入本包中，最终形成新包",
                BuildPackageInputPanel("ModMergeTool.BasePath", "包1(基准):", "ModMergeTool.BasePathPlaceholder", "选择或拖入基准包文件...", true, true));
            var cardMod = BuildPackageCard(
                AntdUI.Style.Get(AntdUI.Colour.SuccessBg),
                AntdUI.Style.Get(AntdUI.Colour.SuccessBorder),
                AntdUI.TType.Success,
                "ModMergeTool.AlertMod", "并入包（包2）：本包数据将被并入基准包，形成总合集，最终形成新包",
                BuildPackageInputPanel("ModMergeTool.ModPath", "包2(并入):", "ModMergeTool.ModPathPlaceholder", "选择或拖入并入包文件...", false, false));
            pathRow.Controls.Add(cardMod, 1, 0);
            pathRow.Controls.Add(cardBase, 0, 0);

            // 扫描按钮 + 使用说明：移到 pathRow 第3列（上下布局，宽度按按钮文本自动撑起），不再占独立行
            // ⚠ **高度必须显式给**：这两个按钮是全项目少数 `AutoSize=false` 的（宽度按文本手量），
            //   而 AntdUI.Button 的默认尺寸是 **0×0** —— 不给 Height 就永远 0 高，整格不可见（用户实测踩过）。
            //   全项目其它按钮都是 AutoSize=true（ButtonFactory 规格化）所以没这个问题。
            _scanButton = new AntdUI.Button
            {
                Name = "ModMergeTool.ScanButton", Text = LanguageManager.Instance.GetText("ModMergeTool.ScanButton", "扫描对比"),
                Type = AntdUI.TTypeMini.Primary,
                AutoSize = false, Font = FontHelper.UiFont(9), Enabled = false,
                Height = DpiManager.S(30)
            };
            _scanButton.Click += OnStartClick;
            var noteBtn = new AntdUI.Button
            {
                Name = "ModMergeTool.NoteButton", Text = LanguageManager.Instance.GetText("ModMergeTool.NoteButton", "合并工具使用说明"),
                Type = AntdUI.TTypeMini.Default,
                AutoSize = false, Font = FontHelper.UiFont(9),
                Height = DpiManager.S(30)
            };
            noteBtn.Click += (s, e) => MarkdownViewer.Show(this,
                // 只有简体中文用中文说明；英文、自定义语言包及其它情况一律英文
                // （与"自定义语言包不可用时回落英文"的取向保持一致）
                string.Equals(LanguageManager.Instance.CurrentLangCode, LanguageManager.BuiltInZh, StringComparison.OrdinalIgnoreCase)
                    ? "!MergeNoteCN.md"
                    : "!MergeNoteEN.md",
                "ModMergeTool.NoteTitle");
            var scanBtnColumn = new Panel { Dock = DockStyle.Fill, Back = Color.Transparent, Padding = new Padding(8, 0, 0, 0) };
            var scanBtnFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = false
            };
            void MeasureScanButtons()
            {
                _scanButton.Width = TextRenderer.MeasureText(_scanButton.Text, _scanButton.Font).Width + 40;
                noteBtn.Width = TextRenderer.MeasureText(noteBtn.Text, noteBtn.Font).Width + 40;
                int colW = Math.Max(_scanButton.Width, noteBtn.Width) + 12;
                pathRow.ColumnStyles[2].Width = colW; // 第3列宽随按钮文本自动撑起
            }
            void CenterButtonsVertical()
            {
                int gap = (scanBtnColumn.Height - _scanButton.Height * 2 - 6) / 2;
                _scanButton.Margin = new Padding(0, Math.Max(0, gap), 0, 4);
                noteBtn.Margin = new Padding(0, 0, 0, 0);
            }
            void RescanButtons(object sender, EventArgs e) { MeasureScanButtons(); scanBtnFlow.PerformLayout(); }
            _scanButton.TextChanged += RescanButtons;
            noteBtn.TextChanged += RescanButtons;
            scanBtnColumn.Resize += (s, e) => CenterButtonsVertical();
            MeasureScanButtons();
            scanBtnFlow.Controls.Add(_scanButton); // TopDown：先加的在上 ⇒ “扫描对比”位于“使用说明”之上
            scanBtnFlow.Controls.Add(noteBtn);
            scanBtnColumn.Controls.Add(scanBtnFlow);
            pathRow.Controls.Add(scanBtnColumn, 2, 0);

            // scanArea: 统一父容器，内容 =「统计tag两行 | 竖分隔 | 冲突数大字」整体水平居中并排
            var scanArea = new Panel { Dock = DockStyle.Top, Height = 48, Back = Color.Transparent };
            _scanArea = scanArea;
            _statsTagsLine2 = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 0, 0, 0)
            };
            _statsTagsLine2b = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 0, 0, 0)
            };
            _statsWrapPanel = new Panel { Back = Color.Transparent };
            _statsWrapPanel.Controls.Add(_statsTagsLine2);
            _statsWrapPanel.Controls.Add(_statsTagsLine2b);
            // 竖分隔线：AntdUI 无原生竖线组件，用 1px 背景 Panel 实现
            _vSepPanel = new System.Windows.Forms.Panel
            {
                Width = 1,
                Height = 40,
                BackColor = Color.FromArgb(0xd0, 0xd4, 0xd8),
                Margin = new Padding(10, 2, 10, 2),
                Visible = false
            };
            // 中央组合容器：统计区 + 竖线 +（冲突数区由 BuildConflictBar 追加）
            var centerRow = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0)
            };
            centerRow.Controls.Add(_statsWrapPanel);
            centerRow.Controls.Add(_vSepPanel);
            scanArea.Controls.Add(centerRow);
            // 整体居中：horizontalLeft=(scanArea.Width-centerRow.Width)/2，随内容/窗口变化实时刷新
            void CenterScanContent(object sender, EventArgs e)
            {
                if (scanArea.Width <= 0 || centerRow.Width <= 0) return;
                centerRow.Left = Math.Max(2, (scanArea.Width - centerRow.Width) / 2);
                centerRow.Top = Math.Max(4, (scanArea.Height - centerRow.Height) / 2);
            }
            centerRow.SizeChanged += CenterScanContent;
            scanArea.Resize += CenterScanContent;
            // 手动摆放两行tag（行一=包1/包2 总览在上，行二=同名/相同/冲突 在下），两行右端顶格（右对齐），
            // wrap 尺寸=两行较宽者，右缘紧贴中间竖线；整体区块再由 CenterScanContent 水平居中。
            // 一律用 PreferredSize（布局稳定值）定位，避免引用运行期 Width/Height 在布局瞬间为 0 导致行一被推离可视区。
            void LayoutStatsLines()
            {
                _statsTagsLine2.PerformLayout();
                _statsTagsLine2b.PerformLayout();
                var p1 = _statsTagsLine2.PreferredSize;
                var p2 = _statsTagsLine2b.PreferredSize;
                int w = Math.Max(p1.Width, p2.Width);
                _statsTagsLine2.Size = p1;
                _statsTagsLine2b.Size = p2;
                _statsWrapPanel.Width = w;
                _statsWrapPanel.Height = p1.Height + p2.Height;
                _statsTagsLine2.Location = new Point(w - p1.Width, 0);          // 行一在上、右对齐
                _statsTagsLine2b.Location = new Point(w - p2.Width, p1.Height); // 行二在下、右对齐（挨着竖线）
                CenterScanContent(scanArea, EventArgs.Empty);
            }
            _statsTagsLine2.SizeChanged += (s, e) => LayoutStatsLines();
            _statsTagsLine2b.SizeChanged += (s, e) => LayoutStatsLines();
            LayoutStatsLines();


            // controlRow: 功能按钮组（全选/全不选/展开/收缩/全用1/全用2/全用合/全用改）
            var controlRow = new Panel { Dock = DockStyle.Top, Height = 38, Back = Color.Transparent };
            // filterRow: 筛选按钮组（全部/冲突/相同/已改名/已编辑），置于功能按钮组正下方，操作区域集中
            var filterRow = new Panel { Dock = DockStyle.Top, Height = 38, Back = Color.Transparent };

            // 筛选按钮组（Dock=Left 反向插入：倒序添加 ⇒ 视觉顺序 = 数组顺序，"全部"在最左）
            _filterButtons.Clear();
            for (int fi = _filterEntries.Length - 1; fi >= 0; fi--)
            {
                var fe = _filterEntries[fi];
                var btn = new AntdUI.Button
                {
                    Name = fe[0],   // Name=语言key：切语言时由 UiTextRefresher 刷新（与 GetFilterButtonText 同源）
                    Text = GetFilterButtonText(fe[0]),
                    Tag = fe[1],
                    Dock = DockStyle.Left,
                    AutoSize = true,
                    
                    Font = FontHelper.UiFont(9),
                    Type = fe[1] == "conflict" ? AntdUI.TTypeMini.Error : AntdUI.TTypeMini.Default,
                    Ghost = fe[1] == "conflict",
                    BorderWidth = fe[1] == "conflict" ? 2 : 0
                };
                string tag = fe[1];
                btn.Click += async (s, e) =>
                {
                    if (_mergeTable == null) { ApplyFilter(tag); return; }
                    var target = (Control)_mergeTable;
                    var cfg = new AntdUI.Spin.Config { Text = LanguageManager.Instance.GetText("ModMergeTool.Filtering", "筛选中...") };
                    await AntdUI.Spin.open(target, cfg, async c =>
                    {
                        await Task.Delay(30);
                        // 后台过滤 + 树构建（A方案：常用且稳定）
                        string snapshotSearch = _searchText;
                        var snapshotAll = _allItems.ToList();
                        var filtered = await Task.Run(() =>
                        {
                            var statusOrder = new Dictionary<CompareStatus, int>
                            {
                                [CompareStatus.Conflict] = 0,
                                [CompareStatus.Only1] = 2,
                                [CompareStatus.Only2] = 3,
                                [CompareStatus.Same] = 4,
                                [CompareStatus.Renamed] = 5
                            };
                            IEnumerable<CompareItem> items = tag switch
                            {
                                "conflict" => snapshotAll.Where(i => i.Status == CompareStatus.Conflict || i.MergeDecision == MergeDecision.Renamed),
                                // 包1：包1内存在的全部文件（仅1有/相同/冲突/非仅2来源的改名文件）；包2 对称
                                "pkg1" => snapshotAll.Where(i => i.Status == CompareStatus.Only1 || i.Status == CompareStatus.Same || i.Status == CompareStatus.Conflict
                                                              || (i.Status == CompareStatus.Renamed && i.OrigStatus != CompareStatus.Only2)),
                                "pkg2" => snapshotAll.Where(i => i.Status == CompareStatus.Only2 || i.Status == CompareStatus.Same || i.Status == CompareStatus.Conflict
                                                              || (i.Status == CompareStatus.Renamed && i.OrigStatus != CompareStatus.Only1)),
                                "same" => snapshotAll.Where(i => i.Status == CompareStatus.Same),
                                "coexist" => snapshotAll.Where(i => i.MergeDecision == MergeDecision.Renamed),
                                "edited" => snapshotAll.Where(i => i.IsEdited || i.MergeDecision == MergeDecision.Merged),
                                _ => snapshotAll
                            };
                            if (!string.IsNullOrEmpty(snapshotSearch))
                            {
                                string lower = snapshotSearch.ToLowerInvariant();
                                items = items.Where(i => MatchSearch(i, lower));
                            }
                            return items.OrderBy(i => statusOrder.TryGetValue(i.Status, out int o) ? o : 99).ThenBy(i => i.FileName).ToList();
                        });
                        var rows = await Task.Run(() => BuildMergeTreeData(filtered, filtered != snapshotAll));
                        if (IsDisposed) return;
                        if (InvokeRequired)
                            Invoke((Action)(() =>
                            {
                                _filteredItems = filtered;
                                _currentFilterTag = tag;
                                _mergeRows = rows;
                                _mergeTable.DataSource = new BindingList<MergeRow>(rows);
                                if (rows.Count > 0 && !_mergeFlatViewMode) _mergeTable.ExpandAll(true);
                                UpdateFilterButtonHighlight(tag);
                                UpdateEmptyFilterText();
                            }));
                        else
                        {
                            _filteredItems = filtered;
                            _currentFilterTag = tag;
                            _mergeRows = rows;
                            _mergeTable.DataSource = new BindingList<MergeRow>(rows);
                            if (rows.Count > 0 && !_mergeFlatViewMode) _mergeTable.ExpandAll(true);
                            UpdateFilterButtonHighlight(tag);
                            UpdateEmptyFilterText();
                        }
                    });
                };
                _filterButtons.Add(btn);
                // 竖线分组：在“包2”与“冲突”之间、“全部”与“包1”之间插入分隔（Dock=Left 反向插入，故在按钮前添加）
                if (fe[1] == "pkg2" || fe[1] == "all") filterRow.Controls.Add(MakeSeparator());
                filterRow.Controls.Add(btn);
            }
            // 筛选行最左：固定文字标签（最后添加 ⇒ 视觉最左）
            filterRow.Controls.Add(new Label
            {
                Name = "ModMergeTool.FilterLabel", Text = LanguageManager.Instance.GetText("ModMergeTool.FilterLabel", "筛选："),
                Dock = DockStyle.Left, AutoSize = true, Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = FontHelper.UiFont(8),
                ForeColor = Color.FromArgb(140, 140, 140),
                Padding = new Padding(0, 7, 8, 0)
            });
            // 筛选区自然追加：文件名搜索框（固定宽300，200ms防抖）+ "|" 分隔，跟随在筛选按钮组之后（不右对齐）
            _searchInput = new AntdUI.Input
            {
                Dock = DockStyle.Left, Width = 300, Height = 30, Margin = new Padding(0, 4, 0, 4),
                PlaceholderText = LanguageManager.Instance.GetText("ModMergeTool.SearchPlaceholder", "搜索 文件名/说明/本地化..."),
                PrefixText = LanguageManager.Instance.GetText("Common.SearchLabel", "搜索："),
                AllowClear = true,
                Font = FontHelper.UiFont(9)
            };
            _searchTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _searchTimer.Tick += (s, e) =>
            {
                _searchTimer.Stop();
                _searchText = _searchInput.Text?.Trim() ?? "";
                ApplyFilter();
            };
            _searchInput.TextChanged += (s, e) =>
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            };
            // Dock=Left 反转布局：把搜索框与分隔线置于最右端（索引0/1 ⇒ 视觉上紧跟最后一个筛选按钮之后）
            var sepSearch = MakeSeparator();
            filterRow.Controls.Add(sepSearch);
            filterRow.Controls.Add(_searchInput);
            filterRow.Controls.SetChildIndex(_searchInput, 0);
            filterRow.Controls.SetChildIndex(sepSearch, 1);


            // 展开 / 收缩（树展开控制，位于功能行中段）
            _btnMergeCollapseAll = new AntdUI.Button
            {
                Name = "ModMergeTool.CollapseAll", Text = LanguageManager.Instance.GetText("ModMergeTool.CollapseAll", "收缩"),
                Type = AntdUI.TTypeMini.Default, Dock = DockStyle.Left, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            _btnMergeCollapseAll.Click += (s, e) =>
            {
                if (_mergeTable == null) return;
                _mergeTable.ExpandAll(false);
            };
            _btnMergeExpandAll = new AntdUI.Button
            {
                Name = "ModMergeTool.ExpandAll", Text = LanguageManager.Instance.GetText("ModMergeTool.ExpandAll", "展开"),
                Type = AntdUI.TTypeMini.Default, Dock = DockStyle.Left, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            _btnMergeExpandAll.Click += (s, e) =>
            {
                if (_mergeTable == null) return;
                _mergeTable.ExpandAll(true);
            };
            // 表格显示模式切换：树型显示 / 平铺显示（视觉上紧跟收缩按钮之后）
            _btnMergeTreeMode = new AntdUI.Button
            {
                Name = "Editor.TreeViewMode", Text = LanguageManager.Instance.GetText("Editor.TreeViewMode", "树型显示"),
                Type = AntdUI.TTypeMini.Primary, Dock = DockStyle.Left, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            _btnMergeFlatMode = new AntdUI.Button
            {
                Name = "Editor.FlatViewMode", Text = LanguageManager.Instance.GetText("Editor.FlatViewMode", "平铺显示"),
                Type = AntdUI.TTypeMini.Default, Dock = DockStyle.Left, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            _btnMergeTreeMode.Click += (s, e) => SwitchMergeViewMode(flat: false);
            _btnMergeFlatMode.Click += (s, e) => SwitchMergeViewMode(flat: true);
            // 勾选快控：全选 / 全不选（替代原表头统控复选框，仅作用于数据层 IsChecked + 显示壳，不绑定统计）
            var checkAllBtn = new AntdUI.Button
            {
                Name = "ModMergeTool.CheckAll", Text = LanguageManager.Instance.GetText("ModMergeTool.CheckAll", "全选"),
                Type = AntdUI.TTypeMini.Default, Dock = DockStyle.Left, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            checkAllBtn.Click += (s, e) =>
            {
                if (_mergeTable == null || _mergeRows == null) return;
                _syncingChecks = true;
                try
                {
                    foreach (var r in _mergeRows)
                    {
                        if (!r.IsDirectory && r.Item != null) r.Item.IsChecked = true;
                        if (r.Children != null && r.Children.Count > 0) SetAllAdoptItems(r, true);
                    }
                }
                finally { _syncingChecks = false; }
                SetAllAdoptRows(_mergeRows, true);
                ScheduleCheckRelayout();
            };
            var uncheckAllBtn = new AntdUI.Button
            {
                Name = "ModMergeTool.UncheckAll", Text = LanguageManager.Instance.GetText("ModMergeTool.UncheckAll", "全不选"),
                Type = AntdUI.TTypeMini.Default, Dock = DockStyle.Left, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            uncheckAllBtn.Click += (s, e) =>
            {
                if (_mergeTable == null || _mergeRows == null) return;
                _syncingChecks = true;
                try
                {
                    foreach (var r in _mergeRows)
                    {
                        if (!r.IsDirectory && r.Item != null) r.Item.IsChecked = false;
                        if (r.Children != null && r.Children.Count > 0) SetAllAdoptItems(r, false);
                    }
                }
                finally { _syncingChecks = false; }
                SetAllAdoptRows(_mergeRows, false);
                ScheduleCheckRelayout();
            };
            // 快捷决策：全用1 / 全用2 / 全用合 / 全用改（作用于 筛选∩勾选 的冲突文件决策）
            void ApplyDecisionToSelection(MergeDecision decision, Func<CompareItem, bool> extra)
            {
                if (_mergeTable == null || _allItems == null) return;
                var targets = _filteredItems
                    .Where(i => i.Status == CompareStatus.Conflict && extra(i))
                    .ToList();
                // 数据层批量改写，显示层仅同步 DiffIcon 后整表纯重绘：不重建树、不重绑、不刷统计。
                foreach (var it in targets) it.MergeDecision = decision;
                if (_mergeRows != null)
                    foreach (var row in _mergeRows.Where(r => !r.IsDirectory && r.Item != null && r.Status == CompareStatus.Conflict && extra(r.Item)))
                    { row.Item.MergeDecision = decision; row.DiffIcon = GetDiffIcon(row); }
                // 决策格按需渲染会缓存文字，纯重绘不生效，故重建显示格使决策文字即时刷新。
                ScheduleCheckRelayout();
                UpdateStats();
                RefreshEditList();
            }
            var useAll1Btn = new AntdUI.Button
            {
                Name = "ModMergeTool.UseAll1", Text = LanguageManager.Instance.GetText("ModMergeTool.UseAll1", "全用1"),
                Type = AntdUI.TTypeMini.Info, Dock = DockStyle.Left, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            useAll1Btn.Click += (s, e) => ApplyDecisionToSelection(MergeDecision.Use1, _ => true);
            var useAll2Btn = new AntdUI.Button
            {
                Name = "ModMergeTool.UseAll2", Text = LanguageManager.Instance.GetText("ModMergeTool.UseAll2", "全用2"),
                Type = AntdUI.TTypeMini.Success, Dock = DockStyle.Left, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            useAll2Btn.Click += (s, e) => ApplyDecisionToSelection(MergeDecision.Use2, _ => true);
            var useAllMergeBtn = new AntdUI.Button
            {
                Name = "ModMergeTool.UseAllMerge", Text = LanguageManager.Instance.GetText("ModMergeTool.UseAllMerge", "全用合"),
                Type = AntdUI.TTypeMini.Warn, Dock = DockStyle.Left, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            useAllMergeBtn.Click += (s, e) => ApplyDecisionToSelection(MergeDecision.Merged, i => i.IsEdited || i.IsMerged);
            var useAllRenameBtn = new AntdUI.Button
            {
                Name = "ModMergeTool.UseAllRename", Text = LanguageManager.Instance.GetText("ModMergeTool.UseAllRename", "全用改"),
                Type = AntdUI.TTypeMini.Warn, Dock = DockStyle.Left, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            useAllRenameBtn.Click += (s, e) => ApplyDecisionToSelection(MergeDecision.Renamed, i => i.MergeDecision == MergeDecision.Renamed || !string.IsNullOrEmpty(i.RenamedPath1) || !string.IsNullOrEmpty(i.RenamedPath2));
            // 功能按钮加入 controlRow（Dock=Left 反向插入：最后加的最左）
            // ⇒ 视觉顺序：全选 全不选 | 展开 收缩 | 全用1 全用2 全用合 全用改
            controlRow.Controls.Add(useAllRenameBtn);
            controlRow.Controls.Add(useAllMergeBtn);
            controlRow.Controls.Add(useAll2Btn);
            controlRow.Controls.Add(useAll1Btn);
            controlRow.Controls.Add(MakeSeparator());
            controlRow.Controls.Add(_btnMergeFlatMode);
            controlRow.Controls.Add(_btnMergeTreeMode);
            controlRow.Controls.Add(_btnMergeCollapseAll);
            controlRow.Controls.Add(_btnMergeExpandAll);
            SwitchMergeViewMode(SettingsManager.MergeFlatViewMode, save: false, refresh: false);
            controlRow.Controls.Add(MakeSeparator());
            controlRow.Controls.Add(uncheckAllBtn);
            controlRow.Controls.Add(checkAllBtn);
            controlRow.Controls.Add(new Label
            {
                Name = "ModMergeTool.OperationLabel", Text = LanguageManager.Instance.GetText("ModMergeTool.OperationLabel", "操作："),
                Dock = DockStyle.Left, AutoSize = true, Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = FontHelper.UiFont(8),
                ForeColor = Color.FromArgb(140, 140, 140),
                Padding = new Padding(0, 7, 8, 0)
            });
            // 默认筛选"冲突"（_currentFilterTag 已为 conflict），高亮对应按钮
            UpdateFilterButtonHighlight(_currentFilterTag);

            // gridPanel — 单表布局（方案3：单文件树）
            var gridPanel = new AntdUI.Panel { Dock = DockStyle.Fill, Back = Color.Transparent, Padding = new Padding(0, 6, 0, 10) };
            _mergeTable = new AntdUI.Table
            {
                Dock = DockStyle.Fill, VisibleHeader = true, Bordered = true,
                BorderColor = ThemeManager.TableGridColor,   // 网格线加深（列头分割线=拖宽抓手，内置色对比弱）
                Font = FontHelper.UiFont(9), EditMode = AntdUI.TEditMode.None,
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill, VirtualMode = true, RowHeight = 26,
                ShowCheckBg = false, EnableHeaderResizing = true
            };
            // 采用列（复选框）：用 (key,title) 构造使 NoTitle=false → 不生成表头统控复选框（改由工具栏"全选/全不选"按钮控制），绑定 MergeRow.Adopt
            // 列宽默认策略（用户定稿）：采用/处理/差异/归属=auto（内容短且可预估），路径/说明/本地化=150px（防虚拟渲染滚动抖动）
            _adoptCheckCol = new AntdUI.ColumnCheck("Adopt", "") { Width = "auto" };
            _mergeTable.Columns.Add(_adoptCheckCol);
            var mergeDecisionCol = new AntdUI.Column("Decision", LanguageManager.Instance.GetText("ModMergeTool.ColDecision", "处理")) { Width = "auto", ColAlign = AntdUI.ColumnAlign.Center };
            mergeDecisionCol.Render = (v, r, i) =>
            {
                if (r is MergeRow row && !row.IsDirectory)
                {
                    if (row.Status == CompareStatus.Same) return null;
                    if (row.IsDirectory) return null;
                    if (row.Status == CompareStatus.Only1 || row.Status == CompareStatus.Only2)
                        return null;
                    // 冲突行（含已改名/已对比编辑/已决策）统一走决策按钮
                    if (row.Status == CompareStatus.Conflict || row.Status == CompareStatus.Renamed)
                    {
                        string txt;
                        AntdUI.TTypeMini t = AntdUI.TTypeMini.Error;
                        switch (row.Item?.MergeDecision)
                        {
                            case MergeDecision.Use1: txt = LanguageManager.Instance.GetText("ModMergeTool.DecisionUse1", "使用1"); t = AntdUI.TTypeMini.Info; break;
                            case MergeDecision.Use2: txt = LanguageManager.Instance.GetText("ModMergeTool.DecisionUse2", "使用2"); t = AntdUI.TTypeMini.Success; break;
                            case MergeDecision.Merged: txt = LanguageManager.Instance.GetText("ModMergeTool.DecisionMerged", "合"); t = AntdUI.TTypeMini.Warn; break;
                            case MergeDecision.Renamed: txt = LanguageManager.Instance.GetText("ModMergeTool.DecisionRenamedShort", "改"); t = AntdUI.TTypeMini.Warn; break;
                            default: txt = LanguageManager.Instance.GetText("ModMergeTool.DecisionNone", "决策"); break;
                        }
                        return new AntdUI.CellButton(row.FullPath, txt + " \u25BE", t);
                    }
                }
                return null;
            };
            _mergeTable.Columns.Add(mergeDecisionCol);
            var mergeDiffCol = new AntdUI.Column("Diff", LanguageManager.Instance.GetText("ModMergeTool.ColDiff", "差异")) { Width = "auto", ColAlign = AntdUI.ColumnAlign.Center, Align = AntdUI.ColumnAlign.Left };
            mergeDiffCol.Render = (v, r, i) =>
            {
                // 差异列：改/合 附专属图标（EditOutlined=改名、MergeCellsOutlined=合并，经 SvgDb.Custom 解析），其余保持颜色标签
                if (r is MergeRow row && !string.IsNullOrEmpty(row.DiffIcon))
                {
                    switch (row.DiffIcon)
                    {
                        case "改":
                            return new AntdUI.ICell[] { new AntdUI.CellImage("EditOutlined", PathOrange), new AntdUI.CellTag(LanguageManager.Instance.GetText("ModMergeTool.DecisionRenamedShort", "改"), AntdUI.TTypeMini.Warn) };
                        case "合":
                            return new AntdUI.ICell[] { new AntdUI.CellImage("MergeCellsOutlined", PathOrange), new AntdUI.CellTag(LanguageManager.Instance.GetText("ModMergeTool.DecisionMerged", "合"), AntdUI.TTypeMini.Warn) };
                        case "X":
                            return new AntdUI.CellTag("X", AntdUI.TTypeMini.Error);
                        default:
                            return new AntdUI.CellTag(row.DiffIcon, AntdUI.TTypeMini.Default);
                    }
                }
                return null;
            };
            _mergeTable.Columns.Add(mergeDiffCol);
            var mergeOwnerCol = new AntdUI.Column("Owner", LanguageManager.Instance.GetText("ModMergeTool.ColOwner", "归属")) { Width = "auto", ColAlign = AntdUI.ColumnAlign.Center };
            mergeOwnerCol.Render = (v, r, i) =>
            {
                // 注意：Render 只能返回 ICell/ICell[]（或 string），不能返回 Control，否则会被 ToString 显示成 "AntdUI.Panel"
                if (r is MergeRow row && !string.IsNullOrEmpty(row.OwnerIcon))
                {
                    switch (row.OwnerIcon)
                    {
                        case "1 2":
                            return new AntdUI.ICell[] { new AntdUI.CellTag("1", AntdUI.TTypeMini.Info), new AntdUI.CellTag("2", AntdUI.TTypeMini.Success) };
                        case "1":
                            return new AntdUI.ICell[] { new AntdUI.CellTag("1", AntdUI.TTypeMini.Info) };
                        case "2":
                            return new AntdUI.ICell[] { new AntdUI.CellTag("2", AntdUI.TTypeMini.Success) };
                        default:
                            return new AntdUI.CellTag(row.OwnerIcon, AntdUI.TTypeMini.Default);
                    }
                }
                return null;
            };
            _mergeTable.Columns.Add(mergeOwnerCol);
            // 路径列：默认 150px 固定（防虚拟渲染滚动抖动；用户可拖宽，关窗后记忆）
            _mergeNameCol = new AntdUI.Column("Name", LanguageManager.Instance.GetText("ModMergeTool.ColName", "路径")) { Width = "150" }.SetTree("Children");
            _mergeNameCol.Render = (v, r, i) =>
            {
                if (r is MergeRow row)
                {
                    var rename = BuildPathRenameSegments(row);
                    if (rename != null) return rename;
                    var col = GetPathTextColor(row);
                    return col.HasValue ? new AntdUI.CellText(row.DisplayName ?? "", col.Value) : new AntdUI.CellText(row.DisplayName ?? "");
                }
                return new AntdUI.CellText("");
            };
            _mergeTable.Columns.Add(_mergeNameCol);
            var mergeDescCol = new AntdUI.Column("Desc", LanguageManager.Instance.GetText("ModMergeTool.ColDesc", "说明")) { Width = "150" };
            mergeDescCol.Render = (v, r, i) =>
            {
                if (r is MergeRow row && !row.IsDirectory)
                {
                    string txt = row.Desc ?? "";
                    if (string.IsNullOrEmpty(txt)) txt = "—";
                    return new AntdUI.CellText(txt);
                }
                return new AntdUI.CellText("");
            };
            _mergeTable.Columns.Add(mergeDescCol);
            var mergeLocCol = new AntdUI.Column("Localized", LanguageManager.Instance.GetText("ModMergeTool.ColLocalized", "本地化")) { Width = "150" };
            mergeLocCol.Render = (v, r, i) =>
            {
                if (r is MergeRow row && !row.IsDirectory)
                {
                    string txt = row.Localized ?? "";
                    if (string.IsNullOrEmpty(txt)) txt = "—";
                    var ct = new AntdUI.CellText(txt);
                    // 本地化有值时用主题色高亮，无值时灰色占位
                    if (!string.IsNullOrEmpty(row.Localized)) ct.Fore = ThemeManager.Text;
                    return ct;
                }
                return new AntdUI.CellText("");
            };
            _mergeTable.Columns.Add(mergeLocCol);
            // 列定义就绪后应用记忆列宽（TableWidthStore；首次调用捕获原始列宽供重置恢复）+ MouseUp 防抖自动保存
            TableWidthStore.Apply(TableWidthStore.TableId.MergeTable, _mergeTable);
            TableWidthStore.AttachAutoSave(TableWidthStore.TableId.MergeTable, _mergeTable);
            // 合并表无其它右键菜单，挂上「重置本表列宽记忆」入口
            TableWidthStore.AttachResetMenu(TableWidthStore.TableId.MergeTable, _mergeTable);

            _mergeTable.CellButtonClick += OnMergeTableCellButtonClick;
            _mergeTable.CellDoubleClick += OnMergeTableCellDoubleClick;
            _mergeTable.CellClick += OnMergeTableCellClick;
            _mergeTable.ExpandChanged += (s, e) =>
            {
                var row = e.Record as MergeRow;
                if (row == null || !row.IsDirectory) return;
                row.Expand = e.Expand;
            };
            _mergeTable.CheckedChanged += (s, e) =>
            {
                if (_syncingChecks) return;
                if (e.Record is MergeRow row)
                {
                    _syncingChecks = true;
                    try
                    {
                        // 数据层：仅同步 IsChecked（打包开关）
                        SetAdoptChecked(row, e.Value);
                        // 显示层：同步 Adopt 字段并沿 Parent 链局部重算祖先（O(深度)），不重建树、不重绑、不刷统计。
                        if (row.IsDirectory)
                        {
                            ApplyCheckSubtree(row, e.Value);
                            RecomputeCheckAncestors(row);
                        }
                        else
                        {
                            row.Adopt = e.Value;
                            RecomputeCheckAncestors(row);
                        }
                    }
                    finally { _syncingChecks = false; }
                    // 目录行会一次改变多个子行复选框，需重建显示；叶子行由表格原生翻转（不回发 Header Overall，也无缓冲），
                    // 无需重建，避免"每次点击都 LoadLayout"导致点击失效的回归。
                    if (row.IsDirectory) ScheduleCheckRelayout();
                }
            };
            // 已去除表头统控复选框（NoTitle=false），故不再监听 CheckedOverallChanged；全选/全不选由工具栏按钮负责。
            gridPanel.Controls.Add(_mergeTable);
            // 空白占位跟随当前筛选内容（如：无冲突数据 / 无已改名数据），随筛选变化实时刷新
            UpdateEmptyFilterText();
            // 保留旧三表字段为 null（兼容旧代码空判断），不再加入界面

            // historySplit: 顶置历史，点间隔条折叠/展开（复刻 ArzArcEditorForm._dbrSplit 的 ArrowSplitter）
            bool historyCollapsed = SettingsManager.MergeHistoryPanelCollapsed;
            _historySplit = new ArrowSplitter
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                CollapsePanel = AntdUI.Splitter.ADCollapsePanel.Panel1,
                Lazy = false,
                SplitterWidth = 24, // 与右侧分割栏（默认 24）一致，视觉风格统一
                CollapsedTextKey = "Editor.CollapseHistory",
                ExpandedTextKey = "Editor.ExpandHistory"
            };
            var historyPanel = new Panel { Dock = DockStyle.Fill, Back = Color.Transparent, Padding = new Padding(0, 0, 0, 0) };
            var historyHeader = new Panel { Dock = DockStyle.Top, Height = 28, Back = Color.Transparent };
            var historyTitle = new Label
            {
                Name = "ModMergeTool.History", Text = LanguageManager.Instance.GetText("ModMergeTool.History", "历史记录"),
                Dock = DockStyle.Left, AutoSize = true,
                Font = FontHelper.UiFont(9), ForeColor = ThemeManager.TextTertiary,
                Padding = new Padding(6, 6, 0, 0)
            };
            var historyClearBtn = new AntdUI.Button
            {
                Name = "ModMergeTool.HistoryClear", Text = LanguageManager.Instance.GetText("ModMergeTool.HistoryClear", "清空历史"),
                Type = AntdUI.TTypeMini.Default, Dock = DockStyle.Right, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            historyClearBtn.Click += OnHistoryClearClick;
            historyHeader.Controls.Add(historyClearBtn);
            historyHeader.Controls.Add(historyTitle);
            _historyTable = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = true,
                Bordered = true,
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill,
                RowHeight = 30,
                GapCell = 4,
                Font = FontHelper.UiFont(9),
                EmptyText = LanguageManager.Instance.GetText("ModMergeTool.HistoryEmpty", "暂无历史记录")
            };
            _historyTable.Columns.Add(new AntdUI.Column("BasePath", LanguageManager.Instance.GetText("ModMergeTool.HistoryBase", "基准包")) { Width = "33%" });
            _historyTable.Columns.Add(new AntdUI.Column("ModPath", LanguageManager.Instance.GetText("ModMergeTool.HistoryMod", "并入包")) { Width = "33%" });
            _historyTable.Columns.Add(new AntdUI.Column("OpenTimeText", LanguageManager.Instance.GetText("ModMergeTool.HistoryTime", "打开时间")) { Width = "15%" });
            _historyTable.Columns.Add(new AntdUI.Column("OpCells", LanguageManager.Instance.GetText("ModMergeTool.HistoryOp", "操作")) { Width = "19%", ColAlign = AntdUI.ColumnAlign.Center });
            _historyTable.CellButtonClick += OnHistoryTableButtonClick;
            _historyTable.CellPaintBegin += (s, e) =>
            {
                if (e.RowType == AntdUI.RowType.Column || e.RowType == AntdUI.RowType.Summary || e.Record is not MergeHistoryRow row) return;
                if (e.Column == null) return;
                if (e.Column.Key == "BasePath")
                    PathCheckHelper.ApplyMissingCellStyle(e, "BasePath", !row.ExistsBase, _historyTable.Font);
                else if (e.Column.Key == "ModPath")
                    PathCheckHelper.ApplyMissingCellStyle(e, "ModPath", !row.ExistsMod, _historyTable.Font);
            };
            historyPanel.Controls.Add(_historyTable);
            historyPanel.Controls.Add(historyHeader);
            RefreshHistoryTable();
            _historySplit.Panel1.Controls.Add(historyPanel);
            var contentPanel = new Panel { Dock = DockStyle.Fill, Back = Color.Transparent };
            // 主内容区（左）+ 右侧撤销便捷栏（右）：右侧面板可折叠并持久化
            _rightSplit = new ArrowSplitter
            {
                Dock = DockStyle.Fill,
                CollapsePanel = AntdUI.Splitter.ADCollapsePanel.Panel2,
                CollapsedTextKey = "ModMergeTool.CollapseEditPanel",
                ExpandedTextKey = "ModMergeTool.ExpandEditPanel",
                Lazy = false
            };
            _rightSplit.Panel1.Controls.Add(contentPanel);
            BuildMergeRightPanel(_rightSplit.Panel2);
            _historySplit.Panel2.Controls.Add(_rightSplit);

            // bottomPanel: 输出路径 + 开始合并/取消（置于内容面板底部）
            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 36, Back = Color.Transparent };

            var outputLabel = new Label
            {
                Name = "ModMergeTool.OutputPath", Text = LanguageManager.Instance.GetText("ModMergeTool.OutputPath", "输出文件:"),
                Dock = DockStyle.Left, AutoSize = true,
                Padding = new Padding(0, 8, 0, 0), Font = FontHelper.UiFont(9)
            };
            _outputPathInput = new TextBox
            {
                Dock = DockStyle.Fill,
                Name = "ModMergeTool.OutputPathPlaceholder",   // Name=key：TextBox 只刷新占位符
                PlaceholderText = LanguageManager.Instance.GetText("ModMergeTool.OutputPathPlaceholder", "选择输出文件..."),
                Font = FontHelper.UiFont(9)
            };
            var outputBrowseBtn = new AntdUI.Button
            {
                Name = "Common.Browse", Text = LanguageManager.Instance.GetText("Common.Browse", "浏览"),
                Type = AntdUI.TTypeMini.Default, Dock = DockStyle.Right, AutoSize = true, Font = FontHelper.UiFont(9)
            };
            outputBrowseBtn.Click += (s, e) => BrowseSaveFile(_outputPathInput);
            _startButton = new AntdUI.Button
            {
                Name = "ModMergeTool.StartButton", Text = LanguageManager.Instance.GetText("ModMergeTool.StartButton", "开始合并"),
                Type = AntdUI.TTypeMini.Primary, Dock = DockStyle.Right,
                AutoSize = true, Font = FontHelper.UiFont(9), Enabled = false
            };
            _startButton.Click += OnMergeClick;
            bottomPanel.Controls.Add(_outputPathInput);
            bottomPanel.Controls.Add(_startButton);
            bottomPanel.Controls.Add(outputBrowseBtn);
            bottomPanel.Controls.Add(outputLabel);

            // 组装 contentPanel（Dock.Fill 在底层，Top 从后往前加）
            BuildConflictBar(centerRow);
            // ── 底部日志框（复用主界面公共 LogView：四级筛选+文本过滤+导出/清空+折叠分组）──
            // 仅接收"开始合并"后的过程日志与产物报告（合并前的筛选/勾选操作不写入）。
            // 展开/收缩（与 ARZ 编辑器日志区同款）：差异表格在 Panel1、日志框在 Panel2，
            // 拖拽间隔条调高度、点间隔条折叠/展开，折叠状态持久化（MergeLogPanelCollapsed）。
            _mergeLogView = new LogView
            {
                Dock = DockStyle.Fill,
                ExportTitle = LanguageManager.Instance.GetText("ModMergeTool.LogExportTitle", "MOD合并日志")
            };
            _logSplit = new ArrowSplitter
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                CollapsePanel = AntdUI.Splitter.ADCollapsePanel.Panel2,
                CollapsedTextKey = "Editor.CollapseLog",
                ExpandedTextKey = "Editor.ExpandLog",
                Lazy = false
            };
            _logSplit.Panel1.Controls.Add(gridPanel);
            _logSplit.Panel2.Controls.Add(_mergeLogView);
            _logSplit.SplitPanelStateChanged += (s, e) =>
            {
                SettingsManager.MergeLogPanelCollapsed = !e.Value;
                SettingsManager.Save();
            };
            contentPanel.Controls.Add(_logSplit);  // 替代原 gridPanel 的 Fill 位（表格+日志框整体填充）
            contentPanel.Controls.Add(bottomPanel);
            contentPanel.Controls.Add(filterRow); // Dock=Top 反向布局：在 controlRow 之后处理 ⇒ 显示于其正下方（原 searchRow 已并入本行右侧）
            contentPanel.Controls.Add(controlRow);
            contentPanel.Controls.Add(scanArea); // 扫描统计区（左tag两行 + 右冲突大字），整行与包行、操作行分区
            contentPanel.Controls.Add(pathRow); // 包1/包2 两张整体卡片（Alert提示+标签+输入框+浏览按钮）
            contentPanel.Controls.Add(hintLabel);

            // 分割条折叠状态与持久化（复刻 ArzArcEditorForm._dbrSplit 逻辑，限高35%防从主界面启动占90%）
            int GetHistoryLimitedHeight() => Math.Min(180, Math.Max(80, (int)(this.ClientSize.Height * 0.35)));
            _historySplit.SplitPanelStateChanged += (s, e) =>
            {
                if (_historySplit.Height == 0) return;
                if (!e.Value)
                {
                    _historySplit.Panel1MinSize = 0;
                    _historySplit.SplitterDistance = 0;
                }
                else
                {
                    int h = GetHistoryLimitedHeight();
                    if (h > 0 && h < _historySplit.Height - _historySplit.Panel2MinSize - _historySplit.SplitterWidth)
                        _historySplit.SplitterDistance = h;
                    _historySplit.Panel1MinSize = (int)(80 * DpiManager.Scale);
                }
                SettingsManager.MergeHistoryPanelCollapsed = !e.Value;
                SettingsManager.Save();
            };
            _rightSplit.SplitPanelStateChanged += (s, e) =>
            {
                SettingsManager.MergeRightPanelCollapsed = !e.Value;
                SettingsManager.Save();
            };
            // 延迟设置初始折叠状态，避免 InitializeUI 时 Height 为 0 导致 SplitterDistance 校验失败
            this.Shown += (s, e) =>
            {
                _historySplit.Panel2MinSize = 260;
                if (historyCollapsed)
                {
                    _historySplit.Panel1MinSize = 0;
                    _historySplit.SplitterDistance = 0;
                    _historySplit.SplitPanelState = false;
                }
                else
                {
                    int h = GetHistoryLimitedHeight();
                    if (_historySplit.Height > 0 && h > 0)
                    {
                        int max = _historySplit.Height - _historySplit.Panel2MinSize - _historySplit.SplitterWidth;
                        if (h > max) h = max;
                        _historySplit.SplitterDistance = h;
                    }
                    _historySplit.Panel1MinSize = (int)(80 * DpiManager.Scale);
                    _historySplit.SplitPanelState = true;
                }
                // 右侧撤销便捷栏：默认展开（右侧面板约 180px），按上次状态折叠恢复。
                // 注意：SplitterDistance 表示 Panel1(左) 宽度；右侧面板宽度 = Width - SplitterDistance - SplitterWidth。
                // Panel2MinSize 必须为 0，Collapse 才会真正隐藏（否则折叠后仍残留 Panel2MinSize 宽度）。
                if (_rightSplit != null && _rightSplit.Width > 0)
                {
                    _rightSplit.Panel2MinSize = 0;
                    try
                    {
                        int targetRight = (int)(180 * DpiManager.Scale);
                        int dist = _rightSplit.Width - targetRight - _rightSplit.SplitterWidth;
                        int min = _rightSplit.Panel1MinSize;
                        if (dist < min) dist = min;
                        _rightSplit.SplitterDistance = dist;
                    }
                    catch { }
                    if (SettingsManager.MergeRightPanelCollapsed) _rightSplit.Collapse();
                }
                // 日志区分割条：日志区初始高约 220（拖拽间隔条可调），按持久化状态恢复折叠。
                // Panel2 最小高度必须为 0：AntdUI 的折叠 = "SplitterDistance = 高度 - 间隔条 - Panel2MinSize"，
                // 留下限会导致折叠后残留一条工具条高度（露出筛选/过滤/导出/清空行）。
                if (_logSplit != null && _logSplit.Height > 0)
                {
                    _logSplit.SetMinSizes((int)(200 * DpiManager.Scale), 0);
                    int logH = DpiManager.S(220);
                    int dist = _logSplit.Height - logH - _logSplit.SplitterWidth;
                    if (dist < _logSplit.Panel1MinSize) dist = _logSplit.Panel1MinSize;
                    int max = _logSplit.Height - _logSplit.Panel2MinSize - _logSplit.SplitterWidth;
                    if (dist > max) dist = max;
                    try { _logSplit.SplitterDistance = dist; } catch { }
                    if (SettingsManager.MergeLogPanelCollapsed)
                    {
                        try { _logSplit.Collapse(); } catch { }
                    }
                }
            };
            this.SizeChanged += (s, e) =>
            {
                if (_historySplit != null && _historySplit.SplitPanelState && _historySplit.Height > 0)
                {
                    int lim = GetHistoryLimitedHeight();
                    if (lim > 0 && _historySplit.SplitterDistance > lim)
                        _historySplit.SplitterDistance = lim;
                }
            };

            mainPanel.Controls.Add(_historySplit);

            _loadingOverlay = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(200, 245, 245, 245),
                Visible = false
            };
            _loadingLabel = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = FontHelper.UiFont(14),
                ForeColor = Color.Gray
            };
            _loadingOverlay.Controls.Add(_loadingLabel);
            mainPanel.Controls.Add(_loadingOverlay);

            Controls.Add(mainPanel);
        }

        private void EnableDragDropFile(TextBox textBox)
        {
            textBox.AllowDrop = true;
            textBox.HandDragFolder = false;
            textBox.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
                else
                    e.Effect = DragDropEffects.None;
            };
            textBox.DragDrop += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files.Length > 0)
                        textBox.Text = files[0];
                }
            };
        }

        private void BrowseFile(TextBox input)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "ARZ/ARC文件|*.arz;*.arc|ARZ文件|*.arz|ARC文件|*.arc|所有文件|*.*";
                if (dialog.ShowDialog() == DialogResult.OK)
                    input.Text = dialog.FileName;
            }
        }

        private void BrowseSaveFile(TextBox input)
        {
            using (var dialog = new SaveFileDialog())
            {
                if (_packageType == "ARZ")
                    dialog.Filter = "ARZ文件|*.arz";
                else if (_packageType == "ARC")
                    dialog.Filter = "ARC文件|*.arc";
                else
                    dialog.Filter = "ARZ/ARC文件|*.arz;*.arc";
                if (dialog.ShowDialog() == DialogResult.OK)
                    input.Text = dialog.FileName;
            }
        }

        private void OnPathChanged(object sender, EventArgs e)
        {
            _basePath = _basePathInput.Text?.Trim();
            _modPath = _modPathInput.Text?.Trim();

            if (!string.IsNullOrEmpty(_basePath) && !string.IsNullOrEmpty(_modPath))
            {
                string baseExt = Path.GetExtension(_basePath).ToLower();
                string modExt = Path.GetExtension(_modPath).ToLower();

                if (baseExt == modExt && (baseExt == ".arz" || baseExt == ".arc"))
                {
                    _packageType = baseExt == ".arz" ? "ARZ" : "ARC";
                    _scanButton.Enabled = true;
                    _outputPathInput.Text = Path.Combine(
                        Path.GetDirectoryName(_basePath),
                        Path.GetFileNameWithoutExtension(_basePath) + "_merged" + baseExt);
                }
                else
                {
                    _packageType = null;
                    _scanButton.Enabled = false;
                    NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.ErrorFormatMismatch", "两个包必须同为 ARZ 或同为 ARC"));
                }
            }
            else
            {
                _packageType = null;
                _scanButton.Enabled = false;
            }
        }

        /// <summary>
        /// 设置窗保存本地化配置后统一调用的公开刷新入口（含忙状态守卫，扫描/合并中或无数据时跳过）。
        /// </summary>
        public void RefreshLocalization()
        {
            if (_isRunning || _mergeRows == null) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                _currentLanguageDict = LocalizationHelper.MergeLanguagePacks(SettingsManager.ActivePackKeys);
                _isLanguagePackActive = _currentLanguageDict != null;
                RebuildLocalizedCache();
                ApplyFilter();
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void OpenCompareDialog(CompareItem item)
        {
            if (item == null || !item.CanCompare) return;
            // 互斥：已改名文件不再进入对比编辑（改名与编辑只能选其一）
            if (item.IsRenamed || item.MergeDecision == MergeDecision.Renamed) return;

            if (item.Lines1 == null && item.Lines2 == null)
                LoadItemContent(item);

            if (item.Lines1 == null) item.Lines1 = Array.Empty<string>();
            if (item.Lines2 == null) item.Lines2 = Array.Empty<string>();

            if (_templates == null)
            {
                var (templates, _, _, _, _) = Program.LoadTemplates(SettingsManager.TemplateEntries);
                _templates = templates?.Values?.ToList() ?? new List<TemplateNode>();
            }

            if (string.IsNullOrEmpty(_tempDir))
            {
                _tempDir = Path.Combine(Path.GetTempPath(), "GDPackEditor_merge", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempDir);
            }

            string tempFilePath = Path.Combine(_tempDir, item.FileName);
            Action<string> onEdited = (recordName) =>
            {
                // 互斥：对比编辑保存后清除此前改名状态，保证「只改或只合」（防御性，正常流程因菜单互斥不会同时存在）
                if (item.IsRenamed || item.MergeDecision == MergeDecision.Renamed)
                {
                    DeleteRenamedTempFiles(item, clearFields: true);
                    item.RenamedPath = null;
                    item.RenamedPath1 = null;
                    item.RenamedPath2 = null;
                }
                item.IsEdited = true;
                item.MergeDecision = MergeDecision.Merged;
                item.EditedTempPath = tempFilePath;
                // P3：编辑保存 → 仅同步该行 DiffIcon 后按筛选情形刷新：
                // "已编辑"筛选中该行刚进入集合 → 重建显示集合；其他筛选 → 整表纯重绘（无重算、无重绑、无统计）。
                if (_mergeRows != null)
                    foreach (var r in FlattenMergeRows(_mergeRows))
                        if (!r.IsDirectory && r.Item == item) { r.DiffIcon = "合"; break; }
                if (_currentFilterTag == "edited") ApplyFilter();
                else ScheduleCheckRelayout();
                RefreshEditList();
                // 对比编辑保存=冲突已决策，刷新 LED 条（该路径不经过 UpdateStats）
                UpdateConflictBar();
            };

            AntdUI.BaseForm dlg;
            if (CompareItem.IsTextFile(item.FileName))
            {
                dlg = new DiffTextDialog(
                    item.FileName, item.Lines1, item.Lines2,
                    tempFilePath, onEdited, EncodingService.TempWriteEncoding);
            }
            else
            {
                string pkg1Name = !string.IsNullOrEmpty(_basePath) ? Path.GetFileNameWithoutExtension(_basePath) : null;
                string pkg2Name = !string.IsNullOrEmpty(_modPath) ? Path.GetFileNameWithoutExtension(_modPath) : null;
                dlg = new MergeCompareDialog(
                    item.FileName, item.Lines1, item.Lines2,
                    tempFilePath, onEdited, _templates,
                    pkg1Name, pkg2Name);
            }
            dlg.ShowDialog(this);
        }

        private void LoadItemContent(CompareItem item)
        {
            bool hasBase = !string.IsNullOrEmpty(_basePath) && File.Exists(_basePath);
            bool hasMod = !string.IsNullOrEmpty(_modPath) && File.Exists(_modPath);

            if (_packageType == "ARZ")
            {
                if (hasBase)
                {
                    try
                    {
                        using (var fs = new FileStream(_basePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var reader = new ARZReader(fs);
                            string text = reader.GetDecompressedTextByName(item.FileName);
                            if (text != null)
                                item.Lines1 = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                        }
                    }
                    catch { }
                }
                if (hasMod)
                {
                    try
                    {
                        using (var fs = new FileStream(_modPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var reader = new ARZReader(fs);
                            string text = reader.GetDecompressedTextByName(item.FileName);
                            if (text != null)
                                item.Lines2 = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                        }
                    }
                    catch { }
                }
            }
            else if (_packageType == "ARC")
            {
                if (hasBase)
                {
                    try
                    {
                        using (var fs = new FileStream(_basePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var arc = new ARCFile();
                            arc.ReadStream(fs);
                            int idx = arc.GetEntryIndex(item.FileName);
                            if (idx >= 0)
                            {
                                using (var ms = new MemoryStream())
                                {
                                    arc.UnpackToStream(arc.toc[idx], ms);
                                    ms.Position = 0;
                                    item.Lines1 = TemplateHelper.DecodeText(ms.ToArray())
                                        .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                                }
                            }
                        }
                    }
                    catch { }
                }
                if (hasMod)
                {
                    try
                    {
                        using (var fs = new FileStream(_modPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var arc = new ARCFile();
                            arc.ReadStream(fs);
                            int idx = arc.GetEntryIndex(item.FileName);
                            if (idx >= 0)
                            {
                                using (var ms = new MemoryStream())
                                {
                                    arc.UnpackToStream(arc.toc[idx], ms);
                                    ms.Position = 0;
                                    item.Lines2 = TemplateHelper.DecodeText(ms.ToArray())
                                        .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        private async void OnStartClick(object sender, EventArgs e)
        {
            if (_isRunning) return;

            // 扫描前检测包文件是否存在，缺失则 Notification 提示并中止
            string checkBase = _basePathInput.Text?.Trim();
            string checkMod = _modPathInput.Text?.Trim();
            var missingMsgs = new List<string>(2);
            if (!PathCheckHelper.Exists(checkBase))
                missingMsgs.Add(string.Format(LanguageManager.Instance.GetText("ModMergeTool.FileMissingPkg1", "包1文件不存在：{0}"), checkBase));
            if (!PathCheckHelper.Exists(checkMod))
                missingMsgs.Add(string.Format(LanguageManager.Instance.GetText("ModMergeTool.FileMissingPkg2", "包2文件不存在：{0}"), checkMod));
            if (missingMsgs.Count > 0)
            {
                NotifyWarn(string.Join("\n", missingMsgs));
                return;
            }

            _isRunning = true;
            _scanButton.Enabled = false;
            _startButton.Enabled = false;
            _cts = new CancellationTokenSource();
            _allItems.Clear();
            _filteredItems.Clear();
            // 重新扫描：清理临时文件、重置临时目录、清空已编辑/已改名列表
            CleanupTempDir();
            _tempDir = null;
            _mergeRows = null;
            RefreshEditList();

            string scanHint = LanguageManager.Instance.GetText("ModMergeTool.Scanning", "正在扫描...");
            var spinCfg = new AntdUI.Spin.Config
            {
                Text = scanHint,
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                CancelTokenSource = _cts
            };
            try
            {
                var targetCtrl = (Control)_mergeTable ?? this;
                await AntdUI.Spin.open(targetCtrl, spinCfg, async cfg =>
                {
                    cfg.Text = scanHint;
                    var scanPctLabel = LanguageManager.Instance.GetText("ModMergeTool.Scanning", "正在扫描...");
                    var progress = new ThrottledProgressCallback((pct, msg) =>
                    {
                        try
                        {
                            string displayText = pct >= 0 ? $"{scanPctLabel} {pct}%" : msg;
                            if (InvokeRequired)
                                BeginInvoke((Action)(() => { cfg.Text = displayText; if (pct >= 0) cfg.Value = pct / 100F; }));
                            else { cfg.Text = displayText; if (pct >= 0) cfg.Value = pct / 100F; }
                        }
                        catch { }
                    }, _cts.Token);

                    List<CompareItem> result = null;
                    if (_packageType == "ARZ")
                        result = await Task.Run(() => GDPackEditorAPI.MergeArzScan(_basePath, _modPath, progress, _cts.Token), _cts.Token);
                    else
                        result = await Task.Run(() => GDPackEditorAPI.MergeArcScan(_basePath, _modPath, progress, _cts.Token), _cts.Token);

                    _allItems = result ?? new List<CompareItem>();
                    for (int i = 0; i < _allItems.Count; i++)
                    {
                        _allItems[i].RealIndex = i;
                        _allItems[i].IsChecked = true;
                    }
                    if (_mergeNameCol != null) _mergeNameCol.Title = LanguageManager.Instance.GetText("ModMergeTool.ColName", "路径");
                    if (_adoptCheckCol != null) _adoptCheckCol.Title = LanguageManager.Instance.GetText("ModMergeTool.ColAdopt", "采用");

                    string mergeLocLabel = LanguageManager.Instance.GetText("ModMergeTool.MergeLangPacks", "正在合并语言包...");
                    cfg.Text = mergeLocLabel;
                    await Task.Yield();
                    var activeKeys = SettingsManager.ActivePackKeys;
                    if (activeKeys != null && activeKeys.Count > 0)
                    {
                        var keysCopy = new List<int>(activeKeys);
                        _currentLanguageDict = await Task.Run(() => LocalizationHelper.MergeLanguagePacks(keysCopy));
                        _isLanguagePackActive = _currentLanguageDict != null;
                        RebuildLocalizedCache();
                    }

                    string buildLabel = LanguageManager.Instance.GetText("ModMergeTool.BuildingStructure", "正在构建数据结构...");
                    cfg.Text = buildLabel;
                    await Task.Yield();
                    if (InvokeRequired)
                        Invoke((Action)(() => { ApplyFilter(); UpdateStats(); }));
                    else { ApplyFilter(); UpdateStats(); }
                    try { SettingsManager.AddMergeHistory(_basePathInput.Text?.Trim(), _modPathInput.Text?.Trim()); if (InvokeRequired) Invoke((Action)RefreshHistoryTable); else RefreshHistoryTable(); } catch { }

                    if (InvokeRequired)
                        Invoke((Action)(() =>
                        {
                            _scanButton.Text = LanguageManager.Instance.GetText("ModMergeTool.RescanButton", "重新扫描");
                            _startButton.Enabled = _allItems.Count > 0;
                            string scanDoneText = string.Format(LanguageManager.Instance.GetText("ModMergeTool.ScanComplete", "扫描完成，共 {0} 个条目"), _allItems.Count);
                            AntdUI.Notification.open(new AntdUI.Notification.Config(this,
                                LanguageManager.Instance.GetText("Common.Notice", "提示"),
                                scanDoneText,
                                AntdUI.TType.Success,
                                AntdUI.TAlignFrom.Top,
                                FontHelper.UiFont(11), 8));
                        }));
                    else
                    {
                        _scanButton.Text = LanguageManager.Instance.GetText("ModMergeTool.RescanButton", "重新扫描");
                        _startButton.Enabled = _allItems.Count > 0;
                        string scanDoneText = string.Format(LanguageManager.Instance.GetText("ModMergeTool.ScanComplete", "扫描完成，共 {0} 个条目"), _allItems.Count);
                        AntdUI.Notification.open(new AntdUI.Notification.Config(this,
                            LanguageManager.Instance.GetText("Common.Notice", "提示"),
                            scanDoneText,
                            AntdUI.TType.Success,
                            AntdUI.TAlignFrom.Top,
                            FontHelper.UiFont(11), 8));
                    }
                });
            }
            catch (OperationCanceledException)
            {
                NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.Cancelled", "已取消"));
            }
            catch (Exception ex)
            {
                NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.Error", "错误: ") + ex.Message);
            }
            finally
            {
                _isRunning = false;
                _scanButton.Enabled = true;
            }
        }

        private void ApplyFilter(string tag = null)
        {
            if (!string.IsNullOrEmpty(tag))
                _currentFilterTag = tag;

            UpdateFilterButtonHighlight(_currentFilterTag);

            var statusOrder = new Dictionary<CompareStatus, int>
            {
                [CompareStatus.Conflict] = 0,
                [CompareStatus.Only1] = 2,
                [CompareStatus.Only2] = 3,
                [CompareStatus.Same] = 4,
                [CompareStatus.Renamed] = 5
            };
            IEnumerable<CompareItem> items = _currentFilterTag switch
            {
                "conflict" => _allItems.Where(i => i.Status == CompareStatus.Conflict || i.MergeDecision == MergeDecision.Renamed),
                "same" => _allItems.Where(i => i.Status == CompareStatus.Same),
                "coexist" => _allItems.Where(i => i.MergeDecision == MergeDecision.Renamed),
                "edited" => _allItems.Where(i => i.IsEdited || i.MergeDecision == MergeDecision.Merged),
                _ => _allItems
            };

            if (!string.IsNullOrEmpty(_searchText))
            {
                string searchLower = _searchText.ToLowerInvariant();
                items = items.Where(i => MatchSearch(i, searchLower));
            }

_filteredItems = items.OrderBy(i => statusOrder.TryGetValue(i.Status, out int o) ? o : 99).ThenBy(i => i.FileName).ToList();
            RefreshMergeTree(_filteredItems);
            UpdateEmptyFilterText();
        }

        /// <summary>
        /// 更新筛选按钮组的选中高亮（选中项使用主色）。
        /// </summary>
        private void UpdateFilterButtonHighlight(string activeTag)
        {
            foreach (var btn in _filterButtons)
            {
                string tag = btn.Tag?.ToString();
                bool active = string.Equals(tag, activeTag, StringComparison.Ordinal);
                if (string.Equals(tag, "conflict", StringComparison.Ordinal))
                {
                    // 冲突筛选：未选中=红边框透明底(Ghost)，选中=红背景(Ghost=false)，符合红边框/红背景视觉逻辑
                    btn.Type = AntdUI.TTypeMini.Error;
                    btn.Ghost = !active;
                    btn.BorderWidth = 2;
                }
                else
                {
                    btn.Ghost = false;
                    btn.BorderWidth = 0;
                    btn.Type = active ? AntdUI.TTypeMini.Primary : AntdUI.TTypeMini.Default;
                }
            }
        }

        private void UpdateStats()
        {
            if (_statsTagsLine2 == null) return;
            var s = ComputeStats();

            _statsTagsLine2.SuspendLayout();
            _statsTagsLine2b?.SuspendLayout();
            _statsTagsLine2.Controls.Clear();
            _statsTagsLine2b?.Controls.Clear();
            // 行一：包1总/包1独有/包2总/包2独有；行二：同名/相同/冲突
            AddStatsTag(_statsTagsLine2, "ModMergeTool.StatPkg1Total", "包1总", s.Pkg1Total, TagBlueBack, TagBlueFore);
            AddStatsTag(_statsTagsLine2, "ModMergeTool.StatPkg1Only", "包1独有", s.Only1, TagBlueBack, TagBlueFore);
            AddStatsTag(_statsTagsLine2, "ModMergeTool.StatPkg2Total", "包2总", s.Pkg2Total, TagGreenBack, TagGreenFore);
            AddStatsTag(_statsTagsLine2, "ModMergeTool.StatPkg2Only", "包2独有", s.Only2, TagGreenBack, TagGreenFore);
            AddStatsTag(_statsTagsLine2b, "ModMergeTool.StatSameName", "同名", s.SameName, TagDefaultBack, TagDefaultFore);
            AddStatsTag(_statsTagsLine2b, "ModMergeTool.StatSame", "相同", s.Same, TagDefaultBack, TagDefaultFore);
            AddStatsTag(_statsTagsLine2b, "ModMergeTool.StatConflict", "冲突", s.Conflict, TagRedBack, TagRedFore);
            _statsTagsLine2.ResumeLayout();
            _statsTagsLine2b?.ResumeLayout();
            UpdateConflictBar();
        }

        /// <summary>
        /// 取当前筛选标签对应的按钮本地化文字（全部/包1/包2/冲突/相同/已改名/已编辑）。
        /// </summary>
        private string GetCurrentFilterText()
        {
            foreach (var fe in _filterEntries)
                if (fe[1] == _currentFilterTag)
                    return GetFilterButtonText(fe[0]);
            return "";
        }

        /// <summary>
        /// 表格空白占位 =「无{当前筛选}数据」，随筛选/搜索/语言变化刷新。
        /// </summary>
        private void UpdateEmptyFilterText()
        {
            if (_mergeTable == null || _mergeTable.IsDisposed) return;
            _mergeTable.EmptyText = string.Format(
                LanguageManager.Instance.GetText("ModMergeTool.EmptyFilter", "无{0}数据"),
                GetCurrentFilterText());
        }

        /// <summary>
        /// 冲突数强显区（扫描后显示，与统计tag两行并排、整体居中）：冲突数=剩余未处理冲突（递减），
        /// 已处理=已决策冲突（累加）。冲突数&gt;0 显示红色「请处理冲突！」，=0 显示绿色「可以合并了！！」。
        /// 扫描前整条隐藏。
        /// </summary>
        private void UpdateConflictBar()
        {
            if (_conflictBarPanel == null || _conflictBarPanel.IsDisposed || _lblConflictNum == null) return;
            int remaining = _allItems.Count(i => i.Status == CompareStatus.Conflict && i.MergeDecision == MergeDecision.None);
            int processed = _allItems.Count(i => i.Status == CompareStatus.Conflict && i.MergeDecision != MergeDecision.None);
            _lblConflictNum.Text = remaining.ToString();
            _lblProcessedNum.Text = processed.ToString();
            _conflictBarPanel.Visible = _allItems.Count > 0;
            if (remaining > 0)
            {
                _lblStatus.Text = LanguageManager.Instance.GetText("ModMergeTool.ConflictRemind", "请处理冲突！");
                _lblStatus.ForeColor = PathRed;
            }
            else
            {
                _lblStatus.Text = LanguageManager.Instance.GetText("ModMergeTool.MergeReady", "可以合并了！！");
                _lblStatus.ForeColor = PathGreen;
            }
            // 竖分隔线跟随冲突数区同显同隐（扫描前不出现游离竖线）
            if (_vSepPanel != null)
                _vSepPanel.Visible = _allItems.Count > 0;
            // 面板尺寸随大字内容自适应（AntdUI.Panel 无 AutoSize，手动按流程内容重算），外层再由 CenterScanContent 居中
            _conflictBarFlow.PerformLayout();
            var need = _conflictBarFlow.PreferredSize;
            _conflictBarPanel.Size = new Size(need.Width + 8, need.Height + 4);
            _conflictBarFlow.Location = new Point(4, 2);
        }

        /// <summary>
        /// 构建冲突数强显区：前缀标签 + 红色数字 + 量词 + 已处理 + 绿色数字 + 量词 + 状态提示。
        /// 挂到 centerRow 中央组合容器内（位于竖分隔线之后），与统计两行 chip 整体居中并排。
        /// </summary>
        private void BuildConflictBar(Control parent)
        {
            _conflictBarPanel = new Panel
            {
                Back = Color.Transparent,
                Visible = false
            };
            _conflictBarFlow = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                Padding = new Padding(0)
            };

            Label MakeBarLabel(string text, int leftMargin = 0, Color? fore = null, string key = null)
            {
                return new Label
                {
                    Name = key,   // 非空时按 Name=语言key 参与切语言刷新；动态数字标签传 null
                    Text = text,
                    AutoSize = true,
                    Font = FontHelper.UiFont(16, FontStyle.Bold),
                    ForeColor = fore ?? ThemeManager.Text,
                    Margin = new Padding(leftMargin, 6, 0, 0)
                };
            }

            var lblConflictPrefix = MakeBarLabel(LanguageManager.Instance.GetText("ModMergeTool.ConflictCountPrefix", "冲突数："), key: "ModMergeTool.ConflictCountPrefix");
            _lblConflictNum = MakeBarLabel("0", 4, PathRed);
            var lblUnit1 = MakeBarLabel(LanguageManager.Instance.GetText("ModMergeTool.CountSuffix", "个"), 2, null, "ModMergeTool.CountSuffix");
            var lblProcessedPrefix = MakeBarLabel(LanguageManager.Instance.GetText("ModMergeTool.ProcessedCountPrefix", "已处理："), 20, null, "ModMergeTool.ProcessedCountPrefix");
            _lblProcessedNum = MakeBarLabel("0", 4, PathGreen);
            var lblUnit2 = MakeBarLabel(LanguageManager.Instance.GetText("ModMergeTool.CountSuffix", "个"), 2, null, "ModMergeTool.CountSuffix");
            _lblStatus = MakeBarLabel(LanguageManager.Instance.GetText("ModMergeTool.ConflictRemind", "请处理冲突！"), 20, null, "ModMergeTool.ConflictRemind");

            _conflictBarFlow.Controls.Add(lblConflictPrefix);
            _conflictBarFlow.Controls.Add(_lblConflictNum);
            _conflictBarFlow.Controls.Add(lblUnit1);
            _conflictBarFlow.Controls.Add(lblProcessedPrefix);
            _conflictBarFlow.Controls.Add(_lblProcessedNum);
            _conflictBarFlow.Controls.Add(lblUnit2);
            _conflictBarFlow.Controls.Add(_lblStatus);

            // 流程靠左排，整体在面板内垂直居中
            void CenterBarVertical(object s, EventArgs e)
            {
                if (_conflictBarFlow.Width > 0)
                    _conflictBarFlow.Top = Math.Max(4, (_conflictBarPanel.Height - _conflictBarFlow.Height) / 2);
            }
            _conflictBarFlow.SizeChanged += CenterBarVertical;
            _conflictBarPanel.SizeChanged += CenterBarVertical;
            _conflictBarPanel.Controls.Add(_conflictBarFlow);
            CenterBarVertical(null, null);
            // 初始尺寸按流程内容给出（更新时由 UpdateConflictBar 重算）
            _conflictBarFlow.PerformLayout();
            _conflictBarPanel.Size = new Size(_conflictBarFlow.PreferredSize.Width + 8, _conflictBarFlow.PreferredSize.Height + 4);
            _conflictBarFlow.Location = new Point(4, 2);

            // 挂到 centerRow 中央组合容器内（尾随竖分隔线之后 ⇒ 与统计区自然并排，外层整体居中）
            parent.Controls.Add(_conflictBarPanel);

            _lblStatus.ForeColor = PathRed;
        }

        private (int Only1, int Only2, int Same, int Conflict, int Coexist1, int Coexist2, int Pkg1Total, int Pkg2Total, int SameName) ComputeStats()
        {
            int only1 = 0, only2 = 0, same = 0, conflict = 0, coexist1 = 0, coexist2 = 0;
            foreach (var item in _allItems)
            {
                switch (item.Status)
                {
                    case CompareStatus.Only1: only1++; break;
                    case CompareStatus.Only2: only2++; break;
                    case CompareStatus.Same: same++; break;
                    case CompareStatus.Conflict:
                        if (item.MergeDecision == MergeDecision.Renamed)
                        {
                            bool r1 = !string.IsNullOrEmpty(item.RenamedPath1);
                            bool r2 = !string.IsNullOrEmpty(item.RenamedPath2);
                            if (r1) coexist1++;
                            if (r2) coexist2++;
                            if (!r1 && !r2) coexist1++;
                        }
                        else conflict++;
                        break;
                    case CompareStatus.Renamed:
                        if (item.OrigStatus == CompareStatus.Only2) coexist2++; else coexist1++;
                        break;
                }
            }
            int pkg1Total = only1 + same + conflict + coexist1;
            int pkg2Total = only2 + same + conflict + coexist2;
            int sameName = same + conflict; // 同名：路径完全相同（不区分数据）
            return (only1, only2, same, conflict, coexist1, coexist2, pkg1Total, pkg2Total, sameName);
        }

        private void NotifyWarn(string text)
        {
            var cfg = new AntdUI.Notification.Config(this,
                LanguageManager.Instance.GetText("Common.Notice", "提示"),
                text,
                AntdUI.TType.Warn,
                AntdUI.TAlignFrom.Top,
                FontHelper.UiFont(11), 8);
            AntdUI.Notification.open(cfg);
        }

        // ========== 右侧撤销便捷栏 ==========

        private void BuildMergeRightPanel(Control panel)
        {
            var header = new AntdUI.Label
            {
                Name = "ModMergeTool.EditPanelTitle", Text = LanguageManager.Instance.GetText("ModMergeTool.EditPanelTitle", "已编辑 / 已改名"),
                Dock = DockStyle.Top, Height = 28,
                Padding = new Padding(3, 6, 0, 0),
                Font = FontHelper.UiFont(9, FontStyle.Bold)
            };

            var undoAllBtn = new AntdUI.Button
            {
AutoSize = true,
                Name = "ModMergeTool.UndoAll", Text = LanguageManager.Instance.GetText("ModMergeTool.UndoAll", "撤销全部"),
                Type = AntdUI.TTypeMini.Default, Dock = DockStyle.Bottom, Font = FontHelper.UiFont(9)
            };
            undoAllBtn.Click += (s, e) => UndoAllEdit();

            _editList = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = true,
                Bordered = true,
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill,
                EditMode = AntdUI.TEditMode.None,
                LostFocusClearSelection = false,
                Gap = 4
            };
            _editList.Columns.Add(new AntdUI.Column("OpCells", "")
            { Width = "52" });
            _editList.Columns.Add(new AntdUI.Column("Kind", LanguageManager.Instance.GetText("ModMergeTool.EditListStatus", "类型"))
            { Width = "40", ColAlign = AntdUI.ColumnAlign.Center });
            _editList.Columns.Add(new AntdUI.Column("Display", LanguageManager.Instance.GetText("ModMergeTool.EditListFile", "文件"))
            { LineBreak = true });
            _editList.CellButtonClick += OnEditListButtonClick;
            _editList.SelectIndexChanged += OnEditListSelect;

            // 布局：Fill 先加 → Bottom → Top（WinForms 从后往前处理 Dock）
            panel.Controls.Add(_editList);
            panel.Controls.Add(undoAllBtn);
            panel.Controls.Add(header);

            RefreshEditList();
        }

        private void RefreshEditList()
        {
            if (_editList == null || _editList.IsDisposed) return;
            _syncingEditList = true;
            try
            {
                _editRows.Clear();
                foreach (var it in _allItems)
                {
                    bool isRenamed = it.MergeDecision == MergeDecision.Renamed
                                     || !string.IsNullOrEmpty(it.RenamedPath1)
                                     || !string.IsNullOrEmpty(it.RenamedPath2);
                    bool isEdited = it.IsEdited || it.MergeDecision == MergeDecision.Merged;
                    if (!isRenamed && !isEdited) continue;
                    string kind = isRenamed
                        ? LanguageManager.Instance.GetText("ModMergeTool.EditListRename", "改")
                        : LanguageManager.Instance.GetText("ModMergeTool.EditListMerge", "合");
                    string display = it.FileName;
                    if (isRenamed)
                    {
                        var names = new List<string>();
                        if (!string.IsNullOrEmpty(it.RenamedPath1)) names.Add(LastPathSegment(it.RenamedPath1));
                        if (!string.IsNullOrEmpty(it.RenamedPath2)) names.Add(LastPathSegment(it.RenamedPath2));
                        if (names.Count > 0) display += "  →  " + string.Join(" | ", names);
                    }
                    _editRows.Add(new MergeEditRow
                    {
                        Item = it,
                        Kind = kind,
                        FilePath = it.FileName,
                        Display = display,
                        OpCells = new AntdUI.ICell[]
                        {
                            new AntdUI.CellButton("undo", LanguageManager.Instance.GetText("ModMergeTool.UndoOne", "撤销"), AntdUI.TTypeMini.Error)
                        }
                    });
                }
                _editList.DataSource = new BindingList<MergeEditRow>(_editRows);
            }
            finally { _syncingEditList = false; }
        }

        private void OnEditListButtonClick(object sender, AntdUI.TableButtonEventArgs e)
        {
            if (e.Record is not MergeEditRow row || row.Item == null) return;
            UndoOneEdit(row.Item);
        }

        private void OnEditListSelect(object sender, EventArgs e)
        {
            if (_syncingEditList) return;
            if (_editList == null || _mergeTable == null) return;
            var row = _editList.GetRow(_editList.SelectedIndex - 1)?.record as MergeEditRow;
            if (row?.Item == null) return;
            var mr = FlattenMergeRows(_mergeRows ?? new List<MergeRow>())
                .FirstOrDefault(r => r.Item == row.Item);
            if (mr == null) return;
            _mergeTable.SetSelected(mr, true);
            _mergeTable.ScrollLine(mr);
        }

        private void UndoAllEdit()
        {
            foreach (var it in _allItems)
            {
                bool isRenamed = it.MergeDecision == MergeDecision.Renamed
                                 || !string.IsNullOrEmpty(it.RenamedPath1)
                                 || !string.IsNullOrEmpty(it.RenamedPath2);
                if (isRenamed)
                {
                    DeleteRenamedTempFiles(it, clearFields: true);
                    it.RenamedPath = null;
                    it.RenamedPath1 = null;
                    it.RenamedPath2 = null;
                }
                if (it.IsEdited)
                {
                    TryDeleteTempFile(it.EditedTempPath);
                    it.IsEdited = false;
                    it.EditedTempPath = null;
                }
                if (it.MergeDecision == MergeDecision.Merged || isRenamed)
                    it.MergeDecision = MergeDecision.None;
                // 还原改名前的文件状态（TryApplyConflictRename/SingleRename 曾把撤销前 Status 记入 OrigStatus）
                if (it.OrigStatus != CompareStatus.None)
                {
                    it.Status = it.OrigStatus;
                    it.OrigStatus = CompareStatus.None;
                }
            }
            RefreshEditList();
            RefreshMergeRowDiffIcons();
            ScheduleCheckRelayout();
            UpdateStats();
        }

        private void UndoOneEdit(CompareItem item)
        {
            if (item == null) return;
            bool isRenamed = item.MergeDecision == MergeDecision.Renamed
                             || !string.IsNullOrEmpty(item.RenamedPath1)
                             || !string.IsNullOrEmpty(item.RenamedPath2);
            if (isRenamed)
            {
                DeleteRenamedTempFiles(item, clearFields: true);
                item.RenamedPath = null;
                item.RenamedPath1 = null;
                item.RenamedPath2 = null;
            }
            if (item.IsEdited)
            {
                TryDeleteTempFile(item.EditedTempPath);
                item.IsEdited = false;
                item.EditedTempPath = null;
            }
            if (item.MergeDecision == MergeDecision.Merged || isRenamed)
                item.MergeDecision = MergeDecision.None;
            // 还原改名前的文件状态（同 UndoAllEdit）
            if (item.OrigStatus != CompareStatus.None)
            {
                item.Status = item.OrigStatus;
                item.OrigStatus = CompareStatus.None;
            }
            RefreshEditList();
            SyncMergeRowDiffIcon(item);
            ScheduleCheckRelayout();
            UpdateStats();
        }

        /// <summary>
        /// 撤销/改名后同步单个条目对应行的差异标记（差异列为预计算数据，需显式回写后才经 LoadLayout 重绘生效）。
        /// </summary>
        private void SyncMergeRowDiffIcon(CompareItem item)
        {
            if (item == null || _mergeRows == null) return;
            foreach (var r in FlattenMergeRows(_mergeRows))
            {
                if (!r.IsDirectory && r.Item == item) { r.DiffIcon = GetDiffIcon(r); break; }
            }
        }

        private void RefreshMergeRowDiffIcons()
        {
            if (_mergeRows == null) return;
            foreach (var r in FlattenMergeRows(_mergeRows))
                if (!r.IsDirectory && r.Item != null) r.DiffIcon = GetDiffIcon(r);
        }

        private static AntdUI.Tag AddStatsTag(FlowLayoutPanel panel, string labelKey, string fallback, int count, Color back, Color fore)
        {
            var tag = new AntdUI.Tag
            {
                Text = $"{LanguageManager.Instance.GetText(labelKey, fallback)} {count}",
                BackColor = back, ForeColor = fore,
                AutoSize = true, Margin = new Padding(0, 0, 6, 0), Font = FontHelper.UiFont(8)
            };
            panel.Controls.Add(tag);
            return tag;
        }

        private string GetLocalizedTagText(Dictionary<string, string> tagDict)
        {
            if (tagDict == null || tagDict.Count == 0) return "";
            var parts = new List<string>();
            foreach (var kvp in tagDict)
            {
                string localized = LocalizationHelper.LookupLocalizedTag(kvp.Value, _currentLanguageDict);
                if (!string.IsNullOrEmpty(localized))
                    parts.Add(localized);
            }
            return parts.Count > 0 ? string.Join(" / ", parts) : "";
        }

        // 固化本地化展示文本缓存：扫描完成或语言包保存后调用一次，此后渲染/搜索直接用缓存对象
        private void RebuildLocalizedCache()
        {
            if (_allItems == null) return;
            foreach (var it in _allItems)
            {
                if (it == null) continue;
                it.Localized = GetLocalizedTagText(it.TagDict);
            }
        }

        private bool MatchSearch(CompareItem it, string lower)
        {
            if (it == null) return false;
            if (!string.IsNullOrEmpty(it.FileName) && it.FileName.ToLowerInvariant().Contains(lower)) return true;
            string eff = GetEffectiveFileName(it);
            if (!string.IsNullOrEmpty(eff) && eff.ToLowerInvariant().Contains(lower)) return true;
            if (!string.IsNullOrEmpty(it.Description) && it.Description.ToLowerInvariant().Contains(lower)) return true;
            if (!string.IsNullOrEmpty(it.Localized) && it.Localized.ToLowerInvariant().Contains(lower)) return true;
            return false;
        }

        private string GetFolderAnnotationSuffix(string folderName) =>
            LocalizationHelper.GetFolderAnnotationSuffix(folderName);

        private void SetAdoptChecked(MergeRow row, bool value)
        {
            if (row == null) return;
            if (row.IsDirectory)
            {
                var files = CollectAdoptFiles(row);
                foreach (var f in files) if (f.Item != null) f.Item.IsChecked = value;
            }
            else
            {
                if (row.Item != null) row.Item.IsChecked = value;
            }
        }

        // P1：勾选显示层辅助 —— 仅更新 MergeRow.Adopt 字段，不重建树、不重绑 DataSource、不刷统计。
        private void ApplyCheckSubtree(MergeRow dir, bool value)
        {
            if (dir == null) return;
            dir.Adopt = value;
            if (dir.Children != null)
                foreach (var c in dir.Children)
                {
                    if (c.Children != null && c.Children.Count > 0) ApplyCheckSubtree(c, value);
                    else c.Adopt = value;
                }
        }

        private void RecomputeCheckAncestors(MergeRow row)
        {
            var p = row?.Parent;
            while (p != null)
            {
                p.Adopt = p.Children != null && p.Children.Count > 0 && p.Children.All(c => c.Adopt);
                p = p.Parent;
            }
        }

        private void SetAllAdoptRows(List<MergeRow> rows, bool value)
        {
            foreach (var r in rows)
            {
                r.Adopt = value;
                if (r.Children != null && r.Children.Count > 0) SetAllAdoptRows(r.Children, value);
            }
        }

        // 只作用于当前显示树：同步各叶子节点对应数据项的 IsChecked（打包开关）。
        private void SetAllAdoptItems(MergeRow row, bool value)
        {
            foreach (var c in row.Children ?? new List<MergeRow>())
            {
                if (c.IsDirectory)
                {
                    if (c.Children != null && c.Children.Count > 0) SetAllAdoptItems(c, value);
                }
                else if (c.Item != null) c.Item.IsChecked = value;
            }
        }

        private List<MergeRow> CollectAdoptFiles(MergeRow dir)
        {
            var list = new List<MergeRow>();
            if (dir?.Children == null) return list;
            foreach (var c in dir.Children)
            {
                if (c.IsDirectory) list.AddRange(CollectAdoptFiles(c));
                else list.Add(c);
            }
            return list;
        }

        // ========== 方案3：单树构建与渲染 ==========

        private string GetMergeOwnerIcon(MergeRow row)
        {
            if (row == null) return "";
            if (row.IsDirectory)
            {
                var pkgs = GetFolderPackages(row);
                if (pkgs.Count == 0) return "";
                if (pkgs.SetEquals(new HashSet<int> { 1 })) return "1";
                if (pkgs.SetEquals(new HashSet<int> { 2 })) return "2";
                if (pkgs.Contains(1) && pkgs.Contains(2)) return "1 2";
                return "";
            }
            var item = row.Item;
            if (item == null) return "";
            switch (row.Status)
            {
                case CompareStatus.Only1: return "1";
                case CompareStatus.Only2: return "2";
                case CompareStatus.Same: return "1 2";
                case CompareStatus.Conflict: return "1 2";
                case CompareStatus.Renamed:
                    if (item.OrigStatus == CompareStatus.Only2) return "2";
                    if (item.OrigStatus == CompareStatus.Conflict) return "1 2";
                    return "1";
                default: return "";
            }
        }

        private string GetDiffIcon(MergeRow row)
        {
            if (row == null || row.IsDirectory) return "";
            var item = row.Item;
            if (item == null) return "";
            switch (row.Status)
            {
                case CompareStatus.Same: return "=";
                case CompareStatus.Renamed: return "改";
                case CompareStatus.Conflict:
                    if (item.MergeDecision == MergeDecision.Merged) return "合";
                    if (item.MergeDecision == MergeDecision.Renamed) return "改";
                    if (item.MergeDecision == MergeDecision.None) return "X";
                    return "";
                default: return "";
            }
        }

        private HashSet<int> GetFolderPackages(MergeRow dir)
        {
            var set = new HashSet<int>();
            foreach (var f in CollectAdoptFiles(dir))
            {
                if (f.Item == null) continue;
                switch (f.Status)
                {
                    case CompareStatus.Only1: set.Add(1); break;
                    case CompareStatus.Only2: set.Add(2); break;
                    case CompareStatus.Same:
                    case CompareStatus.Conflict: set.Add(1); set.Add(2); break;
                    case CompareStatus.Renamed:
                        if (f.Item.OrigStatus == CompareStatus.Only2) set.Add(2);
                        else if (f.Item.OrigStatus == CompareStatus.Conflict) { set.Add(1); set.Add(2); }
                        else set.Add(1);
                        break;
                }
            }
            return set;
        }


private List<MergeRow> BuildMergeRows(string folderPath, Dictionary<string, CompareItem> itemMap, List<NodeData> mergeRoot, MergeRow parent = null)
        {
            var result = new List<MergeRow>();
            var folderNode = string.IsNullOrEmpty(folderPath) ? null : FileTreeHelper.FindNodeByPath(mergeRoot, folderPath);
            var children = string.IsNullOrEmpty(folderPath) ? (mergeRoot ?? new List<NodeData>()) : (folderNode?.Children ?? new List<NodeData>());
            var isDirMap = children.ToDictionary(n => n.Name, n => n.IsDirectory, StringComparer.OrdinalIgnoreCase);
            foreach (var name in children.Select(n => n.Name).OrderBy(n => isDirMap[n] ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var nd = children.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
                if (nd == null) continue;
                bool isDir = nd.IsDirectory;
                string fullPath = string.IsNullOrEmpty(folderPath) ? name : folderPath + "/" + name;
                CompareStatus status;
                CompareItem src = null;
                if (isDir)
                {
                    status = CompareStatus.Same;
                }
                else
                {
                    if (!itemMap.TryGetValue(fullPath, out src))
                        status = CompareStatus.Only1;
                    else
                        status = src.Status;
                }
                if (!isDir && src == null && itemMap.TryGetValue(fullPath, out var tmp)) src = tmp;
                var row = new MergeRow
                {
                    Name = name,
                    FullPath = fullPath,
                    IsDirectory = isDir,
                    Status = status,
                    Item = src,
                    Parent = parent
                };
                if (isDir)
                    row.Children = BuildMergeRows(fullPath, itemMap, mergeRoot, row);
                result.Add(row);
            }
            return result;
        }

        private List<MergeRow> BuildFlatMergeRows(List<CompareItem> items)
        {
            var rows = new List<MergeRow>();
            if (items == null) return rows;
            foreach (var it in items)
            {
                if (it == null) continue;
                string full = it.FileName ?? "";
                rows.Add(new MergeRow
                {
                    Name = full,
                    FullPath = full,
                    IsDirectory = false,
                    Status = it.Status,
                    Item = it,
                    Parent = null,
                    Children = null
                });
            }
            return rows;
        }

        private void SwitchMergeViewMode(bool flat, bool save = true, bool refresh = true)
        {
            if (_mergeFlatViewMode == flat && save) return;
            if (flat && _mergeRows != null)
                _mergeExpandStash = SaveMergeExpandedPathsFromRows(_mergeRows);
            _mergeFlatViewMode = flat;
            _btnMergeTreeMode.Type = flat ? AntdUI.TTypeMini.Default : AntdUI.TTypeMini.Primary;
            _btnMergeFlatMode.Type = flat ? AntdUI.TTypeMini.Primary : AntdUI.TTypeMini.Default;
            if (_btnMergeExpandAll != null) _btnMergeExpandAll.Enabled = !flat;
            if (_btnMergeCollapseAll != null) _btnMergeCollapseAll.Enabled = !flat;
            if (save)
            {
                SettingsManager.MergeFlatViewMode = flat;
                SettingsManager.Save();
            }
            if (refresh) RefreshMergeTree(_filteredItems);
        }

        private Color? GetPathTextColor(MergeRow row)
        {
            if (row == null) return null;
            if (!row.IsDirectory)
            {
                // 文件：相同=默认，仅1有=浅蓝，仅2有=浅绿，冲突=红，已改名=橙
                switch (row.Status)
                {
                    case CompareStatus.Only1: return PathBlue;
                    case CompareStatus.Only2: return PathGreen;
                    case CompareStatus.Conflict:
                        if (row.Item?.MergeDecision == MergeDecision.Renamed) return PathOrange;
                        return PathRed;
                    case CompareStatus.Renamed: return PathOrange;
                    default: return null;
                }
            }
            // 文件夹：优先级 含冲突红 > 含1浅蓝 > 含2浅绿 > 都有青
            bool hasConflict = FolderHasStatus(row, CompareStatus.Conflict);
            if (hasConflict) return PathRed;
            var pkgs = GetFolderPackages(row);
            if (pkgs.Count == 0) return null;
            if (pkgs.SetEquals(new HashSet<int> { 1 })) return PathBlue;
            if (pkgs.SetEquals(new HashSet<int> { 2 })) return PathGreen;
            return PathTeal;
        }

        private static bool FolderHasStatus(MergeRow row, CompareStatus status)
        {
            if (row == null) return false;
            if (!row.IsDirectory) return row.Status == status;
            if (row.Children != null)
                foreach (var c in row.Children)
                    if (FolderHasStatus(c, status)) return true;
            return false;
        }


        private void PrecomputeMergeRenderData(List<MergeRow> rows)
        {
            foreach (var row in rows)
            {
                if (row.IsDirectory)
                {
                    // 先递归子节点，确保文件夹归属可基于子文件计算
                    if (row.Children != null && row.Children.Count > 0) PrecomputeMergeRenderData(row.Children);
                    string suffix = GetFolderAnnotationSuffix(row.Name);
                    row.DisplayName = "\U0001F4C1 " + row.Name + suffix;
                    row.Desc = "";
                    row.Localized = "";
                    row.OwnerIcon = GetMergeOwnerIcon(row);
                    row.DiffIcon = GetDiffIcon(row);
                    continue;
                }
                else
                {
                    row.DisplayName = "\U0001F4C4 " + row.Name;
                    var src = row.Item;
                    if (src != null)
                    {
                        row.Desc = src.Description ?? "";
                        row.Localized = src.Localized ?? "";
                    }
                    row.OwnerIcon = GetMergeOwnerIcon(row);
                    row.DiffIcon = GetDiffIcon(row);
                }
                if (row.Children != null && row.Children.Count > 0) PrecomputeMergeRenderData(row.Children);
            }
        }

        private string GetEffectiveFileName(CompareItem it)
        {
            if (it == null) return "";
            if (!string.IsNullOrEmpty(it.RenamedPath)) return it.RenamedPath;
            if (!string.IsNullOrEmpty(it.RenamedPath1)) return it.RenamedPath1;
            if (!string.IsNullOrEmpty(it.RenamedPath2)) return it.RenamedPath2;
            return it.FileName;
        }

        private static string LastPathSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int idx = path.LastIndexOf('/');
            string leaf = idx >= 0 ? path.Substring(idx + 1) : path;
            return string.IsNullOrEmpty(leaf) ? null : leaf;
        }

        /// <summary>
        /// 构建路径列改名展示 cell 序列：旧名(灰) → 新名(红)[ | 新名2(青)]。
        /// 非改名行返回 null（走默认单色逻辑）。
        /// </summary>
        private IList<AntdUI.ICell> BuildPathRenameSegments(MergeRow row)
        {
            if (row == null || row.IsDirectory || row.Item == null) return null;
            var item = row.Item;
            bool hasRename = !string.IsNullOrEmpty(item.RenamedPath1) || !string.IsNullOrEmpty(item.RenamedPath2)
                || item.MergeDecision == MergeDecision.Renamed;
            if (!hasRename) return null;
            // 平铺模式 Name 为完整路径：拆出目录前缀（保持不变）与旧文件名；树型模式 Name 即叶名，前缀为空
            string oldLeaf = row.Name;
            string prefix = "";
            if (_mergeFlatViewMode)
            {
                string full = row.FullPath ?? row.Name ?? "";
                int idx = full.LastIndexOf('/');
                if (idx >= 0) { prefix = full.Substring(0, idx + 1); oldLeaf = full.Substring(idx + 1); }
                else oldLeaf = full;
            }
            string new1 = LastPathSegment(string.IsNullOrEmpty(item.RenamedPath1) ? item.RenamedPath : item.RenamedPath1);
            string new2 = LastPathSegment(item.RenamedPath2);
            bool renamed1 = !string.IsNullOrEmpty(new1) && !string.Equals(new1, oldLeaf, StringComparison.OrdinalIgnoreCase);
            bool renamed2 = !string.IsNullOrEmpty(new2) && !string.Equals(new2, oldLeaf, StringComparison.OrdinalIgnoreCase);
            if (!renamed1 && !renamed2) return null;
            // 改名行：始终显示两侧名字（包1蓝 | 包2绿）。被改名的一侧显示新名，
            // 未改名的一侧"原样插入"当前原名，统一按新名着色（就当都是新的）。
            string seg1 = renamed1 ? new1 : oldLeaf;
            string seg2 = renamed2 ? new2 : oldLeaf;
            return new List<AntdUI.ICell>
            {
                new AntdUI.CellText(prefix + seg1, PathBlue),
                new AntdUI.CellText(" | ", PathOldNameGray),
                new AntdUI.CellText(seg2, PathGreen)
            };
        }

        private void RefreshMergeTree(List<CompareItem> items = null)
        {
            if (_mergeTable == null) return;
            items ??= _allItems;
            bool isFiltered = items != _allItems;
            if (_mergeFlatViewMode)
            {
                var flat = BuildFlatMergeRows(items);
                _mergeRows = flat;
                PrecomputeMergeRenderData(flat);
                PreComputeMergeAdopt(flat);
                _mergeTable.DataSource = new BindingList<MergeRow>(flat);
                BeginInvoke((Action)(() => { if (_mergeTable != null && _mergeTable.LoadLayout()) _mergeTable.Invalidate(); }));
                return;
            }
            // 树结构固定使用原文件名（改名信息体现于路径列 cell，而非移动树节点）
            var entries = new List<(string name, DateTime fileTime, int index, string recordType)>();
            int ei = 0;
            foreach (var it in items)
            {
                string raw = string.IsNullOrEmpty(it.FileName) ? "" : it.FileName;
                entries.Add((raw, DateTime.Now, ei++, Path.GetExtension(raw)));
            }
            _mergeRootNodes = GuiMainForm.BuildHierarchicalStructureStatic(entries);
            var itemMap = items.ToDictionary(i => i.FileName ?? "", StringComparer.OrdinalIgnoreCase);
            HashSet<string> expanded = null;
            if (!isFiltered)
            {
                if (_mergeExpandStash != null && _mergeExpandStash.Count > 0)
                {
                    expanded = _mergeExpandStash;
                    _mergeExpandStash = null;
                }
                else if (_mergeRows != null)
                    expanded = SaveMergeExpandedPathsFromRows(_mergeRows);
            }
            var root = BuildMergeRows("", itemMap, _mergeRootNodes);
            if (isFiltered) RemoveEmptyMergeFolders(root);
            _mergeRows = root;
            PrecomputeMergeRenderData(root);
            PreComputeMergeAdopt(root);
            _mergeTable.DataSource = new BindingList<MergeRow>(root);
            if (isFiltered) _mergeTable.ExpandAll(true);
            else if (expanded != null && expanded.Count > 0) RestoreMergeExpandOnTable(root, expanded);
            BeginInvoke((Action)(() => { if (_mergeTable != null && _mergeTable.LoadLayout()) _mergeTable.Invalidate(); }));
        }

        private List<MergeRow> BuildMergeTreeData(List<CompareItem> items, bool isFiltered)
        {
            if (_mergeFlatViewMode)
            {
                var flat = BuildFlatMergeRows(items);
                PrecomputeMergeRenderData(flat);
                PreComputeMergeAdopt(flat);
                return flat;
            }
            var entries = new List<(string name, DateTime fileTime, int index, string recordType)>();
            int ei = 0;
            foreach (var it in items) entries.Add((it.FileName ?? "", DateTime.Now, ei++, Path.GetExtension(it.FileName ?? "")));
            var nodes = GuiMainForm.BuildHierarchicalStructureStatic(entries);
            var map = items.ToDictionary(i => i.FileName ?? "", StringComparer.OrdinalIgnoreCase);
            var root = BuildMergeRows("", map, nodes);
            if (isFiltered) RemoveEmptyMergeFolders(root);
            PrecomputeMergeRenderData(root);
            PreComputeMergeAdopt(root);
            return root;
        }

        private static void RemoveEmptyMergeFolders(List<MergeRow> rows)
        {
            rows.RemoveAll(r => r.IsDirectory && (r.Children == null || r.Children.Count == 0));
            foreach (var r in rows.Where(r => r.Children != null).ToList()) RemoveEmptyMergeFolders(r.Children);
        }

        private HashSet<string> SaveMergeExpandedPathsFromRows(List<MergeRow> rows)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(List<MergeRow> list)
            {
                foreach (var r in list)
                {
                    if (r.Expand && !string.IsNullOrEmpty(r.FullPath)) set.Add(r.FullPath);
                    if (r.Children != null) Walk(r.Children);
                }
            }
            Walk(rows);
            return set;
        }

        private void RestoreMergeExpandOnTable(List<MergeRow> rows, HashSet<string> expanded)
        {
            if (expanded == null || expanded.Count == 0 || _mergeTable == null) return;
            void Walk(List<MergeRow> list)
            {
                foreach (var r in list)
                {
                    if (!string.IsNullOrEmpty(r.FullPath) && expanded.Contains(r.FullPath)) _mergeTable.Expand(r, true);
                    if (r.Children != null) Walk(r.Children);
                }
            }
            Walk(rows);
        }

        private void PreComputeMergeAdopt(List<MergeRow> rows)
        {
            foreach (var r in rows)
            {
                if (r.IsDirectory)
                {
                    if (r.Children != null) PreComputeMergeAdopt(r.Children);
                    var files = CollectAdoptFiles(r);
                    r.Adopt = files.Count > 0 && files.All(f => f.Adopt);
                }
                else r.Adopt = r.Item != null && r.Item.IsChecked;
            }
        }

        /// <summary>
        /// 勾选状态兜底刷新：整表重建可见单元（LoadLayout）以重新读取 Adopt —— 仅 Invalidate 无法清除
        /// TCellCheck 的 _checked 缓存，需重建单元才会回读 Adopt。无重算、无重绑、无统计。
        /// </summary>
        private void RefreshMergeChecks()
        {
            ScheduleCheckRelayout();
        }

        /// <summary>
        /// 行级显示兜底刷新：整表重建可见单元（LoadLayout）回读 Adopt。P2/P3 后仅剩旧调用点兜底。
        /// </summary>
        private void ReloadMergeVisible()
        {
            ScheduleCheckRelayout();
        }

        /// <summary>
        /// 勾选后合并式刷新：将多次触发（如表头 CheckAll 逐行回调）合并为一次 LoadLayout + Invalidate。
        /// LoadLayout 会按当前行数据重建可见单元，从而让复选框回读最新 Adopt，不重绑 DataSource（无卡顿）。
        /// </summary>
        private void ScheduleCheckRelayout()
        {
            if (_relayoutScheduled) return;
            _relayoutScheduled = true;
            BeginInvoke((Action)(() =>
            {
                _relayoutScheduled = false;
                if (_mergeTable == null || _mergeTable.IsDisposed) return;
                if (_mergeTable.LoadLayout()) _mergeTable.Invalidate();
            }));
        }

        /// <summary>
        /// 将 action 回主 UI 线程执行；已在 UI 线程则直接执行。
        /// 用于 AntdUI.Modal.OnOk（其后台线程执行）等场景内的控件访问。
        /// </summary>
        private void RunOnUi(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }

        /// <summary>
        /// 同级同名检测：判断 newLeaf 在 item 所在目录层级中是否已存在同名文件（排除自身）。
        /// 不做全局查重。
        /// </summary>
        private bool SameDirNameExists(CompareItem item, string newLeaf, out CompareItem clash)
        {
            clash = null;
            string dir = Path.GetDirectoryName(item.FileName)?.Replace('\\', '/') ?? "";
            foreach (var o in _allItems)
            {
                if (ReferenceEquals(o, item)) continue;
                string oDir = Path.GetDirectoryName(o.FileName)?.Replace('\\', '/') ?? "";
                if (!string.Equals(dir, oDir, StringComparison.OrdinalIgnoreCase)) continue;
                string oLeaf = Path.GetFileName(o.FileName);
                if (string.Equals(oLeaf, newLeaf, StringComparison.OrdinalIgnoreCase)) { clash = o; return true; }
            }
            return false;
        }

        private bool ContainsPathSep(string s)
        {
            return s != null && (s.IndexOf('/') >= 0 || s.IndexOf('\\') >= 0);
        }

        private bool TryApplySingleRename(CompareItem item, string inputText)
        {
            string val = inputText?.Trim();
            if (string.IsNullOrEmpty(val))
            {
                NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.RenameInvalid", "文件名不能为空"));
                return false;
            }
            if (ContainsPathSep(val))
            {
                NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.RenameNoDir", "只能修改文件名，不能修改目录"));
                return false;
            }
            string oldLeaf = Path.GetFileName(item.FileName);
            if (string.Equals(val, oldLeaf, StringComparison.OrdinalIgnoreCase)) return false;
            if (SameDirNameExists(item, val, out _))
            {
                NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.RenameExists", "目标文件名已存在"));
                return false;
            }
            string dir = Path.GetDirectoryName(item.FileName)?.Replace('\\', '/') ?? "";
            string newPath = string.IsNullOrEmpty(dir) ? val : dir + "/" + val;
            item.OrigStatus = item.Status;
            if (item.Status == CompareStatus.Only1)
            {
                item.RenamedPath1 = newPath;
                item.RenamedPath = newPath;
            }
            else
            {
                item.RenamedPath2 = newPath;
            }
            item.Status = CompareStatus.Renamed;
            // P2：改名成功立即释放临时文件（以文件为准）；改名改变 Status → 重建显示集合并刷新统计。
            ReleaseRenamedNow(item);
            ApplyFilter();
            UpdateStats();
            return true;
        }

        private void OpenConflictRename(CompareItem item)
        {
            string curName = item.FileName;
            string currentLeaf = Path.GetFileName(curName);
            // 输入框仅文件名（不含扩展名）；扩展名由原文件自动提取，作为不可编辑后缀展示（如 .dbr/.txt/.lua）
            string nameOnly = Path.GetFileNameWithoutExtension(currentLeaf);
            string extSuffix = Path.GetExtension(currentLeaf);
            // 仿收藏弹窗(ArzArcEditorForm.ShowAddToFavoritesDialog)的 GridPanel 写法：显式宽高+Span+Dock=Fill+Height，确保 Modal 内容测量阶段输入框有确定宽度
            var input1 = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Text = nameOnly,
                PlaceholderText = nameOnly,
                Height = 52,
                PrefixText = LanguageManager.Instance.GetText("ModMergeTool.RenameNewName1", "包1文件新名:"),
                SuffixText = extSuffix,
                AllowClear = true
            };
            var input2 = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Text = nameOnly,
                PlaceholderText = nameOnly,
                Height = 52,
                PrefixText = LanguageManager.Instance.GetText("ModMergeTool.RenameNewName2", "包2文件新名:"),
                SuffixText = extSuffix,
                AllowClear = true
            };
            var grid = new AntdUI.GridPanel
            {
                Span = "fill;fill",
                Gap = 8,
                BackColor = Color.Transparent,
                Width = 520,
                Height = 120
            };
            grid.SetIndex(input1, 0);
            grid.SetIndex(input2, 1);
            grid.Controls.Add(input2);
            grid.Controls.Add(input1);
            var cfg = new AntdUI.Modal.Config(this, LanguageManager.Instance.GetText("ModMergeTool.RenameTitle", "改名"), grid)
            {
                Draggable = true
            };
            cfg.SetWidth(520);
            cfg.SetOk(LanguageManager.Instance.GetText("Common.OK", "确定"));
            cfg.SetCancel(LanguageManager.Instance.GetText("Common.Cancel", "取消"));
            cfg.OnOk = c => TryApplyConflictRename(item, input1.Text, input2.Text);
            AntdUI.Modal.open(cfg);
        }

        private bool TryApplyConflictRename(CompareItem item, string t1, string t2)
        {
            string v1 = t1?.Trim();
            string v2 = t2?.Trim();
            if (string.IsNullOrEmpty(v1) || string.IsNullOrEmpty(v2))
            {
                RunOnUi(() => NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.RenameInvalid", "文件名不能为空")));
                return false;
            }
            if (ContainsPathSep(v1) || ContainsPathSep(v2))
            {
                RunOnUi(() => NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.RenameNoDir", "只能修改文件名，不能修改目录")));
                return false;
            }
            string oldLeaf = Path.GetFileName(item.FileName);
            // 输入为纯文件名：自动拼回原扩展名；若手输已带原扩展名则去重，不产生 xx.dbr.dbr
            string ext = Path.GetExtension(oldLeaf) ?? "";
            string ComposeLeaf(string name)
            {
                string n = name?.Trim() ?? "";
                if (!string.IsNullOrEmpty(ext) && n.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - ext.Length);
                return n + ext;
            }
            string leaf1 = ComposeLeaf(v1);
            string leaf2 = ComposeLeaf(v2);
            // 仅输入扩展名本身（如 ".dbr"）视为无效文件名
            if (string.IsNullOrEmpty(Path.GetFileNameWithoutExtension(leaf1)) || string.IsNullOrEmpty(Path.GetFileNameWithoutExtension(leaf2)))
            {
                RunOnUi(() => NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.RenameInvalid", "文件名不能为空")));
                return false;
            }
            bool r1 = !string.Equals(leaf1, oldLeaf, StringComparison.OrdinalIgnoreCase);
            bool r2 = !string.Equals(leaf2, oldLeaf, StringComparison.OrdinalIgnoreCase);
            if (!r1 && !r2) return false;
            string dir = Path.GetDirectoryName(item.FileName)?.Replace('\\', '/') ?? "";
            string new1 = string.IsNullOrEmpty(dir) ? leaf1 : dir + "/" + leaf1;
            string new2 = string.IsNullOrEmpty(dir) ? leaf2 : dir + "/" + leaf2;
            // 约束①：两新名同级同名 → 禁止
            if (r1 && r2 && string.Equals(new1, new2, StringComparison.OrdinalIgnoreCase))
            {
                RunOnUi(() => NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.RenameConflict", "两个包的新名相同，不允许修改")));
                return false;
            }
            // 约束②：本层目录同级查重（按拼好扩展名的完整文件名比较）
            if (r1 && SameDirNameExists(item, leaf1, out _))
            {
                RunOnUi(() => NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.RenameExists", "目标文件名已存在")));
                return false;
            }
            if (r2 && SameDirNameExists(item, leaf2, out _))
            {
                RunOnUi(() => NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.RenameExists", "目标文件名已存在")));
                return false;
            }
            item.OrigStatus = item.Status;
            item.RenamedPath1 = r1 ? new1 : null;
            item.RenamedPath2 = r2 ? new2 : null;
            item.RenamedPath = item.RenamedPath1;
            // 互斥：改名保存后清除此前对比编辑（合并/编辑）状态，保证「只改或只合」，避免两者叠加输出不受控
            if (item.IsEdited)
            {
                TryDeleteTempFile(item.EditedTempPath);
                item.IsEdited = false;
                item.EditedTempPath = null;
            }
            // 标记式改名：不改变原始 Status（冲突行保持冲突），仅落下层数据 + 上层选择"改"。
            item.MergeDecision = MergeDecision.Renamed;
            // 改名成功立即释放两侧临时文件（含双改名两个输出）；行仍在当前视图内，仅刷新行显示与统计。
            // 数据/临时文件写入为纯操作，可在 OnOk 后台线程执行；UI 刷新回主线程。
            ReleaseRenamedNow(item);
            RunOnUi(() =>
            {
                // 差异列为预计算数据：改名后显式回写该行 DiffIcon（与对比编辑保存后 r.DiffIcon="合" 同机制）
                SyncMergeRowDiffIcon(item);
                ScheduleCheckRelayout();
                UpdateStats();
                RefreshEditList();
            });
            return true;
        }



        private void OnMergeTableCellButtonClick(object sender, AntdUI.TableButtonEventArgs e)
        {
            if (e.RowIndex < 0 || _mergeTable == null) return;
            if (e.Record is MergeRow row) ShowDecisionMenu(row);
        }

        /// <summary>
        /// 决策菜单：处理列点击与行右键共用同一构建与分发（避免两套逻辑漂移）。
        /// 仅冲突（含已改名/已编辑/已决策）文件行提供菜单。
        /// </summary>
        private void ShowDecisionMenu(MergeRow row)
        {
            if (_mergeTable == null || row == null || row.IsDirectory || row.Item == null) return;
            var item = row.Item;
            // 冲突（含已改名/已编辑/已决策）→ 下拉决策
            if (row.Status != CompareStatus.Conflict && row.Status != CompareStatus.Renamed) return;
            var menu = new List<AntdUI.IContextMenuStripItem>();
            menu.Add(new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("ModMergeTool.DecisionUse1", "使用1")) { Tag = "Use1" });
            menu.Add(new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("ModMergeTool.DecisionUse2", "使用2")) { Tag = "Use2" });
            if (item.MergeDecision == MergeDecision.Merged || item.IsMerged)
                menu.Add(new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("ModMergeTool.DecisionMerged", "合")) { Tag = "Merged" });
            // 已改名（标记式）：与「合」机制一致，菜单提供「改」项可再次确认为改
            if (item.MergeDecision == MergeDecision.Renamed)
                menu.Add(new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("ModMergeTool.DecisionRenamedShort", "改")) { Tag = "Renamed" });
            // 互斥①：已对比编辑（合并/编辑）后不再提供「改名」选项，避免改名+编辑叠加导致输出不受控
            bool isEdited = item.IsMerged || item.IsEdited || item.MergeDecision == MergeDecision.Merged;
            if (!isEdited)
                menu.Add(new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("ModMergeTool.DecisionRename", "改名")) { Tag = "Rename" });
            // 互斥②：已改名后不再提供「对比编辑」选项，改名与编辑只能选其一；想恢复可撤销
            bool isRenamed = item.IsRenamed || item.MergeDecision == MergeDecision.Renamed;
            if (item.CanCompare && !isRenamed)
                menu.Add(new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("ModMergeTool.DecisionCompare", "对比编辑")) { Tag = "Compare" });
            AntdUI.ContextMenuStrip.open(_mergeTable, it =>
            {
                string tag = it.Tag as string;
                if (tag == "Use1" || tag == "Use2" || tag == "Merged" || tag == "Renamed")
                {
                    item.MergeDecision = tag switch
                    {
                        "Merged" => MergeDecision.Merged,
                        "Renamed" => MergeDecision.Renamed,
                        "Use1" => MergeDecision.Use1,
                        _ => MergeDecision.Use2
                    };
                    // 显示层：仅同步该行 DiffIcon，行级重绘；不重建树、不重绑、不刷统计。
                    row.DiffIcon = GetDiffIcon(row);
                    // 决策格为按需渲染(CellButton)，行级 Invalidate 不会重读缓存文本，故需重建显示格使决策文字即时刷新。
                    ScheduleCheckRelayout();
                    // 逐行决策影响冲突数/已处理，刷新 LED 条（该路径不经过 UpdateStats）
                    UpdateConflictBar();
                }
                else if (tag == "Rename") { OpenConflictRename(item); }
                else if (tag == "Compare") { OpenCompareDialog(item); }
            }, menu.ToArray());
        }

        /// <summary>
        /// 路径列双击：冲突且可编辑文件直接进入对比编辑（与「对比编辑」按钮同入口）。
        /// </summary>
        private void OnMergeTableCellDoubleClick(object sender, AntdUI.TableClickEventArgs e)
        {
            if (_mergeTable == null) return;
            if (!ReferenceEquals(e.Column, _mergeNameCol)) return; // 仅路径列响应双击
            if (e.Record is not MergeRow row || row.IsDirectory || row.Item == null) return;
            if (!row.Item.CanCompare) return;
            // 互斥：已改名文件不再进入对比编辑（改名与编辑只能选其一）
            if (row.Item.IsRenamed || row.Item.MergeDecision == MergeDecision.Renamed) return;
            OpenCompareDialog(row.Item);
        }

        /// <summary>
        /// 行右键：命中冲突/已改名文件行时弹出与处理列一致的决策菜单（整行响应）。
        /// 采用 CellClick + e.Button 判定（与 ARZ/ARC 编辑器右键同模式，行由 AntdUI 解析，避免手动命中测试偏移）。
        /// </summary>
        private void OnMergeTableCellClick(object sender, AntdUI.TableClickEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (e.Button != MouseButtons.Right) return;
            if (e.Record is MergeRow row) ShowDecisionMenu(row);
        }

        private void EnsureTempDir()
        {
            if (string.IsNullOrEmpty(_tempDir))
            {
                _tempDir = Path.Combine(Path.GetTempPath(), "GDPackEditor_merge", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempDir);
            }
        }

        private static void EmitRenamedOutput(CompareItem item, string outputPath, string[] lines, Dictionary<string, string> decisions, Dictionary<string, string> customPaths, string tempDir)
        {
            if (string.IsNullOrEmpty(outputPath) || lines == null) return;
            string tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
            try { File.WriteAllLines(tempPath, lines, EncodingService.TempWriteEncoding); }
            catch { return; }
            decisions[outputPath] = "C";
            customPaths[outputPath] = tempPath;
        }

        /// <summary>
        /// 打包已改名项：释放到临时目录，按各自新名（未改名侧保留原名）写出。
        /// 支持 仅1有/仅2有/冲突 双改名两个输出。
        /// </summary>
        private void ReleaseRenamedItem(CompareItem item, Dictionary<string, string> decisions, Dictionary<string, string> customPaths)
        {
            LoadItemContent(item);
            EnsureTempDir();
            string orig = item.FileName;
            bool r1 = !string.IsNullOrEmpty(item.RenamedPath1);
            bool r2 = !string.IsNullOrEmpty(item.RenamedPath2);
            if (item.Lines1 != null)
                EmitRenamedOutput(item, r1 ? item.RenamedPath1 : orig, item.Lines1, decisions, customPaths, _tempDir);
            if (item.Lines2 != null)
                EmitRenamedOutput(item, r2 ? item.RenamedPath2 : orig, item.Lines2, decisions, customPaths, _tempDir);
}

        // ===== P2：改名即时释放 / 删除临时文件（以文件为准，打包直接引用；不碰文件树、不刷统计） =====

        /// <summary>
        /// P2：改名成功后立即释放两侧临时文件，缓存到 item.RenamedTempPath1/2 与 RenamedOutputPath1/2。
        /// 与打包期 ReleaseRenamedItem 数据来源一致：改名的用新名输出，未改名的用原名输出。
        /// </summary>
        private void ReleaseRenamedNow(CompareItem item)
        {
            if (item == null) return;
            bool r1 = !string.IsNullOrEmpty(item.RenamedPath1);
            bool r2 = !string.IsNullOrEmpty(item.RenamedPath2);
            if (!r1 && !r2) return;
            DeleteRenamedTempFiles(item, clearFields: false);

            EnsureTempDir();
            LoadItemContent(item);
            string orig = item.FileName;
            if (item.Lines1 != null)
            {
                item.RenamedOutputPath1 = r1 ? item.RenamedPath1 : orig;
                if (_tempDir != null)
                {
                    string tp = Path.Combine(_tempDir, "r1_" + Guid.NewGuid().ToString("N"));
                    try { File.WriteAllLines(tp, item.Lines1, EncodingService.TempWriteEncoding); item.RenamedTempPath1 = tp; }
                    catch { }
                }
            }
            if (item.Lines2 != null)
            {
                item.RenamedOutputPath2 = r2 ? item.RenamedPath2 : orig;
                if (_tempDir != null)
                {
                    string tp = Path.Combine(_tempDir, "r2_" + Guid.NewGuid().ToString("N"));
                    try { File.WriteAllLines(tp, item.Lines2, EncodingService.TempWriteEncoding); item.RenamedTempPath2 = tp; }
                    catch { }
                }
            }
        }

        /// <summary>
        /// P2：删除改名缓存临时文件（恢复改名/再次改名时清理）。
        /// </summary>
        private void DeleteRenamedTempFiles(CompareItem item, bool clearFields = true)
        {
            if (item == null) return;
            TryDeleteTempFile(item.RenamedTempPath1);
            TryDeleteTempFile(item.RenamedTempPath2);
            if (clearFields)
            {
                item.RenamedTempPath1 = null;
                item.RenamedTempPath2 = null;
                item.RenamedOutputPath1 = null;
                item.RenamedOutputPath2 = null;
            }
        }

        private static void TryDeleteTempFile(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        /// <summary>
        /// 展平当前可见树：用于低频场景下按 item 查找对应叶子行（编辑保存/决策等），不重建树。
        /// </summary>
        private static IEnumerable<MergeRow> FlattenMergeRows(IEnumerable<MergeRow> rows)
        {
            foreach (var r in rows)
            {
                yield return r;
                if (r.Children != null)
                    foreach (var c in FlattenMergeRows(r.Children)) yield return c;
            }
        }

        private async void OnMergeClick(object sender, EventArgs e)
        {
            if (_isRunning) return;


            _outputPath = _outputPathInput.Text?.Trim();
            if (string.IsNullOrEmpty(_outputPath))
            {
                NotifyWarn(LanguageManager.Instance.GetText("ModMergeTool.ErrorNoOutput", "请选择输出文件"));
                return;
            }

            string outputDir = Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            if (File.Exists(_outputPath))
            {
                bool overwrite = false;
                var confirmConfig = new AntdUI.Modal.Config(
                    LanguageManager.Instance.GetText("Common.Confirm", "确认"),
                    LanguageManager.Instance.GetText("ModMergeTool.ConfirmOverwrite", "输出文件已存在，是否覆盖？"))
                {
                    OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                    CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                    MaskClosable = false,
                    OnOk = c => { overwrite = true; return true; }
                };
                AntdUI.Modal.open(confirmConfig);
                if (!overwrite) return;
            }

            // 冲突禁止打包（优先级高于勾选）：任何已勾选的冲突项若未做决策，则不允许出包
            var unresolved = _allItems
                .Where(i => i.IsChecked && i.Status == CompareStatus.Conflict && i.MergeDecision == MergeDecision.None)
                .ToList();
            if (unresolved.Count > 0)
            {
                string blockMsg = string.Format(
                    LanguageManager.Instance.GetText("ModMergeTool.ConflictBlockMerge",
                        "存在 {0} 个未决策的冲突文件，请先在工具栏「全部使用包1 / 全部使用包2」或每行下拉中做出决策后再合并。"),
                    unresolved.Count);
                NotifyWarn(blockMsg);
                return;
            }

            _isRunning = true;
            _startButton.Enabled = false;
            _scanButton.Enabled = false;
            // 日志区若处于折叠状态，合并开始时自动展开（对齐 ARZ 编辑器"检测过程可见"行为）。
            // 注意：AntdUI 的箭头折叠走 SplitPanelState（内部 Expand/Collapse），与 WinForms 的
            // Panel2Collapsed 是两套独立机制，后者不影响箭头折叠态。
            if (_logSplit != null && !_logSplit.SplitPanelState) _logSplit.SplitPanelState = true;
            _cts = new CancellationTokenSource();

            try
            {
                var decisions = new Dictionary<string, string>();
                var customPaths = new Dictionary<string, string>();
                var renamed = new Dictionary<string, string>();

                void EmitRenamed(CompareItem it)
                {
                    string p1 = it.RenamedOutputPath1 ?? it.FileName;
                    string p2 = it.RenamedOutputPath2 ?? it.FileName;
                    bool hasR1 = !string.IsNullOrEmpty(it.RenamedTempPath1);
                    bool hasR2 = !string.IsNullOrEmpty(it.RenamedTempPath2);
                    if (hasR1 || hasR2)
                    {
                        if (hasR1) { decisions[p1] = "C"; customPaths[p1] = it.RenamedTempPath1; }
                        if (hasR2) { decisions[p2] = "C"; customPaths[p2] = it.RenamedTempPath2; }
                    }
                    else ReleaseRenamedItem(it, decisions, customPaths);
                }

                foreach (var item in _allItems.Where(i => i.IsChecked))
                {
                    // 决策优先：改名走改名输出，其次编辑(合)→C，再使用包1/包2。
                    switch (item.MergeDecision)
                    {
                        case MergeDecision.Renamed:
                            EmitRenamed(item);
                            continue;
                        case MergeDecision.Merged:
                            decisions[item.FileName] = "C";
                            customPaths[item.FileName] = !string.IsNullOrEmpty(item.EditedTempPath) ? item.EditedTempPath : Path.Combine(_tempDir ?? "", item.FileName);
                            continue;
                        case MergeDecision.Use1:
                            decisions[item.FileName] = "1";
                            continue;
                        case MergeDecision.Use2:
                            decisions[item.FileName] = "2";
                            continue;
                        case MergeDecision.None:
                        default:
                            break;
                    }
                    // 未决策 → 按原始状态兜底
                    if (item.IsEdited || item.IsMerged)
                    {
                        decisions[item.FileName] = "C";
                        string cp = !string.IsNullOrEmpty(item.EditedTempPath) ? item.EditedTempPath : (string.IsNullOrEmpty(_tempDir) ? "" : Path.Combine(_tempDir, item.FileName));
                        customPaths[item.FileName] = cp;
                    }
                    else if (item.Status == CompareStatus.Renamed)
                    {
                        EmitRenamed(item);
                    }
                    else if (item.Status == CompareStatus.Only1) { decisions[item.FileName] = "1"; }
                    else if (item.Status == CompareStatus.Only2) { decisions[item.FileName] = "2"; }
                    else if (item.Status == CompareStatus.Same) { decisions[item.FileName] = "1"; }
                    else if (item.Status == CompareStatus.Conflict)
                    {
                        if (item.MergeDecision == MergeDecision.Use1) decisions[item.FileName] = "1";
                        else if (item.MergeDecision == MergeDecision.Use2) decisions[item.FileName] = "2";
                        else if (item.MergeDecision == MergeDecision.Merged) { decisions[item.FileName] = "C"; customPaths[item.FileName] = !string.IsNullOrEmpty(item.EditedTempPath) ? item.EditedTempPath : Path.Combine(_tempDir ?? "", item.FileName); }
                        // None 未决策则跳过
                    }
                }


                bool showDetail = SettingsManager.ShowDetailLog;
                int totalCount = decisions.Count;
                var mergeLogLines = new List<string>(64);
                var mergeErrorLines = new List<string>(8);
                int use1Count = decisions.Values.Count(d => d == "1");
                int use2Count = decisions.Values.Count(d => d == "2");
                int customCount = decisions.Values.Count(d => d == "C");
                int renameCount = decisions.Values.Count(d => d == "RC");

                // 过程日志 → 底部日志框（仅"开始合并"后；[HH:mm:ss] [INFO] 前缀，与打包/保存日志同格式）。
                // Spin 遮罩内的简要文本走下方 PushLog 通道不变，日志框负责持久记录。
                void PanelInfo(string text) => _mergeLogView?.Enqueue(LogService.FormatLine(LogLevel.Info, text));
                PanelInfo(string.Format(LanguageManager.Instance.GetText("ModMergeLog.Preparing", "准备合并，共 {0} 个文件"), totalCount));
                if (showDetail)
                {
                    foreach (var kv in decisions)
                    {
                        string action = kv.Value switch
                        {
                            "1" => LanguageManager.Instance.GetText("ModMergeLog.UsePkg1", "使用包1"),
                            "2" => LanguageManager.Instance.GetText("ModMergeLog.UsePkg2", "使用包2"),
                            "C" => LanguageManager.Instance.GetText("ModMergeLog.Custom", "编辑后文件"),
                            "RC" => LanguageManager.Instance.GetText("ModMergeLog.RenameCoexist", "改名共存"),
                            _ => kv.Value
                        };
                        PanelInfo($"[{action}] {kv.Key}");
                    }
                }

                var targetCtrl = (Control)_mergeTable ?? this;
                var spinCfg = new AntdUI.Spin.Config
                {
                    Text = LanguageManager.Instance.GetText("ModMergeTool.Merging", "正在合并..."),
                    CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                    CancelTokenSource = _cts
                };

                bool mergeSuccess = false;
                await AntdUI.Spin.open(targetCtrl, spinCfg, async cfg =>
                {
                    var logLines = mergeLogLines;
                    string pctHead = null;
                    string curItem = null;
                    void UpdateSpinText()
                    {
                        var parts = new List<string>(16);
                        if (!string.IsNullOrEmpty(pctHead)) parts.Add(pctHead);
                        if (showDetail)
                        {
                            const int maxLines = 11;
                            var tail = logLines.Count > maxLines ? logLines.GetRange(logLines.Count - maxLines, maxLines) : logLines;
                            parts.AddRange(tail);
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(curItem)) parts.Add(curItem);
                            if (mergeErrorLines.Count > 0)
                            {
                                var errTail = mergeErrorLines.Count > 3 ? mergeErrorLines.GetRange(mergeErrorLines.Count - 3, 3) : mergeErrorLines;
                                parts.AddRange(errTail);
                            }
                            if (parts.Count == 0)
                            {
                                var tail = logLines.Count > 2 ? logLines.GetRange(logLines.Count - 2, 2) : logLines;
                                parts.AddRange(tail);
                            }
                        }
                        var text = string.Join("\n", parts);
                        if (InvokeRequired) BeginInvoke((Action)(() => cfg.Text = text));
                        else cfg.Text = text;
                    }
                    void PushLog(string line, bool force = false)
                    {
                        logLines.Add(line);
                        if (line.StartsWith("ERROR:", StringComparison.Ordinal) || line.StartsWith("WARNING:", StringComparison.Ordinal))
                            mergeErrorLines.Add(line);
                        if (!force && showDetail && (logLines.Count & 15) != 0) return;
                        UpdateSpinText();
                    }

                    PushLog(string.Format(LanguageManager.Instance.GetText("ModMergeLog.Preparing", "准备合并，共 {0} 个文件"), totalCount), true);
                    if (showDetail)
                    {
                        foreach (var kv in decisions)
                        {
                            string action = kv.Value switch
                            {
                                "1" => LanguageManager.Instance.GetText("ModMergeLog.UsePkg1", "使用包1"),
                                "2" => LanguageManager.Instance.GetText("ModMergeLog.UsePkg2", "使用包2"),
                                "C" => LanguageManager.Instance.GetText("ModMergeLog.Custom", "编辑后文件"),
                                "RC" => LanguageManager.Instance.GetText("ModMergeLog.RenameCoexist", "改名共存"),
                                _ => kv.Value
                            };
                            PushLog($"  [{action}] {kv.Key}");
                        }
                    }

                    var progress = new ThrottledProgressCallback((pct, msg) =>
                    {
                        bool isError = msg != null && (msg.StartsWith("ERROR:", StringComparison.Ordinal) || msg.StartsWith("WARNING:", StringComparison.Ordinal));
                        if (isError || (showDetail && !string.IsNullOrEmpty(msg)))
                            PushLog(msg, isError);
                        if (pct >= 0)
                        {
                            var frac = pct / 100F;
                            pctHead = string.Format(LanguageManager.Instance.GetText("ModMergeTool.MergingPct", "正在合并... {0}%"), pct);
                            curItem = (isError || (showDetail && !string.IsNullOrEmpty(msg))) ? null : msg;
                            if (InvokeRequired) BeginInvoke((Action)(() => cfg.Value = frac));
                            else cfg.Value = frac;
                            UpdateSpinText();
                        }
                    }, _cts.Token, _mergeLogView);

                    if (_packageType == "ARZ")
                        mergeSuccess = await Task.Run(() => GDPackEditorAPI.MergeArz(_basePath, _modPath, _outputPath, decisions, customPaths, renamed, progress, _cts.Token), _cts.Token);
                    else
                        mergeSuccess = await Task.Run(() => GDPackEditorAPI.MergeArc(_basePath, _modPath, _outputPath, decisions, customPaths, renamed, progress, _cts.Token), _cts.Token);
                });

                if (mergeSuccess)
                {
                    var s = ComputeStats();

                    // 统计面板：与扫描区统计标签完全同款样式，最后一行为合并后总文件数
                    var statsPanel = new FlowLayoutPanel
                    {
                        Width = 420,
                        WrapContents = true,
                        FlowDirection = FlowDirection.LeftToRight,
                        BackColor = Color.Transparent,
                        Margin = Padding.Empty,
                        Padding = Padding.Empty
                    };
                    var lastTag = AddStatsTag(statsPanel, "ModMergeTool.StatPkg1Total", "包1总", s.Pkg1Total, TagBlueBack, TagBlueFore);
                    lastTag = AddStatsTag(statsPanel, "ModMergeTool.StatPkg1Only", "包1独有", s.Only1, TagBlueBack, TagBlueFore);
                    lastTag = AddStatsTag(statsPanel, "ModMergeTool.StatPkg2Total", "包2总", s.Pkg2Total, TagGreenBack, TagGreenFore);
                    lastTag = AddStatsTag(statsPanel, "ModMergeTool.StatPkg2Only", "包2独有", s.Only2, TagGreenBack, TagGreenFore);
                    lastTag = AddStatsTag(statsPanel, "ModMergeTool.StatSameName", "同名", s.SameName, TagDefaultBack, TagDefaultFore);
                    lastTag = AddStatsTag(statsPanel, "ModMergeTool.StatSame", "相同", s.Same, TagDefaultBack, TagDefaultFore);
                    lastTag = AddStatsTag(statsPanel, "ModMergeTool.StatConflict", "冲突", s.Conflict, TagRedBack, TagRedFore);
                    statsPanel.SetFlowBreak(lastTag, true);
                    var totalLabel = new AntdUI.Label
                    {
                        Text = string.Format(LanguageManager.Instance.GetText("ModMergeTool.MergeResultTotal", "合并后总文件数：{0} 个"), totalCount),
                        AutoSize = true,
                        Margin = new Padding(0, 6, 0, 0),
                        Font = FontHelper.UiFont(10, FontStyle.Bold)
                    };
                    statsPanel.Controls.Add(totalLabel);
                    statsPanel.Height = statsPanel.GetPreferredSize(new Size(420, 0)).Height;

                    var okMcfg = new AntdUI.Modal.Config(
                        LanguageManager.Instance.GetText("ModMergeTool.MergeComplete", "合并完成"),
                        statsPanel)
                    {
                        Icon = AntdUI.TType.Success,
                        OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                        MaskClosable = false
                    };
                    okMcfg.SetCancelHide();
                    AntdUI.Modal.open(okMcfg);
                }
                else
                {
                    string failDetail = mergeErrorLines.Count > 0
                        ? string.Join("\n", mergeErrorLines)
                        : LanguageManager.Instance.GetText("ModMergeTool.MergeFailed", "合并失败");
                    var failMcfg = new AntdUI.Modal.Config(
                        LanguageManager.Instance.GetText("ModMergeTool.MergeFailed", "合并失败"),
                        failDetail)
                    {
                        Icon = AntdUI.TType.Error,
                        OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                        MaskClosable = false
                    };
                    failMcfg.SetCancelHide();
                    AntdUI.Modal.open(failMcfg);
                }
            }
            catch (OperationCanceledException)
            {
                var cancelMcfg = new AntdUI.Modal.Config(
                    LanguageManager.Instance.GetText("ModMergeTool.Cancelled", "已取消"),
                    LanguageManager.Instance.GetText("ModMergeTool.Cancelled", "已取消"))
                {
                    Icon = AntdUI.TType.Warn,
                    OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                    MaskClosable = false
                };
                cancelMcfg.SetCancelHide();
                AntdUI.Modal.open(cancelMcfg);
            }
            catch (Exception ex)
            {
                var errMcfg = new AntdUI.Modal.Config(
                    LanguageManager.Instance.GetText("Common.Error", "错误"),
                    ex.Message)
                {
                    Icon = AntdUI.TType.Error,
                    OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                    MaskClosable = false
                };
                errMcfg.SetCancelHide();
                AntdUI.Modal.open(errMcfg);
            }
            finally
            {
                _isRunning = false;
                _startButton.Enabled = true;
                _scanButton.Enabled = true;
                // 操作收尾：封口并收起日志框末尾折叠组 + 强制刷新（成功/失败/取消三路都要收尾）
                _mergeLogView?.RequestFinalize();
                _mergeLogView?.FlushNow(true);
                CleanupTempDir();
            }
        }

        private void RefreshHistoryTable()
        {
            if (_historyTable == null) return;
            var list = SettingsManager.GetMergeHistory();
            var rows = new List<MergeHistoryRow>();
            foreach (var e in list)
            {
        rows.Add(new MergeHistoryRow
            {
                Id = e.Id,
                BasePath = e.BasePath,
                ModPath = e.ModPath,
                ExistsBase = PathCheckHelper.Exists(e.BasePath),
                ExistsMod = PathCheckHelper.Exists(e.ModPath),
                OpenTime = e.OpenTime,
                    OpenTimeText = e.OpenTime.ToString("yyyy-MM-dd HH:mm"),
                    OpCells = new AntdUI.ICell[]
                    {
                        new AntdUI.CellButton("open_" + e.Id, LanguageManager.Instance.GetText("ModMergeTool.HistoryOpen", "打开"), AntdUI.TTypeMini.Primary),
                        new AntdUI.CellButton("undo_" + e.Id, LanguageManager.Instance.GetText("ModMergeTool.HistoryUndo", "撤销"), AntdUI.TTypeMini.Error)
                    }
                });
            }
            _historyTable.DataSource = rows;
        }

        public void LoadHistoryPair(string basePath, string modPath)
        {
            if (!string.IsNullOrEmpty(basePath) && _basePathInput != null) _basePathInput.Text = basePath;
            if (!string.IsNullOrEmpty(modPath) && _modPathInput != null) _modPathInput.Text = modPath;
        }

        private void OnHistoryTableButtonClick(object sender, AntdUI.TableButtonEventArgs e)
        {
            if (e.Record is not MergeHistoryRow row) return;
            string btnId = e.Btn?.Id ?? "";
if (btnId.StartsWith("open_"))
    {
        var missingMsgs = new List<string>(2);
        if (!row.ExistsBase)
            missingMsgs.Add(string.Format(LanguageManager.Instance.GetText("ModMergeTool.FileMissingPkg1", "包1文件不存在：{0}"), row.BasePath));
        if (!row.ExistsMod)
            missingMsgs.Add(string.Format(LanguageManager.Instance.GetText("ModMergeTool.FileMissingPkg2", "包2文件不存在：{0}"), row.ModPath));
        if (missingMsgs.Count > 0)
        {
            NotifyWarn(string.Join("\n", missingMsgs));
            return;
        }
        if (!string.IsNullOrEmpty(row.BasePath)) _basePathInput.Text = row.BasePath;
        if (!string.IsNullOrEmpty(row.ModPath)) _modPathInput.Text = row.ModPath;
    }
            else if (btnId.StartsWith("undo_"))
            {
                SettingsManager.RemoveMergeHistory(row.Id);
                RefreshHistoryTable();
            }
        }

        private void OnHistoryClearClick(object sender, EventArgs e)
        {
            var list = SettingsManager.GetMergeHistory();
            if (list.Count == 0) return;
            var cfg = new AntdUI.Modal.Config(this, LanguageManager.Instance.GetText("ModMergeTool.History", "历史记录"),
                LanguageManager.Instance.GetText("ModMergeTool.HistoryClear", "清空历史") + "?")
            { OkType = AntdUI.TTypeMini.Error };
            cfg.SetOk(LanguageManager.Instance.GetText("Common.OK", "确定"));
            cfg.SetCancel(LanguageManager.Instance.GetText("Common.Cancel", "取消"));
            cfg.OnOk = _ => { SettingsManager.ClearMergeHistory(); RefreshHistoryTable(); return true; };
            AntdUI.Modal.open(cfg);
        }

        private void CleanupTempDir()
        {
            try
            {
                if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, true);
            }
            catch { }
        }

        /// <summary>
        /// 保存本窗体合并差异表的列宽记忆。
        /// 供主窗体退出时代理兜底调用：主窗体先关时本窗体收不到自身 OnFormClosing（消息循环直接结束），
        /// 列宽会永远丢失。Save 内部去重，重复调用无 IO；表未创建 / 开关关闭时内部直接跳过。
        /// </summary>
        internal void SaveColumnWidths()
        {
            TableWidthStore.Save(TableWidthStore.TableId.MergeTable, _mergeTable);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_isRunning) _cts?.Cancel();
            // 关窗快照列宽记忆（TableWidthStore：合并差异表；设置开关关闭时内部直接跳过）
            TableWidthStore.Save(TableWidthStore.TableId.MergeTable, _mergeTable);
            CleanupTempDir();
            base.OnFormClosing(e);
        }
    }

    /// <summary>
    /// MOD 合并进度回调：Spin 文本通道（进度百分比/遮罩内简要状态）+ 底部日志框（持久记录，可空）。
    /// 日志框写入与主界面 GuiProgressCallback 同款格式（[HH:mm:ss] [级别] 前缀；带 groupKey 的警告行
    /// 行首加 "- " 标记 → 日志框按记录折叠分组，见 <see cref="LogFoldSupport"/>）。
    /// 落盘走 LogService.WriteFileOnly（写文件、不广播主界面 UI 通道），警告/错误可事后在日志文件中排查。
    /// </summary>
    public class ThrottledProgressCallback : IProgressCallback
    {
        private readonly Action<int, string> _report;
        private readonly CancellationToken _ct;
        private readonly LogView _log;   // 合并底部日志框（可空：无日志框的旧调用保持原行为）
        private DateTime _lastUpdate = DateTime.MinValue;
        private readonly int _minIntervalMs = 200;

        public ThrottledProgressCallback(Action<int, string> report, CancellationToken ct, LogView log = null)
        {
            _report = report;
            _ct = ct;
            _log = log;
        }

        public void Report(int percentage, string message)
        {
            _ct.ThrowIfCancellationRequested();
            percentage = Math.Max(0, Math.Min(100, percentage));
            var now = DateTime.UtcNow;
            if (percentage >= 100 || (now - _lastUpdate).TotalMilliseconds >= _minIntervalMs)
            {
                _lastUpdate = now;
                _report?.Invoke(percentage, message);
            }
        }

        public void ReportLog(string message)
        {
            // 合并过程日志 → 底部日志框（[HH:mm:ss] [INFO] 前缀，与打包/保存日志同格式）
            _log?.Enqueue(LogService.FormatLine(LogLevel.Info, message));
            if (!string.IsNullOrEmpty(message))
                _report?.Invoke(-1, message);
        }
        public void ReportError(string error)
        {
            string text = string.Format(
                LanguageManager.Instance.GetText("Log.PrefixError", "错误: {0}"), error);
            LogService.WriteFileOnly(LogLevel.Error, text);
            _log?.Enqueue(LogService.FormatLine(LogLevel.Error, text));
            _report?.Invoke(0, "ERROR: " + error);
        }
        public void ReportWarning(string warning, string groupKey = null)
        {
            string text = string.Format(
                LanguageManager.Instance.GetText("Log.PrefixWarn", "警告: {0}"), warning);
            LogService.WriteFileOnly(LogLevel.Warn, text);
            if (groupKey != null && _log != null)
            {
                // 带分组键（记录名）：子行行首 "- " 标记 + groupKey 打标 → 日志框按记录折叠分组（与主界面同款）
                _log.Enqueue("- " + LogService.FormatLine(LogLevel.Warn, text), groupKey: groupKey);
            }
            else
            {
                _log?.Enqueue(LogService.FormatLine(LogLevel.Warn, text));
            }
            _report?.Invoke(0, "WARNING: " + warning);
        }
        public void LogOverwrite(string message) { }

        public void LogFinalize()
        {
            // 操作收尾：封口并收起末尾折叠组，随后强制刷新（报告行全部落进日志框）
            _log?.RequestFinalize();
            _log?.FlushNow(true);
        }

        public void UpdateProgress(int percentage, string message)
        {
            _ct.ThrowIfCancellationRequested();
            percentage = Math.Max(0, Math.Min(100, percentage));
            var now = DateTime.UtcNow;
            if (percentage >= 100 || (now - _lastUpdate).TotalMilliseconds >= _minIntervalMs)
            {
                _lastUpdate = now;
                _report?.Invoke(percentage, message);
            }
        }
    }
}






