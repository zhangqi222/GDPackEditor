using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GDPackEditor
{
    public partial class ArzArcEditorForm : AntdUI.BaseForm
    {
        public static string AppDirectory { get; set; }

        private string _archiveFilePath;
        /// <summary>跨窗定位请求带过来的关键词（搜索结果「打开」时记下，供后续「定位到关键词」使用）</summary>
        private bool _isArz;
        private FileStream _archiveStream;
        private ARZReader _arzReader;
        private ARCFile _arcFile;
        private Dictionary<string, TemplateNode> _templates;
        private TempSessionManager _session;

        private List<NodeData> _originalRootNodes;
        private string _currentFileRelPath;
        private TemplateNode _currentTemplate;
        private List<TemplateVariableInfo> _currentVariables;
        // "DBR 有而模板无"的字段（独立列表，不混入 _currentVariables，避免波及保存/批量编辑/多文件合并等既有逻辑）
        private List<TemplateVariableInfo> _notInTemplateVars;
        private string _currentDbrTplName; // DBR 文件中原始的模板名称
        private List<string> _originalDbrLines; // 原始 DBR 的逐行内容（不含 templateName 行）
        private string _sourceBaselineText;     // 源码模式基线文本（打开文件/切到源码模式时捕获），用于行级变更记录
        private string _sourceBaselinePath;     // 基线所属文件的相对路径（防止跨文件误比对）
        private bool _editorDirty;

        // 多文件同步编辑（第四阶段）
        private List<string> _multiFileRelPaths = new List<string>(); // [0]=对比基准（首列）
        private Dictionary<string, List<TemplateVariableInfo>> _multiFileVariables = new Dictionary<string, List<TemplateVariableInfo>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<string>> _multiFileOrigLines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, Dictionary<string, TemplateVariableInfo>> _multiFileVarLookup = new Dictionary<string, Dictionary<string, TemplateVariableInfo>>(StringComparer.OrdinalIgnoreCase); // 变量名→变量 O(1) 查找（渲染每格都要取值，线性扫描会卡）
        private System.Collections.Concurrent.ConcurrentDictionary<string, string> _dbrTplNameCache = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 文件列表行的显示文本缓存（描述 / 本地化名 / 标签 / 模板）：以"内容依据的时间戳"为失效键
        /// （有临时文件取临时文件时间，否则取包内记录时间）。用于"改值 / 切文件"等重复刷新同一文件的场景——
        /// 原实现每次都解压整条记录 + 解析整份 DBR 文本，单行刷新会把这个开销放大到每次编辑。
        /// 打开包与语言切换时整体清空（内容来源变了）。
        /// </summary>
        private readonly Dictionary<string, FileDisplayTextCache> _fileDisplayTextCache
            = new Dictionary<string, FileDisplayTextCache>(StringComparer.OrdinalIgnoreCase);

        private sealed class FileDisplayTextCache
        {
            public DateTime Stamp;
            public string Description;
            public string Annotation;
            public string AnnotationTag;
            public string Template;
        }
        private System.Threading.CancellationTokenSource _scanCts;
        private bool _isScanningTemplates;
        private System.Threading.CancellationTokenSource _multiLoadCts; // 多文件模式后台加载的取消源
        private bool _isMultiLoading; // 多文件模式后台加载中，防重入

        private bool _isLoadingFile; // guard against events during file load
        private System.Windows.Forms.Timer _debounceTimer;

        // 文件修改追踪
        private HashSet<string> _modifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, HashSet<string>> _modifiedDbrFields = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _addedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _deletedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 本会话经"导入"落盘的文件（含同名覆盖导入）：保存入包时时间戳以导入的实体文件为准，
        // 与"编辑器内手改"（保留原包内时间）区分开
        private HashSet<string> _importedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false); // 去除BOM头，AssetManager.exe不识别BOM
        private Dictionary<string, int?> _deletedFileRecordIndices = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, AntdUI.TreeItem> _fileNodeMap = new Dictionary<string, AntdUI.TreeItem>(StringComparer.OrdinalIgnoreCase);
        private List<string> _statusChangeLog = new List<string>(); // 文件路径，按变更时间顺序排列
        private List<NodeData> _fileTreeRootNodes;
        private HashSet<string> _allKnownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<string> _allFolderPaths = new List<string>();

        private string GetAnnotationSuffix(string folderName) =>
            FolderMapping.GetLocalizedName(folderName) is string loc
            && !string.IsNullOrWhiteSpace(loc)
            && !string.Equals(loc, folderName, StringComparison.OrdinalIgnoreCase) ? $" ({loc})" : "";

        private string GetFileAnnotationSuffix(string relPath) =>
            _isLanguagePackActive && _currentLanguageDict != null
            && _currentLanguageDict.TryGetValue(relPath, out var ann) ? $" ({ann})" : "";

        // UI
        
        private AntdUI.Tree _fileTree;
        private AntdUI.Tree _folderTree;
        private Panel _folderTreeScrollPanel;
        private AntdUI.Tree _flatTree;
        private AntdUI.Segmented _segFileTreeMode;
        private bool _syncingViewSeg;
        private bool _flatViewMode;
        private Image _closedFolderIcon;
        private Image _openFolderIcon;
        private string _currentFolderPath;
        private HashSet<string> _preservedExpandedPaths;
        private Dictionary<string, string> _currentLanguageDict;
        private bool _isLanguagePackActive;
        private Dictionary<string, Dictionary<string, string>> _dbrTagCache;
        private static readonly string[] KnownTagFields = LocalizationHelper.KnownTagFields;
        private AntdUI.IContextMenuStripItem[] _contextMenuItems;
        private AntdUI.IContextMenuStripItem[] _dbrGridContextMenuItems;
        private AntdUI.ContextMenuStripItem _contextNewFolder;
        private AntdUI.ContextMenuStripItem _contextNewFile;
        private AntdUI.ContextMenuStripItem _contextImport;
        private AntdUI.ContextMenuStripItem _contextExport;
        private AntdUI.ContextMenuStripItem _contextDelete;
        private AntdUI.ContextMenuStripItem _contextReload;
        private AntdUI.TreeItem _contextMenuNode;
        private string _contextMenuPath;
        private bool _rightClickOnNode;
        private AntdUI.Panel _editorPanel;
        private AntdUI.Input _currentFilePathLabel;
        private AntdUI.Tree _outlineTree;
        private AntdUI.Table _dbrGrid;

        // Data row types for AntdUI.Table
        private class DbrGridRow
        {
            public string FieldName { get; set; }
            public AntdUI.CellLink FieldNameCell { get; set; }
            public string FieldClass { get; set; }
            public string FieldType { get; set; }
            public string FieldValue { get; set; }
            public string FieldDesc { get; set; }       // 字段备注[官方]（TPL description；"不在模板中"的行放警告文案）
            public bool NotInTemplate { get; set; }     // 该行字段不在模板中（特殊色 + 只读）
            public AntdUI.CellLink[] CellLinks { get; set; }
            public TemplateVariableInfo Tag { get; set; }
            public bool RowReadOnly { get; set; }
            public bool IsFileOpsRow { get; set; }
            public bool IsArray { get; set; }
            public Color CellForeColor { get; set; } = Color.Empty;
            public Color CellBackColor { get; set; } = Color.Empty;
        }

        private class FileListGridRow : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;
            private bool _checked;
            public bool Checked
            {
                get => _checked;
                set
                {
                    if (_checked == value) return;
                    _checked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
                }
            }
            public string RelPath { get; set; }
            public string FileName { get; set; }
            public string FileDescription { get; set; }
            public string ModifyTime { get; set; }
            public string Notes { get; set; }
            public string AnnotationTag { get; set; }
            public string Template { get; set; }    // DBR 模板名（加载时顺带提取，供模板列显示与模板筛选）
            public NodeData Tag { get; set; }
            public Color RowForeColor { get; set; } = ThemeManager.Text;
        }

        private class FavoriteRow
        {
            public string Note { get; set; }
            public string Path { get; set; }
        }

        private class ModifiedFileRow
        {
            public string FilePath { get; set; }
            public AntdUI.CellTag[] CellTags { get; set; }
            public AntdUI.CellLink[] CellLinks { get; set; }
        }

        private AntdUI.Input _readOnlyView;
        private AntdUI.Table _modifiedList;
        private AntdUI.Checkbox _overwriteOnSaveCheckBox;
        private AntdUI.Button _saveButton;
        private AntdUI.Button _compactButton;
        private AntdUI.Button _tombstoneCheckButton;
        private AntdUI.Button _detectButton;                // 内容检测（等价 CLI --check 的 ARZ 内容检查）
        private AntdUI.Button _cancelButton;
        private AntdUI.Button _terminateButton;
        private AntdUI.Panel _bottomBarPanel;
        private CancellationTokenSource _operationCts;
        private AntdUI.Panel _outputPanel;
        private ScintillaNET.Scintilla _logScintilla;
        /// <summary>groupKey 非空 = 该行属于某个折叠组（记录名，由 ReportWarning 生成侧打标；仿主界面 LogView）</summary>
        private ConcurrentQueue<(string text, bool isOverwrite, string relPath, string groupKey)> _logQueue = new();
        private List<string> _logEntries = new();
        /// <summary>与 <see cref="_logEntries"/> **一一平行**：每行的"包内记录相对路径"（无则 null）。
        /// 由各写入点显式提供（结构化标记，不从文本猜路径）；同时用于日志行双击跳转。</summary>
        private List<string> _logEntryRefs = new();
        /// <summary>与 <see cref="_logEntries"/> **一一平行**：行类型（普通/组头/组子），折叠分组渲染用</summary>
        private readonly List<LogRowKind> _logRowKinds = new();
        /// <summary>与 <see cref="_logEntries"/> **一一平行**：行的折叠分组键（普通行为 null）。
        /// 与 <see cref="_logEntryRefs"/> 分开的原因：relPath 兼任双击跳转（带路径的 INFO 行也有），
        /// 折叠只认"带 groupKey 的警告/错误行"，两者语义不同。</summary>
        private readonly List<string> _logRowKeys = new();
        /// <summary>最近一个折叠组（封口≠失效：显示流中相邻的同名子行仍可续入，跨批次不断组）</summary>
        private LogFoldGroup _logOpenGroup;
        /// <summary>待渲染处理的头行文本更新（批次内只积累，渲染层统一消费）</summary>
        private readonly List<LogFoldGroup> _logHeaderUpdates = new();
        /// <summary>待收起的组（封口时机由内容决定：普通行/新组到达，或操作收尾信号）</summary>
        private readonly List<LogFoldGroup> _logPendingCollapse = new();
        /// <summary>操作收尾信号（LogFinalize 置位，FlushLogQueue 消费：封口末组并收起）</summary>
        private int _logFinalizeRequested;
        private System.Windows.Forms.Timer _logTimer;
        // ===== 编辑器日志的渲染状态与上限（与主界面 LogView 同一套方案：增量渲染 + 行数上限） =====
        /// <summary>已渲染到 Scintilla 的行数（-1 = 需要全量重建，如清空/过滤/裁剪之后）</summary>
        private int _renderedLogCount = -1;
        /// <summary>覆盖行（瞬态进度）替换了末行内容：增量渲染下只需替换 Scintilla 末行文本</summary>
        private bool _logOverwriteDirty;
        /// <summary>编辑器日志行数上限（与主界面文件列表日志相同，超出批量裁头部）</summary>
        private const int EditorLogMaxEntries = 50000;
        /// <summary>单次刷新最多出队的条数（避免一次构造超大字符串；剩余交给下一次定时器追平）</summary>
        private const int LogDequeueBudget = 500;
        /// <summary>强制刷新时的出队上限（收尾时尽快追平积压）</summary>
        private const int LogDequeueBudgetForce = 2000;
        private AntdUI.Input _logSearchBox;
        private AntdUI.Segmented _logLevelFilter;       // 四级筛选（全部/信息/警告/错误），与主界面 LogView 同规格
        private AntdUI.Button _logClearAllBtn;
        private AntdUI.Button _logExportBtn;            // 导出日志（面板当前可见行）

        // ===== 日志行双击跳转（仅编辑器）：结构化标记 + Scintilla 热点指示器 =====
        /// <summary>热点指示器编号（marker 0–4 与 indicator 命名空间独立，故 7 不冲突）</summary>
        private const int LogLinkIndicator = 7;
        /// <summary>与"**可见行**"一一平行：该行可跳转时的目标信息（包内相对路径 + 路径在该行内的字符区间）</summary>
        private List<(string relPath, int start, int len)?> _logLinkRanges = new();
        /// <summary>已渲染的最后一行是否对应 <see cref="_logEntries"/> 的最后一条（覆盖行可原地替换的前提）</summary>
        private bool _logLastRenderedIsLastEntry;
        /// <summary>已渲染视图所用的过滤条件签名（级别下标 | 搜索词）：变化即全量重建，保证可见行与文本一致</summary>
        private string _logRenderedFilterKey;
        /// <summary>上次处理双击跳转的时间（热点事件与鼠标兜底双路由去重）</summary>
        private DateTime _logLinkHandledAt = DateTime.MinValue;
        /// <summary>日志框提示（双击路径可跳转）：挂在日志控件上，随语言切换刷新</summary>
        private ToolTip _logLinkTip;

        // ===== 日志面板行级着色（仅作用于 UI；.log 文件始终为纯文本）=====
        // 颜色统一取 ThemeManager 的日志配色（与公共组件 LogView 同一套，避免两处色值分叉）。
        // 说明：marker 是"每个 Scintilla 实例各自独立"的编号，与 LogView 的 0/1 不会互相影响。
        private const int LogMarkerAdd = 0;    // [add] 新增：绿
        private const int LogMarkerDel = 1;    // [del] 删除：红
        private const int LogMarkerMod = 2;    // [mod] 修改：黄
        private const int LogMarkerWarn = 3;   // 警告：黄
        private const int LogMarkerError = 4;  // 错误：红

        private string _logSearchText = "";
        /// <summary>级别筛选下标：0=全部 1=信息 2=警告 3=错误（与 <see cref="LogLevelFilter"/> 一致）</summary>
        private int _logLevelIndex;
        private System.Windows.Forms.Timer _logSearchTimer;
        /// <summary>源码模式行级日志防抖 Timer（0.5s）：停顿后对基线做一次 diff，单行连续输入合并为一条</summary>
        private System.Windows.Forms.Timer _sourceLogTimer;
        private AntdUI.Checkbox _backupCheckBox;
        private AntdUI.Panel _statusStrip;
        private AntdUI.Label _statusLabel;
        private AntdUI.Label _placeholderLabel;
        private ArrowSplitter _mainSplit;
        private ArrowSplitter _centerRightSplit;
        private ArrowSplitter _logSplit;
        private ArrowSplitter _dbrSplit;
        private AntdUI.Tabs _editorTabControl;
        private ScintillaNET.Scintilla _dbrSourceEditor;
        private AntdUI.Label _sourceNoFilePlaceholder;

        // 文件列表
        private AntdUI.Table _fileListGrid;
        private AntdUI.IContextMenuStripItem[] _fileListContextMenuItems;
        private AntdUI.ContextMenuStripItem _fileListOpenItem;
        private AntdUI.ContextMenuStripItem _fileListExportItem;
        private AntdUI.ContextMenuStripItem _fileListImportItem;
        private AntdUI.ContextMenuStripItem _fileListDeleteItem;

        private AntdUI.ContextMenuStripItem _fileListMultiEditItem;
        private int _fileListContextRow = -1;
        private int _fileListContextCol = -1;
        private FileListGridRow _fileListContextData = null;
        private readonly HashSet<int> _checkedRows1 = new HashSet<int>();
        private bool _syncingSelection;
        private bool _syncOverallPending;
        private bool _dragListActive;
        private bool _dragListMoved;
        private bool _dragListFinished;
        private int _dragListStartRow;
        private int _dragListLastRow;
        private System.Windows.Forms.Timer _dragListScrollTimer;
        private const int DragListScrollStep = 30;
        private const int DragListScrollEdge = 12;
        private int _dbrGridContextRow = -1;
        private int _dbrGridContextCol = -1;
        private NodeData _pendingFileData;
        private Dictionary<string, string> _fileDescriptionCache;
        private readonly object _fileDescCacheLock = new object();
        private readonly object _dbrTagCacheLock = new object();
        private System.Threading.CancellationTokenSource _fileListLoadCts;
        private readonly System.Threading.SemaphoreSlim _fileListLoadGate = new System.Threading.SemaphoreSlim(1, 1);
        private AntdUI.Input _fileListFilterTextBox;
        private System.Windows.Forms.Timer _fileListFilterTimer;
        private AntdUI.Select _templateFilterSelect;          // 模板筛选下拉（与搜索框独立、叠加过滤）
        private AntdUI.Label _templateFilterLabel;            // 「模板：」标签（切语言需带冒号刷新）
        private System.Collections.Generic.IEnumerable<FileListGridRow> _templateOptionSourceRows; // 下拉选项来源（避免重复重建）
        private bool _templateSelectSyncing;                   // 程序设置选中值时抑制事件回环

        private AntdUI.Table _favoritesList;
        private AntdUI.IContextMenuStripItem[] _favoritesContextMenuItems;
        private AntdUI.Button _favoriteButton;
        private AntdUI.Tabs _leftTabCtl;

        // 统一搜索/替换入口（顶部工具条，plan_20260923_UI相关调整）
        private string _searchMode = "filename";       // 搜索范围：filename / content（默认文件名；由顶部单选决定）
        private AntdUI.Label _searchStatusLabel;       // 顶部工具条的进度/状态标签（历史字段名沿用）
        private AntdUI.Panel _topBar;                  // 窗口最顶部的全宽工具条

        /// <summary>工具条行的**控件统一高度**（设计像素，100% 基准）：输入框/下拉/按钮同高，行内观感一致。</summary>
        private const int TopBarCtrlHeight = 44;
        /// <summary>工具条行的**上下留白**（设计像素）：容器高度 = 控件高度 + 2×留白。</summary>
        private const int TopBarRowPad = 4;
        private AntdUI.Button _topHistoryBtn;
        private AntdUI.Button _topOpenBtn;
        private AntdUI.Input _topSearchBox;            // 查找内容（前缀"搜索"）
        private AntdUI.Input _topReplaceBox;           // 替换为（前缀"替换为"；动态显示，仅替换模式）
        private AntdUI.Dropdown _topFuncCombo;         // 搜索/替换 切换下拉（计划第 6 条）
        private string _topFuncMode = "search";        // "search" / "replace"
        private AntdUI.Radio _topScopeFileRadio;       // 范围：文件名
        private AntdUI.Radio _topScopeContentRadio;    // 范围：文件内容（默认）
        private AntdUI.Checkbox _topCaseSensitive;     // 区分大小写（搜索/替换共用）
        private AntdUI.Button _topSearchBtn;           // 搜索按钮（常显；执行中显示 SPIN）
        private AntdUI.Button _topReplaceBtn;          // 替换按钮（仅替换模式；执行中显示 SPIN）
        private AntdUI.Button _topCancelBtn;           // 取消按钮（仅执行中显示，紧贴当前执行的动作按钮）
        private CancellationTokenSource _topActionCts; // 顶部动作的取消源（搜索/查找/替换共用）
        /// <summary>顶部动作状态：0=空闲 1=搜索 2=替换查找 3=替换执行（执行中按钮变"取消"）</summary>
        private int _topAction;

        // 搜索/替换命中直播（文件列表搜索视图）
        private bool _fileListSearchView;
        private AntdUI.AntList<FileListGridRow> _searchViewRows;
        private List<(string path, string fileName, int matchCount, int recordIndex)> _searchViewHits;
        private readonly object _searchViewHitsLock = new object();

        // 文件列表常驻数据（固化全量）与显示集合
        private List<FileListGridRow> _fileListAllRows;
        private AntdUI.AntList<FileListGridRow> _fileListViewRows;

        // 文件列表列头排序状态（外部接管 AntdUI 排序：库只画图标，数据由本项目排序后重绑——
        // 内置 SortDataASC 只重排显示行、不动 DataSource，与"过滤重绑/行号选中"体系打架）
        private string _fileListSortKey;        // 当前排序列 Key（null = 未排序，按包内自然顺序）
        private bool _fileListSortAsc = true;   // true = 升序

        private enum EditorMode { None, FileList, DbrGrid, SourceEditor, ReadOnly }
        private EditorMode _currentEditorMode;

        // 统一搜索
        private AntdUI.Input _fieldFilterTextBox;
        private AntdUI.Button _btnOpenSameTemplate;
        /// <summary>批量编辑入口（仅同模板多文件模式下可见）</summary>
        private AntdUI.Button _btnBatchEdit;
        private System.Windows.Forms.Timer _fieldFilterTimer;
        private string _currentOutlineTag = "__non_zero__";

        // 编辑器字体大小
        private int _editorFontSize = 9;

        // 查找
        private AntdUI.Input _findTextBox;
        private AntdUI.Label _findMatchCountLabel;

        public ArzArcEditorForm(string archiveFilePath)
        {
            // 窗口级主题配置
            Theme()
                .Light("#f0f0f0", "#000000")
                .Dark("#333333", "#ffffff");
            
            _archiveFilePath = archiveFilePath;
            if (!string.IsNullOrEmpty(archiveFilePath))
            {
                SettingsManager.AddRecentFile(archiveFilePath);
                string ext = Path.GetExtension(archiveFilePath)?.ToLower();
                _isArz = ext == ".arz";
                _session = new TempSessionManager(Path.GetFileName(archiveFilePath));
                Text = $"{Path.GetFileName(archiveFilePath)} - " +
                    LanguageManager.Instance.GetText("Editor.Title", "编辑器");
            }
            else
            {
                _isArz = false;
                _session = new TempSessionManager("blank");
                Text = LanguageManager.Instance.GetText("Editor.Title", "编辑器");
            }
            Size = new Size(1400, 900);
            MinimumSize = new Size(1000, 700);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            Load += OnFormLoad;
            InitializeComponents();

            _debounceTimer = new System.Windows.Forms.Timer();
            _debounceTimer.Interval = 300;
            _debounceTimer.Tick += OnDebounceTimerTick;

            // 语言切换刷新：本窗是独立顶层窗体，不在主窗体控件树内 → 需自行订阅
            // （此前切语言后本窗所有文案保持旧语言，实测 44 处残留，含日志工具条"导出/清空日志"）
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
            // 状态型按钮：文本随状态变化，交给各自的更新方法（不能靠静态 key 刷新）
            try { UpdateSameTemplateButtonText(); } catch { }
            try { UpdateFavoriteButtonState(); } catch { }
            // 下拉项文本不在控件树内（Items 集合），单独刷新
            try { RefreshTopBarTexts(); } catch { }
            // 日志框提示（ToolTip 不属控件树）
            try
            {
                _logLinkTip?.SetToolTip(_logScintilla, LanguageManager.Instance.GetText("Editor.LogLinkHint",
                    "双击日志中的文件路径可直达该文件（仅 .dbr / .tpl / .txt / .lua）"));
            }
            catch { }
            // 模板筛选下拉：方法内部有"源行未变则跳过"的早退 → 置空源引用强制重建（语言变了必须重启项文本）
            try
            {
                _templateOptionSourceRows = null;
                UpdateTemplateFilterOptions();
                // AntdUI.Select 的 Text 有缓存：SelectedValue 未变时不会随项文本更新 → 显式重设显示文本
                if (_templateFilterSelect != null && !_templateFilterSelect.IsDisposed)
                {
                    string sel = GetTemplateFilterSelection();
                    _templateFilterSelect.Text = string.IsNullOrEmpty(sel)
                        ? LanguageManager.Instance.GetText("Editor.TemplateFilterAll", "全部模板")
                        : sel;
                    _templateFilterSelect.Invalidate();
                }
            }
            catch { }
            // 「模板：」标签带冒号，单独刷新
            try
            {
                if (_templateFilterLabel != null && !_templateFilterLabel.IsDisposed)
                    _templateFilterLabel.Text = LanguageManager.Instance.GetText("Editor.TemplateFilterLabel", "模板") + "：";
            }
            catch { }
            // 网格内的单元格按钮（✕ 关闭 / 退出多文件编辑）文本在行重建时按当前语言生成 → 重建数据源
            try { RefreshDbrGrid(); } catch { }
            // 表格列头与空数据文案（AntdUI.Table 的列/EmptyText 不在控件树内）
            try { RefreshTableLocalizedTexts(); } catch { }
        }

        /// <summary>
        /// 刷新常驻四张表的**列头**与**空数据文案**：AntdUI.Table 的 Columns/EmptyText 不是子控件，
        /// UiTextRefresher 的控件树遍历覆盖不到，需按"列 Key→语言key"显式刷新。
        /// （对话框内的临时表在弹窗期间无法切语言，不在此列）
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

            SetCols(_favoritesList,
                ("Note", "Common.Note", "备注"),
                ("Path", "Common.Path", "路径"));
            SetCols(_fileListGrid,
                ("FileName", "Editor.FileName", "文件名"),
                ("FileDescription", "Editor.FileDescription", "说明"),
                ("ModifyTime", "Editor.ModifyTime", "修改时间"),
                ("Notes", "Editor.Notes", "本地化名称"),
                ("Template", "Editor.TemplateColumn", "模板"));
            SetCols(_modifiedList,
                ("CellTags", "Common.Status", "状态"),
                ("FilePath", "Common.Path", "路径"));
            SetCols(_dbrGrid,
                ("FieldNameCell", "Editor.FieldName", "字段名"),
                ("FieldClass", "Editor.FieldClass", "字段分组"),
                ("FieldType", "Editor.FieldType", "类型"),
                ("FieldValue", "Editor.FieldValue", "值"));

            // 空数据文案（AntdUI 内置默认是中文"暂无数据"，须显式设置才能跟随语言）
            string noData = LanguageManager.Instance.GetText("Common.NoData", "暂无数据");
            foreach (var t in new[] { _favoritesList, _fileListGrid, _modifiedList })
                if (t != null && !t.IsDisposed) t.EmptyText = noData;
        }

        /// <summary>顶部工具条的多语言文本刷新（下拉/单选/按钮/前缀；见 BuildTopToolbar）</summary>
        private void RefreshTopBarTexts()
        {
            if (_topBar == null || _topBar.IsDisposed) return;
            ApplyTopFuncComboText();
            // 下拉项（Items 不在控件树内）按 Tag 刷新文案
            foreach (var it in _topFuncCombo.Items)
            {
                if (it is not AntdUI.SelectItem si) continue;
                si.Text = si.Tag?.ToString() == "replace"
                    ? LanguageManager.Instance.GetText("Editor.FuncContentReplace", "内容替换")
                    : LanguageManager.Instance.GetText("Editor.FuncGlobalSearch", "全局搜索");
            }
            // 输入框前缀统一带冒号（与 UiTextRefresher 的既有约定一致：key 文本 + ":"）
            _topSearchBox.PrefixText = LanguageManager.Instance.GetText("Editor.FuncSearch", "搜索") + ":";
            _topReplaceBox.PrefixText = LanguageManager.Instance.GetText("Editor.ReplaceToHint", "替换为") + ":";
            _topScopeFileRadio.Text = LanguageManager.Instance.GetText("Editor.SearchModeFileName", "文件名");
            _topScopeContentRadio.Text = LanguageManager.Instance.GetText("Editor.SearchModeContent", "文件内容");
            _topCaseSensitive.Text = LanguageManager.Instance.GetText("Editor.SearchCaseSensitive", "区分大小写");
            // 按钮文案不再随状态改写（进度=按钮上的 SPIN，取消=独立按钮）
            _topSearchBtn.Text = LanguageManager.Instance.GetText("Editor.FuncSearch", "搜索");
            _topReplaceBtn.Text = LanguageManager.Instance.GetText("Editor.FuncReplace", "替换");
            _topCancelBtn.Text = LanguageManager.Instance.GetText("Common.Cancel", "取消");
            _topBar.Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { LanguageManager.Instance.LanguageChanged -= OnLanguageChanged; } catch { }
            base.OnFormClosed(e);
        }

        private void OnFormLoad(object sender, EventArgs e)
        {
            WindowState = FormWindowState.Maximized;
            // 输出会话临时目录：TEMP 被清理软件清空导致"改动丢失"时，用户可据此自查
            if (_session != null)
                LogOutput(string.Format(LanguageManager.Instance.GetText("Log.TempSessionDir", "会话临时目录：{0}"), _session.BasePath), "INFO");
            if (string.IsNullOrEmpty(_archiveFilePath))
            {
                BeginInvoke((Action)(() => OnHistoryClick(null, EventArgs.Empty)));
            }
        }

        // ==================== 跨窗调用入口（EditorLauncher：搜索结果「打开」等）====================

        /// <summary>当前已加载的包路径（空编辑器为 null）。供 EditorLauncher 判定"该包是否已在编辑器中打开"。</summary>
        public string CurrentArchivePath => _archiveFilePath;

        /// <summary>包是否已真正加载出内容（文件树已建）——用于判断"带路径构造"是否已完成加载</summary>
        public bool HasLoadedArchive => _originalRootNodes != null && _originalRootNodes.Count > 0;

        /// <summary>在本编辑器中加载（或切换）一个包 —— 走既有 <see cref="LoadNewArchive"/> 全流程。</summary>
        public void OpenArchiveFromExternal(string archivePath)
        {
            if (string.IsNullOrEmpty(archivePath)) return;
            LoadNewArchive(archivePath);
        }

        /// <summary>
        /// 确保当前包已加载：带路径构造的编辑器若发现尚未加载出内容，补一次加载。
        /// <para>**为什么需要**：本窗的历史记录弹窗只在"<see cref="CurrentArchivePath"/> 为空"时于 Load 事件里弹出，
        /// 所以外部（搜索结果「打开」）必须用带路径构造，避免误弹历史；若该构造路径没有真正加载，则由本方法兜底。</para>
        /// <para>判断放到消息队列末尾（BeginInvoke）：Load 事件里的加载是同步的，先让它跑完再判，
        /// 避免"构造期已加载 + 这里又加载一次"的重复。</para>
        /// </summary>
        public void EnsureArchiveLoaded()
        {
            if (string.IsNullOrEmpty(_archiveFilePath)) return;
            if (!IsHandleCreated) { EnsureLoadedNow(); return; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) return;
                    EnsureLoadedNow();
                }));
            }
            catch { EnsureLoadedNow(); }
        }

        private void EnsureLoadedNow()
        {
            if (IsDisposed || string.IsNullOrEmpty(_archiveFilePath)) return;
            if (HasLoadedArchive) return;
            LoadNewArchive(_archiveFilePath);
        }

        /// <summary>
        /// 等包加载完成后定位到包内文件（跨窗调用）。
        /// <para>新建编辑器时加载收尾时机不确定，故用轻量轮询（100ms × 最多 50 次 ≈ 5s）直到文件树里能找到该文件。</para>
        /// </summary>
        /// <param name="keyword">一并记下关键词，供后续"定位到关键词"使用（本步骤只记不用）</param>
        public void LocateFileWhenReady(string relPath, string keyword = null, int attempt = 0)
        {
            if (IsDisposed) return;
            if (string.IsNullOrEmpty(relPath)) return;

            if (FindFileInTree(relPath))
            {
                SelectFileInTree(relPath);
                // 打开文件后**定位到内容关键词**（搜索结果「打开」的收尾；批8 步骤8/9）。
                // 未传关键词时行为完全不变。
                if (!string.IsNullOrEmpty(keyword)) LocateKeywordWhenReady(keyword, 0);
                return;
            }
            if (attempt >= 50) return;   // 超时放弃（包未加载完 / 文件不存在），不打扰用户

            RunOnceLater(100, () => LocateFileWhenReady(relPath, keyword, attempt + 1));
        }

        /// <summary>
        /// 打开文件后**定位到内容关键词**（搜索结果「打开」的收尾）。
        /// <para>按编辑器当前视图分流：**源码模式**（txt/lua/tpl）→ Scintilla 查找并选中、滚动到位；
        /// **DBR 网格**（.dbr）→ 找第一个字段名或字段值含关键词的行，选中并滚动。</para>
        /// <para>内容尚未就绪时轻量轮询（100ms × 最多 50 次）；找不到就安静放弃，不打扰用户。</para>
        /// </summary>
        public void LocateKeywordWhenReady(string keyword, int attempt = 0)
        {
            if (IsDisposed || string.IsNullOrEmpty(keyword)) return;

            switch (_currentEditorMode)
            {
                case EditorMode.SourceEditor:
                    if (_dbrSourceEditor == null || _dbrSourceEditor.IsDisposed) return;
                    LocateKeywordInSource(keyword);
                    return;

                case EditorMode.DbrGrid:
                    if (!(_dbrGrid.DataSource is List<DbrGridRow> rows) || rows.Count == 0)
                    {
                        if (attempt >= 50) return;   // 网格还没填充完
                        RunOnceLater(100, () => LocateKeywordWhenReady(keyword, attempt + 1));
                        return;
                    }
                    LocateKeywordInGrid(rows, keyword);
                    return;
            }
            // 其它视图（只读 / 未打开）→ 安静放弃
        }

        /// <summary>源码模式：查找关键词并选中、滚动到位（查找口径与 <see cref="FindNext"/> 一致，默认忽略大小写）</summary>
        private void LocateKeywordInSource(string keyword)
        {
            int found = _dbrSourceEditor.FindText(ScintillaNET.SearchFlags.None, keyword, 0, _dbrSourceEditor.TextLength);
            if (found < 0) return;   // 找不到：安静放弃
            _dbrSourceEditor.SetSel(found, found + keyword.Length);
            _dbrSourceEditor.ScrollCaret();
        }

        /// <summary>DBR 网格：定位到第一个字段名 / 字段值含关键词的行（选中 + 滚动可见）</summary>
        private void LocateKeywordInGrid(List<DbrGridRow> rows, string keyword)
        {
            int idx = rows.FindIndex(r =>
                (r.FieldValue ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (r.FieldName ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
            if (idx < 0) return;
            try
            {
                _dbrGrid.SelectedIndexs = new[] { idx };
                _dbrGrid.ScrollLine(rows[idx], true);
            }
            catch { }
        }

        /// <summary>
        /// 等包加载完成后，在**「文件列表」**里定位到指定包内文件（选中 + 滚动可见），**不打开文件内容**。
        /// <para>文件名搜索结果的「打开」用这个：用户要看的是"这个文件在包里哪儿"，而不是进编辑器/源码模式。</para>
        /// </summary>
        public void LocateInFileListWhenReady(string relPath, int attempt = 0)
        {
            if (IsDisposed) return;
            if (string.IsNullOrEmpty(relPath)) return;

            bool loaded = _originalRootNodes != null && _originalRootNodes.Count > 0;
            bool located = loaded && TrySelectFileListRow(relPath);
            if (located) return;
            if (attempt >= 50) return;   // 超时放弃（包未加载完 / 文件不存在）

            RunOnceLater(100, () => LocateInFileListWhenReady(relPath, attempt + 1));
        }

        /// <summary>
        /// 在文件列表里选中并滚动到指定行；行还没出现时先切到该文件所在文件夹并刷新列表，返回 false 表示"再等等"。
        /// </summary>
        private bool TrySelectFileListRow(string relPath)
        {
            try
            {
                if (_fileListViewRows == null || _fileListGrid == null || _fileListGrid.IsDisposed) return false;

                int index = -1;
                for (int i = 0; i < _fileListViewRows.Count; i++)
                {
                    if (string.Equals(_fileListViewRows[i]?.RelPath, relPath, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    // 行还没出现：切到该文件的父文件夹（列表只显示当前文件夹的内容），刷新后等下一轮
                    string parentFolder = (Path.GetDirectoryName(relPath.Replace('/', Path.DirectorySeparatorChar)) ?? "")
                        .Replace('\\', '/');
                    if (_fileListSearchView || !string.Equals(_currentFolderPath, parentFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        ExitSearchFileListView();
                        _currentFolderPath = parentFolder;
                        SelectFolderNode(_currentFolderPath);
                        ShowFileList();
                        RefreshFileList();
                    }
                    return false;
                }

                ShowFileList();
                _fileListGrid.SelectedIndexs = new[] { index + 1 };   // AntdUI.Table 行号 1 起
                _fileListGrid.ScrollLine(_fileListViewRows[index], true);
                _fileListGrid.Focus();
                return true;
            }
            catch
            {
                return true;   // 出错不再重试，避免定时器空转
            }
        }

        /// <summary>延时执行一次（用于等异步加载完成的重试；自动停止并释放定时器）</summary>
        private void RunOnceLater(int milliseconds, Action action)
        {
            var timer = new System.Windows.Forms.Timer { Interval = milliseconds };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                try { action?.Invoke(); } catch { }
            };
            timer.Start();
        }

        private void InitializeComponents()
        {
            var contentPanel = new AntdUI.Panel { Dock = DockStyle.Fill, Back = ThemeManager.BgLayout };
            Controls.Add(contentPanel);

            BuildStatusStrip();

            _mainSplit = new ArrowSplitter
            {
                Dock = DockStyle.Fill,
                CollapsePanel = AntdUI.Splitter.ADCollapsePanel.Panel1,
                CollapsedTextKey = "Editor.CollapseLeft",
                ExpandedTextKey = "Editor.ExpandLeft",
                Lazy = false
            };
            contentPanel.Controls.Add(_mainSplit);

            BuildTopToolbar();
            // 顶部全宽工具条：**最后加入**（AntdUI/WinForms 反向 Dock：最后插入的在最上）
            contentPanel.Controls.Add(_topBar);

            _mainSplit.SplitPanelStateChanged += (s, e) =>
            {
                if (!e.Value)
                {
                    _mainSplit.Panel1MinSize = 0;
                    _mainSplit.SplitterDistance = 0;
                }
                else
                {
                    _mainSplit.Panel1MinSize = (int)(200 * (DpiManager.Scale));
                }
                SettingsManager.EditorLeftPanelCollapsed = !e.Value;
                SettingsManager.Save();
            };

            BuildLeftPanel(_mainSplit.Panel1);

            _centerRightSplit = new ArrowSplitter
            {
                Dock = DockStyle.Fill,
                CollapsePanel = AntdUI.Splitter.ADCollapsePanel.Panel2,
                CollapsedTextKey = "Editor.CollapseModified",
                ExpandedTextKey = "Editor.ExpandModified",
                Lazy = false
            };

            BuildCenterPanel(_centerRightSplit.Panel1);
            BuildRightPanel(_centerRightSplit.Panel2);

            BuildOutputPanel();
            BuildBottomPanel();

            // Layout _mainSplit.Panel2: editor/right split above, output+toolbar below via log splitter
            _mainSplit.Panel2.Controls.Clear();
            _logSplit = new ArrowSplitter
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                CollapsePanel = AntdUI.Splitter.ADCollapsePanel.Panel2,
                CollapsedTextKey = "Editor.CollapseLog",
                ExpandedTextKey = "Editor.ExpandLog",
                Lazy = false
            };
            _logSplit.Panel1.Controls.Add(_centerRightSplit);
            _logSplit.Panel2.Controls.Add(_outputPanel);
            _logSplit.Panel2.Controls.Add(_bottomBarPanel);
            _mainSplit.Panel2.Controls.Add(_logSplit);

            _centerRightSplit.SplitPanelStateChanged += (s, e) =>
            {
                SettingsManager.EditorRightPanelCollapsed = !e.Value;
                SettingsManager.Save();
            };
            _logSplit.SplitPanelStateChanged += (s, e) =>
            {
                SettingsManager.EditorLogPanelCollapsed = !e.Value;
                SettingsManager.Save();
            };
        }

        

        private void BuildStatusStrip()
        {
            _statusStrip = new AntdUI.Panel
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                Back = Color.Transparent
            };
            _statusLabel = new AntdUI.Label() { Text = LanguageManager.Instance.GetText("Common.StatusLabel", "就绪") };
            _statusLabel.Dock = DockStyle.Fill;
            _statusStrip.Controls.Add(_statusLabel);
            Controls.Add(_statusStrip);
        }

        private void BuildLeftPanel(Control panel)
        {
            _leftTabCtl = new AntdUI.Tabs
            {
                Dock = DockStyle.Fill,
                Type = AntdUI.TabType.Card,
                Font = FontHelper.UiFont(10, FontStyle.Bold),
                TabMenuVisible = true,
                Style = new AntdUI.Tabs.StyleCard
                {
                    Gap = 4,
                    Border = 1,
                    BorderColor = ThemeManager.BorderColor,
                    BorderActive = AntdUI.Style.Db.Primary
                }
            };

            // ───── Tab 0: 文件树 ─────
            AntdUI.TabPage treeTab = new AntdUI.TabPage { Name = "Editor.FileTreeTab", Text = LanguageManager.Instance.GetText("Editor.FileTreeTab", "文件树"), Font = FontHelper.UiFont(9) };

            AntdUI.Panel outer = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent
            };

            // Row 2: folder tree (visible, folders only)
            _folderTree = new AntdUI.Tree
            {
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(9)
            };
            _folderTree.NodeMouseClick += OnFolderTreeNodeClick;
            _folderTree.NodeMouseDoubleClick += OnFolderTreeNodeDoubleClick;
            _folderTree.MouseClick += OnFolderTreeMouseClick;
            _folderTree.BeforeExpand += OnFolderTreeBeforeExpand;
            _folderTree.AfterExpand += (s, e) => UpdateFolderTreeScrollWidth();

            float dpiScale = DpiManager.Scale;
            int iconSize = (int)(16 * dpiScale);
            try
            {
                var imageList = TreeHelper.CreateSystemFolderIcons(iconSize);
                _closedFolderIcon = imageList.Images[0];
                _openFolderIcon = imageList.Images.Count > 1 ? imageList.Images[1] : imageList.Images[0];
                imageList.Dispose();
            }
            catch { }

            // Internal full file tree (hidden, used for all existing logic)
            _fileTree = new AntdUI.Tree
            {
                Dock = DockStyle.Fill,
                Checkable = false
            };
            _fileTree.NodeMouseClick += OnFileTreeNodeClick;
            _fileTree.NodeMouseDoubleClick += OnFileTreeNodeDoubleClick;
            _fileTree.BeforeExpand += OnFileTreeBeforeExpand;
            // AntdUI.Tree 自动处理绘制，无需 DrawNode

            // 文件夹树包裹 Panel，支持水平滚动条 + 文字超出隐藏
            _folderTreeScrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.Transparent
            };
            _folderTreeScrollPanel.Resize += OnFolderTreeScrollPanelResize;
            _folderTree.Dock = DockStyle.None;
            _folderTree.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _folderTreeScrollPanel.Controls.Add(_folderTree);

            // 平铺显示模式：仅包含"打开包"根节点的简易树
            _flatTree = new AntdUI.Tree
            {
                Dock = DockStyle.Fill,
                Visible = false,
                Font = FontHelper.UiFont(9)
            };
            _flatTree.NodeMouseClick += OnFlatTreeNodeClick;
            _flatTree.NodeMouseDoubleClick += OnFlatTreeNodeDoubleClick;

            // 显示模式切换按钮组（树型显示 / 平铺显示）
            var modePanel = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Back = Color.Transparent,
                InnerPadding = new Padding(0)
            };
            string treeModeText = LanguageManager.Instance.GetText("Editor.TreeViewMode", "树型显示");
            string flatModeText = LanguageManager.Instance.GetText("Editor.FlatViewMode", "平铺显示");
            _segFileTreeMode = new AntdUI.Segmented
            {
                Dock = DockStyle.None,
                Anchor = AnchorStyles.None,
                AutoSize = false,
                Font = FontHelper.UiFont(9),
                BackColor = ThemeManager.BgContainer,
                BackActive = ThemeManager.Primary,
                ForeActive = Color.White
            };
            _segFileTreeMode.Items.Add(new AntdUI.SegmentedItem
            {
                ID = "tree",
                Text = treeModeText
            });
            _segFileTreeMode.Items.Add(new AntdUI.SegmentedItem
            {
                ID = "flat",
                Text = flatModeText
            });
            int segW = TextRenderer.MeasureText(treeModeText, _segFileTreeMode.Font).Width
                + TextRenderer.MeasureText(flatModeText, _segFileTreeMode.Font).Width + 80;
            _segFileTreeMode.Size = new Size(segW, 26);
            _segFileTreeMode.SelectIndex = SettingsManager.EditorFlatViewMode ? 1 : 0;
            _segFileTreeMode.SelectIndexChanged += (s, e) =>
            {
                if (_syncingViewSeg) return;
                SwitchFileTreeViewMode(_segFileTreeMode.SelectIndex == 1);
            };
            modePanel.Controls.Add(_segFileTreeMode);
            modePanel.Resize += (s, e) =>
            {
                _segFileTreeMode.Location = new Point(
                    Math.Max(0, (modePanel.Width - _segFileTreeMode.Width) / 2),
                    Math.Max(0, (modePanel.Height - _segFileTreeMode.Height) / 2));
            };

            outer.Controls.Add(_folderTreeScrollPanel);
            outer.Controls.Add(_flatTree);
            outer.Controls.Add(modePanel);
            SwitchFileTreeViewMode(SettingsManager.EditorFlatViewMode, save: false);

            // Context menu
            _contextReload = new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("Editor.ReloadArchive", "重新加载")) { Tag = "reload" };
            _contextNewFolder = new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("Editor.NewFolder", "新建文件夹")) { Tag = "newFolder" };
            _contextNewFile = new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("Editor.NewFile", "新建文件")) { Tag = "newFile" };
            _contextImport = new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("Editor.ImportFile", "导入")) { Tag = "import" };
            _contextExport = new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("Editor.ExportFile", "导出")) { Tag = "export" };
            _contextDelete = new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("Editor.DeleteFile", "删除")) { Tag = "delete" };
            var addFavItem = new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("Editor.AddToFavorites", "添加到收藏夹")) { Tag = "addFavorite" };
            _contextMenuItems = new AntdUI.IContextMenuStripItem[] {
                _contextReload,
                new AntdUI.ContextMenuStripItemDivider(),
                _contextNewFolder,
                _contextNewFile,
                _contextImport,
                new AntdUI.ContextMenuStripItemDivider(),
                _contextExport,
                _contextDelete,
                new AntdUI.ContextMenuStripItemDivider(),
                addFavItem
            };

            treeTab.Controls.Add(outer);
            _leftTabCtl.Pages.Add(treeTab);

            // ───── Tab 1: 收藏夹 ─────
            // （搜索/替换入口已上移至窗口最顶部的全宽工具条，见 BuildTopToolbar —— plan_20260923_UI相关调整）
            AntdUI.TabPage favTab = new AntdUI.TabPage { Name = "Editor.FavoritesTab", Text = LanguageManager.Instance.GetText("Editor.FavoritesTab", "收藏夹"), Font = FontHelper.UiFont(9) };
            BuildFavoritesTab(favTab);
            _leftTabCtl.Pages.Add(favTab);

            RefreshFavoritesList();

            panel.Controls.Add(_leftTabCtl);
        }

        private void BuildFavoritesTab(AntdUI.TabPage tab)
        {
            _favoritesList = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = true,
                Bordered = true,
                MultipleRows = false,
                EditMode = AntdUI.TEditMode.DoubleClick,
                EnableHeaderResizing = true,
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill,
                LostFocusClearSelection = false
            };
            _favoritesList.Columns.Add(new AntdUI.Column("Note",
                LanguageManager.Instance.GetText("Common.Note", "备注")) { Width = "35%" });
            _favoritesList.Columns.Add(new AntdUI.Column("Path",
                LanguageManager.Instance.GetText("Common.Path", "路径")));
            _favoritesList.SelectIndexChanged += OnFavoritesListClick;

            _favoritesList.CellBeginEdit += (s, ea) =>
            {
                if (ea.RowIndex >= 1)
                {
                    var col = _favoritesList.Columns[ea.ColumnIndex];
                    if (col.Key != "Note") return false;
                }
                return true;
            };

            _favoritesList.CellEndEdit += (s, ea) =>
            {
                if (ea.RowIndex < 1 || ea.ColumnIndex < 0) return true;
                var col = _favoritesList.Columns[ea.ColumnIndex];
                if (col.Key != "Note" || ea.Value is not string newNote) return true;
                string key = _isArz ? "arz" : "arc";
                var favs = SettingsManager.GetFavorites(key);
                int idx = ea.RowIndex - 1;
                if (idx >= 0 && idx < favs.Count)
                {
                    favs[idx].Note = newNote;
                    SettingsManager.SetFavorites(key, favs);
                }
                return true;
            };

            var editNoteItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Editor.FavoriteEditNote", "修改备注")) { Tag = "editNote" };
            var removeItem = new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("Common.Remove", "移除")) { Tag = "remove" };
            _favoritesContextMenuItems = new AntdUI.IContextMenuStripItem[] { editNoteItem, removeItem };
            _favoritesList.CellClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                {
                    _favoritesList.SelectedIndex = e.RowIndex;
                    AntdUI.ContextMenuStrip.open(_favoritesList, it =>
                    {
                        string tag = it.Tag as string;
                        if (tag == "editNote") OnFavoritesEditNoteClick(it, EventArgs.Empty);
                        else if (tag == "remove") OnFavoritesRemoveClick(it, EventArgs.Empty);
                    }, _favoritesContextMenuItems);
                }
            };

            tab.Controls.Add(_favoritesList);
        }

        private void OnFavoritesListClick(object sender, EventArgs e)
        {
            if (_favoritesList.SelectedIndex < 1) return;
            var row = _favoritesList.GetRow(_favoritesList.SelectedIndex - 1)?.record as FavoriteRow;
            if (row == null) return;
            string path = row.Path;
            if (string.IsNullOrEmpty(path)) return;

            string notFoundMsg = LanguageManager.Instance.GetText("Editor.FavoritePathNotFound", "收藏的路径不存在：{0}");

            bool isFolder = path.EndsWith("/");
            if (isFolder)
            {
                string folderPath = path.TrimEnd('/');
                AntdUI.TreeItem rootNode = _folderTree.Items.Count > 0 ? _folderTree.Items[0] : null;
                if (rootNode == null) return;
                AntdUI.TreeItem node = FindNodeByPath(rootNode, folderPath);
                if (node == null)
                {
                    MessageBox.Show(string.Format(notFoundMsg, path),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "消息"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _folderTree.SelectItem = node;
                OnFolderTreeNodeClick(null, new AntdUI.TreeSelectEventArgs(
                    node, Rectangle.Empty, AntdUI.TreeCType.Item,
                    new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)));
            }
            else
            {
                bool exists = FindFileInTree(path);
                if (!exists)
                {
                    MessageBox.Show(string.Format(notFoundMsg, path),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "消息"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                SelectFileInTree(path);
            }
        }

        private void OnFavoritesEditNoteClick(object sender, EventArgs e)
        {
            if (_favoritesList.SelectedIndex < 1) return;
            var row = _favoritesList.GetRow(_favoritesList.SelectedIndex - 1)?.record as FavoriteRow;
            if (row == null) return;
            string path = row.Path;
            if (string.IsNullOrEmpty(path)) return;

            string key = _isArz ? "arz" : "arc";
            var favs = SettingsManager.GetFavorites(key);
            var entry = favs.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            var lblPathValue = new AntdUI.Label
            {
                Text = path,
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = ThemeManager.TextTertiary,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 10, 0, 0)
            };
            var txtNote = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Text = entry.Note,
                Height = 52,
                PrefixText = LanguageManager.Instance.GetText("Editor.FavoriteNotePrompt", "备注："),
                AllowClear = true
            };

            var grid = new AntdUI.GridPanel
            {
                Span = "fill;fill",
                Gap = 8,
                BackColor = Color.Transparent,
                Width = 420,
                Height = 120
            };
            grid.SetIndex(txtNote, 0);
            grid.SetIndex(lblPathValue, 1);
            grid.Controls.Add(lblPathValue);
            grid.Controls.Add(txtNote);

            AntdUI.Modal.Config config = null;
            config = new AntdUI.Modal.Config(this,
                LanguageManager.Instance.GetText("Editor.FavoriteEditNote", "修改备注"),
                grid)
            {
                Draggable = true
            };
            config.SetWidth(420);
            config.SetOk(LanguageManager.Instance.GetText("Common.OK", "确定"));
            config.SetCancel(LanguageManager.Instance.GetText("Common.Cancel", "取消"));
            config.OnOk = cfg =>
            {
                if (!string.IsNullOrWhiteSpace(txtNote.Text))
                {
                    entry.Note = txtNote.Text.Trim();
                    SettingsManager.SetFavorites(key, favs);
                    RefreshFavoritesList();
                }
                return true;
            };
            AntdUI.Modal.open(config);
        }

        private void OnFavoritesRemoveClick(object sender, EventArgs e)
        {
            if (_favoritesList.SelectedIndex < 1) return;
            var row = _favoritesList.GetRow(_favoritesList.SelectedIndex - 1)?.record as FavoriteRow;
            if (row == null) return;
            string path = row.Path;
            if (!string.IsNullOrEmpty(path))
            {
                string key = _isArz ? "arz" : "arc";
                var favs = SettingsManager.GetFavorites(key);
                favs.RemoveAll(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                SettingsManager.SetFavorites(key, favs);
                RefreshFavoritesList();
            }
        }

        private void OnContextAddToFavorites(object sender, EventArgs e)
        {
            string path = !string.IsNullOrEmpty(_contextMenuPath)
                ? _contextMenuPath
                : (_contextMenuNode != null ? GetNodePath(_contextMenuNode) : _currentFileRelPath);
            if (!string.IsNullOrEmpty(path) && _contextMenuNode?.Tag is NodeData nd && nd.IsDirectory && !path.EndsWith("/"))
                path += "/";
            ShowAddToFavoritesDialog(path);
        }

        private void OnFileListAddToFavorites(object sender, EventArgs e)
        {
            string path = null;
            if (_fileListContextData?.Tag is NodeData nd)
                path = GetFileRowPath(_fileListContextData) ?? GetFileListFullPath(nd);
            if (string.IsNullOrEmpty(path))
                path = _currentFileRelPath;
            ShowAddToFavoritesDialog(path);
        }

        private void ShowAddToFavoritesDialog(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            string key = _isArz ? "arz" : "arc";
            var favs = SettingsManager.GetFavorites(key);
            if (favs.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(LanguageManager.Instance.GetText("Editor.FavoriteAlreadyExists", "该文件已在收藏夹中"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "消息"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var lblPathValue = new AntdUI.Label
            {
                Text = path,
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = ThemeManager.TextTertiary,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 10, 0, 0)
            };
            var txtNote = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Text = Path.GetFileName(path.TrimEnd('/')),
                Height = 52,
                PrefixText = LanguageManager.Instance.GetText("Editor.FavoriteNotePrompt", "备注："),
                AllowClear = true
            };

            var grid = new AntdUI.GridPanel
            {
                Span = "fill;fill",
                Gap = 8,
                BackColor = Color.Transparent,
                Width = 420,
                Height = 120
            };
            grid.SetIndex(txtNote, 0);
            grid.SetIndex(lblPathValue, 1);
            grid.Controls.Add(lblPathValue);
            grid.Controls.Add(txtNote);

            AntdUI.Modal.Config config = null;
            config = new AntdUI.Modal.Config(this,
                LanguageManager.Instance.GetText("Editor.AddToFavorites", "添加到收藏夹"),
                grid)
            {
                Draggable = true
            };
            config.SetWidth(420);
            config.SetOk(LanguageManager.Instance.GetText("Common.OK", "确定"));
            config.SetCancel(LanguageManager.Instance.GetText("Common.Cancel", "取消"));
            config.OnOk = cfg =>
            {
                if (!string.IsNullOrWhiteSpace(txtNote.Text))
                {
                    favs.Add(new FavoriteEntry { Path = path, Note = txtNote.Text.Trim() });
                    SettingsManager.SetFavorites(key, favs);
                    RefreshFavoritesList();
                }
                return true;
            };
            AntdUI.Modal.open(config);
        }

        private void RefreshFavoritesList()
        {
            string key = _isArz ? "arz" : "arc";
            var favs = SettingsManager.GetFavorites(key);
            var rows = new List<FavoriteRow>();
            foreach (var f in favs)
            {
                rows.Add(new FavoriteRow
                {
                    Note = f.Note,
                    Path = f.Path
                });
            }
            _favoritesList.DataSource = rows;
            UpdateFavoriteButtonState();
        }

        private void UpdateFavoriteButtonState()
        {
            if (_currentFilePathLabel == null || _favoriteButton == null) return;
            if (string.IsNullOrEmpty(_currentFileRelPath) || string.IsNullOrEmpty(_currentFilePathLabel.Text))
            {
                _favoriteButton.Visible = false;
                return;
            }
            string key = _isArz ? "arz" : "arc";
            var favs = SettingsManager.GetFavorites(key);
            bool isFavorited = favs.Any(f => f.Path.Equals(_currentFileRelPath, StringComparison.OrdinalIgnoreCase));
            if (isFavorited)
            {
                _favoriteButton.Text = LanguageManager.Instance.GetText("Editor.FavoriteAdded", "已收藏");
                _favoriteButton.Enabled = false;
                _favoriteButton.Cursor = Cursors.Default;
            }
            else
            {
                _favoriteButton.Text = LanguageManager.Instance.GetText("Editor.FavoriteAdd", "收藏");
                _favoriteButton.Enabled = true;
                _favoriteButton.Cursor = Cursors.Hand;
            }
        }

        // ──────── 搜索标签页 ────────
        // ───────────────── 顶部全宽工具条（历史/打开 + 统一搜索/替换入口，plan_20260923_UI相关调整）─────────────────

        /// <summary>构建窗口最顶部的全宽工具条：左「历史、打开」+ 统一搜索/替换入口 + 右「动作按钮」+ 进度/状态标签</summary>
        private void BuildTopToolbar()
        {
            // 行内所有控件取同一实际高度（DPI 缩放后的像素），容器再加上下留白
            int rowH = DpiManager.S(TopBarCtrlHeight);

            _topBar = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = rowH + DpiManager.S(TopBarRowPad) * 2,
                Back = Color.Transparent
            };
            // 底部边框：顶部通栏与下方编辑区之间给出明确分界
            // （自绘而非加一层容器：不参与 Dock 排版、不占高度；颜色每次绘制都读当前主题，明暗/主色改动自动跟随）
            _topBar.Paint += (s, e) =>
            {
                int y = _topBar.ClientSize.Height - 1;
                if (y < 0) return;
                using (var pen = new Pen(ThemeManager.BorderColor, Math.Max(1, DpiManager.S(1))))
                    e.Graphics.DrawLine(pen, 0, y, _topBar.ClientSize.Width, y);
            };

            _topHistoryBtn = ButtonFactory.Create(
                LanguageManager.Instance.GetText("Editor.History", "历史"), name: "Editor.History");
            _topHistoryBtn.Click += OnHistoryClick;

            _topOpenBtn = ButtonFactory.Create(
                LanguageManager.Instance.GetText("Editor.OpenFile", "打开"),
                AntdUI.TTypeMini.Primary, name: "Editor.OpenFile");
            _topOpenBtn.Click += (s, e) => OnOpenArc(null, EventArgs.Empty);

            // 按钮宽度仍由内容撑起（AutoSize），只给高度的**下限**保证行内等高且不塌陷
            // （AutoSize 会重算 Height，设 Height 无效；MinimumSize 才会被 WinForms 强制保底）
            foreach (var b in new[] { _topHistoryBtn, _topOpenBtn })
                b.MinimumSize = new Size(0, rowH);

            // 功能切换下拉：全局搜索 / 内容替换（计划第 6 条）
            var topUiFont = FontHelper.UiFont(9);
            string globalSearchText = LanguageManager.Instance.GetText("Editor.FuncGlobalSearch", "全局搜索");
            string contentReplaceText = LanguageManager.Instance.GetText("Editor.FuncContentReplace", "内容替换");
            _topFuncCombo = new AntdUI.Dropdown
            {
                Width = DpiManager.S(120),
                Height = rowH,          // 下拉无 AutoSize：不设高度则为 0（实测 AntdUI 2.4.2），表现为"控件不显示"
                ShowArrow = true,
                Font = topUiFont,
                Text = globalSearchText
            };
            _topFuncCombo.Items.AddRange(new object[] {
                new AntdUI.SelectItem(globalSearchText) { Tag = "search" },
                new AntdUI.SelectItem(contentReplaceText) { Tag = "replace" }
            });
            _topFuncCombo.SelectedValue = "search";
            // 下拉项与按钮是同级内容：显式给同一字号（层级窗体自身字号偏小时由此统一）
            _topFuncCombo.DrawItem += (s, e) => e.Font = topUiFont;
            _topFuncCombo.SelectedValueChanged += (s, e) =>
            {
                // e.Value 可能是 Tag/文本：按两者双重匹配（防 AntdUI 版本差异导致切换失效）
                var val = e.Value?.ToString();
                if (val == "replace" || val == contentReplaceText) _topFuncMode = "replace";
                else if (val == "search" || val == globalSearchText) _topFuncMode = "search";
                ApplyTopFuncComboText();   // Dropdown 不会像 Select 那样自动回填选中项文本，需手动同步
                UpdateTopBarModeVisibilities();
                LayoutTopToolbar();
            };

            // 查找内容输入框（前缀"搜索"；替换模式下仍可输入并点"搜索"——计划第 9 条）
            _topSearchBox = new AntdUI.Input
            {
                Name = "Editor.FuncSearch",
                PrefixText = LanguageManager.Instance.GetText("Editor.FuncSearch", "搜索") + ":",
                Width = DpiManager.S(280),
                Height = rowH,
                Font = topUiFont,
                AllowClear = true
            };
            _topSearchBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) StartSearch(); };

            // 替换为输入框（动态显示，仅替换模式；替换只针对文件内容，文件名不支持替换）
            _topReplaceBox = new AntdUI.Input
            {
                Name = "Editor.ReplaceToHint",
                PrefixText = LanguageManager.Instance.GetText("Editor.ReplaceToHint", "替换为") + ":",
                Width = DpiManager.S(280),
                Height = rowH,
                Font = topUiFont,
                AllowClear = true,
                Visible = false
            };
            _topReplaceBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && _topFuncMode == "replace") ExecuteReplace(); };

            // 范围单选：文件名（默认）/ 文件内容；只作用于搜索；替换模式下整体隐藏，后面的控件自动左移贴合
            _topScopeContentRadio = new AntdUI.Radio
            {
                Name = "Editor.SearchModeContent", Text = LanguageManager.Instance.GetText("Editor.SearchModeContent", "文件内容"),
                AutoSize = true, Font = topUiFont
            };
            _topScopeFileRadio = new AntdUI.Radio
            {
                Name = "Editor.SearchModeFileName", Text = LanguageManager.Instance.GetText("Editor.SearchModeFileName", "文件名"),
                AutoSize = true, Font = topUiFont, Checked = true   // 默认：文件名
            };
            _topScopeContentRadio.CheckedChanged += (s, e) =>
            {
                if (_topScopeContentRadio.Checked) { _topScopeFileRadio.Checked = false; _searchMode = "content"; }
            };
            _topScopeFileRadio.CheckedChanged += (s, e) =>
            {
                if (_topScopeFileRadio.Checked) { _topScopeContentRadio.Checked = false; _searchMode = "filename"; }
            };

            // 区分大小写（搜索与替换共用）
            _topCaseSensitive = new AntdUI.Checkbox
            {
                Name = "Editor.SearchCaseSensitive", Text = LanguageManager.Instance.GetText("Editor.SearchCaseSensitive", "区分大小写"),
                AutoSize = true, Font = FontHelper.UiFont(9)
            };

            // 动作按钮：紧跟各自输入框（搜索钮跟搜索框、替换钮跟替换为框）；执行中按钮上转 SPIN 表示进度
            _topSearchBtn = ButtonFactory.Create(
                LanguageManager.Instance.GetText("Editor.FuncSearch", "搜索"),
                AntdUI.TTypeMini.Primary, name: "Common.SearchLabel");
            _topSearchBtn.MinimumSize = new Size(0, rowH);     // 同上：宽度由内容撑起，高度保底等高
            _topSearchBtn.Click += (s, e) =>
            {
                if (_topAction == 1) { _topActionCts?.Cancel(); return; }   // 搜索执行中 → 取消搜索
                if (_topAction != 0) return;                                // 其它动作执行中
                StartSearch();
            };
            _topReplaceBtn = ButtonFactory.Create(
                LanguageManager.Instance.GetText("Editor.FuncReplace", "替换"), name: "Editor.ReplaceAll");
            _topReplaceBtn.MinimumSize = new Size(0, rowH);     // 同上：宽度由内容撑起，高度保底等高
            _topReplaceBtn.Visible = false;   // 动态显示：仅替换模式（计划第 11 条）
            _topReplaceBtn.Click += (s, e) =>
            {
                if (_topAction == 3) { _topActionCts?.Cancel(); return; }   // 替换执行中 → 取消替换（已完成的不回滚）
                if (_topAction != 0) return;                                // 其它动作执行中
                ExecuteReplace();
            };

            // 取消按钮：仅在搜索/替换执行中显示，紧贴当前执行的那个动作按钮右侧
            _topCancelBtn = ButtonFactory.Create(
                LanguageManager.Instance.GetText("Common.Cancel", "取消"), name: "Common.Cancel");
            _topCancelBtn.MinimumSize = new Size(0, rowH);
            _topCancelBtn.Visible = false;
            _topCancelBtn.Click += (s, e) => _topActionCts?.Cancel();

            // 按钮宽度会随 SPIN 的出现/文案变化自动撑宽（AutoSize）→ 尺寸变了要重排，否则右侧控件被压住
            _topSearchBtn.SizeChanged += (s, e) => LayoutTopToolbar();
            _topReplaceBtn.SizeChanged += (s, e) => LayoutTopToolbar();

            // 进度/状态标签（填充剩余宽度）：执行中显示 (已搜索/总数) XX% 当前路径；结束后显示结果
            _searchStatusLabel = new AntdUI.Label
            {
                Name = "Editor.WaitSearch", Text = LanguageManager.Instance.GetText("Editor.WaitSearch", "等待搜索..."),
                AutoSize = true, Font = FontHelper.UiFont(9), ForeColor = ThemeManager.TextTertiary
            };

            _topBar.Controls.Add(_topHistoryBtn);
            _topBar.Controls.Add(_topOpenBtn);
            _topBar.Controls.Add(_topFuncCombo);
            _topBar.Controls.Add(_topSearchBox);
            _topBar.Controls.Add(_topSearchBtn);
            _topBar.Controls.Add(_topReplaceBox);
            _topBar.Controls.Add(_topReplaceBtn);
            _topBar.Controls.Add(_topCancelBtn);
            _topBar.Controls.Add(_topScopeFileRadio);
            _topBar.Controls.Add(_topScopeContentRadio);
            _topBar.Controls.Add(_topCaseSensitive);
            _topBar.Controls.Add(_searchStatusLabel);

            _topBar.Resize += (s, e) => LayoutTopToolbar();
            LayoutTopToolbar();
        }

        /// <summary>顶部工具条布局：从左到右顺序定位（历史→打开→功能→搜索框→替换框→范围→大小写），右端为动作按钮，状态标签吃剩余宽度</summary>
        private void LayoutTopToolbar()
        {
            if (_topBar == null || _topBar.IsDisposed) return;
            if (_topHistoryBtn == null || _topSearchBox == null || _topSearchBtn == null || _topCancelBtn == null) return;

            int h = _topBar.ClientSize.Height;
            int w = _topBar.ClientSize.Width;
            if (h <= 0 || w <= 0) return;

            int pad = DpiManager.S(4);
            int gap = DpiManager.S(8);

            int x = pad;
            _topHistoryBtn.Location = new Point(x, LogLevelFilter.CenterY(_topHistoryBtn, h));
            x += _topHistoryBtn.Width + gap;
            _topOpenBtn.Location = new Point(x, LogLevelFilter.CenterY(_topOpenBtn, h));
            x += _topOpenBtn.Width + gap;

            // 功能下拉（搜索/替换切换）
            _topFuncCombo.Location = new Point(x, LogLevelFilter.CenterY(_topFuncCombo, h));
            x += _topFuncCombo.Width + gap;

            // 搜索框 + 紧随其后的搜索按钮
            _topSearchBox.Location = new Point(x, LogLevelFilter.CenterY(_topSearchBox, h));
            x += _topSearchBox.Width + gap / 2;
            _topSearchBtn.Location = new Point(x, LogLevelFilter.CenterY(_topSearchBtn, h));
            x += _topSearchBtn.Width + gap;

            // 替换为框 + 紧随其后的替换按钮：**动态显示**，仅替换模式出现（其余控件自然右移——计划第 11 条）
            if (_topFuncMode == "replace")
            {
                _topReplaceBox.Location = new Point(x, LogLevelFilter.CenterY(_topReplaceBox, h));
                x += _topReplaceBox.Width + gap / 2;
                _topReplaceBtn.Location = new Point(x, LogLevelFilter.CenterY(_topReplaceBtn, h));
                x += _topReplaceBtn.Width + gap;
            }

            // 取消按钮：执行中紧贴当前动作按钮（搜索模式跟搜索钮、替换模式跟替换钮）
            if (_topCancelBtn.Visible)
            {
                _topCancelBtn.Location = new Point(x, LogLevelFilter.CenterY(_topCancelBtn, h));
                x += _topCancelBtn.Width + gap;
            }

            // 范围单选（只作用于搜索）+ 区分大小写：**隐藏的不占位置**
            // （替换模式下单选整体隐藏 → 后面的控件自动左移贴合，不留空档）
            if (_topScopeFileRadio.Visible)
            {
                _topScopeFileRadio.Location = new Point(x, LogLevelFilter.CenterY(_topScopeFileRadio, h));
                x += _topScopeFileRadio.Width + gap / 2;
            }
            if (_topScopeContentRadio.Visible)
            {
                _topScopeContentRadio.Location = new Point(x, LogLevelFilter.CenterY(_topScopeContentRadio, h));
                x += _topScopeContentRadio.Width + gap;
            }
            _topCaseSensitive.Location = new Point(x, LogLevelFilter.CenterY(_topCaseSensitive, h));
            int labelX = x + _topCaseSensitive.Width + gap;

            // 状态/进度标签：吃掉剩余宽度
            int labelW = Math.Max(DpiManager.S(80), w - pad - labelX);
            _searchStatusLabel.Location = new Point(labelX, LogLevelFilter.CenterY(_searchStatusLabel, h));
            if (_searchStatusLabel.Width != labelW) _searchStatusLabel.Width = labelW;
        }

        /// <summary>
        /// 模式切换时的显隐：替换模式显示"替换为"输入框与替换按钮、隐藏范围单选（替换只针对内容）。
        /// 模式从下拉的 **SelectedValue 推导**（编程式赋值不触发事件，也能正确生效）。
        /// </summary>
        private void UpdateTopBarModeVisibilities()
        {
            var sel = _topFuncCombo?.SelectedValue?.ToString();
            if (sel == "search" || sel == "replace") _topFuncMode = sel;
            bool replaceMode = _topFuncMode == "replace";
            _topReplaceBox.Visible = replaceMode;
            _topReplaceBtn.Visible = replaceMode;
            _topScopeFileRadio.Visible = !replaceMode;
            _topScopeContentRadio.Visible = !replaceMode;
        }

        /// <summary>
        /// 顶部动作状态机：0=空闲；1=搜索；3=替换（查找+替换一体）。
        /// 执行中：**正在执行的那个按钮**转 SPIN（文案保持功能名），并在其右侧显示独立「取消」按钮。
        /// </summary>
        private void SetTopActionRunning(int action)
        {
            _topAction = action;
            bool running = action != 0;

            // 文案固定为各自功能名（不再改写成"取消"）：进度交给按钮上的 SPIN，取消交给独立按钮
            _topSearchBtn.Text = LanguageManager.Instance.GetText("Editor.FuncSearch", "搜索");
            _topReplaceBtn.Text = LanguageManager.Instance.GetText("Editor.FuncReplace", "替换");
            _topSearchBtn.Loading = action == 1;
            _topReplaceBtn.Loading = action == 3;

            _topSearchBtn.Enabled = !running || action == 1;
            _topReplaceBtn.Enabled = !running || action == 3;
            _topReplaceBtn.Visible = _topFuncMode == "replace";
            _topCancelBtn.Visible = running;

            LayoutTopToolbar();   // 取消按钮显隐 + 按钮因 SPIN 撑宽后都要重排
        }

        /// <summary>
        /// 下拉按钮自身的显示文本：Dropdown 不像 Select 那样自动回填选中项，选中后必须手动同步
        /// （否则就是上一轮反馈的"切到替换，按钮文字不变"）。
        /// </summary>
        private void ApplyTopFuncComboText()
        {
            if (_topFuncCombo == null || _topFuncCombo.IsDisposed) return;
            _topFuncCombo.Text = _topFuncMode == "replace"
                ? LanguageManager.Instance.GetText("Editor.FuncContentReplace", "内容替换")
                : LanguageManager.Instance.GetText("Editor.FuncGlobalSearch", "全局搜索");
        }

        private async void StartSearch()
        {
            string query = _topSearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query))
                return;
            // 范围以**控件当前状态**为准（AntdUI.Radio 编程式赋值不触发事件，读状态最可靠）；
            // 替换模式下单选整体隐藏（替换只针对内容），此时一律按"文件内容"搜索
            _searchMode = _topFuncMode == "replace" ? "content" : (_topScopeFileRadio.Checked ? "filename" : "content");
            bool searchDone = false;

            bool hasArzReader = _isArz && _arzReader != null && _arzReader.Count > 0;
            bool hasArcFile = !_isArz && _arcFile != null && _arcFile.toc.Count > 0;
            if (!hasArzReader && !hasArcFile)
            {
                _searchStatusLabel.Text = LanguageManager.Instance.GetText("Editor.SearchNoFileLoaded", "请先打开文件");
                return;
            }

            _topActionCts?.Cancel();
            _topActionCts = new CancellationTokenSource();
            var token = _topActionCts.Token;

            EnterSearchFileListView();
            SetTopActionRunning(1);

            string filterTextCapture = _fileListFilterTextBox?.Text?.Trim() ?? "";
            int foundCount = 0;
            bool isCancelled = false;
            string searchError = null;

            // 进度直接显示在顶部工具条的状态标签上：(已扫描/总数) 百分比 当前路径
            var progress = new Progress<(int current, int total, string path)>(p =>
            {
                if (searchDone) return;   // 已出最终状态：迟到的进度不覆盖
                int pct = p.total > 0 ? p.current * 100 / p.total : 0;
                _searchStatusLabel.Text = string.Format(
                    LanguageManager.Instance.GetText("Editor.SearchLiveCountPath", "({0}/{1}) {2}% {3}"),
                    p.current, p.total, pct, p.path ?? "");
            });
            var pendingRows = new List<FileListGridRow>();
            bool flushAll = false;
            void FlushSearchRows()
            {
                var list = _searchViewRows;
                if (list == null || pendingRows.Count == 0) return;
                var arr = pendingRows.ToArray();
                pendingRows.Clear();
                list.AddRange(arr);
            }
            var rowProgress = new Progress<FileListGridRow>(row =>
            {
                if (_searchViewRows == null) return;
                pendingRows.Add(row);
                foundCount++;
                if (foundCount <= 100 || pendingRows.Count >= 100 || flushAll) FlushSearchRows();
            });

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                bool caseSensitive = _topCaseSensitive.Checked;
                StringComparison comparison = caseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                await Task.Run(() =>
                {
                    try
                    {
                        if (_searchMode == "filename")
                        {
                            _ = SearchArchiveNames(query, comparison, token, progress,
                                onMatch: hit =>
                                {
                                    var (name, fileName) = hit;
                                    lock (_searchViewHitsLock) _searchViewHits.Add((name, fileName, 1, -1));
                                    BuildAndReportSearchRow(name, filterTextCapture, token, rowProgress);
                                });
                        }
                        else
                        {
                            _ = SearchArchiveContent(query, comparison, token, progress,
                                onMatch: hit =>
                                {
                                    lock (_searchViewHitsLock) _searchViewHits.Add(hit);
                                    BuildAndReportSearchRow(hit.path, filterTextCapture, token, rowProgress);
                                });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        isCancelled = true;
                    }
                    catch (Exception ex2)
                    {
                        searchError = ex2.Message;
                    }
                });

                flushAll = true;
                FlushSearchRows();

                searchDone = true;   // 迟到的进度回调不再覆盖最终状态
                if (isCancelled)
                {
                    _searchStatusLabel.Text = foundCount > 0
                        ? string.Format(LanguageManager.Instance.GetText("Editor.SearchCancelledWithCount", "已取消，已找到 {0} 个"), foundCount)
                        : LanguageManager.Instance.GetText("Editor.SearchCancelled", "搜索已取消");
                }
                else if (searchError != null)
                {
                    _searchStatusLabel.Text = $"{LanguageManager.Instance.GetText("Common.Error", "错误")}: {searchError}";
                    LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.SearchError", "搜索异常: {0}"), searchError), "ERROR");
                }
                else
                {
                    _searchStatusLabel.Text = foundCount > 0
                        ? string.Format(LanguageManager.Instance.GetText("Editor.SearchFoundCount", "找到 {0} 个匹配"), foundCount)
                        : LanguageManager.Instance.GetText("Editor.SearchNoResults", "未找到匹配");
                }
            }
            catch (Exception ex)
            {
                _searchStatusLabel.Text = $"{LanguageManager.Instance.GetText("Common.Error", "错误")}: {ex.Message}";
                LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.SearchError", "搜索异常: {0}"), ex.Message), "ERROR");
            }
            finally
            {
                sw.Stop();
                SetTopActionRunning(0);
            }
        }

        private List<(string path, string fileName)> SearchArchiveNames(
            string query, StringComparison comparison, CancellationToken token,
            IProgress<(int current, int total, string path)> progress = null,
            Action<(string path, string fileName)> onMatch = null)
        {
            var results = new List<(string path, string fileName)>();

            if (_isArz && _arzReader != null)
            {
                int total = _arzReader.Count;
                int reportInterval = Math.Max(1, total / 100);
                for (int i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    string name = _arzReader.GetRecordName(i);
                    if (i % reportInterval == 0)
                        progress?.Report((i, total, name));
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.IndexOf(query, comparison) >= 0)
                    {
                        var hit = (name, Path.GetFileName(name));
                        results.Add(hit);
                        onMatch?.Invoke(hit);
                    }
                }
                progress?.Report((total, total, null));
            }
            else if (!_isArz && _arcFile != null)
            {
                int total = _arcFile.toc.Count;
                int reportInterval = Math.Max(1, total / 100);
                for (int i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = _arcFile.toc[i];
                    string name = entry.GetEntryString(_arcFile.strs);
                    if (i % reportInterval == 0)
                        progress?.Report((i, total, name));
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.IndexOf(query, comparison) >= 0)
                    {
                        var hit = (name, Path.GetFileName(name));
                        results.Add(hit);
                        onMatch?.Invoke(hit);
                    }
                }
                progress?.Report((total, total, null));
            }

            return results;
        }

        private List<(string path, string fileName, int matchCount, int recordIndex)> SearchArchiveContent(
            string query, StringComparison comparison, CancellationToken token,
            IProgress<(int current, int total, string path)> progress = null,
            Action<(string path, string fileName, int matchCount, int recordIndex)> onMatch = null)
        {
            var results = new List<(string path, string fileName, int matchCount, int recordIndex)>();
            void AddHit(string name, int cnt, int i)
            {
                var hit = (name, Path.GetFileName(name), cnt, i);
                results.Add(hit);
                onMatch?.Invoke(hit);
            }

            if (_isArz && _arzReader != null)
            {
                int total = _arzReader.Count;
                int reportInterval = Math.Max(1, total / 100);
                using (var fs = new FileStream(_archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    for (int i = 0; i < total; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        string name = _arzReader.GetRecordName(i);
                        if (i % reportInterval == 0)
                            progress?.Report((i, total, name));
                        if (string.IsNullOrEmpty(name)) continue;
                        string ext = Path.GetExtension(name);
                        if (!IsSearchableTextFile(ext)) continue;

                        string content = ReadContentByIndex(i, fs);
                        if (string.IsNullOrEmpty(content)) continue;

                        int cnt = 0, pos = 0;
                        while ((pos = content.IndexOf(query, pos, comparison)) >= 0)
                        {
                            cnt++;
                            pos += query.Length;
                        }
                        if (cnt > 0)
                            AddHit(name, cnt, i);
                    }
                }
                progress?.Report((total, total, null));
            }
            else if (!_isArz && _arcFile != null)
            {
                int total = _arcFile.toc.Count;
                int reportInterval = Math.Max(1, total / 100);
                for (int i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = _arcFile.toc[i];
                    string name = entry.GetEntryString(_arcFile.strs);
                    if (i % reportInterval == 0)
                        progress?.Report((i, total, name));
                    if (string.IsNullOrEmpty(name)) continue;
                    string ext = Path.GetExtension(name);
                    if (!IsSearchableTextFile(ext)) continue;

                    string content = ReadContentByIndex(i);
                    if (string.IsNullOrEmpty(content)) continue;

                    int cnt = 0, pos = 0;
                    while ((pos = content.IndexOf(query, pos, comparison)) >= 0)
                    {
                        cnt++;
                        pos += query.Length;
                    }
                    if (cnt > 0)
                        AddHit(name, cnt, i);
                }
                progress?.Report((total, total, null));
            }

            return results;
        }

        private string ReadContentByIndex(int index, Stream arzStream = null)
        {
            try
            {
                if (_isArz && _arzReader != null)
                {
                    var record = arzStream != null
                        ? _arzReader.GetRecord(index, arzStream)
                        : _arzReader.GetRecord(index);
                    try
                    {
                        using (var ms = new MemoryStream())
                        {
                            record.SaveToStream(ms);
                            ms.Position = 0;
                            using (var sr = new StreamReader(ms, Utf8NoBom))
                                return sr.ReadToEnd();
                        }
                    }
                    finally
                    {
                        record?.DiscardData();
                    }
                }
                else if (!_isArz && _arcFile != null)
                {
                    var entry = _arcFile.toc[index];
                    using (var ms = new MemoryStream())
                    {
                        _arcFile.UnpackToStream(entry, ms);
                        return TemplateHelper.DecodeText(ms.ToArray());
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>可搜索/可编辑的文本类扩展名（收敛到公共白名单 TextFileHelper.TextExtensions）</summary>
        private static bool IsSearchableTextFile(string ext)
            => TextFileHelper.IsTextFileExtension(ext);

        private async void ExecuteReplace()
        {
            string searchText = _topSearchBox.Text.Trim();
            string replaceText = _topReplaceBox.Text;
            if (string.IsNullOrEmpty(searchText))
                return;

            bool caseSensitive = _topCaseSensitive.Checked;
            StringComparison comparison = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            _topActionCts?.Cancel();
            _topActionCts = new CancellationTokenSource();
            var token = _topActionCts.Token;
            SetTopActionRunning(3);
            _searchStatusLabel.Text = LanguageManager.Instance.GetText("Editor.SearchInProgress", "搜索中...");

            int totalRecords = _isArz ? (_arzReader?.Count ?? 0) : (_arcFile?.toc.Count ?? 0);
            var progress = new Progress<(int current, int total, string path)>(p =>
            {
                int pct = p.total > 0 ? p.current * 100 / p.total : 0;
                _searchStatusLabel.Text = string.Format(
                    LanguageManager.Instance.GetText("Editor.SearchLiveCountPath", "({0}/{1}) {2}% {3}"),
                    p.current, p.total, pct, p.path ?? "");
            });

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var searchResults = await Task.Run(() => SearchArchiveContent(searchText, comparison, token, progress));

                if (searchResults.Count == 0)
                {
                    _searchStatusLabel.Text = LanguageManager.Instance.GetText("Editor.SearchNoResults", "未找到匹配");
                    return;
                }

                string msg = string.Format(
                    LanguageManager.Instance.GetText("Editor.ReplaceConfirm", "确定要将 {0} 个文件中的 \"{1}\" 替换为 \"{2}\" 吗？"),
                    searchResults.Count, searchText, replaceText);
                if (MessageBox.Show(msg, LanguageManager.Instance.GetText("Common.Confirm", "确认"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    _searchStatusLabel.Text = LanguageManager.Instance.GetText("Common.Cancelled", "已取消");
                    return;
                }

                _searchStatusLabel.Text = LanguageManager.Instance.GetText("Editor.ReplaceProgress", "替换中...");

                var changedPaths = new List<string>();
                var replaceResult = await Task.Run(() =>
                {
                    int totalReplaced = 0;
                    int totalFiles = 0;

                    foreach (var (path, _, count, recordIndex) in searchResults)
                    {
                        token.ThrowIfCancellationRequested();   // 逐文件检查取消（已完成的替换保留，不回滚）
                        int c = ReplaceFileContentByIndex(path, recordIndex, searchText, replaceText, comparison);
                        if (c > 0)
                        {
                            totalReplaced += c;
                            totalFiles++;
                            changedPaths.Add(path);
                        }
                    }
                    return (totalFiles, totalReplaced);
                });

                // 后台线程只负责读写临时文件，所有状态/UI 更新回到 UI 线程执行，
                // 避免与已修改列表点击等操作并发访问共享集合导致 "Collection was modified" 异常
                if (changedPaths.Count > 0)
                {
                    _fileTree.PauseLayout = true;
                    try
                    {
                        foreach (string path in changedPaths)
                            MarkFileModified(path, immediateRefresh: false);
                    }
                    finally
                    {
                        _fileTree.PauseLayout = false;
                    }
                    RefreshAllAfterChange(rebuildFolderTree: false);
                    foreach (string path in changedPaths)
                        RefreshFileTreeNodeColor(path);
                }

                sw.Stop();

                _searchStatusLabel.Text = string.Format(
                    LanguageManager.Instance.GetText("Editor.ReplaceComplete", "替换完成: 处理 {0} 个文件, 共替换 {1} 处"),
                    replaceResult.totalFiles, replaceResult.totalReplaced);

                LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.ReplaceLog", "替换: \"{0}\" → \"{1}\", 处理 {2} 个文件, 共替换 {3} 处, 耗时: {4}"), searchText, replaceText, replaceResult.totalFiles, replaceResult.totalReplaced, FormatDuration(sw.Elapsed)), "INFO");
            }
            catch (OperationCanceledException)
            {
                // 用户取消：已完成的替换保留（不回滚），状态栏说明
                _searchStatusLabel.Text = LanguageManager.Instance.GetText("Editor.ReplaceCancelledKeep", "已取消（已完成的替换保留）");
                LogOutput(LanguageManager.Instance.GetText("Editor.ReplaceCancelledKeep", "已取消（已完成的替换保留）"), "WARN");
            }
            catch (Exception ex)
            {
                _searchStatusLabel.Text = $"{LanguageManager.Instance.GetText("Common.Error", "错误")}: {ex.Message}";
                LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.ReplaceException", "替换异常: {0}"), ex.Message), "ERROR");
            }
            finally
            {
                SetTopActionRunning(0);
            }
        }

        private int ReplaceFileContentByIndex(string path, int recordIndex, string searchText, string replaceText, StringComparison comparison)
        {
            string content = ReadContentByIndex(recordIndex);
            if (string.IsNullOrEmpty(content)) return 0;

            int count = 0;
            int idx = 0;
            while ((idx = content.IndexOf(searchText, idx, comparison)) >= 0)
            {
                count++;
                idx += searchText.Length;
            }
            if (count == 0) return 0;

            string newContent = ReplaceStringWithComparison(content, searchText, replaceText, comparison);

            string tempFile = _session.GetTempFilePath(path);
            File.WriteAllText(tempFile, newContent, EncodingService.TempWriteEncoding);

            return count;
        }

        private NodeData GetNodeDataByPath(string path)
        {
            if (_fileNodeMap.TryGetValue(path, out var node))
                return node.Tag as NodeData;
            return null;
        }

        private static string ReplaceStringWithComparison(string text, string search, string replace, StringComparison comparison)
        {
            var sb = new System.Text.StringBuilder();
            int pos = 0;
            while (true)
            {
                int idx = text.IndexOf(search, pos, comparison);
                if (idx < 0)
                {
                    sb.Append(text.Substring(pos));
                    break;
                }
                sb.Append(text.Substring(pos, idx - pos));
                sb.Append(replace);
                pos = idx + search.Length;
            }
            return sb.ToString();
        }

        private void BuildCenterPanel(Control panel)
        {
            _editorPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0),
                Back = Color.Transparent
            };

            _currentFilePathLabel = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Font = FontHelper.MonoFont(9f),
                Text = "",
                Visible = false,
                TabStop = false
            };

            _favoriteButton = new AntdUI.Button
            {
                Dock = DockStyle.Left,
                Text = LanguageManager.Instance.GetText("Editor.FavoriteAdd", "收藏"),
                Visible = false,
                AutoSize = true,
                MinimumSize = new Size(60, 28),
                Font = FontHelper.UiFont(9)
            };

            _favoriteButton.Click += (s, e) => ShowAddToFavoritesDialog(_currentFileRelPath);

            AntdUI.Panel pathBarPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Margin = new Padding(0),
                Back = Color.Transparent
            };
            pathBarPanel.Controls.Add(_currentFilePathLabel);
            pathBarPanel.Controls.Add(_favoriteButton);

            _placeholderLabel = new AntdUI.Label
            {
                Name = "Editor.SelectFileHint", Text = LanguageManager.Instance.GetText("Editor.SelectFileHint", "请从左侧文件树中选择一个文件进行编辑"),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(14),
                ForeColor = ThemeManager.TextTertiary,
                Visible = false
            };

            // DBR editor: TabControl with two modes (grid / source)
            _editorTabControl = new AntdUI.Tabs
            {
                Dock = DockStyle.Fill,
                Visible = false,
                Type = AntdUI.TabType.Card,
                TabMenuVisible = true,
                Font = FontHelper.UiFont(10, FontStyle.Bold),
                Style = new AntdUI.Tabs.StyleCard
                {
                    Border = 1,
                    Gap = 10,
                    BorderColor = ThemeManager.BorderColor,
                    BorderActive = AntdUI.Style.Db.Primary
                }
            };

            AntdUI.TabPage editorTab = new AntdUI.TabPage { Name = "Editor.DbrEditorMode", Text = LanguageManager.Instance.GetText("Editor.DbrEditorMode", "编辑器模式"), Font = FontHelper.UiFont(9) };
            _dbrSplit = new ArrowSplitter
            {
                Dock = DockStyle.Fill,
                CollapsePanel = AntdUI.Splitter.ADCollapsePanel.Panel1,
                CollapsedTextKey = "Editor.CollapseOutline",
                ExpandedTextKey = "Editor.ExpandOutline",
                Lazy = false
            };
            _dbrSplit.SplitPanelStateChanged += (s, e) =>
            {
                if (!e.Value)
                {
                    _dbrSplit.Panel1MinSize = 0;
                    _dbrSplit.SplitterDistance = 0;
                }
                else
                {
                    _dbrSplit.Panel1MinSize = (int)(120 * (DpiManager.Scale));
                    _dbrSplit.SplitterDistance = 250;
                }
                SettingsManager.EditorOutlinePanelCollapsed = !e.Value;
                SettingsManager.Save();
            };

            _outlineTree = new AntdUI.Tree
            {
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(9),
                Gap = 4
            };
            _outlineTree.SelectChanged += OnOutlineNodeSelected;
            _outlineTree.NodeMouseClick += OnOutlineNodeMouseClick;
            AntdUI.Label outlineHeader = new AntdUI.Label
            {
                Name = "Editor.FieldGroup", Text = LanguageManager.Instance.GetText("Editor.FieldGroup", "字段分组"),
                Dock = DockStyle.Top,
                Height = 22,
                Padding = new Padding(6, 0, 0, 0),
                Font = FontHelper.UiFont(9),
                ForeColor = ThemeManager.TextTertiary,
            };
            _dbrSplit.Panel1.Controls.Add(_outlineTree);
            _dbrSplit.Panel1.Controls.Add(outlineHeader);

            _dbrGrid = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VirtualMode = true,
                VisibleHeader = true,
                Bordered = true,
                BorderColor = ThemeManager.TableGridColor,   // 网格线加深（列头分割线=拖宽抓手，内置色对比弱）
                GapCell = 4,
                EditMode = AntdUI.TEditMode.DoubleClick,
                LostFocusClearSelection = false,
                Font = FontHelper.UiFont(9),
                ClipboardCopyFocusedCell = true,
                EnableHeaderResizing = true,
                Gap = 4
            };
            _dbrGrid.CellClick += OnDbrCellClick;
            _dbrGrid.CellDoubleClick += OnDbrCellDoubleClick;
            _dbrGrid.CellBeginEdit += OnDbrCellBeginEdit;
            _dbrGrid.CellBeginEditInputStyle += OnDbrCellBeginEditInputStyle;
            _dbrGrid.CellEndEdit += OnDbrCellEndEdit;
            _dbrGrid.CellButtonClick += OnDbrCellButtonClick;
            _dbrGrid.SetRowStyle += OnDbrGridSetRowStyle;
            _dbrGrid.SortRows += OnDbrGridSortRows;

            // DBR 网格右键菜单（复制 + 批量编辑此字段）
            var copyMenuItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Common.Copy", "复制")) { Tag = "copy" };
            var batchMenuItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Editor.BatchEditField", "批量编辑此字段")) { Tag = "batchField" };
            var undoBatchMenuItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Editor.BatchUndoMenu", "撤销本次批量修改")) { Tag = "undoBatch" };
            var dbrResetWidthItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Editor.ResetTableWidth", "重置本表列宽记忆")) { Tag = "resetWidths" };
            _dbrGridContextMenuItems = new AntdUI.IContextMenuStripItem[] { copyMenuItem, batchMenuItem, undoBatchMenuItem,
                new AntdUI.ContextMenuStripItemDivider(), dbrResetWidthItem };

            // 搜索栏：统一搜索字段名+数值
            var searchBar = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                Back = Color.Transparent,
                InnerPadding = new Padding(0)
            };

            _fieldFilterTextBox = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(12),
                Name = "Common.SearchLabel", PrefixText = LanguageManager.Instance.GetText("Common.SearchLabel", "搜索："),
                AllowClear = true
            };
            _fieldFilterTextBox.TextChanged += (s, e) =>
            {
                _fieldFilterTimer.Stop();
                _fieldFilterTimer.Start();
            };

            _btnOpenSameTemplate = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Editor.OpenSameTemplate", "同模板文件同时编辑"),
                Type = AntdUI.TTypeMini.Primary,
                AutoSize = true,
                
                Font = FontHelper.UiFont(9),
                Dock = DockStyle.Right,
                Margin = new Padding(4, 0, 0, 0)
            };
            _btnOpenSameTemplate.Click += (s, e) => OnOpenSameTemplateClick();

            // 批量编辑（仅多文件模式显示；§6.6 Dock=Right 后加的在右 → 位于同模板按钮右侧）
            _btnBatchEdit = new AntdUI.Button
            {
                Name = "Editor.BatchEdit", Text = LanguageManager.Instance.GetText("Editor.BatchEdit", "批量编辑"),
                AutoSize = true,
                
                Font = FontHelper.UiFont(9),
                Dock = DockStyle.Right,
                Margin = new Padding(4, 0, 0, 0),
                Visible = false
            };
            _btnBatchEdit.Click += (s, e) => OnBatchEditClick();

            searchBar.Controls.Add(_fieldFilterTextBox);
            searchBar.Controls.Add(_btnOpenSameTemplate);
            searchBar.Controls.Add(_btnBatchEdit);

            _fieldFilterTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _fieldFilterTimer.Tick += (s, e) =>
            {
                _fieldFilterTimer.Stop();
                RefreshDbrGrid();
            };
            _fieldFilterTextBox.TextChanged += (s, e) =>
            {
                _fieldFilterTimer.Stop();
                _fieldFilterTimer.Start();
            };

            var filterPanel = new AntdUI.Panel { Dock = DockStyle.Fill, Back = Color.Transparent };
            filterPanel.Controls.Add(_dbrGrid);
            filterPanel.Controls.Add(searchBar);
            _dbrSplit.Panel2.Controls.Add(filterPanel);
            editorTab.Controls.Add(_dbrSplit);
            _editorTabControl.Pages.Add(editorTab);

            AntdUI.TabPage sourceTab = new AntdUI.TabPage { Name = "Editor.DbrSourceMode", Text = LanguageManager.Instance.GetText("Editor.DbrSourceMode", "源码模式"), Font = FontHelper.UiFont(9) };
            var sourceContainer = new AntdUI.Panel { Dock = DockStyle.Fill, Back = Color.Transparent };

            _dbrSourceEditor = new ScintillaNET.Scintilla
            {
                Dock = DockStyle.Fill,
                TabWidth = 4,
                UseTabs = false,
                TabIndents = true,
                WrapMode = ScintillaNET.WrapMode.Word,
                Technology = ScintillaNET.Technology.Default,  // GDI 渲染（定稿）：DirectWrite 模式下缺字形的字体回退常落日文字形，GDI 走字体链接回退宋体无此问题
                FontQuality = ScintillaNET.FontQuality.LcdOptimized   // ClearType 抗锯齿：GDI 下的最佳清晰度（仅 GDI 生效，DirectWrite 模式忽略此项）
            };
            _dbrSourceEditor.Styles[ScintillaNET.Style.Default].Font = FontHelper.MonoFontName;
            _dbrSourceEditor.Styles[ScintillaNET.Style.Default].Size = (int)(_editorFontSize * DpiManager.Scale);
            _dbrSourceEditor.Margins[0].Type = ScintillaNET.MarginType.Number;
            _dbrSourceEditor.TextChanged += OnDbrSourceTextChanged;
            // 窗体显示后再补一次行号颜色：AntdUI 的主题应用可能晚于本控件创建，
            // 会把 STYLE_LINENUMBER 重置为默认浅灰（暗色下行号栏发灰的根因之一）。
            Shown += (s, e) =>
            {
                if (_dbrSourceEditor != null && !_dbrSourceEditor.IsDisposed)
                    ScintillaSyntax.SetLineNumber(_dbrSourceEditor);
            };

            // 按当前主题配色（深浅两套）
            ApplyEditorTheme();
            _dbrSourceEditor.UpdateUI += (s, e) =>
            {
                var line = _dbrSourceEditor.LineFromPosition(_dbrSourceEditor.CurrentPosition);
                _dbrSourceEditor.MarkerDeleteAll(0);
                _dbrSourceEditor.Lines[line].MarkerAdd(0);
            };
            // Ctrl+F 触发查找栏
            _dbrSourceEditor.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.F)
                {
                    _findTextBox.Focus();
                    _findTextBox.SelectAll();
                    e.SuppressKeyPress = true;
                }
            };

            // 按当前主题应用源码编辑器配色（Scintilla 为第三方控件，不随 AntdUI 主题自动换肤）
            void ApplyEditorTheme()
            {
                if (_dbrSourceEditor == null || _dbrSourceEditor.IsDisposed) return;
                _dbrSourceEditor.BackColor = ThemeManager.EditorBack;
                _dbrSourceEditor.ForeColor = ThemeManager.EditorFore;
                _dbrSourceEditor.Styles[ScintillaNET.Style.Default].BackColor = ThemeManager.EditorBack;
                _dbrSourceEditor.Styles[ScintillaNET.Style.Default].ForeColor = ThemeManager.EditorFore;
                _dbrSourceEditor.CaretForeColor = ThemeManager.EditorFore;
                _dbrSourceEditor.CaretLineBackColor = ThemeManager.EditorCurrentLine;
                _dbrSourceEditor.Markers[0].Symbol = ScintillaNET.MarkerSymbol.Background;
                _dbrSourceEditor.Markers[0].SetBackColor(ThemeManager.EditorMarkerBack);
                _dbrSourceEditor.Markers[0].SetForeColor(ThemeManager.EditorMarkerFore);
                // 选中高亮：暗色下默认选中为白字灰底看不清，统一改为 VS Code 风格选中蓝+白字
                _dbrSourceEditor.SelectionBackColor = ThemeManager.EditorSelectionBack;
                _dbrSourceEditor.SelectionTextColor = ThemeManager.EditorSelectionFore;
                _dbrSourceEditor.SelectionAdditionalBackColor = ThemeManager.EditorSelectionBack;
                _dbrSourceEditor.SelectionAdditionalTextColor = ThemeManager.EditorSelectionFore;
                _dbrSourceEditor.SelectionInactiveBackColor = ThemeManager.EditorSelectionBack;
                _dbrSourceEditor.SelectionInactiveTextColor = ThemeManager.EditorSelectionFore;
                ApplySourceSyntaxTheme();
            }

            // 源码模式顶部工具栏（字体 + 查找合并在一行）
            var sourceToolbar = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                Back = Color.Transparent
            };
            var decBtn = new AntdUI.Button() { Font = FontHelper.UiFont(9), AutoSize = true, Text = "－", Dock = DockStyle.Left };
            var sizeLabel = new AntdUI.Label() { Text = _editorFontSize.ToString(), Name = "sizeLabel", AutoSize = true, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(0, 6, 0, 0) };
            var incBtn = new AntdUI.Button() { Font = FontHelper.UiFont(9), AutoSize = true, Text = "＋", Dock = DockStyle.Left };

            _findTextBox = new AntdUI.Input
            {
                Dock = DockStyle.Left,
                Width = 300,
                Height = 28,
                Name = "Editor.FindBar", PrefixText = LanguageManager.Instance.GetText("Editor.FindBar", "查找") + ":",
                AllowClear = true,
                Margin = new Padding(0)
            };
            _findTextBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { FindNext(); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.Escape) { _findTextBox.Text = ""; _dbrSourceEditor.Focus(); e.SuppressKeyPress = true; }
            };
            var findPrevBtn = new AntdUI.Button
            {
                Name = "Editor.FindPrev", Text = LanguageManager.Instance.GetText("Editor.FindPrev", "查找上一个"),
                AutoSize = true, Dock = DockStyle.Left,
                Font = FontHelper.UiFont(9),
            };
            findPrevBtn.Click += (s, e) => FindPrevious();
            var findNextBtn = new AntdUI.Button
            {
                Name = "Editor.FindNext", Text = LanguageManager.Instance.GetText("Editor.FindNext", "查找下一个"),
                AutoSize = true, Dock = DockStyle.Left,
                Font = FontHelper.UiFont(9),
            };
            findNextBtn.Click += (s, e) => FindNext();
            _findMatchCountLabel = new AntdUI.Label { AutoSize = true, Height = 28, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };

            // Dock顺序：Left控件按显示顺序从右到左添加（WinForms从后往前处理Dock）
            // 显示顺序：[decBtn][sizeLabel][incBtn][findTextBox 300px][findNextBtn][findPrevBtn][_findMatchCountLabel]
            sourceToolbar.Controls.Add(_findMatchCountLabel);
            sourceToolbar.Controls.Add(findNextBtn);
            sourceToolbar.Controls.Add(findPrevBtn);
            sourceToolbar.Controls.Add(_findTextBox);
            sourceToolbar.Controls.Add(incBtn);
            sourceToolbar.Controls.Add(sizeLabel);
            sourceToolbar.Controls.Add(decBtn);

            decBtn.Click += (s, e) =>
            {
                _editorFontSize = Math.Max(6, _editorFontSize - 1);
                ApplyEditorFontSize();
                sizeLabel.Text = _editorFontSize.ToString();
            };
            incBtn.Click += (s, e) =>
            {
                _editorFontSize = Math.Min(24, _editorFontSize + 1);
                ApplyEditorFontSize();
                sizeLabel.Text = _editorFontSize.ToString();
            };

            var closeBtn = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Editor.SourceClose", "关闭"),
                
                
                Dock = DockStyle.Right,
                Margin = new Padding(0),
                Type = AntdUI.TTypeMini.Error
            };
            closeBtn.Click += (s, e) => ConfirmCloseCurrentFile();
            sourceToolbar.Controls.Add(closeBtn);

            // 源码模式未打开文件时的占位提示
            _sourceNoFilePlaceholder = new AntdUI.Label
            {
                Dock = DockStyle.Fill,
                Name = "Editor.SourceNoFile", Text = LanguageManager.Instance.GetText("Editor.SourceNoFile", "当前未打开任何文件"),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = ThemeManager.TextTertiary,
                Visible = false
            };

            sourceContainer.Controls.Add(_sourceNoFilePlaceholder);
            sourceContainer.Controls.Add(_dbrSourceEditor);
            sourceContainer.Controls.Add(sourceToolbar);
            sourceTab.Controls.Add(sourceContainer);
            _editorTabControl.Pages.Add(sourceTab);

            _editorTabControl.SelectedIndexChanged += OnDbrTabSelectedIndexChanged;

            // 文件列表
            _fileListGrid = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = true,
                Bordered = true,
                BorderColor = ThemeManager.TableGridColor,   // 网格线加深（列头分割线=拖宽抓手，内置色对比弱）
                GapCell = 2,
                MultipleRows = true,
EditMode = AntdUI.TEditMode.None,
                LostFocusClearSelection = false,
                EnableHeaderResizing = true,
VirtualMode = true,
                RowHeight = 26,
                // 各列自动列宽（按内容自适应；勾选列保留固定 40）
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Auto,
                // 选中行只换**背景**高亮，不设 RowSelectedFore：AntdUI 渲染时选中前景色优先于行样式
                // （Table.Render.cs），设了就会盖掉"已修改/导入"的状态色。不设则选中行仍按状态着色。
                RowSelectedBg = ThemeManager.EditorSelectionBack
            };
            // 列宽默认策略（用户定稿）：固定内容列=auto（内容可预估），其余=150px（防虚拟渲染滚动抖动）；
            // 用户拖动后关窗 → TableWidthStore 整表快照，此后全列固定像素（渠道收敛：默认定义 → 拖动记忆）
            _fileListGrid.Columns.Add(new AntdUI.ColumnCheck("Checked", "") { Width = "40" });
            // 数据列全部开启列头排序（SortOrder 库画排序图标，点击循环 NONE→升→降→NONE；
            // 实际数据排序由 SortModeChanged 外部接管，见 OnFileListSortModeChanged——修改时间按
            // "yyyy-MM-dd HH:mm:ss" 固定格式存储，字符串比较即时间先后，空值统一沉底）
            _fileListGrid.Columns.Add(new AntdUI.Column("FileName",
                LanguageManager.Instance.GetText("Editor.FileName", "文件名")) { Width = "150", SortOrder = true });
            _fileListGrid.Columns.Add(new AntdUI.Column("FileDescription",
                LanguageManager.Instance.GetText("Editor.FileDescription", "说明")) { Width = "150", SortOrder = true });
            _fileListGrid.Columns.Add(new AntdUI.Column("ModifyTime",
                LanguageManager.Instance.GetText("Editor.ModifyTime", "修改时间")) { Width = "auto", SortOrder = true });
            _fileListGrid.Columns.Add(new AntdUI.Column("Notes",
                LanguageManager.Instance.GetText("Editor.Notes", "本地化名称")) { Width = "150", SortOrder = true });
            _fileListGrid.Columns.Add(new AntdUI.Column("Template",
                LanguageManager.Instance.GetText("Editor.TemplateColumn", "模板"))
            { Width = "150", SortOrder = true, Render = BuildFileListTemplateCellRender });
            // 列定义就绪后应用记忆列宽（TableWidthStore；首次调用捕获原始列宽供重置恢复）+ MouseUp 防抖自动保存
            TableWidthStore.Apply(TableWidthStore.TableId.FileList, _fileListGrid);
            TableWidthStore.AttachAutoSave(TableWidthStore.TableId.FileList, _fileListGrid);
            _fileListGrid.CellClick += OnFileListCellClick;
            _fileListGrid.CellDoubleClick += OnFileListCellDoubleClick;
            _fileListGrid.MouseClick += OnFileListMouseClick;
            _fileListGrid.SelectIndexChanged += OnFileListSelectIndexChanged;
            _fileListGrid.SetRowStyle += OnFileListGridSetRowStyle;
            _fileListGrid.SortModeChanged += OnFileListSortModeChanged;
            _fileListGrid.CheckedChanged += OnFileListCheckedChanged;
            _fileListGrid.CheckedOverallChanged += OnFileListCheckedOverallChanged;
            _fileListGrid.MouseDown += OnFileListMouseDown;
            _fileListGrid.MouseMove += OnFileListMouseMove;
            _fileListGrid.MouseUp += OnFileListMouseUp;

            _fileListOpenItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Editor.OpenFile", "打开")) { Tag = "open" };
            var fileListCopyItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Common.Copy", "复制")) { Tag = "copy" };
            _fileListDeleteItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Editor.DeleteFile", "删除")) { Tag = "delete" };
            _fileListExportItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Editor.ExportFile", "导出")) { Tag = "export" };
            _fileListImportItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Editor.ImportFile", "导入")) { Tag = "import" };
            var fileListFavItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Editor.AddToFavorites", "添加到收藏夹")) { Tag = "addFavorite" };
            _fileListMultiEditItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Editor.MultiEditSameTemplate", "同时编辑")) { Tag = "multiEdit" };
            var fileListResetWidthItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("Editor.ResetTableWidth", "重置本表列宽记忆")) { Tag = "resetWidths" };
            _fileListContextMenuItems = new AntdUI.IContextMenuStripItem[] {
                _fileListOpenItem,
                fileListCopyItem,
                new AntdUI.ContextMenuStripItemDivider(),
                _fileListDeleteItem,
                _fileListExportItem,
                _fileListMultiEditItem,
                new AntdUI.ContextMenuStripItemDivider(),
                _fileListImportItem,
                new AntdUI.ContextMenuStripItemDivider(),
                fileListFavItem,
                new AntdUI.ContextMenuStripItemDivider(),
                fileListResetWidthItem
            };

            // 文件列表筛选
            _fileListFilterTextBox = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(12),
                Name = "Editor.FileListFilter", PrefixText = LanguageManager.Instance.GetText("Editor.FileListFilter", "筛选") + "：",
                AllowClear = true
            };
            _fileListFilterTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _fileListFilterTimer.Tick += (s, e) =>
            {
                _fileListFilterTimer.Stop();
                ApplyFileListFilter();
            };
            _fileListFilterTextBox.TextChanged += (s, e) =>
            {
                _fileListFilterTimer.Stop();
                _fileListFilterTimer.Start();
            };

            var fileListInputBtnPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Left,
                Width = 300,
                InnerPadding = new Padding(0),
                Back = Color.Transparent
            };
            fileListInputBtnPanel.Controls.Add(_fileListFilterTextBox);

            // 模板筛选下拉：与搜索框独立，两者叠加过滤（AND）。
            // 选项在列表加载后动态生成（树模式=当前目录的模板；平铺模式=全部已用模板），见 UpdateTemplateFilterOptions
            _templateFilterSelect = new AntdUI.Select
            {
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(12),
                MaxCount = 20    // 下拉列表最多显示 20 项再出滚动条（默认 4）
            };
            _templateFilterSelect.Items.Add(new AntdUI.SelectItem(
                LanguageManager.Instance.GetText("Editor.TemplateFilterAll", "全部模板"), ""));
            _templateFilterSelect.SelectedValue = "";
            _templateFilterSelect.SelectedIndexChanged += (s, e) =>
            {
                if (_templateSelectSyncing) return;
                ApplyFileListFilter();
            };
            _templateFilterLabel = new AntdUI.Label
            {
                Dock = DockStyle.Left,
                Text = LanguageManager.Instance.GetText("Editor.TemplateFilterLabel", "模板") + "：",
                AutoSize = true,
                Padding = new Padding(0, 10, 0, 0)
            };
            var templateFilterPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Left,
                Width = 380,
                InnerPadding = new Padding(0),
                Back = Color.Transparent
            };
            // 行容器加入顺序：先 Fill（下拉）→ 再 Left（标签），标签靠左（WinForms 反向 Dock）
            templateFilterPanel.Controls.Add(_templateFilterSelect);
            templateFilterPanel.Controls.Add(_templateFilterLabel);

            AntdUI.TabPage fileListTab = new AntdUI.TabPage { Name = "Editor.FileList", Text = LanguageManager.Instance.GetText("Editor.FileList", "文件列表"), Font = FontHelper.UiFont(9) };

            AntdUI.Panel fileListOuterPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent
            };

            var fileListFilterBar = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Back = Color.Transparent
            };
            // 筛选栏加入顺序（WinForms 反向 Dock）：后加的在左 → 搜索框最左，模板下拉在其右侧
            fileListFilterBar.Controls.Add(templateFilterPanel);
            fileListFilterBar.Controls.Add(fileListInputBtnPanel);

            fileListOuterPanel.Controls.Add(_fileListGrid);
            fileListOuterPanel.Controls.Add(fileListFilterBar);

            fileListTab.Controls.Add(fileListOuterPanel);
            _editorTabControl.Pages.Insert(0, fileListTab);

            _readOnlyView = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Visible = false,
                Font = FontHelper.MonoFont(9.5f),
                WordWrap = false,
                ReadOnly = true,
                Multiline = true
            };

            AntdUI.Panel editorContentPanel = new AntdUI.Panel { Dock = DockStyle.Fill, Back = Color.Transparent };
            editorContentPanel.Controls.Add(_editorTabControl);
            editorContentPanel.Controls.Add(_readOnlyView);
            editorContentPanel.Controls.Add(_placeholderLabel);

            var editorLayout = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent
            };
            // Controls.Add 顺序：Fill先加→Top后加（WinForms从后往前处理Dock）
            editorLayout.Controls.Add(editorContentPanel);
            editorLayout.Controls.Add(pathBarPanel);

            _editorPanel.Controls.Add(editorLayout);

            panel.Controls.Add(_editorPanel);
        }

        private void BuildRightPanel(Control panel)
        {
            AntdUI.Label header = new AntdUI.Label
            {
                Name = "Editor.ModifiedFiles", Text = LanguageManager.Instance.GetText("Editor.ModifiedFiles", "已修改的文件"),
                Dock = DockStyle.Top,
                Height = 24,
                Padding = new Padding(3, 4, 0, 0),
                Font = FontHelper.UiFont(9, FontStyle.Bold)
            };

            _modifiedList = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = false,
                Bordered = true,
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill,
                MultipleRows = true,
                EditMode = AntdUI.TEditMode.None,
                LostFocusClearSelection = false,
                Gap = 4
            };
            _modifiedList.VisibleHeader = true;
            _modifiedList.Columns.Add(new AntdUI.Column("CellLinks", "")
            { Width = "46" });
            _modifiedList.Columns.Add(new AntdUI.Column("CellTags",
                LanguageManager.Instance.GetText("Common.Status", "状态"))
            { Width = "46" });
            _modifiedList.Columns.Add(new AntdUI.Column("FilePath",
                LanguageManager.Instance.GetText("Common.Path", "路径"))
            { LineBreak = true });
            _modifiedList.SelectIndexChanged += OnModifiedListClick;
            _modifiedList.CellClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                {
                    _modifiedList.SelectedIndex = e.RowIndex;
                    AntdUI.ContextMenuStrip.open(_modifiedList, it =>
                    {
                        var row = _modifiedList.GetRow(_modifiedList.SelectedIndex - 1)?.record as ModifiedFileRow;
                        if (row == null) return;
                        string relPath = row.FilePath;
                        if (string.IsNullOrEmpty(relPath)) return;

                        string tempFile = _session.GetTempFilePath(relPath);
                        try
                        {
                            if (File.Exists(tempFile))
                                File.Delete(tempFile);
                            UnmarkFileModified(relPath);   // 内部已做局部刷新（含撤销导入的树/列表移除）
                            LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.UndoSelectedDone", "撤销选中完成: {0}"), relPath), "INFO", relPath);
                            ShowFileList();   // 撤销后回到文件列表选项卡
                        }
                        catch (Exception ex)
                        {
                            LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.UndoError", "撤销失败: {0}"), ex.Message), "ERROR");
                        }
                    }, new AntdUI.IContextMenuStripItem[]
                    {
                        new AntdUI.ContextMenuStripItem(
                            LanguageManager.Instance.GetText("Editor.UndoChanges", "撤销选中修改"))
                        { Tag = "undo" }
                    });
                }
            };

            _overwriteOnSaveCheckBox = new AntdUI.Checkbox
            {
                Name = "Editor.OverwriteOnSave", Text = LanguageManager.Instance.GetText("Editor.OverwriteOnSave", "覆盖同名文件"),
                Dock = DockStyle.Bottom,
                Height = 24,
                Padding = new Padding(6, 0, 0, 0),
                Checked = true
            };

            _modifiedList.CellButtonClick += (s, e) =>
            {
                var row = e.Record as ModifiedFileRow;
                if (row == null) return;
                string relPath = row.FilePath;
                if (string.IsNullOrEmpty(relPath)) return;

                string tempFile = _session.GetTempFilePath(relPath);
                try
                {
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
                    UnmarkFileModified(relPath);   // 内部已做局部刷新（含撤销导入的树/列表移除）
                    LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.UndoSelectedDone", "撤销选中完成: {0}"), relPath), "INFO", relPath);
                    // 若撤销的文件正是当前编辑的文件 → 重新从包中加载（与"撤销全部修改"行为一致）
                    if (string.Equals(relPath, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _editorDirty = false;
                        SelectFileInTree(_currentFileRelPath);
                    }
                    ShowFileList();   // 撤销后回到文件列表选项卡
                }
                catch (Exception ex)
                {
                    LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.UndoError", "撤销失败: {0}"), ex.Message), "ERROR");
                }
            };

            var undoAllButton = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Name = "Editor.UndoAllChanges", Text = LanguageManager.Instance.GetText("Editor.UndoAllChanges", "撤销全部修改"),
                Dock = DockStyle.Bottom,
                };
            undoAllButton.Click += OnUndoAllClick;

            panel.Controls.Add(_modifiedList);
            panel.Controls.Add(_overwriteOnSaveCheckBox);
            panel.Controls.Add(undoAllButton);
            panel.Controls.Add(header);
        }
        private void BuildOutputPanel()
        {
            _outputPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent
            };

            // Header bar: 日志工具条（四级筛选 + 日志搜索框 + 导出/清空；布局与主界面 LogView 一致，
            // 均为手工绝对定位：左「筛选+搜索」、右「导出、清空（最右）」）
            AntdUI.Panel headerBar = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = DpiManager.S(40),
                Padding = new Padding(0),
                Back = Color.Transparent
            };

            // 四级筛选：与主界面 LogView 同规格（等宽铺满、逗控件色、默认选「全部」）
            _logLevelFilter = new AntdUI.Segmented
            {
                Name = "LogView.LevelFilter",
                Full = true,
                Width = DpiManager.S(208),
                Height = DpiManager.S(30),
                Font = FontHelper.UiFont(8.5f),
                BackColor = ThemeManager.BgContainer,
                BorderColor = ThemeManager.BorderColor,
                BackActive = ThemeManager.Primary,
                ForeActive = ThemeManager.PrimaryColor,
                SelectIndex = 0
            };
            foreach (string text in LogLevelFilter.LevelTexts())
                _logLevelFilter.Items.Add(new AntdUI.SegmentedItem { Text = text });
            _logLevelFilter.SelectIndexChanged += (s, e) =>
            {
                if (e.Value < 0 || e.Value > 3) return;
                _logLevelIndex = e.Value;
                _renderedLogCount = -1;   // 过滤条件变化 → 下次全量重建
                RefreshLogDisplay();
            };

            _logSearchBox = new AntdUI.Input
            {
                Width = DpiManager.S(260),
                Height = DpiManager.S(36),
                Font = FontHelper.UiFont(8.5f),
                Text = "",
                Name = "Editor.LogSearch", PrefixText = LanguageManager.Instance.GetText("Editor.LogSearch", "日志搜索") + ":",
                AllowClear = true
            };
            _logSearchBox.TextChanged += (s, e) =>
            {
                _logSearchTimer.Stop();
                _logSearchTimer.Start();
            };

            _logClearAllBtn = new AntdUI.Button
            {
                Name = "Editor.ClearLog", Text = LanguageManager.Instance.GetText("Editor.ClearLog", "清空日志"),
                AutoSize = true,
                MinimumSize = new Size(DpiManager.S(80), DpiManager.S(22)),
                Padding = new Padding(DpiManager.S(4), 0, DpiManager.S(4), 0),
                Font = FontHelper.UiFont(9)
            };
            _logClearAllBtn.Click += (s, e) =>
            {
                var result = MessageBox.Show(
                    LanguageManager.Instance.GetText("Editor.ConfirmClearLog", "确定要清空日志吗？"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Confirm", "确认"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.Yes)
                {
                    // 六处一起清：面板条目 / 平行元数据 / 折叠分组状态 / 待显示队列 / 链接侧表 / Scintilla 视图，
                    // 缺一处都会"看着没清干净"或残留可点区域/旧折叠结构。
                    // ① Scintilla 平时为只读（写文本前临时关闭只读是本项目既有惯例，见 RefreshLogDisplay）；
                    // ② 清空后必须显式 RefreshLogDisplay：队列为空时 FlushLogQueue 会直接 return，不会自动重绘。
                    _logEntries.Clear();
                    _logEntryRefs.Clear();
                    _logRowKinds.Clear();
                    _logRowKeys.Clear();
                    _logOpenGroup = null;
                    _logPendingCollapse.Clear();
                    _logHeaderUpdates.Clear();
                    _logFinalizeRequested = 0;
                    _logLinkRanges.Clear();
                    while (_logQueue.TryDequeue(out _)) { }
                    _renderedLogCount = -1;      // 清空后重新全量建立渲染基准
                    _logOverwriteDirty = false;
                    _logLastRenderedIsLastEntry = false;
                    if (_logScintilla != null && !_logScintilla.IsDisposed)
                    {
                        _logScintilla.ReadOnly = false;
                        _logScintilla.ClearAll();
                        _logScintilla.ReadOnly = true;
                    }
                    RefreshLogDisplay();
                }
            };

            // 「导出日志」：把面板当前可见的行（尊重级别筛选与搜索过滤）另存为 .log
            _logExportBtn = new AntdUI.Button
            {
                Name = "Editor.LogExportChanges", Text = LanguageManager.Instance.GetText("Editor.LogExportChanges", "导出日志"),
                AutoSize = true,
                MinimumSize = new Size(DpiManager.S(80), DpiManager.S(22)),
                Padding = new Padding(DpiManager.S(4), 0, DpiManager.S(4), 0),
                Font = FontHelper.UiFont(9)
            };
            _logExportBtn.Click += OnLogExportChanges;

            headerBar.Controls.Add(_logExportBtn);
            headerBar.Controls.Add(_logSearchBox);
            headerBar.Controls.Add(_logClearAllBtn);
            headerBar.Controls.Add(_logLevelFilter);
            headerBar.Resize += (s, e) => LayoutLogToolbar(headerBar);
            LayoutLogToolbar(headerBar);

            // 日志面板改用只读 Scintilla，原因有二：
            // ① 自带纵向 + 横向滚动条（AntdUI.Input 的多行框不提供滚动条，长日志无法回看）；
            // ② 支持 Marker 行级着色（按 add/del/mod 与警告/错误分色，仅 UI，不写进 .log 文件）
            _logScintilla = new ScintillaNET.Scintilla
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                TabWidth = 4,
                UseTabs = false,
                Technology = ScintillaNET.Technology.Default,  // GDI 渲染（定稿）：DirectWrite 模式下缺字形的字体回退常落日文字形，GDI 走字体链接回退宋体无此问题
                FontQuality = ScintillaNET.FontQuality.LcdOptimized   // ClearType 抗锯齿：GDI 下的最佳清晰度（仅 GDI 生效，DirectWrite 模式忽略此项）
            };
            _logScintilla.Margins[0].Width = 0;          // 隐藏行号边距（日志已有时间戳，其行号无参考价值）
            _logScintilla.CaretLineBackColor = Color.Transparent;   // 透明 = 不显示当前行高亮，避免与标记底色冲突
            // 统一样式（公共方法）：等宽字体、明暗配色、选区配色、超宽自动换行、只保留纵向滚动条
            ScintillaSyntax.ApplyReadOnlyLog(_logScintilla, FontHelper.MonoFontName, (int)(9f * DpiManager.Scale));
            LogFoldSupport.ConfigureFoldUi(_logScintilla);   // 折叠边距/点击收放（含折叠符号配色；与主界面 LogView 同配置）

            // ── 日志行双击跳转：热点样式（Scintilla 5 的"悬停指示器"）+ 双击路由 ──
            // 说明：indicator 与 marker 是两套独立编号（本控件上 indicator 只此一处使用，取 7），
            // 不依赖已废弃的 INDIC_HOTSPOT 样式常量：用 TextFore 把路径文字染成主色并加下划线，
            // 悬停走 Scintilla 5 的 HoverStyle/HoverForeColor 提供反馈。
            var linkIndicator = _logScintilla.Indicators[LogLinkIndicator];
            linkIndicator.Style = ScintillaNET.IndicatorStyle.TextFore;
            linkIndicator.ForeColor = ThemeManager.Primary;
            linkIndicator.Under = true;
            linkIndicator.Alpha = 0;
            linkIndicator.HoverStyle = ScintillaNET.IndicatorStyle.TextFore;
            linkIndicator.HoverForeColor = ThemeManager.Primary;
            _logScintilla.ActiveHotspotTextColor = ThemeManager.Primary;
            _logScintilla.ActiveHotspotBackColor = _logScintilla.BackColor;
            // 主路由：Scintilla 原生热点事件（仅在命中热点区间时触发；个别版本可能不触发，故有兜底）
            _logScintilla.HotspotDoubleClick += (s, e) => OnLogLinkDoubleClick(e.Position);
            // 兜底路由：鼠标双击按位置再判一次（两条路由内部按 300ms 去重）
            _logScintilla.MouseDoubleClick += (s, e) =>
            {
                int pos = _logScintilla.CharPositionFromPointClose(e.X, e.Y);
                if (pos < 0) pos = _logScintilla.CharPositionFromPoint(e.X, e.Y);
                OnLogLinkDoubleClick(pos);
            };
            _logLinkTip = new ToolTip();
            _logLinkTip.SetToolTip(_logScintilla,
                LanguageManager.Instance.GetText("Editor.LogLinkHint",
                    "双击日志中的文件路径可直达该文件（仅 .dbr / .tpl / .txt / .lua）"));

            _outputPanel.Controls.Add(_logScintilla);
            _outputPanel.Controls.Add(headerBar);

            _logTimer = new System.Windows.Forms.Timer();
            _logTimer.Interval = 200;
            _logTimer.Tick += (s, e) => FlushLogQueue();
            _logTimer.Start();

            _logSearchTimer = new System.Windows.Forms.Timer();
            _logSearchTimer.Interval = 200;
            _logSearchTimer.Tick += (s, e) =>
            {
                _logSearchTimer.Stop();
                _logSearchText = _logSearchBox.Text;
                RefreshLogDisplay();
            };

            // 源码模式行级日志防抖（0.5s，一次性）：用户停顿 0.5s 后对「上次记录基线 → 当前文本」
            // 做一次整段 diff 并推进基线 —— 单行连续输入自然合并为一条记录（中间值不记录，如 10→15→20 只记 10→20），
            // 不会每个按键刷一条。切页签/关窗/保存的既有触发点保留作兜底（基线已推进，不会重复）。
            _sourceLogTimer = new System.Windows.Forms.Timer();
            _sourceLogTimer.Interval = 500;
            _sourceLogTimer.Tick += (s, e) =>
            {
                _sourceLogTimer.Stop();
                LogSourceLineChanges();
            };
        }

        /// <param name="relPath">该行涉及的**包内记录相对路径**（结构化标记，供日志行双击跳转；无则不传）。
        /// 仅随行在内存中流转，**不写入日志文本、不落盘**（文件日志保持纯文本）。</param>
        /// <param name="groupKey">折叠分组键（记录名/文件名，生成侧打标）：带键的警告/错误行自动按
        /// "连续同名段"折叠分组（仿主界面 LogView，见 <see cref="LogFoldSupport"/>）；不带键的行永远不成组。</param>
        private void LogOutput(string message, string level = "INFO", string relPath = null, string groupKey = null)
        {
            string line = ToSingleLogLine($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
            _logQueue.Enqueue((line, false, relPath, groupKey));

            // 文件通道：编辑器日志面板已自行显示该行，故只落盘、不投递 UI（避免重复显示）。
            // 这样编辑器里的变更记录/操作记录才会进入 %APPDATA%\GDPackEditor\GDPackEditor.log。
            LogService.WriteFileOnly(MapLogLevel(level), message);
        }

        /// <summary>
        /// 仅写入编辑器日志面板、不入文件通道：供变更记录行使用
        /// （这些行的落盘由 <see cref="LogService.AddChange"/> 负责，若再走 LogOutput 会落盘两次）。
        /// </summary>
        /// <param name="relPath">该行涉及的包内记录相对路径（双击跳转用；不写入文本）</param>
        private void LogOutputPanelOnly(string message, string relPath = null, string groupKey = null)
        {
            if (message == null) return;
            _logQueue.Enqueue((ToSingleLogLine($"[{DateTime.Now:HH:mm:ss}] [INFO] {message}"), false, relPath, groupKey));
        }

        /// <summary>编辑器日志级别字符串（INFO/WARN/ERROR/DEBUG）→ LogService 级别</summary>
        private static LogLevel MapLogLevel(string level)
        {
            switch ((level ?? string.Empty).ToUpperInvariant())
            {
                case "WARN": return LogLevel.Warn;
                case "ERROR": return LogLevel.Error;
                case "DEBUG": return LogLevel.Debug;
                case "FATAL": return LogLevel.Fatal;
                default: return LogLevel.Info;
            }
        }

        /// <summary>
        /// 记录一次【字段级】改动 —— MOD 变更记录的核心：MOD 作者必须能看到"哪个字段从什么改成了什么"。
        /// 行格式固定为 `[字段] [mod] 文件 | 字段 | 旧值 → 新值`；同时登记到全量变更报告（见 LogService.AddChange）。
        /// 无冗余：值未变则不记（调用点已有 != 守卫，此处再兜一层）。
        /// </summary>
        private void LogFieldChange(string relPath, string fieldName, string oldValue, string newValue)
        {
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) return;
            string line = LogService.AddChange(new LogService.ChangeRecord
            {
                Kind = LogService.MarkField,
                Verb = LogService.VerbMod,
                File = relPath,
                Detail = fieldName,
                OldValue = oldValue ?? string.Empty,
                NewValue = newValue ?? string.Empty
            });
            LogOutputPanelOnly(line, relPath);   // 带包内路径：该行可双击跳转
        }

        /// <summary>源码模式行级变更记录的单文件文本上限（超出则跳过并记一行提示，避免超长文本拖慢日志）</summary>
        private const int SourceDiffMaxChars = 2 * 1024 * 1024;

        /// <summary>
        /// 捕获"源码模式基线"：源码编辑器被程序化赋值后调用（打开文件、网格切到源码模式、载入临时内容）。
        /// 之后用户的改动 = 当前文本 vs 该基线，用于生成行级变更记录（绝不逐字符记日志）。
        /// </summary>
        private void CaptureSourceBaseline()
        {
            _sourceBaselineText = _dbrSourceEditor?.Text;
            _sourceBaselinePath = _currentFileRelPath;
            // 程序化赋值（打开文件/载入临时内容/网格切源码）不算用户编辑：
            // 停掉防抖 Timer，避免用旧的待比对状态误触发一次 diff
            _sourceLogTimer?.Stop();
        }

        /// <summary>
        /// 输出源码模式下的【行级】变更记录（MOD 作者必须能看到改了哪几行）：
        /// 以 <see cref="CaptureSourceBaseline"/> 捕获的文本为基线做行级 diff，输出后把基线推进到当前文本，
        /// 因此同一批改动只会输出一次，可安全地在"切回网格 / 关闭文件 / 保存或切换文件前"多个时机重复调用。
        /// 按用户要求：不做逐字符日志、不做条数截断（完整呈现）。
        /// </summary>
        private void LogSourceLineChanges()
        {
            string baseline = _sourceBaselineText;
            string current = _dbrSourceEditor?.Text;
            if (baseline == null || current == null) return;                                   // 未进过源码模式
            if (!string.Equals(_sourceBaselinePath, _currentFileRelPath, StringComparison.OrdinalIgnoreCase)) return; // 跨文件
            if (string.Equals(baseline, current, StringComparison.Ordinal)) return;            // 无改动不记

            _sourceBaselineText = current;   // 基线推进（即使下面因超限跳过也推进，避免重复比对）

            if (baseline.Length + current.Length > SourceDiffMaxChars)
            {
                LogOutput($"{LogService.MarkLine} {_currentFileRelPath} | " + string.Format(
                    LanguageManager.Instance.GetText("Editor.SourceDiffTooLarge",
                        "文件过大（超过 {0}MB），源码模式行级变更记录已跳过"),
                    SourceDiffMaxChars / 1024 / 1024), "WARN");
                return;
            }

            DiffCompareResult diff;
            try
            {
                diff = TextDiffEngine.ComputeDiff(baseline, current, DiffMatchMode.Auto);
            }
            catch (Exception ex)
            {
                LogOutput($"{LogService.MarkLine} {_currentFileRelPath} | " + string.Format(
                    LanguageManager.Instance.GetText("Editor.SourceDiffFailed", "行级差异计算失败：{0}"),
                    ex.Message), "WARN");
                return;
            }

            // Auto 模式下两侧行各自只含本侧内容：未变/修改两侧都有，删除仅左、插入仅右。
            // 按生成顺序合并遍历即可正确配对"修改"行（同一差异块内顺序为：未变对 → 修改对 → 删除余 → 插入余）
            var left = diff.LeftLines;
            var right = diff.RightLines;
            int i = 0, j = 0;
            while (i < left.Count || j < right.Count)
            {
                DiffLineInfo l = i < left.Count ? left[i] : null;
                DiffLineInfo r = j < right.Count ? right[j] : null;

                if (l != null && l.Type == DiffLineType.Deleted)
                {
                    LogLineChange(LogService.VerbDel, l.OriginalIndex + 1, l.Text, null);
                    i++;
                    continue;
                }
                if (r != null && r.Type == DiffLineType.Inserted)
                {
                    LogLineChange(LogService.VerbAdd, r.OriginalIndex + 1, null, r.Text);
                    j++;
                    continue;
                }
                if (l != null && r != null && l.Type == DiffLineType.Modified && r.Type == DiffLineType.Modified)
                {
                    LogLineChange(LogService.VerbMod, r.OriginalIndex + 1, l.Text, r.Text);
                    i++;
                    j++;
                    continue;
                }
                // 未变行：两侧同步前进
                if (l != null) i++;
                if (r != null) j++;
            }
        }

        /// <summary>
        /// 记录一次【文件级】变更：<c>[文件] [add|del|mod] 路径</c>，并登记到全量变更报告。
        /// 仅用于**文件级操作**（导入 / 新建 / 删除）——有内容级明细的文件不再单独记文件行（按需输出，避免冗余）。
        /// </summary>
        private void LogFileChange(string relPath, string verb)
        {
            string line = LogService.AddChange(new LogService.ChangeRecord
            {
                Kind = LogService.MarkFile,
                Verb = verb,
                File = relPath
            });
            LogOutputPanelOnly(line, relPath);   // 带包内路径：该行可双击跳转
        }

        /// <summary>行级变更单条输出：<c>[行] [add|del|mod] 文件 | 第 N 行 | 旧 → 新</c>，并登记到全量变更报告</summary>
        private void LogLineChange(string verb, int lineNo, string oldLine, string newLine)
        {
            string line = LogService.AddChange(new LogService.ChangeRecord
            {
                Kind = LogService.MarkLine,
                Verb = verb,
                File = _currentFileRelPath,
                Detail = LogService.LineNumber(lineNo),
                OldValue = CleanDiffLine(oldLine),
                NewValue = CleanDiffLine(newLine)
            });
            LogOutputPanelOnly(line, _currentFileRelPath);   // 带当前文件路径：该行可双击跳转
        }

        /// <summary>
        /// 清洗差异引擎返回的单行文本：去掉行内换行符（DiffPlex 的 chunk 可能保留行尾 \r\n），
        /// 否则写进日志文件会破坏"一行一条记录"的结构。
        /// </summary>
        private static string CleanDiffLine(string s)
            => s == null ? null : s.Replace("\r", string.Empty).Replace("\n", string.Empty);

        /// <summary>
        /// 覆盖行（界面替换上一行显示的瞬态进度）。【约定】只作用于界面、不落盘：
        /// 否则每个进度点都会写一行，日志文件会被淹没。
        /// </summary>
        private void LogOverwriteLine(string message)
        {
            string line = ToSingleLogLine($"[{DateTime.Now:HH:mm:ss}] [INFO] {message}");
            _logQueue.Enqueue((line, true, null, null));
        }

        /// <summary>
        /// 压成**单行**日志文本（CR/LF → 空格）。
        /// <para>⚠ 关键不变量：**一条日志 = 一个显示行**。链接跳转的"行号 → 条目"侧表、
        /// 行级着色、以及"增量追加只补新行"都建立在这个一一对应上；一条日志里只要混进换行，
        /// 它在 Scintilla 里就会渲染成多行 → 后面所有行的行号整体错位
        /// （实测表现：双击某行却打开了**下一行**的文件、链接着色串行）。</para>
        /// </summary>
        private static string ToSingleLogLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.IndexOf('\r') >= 0) s = s.Replace("\r", " ");
            if (s.IndexOf('\n') >= 0) s = s.Replace("\n", " ");
            return s;
        }

        private void FlushLogQueue() => FlushLogQueue(false);

        /// <summary>
        /// 出队并刷新编辑器日志面板。
        /// <para>非强制调用带**限量**（单次最多 <see cref="LogDequeueBudget"/> 条，剩余交给下一次定时器）
        /// 与**增量渲染**（只 Append 新行，不再全量重建整篇文本）——16 万条记录的包在批量编辑/检测时
        /// 日志可达数万行，全量重建是编辑器内操作卡顿的根因（与主界面 LogView 同款问题）。</para>
        /// </summary>
        private void FlushLogQueue(bool force)
        {
            if (_logScintilla == null || _logScintilla.IsDisposed) return;

            // 操作收尾信号：即使队列已空也要消费一次（封口末组收起），不能因 changed=false 提前返回
            bool finalize = System.Threading.Interlocked.Exchange(ref _logFinalizeRequested, 0) == 1;

            bool changed = false;
            int budget = force ? LogDequeueBudgetForce : LogDequeueBudget;
            while (budget-- > 0 && _logQueue.TryDequeue(out var entry))
            {
                if (entry.isOverwrite && _logEntries.Count > 0)
                {
                    CollapseOpenLogGroup();   // 覆盖行属普通行语义：中断分组
                    _logEntries[_logEntries.Count - 1] = entry.text;
                    if (_logEntryRefs.Count > 0) _logEntryRefs[_logEntryRefs.Count - 1] = entry.relPath;  // 平行元数据同步
                    _logOverwriteDirty = true;   // 增量渲染下只需替换末行文本
                }
                else if (entry.groupKey != null)
                {
                    // 带分组键的警告/错误行：按"连续同名段"归组（跨批次不断组，详见 ProcessGroupedLogLine）
                    ProcessGroupedLogLine(entry.text, entry.groupKey);
                }
                else
                {
                    // 普通行到来 → 封口当前组（含 _logEntries 为空时的覆盖行兜底）
                    CollapseOpenLogGroup();
                    _logEntries.Add(entry.text);
                    _logEntryRefs.Add(entry.relPath);   // 与 _logEntries 严格平行（跳转用）
                    _logRowKinds.Add(LogRowKind.Normal);
                    _logRowKeys.Add(null);
                }
                changed = true;
            }
            if (!_logQueue.IsEmpty)
                _logTimer?.Start();   // 还有积压：定时器继续追平

            if (finalize)
                CollapseOpenLogGroup();   // 收尾：末组封口收起（决策：默认收起）

            // 批次末：开组头行文本按最新计数/级别统一刷新一次（组开着时"N 个问题"实时增长）
            if (_logOpenGroup != null && _logOpenGroup.HeadLineIndex >= 0 && _logOpenGroup.ChildCount > 0)
            {
                string newText = LogFoldSupport.BuildHeaderText(_logOpenGroup);
                if (!string.Equals(_logEntries[_logOpenGroup.HeadLineIndex], newText, StringComparison.Ordinal))
                {
                    _logEntries[_logOpenGroup.HeadLineIndex] = newText;
                    QueueLogHeaderUpdate(_logOpenGroup);
                }
            }

            if (_logEntries.Count > EditorLogMaxEntries)
            {
                // 批量裁剪（仿 LogView）：一次裁掉一大块，摊薄 List 头部移动成本
                int cut = Math.Max(_logEntries.Count - EditorLogMaxEntries, EditorLogMaxEntries / 5);
                _logEntries.RemoveRange(0, cut);
                cut = Math.Min(cut, _logEntryRefs.Count);
                if (cut > 0) _logEntryRefs.RemoveRange(0, cut);   // 元数据同步裁头
                int kindCut = Math.Min(cut, _logRowKinds.Count);
                if (kindCut > 0) _logRowKinds.RemoveRange(0, kindCut);
                int keyCut = Math.Min(cut, _logRowKeys.Count);
                if (keyCut > 0) _logRowKeys.RemoveRange(0, keyCut);
                // 折叠组引用同步平移；头行已被裁掉的组直接废弃（历史概述随头行一起没了，子行按普通行显示）
                if (_logOpenGroup != null)
                {
                    _logOpenGroup.HeadLineIndex -= cut;
                    if (_logOpenGroup.HeadLineIndex < 0) _logOpenGroup = null;
                }
                foreach (var g in _logPendingCollapse) g.HeadLineIndex -= cut;
                _logPendingCollapse.RemoveAll(g => g.HeadLineIndex < 0);
                foreach (var g in _logHeaderUpdates) g.HeadLineIndex -= cut;
                _logHeaderUpdates.RemoveAll(g => g.HeadLineIndex < 0);
                _renderedLogCount = -1;   // 头部被裁 → 已渲染前缀失效，下次全量重建
            }

            if (changed)
                RefreshLogDisplay();
            else if (finalize)
            {
                // 无新行但有收尾信号：仍要渲染一次（消费待收起队列，把末组收起来）
                RefreshLogDisplay();
            }
            else if (force)
            {
                // 无新内容但要求强制刷新：确保视图滚动到最新（清空/收尾场景）
                ScrollLogToLatest();
            }
        }

        // ─────────────────────── 日志折叠分组（与主界面 LogView 同一套方案，见 LogFoldSupport）───────────────────────

        /// <summary>操作收尾信号（线程安全）：封口当前开组并收起（末组不再有后续行，靠此收尾）</summary>
        private void RequestLogFinalize() => System.Threading.Interlocked.Exchange(ref _logFinalizeRequested, 1);

        /// <summary>
        /// 处理带分组键的警告/错误行：与"最近组"显示流相邻且同名 → 续入；否则封口旧组、插入新组头行后作为首条子行。
        /// 组头行是真实行（先于子行进入行集合），参与着色/筛选/导出/行数上限；同名判定走 <see cref="_logRowKeys"/>
        /// （折叠键）而非 <see cref="_logEntryRefs"/>（跳转键）——带 relPath 的普通行不会干扰分组。
        /// </summary>
        private void ProcessGroupedLogLine(string text, string key)
        {
            bool continues = _logOpenGroup != null && _logOpenGroup.HeadLineIndex >= 0
                && _logEntries.Count > 0
                && _logRowKinds[_logEntries.Count - 1] == LogRowKind.GroupChild
                && string.Equals(_logRowKeys[_logEntries.Count - 1], key, StringComparison.Ordinal);

            if (!continues)
            {
                CollapseOpenLogGroup();
                var g = new LogFoldGroup { Key = key, HeadLineIndex = _logEntries.Count, ChildCount = 0 };
                LogFoldSupport.ExtractLeadTags(text, out g.TimeTag, out g.LevelTag);
                _logEntries.Add(LogFoldSupport.BuildHeaderText(g));
                _logEntryRefs.Add(key);                  // 头行的记录名同样可双击跳转
                _logRowKinds.Add(LogRowKind.GroupHeader);
                _logRowKeys.Add(key);
                _logOpenGroup = g;
            }

            var grp = _logOpenGroup;
            grp.ChildCount++;
            // 组内最高级别（ERROR > WARN）：头行标签随组内问题升级，级别筛选/着色随之联动
            LogFoldSupport.ExtractLeadTags(text, out _, out string lineTag);
            if (LogFoldSupport.LevelRank(lineTag) > LogFoldSupport.LevelRank(grp.LevelTag))
                grp.LevelTag = lineTag;

            // 子行行首加 "- " 标记（与组头行区分层级；仅显示层，文件通道的落盘文本不受影响）
            _logEntries.Add("- " + text);
            _logEntryRefs.Add(key);
            _logRowKinds.Add(LogRowKind.GroupChild);
            _logRowKeys.Add(key);
        }

        /// <summary>封口当前组：头行文本按最终计数定稿，待渲染层收起（默认收起策略）。组引用保留——同名相邻行仍可续入。</summary>
        private void CollapseOpenLogGroup()
        {
            var g = _logOpenGroup;
            if (g != null && g.HeadLineIndex >= 0 && g.ChildCount > 0 && !g.Collapsed)
            {
                g.Collapsed = true;
                _logEntries[g.HeadLineIndex] = LogFoldSupport.BuildHeaderText(g);   // 内部行集合先行定稿
                QueueLogHeaderUpdate(g);
                _logPendingCollapse.Add(g);
            }
        }

        /// <summary>登记一个头行更新（渲染层统一 ReplaceTarget + marker 重打 + 链接区间重算）；同批重复登记只留最后一次</summary>
        private void QueueLogHeaderUpdate(LogFoldGroup g)
        {
            if (g == null || g.HeadLineIndex < 0) return;
            if (_logHeaderUpdates.Count == 0 || !ReferenceEquals(_logHeaderUpdates[_logHeaderUpdates.Count - 1], g))
                _logHeaderUpdates.Add(g);
        }

        /// <summary>
        /// 给 [fromIndex, toIndex) 范围的新行设置折叠层级（无过滤时编辑器行号 = 行集合下标）：
        /// 组头行 1025|Header、子行 1026；普通行保持 Scintilla 默认 1024，无需逐行设置。
        /// </summary>
        private void ApplyLogFoldLevelsRange(int fromIndex, int toIndex)
        {
            var s = _logScintilla;
            if (s == null || s.IsDisposed) return;
            int lineCount = s.Lines.Count;
            for (int i = Math.Max(0, fromIndex); i < toIndex && i < lineCount && i < _logRowKinds.Count; i++)
            {
                var kind = _logRowKinds[i];
                if (kind == LogRowKind.GroupHeader)
                {
                    var line = s.Lines[i];
                    line.FoldLevel = LogFoldSupport.FoldLevelHeader;
                    line.FoldLevelFlags = ScintillaNET.FoldLevelFlags.Header;
                }
                else if (kind == LogRowKind.GroupChild)
                {
                    s.Lines[i].FoldLevel = LogFoldSupport.FoldLevelChild;
                }
            }
        }

        /// <summary>
        /// 消费待处理的折叠动作（仿主界面 LogView.ApplyPendingFoldActions，另加头行链接区间重算）：
        /// ① 头行文本更新（ReplaceTarget 同步行文本 + marker 级别重打 + 双击跳转区间重算）；
        /// ② 组首次渲染时应用折叠结构并收起（已封口的组由待收起队列再统一收起）；
        /// ③ 流式追加进"已收起开组"的子行补一对 展开→收起 强制重算（等效"插入即隐藏"）。
        /// </summary>
        private void ApplyPendingLogFoldActions()
        {
            var s = _logScintilla;
            if (s == null || s.IsDisposed) return;

            if (_logHeaderUpdates.Count > 0)
            {
                foreach (var g in _logHeaderUpdates)
                {
                    int idx = g.HeadLineIndex;
                    if (idx < 0 || idx >= s.Lines.Count || idx >= _logEntries.Count) continue;

                    // ① 行文本同步（_logEntries 已是最新文本）。⚠ Scintilla 的 Line.Text/Length/EndPosition
                    //    均【包含行尾换行符】：目标区间一旦含换行符，ReplaceTarget 会把它一并替换掉 ——
                    //    头行与下一行拼接、文档行数永久减一、之后所有按下标定位的头行更新全部漂移（踩过，见 wiki）。
                    var line = s.Lines[idx];
                    string oldText = line.Text ?? string.Empty;
                    if (oldText.EndsWith("\r\n", StringComparison.Ordinal))
                        oldText = oldText.Substring(0, oldText.Length - 2);
                    else if (oldText.EndsWith("\n", StringComparison.Ordinal) || oldText.EndsWith("\r", StringComparison.Ordinal))
                        oldText = oldText.Substring(0, oldText.Length - 1);
                    s.ReadOnly = false;
                    s.TargetStart = line.Position;
                    s.TargetEnd = line.Position + oldText.Length;
                    s.ReplaceTarget(_logEntries[idx]);
                    s.ReadOnly = true;

                    // ② 折叠结构 + 收起：只在组首次渲染时应用一次（组一有子行就收起，操作过程中也保持折叠）。
                    //    头行后续刷新只换文本、不重设折叠 —— 用户手动展开的组不会被下一批刷新强行收回。
                    if (!g.FoldApplied)
                    {
                        LogFoldSupport.ApplyFoldToHeader(s, idx);
                        g.FoldApplied = true;
                    }

                    // ③ marker 重打：头行级别可能随组内问题升级（WARN→ERROR），背景色要跟上
                    //    （编辑器日志的警告/错误 marker 槽位是 3/4，与主界面 LogView 的 0/1 不同）
                    var rel = s.Lines[idx];
                    rel.MarkerDelete(LogMarkerWarn);
                    rel.MarkerDelete(LogMarkerError);
                    int lv = LogLevelFilter.LineLevel(_logEntries[idx]);
                    if (lv == 3) rel.MarkerAdd(LogMarkerError);
                    else if (lv == 2) rel.MarkerAdd(LogMarkerWarn);

                    // ④ 头行文本变了 → 该行的双击跳转区间按新文本重算（记录名在头行文本里，长度已变）
                    if (idx < _logLinkRanges.Count)
                    {
                        ClearLinkIndicatorForLine(idx);
                        string relPath = idx < _logEntryRefs.Count ? _logEntryRefs[idx] : null;
                        var range = BuildLinkRange(idx, relPath);
                        _logLinkRanges[idx] = range;
                        FillLinkIndicator(range);
                    }
                }
                _logHeaderUpdates.Clear();
            }

            if (_logPendingCollapse.Count > 0)
            {
                foreach (var g in _logPendingCollapse)
                {
                    // SCI_FOLDLINE 真收起（会隐藏子行）；SetFoldExpanded 只改标记不藏行，已废弃
                    if (g.HeadLineIndex >= 0 && g.HeadLineIndex < s.Lines.Count)
                        s.Lines[g.HeadLineIndex].FoldLine(ScintillaNET.FoldAction.Contract);
                }
                _logPendingCollapse.Clear();
            }

            // ③ 流式追加的子行必须批量末补收起：组第一次收起那一刻 Scintilla 按"当时范围"藏行，
            //    之后追加的新子行只是把折叠范围拉长，不会被自动并入收起区（保持可见）；
            //    且对已收起的头行直接再发 Contract 是空操作（展开标记未变，跳过重算）。
            //    故对仍收起的开组补一对 展开→收起：两者都按当前完整折叠层级重新计算范围，
            //    净效果 = 新旧子行全部隐藏（等效"插入即隐藏"）；同批次内完成、无绘制窗口、无闪烁。
            //    头行处于展开状态（用户手动展开正在增长的组）时不补，保留用户开合，封口时统一收起。
            if (_logOpenGroup != null && _logOpenGroup.FoldApplied
                && _logOpenGroup.HeadLineIndex >= 0 && _logOpenGroup.HeadLineIndex < s.Lines.Count)
            {
                var head = s.Lines[_logOpenGroup.HeadLineIndex];
                if (!head.Expanded)   // Expanded=账面展开标记（非可见状态）；收起中的组才需要补重算
                {
                    head.FoldLine(ScintillaNET.FoldAction.Expand);     // 先按当前层级显示完整范围（新子行纳入计算）
                    head.FoldLine(ScintillaNET.FoldAction.Contract);   // 再整体收起（新子行一并藏掉）
                }
            }
        }

        /// <summary>
        /// 全量重建后的折叠重投影（仿主界面 LogView.RebuildFoldStructure）：按行类型重设折叠层级，
        /// 再从行集合重新扫描分组（头行文本是权威），默认收起。头部裁剪可能留下"头行被裁、子行残留"的
        /// 孤儿段——按普通内容显示，不折叠（不为其再造头行）。过滤态（filtered）行号与行集合不对应，直接跳过。
        /// </summary>
        private void RebuildLogFoldStructure(bool filtered)
        {
            var s = _logScintilla;
            if (s == null || s.IsDisposed) return;
            if (filtered)
            {
                // 过滤视图不做折叠：待处理动作清掉（取消过滤后的全量重建会从行集合重新投影，不丢状态）
                _logHeaderUpdates.Clear();
                _logPendingCollapse.Clear();
                return;
            }

            int n = Math.Min(s.Lines.Count, Math.Min(_logEntries.Count, Math.Min(_logRowKinds.Count, _logRowKeys.Count)));

            // ① 逐行折叠层级
            for (int i = 0; i < n; i++)
            {
                var kind = _logRowKinds[i];
                if (kind == LogRowKind.GroupHeader)
                {
                    var line = s.Lines[i];
                    line.FoldLevel = LogFoldSupport.FoldLevelHeader;
                    line.FoldLevelFlags = ScintillaNET.FoldLevelFlags.Header;
                }
                else if (kind == LogRowKind.GroupChild)
                {
                    s.Lines[i].FoldLevel = LogFoldSupport.FoldLevelChild;
                }
            }

            // ② 重新扫描分组（头行文本是权威，子行只数个数）→ 默认收起（决策：折叠后不占空间、只显示头行）
            for (int i = 0; i < n; i++)
            {
                if (_logRowKinds[i] != LogRowKind.GroupHeader) continue;

                int j = i + 1;
                while (j < n && _logRowKinds[j] == LogRowKind.GroupChild
                    && string.Equals(_logRowKeys[j], _logRowKeys[i], StringComparison.Ordinal))
                    j++;

                if (j > i + 1)
                    LogFoldSupport.ApplyFoldToHeader(s, i);
                i = j - 1;
            }
        }

        /// <summary>
        /// 日志工具条布局：与主界面 <c>LogView.LayoutToolbar</c> 同一套做法（手工绝对定位，
        /// 左组「四级筛选 + 搜索框」、右组「导出日志、清空日志（最右）」），保证两处观感一致。
        /// </summary>
        private void LayoutLogToolbar(Control headerBar)
        {
            if (headerBar == null || headerBar.IsDisposed) return;
            if (_logLevelFilter == null || _logSearchBox == null || _logClearAllBtn == null || _logExportBtn == null) return;

            int h = headerBar.ClientSize.Height;
            int w = headerBar.ClientSize.Width;
            if (h <= 0 || w <= 0) return;

            int pad = DpiManager.S(4);
            int gap = DpiManager.S(8);

            // 四级筛选的高度跟随右侧按钮实际高度（按钮 AutoSize，高度随字体/DPI 变化）
            int segH = Math.Max(DpiManager.S(26), _logExportBtn.Height);
            if (_logLevelFilter.Height != segH) _logLevelFilter.Height = segH;

            // 右组：清空日志在最右，导出日志在其左侧
            _logClearAllBtn.Location = new Point(Math.Max(0, w - pad - _logClearAllBtn.Width),
                LogLevelFilter.CenterY(_logClearAllBtn, h));
            _logExportBtn.Location = new Point(Math.Max(0, _logClearAllBtn.Left - gap - _logExportBtn.Width),
                LogLevelFilter.CenterY(_logExportBtn, h));
            int rightStart = _logExportBtn.Left;

            // 左组：四级筛选固定宽度，搜索框吃掉中间剩余空间（120..260）
            _logLevelFilter.Location = new Point(pad, LogLevelFilter.CenterY(_logLevelFilter, h));
            int searchX = pad + _logLevelFilter.Width + gap;
            int searchAvail = rightStart - gap - searchX;
            int searchW = Math.Max(DpiManager.S(120), Math.Min(DpiManager.S(260), searchAvail));
            if (_logSearchBox.Width != searchW) _logSearchBox.Width = searchW;
            _logSearchBox.Location = new Point(searchX, LogLevelFilter.CenterY(_logSearchBox, h));
        }

        private void RefreshLogDisplay()
        {
            if (_logScintilla == null || _logScintilla.IsDisposed) return;
            ApplyLogPanelColors();   // 每次刷新重设底色/前景：主题切换后随下一条日志自动跟随

            int target = _logEntries.Count;
            bool filtered = _logLevelIndex > 0 || !string.IsNullOrEmpty(_logSearchText);
            string filterKey = _logLevelIndex + "|" + (_logSearchText ?? "");
            if (!string.Equals(filterKey, _logRenderedFilterKey, StringComparison.Ordinal))
                _renderedLogCount = -1;   // 过滤条件变化 → 可见行与已渲染文本不再对应，必须全量重建

            // ① 覆盖行（瞬态进度）：被覆盖的永远是 _logEntries 末条
            //    · 末条当前不可见 → 界面无需变化（仅清标志，不重建）
            //    · 末条可见且正是已渲染的末行 → 原地替换该行文本
            //    · 其余 → 退回全量重建（保证文本正确）
            if (_logOverwriteDirty && target > 0)
            {
                bool lastVisible = !filtered
                    || LogLevelFilter.Match(_logEntries[target - 1] ?? "", _logLevelIndex, _logSearchText);
                if (!lastVisible)
                {
                    _logOverwriteDirty = false;
                }
                else if (_logLastRenderedIsLastEntry && _logScintilla.Lines.Count > 0)
                {
                    ReplaceLastLogLineText(_logEntries[target - 1] ?? "");
                    _logOverwriteDirty = false;
                }
                else
                {
                    RebuildLogView(filterKey, filtered);
                    _renderedLogCount = target;
                    _logOverwriteDirty = false;
                    ScrollLogToLatest();
                    return;
                }
            }

            // ② 无新增行：仍可能有待消费的折叠动作（头行文本更新 / 收起末组）
            if (_renderedLogCount >= 0 && _renderedLogCount == target)
            {
                if (!filtered) ApplyPendingLogFoldActions();
                else { _logHeaderUpdates.Clear(); _logPendingCollapse.Clear(); }
                ScrollLogToLatest();
                return;
            }

            // ③ 增量追加：无筛选 → 追加全部新增行；筛选态 → 只追加"匹配"的新增行
            if (_renderedLogCount >= 0 && _renderedLogCount < target)
            {
                AppendLogEntries(_renderedLogCount, target, filtered);
                if (!filtered)
                {
                    // 新行的折叠层级（头行/子行）+ 待处理折叠动作（头行文本/首次收起/开组补收起）
                    ApplyLogFoldLevelsRange(_renderedLogCount, target);
                    ApplyPendingLogFoldActions();
                }
                else
                {
                    // 过滤视图不做折叠：待处理动作清掉（取消过滤后的全量重建会从行集合重新投影，不丢状态）
                    _logHeaderUpdates.Clear();
                    _logPendingCollapse.Clear();
                }
                _renderedLogCount = target;
                _logRenderedFilterKey = filterKey;
                ScrollLogToLatest();
                return;
            }

            // ④ 全量重建（清空/裁头/切换筛选之后）
            RebuildLogView(filterKey, filtered);
            _renderedLogCount = target;
            ScrollLogToLatest();
        }

        /// <summary>
        /// 全量重建日志视图：**文本、行级着色、链接区间一律基于同一份"可见行"**
        /// （既修掉"着色用全量行导致串行"的旧缺陷，也保证链接区间与行号严格对齐）。
        /// </summary>
        /// <param name="filtered">是否处于过滤态：过滤视图行号与内部行集合不对应，不做折叠重投影（平铺显示）</param>
        private void RebuildLogView(string filterKey, bool filtered)
        {
            var lines = GetVisibleLogLines();
            _logScintilla.ReadOnly = false;
            _logScintilla.Text = string.Join("\r\n", lines);
            _logScintilla.ReadOnly = true;

            ApplyLogLineColors(lines);
            RebuildLinkRanges(lines);
            RebuildLogFoldStructure(filtered);   // 折叠重投影（Text 重置会清掉全部折叠状态）+ 默认收起

            _logRenderedFilterKey = filterKey;
            _logLastRenderedIsLastEntry = _logEntries.Count > 0 && lines.Count > 0
                && LogLevelFilter.Match(_logEntries[_logEntries.Count - 1] ?? "", _logLevelIndex, _logSearchText);
        }

        /// <summary>
        /// 增量追加 [from,to) 的新增条目（一次插入几百行）；筛选态只追加匹配行。
        /// 追加的同时补行级 marker 与链接区间，避免"每次刷新都全量重建"。
        /// </summary>
        private void AppendLogEntries(int from, int to, bool filtered)
        {
            var newLines = new List<string>();
            var newRefs = new List<string>();
            for (int i = from; i < to; i++)
            {
                string line = _logEntries[i];
                if (filtered && !LogLevelFilter.Match(line, _logLevelIndex, _logSearchText)) continue;
                newLines.Add(line);
                newRefs.Add(i < _logEntryRefs.Count ? _logEntryRefs[i] : null);
            }

            _logLastRenderedIsLastEntry = to == _logEntries.Count && (_logEntries.Count > 0)
                && (!filtered || LogLevelFilter.Match(_logEntries[_logEntries.Count - 1] ?? "", _logLevelIndex, _logSearchText));

            if (newLines.Count == 0) return;

            // 起始行号：空文档从第 0 行起；否则从"当前行数"起（追加时先补一个换行，避免与上一行粘连合并）
            int startLine = _logScintilla.TextLength == 0 ? 0 : _logScintilla.Lines.Count;
            string block = string.Join("\r\n", newLines);
            if (_logScintilla.TextLength > 0) block = "\r\n" + block;

            _logScintilla.ReadOnly = false;
            _logScintilla.AppendText(block);
            _logScintilla.ReadOnly = true;

            ApplyLogLineColorsForAppended(newLines, startLine);

            // 链接区间：与追加行一一对应（若侧表与控件行号不同步则重建一次，保证不错位）
            if (_logLinkRanges.Count != startLine)
            {
                RebuildLinkRanges(GetVisibleLogLines());
                return;
            }
            // 与 RebuildLinkRanges 同一套推进：按每条实际占的显示行数登记，续行填 null
            int docLine = startLine;
            for (int k = 0; k < newLines.Count; k++)
            {
                var range = BuildLinkRange(docLine, newRefs[k]);
                _logLinkRanges.Add(range);
                FillLinkIndicator(range);
                int extra = DisplayLineCount(newLines[k]) - 1;
                for (int m = 0; m < extra; m++) _logLinkRanges.Add(null);
                docLine += 1 + extra;
            }
        }

        /// <summary>原地替换日志面板末行文本（覆盖行/瞬态进度）：按新文本重算该行的链接区间</summary>
        private void ReplaceLastLogLineText(string text)
        {
            if (_logScintilla == null || _logScintilla.IsDisposed) return;
            int lineCount = _logScintilla.Lines.Count;
            if (lineCount <= 0) return;

            var line = _logScintilla.Lines[lineCount - 1];
            _logScintilla.ReadOnly = false;
            _logScintilla.TargetStart = line.Position;
            _logScintilla.TargetEnd = line.EndPosition;
            _logScintilla.ReplaceTarget(text ?? "");
            _logScintilla.ReadOnly = true;

            int lineIndex = _logScintilla.Lines.Count - 1;
            if (lineIndex < 0 || lineIndex >= _logLinkRanges.Count) return;

            string relPath = _logEntryRefs.Count > 0 ? _logEntryRefs[_logEntryRefs.Count - 1] : null;
            ClearLinkIndicatorForLine(lineIndex);
            var range = BuildLinkRange(lineIndex, relPath);
            _logLinkRanges[lineIndex] = range;
            FillLinkIndicator(range);
        }

        // ─────────────────────── 日志行双击跳转（结构化标记 + Scintilla 热点）───────────────────────

        /// <summary>
        /// 该路径是否允许作为"日志行双击跳转"的目标（三道闸门）：
        /// ① **扩展名白名单**：仅可编辑文本类 `.dbr/.tpl/.txt/.lua`（复用 <see cref="TextFileHelper.IsTextFile"/>）；
        /// ② **必须是包内相对路径**：拒绝盘符/反斜杠/前导斜杠/上跳/中间包名
        ///    （临时目录、包文件、备份包、变更报告等路径形态一律被挡在门外）；
        /// ③ **包内确实存在该记录**（<see cref="FindNodeDataByPath"/> 命中）。
        /// </summary>
        private bool IsClickableRecordPath(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            if (relPath.IndexOf('\\') >= 0 || relPath.IndexOf(':') >= 0) return false;
            if (relPath.StartsWith("/", StringComparison.Ordinal)
                || relPath.StartsWith("..", StringComparison.Ordinal)) return false;
            if (relPath.IndexOf(".GDPackEditor-tmp-", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (!TextFileHelper.IsTextFile(relPath)) return false;
            return FindNodeDataByPath(relPath) != null;
        }

        /// <summary>
        /// 在**控件的第 lineIndex 行**里定位路径文字，返回命中区间（不可跳转返回 null）。
        /// <para>⚠ **一律走 Scintilla 自己的文本搜索（`TargetStart/TargetEnd` + `SearchInTarget`），
        /// 不手算任何偏移**：路径是"结构化标记"记下来的数据，直接拿它去本行里搜即可，
        /// 命中后 Scintilla 会把 `TargetStart/TargetEnd` 收缩到命中区间 —— 位置由控件自己给出，
        /// 不会再出现"算出来的偏移和控件口径不一致"这类问题（踩过，见 wiki 5.12）。</para>
        /// <para>位置单位：**字符（UTF-16 code unit）**，不是字节 —— 实测 `GetTextRange(3,1)` 在
        /// "abc中文def" 上返回完整的"中"、`Lines[1].Position` = 7 而非 11（字节值）。
        /// 所以这里**绝不能做 UTF-8 字节换算**（曾误判为字节并改错，导致蓝字整体右移）。</para>
        /// <para>行尾用"**下一行的 Position**"（末行用 `TextLength`）：不依赖 `Line.Length/EndPosition`
        /// 是否含行尾符，边界最省心。</para>
        /// </summary>
        private (string relPath, int start, int len)? BuildLinkRange(int lineIndex, string relPath)
        {
            if (_logScintilla == null || _logScintilla.IsDisposed) return null;
            if (string.IsNullOrEmpty(relPath)) return null;
            if (lineIndex < 0 || lineIndex >= _logScintilla.Lines.Count) return null;
            if (!IsClickableRecordPath(relPath)) return null;

            int lineStart = _logScintilla.Lines[lineIndex].Position;
            int lineEnd = lineIndex + 1 < _logScintilla.Lines.Count
                ? _logScintilla.Lines[lineIndex + 1].Position
                : _logScintilla.TextLength;
            if (lineEnd <= lineStart) return null;

            // 纯文本精确匹配：不当正则、不当整词、不区分大小写也不开（路径大小写固定）
            ScintillaNET.SearchFlags saved = _logScintilla.SearchFlags;
            int found;
            try
            {
                _logScintilla.SearchFlags = ScintillaNET.SearchFlags.None;
                _logScintilla.TargetStart = lineStart;
                _logScintilla.TargetEnd = lineEnd;
                found = _logScintilla.SearchInTarget(relPath);
            }
            finally { _logScintilla.SearchFlags = saved; }
            if (found < 0) return null;

            // 命中后 Scintilla 会把 target 收缩到命中区间 → 直接取，位置由控件给出、无需换算
            int hitStart = _logScintilla.TargetStart;
            int hitLen = _logScintilla.TargetEnd - hitStart;
            if (hitLen <= 0) return null;
            return (relPath, hitStart, hitLen);
        }

        /// <summary>该条日志在 Scintilla 里占几个显示行（文本内的 \n 会把一行拆成多行）</summary>
        private static int DisplayLineCount(string lineText)
        {
            if (string.IsNullOrEmpty(lineText)) return 1;
            int n = 1;
            for (int i = 0; i < lineText.Length; i++)
                if (lineText[i] == '\n') n++;
            return n;
        }

        /// <summary>重建"可见行 → 链接区间"侧表，并按需填充热点指示器（只填可跳转的少数行）</summary>
        private void RebuildLinkRanges(List<string> visibleLines)
        {
            _logLinkRanges.Clear();
            ClearLinkIndicator();
            if (visibleLines == null || visibleLines.Count == 0) return;

            var refs = new List<string>(visibleLines.Count);
            for (int i = 0; i < _logEntries.Count; i++)
            {
                if (_logLevelIndex > 0 || !string.IsNullOrEmpty(_logSearchText))
                {
                    if (!LogLevelFilter.Match(_logEntries[i], _logLevelIndex, _logSearchText)) continue;
                }
                refs.Add(i < _logEntryRefs.Count ? _logEntryRefs[i] : null);
            }

            // ⚠ 侧表按**控件的显示行**索引（双击时用 LineFromPosition 取行号直接查表）。
            // 正常情况一条日志 = 一个显示行；但文本里若混进 \n，该条会占多行 ——
            // 这里按"每条实际占几行"推进行号，续行登记 null（不可跳转），
            // 保证**行号 ↔ 条目**永远对齐（否则会"点这行、打开下一行的文件"）。
            int docLine = 0;
            for (int i = 0; i < visibleLines.Count; i++)
            {
                string relPath = i < refs.Count ? refs[i] : null;
                var range = BuildLinkRange(docLine, relPath);
                _logLinkRanges.Add(range);
                FillLinkIndicator(range);
                int extra = DisplayLineCount(visibleLines[i]) - 1;
                for (int k = 0; k < extra; k++) _logLinkRanges.Add(null);   // 续行：不可跳转
                docLine += 1 + extra;
            }
        }

        /// <summary>填充热点区间（无区间则跳过）；start/len 是**绝对字符位置**（由 <see cref="BuildLinkRange"/> 搜索得出）</summary>
        private void FillLinkIndicator((string relPath, int start, int len)? range)
        {
            if (!range.HasValue || _logScintilla == null || _logScintilla.IsDisposed) return;
            _logScintilla.IndicatorCurrent = LogLinkIndicator;
            _logScintilla.IndicatorFillRange(range.Value.start, range.Value.len);
        }

        /// <summary>清空整个日志控件上的热点区间</summary>
        private void ClearLinkIndicator()
        {
            if (_logScintilla == null || _logScintilla.IsDisposed) return;
            _logScintilla.IndicatorCurrent = LogLinkIndicator;
            _logScintilla.IndicatorClearRange(0, _logScintilla.TextLength);
        }

        /// <summary>清空某行的热点区间</summary>
        private void ClearLinkIndicatorForLine(int lineIndex)
        {
            if (_logScintilla == null || _logScintilla.IsDisposed) return;
            if (lineIndex < 0 || lineIndex >= _logScintilla.Lines.Count) return;
            int start = _logScintilla.Lines[lineIndex].Position;
            // 行尾用"下一行起点"（末行用 TextLength）：`line.Length` 含 2 个行尾符、末行又不含，
            // 用它要两头分支；统一取"下一行 Position"最省心，且末行也不会漏清。
            int end = lineIndex + 1 < _logScintilla.Lines.Count
                ? _logScintilla.Lines[lineIndex + 1].Position
                : _logScintilla.TextLength;
            _logScintilla.IndicatorCurrent = LogLinkIndicator;
            _logScintilla.IndicatorClearRange(start, Math.Max(0, end - start));
        }

        /// <summary>
        /// 日志行双击跳转：命中"路径文字范围"才跳（不是整行）；已删除记录给提示不打开。
        /// 由 <c>HotspotDoubleClick</c>（热点原生事件）与 <c>MouseDoubleClick</c>（兜底）两条路由共同调用。
        /// </summary>
        private void OnLogLinkDoubleClick(int position)
        {
            if (_logScintilla == null || _logScintilla.IsDisposed || position < 0) return;
            if ((DateTime.Now - _logLinkHandledAt).TotalMilliseconds < 300) return;   // 双路由去重

            int line = _logScintilla.LineFromPosition(position);
            if (line < 0 || line >= _logLinkRanges.Count) return;
            var range = _logLinkRanges[line];
            if (!range.HasValue) return;

            int from = range.Value.start;               // 绝对字符位置（鼠标事件的 position 同口径）
            if (position < from || position >= from + range.Value.len) return;   // 必须点中路径文字本身
            _logLinkHandledAt = DateTime.Now;

            string relPath = range.Value.relPath;
            if (_deletedFiles != null && _deletedFiles.Contains(relPath))
            {
                MessageBox.Show(
                    LanguageManager.Instance.GetText("Editor.JumpDeletedRecord", "该记录已删除，无法打开。"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SelectFileInTree(relPath);
        }

        /// <summary>
        /// 滚动到最新一条日志：**纯滚动**（不动光标、不展开折叠），与主界面 LogView.ScrollToEnd 同一套做法。
        /// ⚠ 不能用 GotoPosition(TextLength)：SCI_GOTOPOS 会"自动展开折叠以让该位置可见"，
        /// 末尾收起组的子行会被每次刷新强制撑开（子项闪现），改用 LineScroll 钳制滚到末尾。
        /// </summary>
        private void ScrollLogToLatest()
        {
            _logScintilla.LineScroll(_logScintilla.Lines.Count, 0);
        }

        /// <summary>
        /// 日志面板【当前可见行】：与显示使用同一条过滤链——**级别筛选 + 搜索词**（大小写不敏感包含），
        /// 判定与主界面 LogView 共用 <see cref="LogLevelFilter"/>（先级别后文本，只影响显示）。
        /// 抽成公共方法供「面板刷新」与「导出日志」共用，保证导出内容"所见即所得"。
        /// </summary>
        private List<string> GetVisibleLogLines()
        {
            IEnumerable<string> display = _logEntries;
            if (_logLevelIndex > 0 || !string.IsNullOrEmpty(_logSearchText))
                display = display.Where(l => LogLevelFilter.Match(l, _logLevelIndex, _logSearchText));

            return display as List<string> ?? display.ToList();
        }

        /// <summary>
        /// 刷新日志面板配色（跟随明暗主题）：走 ScintillaSyntax 的统一公共方法。
        /// resetStyles:false —— 只更新配色，不执行 StyleClearAll（否则每次刷新都会清掉行标记底色）。
        /// </summary>
        private void ApplyLogPanelColors()
        {
            ScintillaSyntax.ApplyReadOnlyLog(_logScintilla, FontHelper.MonoFontName, (int)(9f * DpiManager.Scale), resetStyles: false);
            LogFoldSupport.ApplyFoldColors(_logScintilla);   // 折叠符号颜色跟随明暗主题（与 marker 颜色同一刷新机制）
        }

        /// <summary>
        /// 日志面板行级着色：按变更动词与级别给整行加背景标记
        /// （[add] 绿 / [del] 红 / [mod] 黄 / 警告 黄 / 错误 红）。
        /// 颜色每次刷新都重设，因此自动跟随明暗主题切换，无需额外的主题钩子；仅作用于界面，.log 文件保持纯文本。
        /// 色值统一取 ThemeManager 的日志配色（与公共组件 LogView 同一套）。
        /// </summary>
        private void ApplyLogLineColors(IList<string> lines)
        {
            var s = _logScintilla;
            if (s == null || s.IsDisposed || lines == null) return;

            s.Markers[LogMarkerAdd].Symbol = ScintillaNET.MarkerSymbol.Background;
            s.Markers[LogMarkerAdd].SetBackColor(ThemeManager.LogAddBack);
            s.Markers[LogMarkerDel].Symbol = ScintillaNET.MarkerSymbol.Background;
            s.Markers[LogMarkerDel].SetBackColor(ThemeManager.LogDelBack);
            s.Markers[LogMarkerMod].Symbol = ScintillaNET.MarkerSymbol.Background;
            s.Markers[LogMarkerMod].SetBackColor(ThemeManager.LogModBack);
            s.Markers[LogMarkerWarn].Symbol = ScintillaNET.MarkerSymbol.Background;
            s.Markers[LogMarkerWarn].SetBackColor(ThemeManager.LogWarnBack);
            s.Markers[LogMarkerError].Symbol = ScintillaNET.MarkerSymbol.Background;
            s.Markers[LogMarkerError].SetBackColor(ThemeManager.LogErrorBack);

            s.MarkerDeleteAll(LogMarkerAdd);
            s.MarkerDeleteAll(LogMarkerDel);
            s.MarkerDeleteAll(LogMarkerMod);
            s.MarkerDeleteAll(LogMarkerWarn);
            s.MarkerDeleteAll(LogMarkerError);

            int lineCount = s.Lines.Count;
            for (int i = 0; i < lines.Count && i < lineCount; i++)
            {
                int marker = LogLineMarker(lines[i]);
                if (marker >= 0) s.Lines[i].MarkerAdd(marker);
            }
        }

        /// <summary>
        /// 给"刚追加的可见行"补 marker：按 **Scintilla 行号**起算（增量追加用；规则同 <see cref="ApplyLogLineColors"/>）。
        /// </summary>
        private void ApplyLogLineColorsForAppended(IList<string> appendedLines, int startScintillaLine)
        {
            var s = _logScintilla;
            if (s == null || s.IsDisposed || appendedLines == null) return;

            int lineCount = s.Lines.Count;
            for (int k = 0; k < appendedLines.Count; k++)
            {
                int lineIndex = startScintillaLine + k;
                if (lineIndex < 0 || lineIndex >= lineCount) break;
                int marker = LogLineMarker(appendedLines[k]);
                if (marker >= 0) s.Lines[lineIndex].MarkerAdd(marker);
            }
        }

        /// <summary>
        /// 行级 marker 判定（唯一真源，供全量/增量两处复用）：
        /// `[add]/[del]/[mod]` 优先，其次 `[ERROR]/[FATAL]`、`[WARN]`，其余 -1。
        /// </summary>
        private static int LogLineMarker(string l)
        {
            if (string.IsNullOrEmpty(l)) return -1;
            if (l.IndexOf(LogService.VerbAdd, StringComparison.Ordinal) >= 0) return LogMarkerAdd;
            if (l.IndexOf(LogService.VerbDel, StringComparison.Ordinal) >= 0) return LogMarkerDel;
            if (l.IndexOf(LogService.VerbMod, StringComparison.Ordinal) >= 0) return LogMarkerMod;
            if (l.IndexOf("[ERROR]", StringComparison.Ordinal) >= 0
                || l.IndexOf("[FATAL]", StringComparison.Ordinal) >= 0) return LogMarkerError;
            if (l.IndexOf("[WARN]", StringComparison.Ordinal) >= 0) return LogMarkerWarn;
            return -1;
        }

        /// <summary>
        /// 只给从 <paramref name="fromIndex"/> 起的新行补背景标记（增量渲染用；不清已有 marker）。
        /// 行级判定规则与 <see cref="ApplyLogLineColors"/> 完全一致。
        /// </summary>
        private void ApplyLogLineColorsRange(IList<string> lines, int fromIndex)
        {
            var s = _logScintilla;
            if (s == null || s.IsDisposed || lines == null) return;

            int lineCount = s.Lines.Count;
            for (int i = Math.Max(0, fromIndex); i < lines.Count && i < lineCount; i++)
            {
                int marker = LogLineMarker(lines[i]);
                if (marker >= 0) s.Lines[i].MarkerAdd(marker);
            }
        }

        /// <summary>
        /// 导出日志：把日志面板【当前可见】的行（尊重搜索框过滤，变更行自然包含在内）
        /// 另存为 .log。纯文本、与日志格式完全一致，便于 MOD 作者直接用于发布说明。
        /// </summary>
        private void OnLogExportChanges(object sender, EventArgs e)
        {
            var lines = GetVisibleLogLines();
            if (lines.Count == 0)
            {
                MessageBox.Show(
                    LanguageManager.Instance.GetText("Editor.LogExportEmpty", "当前没有可导出的日志。"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string pkgName = System.IO.Path.GetFileName(_archiveFilePath);
            using var dlg = new SaveFileDialog
            {
                Filter = LanguageManager.Instance.GetText("Editor.LogFilterLogFile", "日志文件 (*.log)|*.log|所有文件 (*.*)|*.*"),
                FileName = $"{System.IO.Path.GetFileNameWithoutExtension(pkgName)}-log-{DateTime.Now:yyyyMMdd-HHmmss}.log",
                Title = LanguageManager.Instance.GetText("Editor.LogExportChanges", "导出日志")
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(string.Format(LanguageManager.Instance.GetText("Log.Report.Package", "包：{0}"), pkgName));
                sb.AppendLine(string.Format(LanguageManager.Instance.GetText("Editor.LogExportTime", "导出时间：{0}"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                sb.AppendLine();
                foreach (var l in lines) sb.AppendLine(l);

                File.WriteAllText(dlg.FileName, sb.ToString(), Utf8NoBom);
                LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.LogExportDone", "日志已导出：{0}"),
                    dlg.FileName), "INFO");
            }
            catch (Exception ex)
            {
                LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.LogExportFailed", "导出失败：{0}"), ex.Message), "ERROR");
            }
        }

        private void FlushLogImmediately()
        {
            if (_logScintilla != null && !_logScintilla.IsDisposed && _logScintilla.InvokeRequired)
            {
                _logScintilla.Invoke(new Action(() => FlushLogQueue(force: true)));
            }
            else
            {
                FlushLogQueue(force: true);
            }
        }

        private class LogOnlyProgressCallback : IProgressCallback
        {
            // 面板 + 文件（第 3 参 = 包内路径，供双击跳转；第 4 参 = 折叠分组键，警告行成组用）
            private readonly Action<string, string, string, string> _log;
            private readonly Action<string> _overwrite;
            private readonly Action<string, string> _logPanelOnly;   // 只进面板
            private readonly Action<string, string> _logFileOnly;    // 只落盘
            private readonly Action _finalize;                       // 操作收尾信号（封口末组并收起）

            public LogOnlyProgressCallback(Action<string, string, string, string> log, Action<string> overwrite = null,
                Action<string, string> logPanelOnly = null, Action<string, string> logFileOnly = null,
                Action finalize = null)
            {
                _log = log;
                _overwrite = overwrite;
                _logPanelOnly = logPanelOnly;
                _logFileOnly = logFileOnly;
                _finalize = finalize;
            }

            /// <summary>
            /// 过程行分流（与 <see cref="GuiProgressCallback"/> 同一套语义）：
            /// 「日志显示详情」管**界面**、「详情记录在日志文件中」管**落盘**；
            /// 非过程行始终走双通道；错误/警告永不跳过（一次导入/保存可能上千行过程行，
            /// 默认都不记，避免把 2000 行滚动窗口里的变更记录挤掉）。
            /// </summary>
            private void EmitProcessAware(string message, string level)
            {
                if (!LogService.IsProgressLine(message))
                {
                    _log(message, level, null, null);
                    return;
                }
                if (SettingsManager.ShowDetailLog)
                {
                    if (_logPanelOnly != null) _logPanelOnly(message, level);
                    else _log(message, level, null, null);
                }
                if (SettingsManager.LogDetailToFile)
                    _logFileOnly?.Invoke(message, level);
            }

            public void Report(int percentage, string message) => EmitProcessAware(message, "INFO");
            public void ReportLog(string message) => EmitProcessAware(message, "INFO");
            public void ReportError(string error) => _log(error, "ERROR", null, null);

            /// <summary>groupKey（记录名）直通日志面板 → 折叠分组（仿主界面 LogView）；
            /// 同时兼作 relPath：折叠头行/子行的记录名可直接双击跳转（IsClickableRecordPath 会再做闸门校验）。</summary>
            public void ReportWarning(string warning, string groupKey = null)
                => _log(warning, "WARN", groupKey, groupKey);

            /// <summary>编辑器侧：携带 relPath 的行直通 LogOutput（该行在日志面板可双击跳转）；
            /// 无路径的过程行仍走设置分流（"日志显示详情"管界面、"详情记录在日志文件中"管落盘）。</summary>
            public void ReportWithFile(string message, string level, string relPath)
            {
                if (relPath == null)
                {
                    EmitProcessAware(message, level);
                    return;
                }
                switch ((level ?? string.Empty).ToUpperInvariant())
                {
                    case "ERROR": _log(message, "ERROR", relPath, null); break;
                    case "FATAL":
                    case "WARN": _log(message, "WARN", relPath, relPath); break;
                    default: _log(message, "INFO", relPath, null); break;
                }
            }
            public void LogOverwrite(string message)
            {
                if (_overwrite != null) _overwrite(message);
                else _log(message, "INFO", null, null);
            }
            public void LogFinalize() => _finalize?.Invoke();
            public void UpdateProgress(int percentage, string message) { }
        }

        private CancellationToken GetOperationToken()
        {
            _operationCts?.Cancel();
            _operationCts = new CancellationTokenSource();
            UpdateTerminateButtonState();
            return _operationCts.Token;
        }

        private void OnTerminateClick(object sender, EventArgs e)
        {
            _operationCts?.Cancel();
            LogOutput(LanguageManager.Instance.GetText("Editor.UserCancelled", "用户中止操作"), "WARN");
            UpdateTerminateButtonState();
        }

        private void UpdateTerminateButtonState()
        {
            bool running = _operationCts != null && !_operationCts.IsCancellationRequested;
            if (_terminateButton != null && !_terminateButton.IsDisposed)
            {
                _terminateButton.Enabled = running;
                _terminateButton.ForeColor = running ? ThemeManager.Modified : ThemeManager.Text;
            }
        }

        private void ResetOperationToken()
        {
            _operationCts?.Dispose();
            _operationCts = null;
            UpdateTerminateButtonState();
        }

        private enum OperationResult { Success, Failed, Cancelled }

        /// <summary>最近一次操作失败时的异常（供调用方区分失败原因，如"替换原包文件失败"）。</summary>
        private Exception _lastOperationError;

        /// <summary>本次保存生成的"修改前备份"文件路径（未生成备份时为 null）；写入变更报告便于用户回滚。</summary>
        private string _lastSaveBackupPath;

        private async Task<OperationResult> RunWithCancelAsync(
            string operationLabel,
            Func<IProgressCallback, CancellationToken, bool> backgroundWork,
            Action preWork = null)
        {
            LogOutput($"{operationLabel}{LanguageManager.Instance.GetText("Editor.OpStarted", "开始")}", "INFO");
            _statusLabel.Text = $"{operationLabel}{LanguageManager.Instance.GetText("Editor.OpInProgress", "中...")}";
            var ct = GetOperationToken();
            var progress = new LogOnlyProgressCallback((m, lv, rp, gk) => LogOutput(m, lv, rp, gk), LogOverwriteLine,
                (m, lv) => LogOutputPanelOnly(m),                          // 过程行：只进面板
                (m, lv) => LogService.WriteFileOnly(MapLogLevel(lv), m),   // 过程行：只落盘
                RequestLogFinalize);                                       // 操作收尾：封口末组并收起（折叠）
            OperationResult result = OperationResult.Failed;
            _lastOperationError = null;

            try
            {
                preWork?.Invoke();

                await Task.Run(() =>
                {
                    try
                    {
                        result = backgroundWork(progress, ct) ? OperationResult.Success : OperationResult.Failed;
                    }
                    catch (OperationCanceledException)
                    {
                        result = OperationResult.Cancelled;
                    }
                    catch (Exception ex)
                    {
                        // 兜底捕获：避免异常穿透 async void 调用方导致未处理异常/崩溃，
                        // 并把失败原因记录下来供调用方提示用户
                        result = OperationResult.Failed;
                        _lastOperationError = ex;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                result = OperationResult.Cancelled;
            }
            catch (Exception ex)
            {
                result = OperationResult.Failed;
                _lastOperationError = ex;
            }
            finally
            {
                if (result == OperationResult.Cancelled)
                {
                    _statusLabel.Text = $"{operationLabel}{LanguageManager.Instance.GetText("Editor.OpCancelled", "已取消")}";
                    LogOutput($"{operationLabel}{LanguageManager.Instance.GetText("Editor.OpCancelled", "已取消")}", "WARN");
                }
                else if (result == OperationResult.Failed)
                {
                    string reason = _lastOperationError?.Message;
                    string failText = $"{operationLabel}{LanguageManager.Instance.GetText("Editor.OpFailed", "失败")}";
                    _statusLabel.Text = string.IsNullOrEmpty(reason) ? failText : $"{failText}: {reason}";
                    LogOutput(string.IsNullOrEmpty(reason) ? failText : $"{failText}: {reason}", "ERROR");
                }
                ResetOperationToken();
            }

            return result;
        }

        private void BuildBottomPanel()
        {
            _bottomBarPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                Padding = new Padding(10, 0, 10, 0),
                Back = Color.Transparent
            };

            _cancelButton = new AntdUI.Button
            {
                Name = "Common.Cancel", Text = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                AutoSize = true,
                Font = FontHelper.UiFont(9),
                MinimumSize = new Size(90, 28),
                Dock = DockStyle.Left
            };
            _cancelButton.Click += (s, e) => Close();

            _terminateButton = new AntdUI.Button
            {
                Name = "Editor.Terminate", Text = LanguageManager.Instance.GetText("Editor.Terminate", "终止"),
                AutoSize = true,
                Font = FontHelper.UiFont(9),
                MinimumSize = new Size(90, 28),
                Dock = DockStyle.Left,
                Enabled = false,
                ForeColor = ThemeManager.Modified
            };
            _terminateButton.Click += OnTerminateClick;

            _tombstoneCheckButton = new AntdUI.Button
            {
                Name = "Editor.TombstoneCheck", Text = LanguageManager.Instance.GetText("Editor.TombstoneCheck", "检查墓碑记录"),
                AutoSize = true,
                Font = FontHelper.UiFont(9),
                MinimumSize = new Size(90, 28),
                Dock = DockStyle.Left
            };
            _tombstoneCheckButton.Click += OnCheckTombstoneClick;

            _detectButton = new AntdUI.Button
            {
                Name = "Editor.Detect", Text = LanguageManager.Instance.GetText("Editor.Detect", "检测"),
                AutoSize = true,
                Font = FontHelper.UiFont(9),
                MinimumSize = new Size(90, 28),
                Dock = DockStyle.Left
            };
            _detectButton.Click += OnDetectClick;

            _compactButton = new AntdUI.Button
            {
                Name = "Common.CompactButton", Text = LanguageManager.Instance.GetText("Common.CompactButton", "固实（清理墓碑记录）"),
                AutoSize = true,
                Font = FontHelper.UiFont(9),
                MinimumSize = new Size(90, 28),
                Dock = DockStyle.Left
            };
            _compactButton.Click += OnCompactClick;

            _saveButton = new AntdUI.Button
            {
                Name = "Common.Save", Text = LanguageManager.Instance.GetText("Common.Save", "保存"),
                AutoSize = true,
                MinimumSize = new Size(90, 28),
                Type = AntdUI.TTypeMini.Primary,
                Font = new Font(Font, FontStyle.Bold),
                Dock = DockStyle.Left
            };
            _saveButton.Click += OnSaveClick;

            _backupCheckBox = new AntdUI.Checkbox
            {
                Name = "Editor.BackupOriginal", Text = LanguageManager.Instance.GetText("Editor.BackupOriginal", "备份原文件"),
                AutoSize = true,
                Checked = SettingsManager.BackupOriginalFile,
                Margin = new Padding(20, 0, 0, 0),
                Dock = DockStyle.Left
            };
            _backupCheckBox.CheckedChanged += OnBackupOriginalChanged;

            _bottomBarPanel.Controls.Add(_saveButton);
            _bottomBarPanel.Controls.Add(_backupCheckBox);
            _bottomBarPanel.Controls.Add(_compactButton);
            _bottomBarPanel.Controls.Add(_tombstoneCheckButton);
            _bottomBarPanel.Controls.Add(_detectButton);   // 反向 Dock：落在「检查墓碑记录」右侧
            _bottomBarPanel.Controls.Add(_terminateButton);
            _bottomBarPanel.Controls.Add(_cancelButton);
        }

        // ======================== Archive Loading ========================

        private void LoadArchive()
        {
            try
            {
                _statusLabel.Text = LanguageManager.Instance.GetText("Common.StatusLabel.Parsing", "解析中...");
                LogOutput(LanguageManager.Instance.GetText("Editor.Parsing", "解析中..."), "INFO");
                Cursor = Cursors.WaitCursor;

                _fileDescriptionCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // 清理上次异常退出遗留的同目录中间包（仅删除本工具命名规则且超过一天的，避免影响其它实例）
                SessionPackageWriter.CleanupStaleTempPackages(_archiveFilePath);

                _archiveStream = new FileStream(_archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

                List<(string name, DateTime fileTime, int index, string recordType)> entriesWithMeta;

                if (_isArz)
                {
                    var arzFile = new ARZFile();
                    arzFile.ReadStream(_archiveStream);
                    _arzReader = arzFile.Reader;
                    int total = _arzReader.Count;
                    entriesWithMeta = new List<(string, DateTime, int, string)>();
                    for (int i = 0; i < total; i++)
                    {
                        string name = _arzReader.GetRecordName(i);
                        var info = _arzReader.GetRecordInfo(i);
                        entriesWithMeta.Add((name, info.rdFileTime.ToLocalTime(), i, info.rtype));
                    }
                }
                else
                {
                    _arcFile = new ARCFile();
                    // 读侧名字编码判定结论写入编辑器日志（仅包内含非 ASCII 名时才会输出）
                    _arcFile.NameReporter = t => LogOutput(t, "INFO");
                    _arcFile.ReadStream(_archiveStream);
                    int total = _arcFile.toc.Count;
                    entriesWithMeta = new List<(string, DateTime, int, string)>();
                    for (int i = 0; i < total; i++)
                    {
                        string name = _arcFile.toc[i].GetEntryString(_arcFile.strs);
                        entriesWithMeta.Add((name, _arcFile.toc[i].FileTime, i, Path.GetExtension(name)));
                    }
                }

                _originalRootNodes = GuiMainForm.BuildHierarchicalStructureStatic(entriesWithMeta);
                PopulateFileTree(_originalRootNodes);
                RecalculateAllNodeDataCounts();

                // Load templates (使用 TemplateEntries 确保内置/ARC/文件夹模板全部加载)
                var (templates, _, _, tplSources, _) = Program.LoadTemplates(SettingsManager.TemplateEntries);
                _templates = templates;
                // 模板来源汇总（与打包侧同一格式/同一实现）：打开包就能看到"当前用的是哪几套模板、各生效多少"
                try { LogOutput(Program.FormatTemplateSources(tplSources, templates.Count), "INFO"); } catch { }

                // 包上下文：变更记录必须能追溯到所属包。打开成功后输出一条 [PACKAGE] 边界行，
                // 之后的变更行按时间顺序即归属该包（不在每条变更行里重复包名，避免冗余）。
                // 行内不放条目数：下一条"已加载…"已含条目数，且该文案需多语言；改为文件大小（单位国际通用，天然免多语言）。
                string pkgName = System.IO.Path.GetFileName(_archiveFilePath);
                string pkgSize = LogService.FileSizeText(_archiveFilePath);
                int loadedEntryCount = entriesWithMeta.Count(e => !string.IsNullOrEmpty(e.name));
                LogService.PackageContext = string.IsNullOrEmpty(pkgSize) ? pkgName : $"{pkgName} | {pkgSize}";
                LogOutput($"{LogService.PackageMarker} {pkgName} | {pkgSize} | {_archiveFilePath}", "INFO");

                string loadedMsg = string.Format(
                    LanguageManager.Instance.GetText("Editor.LoadedInfo", "已加载 {0}：{1} 个条目"),
                    pkgName, loadedEntryCount);
                _statusLabel.Text = loadedMsg;
                LogOutput(loadedMsg, "INFO");
                ExitSearchFileListView();
                ShowFileList();
                if (_flatViewMode)
                {
                    BuildFlatTree();
                    RefreshFileListAll();
                }
                else
                {
                    RefreshFileList();
                }
                RefreshFavoritesList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(LanguageManager.Instance.GetText("Editor.LoadError", "加载文件失败: {0}"), ex.Message),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Error", "错误"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                BeginInvoke((Action)Close);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void PopulateFileTree(List<NodeData> rootNodes)
        {
            _fileTree.Items.Clear();
            _fileTreeRootNodes = rootNodes.Select(nd => nd.DeepCopy()).ToList();
            var sorted = _fileTreeRootNodes
                .OrderBy(nd => nd.IsDirectory ? 0 : 1)
                .ThenBy(nd => nd.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string rootName = _archiveFilePath != null
                ? Path.GetFileName(_archiveFilePath)
                : LanguageManager.Instance.GetText("Editor.ArchiveRoot", "档案");
            var rootNode = new AntdUI.TreeItem() { Text = rootName, Tag = null };
            _fileTree.Items.Add(rootNode);

            foreach (var nd in sorted)
            {
                rootNode.Sub.Add(CreateTreeNode(nd, ""));
            }
            rootNode.Expand = true;

            BuildFileNodeMap();
            RefreshFileTreeColors();
            RefreshFileAnnotations();
        }

        private void LoadNewArchive(string newPath)
        {
            _archiveStream?.Dispose();
            _archiveFilePath = newPath;
            string ext = Path.GetExtension(newPath)?.ToLower();
            _isArz = ext == ".arz";
            // 先释放旧会话（含临时目录与 owner 标记），否则旧临时目录会残留到进程退出
            _session?.Dispose();
            _session = new TempSessionManager(Path.GetFileName(newPath));
            Text = $"{Path.GetFileName(newPath)} - " +
                LanguageManager.Instance.GetText("Editor.Title", "编辑器");
            _currentEditorMode = EditorMode.None;
            _currentFolderPath = null;
            _currentFileRelPath = null;
            _addedFiles.Clear();
            _deletedFiles.Clear();
            _modifiedFiles.Clear();
            _modifiedDbrFields.Clear();
            _deletedFileRecordIndices.Clear();
            _fileNodeMap.Clear();
            // 换包 → 行显示文本缓存（描述/本地化名/模板）内容来源已变，整体作废
            _fileDisplayTextCache.Clear();
            _allKnownPaths?.Clear();
            _allFolderPaths?.Clear();
            _fileTree.Items.Clear();
            _folderTree.Items.Clear();
            _fileListGrid.DataSource = null;
            ResetFileListSelectionState();
            _currentVariables?.Clear();
            _originalRootNodes?.Clear();
            ShowPlaceholder();
            LoadArchive();
        }

        private AntdUI.TreeItem CreateTreeNode(NodeData data, string parentRelPath = null)
        {
            var node = new AntdUI.TreeItem();
            string relPath = parentRelPath != null ? parentRelPath + "/" + data.Name : data.Name;
            bool showDate = data.FileTime.HasValue;
            node.Text = data.IsDirectory
                ? $"{data.Name} [{data.TotalItemCount}]{GetAnnotationSuffix(data.Name)}"
                : showDate
                    ? $"{data.Name}  {data.FileTime.Value:yyyy-MM-dd HH:mm:ss}{GetFileAnnotationSuffix(relPath)}"
                    : $"{data.Name}{GetFileAnnotationSuffix(relPath)}";
            node.Tag = data;
            if (data.IsDirectory && data.Children.Count > 0)
            {
                node.Sub.Add(new AntdUI.TreeItem() {
                    Text = LanguageManager.Instance.GetText("Common.Loading", "加载中...")
                });
            }
            return node;
        }

        private void PopulateChildren(AntdUI.TreeItem treeNode)
        {
            var data = treeNode.Tag as NodeData;
            if (data == null) return;
            treeNode.Sub.Clear();
            var sorted = data.Children
                .OrderBy(child => child.IsDirectory ? 0 : 1)
                .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string parentRelPath = GetNodePath(treeNode);
            foreach (var child in sorted)
                treeNode.Sub.Add(CreateTreeNode(child, parentRelPath));
            BuildFileNodeMap();
            RefreshFileTreeColors();
            RefreshFileAnnotations();
        }

        // ======================== File Tree Events ========================

        private void OnFileTreeNodeClick(object sender, AntdUI.TreeSelectEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                _fileTree.SelectItem = e.Item;
                _contextMenuNode = e.Item;
                ShowContextMenu(_fileTree, new Point(e.X, e.Y));
                return;
            }
            // 左键单击仅选中（AntdUI.Tree 默认行为），打开/展开统一由双击处理
        }

        private void OnFileTreeNodeDoubleClick(object sender, AntdUI.TreeSelectEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var data = e.Item.Tag as NodeData;
            if (data == null || data.IsDirectory) return;
            string relPath = GetNodePath(e.Item);
            if (data.RecordIndex.HasValue)
                OpenFileForEditing(data, relPath);
            else
                OpenNewBlankFile(relPath);
        }

        private void OnFileTreeBeforeExpand(object sender, AntdUI.TreeExpandEventArgs e)
        {
            var data = e.Item.Tag as NodeData;
            if (data == null || !data.IsDirectory) return;
            if (e.Item.Sub != null && e.Item.Sub.Count == 1 &&
                e.Item.Sub[0].Text == LanguageManager.Instance.GetText("Common.Loading", "加载中..."))
            {
                PopulateChildren(e.Item);
            }
        }

        private void OnFileTreeMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (_contextMenuNode == null)
                {
                    _fileTree.SelectItem = null;
                }
                ShowContextMenu(_fileTree, e.Location);
            }
        }

        // ======================== Folder-Only Tree Events ========================

        private void OnFolderTreeBeforeExpand(object sender, AntdUI.TreeExpandEventArgs e)
        {
            e.Item.Icon = _openFolderIcon;
        }

        private void OnFolderTreeNodeClick(object sender, AntdUI.TreeSelectEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 单击选中由 AntdUI 内置处理；这里仅加载该文件夹的文件列表，不手动展开/收缩
                ExitSearchFileListView();
                _currentFolderPath = GetFolderTreePath(e.Item);
                ShowFileList();
                RefreshFileList();
            }
            else if (e.Button == MouseButtons.Right)
            {
                _folderTree.SelectItem = e.Item;
                _contextMenuPath = GetFolderTreePath(e.Item);
                _contextMenuNode = e.Item.Tag as AntdUI.TreeItem;
                if (_contextMenuNode == null)
                {
                    _contextMenuNode = FindOrCreateLazyNode(_contextMenuPath);
                    if (_contextMenuNode != null)
                        e.Item.Tag = _contextMenuNode;
                }
                _rightClickOnNode = true;
                ShowContextMenu(_folderTree, new Point(e.X, e.Y));
            }
        }

        private void OnFolderTreeNodeDoubleClick(object sender, AntdUI.TreeSelectEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // 展开动画由 AntdUI 内置处理，这里负责加载该文件夹的文件列表
            ExitSearchFileListView();
            _currentFolderPath = GetFolderTreePath(e.Item);
            ShowFileList();
            RefreshFileList();
        }

        private void OnFolderTreeMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (_rightClickOnNode)
                {
                    _rightClickOnNode = false;
                    return; // 已由 OnFolderTreeNodeClick 处理
                }
                _contextMenuNode = null;
                _contextMenuPath = null;
                _folderTree.SelectItem = null;
                ShowContextMenu(_folderTree, e.Location);
            }
            else
            {
                _rightClickOnNode = false;
            }
        }

        private static string GetNodePath(AntdUI.TreeItem node)
        {
            var parts = new List<string>();
            var cur = node;
            while (cur != null)
            {
                var nd = cur.Tag as NodeData;
                if (nd != null) parts.Add(nd.Name);
                cur = cur.ParentItem;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string GetFolderTreePath(AntdUI.TreeItem node)
        {
            var parts = new List<string>();
            var cur = node;
            while (cur.ParentItem != null)
            {
                string name = GetFolderTreeNodeName(cur);
                if (string.IsNullOrEmpty(name)) break;
                parts.Add(name);
                cur = cur.ParentItem;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string GetFolderTreeNodeName(AntdUI.TreeItem folderNode)
        {
            // 文件夹树节点的 Tag 指向文件树的对应 TreeItem
            var fileNode = folderNode.Tag as AntdUI.TreeItem;
            if (fileNode != null)
            {
                var data = fileNode.Tag as NodeData;
                if (data != null && !string.IsNullOrEmpty(data.Name))
                    return data.Name;
            }
            // Tag 未设置时（如 lazy loading 未展开的目录），从 Text 剥离 [N](备注) 后缀
            string text = folderNode.Text;
            int bracket = text.IndexOf(" [", StringComparison.Ordinal);
            if (bracket > 0)
                return text[..bracket];
            return text;
        }

        private AntdUI.TreeItem FindInternalNodeByPath(string path)
        {
            if (_fileTree.Items.Count == 0) return null;
            if (string.IsNullOrEmpty(path))
                return _fileTree.Items[0];
            string[] parts = path.Split('/');
            AntdUI.TreeItem current = _fileTree.Items[0];
            foreach (var part in parts)
            {
                var child = current.Sub
                    .Cast<AntdUI.TreeItem>()
                    .FirstOrDefault(n =>
                    {
                        var nd = n.Tag as NodeData;
                        return nd != null && string.Equals(nd.Name, part, StringComparison.OrdinalIgnoreCase);
                    });
                if (child == null) return null;
                current = child;
            }
            return current;
        }

        private AntdUI.TreeItem FindOrCreateLazyNode(string path)
        {
            if (_fileTree.Items.Count == 0) return null;
            if (string.IsNullOrEmpty(path))
                return _fileTree.Items[0];

            string[] parts = path.Split('/');
            AntdUI.TreeItem current = _fileTree.Items[0];
            NodeData currentData = current.Tag as NodeData;

            foreach (var part in parts)
            {
                var child = current.Sub
                    .Cast<AntdUI.TreeItem>()
                    .FirstOrDefault(n =>
                    {
                        var nd = n.Tag as NodeData;
                        return nd != null && string.Equals(nd.Name, part, StringComparison.OrdinalIgnoreCase);
                    });

                if (child != null)
                {
                    current = child;
                    currentData = current.Tag as NodeData;
                    continue;
                }

                if (currentData == null) return null;
                NodeData childData = currentData.Children
                    .FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
                if (childData == null) return null;

                string parentRelPath = GetNodePath(current);
                child = CreateTreeNode(childData, parentRelPath);
                current.Sub.Add(child);

                current = child;
                currentData = childData;
            }

            return current;
        }

        // ======================== Opening Files ========================

        private void OpenFileForEditing(NodeData data, string relPath)
        {
            if (data == null || data.IsDirectory || !data.RecordIndex.HasValue) return;

            if (!ConfirmExitMultiFileForOpen(relPath)) return;

            SaveCurrentEditorIfDirty();

            _isLoadingFile = true;
            _currentFileRelPath = relPath;
            _currentFilePathLabel.Text = relPath;
            _currentFilePathLabel.Visible = true;
            _favoriteButton.Visible = true;
            UpdateFavoriteButtonState();
            _editorPanel.PerformLayout();
            _editorDirty = false;

            try
            {
                if (TemplateHelper.IsEditable(relPath))
                {
                    string ext = Path.GetExtension(relPath);
                    if (ext.Equals(".dbr", StringComparison.OrdinalIgnoreCase))
                        LoadDbrForEditing(data, relPath);
                    else
                        LoadSourceForEditing(data, relPath);
                }
                else
                {
                    var lang = LanguageManager.Instance;
                    _placeholderLabel.Text = lang.GetText("Editor.UnsupportedFileType",
                        "不支持编辑此文件类型");
                    HideAllEditors();
                    _placeholderLabel.Visible = true;
                    _currentEditorMode = EditorMode.None;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(LanguageManager.Instance.GetText("Editor.OpenError", "打开文件失败: {0}"), ex.Message),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Error", "错误"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                ShowPlaceholder();
            }
            finally
            {
                _isLoadingFile = false;
            }
        }

        private void OpenNewBlankFile(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return;

            if (!ConfirmExitMultiFileForOpen(relPath)) return;

            SaveCurrentEditorIfDirty();

            _isLoadingFile = true;
            _currentFileRelPath = relPath;
            _currentFilePathLabel.Text = relPath;
            _currentFilePathLabel.Visible = true;
            _favoriteButton.Visible = true;
            UpdateFavoriteButtonState();
            _editorPanel.PerformLayout();
            _editorDirty = false;

            try
            {
                string ext = Path.GetExtension(relPath);
                if (ext.Equals(".dbr", StringComparison.OrdinalIgnoreCase))
                {
                    // 尝试用 DBR 编辑器（读 temp，找模板）
                    var dummyData = new NodeData
                    {
                        Name = GetFileName(relPath),
                        IsDirectory = false,
                        RecordIndex = null
                    };
                    LoadDbrForEditing(dummyData, relPath);
                }
                else if (TemplateHelper.IsEditable(relPath))
                {
                    string tempFile = _session.GetTempFilePath(relPath);
                    string content = File.Exists(tempFile)
                        ? EncodingService.ReadTextFile(tempFile)
                        : "";

                    ShowSourceEditor(content);
                }
                else
                {
                    var lang = LanguageManager.Instance;
                    _placeholderLabel.Text = lang.GetText("Editor.UnsupportedFileType",
                        "不支持编辑此文件类型");
                    HideAllEditors();
                    _placeholderLabel.Visible = true;
                    _currentEditorMode = EditorMode.None;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(LanguageManager.Instance.GetText("Editor.OpenError", "打开文件失败: {0}"), ex.Message),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Error", "错误"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                ShowPlaceholder();
            }
            finally
            {
                _isLoadingFile = false;
            }
        }

        private void LoadReadOnlyViewFromTemp(string relPath)
        {
            string tempFile = _session.GetTempFilePath(relPath);
            string text = File.Exists(tempFile)
                ? EncodingService.ReadTextFile(tempFile)
                : "";

            HideAllEditors();
            _currentEditorMode = EditorMode.ReadOnly;
            _readOnlyView.Text = text;
            _readOnlyView.Visible = true;
            BeginInvoke((Action)(() =>
            {
                _readOnlyView.Select(0, 0);
                _readOnlyView.ScrollToCaret();
            }));
        }

        // ======================== DBR Editor ========================

        private void LoadDbrForEditing(NodeData data, string relPath)
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                string[] lines = null;
                string tempFile = _session.GetTempFilePath(relPath);
                if (File.Exists(tempFile))
                {
                    string text = EncodingService.ReadTextFile(tempFile);
                    lines = text.Replace("\r\n", "\n").Split('\n');
                }
                else if (_isArz)
                {
                    var record = _arzReader.GetRecord(data.RecordIndex.Value);
                    using (var ms = new MemoryStream())
                    {
                        record.SaveToStream(ms);
                        ms.Position = 0;
                        using (var sr = new StreamReader(ms, Utf8NoBom))
                        {
                            string all = sr.ReadToEnd();
                            lines = all.Replace("\r\n", "\n").Split('\n');
                        }
                    }
                }
                else
                {
                    var entry = _arcFile.toc[data.RecordIndex.Value];
                    using (var ms = new MemoryStream())
                    {
                        _arcFile.UnpackToStream(entry, ms);
                        string text = TemplateHelper.DecodeText(ms.ToArray());
                        lines = text.Replace("\r\n", "\n").Split('\n');
                    }
                }

                string tplName = TemplateHelper.GetDbrTemplateName(lines);
                _currentDbrTplName = tplName;
                // 存储原始 DBR 逐行内容（跳过 templateName 行），用于保存时覆盖匹配，保留空行与缩进
                _originalDbrLines = new List<string>();
                for (int i = 1; i < lines.Length; i++)
                {
                    _originalDbrLines.Add(lines[i]);
                }
                TemplateNode rootTpl = null;
                if (tplName != null)
                {
                    // 统一解析口径（TemplateNameResolver）：兼容 mods/<mod名>/database/templates/x.tpl 等非标准写法
                    rootTpl = TemplateNameResolver.Resolve(_templates, tplName).Template;
                }

                if (rootTpl == null)
                {
                    Cursor = Cursors.Default;
                    LoadSourceForEditing(data, relPath);
                    return;
                }

                var dbrValues = TemplateHelper.ParseDbrLines(lines);
                _currentVariables = TemplateHelper.CollectAllVariables(rootTpl, dbrValues);
                _notInTemplateVars = TemplateHelper.CollectNotInTemplateVariables(dbrValues, _currentVariables);
                _currentTemplate = rootTpl;

                ShowDbrGrid(_currentVariables, rootTpl);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void ShowDbrGrid(List<TemplateVariableInfo> variables, TemplateNode rootTpl)
        {
            HideAllEditors();
            _currentEditorMode = EditorMode.DbrGrid;

            EnsureBaseColumns();

            // 值列列名改为当前文件名（为多文件扩展铺路）
            var fvColumn = _dbrGrid.Columns.FirstOrDefault(c => c.Key == "FieldValue");
            if (fvColumn != null)
            {
                string fileName = string.IsNullOrEmpty(_currentFileRelPath)
                    ? ""
                    : Path.GetFileName(_currentFileRelPath);
                fvColumn.Title = string.IsNullOrEmpty(fileName)
                    ? LanguageManager.Instance.GetText("Editor.FieldValue", "数值")
                    : fileName;
            }

            _currentOutlineTag = "__non_zero__";
            _fieldFilterTextBox.Text = "";
            _dbrSplit.Visible = true;
            _editorTabControl.Visible = true;
            _editorTabControl.SelectedIndex = 1;
            _dbrSourceEditor.Visible = true;
            if (_sourceNoFilePlaceholder != null)
                _sourceNoFilePlaceholder.Visible = false;
            BuildOutlineTree(variables, rootTpl);
            RefreshDbrGrid();
            // 等布局完成后再设分割器位置并聚焦筛选框
            BeginInvoke((Action)(() =>
            {
                if (SettingsManager.EditorOutlinePanelCollapsed)
                {
                    _dbrSplit.Collapse();
                    _dbrSplit.Panel1MinSize = 0;
                    _dbrSplit.SplitterDistance = 0;
                }
                else
                {
                    int desired = (int)(200 * DpiManager.Scale);
                    int maxDist = _dbrSplit.Width - _dbrSplit.Panel2MinSize;
                    if (desired >= _dbrSplit.Panel1MinSize && desired <= maxDist)
                        _dbrSplit.SplitterDistance = desired;
                }
                _fieldFilterTextBox?.Focus();
            }));
        }

        private static bool IsNonZeroValue(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return false;
            string trimmed = val.Trim().Trim('\uFEFF', '\u200B', '\u200C', '\u200D', '\u00A0');
            if (string.IsNullOrWhiteSpace(trimmed)) return false;
            if (decimal.TryParse(trimmed,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out decimal d) && d == 0m)
                return false;
            return true;
        }

        /// <summary>
        /// 多文件比较：以基准文件(_multiFileRelPaths[0])为基准，任一非基准文件的值与该基准不同即视为不同。
        /// 判定口径与单元格标红逻辑完全一致（"无此字段"也视为与基准不同）。
        /// </summary>
        private bool IsFieldDifferent(TemplateVariableInfo v)
        {
            if (_multiFileRelPaths.Count <= 1) return false;
            string baseVal = GetFileFieldValue(_multiFileRelPaths[0], v.Name);
            for (int i = 1; i < _multiFileRelPaths.Count; i++)
            {
                string val = GetFileFieldValue(_multiFileRelPaths[i], v.Name);
                if (!string.Equals(val, baseVal, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 在大纲树中递归查找指定 Tag 的节点（特殊分组/模板分组现在为两级结构）。
        /// </summary>
        private static AntdUI.TreeItem FindOutlineItem(AntdUI.TreeItemCollection items, string tag)
        {
            foreach (AntdUI.TreeItem item in items)
            {
                if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                    return item;
                if (item.Sub != null && item.Sub.Count > 0)
                {
                    var found = FindOutlineItem(item.Sub, tag);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private void BuildOutlineTree(List<TemplateVariableInfo> vars, TemplateNode rootTpl)
        {
            _outlineTree.Items.Clear();

            bool multiMode = _multiFileRelPaths.Count > 1;

            // 一级：特殊分组
            var specialGroup = new AntdUI.TreeItem(LanguageManager.Instance.GetText("Editor.SpecialGroups", "特殊分组"))
            {
                Tag = "__special__",
                Expand = true
            };
            _outlineTree.Items.Add(specialGroup);

            var allNode = new AntdUI.TreeItem(LanguageManager.Instance.GetText("Editor.AllFields", "全部显示"));
            allNode.Tag = "__all__";
            specialGroup.Sub.Add(allNode);

            // "不在模板中"的字段按正常字段口径参与计数（用户定稿：全量显示 DBR 内容，正常筛选计数）
            int withValueCount = vars.Count(v => !string.IsNullOrEmpty(v.Value))
                + (_notInTemplateVars?.Count(v => v != null && !string.IsNullOrEmpty(v.Value)) ?? 0);
            var hasValueNode = new AntdUI.TreeItem(
                string.Format(LanguageManager.Instance.GetText("Editor.FieldsWithValue", "有数值(含0) ({0})"), withValueCount));
            hasValueNode.Tag = "__with_value__";
            specialGroup.Sub.Add(hasValueNode);

            int nonZeroCount = vars.Count(v => v.Type != "bool" && IsNonZeroValue(v.Value))
                + (_notInTemplateVars?.Count(v => v != null && v.Type != "bool" && IsNonZeroValue(v.Value)) ?? 0);
            var nonZeroNode = new AntdUI.TreeItem(
                string.Format(LanguageManager.Instance.GetText("Editor.NonZeroFields", "有数值(不含0) ({0})"), nonZeroCount));
            nonZeroNode.Tag = "__non_zero__";
            specialGroup.Sub.Add(nonZeroNode);

            // "不在模板中"：DBR 有而模板无的字段（独立于模板变量集计数，不并入上面两个"有数值"分组）。
            // 分组节点保持正常色——特殊颜色只用于表格行（用户定稿）
            int notplCount = _notInTemplateVars?.Count(v => v != null) ?? 0;
            var notplNode = new AntdUI.TreeItem(
                string.Format(LanguageManager.Instance.GetText("Editor.FieldsNotInTemplate", "不在模板中 ({0})"), notplCount))
            { Tag = TemplateHelper.NotInTemplateGroupName };
            specialGroup.Sub.Add(notplNode);

            // 仅不同/仅相同：多文件模式带计数可点；单文件模式灰显、不带计数、不可点击
            string diffText = LanguageManager.Instance.GetText("Editor.OnlyDiff", "仅不同-多文件 ({0})");
            string sameText = LanguageManager.Instance.GetText("Editor.OnlySame", "仅相同-多文件 ({0})");
            if (multiMode)
            {
                int diffCount = vars.Count(IsFieldDifferent);
                diffText = string.Format(diffText, diffCount);
                sameText = string.Format(sameText, vars.Count - diffCount);
            }
            var diffNode = new AntdUI.TreeItem(diffText) { Tag = "__diff__" };
            var sameNode = new AntdUI.TreeItem(sameText) { Tag = "__same__" };
            if (!multiMode)
            {
                diffNode.Fore = ThemeManager.Deleted;
                sameNode.Fore = ThemeManager.Deleted;
            }
            specialGroup.Sub.Add(diffNode);
            specialGroup.Sub.Add(sameNode);

            // 一级：模板分组
            var tplGroup = new AntdUI.TreeItem(LanguageManager.Instance.GetText("Editor.TemplateGroups", "模板分组"))
            {
                Tag = "__template__",
                Expand = true
            };
            _outlineTree.Items.Add(tplGroup);

            _modifiedDbrFields.TryGetValue(_currentFileRelPath, out var modFields);
            BuildOutlineTreeRecurse(rootTpl, "", tplGroup.Sub, vars, modFields);

            // 若全部字段中有被修改的字段也将特殊分组标红
            if (modFields != null && modFields.Count > 0)
            {
                allNode.Fore = ThemeManager.Modified;
                if (vars.Any(v => !string.IsNullOrEmpty(v.Value) && modFields.Contains(v.Name)))
                    hasValueNode.Fore = ThemeManager.Modified;
                if (vars.Any(v => v.Type != "bool" && IsNonZeroValue(v.Value) && modFields.Contains(v.Name)))
                    nonZeroNode.Fore = ThemeManager.Modified;
            }

            _outlineTree.SelectItem = FindOutlineItem(_outlineTree.Items, _currentOutlineTag) ?? allNode;
        }

        private void BuildOutlineTreeRecurse(TemplateNode node, string parentPath, AntdUI.TreeItemCollection parentNodes, List<TemplateVariableInfo> vars, HashSet<string> modFields)
        {
            foreach (var sub in node.subitems)
            {
                if (sub.kind != "group") continue;
                string gName = sub.values.TryGetValue("name", out var gn) ? gn : "";
                if (string.IsNullOrEmpty(gName)) continue;
                string gPath = string.IsNullOrEmpty(parentPath) ? gName : parentPath + "/" + gName;

                // 去重：同一层已有同名分组则跳过（include 可能引入同名分组）
                bool duplicate = false;
                foreach (AntdUI.TreeItem existing in parentNodes)
                {
                    if (string.Equals(existing.Tag as string, gPath, StringComparison.OrdinalIgnoreCase))
                    { duplicate = true; break; }
                }
                if (duplicate) continue;

                int count = CountVarsInGroup(vars, gPath);
                var groupNode = new AntdUI.TreeItem($"{gName} ({count})");
                groupNode.Tag = gPath;
                parentNodes.Add(groupNode);

                // 检查组内是否有已修改的字段
                if (modFields != null)
                {
                    bool groupModified = vars.Any(v =>
                        (string.Equals(v.GroupName, gPath, StringComparison.OrdinalIgnoreCase) ||
                         (v.GroupName != null && v.GroupName.StartsWith(gPath + "/", StringComparison.OrdinalIgnoreCase))) &&
                        modFields.Contains(v.Name));
                    if (groupModified)
                        groupNode.Fore = ThemeManager.Modified;
                }

                BuildOutlineTreeRecurse(sub, gPath, groupNode.Sub, vars, modFields);
            }

            // 处理 include 中的分组
            foreach (var incl in node.includes)
                BuildOutlineTreeRecurse(incl, parentPath, parentNodes, vars, modFields);
        }

        private static int CountVarsInGroup(List<TemplateVariableInfo> vars, string groupPath)
        {
            return vars.Count(v =>
                string.Equals(v.GroupName, groupPath, StringComparison.OrdinalIgnoreCase) ||
                (v.GroupName != null && v.GroupName.StartsWith(groupPath + "/", StringComparison.OrdinalIgnoreCase)));
        }

        private void OnOutlineNodeSelected(object sender, AntdUI.TreeSelectEventArgs e)
        {
            if (_isLoadingFile) return;
            var selectedTag = e.Item?.Tag as string;
            if (selectedTag == null) return;

            // 父分组节点（特殊分组/模板分组）不参与筛选，仅用于展开/收起。
            // 注意：此处不恢复 SelectItem——AntdUI Select() 在目标节点父链未展开时会自动展开父节点（Tree.cs），
            // 若在此设置 SelectItem 会与 OnOutlineNodeMouseClick 的 Expand 翻转冲突，导致"单击展开又马上折叠"。
            if (selectedTag == "__special__" || selectedTag == "__template__")
                return;

            // 单文件模式下"仅不同/仅相同"不可点击
            if ((selectedTag == "__diff__" || selectedTag == "__same__") && _multiFileRelPaths.Count <= 1)
            {
                _outlineTree.SelectItem = FindOutlineItem(_outlineTree.Items, _currentOutlineTag);
                return;
            }

            _currentOutlineTag = selectedTag;

            _isLoadingFile = true;
            try
            {
                RefreshDbrGrid();
            }
            finally
            {
                _isLoadingFile = false;
            }
            BeginInvoke((Action)(() => _fieldFilterTextBox?.Focus()));
        }

        private void OnOutlineNodeMouseClick(object sender, AntdUI.TreeSelectEventArgs e)
        {
            if (e.Item?.Sub == null || e.Item.Sub.Count == 0) return;
            if (e.Type == AntdUI.TreeCType.Arrow) return;
            e.Item.Expand = !e.Item.Expand;
        }

        private DbrGridRow GetDbrRowData(int rowIndex)
        {
            if (_dbrGrid.DataSource is List<DbrGridRow> list && rowIndex >= 0 && rowIndex < list.Count)
                return list[rowIndex];
            return null;
        }

        private void OnDebounceTimerTick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            if (_isLoadingFile) return;
        }

        private bool IsDbrValueColumn(int columnIndex)
        {
            if (_dbrGrid == null) return false;
            if (columnIndex < 0 || columnIndex >= _dbrGrid.Columns.Count) return false;
            string key = _dbrGrid.Columns[columnIndex].Key;
            return key == "FieldValue" || (key != null && key.StartsWith("V_", StringComparison.Ordinal));
        }

        /// <summary>
        /// 由事件参数里的**列对象**取该列对应的文件相对路径；不是文件列（或拿不到列对象）时返回 null。
        ///
        /// 【为什么必须用列对象，而不是列下标】事件回调传入的 ColumnIndex 是**视觉顺序**下标，
        /// 而 _dbrGrid.Columns 是**插入顺序**集合；用户拖动过列之后两者会错位 ——
        /// 按下标取会把操作作用到**另一个文件**（编辑结束尤其危险：会把值写进错误的文件并落盘，属静默数据损坏）。
        /// 列 Key 在 SetMultiFileColumns 里就是 "V_" + 相对路径，与列顺序无关，天然免疫拖列。
        /// </summary>
        private string GetPathByColumnKey(AntdUI.Column column)
        {
            string key = column?.Key;
            if (string.IsNullOrEmpty(key)) return null;
            if (key == "FieldValue") return _currentFileRelPath;
            if (key.StartsWith("V_", StringComparison.Ordinal)) return key.Substring(2);
            return null;
        }

        /// <summary>
        /// 复位 dbrGrid 的列排序状态（拖列后留在控件里的"视觉顺序表"）。
        /// 必要性：该状态按**列下标**记录，而多文件模式每次进出都会增删 V_ 列；
        /// 跨会话残留的旧下标表在新列集合上会让布局按越界下标取列，
        /// 而 Table.Layout 内部对越界是 catch{} 静默吞掉的 —— 表现为**缺列或错列**，很难查。
        /// </summary>
        private void ResetDbrGridColumnSort()
        {
            try { _dbrGrid?.SetSortColumnsIndex(null); } catch { }
        }

        private bool OnDbrCellBeginEdit(object sender, AntdUI.TableEventArgs e)
        {
            if (_isLoadingFile) return false;
            int rowIndex = e.RowIndex - 1;
            if (rowIndex < 0) return false;
            string colPath = GetPathByColumnKey(e.Column);
            if (colPath == null) return false;

            var data = GetDbrRowData(rowIndex);
            if (data == null || data.RowReadOnly) return false;
            var v = data.Tag;
            if (v == null) return false;

            // 多文件模式：目标文件无此字段（显示 "—"）不可编辑
            if (!string.Equals(colPath, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!_multiFileVariables.TryGetValue(colPath, out var vars)) return false;
                bool found = vars.Any(x => x != null && string.Equals(x.Name, v.Name, StringComparison.OrdinalIgnoreCase));
                if (!found) return false;
            }
            return true;
        }

        private void OnDbrCellBeginEditInputStyle(object sender, AntdUI.TableBeginEditInputStyleEventArgs e)
        {
            if (_isLoadingFile) return;
            int rowIndex = e.RowIndex - 1;
            string colPath = GetPathByColumnKey(e.Column);
            if (rowIndex < 0 || colPath == null) return;
            var data = GetDbrRowData(rowIndex);
            if (data?.Tag is not TemplateVariableInfo v) return;
            string val = GetFileFieldValue(colPath, v.Name);
            if (val != null) e.Input.Text = val;
        }

        private void OnDbrCellDoubleClick(object sender, AntdUI.TableClickEventArgs e)
        {
            if (_isLoadingFile) return;
            if (e.RowIndex < 0 || GetPathByColumnKey(e.Column) == null) return;

            var data = GetDbrRowData(e.RowIndex - 1);
            if (data == null || data.RowReadOnly) return;
        }

        private bool OnDbrCellEndEdit(object sender, AntdUI.TableEndEditEventArgs e)
        {
            if (_isLoadingFile) return false;
            int rowIndex = e.RowIndex - 1;
            if (rowIndex < 0) return false;
            string colPath = GetPathByColumnKey(e.Column);
            if (colPath == null) return false;

            var data = GetDbrRowData(rowIndex);
            if (data == null) return false;

            var v = data.Tag;
            if (v == null) return false;

            // 定位目标文件的变量（V_ 列对应各自文件，FieldValue 列对应当前文件）
            List<TemplateVariableInfo> targetVars;
            TemplateVariableInfo targetV;
            if (string.Equals(colPath, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
            {
                targetVars = _currentVariables;
                targetV = v;
            }
            else
            {
                if (!_multiFileVariables.TryGetValue(colPath, out targetVars)) return false;
                targetV = targetVars.FirstOrDefault(x => x != null && string.Equals(x.Name, v.Name, StringComparison.OrdinalIgnoreCase));
                if (targetV == null) return false;
            }

            // 值门禁（与批量编辑同一套 DbrValueGuard）：换行 / 逗号 / 控制字符等会破坏 DBR 结构的输入直接拒绝。
            // 返回 false 时 AntdUI 不写值，单元格显示自动回滚（无需手工回滚）。
            {
                List<string> origLines;
                if (string.Equals(colPath, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
                    origLines = _originalDbrLines;
                else
                    _multiFileOrigLines.TryGetValue(colPath, out origLines);

                int occ = CountKeyOccurrences(origLines, v.Name);
                var gctx = new DbrValueContext
                {
                    FieldName = v.Name,
                    IsArray = targetV.IsArray,
                    IsPicklist = DbrFieldHelper.IsPicklistField(targetV),
                    KeyExistsInOrigLines = occ > 0,
                    KeyOccurrences = occ
                };
                var editIssues = DbrValueGuard.Validate(e.Value ?? "", gctx);
                if (DbrValueGuard.HasError(editIssues))
                {
                    var primary = DbrValueGuard.Primary(editIssues);
                    MessageBox.Show(this, primary != null ? primary.Text
                            : LanguageManager.Instance.GetText("Editor.InlineValueBlocked", "值不合法"),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }

            string newVal = e.Value ?? "";
            if (string.Equals(colPath, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
                data.FieldValue = newVal;

            if (targetV.Value != newVal)
            {
                LogFieldChange(colPath, v.Name, targetV.Value, newVal);   // 变更记录：先取旧值
                targetV.Value = newVal;
                if (!_modifiedDbrFields.TryGetValue(colPath, out var fieldSet))
                {
                    fieldSet = new HashSet<string>();
                    _modifiedDbrFields[colPath] = fieldSet;
                }
                fieldSet.Add(v.Name);
                if (SaveDbrToTemp(colPath))
                {
                    MarkFileModified(colPath);
                    if (string.Equals(colPath, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
                        data.CellForeColor = ThemeManager.Modified;
                    RefreshOutlineTreeColors();
                }
            }
            // 值列已改为 Render 渲染，编辑结束后需重建单元格以更新显示
            BeginInvoke((Action)(() =>
            {
                if (_dbrGrid != null && _dbrGrid.IsHandleCreated && _dbrGrid.Visible)
                    _dbrGrid.Refresh();
            }));
            return true;
        }

        private void OnDbrGridCopyClick(object sender, EventArgs e)
        {
            if (_dbrGridContextRow < 0) return;
            _dbrGrid.CopyData(_dbrGridContextRow, _dbrGridContextCol);
        }

        private AntdUI.Table.CellStyleInfo OnDbrGridSetRowStyle(object sender, AntdUI.TableSetRowStyleEventArgs e)
        {
            if (e.RowType != AntdUI.RowType.None) return null;
            if (e.Record is not DbrGridRow row) return null;

            // 仅负责背景（templateName 灰行等），前景改由值列 Render 的 CellText.Fore 管，避免 VirtualMode 双路叠加错行
            if (row.CellBackColor != Color.Empty)
                return new AntdUI.Table.CellStyleInfo { BackColor = row.CellBackColor };

            return null;
        }

        private void OnDbrCellClick(object sender, AntdUI.TableClickEventArgs e)
        {
            if (_isLoadingFile) return;
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            if (e.Button == MouseButtons.Right)
            {
                _dbrGridContextRow = e.RowIndex;
                _dbrGridContextCol = e.ColumnIndex;
                AntdUI.ContextMenuStrip.open(_dbrGrid, it =>
                {
                    if ((it.Tag as string) == "copy") OnDbrGridCopyClick(it, EventArgs.Empty);
                    else if ((it.Tag as string) == "batchField") OnDbrCellBatchEditClick(EventArgs.Empty);
                    else if ((it.Tag as string) == "undoBatch") OnDbrGridUndoBatchClick();
                    else if ((it.Tag as string) == "resetWidths") TableWidthStore.Reset(TableWidthStore.TableId.DbrGrid, _dbrGrid);
                }, _dbrGridContextMenuItems);
            }
        }

        /// <summary>右键「批量编辑此字段」：以被点击的字段为预选打开批量编辑弹窗（仅多文件模式）</summary>
        private void OnDbrCellBatchEditClick(EventArgs e)
        {
            if (_isLoadingFile || _multiFileRelPaths.Count < 2) return;
            if (_dbrGridContextRow < 1) return;
            var data = GetDbrRowData(_dbrGridContextRow - 1);
            string field = data?.Tag?.Name;
            if (string.IsNullOrEmpty(field)) return;
            OnBatchEditClick(field);
        }

        private void OnDbrCellButtonClick(object sender, AntdUI.TableButtonEventArgs e)
        {
            if (_isLoadingFile) return;

            var rowData = e.Record as DbrGridRow;
            if (rowData == null) return;
            if (rowData.IsFileOpsRow)
            {
                if (e.Btn != null)
                {
                    string id = e.Btn.Id;
                    if (id == "exitMulti")
                        ExitMultiFileMode();
                    else if (id != null && id.StartsWith("setbase_", StringComparison.Ordinal))
                        SetBaseColumn(id.Substring("setbase_".Length));
                    else if (id != null && id.StartsWith("close_", StringComparison.Ordinal))
                        CloseFileColumn(id.Substring("close_".Length));
                    else
                        ConfirmCloseCurrentFile();
                }
                else
                    ConfirmCloseCurrentFile();
                return;
            }
            if (rowData.RowReadOnly) return;

            var v = rowData.Tag;
            if (v == null) return;

            string colPath = GetPathByColumnKey(e.Column);
            if (colPath == null) return;

            bool multiMode = _multiFileRelPaths.Count > 1;

            TemplateVariableInfo targetV = v;
            if (multiMode)
            {
                if (string.Equals(colPath, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
                {
                    targetV = _currentVariables?.FirstOrDefault(x => x != null && string.Equals(x.Name, v.Name, StringComparison.OrdinalIgnoreCase)) ?? v;
                }
                else
                {
                    if (!_multiFileVariables.TryGetValue(colPath, out var vars)) return;
                    targetV = vars.FirstOrDefault(x => x != null && string.Equals(x.Name, v.Name, StringComparison.OrdinalIgnoreCase));
                    if (targetV == null) return;
                }
            }

            if (v.IsArray)
            {
                Func<string, string> browser = v.Type == "file_dbr" ? ShowDbrFileBrowser : null;
                var dlg = new ArrayEditorDialog(v.Name, targetV.Value, browser);
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                    ApplyButtonEditResult(rowData, v, targetV, colPath, multiMode, dlg.Result);
            }
            else if (v.Type == "file_dbr")
            {
                string result = ShowDbrFileBrowser(targetV.Value);
                if (result != null)
                    ApplyButtonEditResult(rowData, v, targetV, colPath, multiMode, result);
            }
            else if (DbrFieldHelper.IsPicklistField(v))
            {
                var cellPt = _dbrGrid.PointToScreen(new Point(e.Rect.X, e.Rect.Bottom));
                DbrFieldHelper.ShowPicklistDropdown(this, cellPt, v, targetV.Value, result =>
                {
                    ApplyButtonEditResult(rowData, v, targetV, colPath, multiMode, result);
                    if (_dbrGrid != null && _dbrGrid.IsHandleCreated && _dbrGrid.Visible)
                        _dbrGrid.Refresh();
                });
            }
        }

        // ───────────── 批量编辑（同模板多文件一次改值） ─────────────

        /// <summary>批量编辑入口（按钮 / 字段右键）</summary>
        private void OnBatchEditClick(string preselectField = null)
        {
            if (_isLoadingFile || _multiFileRelPaths.Count < 2) return;

            var ctx = BuildBatchEditContext(preselectField);
            if (ctx.Files.Count < 2 || ctx.FieldNames.Count == 0) return;

            var result = BatchEditDialog.Show(this, ctx, () => BuildBatchEditContext(preselectField));
            if (result == null) return;    // 取消或未通过确认

            var applied = ApplyBatchFieldEdits(result, out var stats);

            LogOutput(string.Format(
                LanguageManager.Instance.GetText("Editor.BatchApplyDone",
                    "已批量更新 {0} 个文件；跳过写入 {1}（忽略 {2} / 无此字段 {3} / 校验未过 {4}）；未变化 {5}；失败 {6}"),
                stats.Applied, stats.SkippedMask, stats.SkippedIgnore, stats.SkippedNoField,
                stats.SkippedInvalid, stats.Unchanged, stats.SaveFailed + stats.ExceptionCount),
                stats.SaveFailed + stats.ExceptionCount > 0 ? "ERROR" : "INFO");

            if (stats.FailedPaths.Count > 0)
                MessageBox.Show(this,
                    string.Format(LanguageManager.Instance.GetText("Editor.BatchSaveFailedHint",
                        "有 {0} 个文件写入临时目录失败"), stats.FailedPaths.Count) + "：\n" +
                    string.Join("\n", stats.FailedPaths.Take(10)),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// 构建批量编辑的外部数据快照：
        /// 文件顺序 = 网格当前视觉列序（用户眼里的顺序）；
        /// 每文件带 变量名 → 现值 / 原文是否有该行 / 出现次数（门禁与差异展示都要用）。
        /// </summary>
        private BatchEditContext BuildBatchEditContext(string preselectField)
        {
            var ctx = new BatchEditContext
            {
                BasePath = _currentFileRelPath,
                PreselectField = preselectField
            };

            // 字段清单：模板变量全量（排除 templateName —— 批量改它会换掉整个文件的模板）
            if (_currentVariables != null)
            {
                foreach (var v in _currentVariables)
                {
                    if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                    if (string.Equals(v.Name, "templateName", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ctx.FieldIsArray.ContainsKey(v.Name)) continue;
                    ctx.FieldNames.Add(v.Name);
                    ctx.FieldIsArray[v.Name] = v.IsArray;
                    ctx.FieldIsPicklist[v.Name] = DbrFieldHelper.IsPicklistField(v);
                    ctx.FieldPicklist[v.Name] = v.PicklistOptions ?? Array.Empty<string>();
                }
            }

            // 文件（按视觉列序，避免受列拖动影响）
            foreach (var col in _dbrGrid.SortColumnsList())
            {
                string path = GetPathByColumnKey(col);
                if (string.IsNullOrEmpty(path)) continue;
                if (ctx.Files.Any(f => string.Equals(f.RelPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                if (!_multiFileVariables.TryGetValue(path, out var vars)) continue;
                _multiFileOrigLines.TryGetValue(path, out var origLines);

                var snap = new BatchFileSnap
                {
                    RelPath = path,
                    DisplayName = Path.GetFileName(path)
                };

                // 原文的字段行键计数（用于 是否有该行 / 出现次数）：一次遍历建表，变量查询 O(1)
                var lineKeyCounts = CollectLineKeyCounts(origLines);

                foreach (var v in vars)
                {
                    if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                    snap.Values[v.Name] = v.Value ?? "";
                    bool exists = lineKeyCounts.TryGetValue(v.Name, out int occ);
                    snap.Occurrences[v.Name] = exists ? occ : 0;
                    snap.KeyExists[v.Name] = exists;
                }

                ctx.Files.Add(snap);
            }
            return ctx;
        }

        /// <summary>
        /// 一次遍历统计原文各行键的出现次数（键 = 逗号前第一段，忽略大小写）。
        /// 供批量编辑上下文按变量名 O(1) 查询，避免"每变量 × 全部行"交叉比对导致的卡顿。
        /// </summary>
        private static Dictionary<string, int> CollectLineKeyCounts(List<string> lines)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (lines == null) return counts;
            foreach (var line in lines)
            {
                string k = (line ?? "").Split(',')[0].Trim();
                if (string.IsNullOrEmpty(k)) continue;
                counts.TryGetValue(k, out int c);
                counts[k] = c + 1;
            }
            return counts;
        }

        /// <summary>单次遍历统计某字段键在原文行中的出现次数</summary>
        private static int CountKeyOccurrences(List<string> lines, string fieldName)
        {
            if (lines == null || string.IsNullOrEmpty(fieldName)) return 0;
            int occ = 0;
            foreach (var line in lines)
            {
                string k = (line ?? "").Split(',')[0].Trim();
                if (string.Equals(k, fieldName, StringComparison.OrdinalIgnoreCase)) occ++;
            }
            return occ;
        }

        /// <summary>
        /// 批量写入引擎：按**文件路径**写回（免疫列顺序），逐文件回滚，绝不整批中断。
        /// 执行序列（与单元格编辑 :4069-4089 的既有安全顺序一致）：
        /// 登记已改字段 → 落临时文件 → 成功才记变更日志/标记文件已改 → 收尾一次性刷新。
        /// </summary>
        private int ApplyBatchFieldEdits(BatchEditResult result, out BatchApplyStats stats)
        {
            stats = new BatchApplyStats();
            if (result == null || string.IsNullOrEmpty(result.FieldName) || _multiFileRelPaths.Count <= 1) return 0;

            SaveCurrentEditorIfDirty();   // 源码页可能有未落盘改动，先落盘（否则批量基于过期数据）

            var undoEntries = new List<BatchUndoEntry>();   // 撤销本次批处理的数据（仅实际写入的文件）

            var skipSet = new HashSet<string>(result.SkipPaths, StringComparer.OrdinalIgnoreCase);
            var ignoreSet = new HashSet<string>(result.IgnorePaths, StringComparer.OrdinalIgnoreCase);

            foreach (var path in _multiFileRelPaths.ToList())
            {
                if (ignoreSet.Contains(path)) { stats.SkippedIgnore++; continue; }
                if (skipSet.Contains(path)) { stats.SkippedMask++; continue; }
                if (!result.FinalValuesByPath.TryGetValue(path, out var newVal)) { stats.SkippedMask++; continue; }

                // 取该文件自己的变量集（绝不用基准文件的变量兜底——那是另一个文件）
                List<TemplateVariableInfo> vars;
                List<string> origLines;
                if (string.Equals(path, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
                {
                    vars = _currentVariables;
                    origLines = _originalDbrLines;
                }
                else if (!_multiFileVariables.TryGetValue(path, out vars) ||
                    !_multiFileOrigLines.TryGetValue(path, out origLines))
                {
                    stats.SkippedNoField++; continue;
                }
                if (vars == null) { stats.SkippedNoField++; continue; }

                var targetV = vars.FirstOrDefault(x => x != null &&
                    string.Equals(x.Name, result.FieldName, StringComparison.OrdinalIgnoreCase));
                if (targetV == null) { stats.SkippedNoField++; continue; }

                string oldVal = targetV.Value ?? "";
                if (string.Equals(oldVal, newVal, StringComparison.Ordinal)) { stats.Unchanged++; continue; }

                // 写前再过一次门禁（纵深防御：用该文件自己的处境）
                int occ = CountKeyOccurrences(origLines, targetV.Name);
                var gctx = new DbrValueContext
                {
                    FieldName = result.FieldName,
                    IsArray = targetV.IsArray,
                    IsPicklist = DbrFieldHelper.IsPicklistField(targetV),
                    KeyExistsInOrigLines = occ > 0,
                    KeyOccurrences = occ
                };
                var issues = DbrValueGuard.Validate(newVal, gctx);
                if (DbrValueGuard.HasError(issues)) { stats.SkippedInvalid++; continue; }

                bool regExisted = _modifiedDbrFields.TryGetValue(path, out var modSet) && modSet.Contains(targetV.Name);
                try
                {
                    targetV.Value = newVal;
                    if (!_modifiedDbrFields.TryGetValue(path, out modSet))
                    {
                        modSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _modifiedDbrFields[path] = modSet;
                    }
                    modSet.Add(targetV.Name);

                    if (!SaveDbrToTemp(path))
                    {
                        // 回滚：值 + 本次新增的登记（不动该文件既有登记，否则之前的修改会连带丢）
                        targetV.Value = oldVal;
                        if (!regExisted)
                        {
                            modSet.Remove(targetV.Name);
                            if (modSet.Count == 0) _modifiedDbrFields.Remove(path);
                        }
                        stats.SaveFailed++;
                        if (stats.FailedPaths.Count < 20) stats.FailedPaths.Add(path);
                        continue;
                    }

                    LogFieldChange(path, targetV.Name, oldVal, newVal);   // 成功后才记（批量绝不"日志撒谎"）
                    MarkFileModified(path, immediateRefresh: false);
                    stats.Applied++;
                    if (!gctx.KeyExistsInOrigLines && !string.IsNullOrEmpty(newVal)) stats.AddedNewLine++;
                    undoEntries.Add(new BatchUndoEntry
                    {
                        Path = path,
                        Field = result.FieldName,
                        OldValue = oldVal,
                        NewValue = newVal,
                        RegExisted = regExisted
                    });

                    // 基准列显示同步（与单元格编辑 :4070-4071 一致）
                    if (string.Equals(path, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var row = FindDbrGridRowByName(targetV.Name);
                        if (row != null) row.FieldValue = newVal;
                    }
                }
                catch (Exception ex)
                {
                    targetV.Value = oldVal;
                    if (!regExisted && _modifiedDbrFields.TryGetValue(path, out modSet))
                    {
                        modSet.Remove(targetV.Name);
                        if (modSet.Count == 0) _modifiedDbrFields.Remove(path);
                    }
                    stats.ExceptionCount++;
                    if (stats.FailedPaths.Count < 20) stats.FailedPaths.Add(path);
                    LogOutput($"批量编辑失败 {path}：{ex.Message}", "ERROR");
                }
            }

            // 收尾：只刷新**受影响文件**（树节点色 + 文件夹链路色 + 文件列表行着色 + 右侧列表）。
            // 原实现走 RefreshAllAfterChange(refreshFileList: true, refreshColors: true)：
            // 平铺模式会重扫全部文件（6 万）重建行、并全树刷色 —— 26 个文件批量改值后表现为"着色缓慢"。
            _lastBatchUndo = stats.Applied > 0 ? undoEntries : null;   // 供「撤销本次批量修改」使用
            RefreshDbrGrid();
            RefreshOutlineTreeColors();
            var touchedPaths = new HashSet<string>(undoEntries.Select(u => u.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var p in touchedPaths)
            {
                RefreshFileTreeNodeColor(p);
                RefreshFolderTreeNodeColor(p);
            }
            UpdateFileListRowVisualForPaths(touchedPaths);
            UpdateModifiedList();
            return stats.Applied;
        }

        private DbrGridRow FindDbrGridRowByName(string fieldName)
        {
            if (_dbrGrid?.DataSource is List<DbrGridRow> list)
                return list.FirstOrDefault(r => r != null && r.Tag != null &&
                    string.Equals(r.Tag.Name, fieldName, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        /// <summary>批量写入的统计（写日志用）</summary>
        private sealed class BatchApplyStats
        {
            public int Applied;
            public int Unchanged;
            public int SkippedIgnore;
            public int SkippedMask;
            public int SkippedNoField;
            public int SkippedInvalid;
            public int SaveFailed;
            public int ExceptionCount;
            public int AddedNewLine;
            public List<string> FailedPaths = new List<string>();
        }

        /// <summary>「撤销本次批量修改」的单文件数据：写入前的值与登记状态</summary>
        private sealed class BatchUndoEntry
        {
            public string Path;
            public string Field;
            public string OldValue;
            public string NewValue;
            /// <summary>批量写入前该字段是否已登记为修改（决定撤销时要不要回收登记）</summary>
            public bool RegExisted;
        }

        /// <summary>最近一次批量的撤销数据（null = 无可撤销）；新批量会覆盖旧的</summary>
        private List<BatchUndoEntry> _lastBatchUndo;

        /// <summary>右键菜单「撤销本次批量修改」</summary>
        private void OnDbrGridUndoBatchClick()
        {
            if (_isLoadingFile) return;
            if (_lastBatchUndo == null || _lastBatchUndo.Count == 0)
            {
                MessageBox.Show(this,
                    LanguageManager.Instance.GetText("Editor.BatchUndoNone",
                        "当前没有可撤销的批量修改（撤销仅针对最近一次批量，且新一次批量会覆盖上一次的撤销数据）"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string field = _lastBatchUndo[0].Field;
            string msg = string.Format(
                LanguageManager.Instance.GetText("Editor.BatchUndoConfirm",
                    "将撤销最近一次批量修改：字段 {0}，共 {1} 个文件会恢复为写入前的值。\r\n注意：只撤销那一次批量写入，不影响这些文件此前的其它修改。确定继续？"),
                field, _lastBatchUndo.Count);
            var config = new AntdUI.Modal.Config(
                this,
                LanguageManager.Instance.GetText("Common.Confirm", "确认"),
                new AntdUI.Label { Text = msg, Width = DpiManager.S(420), AutoSize = true })
            {
                OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                OkType = AntdUI.TTypeMini.Primary,
                MaskClosable = false,
                Draggable = true,
                OnOk = c =>
                {
                    int undone = UndoLastBatch(out int failed);
                    LogOutput(string.Format(
                        LanguageManager.Instance.GetText("Editor.BatchUndoDone",
                            "已撤销批量修改 {0} 个文件；失败 {1}"), undone, failed),
                        failed > 0 ? "ERROR" : "INFO");
                    if (failed > 0)
                        MessageBox.Show(this,
                            string.Format(LanguageManager.Instance.GetText("Editor.BatchUndoFailedHint",
                                "有 {0} 个文件撤销失败（写临时目录失败，已保持批量后的值）"), failed),
                            LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    ShowFileList();   // 撤销后回到文件列表选项卡
                    return true;
                }
            };
            AntdUI.Modal.open(config);
        }

        /// <summary>
        /// 撤销最近一次批量：逐文件 恢复旧值 → 按写入前状态回收登记 → 重写临时文件。
        /// 回收登记后重写，重建的正文即原始内容（新增行自然消失）；登记仍存在（批量前就改过）则写回恢复后的值。
        /// </summary>
        private int UndoLastBatch(out int failed)
        {
            failed = 0;
            int undone = 0;
            if (_lastBatchUndo == null || _lastBatchUndo.Count == 0) return 0;

            SaveCurrentEditorIfDirty();   // 源码页可能有未落盘改动，先落盘

            foreach (var e in _lastBatchUndo)
            {
                List<TemplateVariableInfo> vars;
                if (string.Equals(e.Path, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
                    vars = _currentVariables;
                else if (!_multiFileVariables.TryGetValue(e.Path, out vars))
                { failed++; continue; }   // 该文件已从多文件模式移除，无法恢复
                if (vars == null) { failed++; continue; }

                var targetV = vars.FirstOrDefault(x => x != null &&
                    string.Equals(x.Name, e.Field, StringComparison.OrdinalIgnoreCase));
                if (targetV == null) { failed++; continue; }

                bool regStillExists = _modifiedDbrFields.TryGetValue(e.Path, out var modSet) && modSet.Contains(e.Field);
                try
                {
                    targetV.Value = e.OldValue;
                    bool removedReg = false;
                    if (!e.RegExisted && regStillExists && modSet != null)
                    {
                        modSet.Remove(e.Field);
                        if (modSet.Count == 0) _modifiedDbrFields.Remove(e.Path);
                        removedReg = true;
                    }

                    if (SaveDbrToTemp(e.Path))
                    {
                        LogFieldChange(e.Path, e.Field, e.NewValue, e.OldValue);   // 撤销也是一次变更（新→旧）
                        if (_modifiedDbrFields.TryGetValue(e.Path, out var after) && after.Count > 0)
                            MarkFileModified(e.Path, immediateRefresh: false);
                        else
                        {
                            // 该文件已无任何登记的修改 → 不再算"已修改"（只改标记，不在循环里刷新）
                            _modifiedFiles.Remove(e.Path);
                            RemoveStatusChange(e.Path);
                            SetNodeState(e.Path, "");
                        }
                        undone++;
                    }
                    else
                    {
                        // 写失败回滚：恢复批量后的值 + 恢复刚回收的登记
                        targetV.Value = e.NewValue;
                        if (removedReg)
                        {
                            if (!_modifiedDbrFields.TryGetValue(e.Path, out modSet))
                            {
                                modSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                _modifiedDbrFields[e.Path] = modSet;
                            }
                            modSet.Add(e.Field);
                        }
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    targetV.Value = e.NewValue;
                    if (!e.RegExisted && !_modifiedDbrFields.TryGetValue(e.Path, out _))
                    {
                        var set2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { e.Field };
                        _modifiedDbrFields[e.Path] = set2;
                    }
                    LogOutput($"撤销批量修改失败 {e.Path}：{ex.Message}", "ERROR", e.Path);
                    failed++;
                }
            }

            // 收尾：只刷新**受影响文件**（与"批量编辑"收尾同策略；原实现会重扫全部文件 + 全树刷色）
            var touchedPaths = new HashSet<string>(_lastBatchUndo.Select(u => u.Path), StringComparer.OrdinalIgnoreCase);
            _lastBatchUndo = null;   // 撤销是一次性动作
            RefreshDbrGrid();
            RefreshOutlineTreeColors();
            foreach (var p in touchedPaths)
            {
                RefreshFileTreeNodeColor(p);
                RefreshFolderTreeNodeColor(p);
            }
            UpdateFileListRowVisualForPaths(touchedPaths);
            UpdateModifiedList();
            return undone;
        }

        /// <summary>
        /// 应用按钮类编辑结果：多文件模式写回目标文件变量集并单独保存；
        /// 单文件模式维持原逻辑（写回当前文件、标红、刷新网格）。
        /// </summary>
        private void ApplyButtonEditResult(DbrGridRow rowData, TemplateVariableInfo v, TemplateVariableInfo targetV,
            string colPath, bool multiMode, string result)
        {
            if (multiMode)
            {
                if (targetV.Value != result)
                {
                    LogFieldChange(colPath, targetV.Name, targetV.Value, result);   // 变更记录（多文件模式：逐文件一条）
                    targetV.Value = result;
                    if (!_modifiedDbrFields.TryGetValue(colPath, out var fieldSet))
                    {
                        fieldSet = new HashSet<string>();
                        _modifiedDbrFields[colPath] = fieldSet;
                    }
                    fieldSet.Add(targetV.Name);
                    if (SaveDbrToTemp(colPath))
                    {
                        MarkFileModified(colPath);
                        RefreshOutlineTreeColors();
                    }
                }
                if (_dbrGrid != null && _dbrGrid.IsHandleCreated && _dbrGrid.Visible)
                    _dbrGrid.Refresh();
            }
            else
            {
                rowData.FieldValue = result;
                if (v.Value != result)
                {
                    LogFieldChange(_currentFileRelPath, v.Name, v.Value, result);   // 变更记录：先取旧值
                    v.Value = result;
                    if (!_modifiedDbrFields.TryGetValue(_currentFileRelPath, out var fieldSet))
                    {
                        fieldSet = new HashSet<string>();
                        _modifiedDbrFields[_currentFileRelPath] = fieldSet;
                    }
                    fieldSet.Add(v.Name);
                    if (SaveCurrentDbrToTemp())
                    {
                        MarkFileModified(_currentFileRelPath);
                        rowData.CellForeColor = ThemeManager.Modified;
                        RefreshOutlineTreeColors();
                    }
                }
                if (_dbrGrid.DataSource is List<DbrGridRow> dataSource)
                {
                    int rowIdx = dataSource.IndexOf(rowData);
                    if (rowIdx >= 0)
                        _dbrGrid.Refresh();
                }
            }
        }

        private void ConfirmCloseCurrentFile()
        {
            string fileName = string.IsNullOrEmpty(_currentFileRelPath)
                ? ""
                : Path.GetFileName(_currentFileRelPath);
            string msg = string.Format(
                LanguageManager.Instance.GetText("Editor.MultiFileCloseConfirm", "确定关闭文件 {0}？"),
                fileName);
            var config = new AntdUI.Modal.Config(
                LanguageManager.Instance.GetText("Common.Confirm", "确认"),
                msg)
            {
                OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                MaskClosable = false,
                OnOk = c =>
                {
                    CloseCurrentFile();
                    return true;
                }
            };
            AntdUI.Modal.open(config);
        }

        private void EnsureBaseColumns()
        {
            if (_dbrGrid == null) return;
            // 列宽模式=Auto：表格区域收缩到列总宽，外框线贴住最后一列右缘（Fill 模式下最后一列右侧分割线被外框/滚动条遮盖而缺失）
            _dbrGrid.AutoSizeColumnsMode = AntdUI.ColumnsMode.Auto;
            if (_dbrGrid.Columns.Count == 0)
            {
                // 列宽默认策略（用户定稿）：字段名/数值=150px 固定，字段分类/数值类型=auto（内容短且可预估）
                _dbrGrid.Columns.Add(new AntdUI.Column("FieldNameCell",
                    LanguageManager.Instance.GetText("Editor.FieldName", "字段名")) { SortOrder = true, Width = "150" });
                _dbrGrid.Columns.Add(new AntdUI.Column("FieldClass",
                    LanguageManager.Instance.GetText("Editor.FieldClass", "字段分类")) { Width = "auto" });
                _dbrGrid.Columns.Add(new AntdUI.Column("FieldType",
                    LanguageManager.Instance.GetText("Editor.FieldType", "数值类型")) { Width = "auto" });
            }
            if (_dbrGrid.Columns.All(c => c.Key != "FieldValue"))
            {
                var fieldValueCol = new AntdUI.Column("FieldValue",
                    LanguageManager.Instance.GetText("Editor.FieldValue", "数值")) { Ellipsis = true, Width = "150" };
                fieldValueCol.Render = BuildDbrValueCellRender;
                _dbrGrid.Columns.Add(fieldValueCol);
            }
            if (_dbrGrid.Columns.All(c => c.Key != "FieldDesc"))
            {
                // "字段备注[官方]"列：置于最后——① 显示 TPL 模板对字段的官方描述；② 让"数值"不再处于
                // 最后一列，列宽拖拽手柄不再与表格右缘重叠（初始 50px 非重点列，可拖宽、记入列宽记忆）
                var fieldDescCol = new AntdUI.Column("FieldDesc",
                    LanguageManager.Instance.GetText("Editor.FieldTplNote", "字段备注[官方]")) { Ellipsis = true, Width = "50" };
                fieldDescCol.Render = BuildDbrDescCellRender;
                _dbrGrid.Columns.Add(fieldDescCol);
            }
            // 列定义就绪后应用记忆列宽（TableWidthStore；幂等，首次调用捕获原始列宽供重置恢复）+ MouseUp 防抖自动保存
            TableWidthStore.Apply(TableWidthStore.TableId.DbrGrid, _dbrGrid);
            TableWidthStore.AttachAutoSave(TableWidthStore.TableId.DbrGrid, _dbrGrid);
        }

        private void CloseCurrentFile()
        {
            // 关闭文件前先落一次行级变更记录；随后清空源码模式基线
            // （清空是必须的：否则再次打开同一文件时会把新内容与已失效的旧基线比对，可能产生误导性的差异）
            LogSourceLineChanges();
            _sourceBaselineText = null;
            _sourceBaselinePath = null;
            _sourceLogTimer?.Stop();   // 关闭文件后不再有"待防抖"的编辑

            _multiFileRelPaths.Clear();
            _multiFileVariables.Clear();
            _multiFileOrigLines.Clear();
            ResetDbrGridColumnSort();   // 先清列排序状态，再改列集合（否则旧下标表会作用于新列集合）
            for (int i = _dbrGrid.Columns.Count - 1; i >= 0; i--)
            {
                string key = _dbrGrid.Columns[i].Key;
                if (key != null && (key == "FieldValue" || key.StartsWith("V_", StringComparison.Ordinal)))
                    _dbrGrid.Columns.RemoveAt(i);
            }
            EnsureBaseColumns();
            _dbrGrid.ColumnDragSort = false;
            UpdateSameTemplateButtonText();
            _currentVariables = null;
            _currentTemplate = null;
            _currentDbrTplName = null;
            _notInTemplateVars = null;
            _originalDbrLines = null;
            _fileTree.SelectItem = null;
            if (_fileListGrid.DataSource != null)
                _fileListGrid.SelectedIndexs = Array.Empty<int>();
            _outlineTree.SelectItem = null;
            _isLoadingFile = true;
            _dbrSourceEditor.Text = "";
            _isLoadingFile = false;
            _dbrSourceEditor.Visible = false;
            if (_sourceNoFilePlaceholder != null)
                _sourceNoFilePlaceholder.Visible = true;
            ShowPlaceholder();
        }

        // ======================== Multi-File Same-Template Editing (Phase 4) ========================

        private class SameTplRow
        {
            public bool Checked { get; set; }
            public bool IsBase { get; set; }
            public string Path { get; set; }
            /// <summary>列表显示名（基准行带 ★ 前缀的文件名）</summary>
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string LocalizedName { get; set; }
            public string Template { get; set; }
        }

        private async void OnOpenSameTemplateClick()
        {
            if (_isScanningTemplates)
            {
                _scanCts?.Cancel();
                return;
            }
            if (_multiFileRelPaths.Count > 0)
            {
                ExitMultiFileMode();
                return;
            }
            if (_currentEditorMode != EditorMode.DbrGrid || _currentVariables == null ||
                string.IsNullOrEmpty(_currentDbrTplName))
            {
                MessageBox.Show(
                    LanguageManager.Instance.GetText("Editor.NoBaseDbrForScan", "请先打开一个带模板的 DBR 文件。"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _isScanningTemplates = true;
            _btnOpenSameTemplate.Text = LanguageManager.Instance.GetText("Editor.ScanTemplateCancel", "取消扫描");
            _scanCts?.Dispose();
            _scanCts = new System.Threading.CancellationTokenSource();
            var ct = _scanCts.Token;
            List<string> candidates = null;
            try
            {
                if (_isArz && _arzReader != null)
                {
                    _arzReader.EnsureRecordTableIsLoaded();
                    _arzReader.EnsureStringTableLoadedForParallel();
                }
                string scanHint = LanguageManager.Instance.GetText("Editor.ScanTemplateHint", "正在扫描同模板文件...");
                var spinCfg = new AntdUI.Spin.Config
                {
                    Text = scanHint,
                    CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                    CancelTokenSource = _scanCts
                };
                var spinTarget = (Control)_dbrGrid ?? this;
                await AntdUI.Spin.open(spinTarget, spinCfg, async cfg =>
                {
                    var items = CollectDbrScanItems();
                    int total = items.Count;
                    int done = 0;
                    var result = new List<string>();
                    await Task.Run(() =>
                    {
                        // 每线程复用一个只读流（4 线程 → 最多 4 个），替代"每个文件开一次流"：
                        // 几万文件的扫描里，开/关流的系统调用是纯开销。扫描结束统一释放。
                        var scanStreams = new ThreadLocal<FileStream>(() =>
                            new FileStream(_archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                            trackAllValues: true);
                        try
                        {
                            Parallel.ForEach(items,
                                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                                item =>
                                {
                                    string tpl = GetDbrTplNameCached(item.path, item.recordIndex, scanStreams);
                                    if (string.Equals(tpl, _currentDbrTplName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        lock (result) result.Add(item.path);
                                    }
                                    int d = Interlocked.Increment(ref done);
                                    if (d % 100 == 0 || d == total)
                                    {
                                        string msg = string.Format(
                                            LanguageManager.Instance.GetText("Editor.ScanTemplateProgress", "正在扫描同模板 DBR 文件... {0}/{1}"),
                                            d, total);
                                        try
                                        {
                                            if (IsHandleCreated)
                                                BeginInvoke((Action)(() => { cfg.Text = msg; }));
                                        }
                                        catch { }
                                    }
                                });
                        }
                        finally
                        {
                            foreach (var s in scanStreams.Values) { try { s?.Dispose(); } catch { } }
                            scanStreams.Dispose();
                        }
                    }, ct);
                    candidates = result;
                });

                if (candidates == null || candidates.Count == 0)
                {
                    MessageBox.Show(
                        LanguageManager.Instance.GetText("Editor.NoTemplateMatch", "未找到与当前文件同模板的其他 DBR 文件。"),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ShowSameTemplatePicker(candidates);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _isScanningTemplates = false;
                UpdateSameTemplateButtonText();
                _scanCts?.Dispose();
                _scanCts = null;
            }
        }

        private List<(string path, int? recordIndex)> CollectDbrScanItems()
        {
            var list = new List<(string path, int? recordIndex)>();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(List<NodeData> nodes, string prefix)
            {
                foreach (var nd in nodes)
                {
                    if (nd == null) continue;
                    string rel = string.IsNullOrEmpty(prefix) ? nd.Name : prefix + "/" + nd.Name;
                    if (nd.IsDirectory)
                        Walk(nd.Children, rel);
                    else if (rel.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_deletedFiles.Contains(rel)) continue;
                        list.Add((rel, nd.RecordIndex));
                        set.Add(rel);
                    }
                }
            }
            if (_originalRootNodes != null)
                Walk(_originalRootNodes, "");
            foreach (var p in _session.GetModifiedRelativePaths())
            {
                if (p.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase) && !set.Contains(p))
                {
                    list.Add((p, null));
                    set.Add(p);
                }
            }
            return list;
        }

        /// <summary>
        /// 取文件模板名（缓存优先）。
        /// <paramref name="streamSource"/>：可选的"每线程一个只读流"来源——批量扫描时传入可避免
        /// 每个文件创建/销毁一次 FileStream（几万文件时是纯 IO 开销）；为空则按需临时开流（单文件调用场景）。
        /// </summary>
        private string GetDbrTplNameCached(string path, int? recordIndex, ThreadLocal<FileStream> streamSource = null)
        {
            if (_dbrTplNameCache.TryGetValue(path, out var cached)) return cached;
            string tpl = null;
            try
            {
                string tempFile = _session.GetTempFilePath(path);
                if (File.Exists(tempFile))
                {
                    string text = EncodingService.ReadTextFile(tempFile);
                    tpl = TemplateHelper.GetDbrTemplateName(text.Replace("\r\n", "\n").Split('\n'));
                }
                else if (_isArz && recordIndex.HasValue && _arzReader != null)
                {
                    FileStream fs = streamSource != null
                        ? streamSource.Value
                        : new FileStream(_archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    try
                    {
                        var record = _arzReader.GetRecord(recordIndex.Value, fs);
                        using (var ms = new MemoryStream())
                        {
                            record.SaveToStream(ms);
                            ms.Position = 0;
                            using (var sr = new StreamReader(ms, Utf8NoBom))
                                tpl = TemplateHelper.GetDbrTemplateName(sr.ReadToEnd().Replace("\r\n", "\n").Split('\n'));
                        }
                    }
                    finally
                    {
                        if (streamSource == null) fs.Dispose();   // 临时流自己释放；每线程流由调用方统一释放
                    }
                }
                else if (!_isArz && recordIndex.HasValue && _arcFile != null)
                {
                    FileStream fs = streamSource != null
                        ? streamSource.Value
                        : new FileStream(_archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    try
                    {
                        using (var ms = new MemoryStream())
                        {
                            _arcFile.UnpackToStream(_arcFile.toc[recordIndex.Value], fs, ms);
                            string text = TemplateHelper.DecodeText(ms.ToArray());
                            tpl = TemplateHelper.GetDbrTemplateName(text.Replace("\r\n", "\n").Split('\n'));
                        }
                    }
                    finally
                    {
                        if (streamSource == null) fs.Dispose();
                    }
                }
            }
            catch { }
            // ConcurrentDictionary 不允许 null 值：无模板的 DBR 只跳过缓存，下次再读（原写法会抛异常中断扫描）
            if (!string.IsNullOrEmpty(tpl))
                _dbrTplNameCache[path] = tpl;
            return tpl;
        }

        private void ShowSameTemplatePicker(List<string> candidates)
        {
            var sorted = candidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            string baseDir = "";
            int slash = (_currentFileRelPath ?? "").LastIndexOf('/');
            if (slash > 0) baseDir = _currentFileRelPath.Substring(0, slash);

            var rows = new List<SameTplRow>();
            foreach (var p in sorted)
            {
                var nd = FindNodeDataByPath(_originalRootNodes, p.Split('/'), 0);
                string localName = nd != null ? LookupLanguageNote(p, nd) : "";
                string desc = nd != null ? GetFileDescription(p, nd) : "";
                bool isBase = string.Equals(p, _currentFileRelPath, StringComparison.OrdinalIgnoreCase);
                rows.Add(new SameTplRow
                {
                    Checked = isBase,          // 基准行默认勾选
                    IsBase = isBase,
                    Path = p,
                    DisplayName = (isBase ? "★ " : "") + Path.GetFileName(p),
                    Description = desc,
                    LocalizedName = localName
                });
            }

            var grid = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = true,
                Bordered = true,
                MultipleRows = true,
                EditMode = AntdUI.TEditMode.None,
                LostFocusClearSelection = false,
                EnableHeaderResizing = true,
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill,
                RowHeight = 26,    // 与文件列表一致
                Font = FontHelper.UiFont(9)
            };
            var checkCol = new AntdUI.ColumnCheck("Checked", "") { Width = "40" };
            // 基准行锁定勾选（Call 返回值即最终状态）：多文件模式的基准列不可退出，防误点
            checkCol.Call = (value, record, rowIdx, colIdx) =>
                (record as SameTplRow)?.IsBase == true ? true : value;
            grid.Columns.Add(checkCol);
            // 注意：本表固定行高 26，各文本列一律不开 Ellipsis —— Ellipsis 会放开 GDI+ 折行，
            // 长文本（完整路径等）折成多行后被行高整格裁空（只剩悬停 tip），见 wiki 6.3.0；超宽靠硬裁 + 自带 tip 兜底
            grid.Columns.Add(new AntdUI.Column("DisplayName",
                LanguageManager.Instance.GetText("Editor.FileName", "文件名")) { SortOrder = true });
            grid.Columns.Add(new AntdUI.Column("Path",
                LanguageManager.Instance.GetText("Editor.SameTemplateFullPath", "完整路径")) { Width = "26%", SortOrder = true });
            grid.Columns.Add(new AntdUI.Column("LocalizedName",
                LanguageManager.Instance.GetText("Editor.Notes", "本地化名称")) { Width = "15%", SortOrder = true });
            grid.Columns.Add(new AntdUI.Column("Description",
                LanguageManager.Instance.GetText("Editor.FileDescription", "说明")) { Width = "15%" });
            grid.DataSource = rows;
            List<SameTplRow> currentList = rows;   // 当前筛选/排序范围内显示的行（拖拽/排序处理器引用）

            // 点击列头排序（NONE→升→降），对当前筛选范围内的列表重排
            grid.SortRows += (s, e) =>
            {
                if (e.Value < 0 || e.Value >= grid.Columns.Count) return;
                var col = grid.Columns[e.Value];
                bool asc = col.SortMode == AntdUI.SortMode.ASC;
                Func<SameTplRow, string> sel = col.Key switch
                {
                    "DisplayName" => r => (r.DisplayName ?? "").TrimStart('★', ' '),
                    "Path" => r => r.Path ?? "",
                    "LocalizedName" => r => r.LocalizedName ?? "",
                    _ => null
                };
                if (sel == null) return;
                currentList = asc ? currentList.OrderBy(sel).ToList() : currentList.OrderByDescending(sel).ToList();
                grid.DataSource = currentList;
            };

            // ── 拖拽勾选（仿文件列表）：按住左键划过的行批量勾选/取消；Ctrl+点行切换单行；边缘自动滚动 ──
            bool dragActive = false, dragTarget = true;
            int dragStartRow = -1, dragLastRow = -1;
            System.Windows.Forms.Timer dragScrollTimer = new System.Windows.Forms.Timer { Interval = 50 };
            List<SameTplRow> ViewList() => grid.DataSource as List<SameTplRow> ?? currentList;

            void ApplyDragRange(int a, int b)
            {
                var list = ViewList();
                if (list == null) return;
                if (a < 1) a = 1;
                if (b > list.Count) b = list.Count;
                bool changed = false;
                for (int i = a; i <= b; i++)
                {
                    var r = list[i - 1];
                    if (r.IsBase && !dragTarget) continue;   // 基准行勾选锁定，不参与取消
                    if (r.Checked != dragTarget) { r.Checked = dragTarget; changed = true; }
                }
                if (changed)
                {
                    // 普通 List 数据源不订阅行属性变更，必须 LoadLayout 重建单元格才会刷新勾选显示
                    grid.LoadLayout();
                    grid.Invalidate();
                }
            }
            void ExtendDrag(int x, int y)
            {
                var cell = grid.HitTest(x, y, out int row, out _);
                if (cell == null || row < 1) return;
                if (row == dragLastRow) return;
                dragLastRow = row;
                ApplyDragRange(Math.Min(dragStartRow, row), Math.Max(dragStartRow, row));
            }
            dragScrollTimer.Tick += (s, e) =>
            {
                if (!dragActive) { dragScrollTimer.Stop(); return; }
                var sb = grid.ScrollBar;
                if (sb == null || !sb.ShowY || sb.MaxY <= 0) return;
                var pt = grid.PointToClient(Cursor.Position);
                int h = grid.Height;
                int before = sb.ValueY;
                if (pt.Y < 8) sb.ValueY = before - 12;
                else if (pt.Y > h - 8) sb.ValueY = before + 12;
                if (sb.ValueY != before) ExtendDrag(pt.X, Math.Min(Math.Max(pt.Y, 1), h - 1));
            };
            grid.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var cell = grid.HitTest(e.X, e.Y, out int row, out _);
                if (cell == null || row < 1) return;
                var list = ViewList();
                if (list == null || row > list.Count) return;
                if (ModifierKeys.HasFlag(Keys.Control))
                {
                    var r = list[row - 1];
                    if (!r.IsBase) r.Checked = !r.Checked;   // 基准行保持勾选
                    grid.LoadLayout();
                    grid.Invalidate();
                    return;
                }
                dragActive = true;
                dragStartRow = row; dragLastRow = row;
                dragTarget = !list[row - 1].Checked;   // 拖拽目标状态 = 起始行点击前的相反状态
                dragScrollTimer.Start();
            };
            grid.MouseMove += (s, e) =>
            {
                if (!dragActive) return;
                int h = grid.Height;
                if (e.Y < 0 || e.Y > h) return;
                ExtendDrag(e.X, Math.Min(Math.Max(e.Y, 1), h - 1));
            };
            grid.MouseUp += (s, e) =>
            {
                if (!dragActive) return;
                dragActive = false;
                dragScrollTimer.Stop();
                // 未拖动（原地点击）：什么都不做，交给 AntdUI 原生勾选切换，避免双切
            };

            var filterInput = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Height = 32,
                Font = FontHelper.UiFont(10),
                PrefixText = LanguageManager.Instance.GetText("Editor.SameTemplateFilter", "筛选") + ":",
                AllowClear = true,
                Margin = new Padding(0, 0, 0, 6)
            };

            // 仅基准目录：只看与当前文件同目录的候选
            var chkBaseDir = new AntdUI.Checkbox
            {
                Text = LanguageManager.Instance.GetText("Editor.SameTemplateBaseDirOnly", "仅基准目录"),
                Dock = DockStyle.Right, Width = 96, Height = 28,
                Font = FontHelper.UiFont(9), BackColor = Color.Transparent
            };

            // 筛选防抖（250ms），避免大列表逐字符重建
            var filterTimer = new System.Windows.Forms.Timer { Interval = 250 };
            filterTimer.Tick += (s, e) =>
            {
                filterTimer.Stop();
                string kw = filterInput.Text?.Trim() ?? "";
                IEnumerable<SameTplRow> scope = chkBaseDir.Checked && baseDir.Length > 0
                    ? rows.Where(r => r.Path.StartsWith(baseDir + "/", StringComparison.OrdinalIgnoreCase))
                    : rows;
                currentList = string.IsNullOrEmpty(kw)
                    ? scope.ToList()
                    : scope.Where(r => r.Path.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0
                        || (r.Description?.IndexOf(kw, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                        || (r.LocalizedName?.IndexOf(kw, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0).ToList();
                grid.DataSource = currentList;
            };
            void ApplyFilter()
            {
                filterTimer.Stop();
                filterTimer.Start();
            }
            chkBaseDir.CheckedChanged += (s, e) => ApplyFilter();
            filterInput.TextChanged += (s, e) => ApplyFilter();

            var checkPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Back = Color.Transparent,
                Margin = new Padding(0)
            };
            var deselectAllBtn = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Editor.DeselectAll", "全不选"),
                Dock = DockStyle.Left,
                
                
                Margin = new Padding(0, 4, 4, 0)
            };
            var selectAllBtn = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Editor.SelectAll", "全选"),
                Dock = DockStyle.Left,
                
                
                Margin = new Padding(0, 4, 4, 0)
            };
            selectAllBtn.Click += (s, e) =>
            {
                foreach (var r in currentList) r.Checked = true;
                grid.DataSource = currentList.ToList();
            };
            deselectAllBtn.Click += (s, e) =>
            {
                foreach (var r in currentList) r.Checked = !r.IsBase;   // 基准行保持勾选
                grid.DataSource = currentList.ToList();
            };
            checkPanel.Controls.Add(deselectAllBtn);
            checkPanel.Controls.Add(selectAllBtn);

            var content = new AntdUI.Panel
            {
                Width = 720,
                Height = 560,
                BackColor = Color.Transparent
            };
            content.Controls.Add(grid);
            content.Controls.Add(checkPanel);
            var filterWrap = new AntdUI.Panel { Dock = DockStyle.Top, Height = 38, Back = Color.Transparent };
            filterWrap.Controls.Add(filterInput);
            filterWrap.Controls.Add(chkBaseDir);
            content.Controls.Add(filterWrap);

            List<string> selected = null;
            var config = new AntdUI.Modal.Config(
                this,
                LanguageManager.Instance.GetText("Editor.SelectSameTemplateTitle", "选择同模板文件"),
                content)
            {
                Width = 740,
                Padding = new Size(12, 12),
                Resizable = true,
                MinimumSize = new Size(500, 400),
                OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                OkType = AntdUI.TTypeMini.Primary,
                MaskClosable = false,
                // ⚠️ 不设 Draggable：AntdUI 对 Draggable 弹窗会在底部按钮行 MouseMove 时触发拖动逻辑，
                //    鼠标扫过"确定"一行就会激活窗口抢前台（与批量编辑弹窗同一问题）
                OnOk = cfg =>
                {
                    var sel = rows.Where(r => r.Checked).Select(r => r.Path).ToList();
                    if (sel.Count == 0)
                    {
                        MessageBox.Show(
                            LanguageManager.Instance.GetText("Editor.SelectZeroHint", "请至少选择一个文件。"),
                            LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return false;
                    }
                    selected = sel;
                    return true;
                }
            };
            AntdUI.Modal.open(config);

            if (selected != null && selected.Count > 0)
                EnterMultiFileMode(selected);
        }

        private async void EnterMultiFileMode(List<string> paths)
        {
            if (_isMultiLoading) return;
            var all = new List<string> { _currentFileRelPath };
            foreach (var p in paths)
            {
                if (!all.Contains(p, StringComparer.OrdinalIgnoreCase))
                    all.Add(p);
            }

            // 后台加载（仿同模板扫描）：解压+解析较慢，不能卡 UI 线程；Spin 可取消
            _isMultiLoading = true;
            _multiLoadCts?.Dispose();
            _multiLoadCts = new System.Threading.CancellationTokenSource();
            var ct = _multiLoadCts.Token;
            var failed = new List<string>();
            var spinCfg = new AntdUI.Spin.Config
            {
                Text = LanguageManager.Instance.GetText("Editor.MultiFileLoading", "正在加载同模板文件..."),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                CancelTokenSource = _multiLoadCts
            };
            // Spin 罩在整个窗体上：无论当前停在文件列表页还是编辑器页都能看到加载进度
            var spinTarget = (Control)this;
            try
            {
                await AntdUI.Spin.open(spinTarget, spinCfg, async cfg =>
                {
                    int total = all.Count;
                    int done = 0;
                    await Task.Run(() =>
                    {
                        foreach (var p in all)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!_multiFileVariables.ContainsKey(p))
                            {
                                if (!LoadDbrVariablesToCache(p))
                                    lock (failed) failed.Add(p);
                            }
                            int d = Interlocked.Increment(ref done);
                            string msg = string.Format(
                                LanguageManager.Instance.GetText("Editor.MultiFileLoadProgress", "正在加载... {0}/{1}"),
                                d, total);
                            try
                            {
                                if (IsHandleCreated)
                                    BeginInvoke((Action)(() => { cfg.Text = msg; }));
                            }
                            catch { }
                        }
                    }, ct);
                });

                // 汇总后台加载结果（此处已回到 UI 线程）：可对比文件不足 2 个则视为进入失败
                if (_multiFileVariables.Count < 2)
                {
                    MessageBox.Show(
                        LanguageManager.Instance.GetText("Editor.NoTemplateMatch", "未能加载可对比的同模板文件。"),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _multiFileVariables.Clear();
                    _multiFileOrigLines.Clear();
                    _multiFileVarLookup.Clear();
                    return;
                }
                if (failed.Count > 0)
                    LogOutput(string.Format(
                        LanguageManager.Instance.GetText("Editor.MultiFileLoadFailed", "以下文件加载失败，已跳过：{0}"),
                        string.Join("、", failed)), "WARN");

                _multiFileRelPaths = all.Where(p => _multiFileVariables.ContainsKey(p)).ToList();
                SetMultiFileColumns(_multiFileRelPaths);   // 重建列（量小，UI 线程直接做）

                // 阶段2：表格行构建同样计入加载动画（后台构建 + 进度），完成后回 UI 线程一次挂数据源。
                // 943 行 × 26 列级别的表在 UI 线程构建+挂载会明显卡顿，这里把可移出 UI 线程的部分全部移出。
                string filterText = _fieldFilterTextBox?.Text ?? "";
                string outlineTag = _currentOutlineTag;
                var fillCfg = new AntdUI.Spin.Config
                {
                    Text = LanguageManager.Instance.GetText("Editor.MultiFileFill", "正在填充表格..."),
                    CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                    CancelTokenSource = _multiLoadCts
                };
                List<DbrGridRow> gridRows = null;
                await AntdUI.Spin.open(spinTarget, fillCfg, async cfg =>
                {
                    await Task.Run(() =>
                    {
                        gridRows = BuildDbrGridRows(filterText, outlineTag, (cur, tot) =>
                        {
                            string msg = string.Format(
                                LanguageManager.Instance.GetText("Editor.MultiFileFillProgress", "正在填充表格... {0}/{1}"),
                                cur, tot);
                            try
                            {
                                if (IsHandleCreated)
                                    BeginInvoke((Action)(() => { cfg.Text = msg; }));
                            }
                            catch { }
                        });
                    }, ct);
                    // 回 UI 线程挂数据源（后台线程不能触碰网格控件）
                    await OnUiAsync(() => _dbrGrid.DataSource = gridRows);
                });
                UpdateSameTemplateButtonText();
                if (_currentVariables != null && _currentTemplate != null)
                    BuildOutlineTree(_currentVariables, _currentTemplate);
            }
            catch (OperationCanceledException)
            {
                // 用户取消：清掉本次加载的半成品缓存（保留进入前状态）
                foreach (var p in all)
                {
                    if (!string.Equals(p, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _multiFileVariables.Remove(p);
                        _multiFileOrigLines.Remove(p);
                        _multiFileVarLookup.Remove(p);
                    }
                }
            }
            finally
            {
                _isMultiLoading = false;
                _multiLoadCts?.Dispose();
                _multiLoadCts = null;
            }
        }

        /// <summary>在 UI 线程执行动作并等待完成（后台构建表格行后挂数据源用）</summary>
        private Task OnUiAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (IsDisposed || !IsHandleCreated) { tcs.SetResult(true); return tcs.Task; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    try { action(); tcs.SetResult(true); }
                    catch (Exception ex) { tcs.SetException(ex); }
                }));
            }
            catch (Exception ex) { tcs.SetException(ex); }
            return tcs.Task;
        }

        private bool ConfirmExitMultiFileForOpen(string relPath)
        {
            if (_multiFileRelPaths.Count <= 1) return true;
            if (string.Equals(relPath, _multiFileRelPaths[0], StringComparison.OrdinalIgnoreCase)) return true;
            string msg = string.Format(
                LanguageManager.Instance.GetText("Editor.ConfirmExitMultiFile",
                    "当前处于多文件比较模式（{0} 个文件），打开其他文件将退出比较模式。是否继续？"),
                _multiFileRelPaths.Count);
            var r = MessageBox.Show(msg,
                LanguageManager.Instance.GetText("Common.Confirm", "确认"),
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return false;
            ExitMultiFileMode();
            return true;
        }

        private void ExitMultiFileMode()
        {
            _multiFileRelPaths.Clear();
            _multiFileVariables.Clear();
            _multiFileOrigLines.Clear();
            _multiFileVarLookup.Clear();
            ResetDbrGridColumnSort();   // 同上：先清列排序状态，再改列集合
            for (int i = _dbrGrid.Columns.Count - 1; i >= 0; i--)
            {
                string key = _dbrGrid.Columns[i].Key;
                if (key != null && (key == "FieldValue" || key.StartsWith("V_", StringComparison.Ordinal)))
                    _dbrGrid.Columns.RemoveAt(i);
            }
            EnsureBaseColumns();
            _dbrGrid.ColumnDragSort = false;
            UpdateSameTemplateButtonText();
            RefreshDbrGrid();
            if (_currentVariables != null && _currentTemplate != null)
                BuildOutlineTree(_currentVariables, _currentTemplate);
            LogOutput(LanguageManager.Instance.GetText("Editor.ExitMultiModeDone", "已退出多文件编辑模式"), "INFO");
        }

        private void CloseFileColumn(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_multiFileRelPaths.Count <= 1) return;
            string fileName = Path.GetFileName(path);
            string msg = string.Format(
                LanguageManager.Instance.GetText("Editor.MultiFileCloseConfirm", "确定关闭文件 {0}？"),
                fileName);
            var config = new AntdUI.Modal.Config(
                LanguageManager.Instance.GetText("Common.Confirm", "确认"),
                msg)
            {
                OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                MaskClosable = false,
                OnOk = c =>
                {
                    _multiFileRelPaths.Remove(path);
                    _multiFileVariables.Remove(path);
                    _multiFileOrigLines.Remove(path);
                    _multiFileVarLookup.Remove(path);
                    for (int i = _dbrGrid.Columns.Count - 1; i >= 0; i--)
                    {
                        string key = _dbrGrid.Columns[i].Key;
                        if (key == "V_" + path)
                            _dbrGrid.Columns.RemoveAt(i);
                    }
                    if (_multiFileRelPaths.Count <= 1)
                    {
                        ExitMultiFileMode();
                    }
                    else
                    {
                        UpdateSameTemplateButtonText();
                        RefreshDbrGrid();
                        if (_currentVariables != null && _currentTemplate != null)
                            BuildOutlineTree(_currentVariables, _currentTemplate);
                    }
                    return true;
                }
            };
            AntdUI.Modal.open(config);
        }

        private void SetBaseColumn(string path)
        {
            if (string.IsNullOrEmpty(path) || _multiFileRelPaths.Count < 2) return;
            int idx = _multiFileRelPaths.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (idx <= 0) return;
            var list = new List<string>(_multiFileRelPaths);
            list.RemoveAt(idx);
            list.Insert(0, path);
            _multiFileRelPaths = list;
            SetMultiFileColumns(list);
            RefreshDbrGrid();
            if (_currentVariables != null && _currentTemplate != null)
                BuildOutlineTree(_currentVariables, _currentTemplate);
            LogOutput(string.Format(
                LanguageManager.Instance.GetText("Editor.SetBaseDone", "已切换对比基准为 {0}"),
                Path.GetFileName(path)), "INFO");
        }

        private void UpdateSameTemplateButtonText()
        {
            if (_btnOpenSameTemplate == null) return;
            _btnOpenSameTemplate.Text = _multiFileRelPaths.Count > 0
                ? LanguageManager.Instance.GetText("Editor.ExitMultiMode", "退出多文件编辑模式")
                : LanguageManager.Instance.GetText("Editor.OpenSameTemplate", "同模板文件同时编辑");
            if (_btnBatchEdit != null)
                _btnBatchEdit.Visible = _multiFileRelPaths.Count > 1;
        }

        private bool LoadDbrVariablesToCache(string path)
        {
            if (string.Equals(path, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
            {
                _multiFileVariables[path] = _currentVariables;
                _multiFileOrigLines[path] = _originalDbrLines;
                _multiFileVarLookup[path] = BuildVarLookup(_currentVariables);
                return true;
            }
            var nd = FindNodeDataByPath(_originalRootNodes, path.Split('/'), 0);
            string[] lines = ReadDbrLines(path, nd?.RecordIndex);
            if (lines == null) return false;
            string tpl = TemplateHelper.GetDbrTemplateName(lines);
            TemplateNode rootTpl = null;
            if (tpl != null)
            {
                // 统一解析口径（TemplateNameResolver）：兼容非标准模板路径写法
                rootTpl = TemplateNameResolver.Resolve(_templates, tpl).Template;
            }
            if (rootTpl == null) return false;
            var dbrValues = TemplateHelper.ParseDbrLines(lines);
            var vars = TemplateHelper.CollectAllVariables(rootTpl, dbrValues);
            if (vars == null) return false;
            _multiFileVariables[path] = vars;
            _multiFileVarLookup[path] = BuildVarLookup(vars);
            var orig = new List<string>();
            for (int i = 1; i < lines.Length; i++) orig.Add(lines[i]);
            _multiFileOrigLines[path] = orig;
            return true;
        }

        private string[] ReadDbrLines(string relPath, int? recordIndex)
        {
            string tempFile = _session.GetTempFilePath(relPath);
            if (File.Exists(tempFile))
                return EncodingService.ReadTextFile(tempFile).Replace("\r\n", "\n").Split('\n');
            if (_isArz && recordIndex.HasValue && _arzReader != null)
            {
                // 独立 FileStream + 双参 GetRecord：不触碰 _arzReader 共享流，也不把记录塞进全局缓存（避免污染内存）
                using (var fs = new FileStream(_archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var record = _arzReader.GetRecord(recordIndex.Value, fs);
                    using (var ms = new MemoryStream())
                    {
                        record.SaveToStream(ms);
                        ms.Position = 0;
                        using (var sr = new StreamReader(ms, Utf8NoBom))
                            return sr.ReadToEnd().Replace("\r\n", "\n").Split('\n');
                    }
                }
            }
            if (!_isArz && recordIndex.HasValue && _arcFile != null)
            {
                var entry = _arcFile.toc[recordIndex.Value];
                using (var ms = new MemoryStream())
                {
                    _arcFile.UnpackToStream(entry, ms);
                    string text = TemplateHelper.DecodeText(ms.ToArray());
                    return text.Replace("\r\n", "\n").Split('\n');
                }
            }
            return null;
        }

        private void SetMultiFileColumns(List<string> paths)
        {
            _dbrGrid.ColumnDragSort = true;
            // 本方法会整批重建 V_ 列，旧列的下标表对新列集合无意义（且跨会话残留会导致缺列/错列），必须先复位
            ResetDbrGridColumnSort();
            for (int i = _dbrGrid.Columns.Count - 1; i >= 0; i--)
            {
                string key = _dbrGrid.Columns[i].Key;
                if (key == "FieldValue" || key.StartsWith("V_", StringComparison.Ordinal))
                    _dbrGrid.Columns.RemoveAt(i);
            }
            foreach (var p in paths)
            {
                string key = "V_" + p;
                string display = Path.GetFileName(p);
                if (string.Equals(p, paths[0], StringComparison.OrdinalIgnoreCase))
                    display = "★ " + display;
                if (string.Equals(p, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
                    display = "* " + display;
                var col = new AntdUI.Column(key, display)
                {
                    Ellipsis = true,
                    Width = "300",
                    DragSort = true
                };
                string path = p;
                col.Render = (value, record, index) => BuildDbrMultiValueCellRender(value, record, index, path);
                _dbrGrid.Columns.Add(col);
            }
            foreach (var c in _dbrGrid.Columns)
            {
                if (c.Key == "FieldNameCell" || c.Key == "FieldClass" || c.Key == "FieldType")
                    c.Width = "auto";
            }
        }

        private string GetFileFieldValue(string path, string name)
        {
            if (!_multiFileVariables.TryGetValue(path, out var vars)) return null;
            // O(1) 快路径：进入多文件模式时建立的 变量名→变量 映射（渲染每格都调用，线性扫描会拖慢大表）
            if (_multiFileVarLookup.TryGetValue(path, out var map) && map.TryGetValue(name, out var hit))
                return hit.Value ?? "";
            // 回退线性扫描：覆盖查找表建立后新增的字段行
            foreach (var v in vars)
            {
                if (v != null && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                    return v.Value ?? "";
            }
            return null;
        }

        /// <summary>构建 变量名→变量 映射（忽略大小写；跳过空项与重名）</summary>
        private static Dictionary<string, TemplateVariableInfo> BuildVarLookup(List<TemplateVariableInfo> vars)
        {
            var map = new Dictionary<string, TemplateVariableInfo>(StringComparer.OrdinalIgnoreCase);
            if (vars == null) return map;
            foreach (var v in vars)
            {
                if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                if (!map.ContainsKey(v.Name)) map.Add(v.Name, v);
            }
            return map;
        }

        private string ShowDbrFileBrowser(string currentValue)
        {
            if (_originalRootNodes == null || _originalRootNodes.Count == 0)
            {
                MessageBox.Show(
                    LanguageManager.Instance.GetText("Editor.NoDbrFiles", "当前包内没有可选择的文件，请先加载 ARZ/ARC 文件。"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            var filteredRoots = FilterDbrOnly(_originalRootNodes);
            if (filteredRoots.Count == 0)
            {
                MessageBox.Show(
                    LanguageManager.Instance.GetText("Editor.NoDbrFiles", "当前包内没有可选择的文件，请先加载 ARZ/ARC 文件。"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            AntdUI.TreeItem selectedNode = null;
            var tree = new AntdUI.Tree
            {
                Dock = DockStyle.Fill,
                Font = FontHelper.UiFont(9),
                Checkable = false
            };

            void RebuildTree(List<NodeData> roots, bool expandAll)
            {
                tree.Items.Clear();
                foreach (var root in roots)
                {
                    var item = BuildBrowserTreeItem(root);
                    item.Expand = true;
                    tree.Items.Add(item);
                }
                if (expandAll) tree.ExpandAll();
            }

            RebuildTree(filteredRoots, false);

            tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var data = e.Item.Tag as NodeData;
                if (data == null) return;
                if (data.IsDirectory)
                {
                    if (e.Type != AntdUI.TreeCType.Arrow && e.Item.Sub.Count > 0)
                        e.Item.Expand = !e.Item.Expand;
                    return;
                }
                selectedNode = e.Item;
                tree.SelectItem = e.Item;
            };

            // 回显当前值：展开对应目录并选中文件
            if (!string.IsNullOrEmpty(currentValue))
            {
                string norm = currentValue.Trim().Replace('\\', '/').Trim('/');
                if (norm.Length > 0)
                {
                    var match = FindBrowserNode(tree.Items, norm);
                    if (match != null)
                    {
                        var cur = match;
                        while (cur != null) { cur.Expand = true; cur = cur.ParentItem; }
                        selectedNode = match;
                        tree.SelectItem = match;
                    }
                }
            }

            // 过滤输入框（防抖 200ms）
            var filterTimer = new System.Windows.Forms.Timer { Interval = 200 };
            var filterInput = new AntdUI.Input
            {
                Dock = DockStyle.Top,
                Height = 38,
                Font = FontHelper.UiFont(9),
                PrefixText = LanguageManager.Instance.GetText("Editor.BrowserFilterPrefix", "过滤") + "：",
                AllowClear = true
            };
            filterInput.TextChanged += (s, e) =>
            {
                filterTimer.Stop();
                filterTimer.Start();
            };
            filterTimer.Tick += (s, e) =>
            {
                filterTimer.Stop();
                string kw = (filterInput.Text ?? "").Trim();
                bool hasKw = kw.Length > 0;
                var roots = !hasKw
                    ? filteredRoots
                    : FilterTreeByKeyword(filteredRoots, kw);
                RebuildTree(roots, hasKw);

                if (hasKw)
                {
                    var exact = FindExactNode(tree.Items, kw);
                    if (exact != null)
                    {
                        selectedNode = exact;
                        tree.SelectItem = exact;
                        tree.Focus(exact);
                        return;
                    }
                }

                if (selectedNode != null)
                {
                    string path = GetNodePath(selectedNode);
                    var match = FindBrowserNode(tree.Items, path);
                    if (match != null)
                    {
                        selectedNode = match;
                        var cur = match;
                        while (cur != null) { cur.Expand = true; cur = cur.ParentItem; }
                        tree.SelectItem = match;
                    }
                    else
                    {
                        selectedNode = null;
                    }
                }
            };

            var content = new AntdUI.Panel
            {
                Width = 420,
                Height = 480,
                BackColor = Color.Transparent
            };
            content.Controls.Add(tree);
            content.Controls.Add(filterInput);

            string result = null;
            var config = new AntdUI.Modal.Config(
                this,
                LanguageManager.Instance.GetText("Editor.DbrFileBrowserTitle", "选择DBR文件"),
                content)
            {
                Width = 420,
                Padding = new Size(12, 12),
                OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                OkType = AntdUI.TTypeMini.Primary,
                MaskClosable = false,
                Draggable = true,
                OnOk = cfg =>
                {
                    filterTimer.Stop();
                    if (selectedNode == null)
                    {
                        MessageBox.Show(
                            LanguageManager.Instance.GetText("Editor.SelectDbrFirst", "请先在左侧选择要引用的 DBR 文件。"),
                            LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return false;
                    }
                    result = GetNodePath(selectedNode);
                    return true;
                }
            };
            AntdUI.Modal.open(config);
            filterTimer.Stop();
            return result;
        }

        private static List<NodeData> FilterDbrOnly(List<NodeData> nodes)
        {
            var result = new List<NodeData>();
            foreach (var nd in nodes)
            {
                if (!nd.IsDirectory)
                {
                    if (nd.Name.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(new NodeData
                        {
                            Name = nd.Name,
                            IsDirectory = false,
                            FileTime = nd.FileTime,
                            RecordIndex = nd.RecordIndex,
                            RecordType = nd.RecordType
                        });
                    }
                    continue;
                }
                var filteredChildren = FilterDbrOnly(nd.Children);
                if (filteredChildren.Count > 0)
                {
                    result.Add(new NodeData { Name = nd.Name, IsDirectory = true, Children = filteredChildren });
                }
            }
            return result;
        }

        private static AntdUI.TreeItem BuildBrowserTreeItem(NodeData data)
        {
            var item = new AntdUI.TreeItem { Text = data.Name, Tag = data };
            foreach (var child in data.Children)
                item.Sub.Add(BuildBrowserTreeItem(child));
            return item;
        }

        private static List<NodeData> FilterTreeByKeyword(List<NodeData> nodes, string keyword)
        {
            var result = new List<NodeData>();
            foreach (var nd in nodes)
            {
                bool nameMatch = nd.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                if (nd.IsDirectory)
                {
                    var filteredChildren = FilterTreeByKeyword(nd.Children, keyword);
                    if (nameMatch)
                    {
                        // 目录名匹配：整个目录子树都符合过滤条件，直接保留
                        result.Add(nd);
                    }
                    else if (filteredChildren.Count > 0)
                    {
                        result.Add(new NodeData { Name = nd.Name, IsDirectory = true, Children = filteredChildren });
                    }
                }
                else if (nameMatch)
                {
                    result.Add(nd);
                }
            }
            return result;
        }

        private AntdUI.TreeItem FindBrowserNode(AntdUI.TreeItemCollection items, string path)
        {
            foreach (var item in items)
            {
                if (item.Tag is NodeData)
                {
                    if (string.Equals(GetNodePath(item), path, StringComparison.OrdinalIgnoreCase))
                        return item;
                }
                if (item.Sub != null && item.Sub.Count > 0)
                {
                    var found = FindBrowserNode(item.Sub, path);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private AntdUI.TreeItem FindExactNode(AntdUI.TreeItemCollection items, string kw)
        {
            foreach (var item in items)
            {
                if (item.Tag is NodeData nd && !nd.IsDirectory)
                {
                    string name = nd.Name;
                    string bare = name.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase)
                        ? name.Substring(0, name.Length - 4)
                        : name;
                    if (string.Equals(name, kw, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(bare, kw, StringComparison.OrdinalIgnoreCase))
                        return item;
                }
                if (item.Sub != null && item.Sub.Count > 0)
                {
                    var found = FindExactNode(item.Sub, kw);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private string BuildDbrText()
        {
            if (string.IsNullOrEmpty(_currentFileRelPath)) return null;

            var outLines = new List<string>();

            // templateName 行
            if (!string.IsNullOrEmpty(_currentDbrTplName))
                outLines.Add($"templateName,{_currentDbrTplName},");
            else if (_currentTemplate != null)
            {
                string tplPath = _currentTemplate.GetTemplateFile();
                string tplKey = tplPath != null
                    ? "database/templates/" + Path.GetFileNameWithoutExtension(tplPath)
                    : "";
                if (!string.IsNullOrEmpty(tplKey))
                    outLines.Add($"templateName,{tplKey},");
            }
            // else: 无模板时保持原始内容，不凭空添加 templateName 行

            // 以原始 DBR 内容为基础，仅覆盖有修改的字段
            if (_originalDbrLines != null)
            {
                var lineMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _originalDbrLines.Count; i++)
                {
                    string line = _originalDbrLines[i];
                    string key = line.Split(',')[0].Trim();
                    if (!string.IsNullOrEmpty(key))
                        lineMap[key] = i;
                }

                var saveLines = new List<string>(_originalDbrLines);

                if (_modifiedDbrFields.TryGetValue(_currentFileRelPath, out var modFields))
                {
                    foreach (var v in _currentVariables)
                    {
                        if (v != null && modFields.Contains(v.Name))
                        {
                            string newLine = $"{v.Name},{v.Value},";
                            string varName = v.Name.Trim();
                            int idx = -1;
                            if (lineMap.TryGetValue(varName, out idx))
                                saveLines[idx] = newLine;
                            else
                            {
                                saveLines.Add(newLine);
                            }
                        }
                    }
                }

                outLines.AddRange(saveLines);
            }
            else
            {
                foreach (var v in _currentVariables)
                    outLines.Add($"{v.Name},{v.Value},");
            }

            return string.Join("\r\n", outLines);
        }

        /// <summary>
        /// 把导入的文件写入临时目录。当设置「ARC 文本编码 = 转 UTF-8 无 BOM」开启时，
        /// 文本类文件（.dbr/.txt/.lua/.tpl，且不超过体积上限、不含 NUL 字节）先规范化为 UTF-8 无 BOM 再写入；
        /// 这样"临时文件即最终内容"的增量保存写回包内的编码天然统一（保存侧无需再做任何转换）。
        /// 非文本文件、关闭开关、超限或二进制文件一律原样拷贝。
        /// 编辑器自身编辑产生的临时文件本就以 UTF-8 无 BOM 写入（TryWriteTempText），无需在此处理。
        /// </summary>
        /// <summary>
        /// 名字编码预检（编辑器导入路径，与打包侧同一口径）：
        /// ARC 的包内名字必须能用本机码页表示，否则一律不导入
        /// （否则保存打包时要么失败，要么产出不可恢复的 '?' 坏名）。
        /// ARZ 的字符串表是 UTF-8，不受本机码页限制。
        /// </summary>
        private bool CheckImportNameUsable(string relPath, IProgressCallback progress)
        {
            if (_isArz) return true;
            if (NameCodec.TryEncode(relPath, out _, out char badChar, out int badCodePoint)) return true;

            string badText = badCodePoint > 0xFFFF ? char.ConvertFromUtf32(badCodePoint) : badChar.ToString();
            progress?.ReportWarning(string.Format(
                LanguageManager.Instance.GetText("ArcName.ImportSkipped",
                    "无法导入（文件名无法用本机码页 {0} 表示）：{1}（字符 '{2}' U+{3:X4}）"),
                NameCodec.AcpName, relPath, badText, badCodePoint));
            return false;
        }

        private void WriteImportedFileToTemp(string sourcePath, string relPath, string destPath, IProgressCallback progress)
        {
            if (!SettingsManager.ShouldNormalizeArcText || !TextFileHelper.IsTextFile(relPath))
            {
                File.Copy(sourcePath, destPath, overwrite: true);
                return;
            }

            try
            {
                if (new FileInfo(sourcePath).Length > TextFileHelper.MaxNormalizeBytes)
                {
                    progress?.ReportWarning(
                        $"跳过文本编码转换: {relPath}（超过 {TextFileHelper.MaxNormalizeBytes / (1024 * 1024)}MB 上限）");
                    File.Copy(sourcePath, destPath, overwrite: true);
                    return;
                }

                byte[] src = File.ReadAllBytes(sourcePath);
                // 目标编码来自【设置 → 编码 → 内容打包】（原样档 = null → 直接返回源字节）
                var outcome = EncodingService.NormalizeForPack(src, out byte[] dst, out string reason);
                if (outcome == TextFileHelper.NormalizeOutcome.Skipped)
                {
                    progress?.ReportWarning(string.Format(
                        LanguageManager.Instance.GetText("LOG.SkipTextTranscode", "跳过文本编码转换: {0}（{1}）"),
                        relPath, reason));
                    File.Copy(sourcePath, destPath, overwrite: true);
                    return;
                }

                File.WriteAllBytes(destPath, dst);
                // 导入遵循"实体文件时间戳"：File.Copy 自动保留源时间，转码写出（WriteAllBytes）不会，需手动补齐，
                // 保证编辑器保存新条目入包时取到的临时文件时间 = 导入源文件时间
                try { File.SetLastWriteTime(destPath, File.GetLastWriteTime(sourcePath)); } catch { }
                if (outcome == TextFileHelper.NormalizeOutcome.Converted && SettingsManager.ShowDetailLog)
                    progress?.ReportLog(string.Format(
                        LanguageManager.Instance.GetText("LOG.TextTranscode", "文本编码转换: {0}（{1}）"),
                        relPath, reason));
            }
            catch (Exception ex)
            {
                progress?.ReportWarning(string.Format(
                    LanguageManager.Instance.GetText("LOG.TextTranscodeImportFailed",
                        "文本编码转换失败，按原样导入: {0}（{1}）"),
                    relPath, ex.Message));
                try { File.Copy(sourcePath, destPath, overwrite: true); } catch { }
            }
        }

        private bool TryWriteTempText(string tempFile, string text)
        {
            // 同步重试是刻意的：调用链处在保存/切页签的同步契约里，改 async 会扩散到多个事件入口；
            // 8×50ms（上限 350ms）只用于等待杀软/编辑器/同步盘释放瞬时文件句柄。
            const int retries = 8;
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    File.WriteAllText(tempFile, text, EncodingService.TempWriteEncoding);
                    return true;
                }
                catch (IOException)
                {
                    if (i == retries - 1) return false;
                    Thread.Sleep(50);
                }
            }
            return false;
        }

        private void NotifyTempWriteFailed(string relPath)
        {
            MessageBox.Show(
                string.Format(LanguageManager.Instance.GetText("Editor.TempWriteFailed", "文件被占用，保存失败，请稍后重试：{0}"), relPath),
                LanguageManager.Instance.GetText("Common.MessageBoxTitle.Error", "错误"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private bool SaveCurrentDbrToTemp()
        {
            _fileListLoadCts?.Cancel();
            string text = BuildDbrText();
            if (text == null) return false;

            string tempFile = _session.GetTempFilePath(_currentFileRelPath);
            if (!TryWriteTempText(tempFile, text))
            {
                NotifyTempWriteFailed(_currentFileRelPath);
                return false;
            }
            _modifiedFiles.Add(_currentFileRelPath);
            _editorDirty = false;
            return true;
        }

        private bool SaveDbrToTemp(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            _fileListLoadCts?.Cancel();
            string text;
            if (string.Equals(relPath, _currentFileRelPath, StringComparison.OrdinalIgnoreCase))
            {
                text = BuildDbrText();
            }
            else
            {
                if (!_multiFileVariables.TryGetValue(relPath, out var vars)) return false;
                _multiFileOrigLines.TryGetValue(relPath, out var origLines);
                text = BuildDbrTextFor(relPath, vars, origLines);
            }
            if (text == null) return false;

            string tempFile = _session.GetTempFilePath(relPath);
            if (!TryWriteTempText(tempFile, text))
            {
                NotifyTempWriteFailed(relPath);
                return false;
            }
            _modifiedFiles.Add(relPath);
            return true;
        }

        /// <summary>
        /// 为指定文件构建 DBR 文本：templateName 行沿用当前模板名（同模板文件），
        /// 以该文件原始逐行为基础，仅覆盖 _modifiedDbrFields[relPath] 中的修改字段。
        /// </summary>
        private string BuildDbrTextFor(string relPath, List<TemplateVariableInfo> variables, List<string> origLines)
        {
            if (variables == null) return null;
            var outLines = new List<string>();

            if (!string.IsNullOrEmpty(_currentDbrTplName))
                outLines.Add($"templateName,{_currentDbrTplName},");

            if (origLines != null)
            {
                var lineMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < origLines.Count; i++)
                {
                    string line = origLines[i];
                    string key = line.Split(',')[0].Trim();
                    if (!string.IsNullOrEmpty(key))
                        lineMap[key] = i;
                }

                var saveLines = new List<string>(origLines);

                if (_modifiedDbrFields.TryGetValue(relPath, out var modFields))
                {
                    foreach (var v in variables)
                    {
                        if (v != null && modFields.Contains(v.Name))
                        {
                            string varName = v.Name.Trim();
                            // 无键不新增：原无此键且新值为空则保持缺省，不生 aaa,,
                            if (string.IsNullOrEmpty(v.Value) && !lineMap.ContainsKey(varName))
                                continue;
                            string newLine = $"{v.Name},{v.Value},";
                            if (lineMap.TryGetValue(varName, out int idx))
                                saveLines[idx] = newLine;
                            else
                                saveLines.Add(newLine);
                        }
                    }
                }

                outLines.AddRange(saveLines);
            }
            else
            {
                foreach (var v in variables)
                {
                    if (string.IsNullOrEmpty(v.Value)) continue;
                    outLines.Add($"{v.Name},{v.Value},");
                }
            }

            return string.Join("\r\n", outLines);
        }

        // ======================== DBR Tab Switching (Grid ↔ Source) ========================

        private void OnDbrTabSelectedIndexChanged(object sender, AntdUI.IntEventArgs e)
        {
            if (_isLoadingFile) return;
            if (string.IsNullOrEmpty(_currentFileRelPath)) return;

            int idx = _editorTabControl.SelectedIndex;

            if (idx == 0)
            {
                // 文件列表 — 不执行数据同步。
                // 但仍要落一次行级变更记录：从源码页切到文件列表，与"切回网格"一样属于离开源码页。
                LogSourceLineChanges();
                return;
            }

            // 检查是否有待加载文件（单击选择后切到编辑器Tab时触发）
            if (_pendingFileData != null)
            {
                var nd = _pendingFileData;
                var path = _currentFileRelPath;
                _pendingFileData = null;
                if (nd.RecordIndex.HasValue)
                    OpenFileForEditing(nd, path);
                else
                    OpenNewBlankFile(path);
                return;
            }

            if (_currentTemplate == null)
            {
                // 非 dbr 文件（.txt / .lua / .tpl，或模板缺失的 dbr）没有模板、也没有可用的网格页签，
                // 此前直接 return，导致"编辑后切页签"永远到不了下面的 idx==1 分支 → 非 dbr 没有 [行] 日志。
                // 这里在离开源码页（idx != 2）时补一次行级记录：基线随输出推进，重复调用不会重复记。
                if (idx != 2) LogSourceLineChanges();
                return;
            }

            if (idx == 2)
            {
                // 源码模式：从 DBR 网格构建源码文本
                if (_debounceTimer.Enabled)
                {
                    _debounceTimer.Stop();
                }

                string content = BuildDbrText() ?? "";
                _isLoadingFile = true;
                _dbrSourceEditor.Text = content;
                PumpEditorMessages();
                _isLoadingFile = false;
                CaptureSourceBaseline();   // 进入源码模式：以刚从网格生成的文本为基线（在源码模式里改了什么，从此刻起算）
                // 手动从 DBR 网格切到源码模式：内容已变更，重新应用按扩展名的语法着色（dbr 手动逗号染色）
                ApplySourceSyntaxCurrent();
            }
            else if (idx == 1)
            {
                // 离开源码模式（切回网格）：先把源码模式里的行级改动落进变更记录
                LogSourceLineChanges();

                // 编辑器模式：从源码文本解析回 DBR 网格
                string text = _dbrSourceEditor.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    string[] lines = text.Replace("\r\n", "\n").Split('\n');
                    var dbrValues = TemplateHelper.ParseDbrLines(lines);
                    _currentVariables = TemplateHelper.CollectAllVariables(_currentTemplate, dbrValues);
                    _notInTemplateVars = TemplateHelper.CollectNotInTemplateVariables(dbrValues, _currentVariables);

                    _originalDbrLines = new List<string>();
                    for (int i = 1; i < lines.Length; i++)
                    {
                        _originalDbrLines.Add(lines[i]);
                    }

                    if (_multiFileRelPaths.Count > 1)
                    {
                        // 多文件模式：源码只联动首列，将首列缓存同步为解析结果，
                        // 并标记变化字段（红字），保证逐文件保存正确
                        if (_multiFileVariables.TryGetValue(_currentFileRelPath, out var oldVars))
                        {
                            if (!_modifiedDbrFields.TryGetValue(_currentFileRelPath, out var fs))
                            {
                                fs = new HashSet<string>();
                                _modifiedDbrFields[_currentFileRelPath] = fs;
                            }
                            foreach (var nv in _currentVariables)
                            {
                                if (nv == null) continue;
                                var old = oldVars.FirstOrDefault(x => x != null && string.Equals(x.Name, nv.Name, StringComparison.OrdinalIgnoreCase));
                                if (old == null || !string.Equals(old.Value ?? "", nv.Value ?? "", StringComparison.Ordinal))
                                    fs.Add(nv.Name);
                            }
                        }
                        _multiFileVariables[_currentFileRelPath] = _currentVariables;
                        _multiFileOrigLines[_currentFileRelPath] = _originalDbrLines;
                        RefreshDbrGrid();
                    }
                    else if (_currentVariables == null || _currentVariables.Count == 0)
                    {
                        RefreshDbrGrid();
                    }
                    else
                    {
                        ShowDbrGrid(_currentVariables, _currentTemplate);
                    }
                }
                else
                {
                    // 源码文本为空（首次打开尚未切到源码模式），直接刷新网格
                    RefreshDbrGrid();
                }
            }
        }

        private void ReloadDbrFromTemp()
        {
            string tempFile = _session.GetTempFilePath(_currentFileRelPath);
            if (!File.Exists(tempFile)) return;

            string text = EncodingService.ReadTextFile(tempFile);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');

            var dbrValues = TemplateHelper.ParseDbrLines(lines);
            _currentVariables = TemplateHelper.CollectAllVariables(_currentTemplate, dbrValues);
            _notInTemplateVars = TemplateHelper.CollectNotInTemplateVariables(dbrValues, _currentVariables);

            // 保留原始逐行内容（含空行、缩进），用于保存时覆盖匹配
            _originalDbrLines = new List<string>();
            for (int i = 1; i < lines.Length; i++)
            {
                _originalDbrLines.Add(lines[i]);
            }

            ShowDbrGrid(_currentVariables, _currentTemplate);
        }

        private void OnDbrSourceTextChanged(object sender, EventArgs e)
        {
            if (_isLoadingFile) return;
            if (!string.IsNullOrEmpty(_currentFileRelPath))
            {
                string tempFile = _session.GetTempFilePath(_currentFileRelPath);
                try
                {
                    File.WriteAllText(tempFile, _dbrSourceEditor.Text, EncodingService.TempWriteEncoding);
                    MarkFileModified(_currentFileRelPath);
                    _editorDirty = false;
                }
                catch (IOException)
                {
                    _editorDirty = true;
                }

                // 行级日志实时化：与临时文件写入同节奏防抖，停顿 0.5s 后自动落一次 [行] 记录
                // （Tick 内做整段 diff 并推进基线；基线守卫保证切页签/关窗的兜底调用不重复记录）
                _sourceLogTimer?.Stop();
                _sourceLogTimer?.Start();
            }
        }

        // ======================== DBR Grid Filter, Sort, Find, Font ========================

        private void RefreshDbrGrid()
        {
            if (_currentVariables == null) return;
            try
            {
                _dbrGrid.DataSource = BuildDbrGridRows(_fieldFilterTextBox?.Text ?? "", _currentOutlineTag);
            }
            catch (Exception ex)
            {
                string ctx = $"rows={(_dbrGrid.DataSource as List<DbrGridRow>)?.Count ?? 0}, cols={_dbrGrid.Columns.Count}, vars={_currentVariables?.Count}";
                LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.DbrGridRefreshError", "刷新DBR网格异常: {0} | {1}"), ex.Message, ctx), "ERROR");
                ShowPlaceholder();
                throw;
            }
        }

        /// <summary>
        /// 构建 DBR 网格行（纯数据构建，可在后台线程执行：只读变量表与筛选状态，不触碰网格控件）。
        /// 多文件模式进入时的表格填充由后台调用并回报进度，避免大表在 UI 线程卡顿。
        /// </summary>
        /// <param name="filterText">字段筛选文本（调用方在 UI 线程取好快照传入）</param>
        /// <param name="outlineTag">当前大纲分组标记（调用方在 UI 线程取好快照传入）</param>
        /// <param name="progress">可选进度回调 (已构建行数, 总行数)</param>
        private List<DbrGridRow> BuildDbrGridRows(string filterText, string outlineTag, Action<int, int> progress = null)
        {
            try
            {
            bool isAllView = outlineTag == "__all__" || outlineTag == "__with_value__" || outlineTag == "__non_zero__";
            bool showTpl = isAllView && (string.IsNullOrEmpty(filterText) ||
                "templateName".IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0);

            var matched = new List<TemplateVariableInfo>();
            foreach (var v in _currentVariables)
            {
                if (v == null) continue;
                bool isHeader = string.Equals(v.GroupName, "Header", StringComparison.OrdinalIgnoreCase);
                bool outlineMatch = outlineTag == "__all__" ||
                    (outlineTag == "__with_value__" && (!string.IsNullOrEmpty(v.Value) || isHeader)) ||
                    (outlineTag == "__non_zero__" && ((v.Type != "bool" && IsNonZeroValue(v.Value)) || isHeader)) ||
                    (outlineTag == "__diff__" && IsFieldDifferent(v)) ||
                    (outlineTag == "__same__" && !IsFieldDifferent(v)) ||
                    string.Equals(v.GroupName, outlineTag, StringComparison.OrdinalIgnoreCase) ||
                    (v.GroupName != null && v.GroupName.StartsWith(outlineTag + "/", StringComparison.OrdinalIgnoreCase));
                if (!outlineMatch) continue;

                if (!string.IsNullOrEmpty(filterText))
                {
                    string localName = DbrKeyMapping.GetLocalizedName(v.Name);
                    bool nameMatch = v.Name.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0
                        || localName.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool valueMatch = (v.Value ?? "").IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!nameMatch && !valueMatch)
                        continue;
                }

                matched.Add(v);
            }

            matched.Sort((a, b) =>
            {
                bool aHeader = string.Equals(a.GroupName, "Header", StringComparison.OrdinalIgnoreCase);
                bool bHeader = string.Equals(b.GroupName, "Header", StringComparison.OrdinalIgnoreCase);
                if (aHeader && !bHeader) return -1;
                if (!aHeader && bHeader) return 1;
                return NaturalCompare(a.Name, b.Name);
            });

            // Build data source
            var rows = new List<DbrGridRow>();
            int total = matched.Count;

            if (showTpl)
            {
                string fileOpsText = LanguageManager.Instance.GetText("Editor.FileOpsRow", "文件操作");
                rows.Add(new DbrGridRow
                {
                    IsFileOpsRow = true,
                    FieldName = fileOpsText,
                    FieldNameCell = CreateFieldNameCell(fileOpsText),
                    FieldClass = "",
                    FieldType = "",
                    FieldValue = "",
                    RowReadOnly = true,
                    CellBackColor = ThemeManager.FileRowBack
                });
                rows.Add(new DbrGridRow
                {
                    FieldName = "templateName",
                    FieldNameCell = CreateFieldNameCell("templateName"),
                    FieldClass = "",
                    FieldType = "string",
                    FieldValue = _currentDbrTplName ?? "",
                    RowReadOnly = true,
                    CellBackColor = ThemeManager.FileRowBack
                });
            }

            HashSet<string> modFields = null;
            bool hasModFields = _currentFileRelPath != null
                && _modifiedDbrFields.TryGetValue(_currentFileRelPath, out modFields);

            foreach (var v in matched)
            {
                var row = new DbrGridRow
                {
                    FieldName = v.Name,
                    FieldNameCell = CreateFieldNameCell(v.Name),
                    FieldClass = v.Class,
                    FieldType = v.Type,
                    FieldValue = v.Value,
                    FieldDesc = v.Description,
                    Tag = v,
                    IsArray = v.IsArray
                };

                if (hasModFields && modFields.Contains(v.Name))
                    row.CellForeColor = ThemeManager.Modified;

                rows.Add(row);

                if (progress != null && rows.Count % 200 == 0)
                    progress(rows.Count, total);
            }

            // "不在模板中"的字段（DBR 有、模板无）：全量参与各特殊分组（全部/有数值两档/专属分组），
            // 追加在模板字段之后、按字段名自然排序。只读 + 警告色（打包时不入包，保存逻辑不经过这些行）。
            // 仅"仅不同/仅相同"（多文件差异对比）不参与——该对比逻辑建立在模板变量集上。
            bool notplInSpecial = outlineTag == "__all__" || outlineTag == "__notpl__"
                || outlineTag == "__with_value__" || outlineTag == "__non_zero__";
            if (notplInSpecial)
            {
                var notplVars = new List<TemplateVariableInfo>(_notInTemplateVars ?? Enumerable.Empty<TemplateVariableInfo>());
                notplVars.Sort((a, b) => NaturalCompare(a?.Name ?? "", b?.Name ?? ""));
                string notplText = LanguageManager.Instance.GetText("Editor.FieldNotInTemplate",
                    "字段不在模板中，打包时不入包");
                foreach (var v in notplVars)
                {
                    if (v == null) continue;
                    bool notplMatch = outlineTag == "__all__" || outlineTag == "__notpl__"
                        || (outlineTag == "__with_value__" && !string.IsNullOrEmpty(v.Value))
                        || (outlineTag == "__non_zero__" && v.Type != "bool" && IsNonZeroValue(v.Value));
                    if (!notplMatch) continue;
                    if (!string.IsNullOrEmpty(filterText))
                    {
                        string localName = DbrKeyMapping.GetLocalizedName(v.Name);
                        bool nameMatch = v.Name.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0
                            || localName.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool valueMatch = (v.Value ?? "").IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!nameMatch && !valueMatch) continue;
                    }

                    var nameCell = CreateFieldNameCell(v.Name);
                    nameCell.Tooltip = v.Name + "\r\n" + notplText;   // 原字段名 Tooltip 追加完整警告（50px 备注列放不下，CellText 无 Tooltip）
                    rows.Add(new DbrGridRow
                    {
                        FieldName = v.Name,
                        FieldNameCell = nameCell,
                        FieldClass = "",
                        FieldType = "",
                        FieldValue = v.Value,
                        FieldDesc = notplText,
                        Tag = v,
                        RowReadOnly = true,
                        NotInTemplate = true,
                        CellForeColor = ThemeManager.Deleted
                    });

                    if (progress != null && rows.Count % 200 == 0)
                        progress(rows.Count, total);
                }
            }
            progress?.Invoke(rows.Count, total);

            return rows;
            }
            catch (Exception ex)
            {
                LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.DbrGridRefreshError", "刷新DBR网格异常: {0} | {1}"), ex.Message, $"vars={_currentVariables?.Count}"), "ERROR");
                throw;
            }
        }

        private static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int numA = 0, numB = 0;
                    while (i < a.Length && char.IsDigit(a[i]))
                        numA = numA * 10 + (a[i++] - '0');
                    while (j < b.Length && char.IsDigit(b[j]))
                        numB = numB * 10 + (b[j++] - '0');
                    if (numA != numB) return numA.CompareTo(numB);
                }
                else
                {
                    int cmp = a[i++].CompareTo(b[j++]);
                    if (cmp != 0) return cmp;
                }
            }
            return a.Length.CompareTo(b.Length);
        }

        private void OnDbrGridSortRows(object sender, AntdUI.IntEventArgs e)
        {
            // AntdUI Table 自动切换列头 SortMode (NONE→ASC→DESC)，
            // 此处根据当前列的 SortMode 对 DataSource 排序
            if (_dbrGrid.DataSource is not List<DbrGridRow> rows || rows.Count == 0) return;
            if (e.Value < 0 || e.Value >= _dbrGrid.Columns.Count) return;

            var col = _dbrGrid.Columns[e.Value];
            string key = col.Key;
            bool ascending = col.SortMode == AntdUI.SortMode.ASC;

            Func<DbrGridRow, string> selector = key switch
            {
                "FieldNameCell" => r => r.FieldName ?? "",
                "FieldClass" => r => r.FieldClass ?? "",
                "FieldType" => r => r.FieldType ?? "",
                "FieldValue" => r => r.FieldValue ?? "",
                "FieldDesc" => r => r.FieldDesc ?? "",
                _ => r => r.FieldName ?? ""
            };

            var sorted = ascending
                ? rows.OrderBy(IsFixedTopDbrRow).ThenBy(selector).ToList()
                : rows.OrderBy(IsFixedTopDbrRow).ThenByDescending(selector).ToList();

            _dbrGrid.DataSource = sorted;
        }

        private static int IsFixedTopDbrRow(DbrGridRow r)
        {
            if (r.IsFileOpsRow) return 0;
            if (string.Equals(r.FieldName, "templateName", StringComparison.OrdinalIgnoreCase)) return 0;
            return 1;
        }

        /// <summary>
        /// 模板名 → 单元格渲染（**展示层薄封装**，查询走公共入口
        /// <see cref="TemplateNameResolver.ResolveSourceLabel"/>）：
        /// 有来源 → [来源tag(绿系)] + 模板路径 同格组合（AntdUI 原生 ICell[] 水平排列）；
        /// 无来源 → 回退 fallback 原样纯文本。仅影响渲染，数据层（排序/筛选/搜索）不感知。
        /// </summary>
        private object BuildTemplateSourceCell(string tplName, object fallback)
        {
            string label = TemplateNameResolver.ResolveSourceLabel(tplName);
            if (string.IsNullOrEmpty(label)) return fallback;
            return new AntdUI.ICell[]
            {
                new AntdUI.CellTag(label, AntdUI.TTypeMini.Success),
                new AntdUI.CellText(tplName ?? "")
            };
        }

        /// <summary>
        /// 文件列表「模板」列渲染：模板名前加来源 tag（pass 色系），仅展示。
        /// </summary>
        private object BuildFileListTemplateCellRender(object value, object record, int index)
        {
            if (record is FileListGridRow row && !string.IsNullOrEmpty(row.Template))
                return BuildTemplateSourceCell(row.Template, value);
            return value;
        }

        /// <summary>
        /// 值列渲染：字段类型对应的按钮/链接在前，值文本在后（操作列已合并进值列）。
        /// 长值不会被按钮顶没；按钮与值文本之间用 EN SPACE 占位保持间距。
        /// 差异着色（修改标红）对值文本设置 Fore，不影响原始数据。
        /// </summary>
        private object BuildDbrValueCellRender(object value, object record, int index)
        {
            if (record is not DbrGridRow row) return value;
            if (row.IsFileOpsRow)
            {
                var opsCells = new List<AntdUI.ICell>();
                if (_multiFileRelPaths.Count > 0)
                    opsCells.Add(new AntdUI.CellButton("exitMulti",
                        LanguageManager.Instance.GetText("Editor.ExitMultiMode", "退出多文件编辑模式"), AntdUI.TTypeMini.Primary));
                opsCells.Add(new AntdUI.CellButton("closeFile",
                    "✕ " + LanguageManager.Instance.GetText("Editor.CloseFile", "关闭"), AntdUI.TTypeMini.Error));
                return opsCells.ToArray();
            }
            // templateName 行：模板路径前加来源 tag（与文件列表模板列同款展示，仅渲染）
            if (string.Equals(row.FieldName, "templateName", StringComparison.OrdinalIgnoreCase))
                return BuildTemplateSourceCell(row.FieldValue, value);
            if (row.Tag is not TemplateVariableInfo v) return value;

            var cells = new List<AntdUI.ICell>();
            if (!row.RowReadOnly)
            {
                if (v.IsArray)
                    cells.Add(new AntdUI.CellButton("arr_" + row.FieldName,
                        LanguageManager.Instance.GetText("Editor.ArrayEdit", "数组"), AntdUI.TTypeMini.Default));
                else if (v.Type == "file_dbr")
                    cells.AddRange(DbrFieldHelper.GetDbrBrowseLinks());
                else if (DbrFieldHelper.IsPicklistField(v))
                    cells.Add(new AntdUI.CellButton("pick_" + row.FieldName,
                        LanguageManager.Instance.GetText("Editor.DropdownSelect", "下拉▼"), AntdUI.TTypeMini.Default));

                if (cells.Count > 0)
                    cells.Add(new AntdUI.CellText("\u2002"));
            }

            var text = new AntdUI.CellText(row.FieldValue ?? "");
            if (row.CellForeColor != Color.Empty) text.Fore = row.CellForeColor;
            cells.Add(text);
            return cells.ToArray();
        }

        /// <summary>
        /// "字段备注[官方]"列渲染：模板字段 = TPL description（官方对字段用途的定义，只读展示）；
        /// "不在模板中"的行 = 警告文案（特殊色）。CellText 无 Tooltip，完整警告挂在字段名单元格上。
        /// </summary>
        private object BuildDbrDescCellRender(object value, object record, int index)
        {
            if (record is not DbrGridRow row) return value;
            if (string.IsNullOrEmpty(row.FieldDesc)) return value;
            var text = new AntdUI.CellText(row.FieldDesc);
            if (row.NotInTemplate)
                text.Fore = ThemeManager.Deleted;
            else if (row.CellForeColor != Color.Empty)
                text.Fore = row.CellForeColor;
            return text;
        }

        private object BuildDbrMultiValueCellRender(object value, object record, int index, string path)
        {
            if (record is not DbrGridRow row) return value;

            if (row.IsFileOpsRow)
            {
                string basePath = _multiFileRelPaths.Count > 0 ? _multiFileRelPaths[0] : _currentFileRelPath;
                bool isBase = string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase);
                string tip = path;
                if (isBase)
                {
                    tip = LanguageManager.Instance.GetText("Editor.BaseColumnTooltip", "对比基准") + "\r\n" + path;
                }
                var closeBtn = new AntdUI.CellButton("close_" + path,
                    "✕ " + LanguageManager.Instance.GetText("Editor.CloseFile", "关闭"), AntdUI.TTypeMini.Error)
                {
                    Tooltip = tip
                };
                if (isBase)
                    return new AntdUI.ICell[] { closeBtn };
                var setBaseBtn = new AntdUI.CellButton("setbase_" + path,
                    LanguageManager.Instance.GetText("Editor.SetBaseColumn", "设为基准"), AntdUI.TTypeMini.Primary)
                {
                    Tooltip = LanguageManager.Instance.GetText("Editor.SetBaseColumnTooltip", "设为对比基准列")
                };
                return new AntdUI.ICell[] { setBaseBtn, closeBtn };
            }
            if (row.Tag is not TemplateVariableInfo v)
                return new AntdUI.CellText("");

            string val = GetFileFieldValue(path, v.Name);
            var cells = new List<AntdUI.ICell>();

            if (val == null)
            {
                cells.Add(new AntdUI.CellText("—") { Fore = ThemeManager.Deleted });
                return cells.ToArray();
            }

            if (!row.RowReadOnly)
            {
                if (v.IsArray)
                    cells.Add(new AntdUI.CellButton(Guid.NewGuid().ToString(),
                        LanguageManager.Instance.GetText("Editor.ArrayEdit", "数组"), AntdUI.TTypeMini.Default));
                else if (v.Type == "file_dbr")
                    cells.AddRange(DbrFieldHelper.GetDbrBrowseLinks());
                else if (DbrFieldHelper.IsPicklistField(v))
                    cells.Add(new AntdUI.CellButton(Guid.NewGuid().ToString(),
                        LanguageManager.Instance.GetText("Editor.DropdownSelect", "下拉▼"), AntdUI.TTypeMini.Default));

                if (cells.Count > 0)
                    cells.Add(new AntdUI.CellText("\u2002"));
            }

            var text = new AntdUI.CellText(val);

            // 已修改 → 红字
            if (_modifiedDbrFields.TryGetValue(path, out var modSet) && modSet.Contains(v.Name))
                text.Fore = ThemeManager.Modified;

            // 非基准列且与基准不同 → 浅红背景
            bool isBaseCol = _multiFileRelPaths.Count > 0
                && string.Equals(path, _multiFileRelPaths[0], StringComparison.OrdinalIgnoreCase);
            if (!isBaseCol && _multiFileRelPaths.Count > 0)
            {
                string baseVal = GetFileFieldValue(_multiFileRelPaths[0], v.Name);
                if (baseVal != null && !string.Equals(baseVal, val, StringComparison.Ordinal))
                    text.Back = ThemeManager.DiffBack;
            }

            cells.Add(text);
            return cells.ToArray();
        }

        /// <summary>
        /// 构造字段名单元格：显示本地化名（无映射显示英文原值），悬停 Tooltip 显示英文原值便于对照。
        /// </summary>
        private static AntdUI.CellLink CreateFieldNameCell(string name)
        {
            string display = DbrKeyMapping.GetLocalizedName(name);
            return new AntdUI.CellLink(Guid.NewGuid().ToString(), display)
            {
                Tooltip = name,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private void ApplyEditorFontSize()
        {
            _dbrSourceEditor.Styles[ScintillaNET.Style.Default].Size = (int)(_editorFontSize * DpiManager.Scale);
            _dbrSourceEditor.Zoom = 0;
            var dpiScale = DpiManager.Scale;
            _dbrSourceEditor.Margins[0].Width = (int)(_editorFontSize * 5 * dpiScale);
        }

        private void ShowFindBar()
        {
            _findTextBox.Focus();
            _findTextBox.SelectAll();
        }

        private void HideFindBar()
        {
            _findTextBox.Text = "";
            _findMatchCountLabel.Text = "";
            _dbrSourceEditor.IndicatorCurrent = 0;
            _dbrSourceEditor.IndicatorClearRange(0, _dbrSourceEditor.TextLength);
            _dbrSourceEditor.Focus();
        }

        private void FindNext()
        {
            if (string.IsNullOrEmpty(_findTextBox.Text) || _dbrSourceEditor == null) return;
            var flags = ScintillaNET.SearchFlags.None; // or MatchCase if checkbox checked
            int pos = _dbrSourceEditor.CurrentPosition;
            int found = _dbrSourceEditor.FindText(flags, _findTextBox.Text, pos, _dbrSourceEditor.TextLength);
            if (found < 0)
            {
                // wrap
                found = _dbrSourceEditor.FindText(flags, _findTextBox.Text, 0, pos);
            }
            if (found >= 0)
            {
                _dbrSourceEditor.SetSel(found, found + _findTextBox.Text.Length);
                _dbrSourceEditor.ScrollCaret();
                _findMatchCountLabel.Text = "";
            }
        }

        private void FindPrevious()
        {
            if (string.IsNullOrEmpty(_findTextBox.Text) || _dbrSourceEditor == null) return;
            var flags = ScintillaNET.SearchFlags.None;
            int pos = _dbrSourceEditor.CurrentPosition;
            int found = _dbrSourceEditor.FindText(flags, _findTextBox.Text, 0, pos);
            if (found < 0)
            {
                int end = _dbrSourceEditor.TextLength;
                found = _dbrSourceEditor.FindText(flags, _findTextBox.Text, end, 0);
            }
            if (found >= 0)
            {
                _dbrSourceEditor.SetSel(found, found + _findTextBox.Text.Length);
                _dbrSourceEditor.ScrollCaret();
                _findMatchCountLabel.Text = "";
            }
        }

        // ======================== Source Editor (unified for all editable files) ========================

        private void LoadSourceForEditing(NodeData data, string relPath)
        {
            string text;

            if (!data.RecordIndex.HasValue)
            {
                string tempFile = _session.GetTempFilePath(relPath);
                text = File.Exists(tempFile)
                    ? EncodingService.ReadTextFile(tempFile)
                    : "";
            }
            else if (_isArz)
            {
                var record = _arzReader.GetRecord(data.RecordIndex.Value);
                using (var ms = new MemoryStream())
                {
                    record.SaveToStream(ms);
                    ms.Position = 0;
                    using (var sr = new StreamReader(ms, Utf8NoBom))
                        text = sr.ReadToEnd();
                }
            }
            else
            {
                var entry = _arcFile.toc[data.RecordIndex.Value];
                using (var ms = new MemoryStream())
                {
                    _arcFile.UnpackToStream(entry, ms);
                    text = TemplateHelper.DecodeText(ms.ToArray());
                }
            }

            ShowSourceEditor(text);

            string existingTemp = _session.GetTempFilePath(relPath);
            if (File.Exists(existingTemp))
            {
                _isLoadingFile = true;
                _dbrSourceEditor.Text = EncodingService.ReadTextFile(existingTemp);
                PumpEditorMessages();
                _isLoadingFile = false;
                CaptureSourceBaseline();   // 以最终载入（含临时内容）的文本为基线
                // 临时文件内容覆盖了 ShowSourceEditor 设置的文本，需重新应用着色（dbr 手动逗号染色）
                ApplySourceSyntaxCurrent();
            }
        }

        /// <summary>应用 Scintilla 语法着色（行号 + 按扩展名的 lexer/dbr 手动染色，明暗两套）。</summary>
        /// <remarks>仅在主题切换时调用；颜色统一由 ScintillaSyntax 逻辑提供。</remarks>
        private void ApplySourceSyntaxTheme()
        {
            if (_dbrSourceEditor == null || _dbrSourceEditor.IsDisposed) return;
            ScintillaSyntax.ApplyTheme(_dbrSourceEditor, FontHelper.MonoFontName,
                (int)(_editorFontSize * DpiManager.Scale));
            // 最后一步重设行号颜色（同 ApplySourceSyntaxCurrent 的说明）
            ScintillaSyntax.SetLineNumber(_dbrSourceEditor);
        }

        /// <summary>
        /// 载入源码文本后泵一次消息。Scintilla 可能把 TextChanged/样式消息排队，
        /// 必须在 _isLoadingFile 仍为 true 时处理完；改成 Update() 只会重绘、不处理排队事件。
        /// </summary>
        private void PumpEditorMessages()
        {
            Application.DoEvents();
        }

        private void ShowSourceEditor(string content)
        {
            HideAllEditors();
            _currentEditorMode = EditorMode.SourceEditor;

            _editorTabControl.SelectedIndex = Math.Min(2, _editorTabControl.Pages.Count - 1);
            _editorTabControl.Visible = true;

            _dbrSourceEditor.Visible = true;
            if (_sourceNoFilePlaceholder != null)
                _sourceNoFilePlaceholder.Visible = false;

            _isLoadingFile = true;
            _dbrSourceEditor.Text = content;
            PumpEditorMessages();
            _isLoadingFile = false;
            CaptureSourceBaseline();   // 打开文件进入源码模式：以载入文本为基线（随后若被临时内容覆盖会再次更新）
            _dbrSourceEditor.ReadOnly = false;

            // 语法着色（统一入口，按扩展名：.lua→lua、.txt→props、.tpl→json、.dbr→手动逗号染色、其余→默认）
            ApplySourceSyntaxCurrent();

            // 隐藏查找栏
            HideFindBar();

            BeginInvoke((Action)(() =>
            {
                _dbrSourceEditor.GotoPosition(0);
            }));
        }

        /// <summary>
        /// 源码编辑器统一着色入口：按当前文件扩展名调用 ScintillaSyntax.Apply。
        /// 覆盖 dbr 无模板回退源码、以及从 DBR 网格手动切到源码模式、临时文件覆盖后等所有填充路径。
        /// </summary>
        private void ApplySourceSyntaxCurrent()
        {
            if (_dbrSourceEditor == null || _dbrSourceEditor.IsDisposed) return;
            if (string.IsNullOrEmpty(_currentFileRelPath)) return;
            ScintillaSyntax.Apply(_dbrSourceEditor, _currentFileRelPath, FontHelper.MonoFontName,
                (int)(_editorFontSize * DpiManager.Scale));
            // 最后一步重设行号颜色：Apply 内部虽已设置，但主题/控件初始化可能在其后把 STYLE_LINENUMBER 重置为默认浅灰
            ScintillaSyntax.SetLineNumber(_dbrSourceEditor);
        }

        // ======================== Read-Only View ========================

        private void LoadReadOnlyView(NodeData data, string relPath)
        {
            string text;
            if (_isArz)
            {
                var record = _arzReader.GetRecord(data.RecordIndex.Value);
                using (var ms = new MemoryStream())
                {
                    record.SaveToStream(ms);
                    ms.Position = 0;
                    using (var sr = new StreamReader(ms, Utf8NoBom))
                        text = sr.ReadToEnd();
                }
            }
            else
            {
                var entry = _arcFile.toc[data.RecordIndex.Value];
                using (var ms = new MemoryStream())
                {
                    _arcFile.UnpackToStream(entry, ms);
                    text = TemplateHelper.DecodeText(ms.ToArray());
                }
            }

            HideAllEditors();
            _currentEditorMode = EditorMode.ReadOnly;
            _readOnlyView.Text = text;
            _readOnlyView.Visible = true;
            BeginInvoke((Action)(() =>
            {
                _readOnlyView.Select(0, 0);
                _readOnlyView.ScrollToCaret();
            }));
        }

        // ======================== Editor Switching Helpers ========================

        private void HideAllEditors()
        {
            _placeholderLabel.Visible = false;
            _editorTabControl.Visible = false;
            _dbrSplit.Visible = false;
            _readOnlyView.Visible = false;
        }

        private void ShowPlaceholder()
        {
            HideAllEditors();
            _placeholderLabel.Visible = true;
            _currentFilePathLabel.Visible = false;
            _favoriteButton.Visible = false;
            _currentFileRelPath = null;
            _currentEditorMode = EditorMode.None;
        }

        // ======================== File List ========================

        private void EnterSearchFileListView()
        {
            _fileListLoadCts?.Cancel();
            _fileListLoadCts?.Dispose();
            _fileListLoadCts = new System.Threading.CancellationTokenSource();
            _fileListSearchView = true;
            _searchViewHits = new List<(string path, string fileName, int matchCount, int recordIndex)>();
            _searchViewRows = new AntdUI.AntList<FileListGridRow>();
            _fileListGrid.DataSource = null;
            ResetFileListSelectionState();
            _fileListGrid.Binding(_searchViewRows);
            ShowFileList();
        }

        private void ExitSearchFileListView()
        {
            if (!_fileListSearchView) return;
            _fileListSearchView = false;
            _searchViewHits = null;
            _searchViewRows = null;
        }

        private FileListGridRow BuildSearchViewRow(string path, string filterText, System.Threading.CancellationToken ct)
        {
            var nd = FindNodeDataByPath(path);
            if (nd == null || nd.IsDirectory) return null;
            return BuildFileListRow(nd, path, filterText, ct, forceFullPathAsName: true);
        }

        private void BuildAndReportSearchRow(string path, string filterText, System.Threading.CancellationToken token,
            IProgress<FileListGridRow> rowProgress)
        {
            if (token.IsCancellationRequested) return;
            var nd = FindNodeDataByPath(path);
            if (nd == null || nd.IsDirectory) return;
            FileListGridRow row;
            try
            {
                row = BuildFileListRow(nd, path, filterText, token, forceFullPathAsName: true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (row != null) rowProgress.Report(row);
        }

        private async Task ReloadSearchFileListView()
        {
            _fileListLoadCts?.Cancel();
            _fileListLoadCts?.Dispose();
            _fileListLoadCts = new System.Threading.CancellationTokenSource();
            var ct = _fileListLoadCts.Token;
            string filterText = _fileListFilterTextBox?.Text?.Trim() ?? "";
            List<(string path, string fileName, int matchCount, int recordIndex)> hits;
            lock (_searchViewHitsLock)
            {
                hits = _searchViewHits == null
                    ? new List<(string path, string fileName, int matchCount, int recordIndex)>()
                    : _searchViewHits.ToList();
            }
            var list = _searchViewRows;
            if (list == null) return;

            // 已删除文件同样不在"文件列表"的搜索视图中出现（撤销删除后自动恢复显示）
            if (_deletedFiles.Count > 0)
                hits = hits.Where(h => !_deletedFiles.Contains(h.path)).ToList();
            try
            {
                var rows = await Task.Run(() =>
                {
                    var rs = new List<FileListGridRow>();
                    foreach (var h in hits.OrderBy(h => h.path, StringComparer.OrdinalIgnoreCase))
                    {
                        ct.ThrowIfCancellationRequested();
                        var row = BuildSearchViewRow(h.path, filterText, ct);
                        if (row != null) rs.Add(row);
                    }
                    return rs;
                }, ct);
                if (IsDisposed || _fileListGrid == null || _fileListGrid.IsDisposed) return;
                // 列头排序（有排序状态时覆盖默认的按路径排序）
                if (_fileListSortKey != null)
                    rows.Sort((a, b) => CompareFileListRows(a, b, _fileListSortKey, _fileListSortAsc));
                _searchViewRows.Clear();
                _searchViewRows.AddRange(rows);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.FileListLoadError", "加载文件列表失败: {0}"), ex.Message), "ERROR");
            }
        }

        private void ShowFileList()
        {
            _placeholderLabel.Visible = false;
            _readOnlyView.Visible = false;
            _editorTabControl.Visible = true;
            _editorTabControl.SelectedIndex = 0;
            _currentEditorMode = EditorMode.FileList;
            _currentFilePathLabel.Visible = !string.IsNullOrEmpty(_currentFileRelPath);
            _favoriteButton.Visible = _currentFilePathLabel.Visible;
            UpdateFavoriteButtonState();
        }

        private void SwitchToEditor()
        {
            _placeholderLabel.Visible = false;
            _readOnlyView.Visible = false;
            _editorTabControl.Visible = true;
            if (_currentEditorMode == EditorMode.DbrGrid)
            {
                _editorTabControl.SelectedIndex = 1;
            }
            else if (_currentEditorMode == EditorMode.SourceEditor)
            {
                _editorTabControl.SelectedIndex = 2;
            }
            else if (_currentEditorMode == EditorMode.ReadOnly)
            {
                _readOnlyView.Visible = true;
            }
            else
            {
                _placeholderLabel.Visible = true;
            }
            _currentFilePathLabel.Visible = !string.IsNullOrEmpty(_currentFileRelPath);
            _favoriteButton.Visible = _currentFilePathLabel.Visible;
            UpdateFavoriteButtonState();
        }

        private void RefreshFileList(bool silent = false)
        {
            if (_fileListSearchView)
            {
                _ = ReloadSearchFileListView();
                return;
            }
            _ = LoadFileListAsync(full: false, silent: silent);
        }

        private void RefreshFileListAll(bool silent = false)
        {
            if (_fileListSearchView)
            {
                _ = ReloadSearchFileListView();
                return;
            }
            _currentFolderPath = null;
            ShowFileList();
            _ = LoadFileListAsync(full: true, silent: silent);
        }

        /// <summary>silent = true 时不弹 Spin 加载层（如设置保存后的关联刷新场景，
        /// 避免在 RefreshOpenWindows 枚举窗口期间创建顶层窗体导致 Application.OpenForms 被修改）</summary>
        private async Task LoadFileListAsync(bool full, bool silent = false)
        {
            _fileListLoadCts?.Cancel();
            _fileListLoadCts?.Dispose();
            _fileListLoadCts = new System.Threading.CancellationTokenSource();
            var ct = _fileListLoadCts.Token;
            string filterText = _fileListFilterTextBox?.Text?.Trim() ?? "";
            bool useSpin = !silent
                && _fileListGrid != null && !_fileListGrid.IsDisposed
                && _fileListGrid.Visible && _fileListGrid.IsHandleCreated && !IsDisposed;

            AntdUI.Spin.Config spinCfg = null;
            var progress = new Progress<(int current, int total)>(p =>
            {
                if (spinCfg == null || p.total <= 0) return;
                spinCfg.Value = Math.Max(0, Math.Min(1, (float)p.current / p.total));
                spinCfg.Text = string.Format(
                    LanguageManager.Instance.GetText("Editor.FileListLoadingCount", "正在加载文件列表... ({0}/{1})"),
                    p.current, p.total);
            });
            var uiContext = SynchronizationContext.Current;

            Func<Task> runLoad = async () =>
            {
                bool entered = false;
                try
                {
                    await _fileListLoadGate.WaitAsync(ct);
                    entered = true;
                    ct.ThrowIfCancellationRequested();
                    var rows = await Task.Run(() =>
                    {
                        using var scanFs = OpenArchiveScanStream();
                        return full
                            ? CollectAllFileRows(ct, progress, scanFs)
                            : CollectFolderFileRows(ct, progress, scanFs);
                    }, ct);
                    ct.ThrowIfCancellationRequested();
                    if (IsDisposed || _fileListGrid == null || _fileListGrid.IsDisposed) return;
                    _fileListAllRows = rows;
                    if (uiContext != null)
                    {
                        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        uiContext.Post(_ =>
                        {
                            try
                            {
                                RebindFileListView(filterText, progressive: true);
                                tcs.SetResult(true);
                            }
                            catch (Exception e)
                            {
                                tcs.TrySetException(e);
                            }
                        }, null);
                        await tcs.Task;
                        ct.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        RebindFileListView(filterText);
                    }
                }
                finally
                {
                    if (entered) _fileListLoadGate.Release();
                }
            };

            try
            {
                if (useSpin)
                {
                    spinCfg = new AntdUI.Spin.Config
                    {
                        Text = LanguageManager.Instance.GetText("Common.Loading", "加载中..."),
                        CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                        CancelTokenSource = _fileListLoadCts
                    };
                    await AntdUI.Spin.open((Control)_fileListGrid, spinCfg, async _ => await runLoad());
                }
                else
                {
                    await runLoad();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogOutput(string.Format(LanguageManager.Instance.GetText("Editor.FileListLoadError", "加载文件列表失败: {0}"), ex.Message), "ERROR");
            }
        }

        private static bool MatchesFilter(FileListGridRow row, string filterText)
        {
            if (string.IsNullOrEmpty(filterText)) return true;
            if (row == null) return false;
            return (row.FileName ?? "").IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0
                || (row.FileDescription ?? "").IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0
                || (row.Notes ?? "").IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0
                || (row.Template ?? "").IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ApplyFileListFilter()
        {
            if (_fileListSearchView)
            {
                _ = ReloadSearchFileListView();
                return;
            }
            string filterText = _fileListFilterTextBox?.Text?.Trim() ?? "";
            RebindFileListView(filterText);
        }

        /// <summary>
        /// 文件列表列头排序点击（外部接管：返回 true 让 AntdUI 跳过内置"仅重排显示行"的排序——
        /// 内置排序不动 DataSource，与过滤重绑、按行号选中的体系冲突）。库只负责画排序图标。
        /// </summary>
        private bool OnFileListSortModeChanged(object sender, AntdUI.TableSortModeEventArgs e)
        {
            if (e.Column is AntdUI.ColumnCheck) return false;   // 勾选列不参与排序
            if (e.SortMode == AntdUI.SortMode.NONE)
            {
                _fileListSortKey = null;                        // 回到默认顺序（包内自然顺序）
            }
            else
            {
                _fileListSortKey = e.Column.Key;
                _fileListSortAsc = e.SortMode == AntdUI.SortMode.ASC;
            }

            if (_fileListSearchView)
            {
                // 搜索视图：行对象已构建在 _searchViewRows，就地重排（不重走动态构建管线）
                SortAntListInPlace(_searchViewRows);
            }
            else
            {
                // 普通视图：重走过滤重绑管线（当前过滤文本/模板过滤叠加生效）
                RebindFileListView(_fileListFilterTextBox?.Text?.Trim() ?? "");
            }
            return true;
        }

        /// <summary>按当前排序状态就地重排 AntList（清空再按序装回；行对象引用不变，着色/勾选等状态保留）</summary>
        private void SortAntListInPlace(AntdUI.AntList<FileListGridRow> list)
        {
            if (list == null || _fileListSortKey == null) return;
            var rows = new List<FileListGridRow>(list);
            rows.Sort((a, b) => CompareFileListRows(a, b, _fileListSortKey, _fileListSortAsc));
            list.Clear();
            list.AddRange(rows);
        }

        /// <summary>
        /// 文件列表行排序比较器（普通视图与搜索视图共用）：
        /// 全字段按字符串比较（修改时间为固定的 "yyyy-MM-dd HH:mm:ss" 格式，字典序 = 时间序）；
        /// 空值统一沉底（不随升降序翻转，避免空说明/无时间的行排到最前干扰浏览）。
        /// </summary>
        private static int CompareFileListRows(FileListGridRow a, FileListGridRow b, string sortKey, bool asc)
        {
            string va, vb;
            switch (sortKey)
            {
                case "FileDescription": va = a?.FileDescription; vb = b?.FileDescription; break;
                case "ModifyTime": va = a?.ModifyTime; vb = b?.ModifyTime; break;
                case "Notes": va = a?.Notes; vb = b?.Notes; break;
                case "Template": va = a?.Template; vb = b?.Template; break;
                default: va = a?.FileName; vb = b?.FileName; break;   // FileName 及未知 Key 兜底
            }
            int cmp;
            if (string.IsNullOrEmpty(va) && string.IsNullOrEmpty(vb)) cmp = 0;
            else if (string.IsNullOrEmpty(va)) return 1;    // 空值沉底（与方向无关）
            else if (string.IsNullOrEmpty(vb)) return -1;
            else cmp = string.Compare(va, vb, StringComparison.OrdinalIgnoreCase);
            return asc ? cmp : -cmp;
        }

        private void RebindFileListView(string filterText, bool progressive = false)
        {
            if (IsDisposed || _fileListGrid == null || _fileListGrid.IsDisposed || _fileListAllRows == null) return;
            if (_fileListViewRows == null || !ReferenceEquals(_fileListGrid.DataSource, _fileListViewRows))
            {
                _fileListViewRows = new AntdUI.AntList<FileListGridRow>();
                _fileListGrid.DataSource = null;
                _fileListGrid.Binding(_fileListViewRows);
            }
            System.Collections.Generic.IList<FileListGridRow> viewData = _fileListAllRows;
            // ① 已删除文件不在文件列表显示（右侧「已修改列表」仍保留删除项）：
            // 避免"要删掉的东西还占着列表"的视觉污染；撤销删除后 _deletedFiles 清空 → 自动恢复显示。
            // 注意：判断用 _deletedFiles 而不是 nd.State=="d" —— 撤销后若 State 未及时复位会残留灰行（见 OnUndoAllClick）。
            if (_deletedFiles.Count > 0)
            {
                var visible = new List<FileListGridRow>(_fileListAllRows.Count);
                foreach (var r in _fileListAllRows)
                {
                    if (r != null && !_deletedFiles.Contains(r.RelPath)) visible.Add(r);
                }
                viewData = visible;
            }
            // ② 搜索框过滤：在 ① 的结果上做，否则会把已隐藏的删除项又过滤回来
            if (!string.IsNullOrEmpty(filterText))
            {
                var matched = new List<FileListGridRow>(viewData.Count);
                foreach (var r in viewData)
                    if (MatchesFilter(r, filterText)) matched.Add(r);
                viewData = matched;
            }
            // ③ 模板下拉过滤：与搜索框相互独立、叠加生效（AND）
            string tplFilter = GetTemplateFilterSelection();
            if (!string.IsNullOrEmpty(tplFilter))
            {
                var matched = new List<FileListGridRow>(viewData.Count);
                foreach (var r in viewData)
                    if (string.Equals(r.Template, tplFilter, StringComparison.OrdinalIgnoreCase)) matched.Add(r);
                viewData = matched;
            }
            UpdateTemplateFilterOptions();
            // ④ 列头排序（有排序状态时按列排序；复制一份再排，不改 _fileListAllRows 的包内自然顺序）
            if (_fileListSortKey != null && viewData.Count > 1)
            {
                var sorted = new List<FileListGridRow>(viewData);
                sorted.Sort((a, b) => CompareFileListRows(a, b, _fileListSortKey, _fileListSortAsc));
                viewData = sorted;
            }
            _fileListViewRows.Clear();
            if (!progressive || viewData.Count <= 500)
            {
                _fileListViewRows.AddRange(viewData);
            }
            else
            {
                const int step = 500;
                for (int i = 0; i < viewData.Count; i += step)
                {
                    int count = Math.Min(step, viewData.Count - i);
                    var batch = new FileListGridRow[count];
                    for (int k = 0; k < count; k++) batch[k] = viewData[i + k];
                    _fileListViewRows.AddRange(batch);
                }
            }
            RebuildSelectionMirror();
        }

        /// <summary>当前模板下拉选中的模板名（"全部模板" = 空串 → 不过滤）</summary>
        private string GetTemplateFilterSelection()
        {
            return _templateFilterSelect?.SelectedValue as string;
        }

        /// <summary>
        /// 依据 <c>_fileListAllRows</c> 动态生成模板下拉选项：
        /// 树模式 = 当前目录下文件用到的模板；平铺模式 = 全部已用模板。
        /// 仅在行数据源更换时重建（避免每次输入筛选都重建）；重建时保留仍存在的当前选择。
        /// </summary>
        private void UpdateTemplateFilterOptions()
        {
            if (_templateFilterSelect == null) return;
            if (ReferenceEquals(_fileListAllRows, _templateOptionSourceRows)) return;
            _templateOptionSourceRows = _fileListAllRows;

            string current = GetTemplateFilterSelection();
            var tplSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_fileListAllRows != null)
            {
                foreach (var r in _fileListAllRows)
                {
                    if (!string.IsNullOrEmpty(r.Template)) tplSet.Add(r.Template);
                }
            }
            _templateSelectSyncing = true;
            try
            {
                _templateFilterSelect.Items.Clear();
                _templateFilterSelect.Items.Add(new AntdUI.SelectItem(
                    LanguageManager.Instance.GetText("Editor.TemplateFilterAll", "全部模板"), ""));
                foreach (var tpl in tplSet)
                    _templateFilterSelect.Items.Add(new AntdUI.SelectItem(tpl, tpl));
                // 保留仍存在的选择；选择失效（如切换目录）时回到"全部模板"
                _templateFilterSelect.SelectedValue = (current != null && tplSet.Contains(current)) ? current : "";
            }
            finally
            {
                _templateSelectSyncing = false;
            }
        }

        private FileStream OpenArchiveScanStream()
        {
            if (!string.IsNullOrEmpty(_archiveFilePath) && File.Exists(_archiveFilePath))
                return new FileStream(_archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return null;
        }

        private void RefreshFileListRow(string relPath)
        {
            if (RefreshFileListRowCore(relPath))
                RebindFileListView(_fileListFilterTextBox?.Text?.Trim() ?? "");
        }

        /// <summary>
        /// 行前景色：按节点状态取色（与文件树节点色规则一致）。
        /// </summary>
        private Color GetFileRowForeColor(string relPath)
        {
            var nd = FindNodeDataByPath(relPath);
            if (nd == null) return ThemeManager.Text;
            return nd.State == "a" ? ThemeManager.Added
                 : nd.State == "d" ? ThemeManager.Deleted
                 : nd.State == "m" ? ThemeManager.Modified
                 : ThemeManager.Text;
        }

        /// <summary>
        /// 改值后的文件列表**单行着色**（不重建行集合）：
        /// 只更新命中行的 <see cref="FileListGridRow.RowForeColor"/>，再让表格重绘——
        /// 表格是虚拟模式，绘制时经 <c>SetRowStyle</c> 按行模型取色，故更新字段 + Invalidate 即可。
        /// <para>原实现走 <see cref="RefreshFileListRow"/> → <c>RebindFileListView</c>：会把当前视图的**全部行**
        /// （平铺可达 6 万）重新塞回数据源，是"改一格就卡"的另一半原因。该行不在当前视图时自然什么都不做。</para>
        /// </summary>
        private void UpdateFileListRowVisual(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return;
            UpdateFileListRowVisualForPaths(new[] { relPath });
        }

        /// <summary>批量版单行着色：命中多行时只重绘一次（供批量编辑/撤销收尾用）</summary>
        private void UpdateFileListRowVisualForPaths(ICollection<string> relPaths)
        {
            if (relPaths == null || relPaths.Count == 0) return;
            var rows = GetFileListGridRows();
            if (rows == null || _fileListGrid == null || _fileListGrid.IsDisposed) return;
            // 命中判定用哈希集（批量场景 26 个路径 × 6 万行时避免线性 Contains）
            var pathSet = relPaths as HashSet<string>
                ?? new HashSet<string>(relPaths, StringComparer.OrdinalIgnoreCase);
            bool hit = false;
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrEmpty(row.RelPath)) continue;
                if (!pathSet.Contains(row.RelPath)) continue;
                row.RowForeColor = GetFileRowForeColor(row.RelPath);
                hit = true;
            }
            if (hit) _fileListGrid.Invalidate();   // 只重绘（虚拟模式按新颜色画可见行），不重建行
        }

        /// <summary>文件列表行的排序键（与列表构建时的排序一致）：平铺按完整路径，树模式按文件名。</summary>
        private string GetFileListRowSortKey(FileListGridRow row)
            => _flatViewMode ? (row.RelPath ?? "") : (row.FileName ?? "");

        /// <summary>该行当前是否应显示在视图里（已删除的不显示 / 搜索框过滤 / 模板下拉过滤）。</summary>
        private bool IsFileListRowVisible(FileListGridRow row)
        {
            if (row == null) return false;
            if (!string.IsNullOrEmpty(row.RelPath) && _deletedFiles.Contains(row.RelPath)) return false;
            string filterText = _fileListFilterTextBox?.Text?.Trim() ?? "";
            if (!MatchesFilter(row, filterText)) return false;
            string tplFilter = GetTemplateFilterSelection();
            if (!string.IsNullOrEmpty(tplFilter)
                && !string.Equals(row.Template, tplFilter, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        /// <summary>
        /// 文件列表**增量插入一行**（导入用）：构造行 → 插入全量集合的排序位置 → 通过当前过滤时插入视图。
        /// 只处理这一行（行文本走显示缓存，基本零成本）；**不重扫全部文件、不弹 Spin**
        /// （原实现走 RefreshAllAfterChange → 平铺模式全量重扫 6 万文件并显示进度圈）。
        /// </summary>
        private void InsertFileListRow(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return;
            if (_fileListAllRows == null || _fileListGrid == null || _fileListGrid.IsDisposed) return;

            // 树模式下列表只显示"当前目录"的文件：导入到其它目录的文件不插入当前视图
            if (!_flatViewMode && !string.Equals(GetParentPath(relPath) ?? "", _currentFolderPath ?? "",
                    StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var r in _fileListAllRows)
            {
                if (r != null && string.Equals(r.RelPath, relPath, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateFileListRowVisual(relPath);   // 已在列表里 → 只更新颜色（幂等）
                    return;
                }
            }

            var nd = FindNodeDataByPath(relPath);
            if (nd == null || nd.IsDirectory) return;
            var row = BuildFileListRow(nd, relPath, "", System.Threading.CancellationToken.None,
                null, forceFullPathAsName: _flatViewMode);
            if (row == null) return;

            string key = GetFileListRowSortKey(row);
            int at = _fileListAllRows.Count;
            for (int i = 0; i < _fileListAllRows.Count; i++)
            {
                if (string.Compare(GetFileListRowSortKey(_fileListAllRows[i]), key, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    at = i;
                    break;
                }
            }
            _fileListAllRows.Insert(at, row);

            if (_fileListViewRows != null && IsFileListRowVisible(row))
            {
                int vAt = _fileListViewRows.Count;
                for (int i = 0; i < _fileListViewRows.Count; i++)
                {
                    if (string.Compare(GetFileListRowSortKey(_fileListViewRows[i]), key, StringComparison.OrdinalIgnoreCase) > 0)
                    {
                        vAt = i;
                        break;
                    }
                }
                _fileListViewRows.Insert(vAt, row);
                RebuildSelectionMirror();
            }

            // 新导入文件可能带来新的模板 → 强制重建模板下拉选项（成本与模板数成正比，很小）
            _templateOptionSourceRows = null;
            UpdateTemplateFilterOptions();
        }

        /// <summary>文件列表**增量移除一行**（删除 / 撤销导入用）：从全量集合与视图同时移除。</summary>
        private void RemoveFileListRow(string relPath)
        {
            if (string.IsNullOrEmpty(relPath) || _fileListGrid == null || _fileListGrid.IsDisposed) return;
            if (_fileListAllRows != null)
            {
                for (int i = _fileListAllRows.Count - 1; i >= 0; i--)
                {
                    if (_fileListAllRows[i] != null &&
                        string.Equals(_fileListAllRows[i].RelPath, relPath, StringComparison.OrdinalIgnoreCase))
                        _fileListAllRows.RemoveAt(i);
                }
            }
            if (_fileListViewRows != null)
            {
                for (int i = _fileListViewRows.Count - 1; i >= 0; i--)
                {
                    if (_fileListViewRows[i] != null &&
                        string.Equals(_fileListViewRows[i].RelPath, relPath, StringComparison.OrdinalIgnoreCase))
                        _fileListViewRows.RemoveAt(i);
                }
                RebuildSelectionMirror();
            }
        }

        /// <summary>
        /// 修补单个文件在 <c>_fileListAllRows</c> 中对应行的显示字段（**不触发重绑**，便于批量修补后统一重绑）。
        /// 返回是否找到该行（未找到说明该行不在当前缓存中，调用方可退回整表刷新）。
        /// </summary>
        private bool RefreshFileListRowCore(string relPath)
        {
            if (string.IsNullOrEmpty(relPath) || _fileListAllRows == null) return false;
            string tempFile = _session.GetTempFilePath(relPath);
            bool hasTemp = File.Exists(tempFile);
            int index = -1;
            for (int i = 0; i < _fileListAllRows.Count; i++)
            {
                if (string.Equals(_fileListAllRows[i].RelPath, relPath, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) return false;
            var row = _fileListAllRows[index];
            var nd = row.Tag;
            using var scanFs = (!hasTemp && !string.IsNullOrEmpty(_archiveFilePath) && File.Exists(_archiveFilePath))
                ? new FileStream(_archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
            GetFileDisplayTexts(relPath, nd, hasTemp ? tempFile : null, scanFs,
                out string description, out string annotation, out string annotationTag, out string templateName);
            row.FileDescription = description;
            row.Notes = annotation;
            row.AnnotationTag = annotationTag;
            row.Template = templateName ?? "";
            if (hasTemp)
            {
                try { row.ModifyTime = File.GetLastWriteTime(tempFile).ToString("yyyy-MM-dd HH:mm:ss"); }
                catch { row.ModifyTime = nd?.FileTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""; }
            }
            else
                row.ModifyTime = nd?.FileTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            // 节点状态可能刚被复位（撤销/删除）→ 同步刷新行颜色，避免残留灰行
            row.RowForeColor = nd == null ? row.RowForeColor
                : nd.State == "a" ? ThemeManager.Added
                : nd.State == "d" ? ThemeManager.Deleted
                : nd.State == "m" ? ThemeManager.Modified
                : ThemeManager.Text;
            return true;
        }

        private List<FileListGridRow> CollectFolderFileRows(System.Threading.CancellationToken ct,
            IProgress<(int current, int total)> progress, Stream archiveStream)
        {
            var rows = new List<FileListGridRow>();
            if (_fileTreeRootNodes == null) return rows;

            List<NodeData> filesToShow = null;
            if (string.IsNullOrEmpty(_currentFolderPath))
            {
                filesToShow = _fileTreeRootNodes
                    .Where(nd => !nd.IsDirectory)
                    .OrderBy(nd => nd.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                string[] parts = _currentFolderPath.Split('/');
                var folder = FindNodeDataByPath(_fileTreeRootNodes, parts, 0);
                if (folder == null)
                    folder = FindNodeDataByPath(_currentFolderPath);
                if (folder == null || !folder.IsDirectory)
                    return rows;
                filesToShow = folder.Children
                    .Where(c => !c.IsDirectory)
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            int current = 0, total = filesToShow.Count, reportEvery = 300;
            foreach (var nd in filesToShow)
            {
                ct.ThrowIfCancellationRequested();
                string fullPath = GetFileListFullPath(nd);
                var row = BuildFileListRow(nd, fullPath, "", ct, archiveStream);
                if (row != null) rows.Add(row);
                current++;
                if (progress != null && (current % reportEvery == 0 || current == total))
                    progress.Report((current, total));
            }
            return rows;
        }

        private List<FileListGridRow> CollectAllFileRows(System.Threading.CancellationToken ct,
            IProgress<(int current, int total)> progress, Stream archiveStream)
        {
            var rows = new List<FileListGridRow>();
            if (_fileTreeRootNodes == null) return rows;

            int CountNodes(List<NodeData> nodes)
            {
                int n = 0;
                foreach (var nd in nodes)
                {
                    if (nd == null) continue;
                    if (nd.IsDirectory) n += CountNodes(nd.Children);
                    else n++;
                }
                return n;
            }
            int total = CountNodes(_fileTreeRootNodes);
            int current = 0, reportEvery = 300;

            void Walk(List<NodeData> nodes, string prefix)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var nd in nodes)
                {
                    if (nd == null) continue;
                    string rel = string.IsNullOrEmpty(prefix) ? nd.Name : prefix + "/" + nd.Name;
                    if (nd.IsDirectory)
                        Walk(nd.Children, rel);
                    else
                    {
                        var row = BuildFileListRow(nd, rel, "", ct, archiveStream);
                        if (row != null) rows.Add(row);
                        current++;
                        if (progress != null && (current % reportEvery == 0 || current == total))
                            progress.Report((current, total));
                    }
                }
            }
            Walk(_fileTreeRootNodes, "");
            rows.Sort((a, b) => string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase));
            return rows;
        }

        private FileListGridRow BuildFileListRow(NodeData nd, string fullPath, string filterText,
            System.Threading.CancellationToken ct, Stream archiveStream = null, bool forceFullPathAsName = false)
        {
            ct.ThrowIfCancellationRequested();
            string tempFile = _session.GetTempFilePath(fullPath);
            bool hasTemp = File.Exists(tempFile);
            GetFileDisplayTexts(fullPath, nd, hasTemp ? tempFile : null, archiveStream,
                out string description, out string annotation, out string annotationTag, out string templateName);

            bool useFullPath = forceFullPathAsName || _flatViewMode;
            string nameForFilter = useFullPath ? fullPath : nd.Name;
            if (!string.IsNullOrEmpty(filterText) &&
                nameForFilter.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) < 0 &&
                description.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) < 0 &&
                annotation.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            string modifyTime = "";
            if (hasTemp)
            {
                try { modifyTime = File.GetLastWriteTime(tempFile).ToString("yyyy-MM-dd HH:mm:ss"); }
                catch { modifyTime = nd.FileTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""; }
            }
            else
                modifyTime = nd.FileTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";

            var row = new FileListGridRow
            {
                RelPath = fullPath,
                FileName = useFullPath ? fullPath : nd.Name,
                FileDescription = description,
                ModifyTime = modifyTime,
                Notes = annotation,
                AnnotationTag = annotationTag,
                Template = templateName ?? "",
                Tag = nd
            };

            if (nd.State == "a")
                row.RowForeColor = ThemeManager.Added;
            else if (nd.State == "d")
                row.RowForeColor = ThemeManager.Deleted;
            else if (nd.State == "m")
                row.RowForeColor = ThemeManager.Modified;

            return row;
        }

        private void SwitchFileTreeViewMode(bool flat, bool save = true)
        {
            ExitSearchFileListView();
            _flatViewMode = flat;
            if (save)
            {
                SettingsManager.EditorFlatViewMode = flat;
                SettingsManager.Save();
            }
            if (_segFileTreeMode != null)
            {
                _syncingViewSeg = true;
                try { _segFileTreeMode.SelectIndex = flat ? 1 : 0; }
                finally { _syncingViewSeg = false; }
            }
            if (flat)
            {
                _flatTree.Visible = true;
                _folderTreeScrollPanel.Visible = false;
                BuildFlatTree();
            }
            else
            {
                _flatTree.Visible = false;
                _folderTreeScrollPanel.Visible = true;
            }
        }

        private void BuildFlatTree()
        {
            if (_flatTree == null) return;
            _flatTree.Items.Clear();
            if (string.IsNullOrEmpty(_archiveFilePath) && _fileTreeRootNodes == null) return;
            string label = Path.GetFileName(_archiveFilePath);
            if (string.IsNullOrEmpty(label))
                label = LanguageManager.Instance.GetText("Editor.OpenPackFlat", "打开包");
            var item = new AntdUI.TreeItem { Text = label, Tag = null };
            _flatTree.Items.Add(item);
            _flatTree.SelectItem = item;
        }

        private void OnFlatTreeNodeClick(object sender, AntdUI.TreeSelectEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Item == null || e.Item.Tag != null) return;
            _flatTree.SelectItem = e.Item;
            ExitSearchFileListView();
            RefreshFileListAll();
        }

        private void OnFlatTreeNodeDoubleClick(object sender, AntdUI.TreeSelectEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Item == null || e.Item.Tag != null) return;
            _flatTree.SelectItem = e.Item;
            ExitSearchFileListView();
            RefreshFileListAll();
        }

        private static NodeData FindNodeDataByPath(List<NodeData> nodes, string[] parts, int index)
        {
            if (index >= parts.Length) return null;
            foreach (var nd in nodes)
            {
                if (string.Equals(nd.Name, parts[index], StringComparison.OrdinalIgnoreCase))
                {
                    if (index == parts.Length - 1)
                        return nd;
                    return FindNodeDataByPath(nd.Children, parts, index + 1);
                }
            }
            return null;
        }

        private string GetFileListFullPath(NodeData nd)
        {
            return string.IsNullOrEmpty(_currentFolderPath)
                ? nd.Name
                : _currentFolderPath + "/" + nd.Name;
        }

        private FileListGridRow GetFileListRowData(int rowIndex)
        {
            if (_fileListGrid.DataSource is IList<FileListGridRow> list && rowIndex >= 0 && rowIndex < list.Count)
                return list[rowIndex];
            return null;
        }

        private void OnFileListCellClick(object sender, AntdUI.TableClickEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            if (e.Button == MouseButtons.Right)
            {
                var data = e.Record as FileListGridRow;
                if (data == null) data = GetFileListRowData(e.RowIndex);
                if (data == null) return;
                _fileListContextRow = e.RowIndex;
                _fileListContextCol = e.ColumnIndex;
                _fileListContextData = data;
                bool hasSelection = true;
                _fileListOpenItem.Enabled = hasSelection;
                _fileListDeleteItem.Enabled = hasSelection;
                _fileListExportItem.Enabled = hasSelection;
                UpdateMultiEditMenuState();
                AntdUI.ContextMenuStrip.open(_fileListGrid, it =>
                {
                    string tag = it.Tag as string;
                    switch (tag)
                    {
                        case "open": OnFileListOpen(it, EventArgs.Empty); break;
                        case "copy": OnFileListCopyCell(it, EventArgs.Empty); break;
                        case "delete": OnFileListDelete(it, EventArgs.Empty); break;
                        case "export": OnFileListExport(it, EventArgs.Empty); break;
                        case "multiEdit": OnFileListMultiEdit(it, EventArgs.Empty); break;
                        case "import": OnFileListImport(it, EventArgs.Empty); break;
                        case "addFavorite": OnFileListAddToFavorites(it, EventArgs.Empty); break;
                        case "resetWidths": TableWidthStore.Reset(TableWidthStore.TableId.FileList, _fileListGrid); break;
                    }
                }, _fileListContextMenuItems);
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                // 单击仅选中（AntdUI.Table 默认行为），打开文件统一由双击或右键菜单处理
            }
        }

        private void OnFileListCellDoubleClick(object sender, AntdUI.TableClickEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var data = e.Record as FileListGridRow;
            if (data == null) data = GetFileListRowData(e.RowIndex);
            if (data == null) return;

            if (data.Tag is NodeData nd && !nd.IsDirectory)
            {
                string fullPath = GetFileRowPath(data) ?? GetFileListFullPath(nd);
                _currentFileRelPath = fullPath;
                _currentFilePathLabel.Text = fullPath;
                _currentFilePathLabel.Visible = true;
                _pendingFileData = null;
                if (nd.RecordIndex.HasValue)
                    OpenFileForEditing(nd, fullPath);
                else
                    OpenNewBlankFile(fullPath);
                SwitchToEditor();
            }
        }

        private void OnFileListSelectIndexChanged(object sender, EventArgs e)
        {
            if (_dragListFinished) return;
            SyncCheckedFromHighlight();
        }

        private AntdUI.Table.CellStyleInfo OnFileListGridSetRowStyle(object sender, AntdUI.TableSetRowStyleEventArgs e)
        {
            if (e.RowType != AntdUI.RowType.None) return null;
            if (e.Record is not FileListGridRow row) return null;

            if (row.RowForeColor != Color.Empty)
            {
                return new AntdUI.Table.CellStyleInfo { ForeColor = row.RowForeColor };
            }

            return null;
        }

        private void OnFileListMouseClick(object sender, MouseEventArgs e)
        {
        }

        private void ResetFileListSelectionState()
        {
            _checkedRows1.Clear();
            _dragListActive = false;
            _dragListMoved = false;
            _dragListFinished = false;
            _syncOverallPending = false;
            try { _fileListGrid.SelectedIndexs = Array.Empty<int>(); } catch { }
        }

        private IList<FileListGridRow> GetFileListGridRows()
        {
            return _fileListGrid.DataSource as IList<FileListGridRow>;
        }

        private void RebuildSelectionMirror()
        {
            _checkedRows1.Clear();
            var list = GetFileListGridRows();
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Checked) _checkedRows1.Add(i + 1);
            }
            _syncingSelection = true;
            try { _fileListGrid.SelectedIndexs = _checkedRows1.ToArray(); }
            finally { _syncingSelection = false; }
        }

        private void SyncHighlightFromChecked()
        {
            _syncingSelection = true;
            try { _fileListGrid.SelectedIndexs = _checkedRows1.ToArray(); }
            finally { _syncingSelection = false; }
        }

        private void SyncCheckedFromHighlight()
        {
            if (_syncingSelection) return;
            var list = GetFileListGridRows();
            if (list == null) return;
            var sel = new HashSet<int>(_fileListGrid.SelectedIndexs ?? Array.Empty<int>());
            _checkedRows1.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                int idx = i + 1;
                bool want = sel.Contains(idx);
                if (want) _checkedRows1.Add(idx);
                list[i].Checked = want;
            }
        }

        private void OnFileListCheckedChanged(object sender, AntdUI.TableCheckEventArgs e)
        {
            if (_syncingSelection || e.RowIndex < 1) return;
            if (e.Value)
            {
                _checkedRows1.Add(e.RowIndex);
            }
            else
            {
                if (_dragListFinished && _checkedRows1.Contains(e.RowIndex))
                {
                    var list = GetFileListGridRows();
                    if (list != null && e.RowIndex <= list.Count)
                        list[e.RowIndex - 1].Checked = true;
                    return;
                }
                _checkedRows1.Remove(e.RowIndex);
            }
            if (_syncOverallPending) return;
            SyncHighlightFromChecked();
        }

        private void OnFileListCheckedOverallChanged(object sender, AntdUI.Table.CheckStateEventArgs e)
        {
            _syncOverallPending = true;
            try
            {
                _fileListGrid.BeginInvoke(() =>
                {
                    _syncOverallPending = false;
                    SyncHighlightFromChecked();
                });
            }
            catch
            {
                _syncOverallPending = false;
            }
        }

        private void OnFileListMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var cell = _fileListGrid.HitTest(e.X, e.Y, out int row, out int iCel);
            if (cell == null || row < 1) return;
            var list = GetFileListGridRows();
            if (list == null || row > list.Count) return;
            _dragListActive = true;
            _dragListMoved = false;
            _dragListFinished = false;
            _dragListStartRow = row;
            _dragListLastRow = row;
            if (iCel > 0)
            {
                bool ctrl = ModifierKeys.HasFlag(Keys.Control);
                if (ctrl)
                {
                    if (list[row - 1].Checked)
                    {
                        list[row - 1].Checked = false;
                        _checkedRows1.Remove(row);
                    }
                    else
                    {
                        list[row - 1].Checked = true;
                        _checkedRows1.Add(row);
                    }
                    SyncHighlightFromChecked();
                }
                else
                {
                    bool changed = false;
                    foreach (int idx in _checkedRows1)
                    {
                        if (idx >= 1 && idx <= list.Count && idx != row)
                        {
                            if (list[idx - 1].Checked)
                            {
                                list[idx - 1].Checked = false;
                                changed = true;
                            }
                        }
                    }
                    _checkedRows1.Clear();
                    _checkedRows1.Add(row);
                    if (!list[row - 1].Checked)
                    {
                        list[row - 1].Checked = true;
                        changed = true;
                    }
                    if (changed) SyncHighlightFromChecked();
                }
            }
            StartListDragScroll();
        }

        private void OnFileListMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragListActive) return;
            int h = _fileListGrid.Height;
            if (e.Y < 0 || e.Y > h) return;
            ExtendListDragSelection(e.X, Math.Min(Math.Max(e.Y, 1), h - 1));
        }

        private void ExtendListDragSelection(int x, int y)
        {
            var cell = _fileListGrid.HitTest(x, y, out int row, out _);
            if (cell == null || row < 1) return;
            if (row == _dragListLastRow) return;
            _dragListMoved = true;
            _dragListLastRow = row;
            var list = GetFileListGridRows();
            if (list == null) return;
            int a = Math.Min(_dragListStartRow, row);
            int b = Math.Max(_dragListStartRow, row);
            if (a < 1) a = 1;
            if (b > list.Count) b = list.Count;
            bool changed = false;
            for (int i = a; i <= b; i++)
            {
                var r = list[i - 1];
                if (!r.Checked)
                {
                    r.Checked = true;
                    _checkedRows1.Add(i);
                    changed = true;
                }
            }
            if (changed) SyncHighlightFromChecked();
        }

        private void StartListDragScroll()
        {
            if (_dragListScrollTimer == null)
            {
                _dragListScrollTimer = new System.Windows.Forms.Timer();
                _dragListScrollTimer.Interval = 50;
                _dragListScrollTimer.Tick += OnListDragScrollTick;
            }
            _dragListScrollTimer.Start();
        }

        private void StopListDragScroll()
        {
            _dragListScrollTimer?.Stop();
        }

        private void OnListDragScrollTick(object sender, EventArgs e)
        {
            if (!_dragListActive) return;
            var sb = _fileListGrid.ScrollBar;
            if (sb == null || !sb.ShowY || sb.MaxY <= 0) return;
            var pt = _fileListGrid.PointToClient(Cursor.Position);
            int h = _fileListGrid.Height;
            bool scrolled = false;
            int before = sb.ValueY;
            if (pt.Y < DragListScrollEdge)
            {
                sb.ValueY = before - DragListScrollStep;
            }
            else if (pt.Y > h - DragListScrollEdge)
            {
                sb.ValueY = before + DragListScrollStep;
            }
            scrolled = sb.ValueY != before;
            if (scrolled)
            {
                ExtendListDragSelection(pt.X, Math.Min(Math.Max(pt.Y, 1), h - 1));
            }
        }

        private void OnFileListMouseUp(object sender, MouseEventArgs e)
        {
            if (!_dragListActive) return;
            _dragListActive = false;
            StopListDragScroll();
            bool ctrl = ModifierKeys.HasFlag(Keys.Control);
            if (!_dragListMoved && !ctrl) return;
            _dragListFinished = true;
            try
            {
                _fileListGrid.BeginInvoke(() =>
                {
                    _dragListFinished = false;
                    SyncHighlightFromChecked();
                });
            }
            catch
            {
                _dragListFinished = false;
            }
        }

        private string GetFileDescription(string fullPath, NodeData nd, string tempFilePath = null)
        {
            lock (_fileDescCacheLock)
            {
                if (_fileDescriptionCache == null)
                    _fileDescriptionCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (_fileDescriptionCache.TryGetValue(fullPath, out var cached))
                {
                    if (string.IsNullOrEmpty(tempFilePath) || !File.Exists(tempFilePath))
                        return cached;
                }
            }

            string desc = "";
            string ext = Path.GetExtension(fullPath);
            if (!string.Equals(ext, ".dbr", StringComparison.OrdinalIgnoreCase))
            {
                lock (_fileDescCacheLock) _fileDescriptionCache[fullPath] = desc;
                return desc;
            }

            try
            {
                if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    string all = EncodingService.ReadTextFile(tempFilePath);
                    desc = DbrHelper.ParseDbrFields(all, includeTags: false).Description;
                }
                else if (_isArz && nd.RecordIndex.HasValue)
                {
                    var record = _arzReader.GetRecord(nd.RecordIndex.Value);
                    using (var ms = new MemoryStream())
                    {
                        record.SaveToStream(ms);
                        ms.Position = 0;
                        using (var sr = new StreamReader(ms, Utf8NoBom))
                        {
                            string all = sr.ReadToEnd();
                            desc = DbrHelper.ParseDbrFields(all, includeTags: false).Description;
                        }
                    }
                }
                else if (!_isArz && nd.RecordIndex.HasValue)
                {
                    var entry = _arcFile.toc[nd.RecordIndex.Value];
                    using (var ms = new MemoryStream())
                    {
                        _arcFile.UnpackToStream(entry, ms);
                        desc = DbrHelper.ParseDbrFields(TemplateHelper.DecodeText(ms.ToArray()), includeTags: false).Description;
                    }
                }
            }
            catch
            {
            }

            lock (_fileDescCacheLock) _fileDescriptionCache[fullPath] = desc;
            return desc;
        }

        private void GetFileDisplayTexts(string fullPath, NodeData nd, string tempFilePath, Stream archiveStream,
            out string description, out string annotation, out string annotationTag, out string templateName)
        {
            description = "";
            annotation = "";
            annotationTag = "";
            templateName = null;
            string ext = Path.GetExtension(fullPath);
            if (!string.Equals(ext, ".dbr", StringComparison.OrdinalIgnoreCase)) return;

            // 缓存命中：内容依据的时间戳未变则直接复用（省一次解压 + 整份文本解析）
            bool hasTempFile = !string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath);
            DateTime stamp;
            try { stamp = hasTempFile ? File.GetLastWriteTime(tempFilePath) : (nd?.FileTime ?? DateTime.MinValue); }
            catch { stamp = DateTime.MinValue; }
            if (_fileDisplayTextCache.TryGetValue(fullPath, out var cached) && cached.Stamp == stamp)
            {
                description = cached.Description;
                annotation = cached.Annotation;
                annotationTag = cached.AnnotationTag;
                templateName = cached.Template;
                return;
            }

            string text = null;
            if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
            {
                try { text = EncodingService.ReadTextFile(tempFilePath); }
                catch { }
            }
            else if (_isArz && nd.RecordIndex.HasValue)
            {
                try
                {
                    var record = archiveStream != null
                        ? _arzReader.GetRecord(nd.RecordIndex.Value, archiveStream)
                        : _arzReader.GetRecord(nd.RecordIndex.Value);
                    try
                    {
                        using var ms = new MemoryStream();
                        record.SaveToStream(ms);
                        ms.Position = 0;
                        using var sr = new StreamReader(ms, Utf8NoBom);
                        text = sr.ReadToEnd();
                    }
                    finally
                    {
                        if (archiveStream != null) record?.DiscardData();
                    }
                }
                catch { }
            }
            else if (!_isArz && nd.RecordIndex.HasValue)
            {
                try
                {
                    var entry = _arcFile.toc[nd.RecordIndex.Value];
                    using var ms = new MemoryStream();
                    if (archiveStream != null)
                        _arcFile.UnpackToStream(entry, archiveStream, ms);
                    else
                        _arcFile.UnpackToStream(entry, ms);
                    text = TemplateHelper.DecodeText(ms.ToArray());
                }
                catch { }
            }
            if (string.IsNullOrEmpty(text)) return;

            // 模板名提取（零额外读取）：DBR 文本本已为描述/tag 解读，模板名取自同一份文本的第一行。
            // 顺带写入 _dbrTplNameCache，同模板扫描/多文件编辑菜单直接命中缓存，无需再解包。
            // 只取第一行判定（与 TemplateHelper.GetDbrTemplateName 的既有口径一致），避免对大文本整体 Split
            int nl = text.IndexOf('\n');
            templateName = TemplateHelper.GetDbrTemplateName(new[] { nl >= 0 ? text.Substring(0, nl) : text });
            if (!string.IsNullOrEmpty(templateName))
                _dbrTplNameCache[fullPath] = templateName;

            var parsed = DbrHelper.ParseDbrFields(text, recordName: fullPath);
            description = parsed.Description;
            annotationTag = parsed.FirstTagValue;
            // 与 MOD合并窗同一套公共回退语义：有本地化文本用本地化文本，否则显示原始 tag
            // （未启用语言包时同样显示原始 tag，不再空白）
            if (!string.IsNullOrEmpty(annotationTag))
                annotation = LocalizationHelper.LookupLocalizedTag(annotationTag, _currentLanguageDict);

            // 写入缓存（含"无模板名"的结果：描述/本地化名仍可复用；文本读不到的分支在上面已提前返回，不缓存）
            _fileDisplayTextCache[fullPath] = new FileDisplayTextCache
            {
                Stamp = stamp,
                Description = description,
                Annotation = annotation,
                AnnotationTag = annotationTag,
                Template = templateName
            };
        }

        private void OnFileListOpen(object sender, EventArgs e)
        {
            var data = _fileListContextData;
            if (data == null && _fileListContextRow >= 1)
                data = GetFileListRowData(_fileListContextRow - 1);
            if (data?.Tag is NodeData nd && !nd.IsDirectory)
            {
                string fullPath = GetFileRowPath(data) ?? GetFileListFullPath(nd);
                _currentFileRelPath = fullPath;
                _currentFilePathLabel.Text = fullPath;
                _currentFilePathLabel.Visible = true;
                _favoriteButton.Visible = true;
                UpdateFavoriteButtonState();
                _pendingFileData = null;
                if (nd.RecordIndex.HasValue)
                    OpenFileForEditing(nd, fullPath);
                else
                    OpenNewBlankFile(fullPath);
                SwitchToEditor();
            }
        }

        private void OnFileListCopyCell(object sender, EventArgs e)
        {
            var data = _fileListContextData;
            if (data == null && _fileListContextRow >= 1)
                data = GetFileListRowData(_fileListContextRow - 1);
            if (data == null) return;
            string text = "";
            if (_fileListContextCol == 1) text = data.FileName;
            else if (_fileListContextCol == 2) text = data.FileDescription;
            else if (_fileListContextCol == 3) text = data.ModifyTime;
            else if (_fileListContextCol == 4) text = data.Notes;
            else if (_fileListContextCol == 5) text = data.Template;
            if (!string.IsNullOrEmpty(text))
                Clipboard.SetText(text);
        }

        private List<(string path, NodeData nd)> GetSelectedFileListData()
        {
            var result = new List<(string path, NodeData nd)>();
            if (_fileListGrid.DataSource is IList<FileListGridRow> rows)
            {
                foreach (var idx in _fileListGrid.SelectedIndexs)
                {
                    int ri = idx - 1;
                    if (ri >= 0 && ri < rows.Count && rows[ri].Tag is NodeData nd && !nd.IsDirectory)
                    {
                        string fullPath = GetFileRowPath(rows[ri]) ?? GetFileListFullPath(nd);
                        result.Add((fullPath, nd));
                    }
                }
            }
            if (result.Count == 0 && _fileListContextData?.Tag is NodeData ctxNd && !ctxNd.IsDirectory)
            {
                string fullPath = GetFileRowPath(_fileListContextData) ?? GetFileListFullPath(ctxNd);
                result.Add((fullPath, ctxNd));
            }
            return result;
        }

        private string GetFileRowPath(FileListGridRow row)
        {
            if (row == null) return null;
            if (!string.IsNullOrEmpty(row.RelPath)) return row.RelPath;
            if (row.Tag is NodeData nd) return GetFileListFullPath(nd);
            return null;
        }

        private List<(string path, NodeData nd)> GetCheckedFileListData()
        {
            var result = new List<(string path, NodeData nd)>();
            if (_fileListGrid.DataSource is IList<FileListGridRow> rows)
            {
                foreach (var r in rows)
                {
                    if (r.Checked && r.Tag is NodeData nd && !nd.IsDirectory)
                    {
                        string fullPath = GetFileRowPath(r) ?? GetFileListFullPath(nd);
                        result.Add((fullPath, nd));
                    }
                }
            }
            return result;
        }

        private void UpdateMultiEditMenuState()
        {
            string sameTplText = LanguageManager.Instance.GetText("Editor.MultiEditSameTemplate", "同时编辑");
            string requireTplText = LanguageManager.Instance.GetText("Editor.MultiEditRequireSameTpl", "同时编辑（必须为相同模板）");
            var checkedRows = GetCheckedFileListData();
            if (checkedRows.Count >= 2 && checkedRows.Count <= 100)
            {
                bool allSame = true;
                string tpl0 = null;
                foreach (var (fp, nd) in checkedRows)
                {
                    string tpl = GetDbrTplNameCached(fp, nd.RecordIndex) ?? "";
                    if (tpl0 == null) tpl0 = tpl;
                    else if (!string.Equals(tpl0, tpl, StringComparison.OrdinalIgnoreCase)) { allSame = false; break; }
                }
                if (allSame && !string.IsNullOrEmpty(tpl0))
                {
                    _fileListMultiEditItem.Enabled = true;
                    _fileListMultiEditItem.Text = sameTplText;
                    return;
                }
            }
            _fileListMultiEditItem.Enabled = false;
            _fileListMultiEditItem.Text = requireTplText;
        }

        private void OnFileListMultiEdit(object sender, EventArgs e)
        {
            if (_isMultiLoading) return;   // 正在后台加载时不重复触发（进度已由全窗 Spin 展示）
            if (_multiFileRelPaths.Count > 0) ExitMultiFileMode();
            var checkedRows = GetCheckedFileListData();
            if (checkedRows.Count < 2) return;
            string basePath = checkedRows[0].path;
            var first = checkedRows[0].nd;
            var otherPaths = checkedRows.Skip(1).Select(x => x.path).ToList();

            if (!string.Equals(_currentFileRelPath, basePath, StringComparison.OrdinalIgnoreCase) ||
                _currentEditorMode != EditorMode.DbrGrid)
            {
                _currentFileRelPath = basePath;
                _currentFilePathLabel.Text = basePath;
                _currentFilePathLabel.Visible = true;
                _favoriteButton.Visible = true;
                UpdateFavoriteButtonState();
                _pendingFileData = null;
                if (first.RecordIndex.HasValue)
                    OpenFileForEditing(first, basePath);
                else
                    OpenNewBlankFile(basePath);
                SwitchToEditor();
            }
            if (_currentEditorMode != EditorMode.DbrGrid || _currentVariables == null ||
                string.IsNullOrEmpty(_currentDbrTplName))
            {
                MessageBox.Show(
                    LanguageManager.Instance.GetText("Editor.NoBaseDbrForScan", "请先打开一个带模板的 DBR 文件。"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            EnterMultiFileMode(otherPaths);
        }

        private void OnFileListDelete(object sender, EventArgs e)
        {
            var filesToDelete = GetCheckedFileListData();
            if (filesToDelete.Count == 0) return;

            if (filesToDelete.Count == 1)
            {
                string msg = string.Format(
                    LanguageManager.Instance.GetText("Editor.ConfirmDelete", "确定要删除文件 {0} 吗？"), filesToDelete[0].path);
                if (MessageBox.Show(msg, LanguageManager.Instance.GetText("Common.MessageBoxTitle.Confirm", "确认"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                DeleteFile(filesToDelete[0].path, filesToDelete[0].nd);
                RemoveFileListRow(filesToDelete[0].path);   // 列表增量移除该行（不再全量重扫）
                UpdateModifiedList();
            }
            else
            {
                string msg = string.Format(
                    LanguageManager.Instance.GetText("Editor.ConfirmDeleteMulti", "确定要删除选中的 {0} 个项目吗？"), filesToDelete.Count);
                if (MessageBox.Show(msg, LanguageManager.Instance.GetText("Common.MessageBoxTitle.Confirm", "确认"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                foreach (var (fp, nd) in filesToDelete)
                    DeleteFile(fp, nd, true);
                foreach (var (fp, _) in filesToDelete)
                    RemoveFileListRow(fp);
                UpdateModifiedList();
                if (_currentFileRelPath != null && filesToDelete.Any(f => f.path == _currentFileRelPath)
                    || filesToDelete.Count == 0)
                    ShowPlaceholder();
            }
        }

        private async void OnFileListExport(object sender, EventArgs e)
        {
            var filesToExport = GetCheckedFileListData();
            if (filesToExport.Count == 0) return;

            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = LanguageManager.Instance.GetText("Editor.ExportFolderTitle", "选择导出目录");
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string baseDir = dlg.SelectedPath;

                int total = filesToExport.Count;
                LogOutput(string.Format(
                    LanguageManager.Instance.GetText("Editor.ExportStart", "开始导出 {0} 个文件到: {1}"),
                    total, baseDir));

                try
                {
                    await Task.Run(() =>
                    {
                        for (int i = 0; i < total; i++)
                        {
                            var (fp, nd) = filesToExport[i];
                            string dest = Path.Combine(baseDir, fp);
                            ExportSingleFile(fp, nd, dest);
                            LogOutput(string.Format(
                                LanguageManager.Instance.GetText("Editor.ExportingFile", "正在导出: {0} ({1}/{2})"),
                                fp, i + 1, total));
                        }
                    });
                    LogOutput(string.Format(
                        LanguageManager.Instance.GetText("Editor.ExportComplete", "导出完成: {0} 个文件 -> {1}"),
                        total, baseDir));
                }
                catch (Exception ex)
                {
                    LogOutput(string.Format(
                        LanguageManager.Instance.GetText("Editor.ExportError", "导出失败: {0}"),
                        ex.Message), "ERROR");
                }
            }
        }

        private async void OnFileListImport(object sender, EventArgs e)
        {
            string defaultFolder = _currentFolderPath ?? "";

            var existing = new HashSet<string>(_allKnownPaths, StringComparer.OrdinalIgnoreCase);
            var allFolders = _allFolderPaths;

            var picker = new ImportDialog(existing, allFolders, defaultFolder, _archiveFilePath);
            picker.Width = 900;
            picker.Height = 650;

            List<(string sourcePath, string relPath)> selectedFiles = null;
            AntdUI.Modal.Config config = null;
            config = new AntdUI.Modal.Config(this,
                LanguageManager.Instance.GetText("Editor.ImportFileTitle", "导入文件"),
                picker)
            {
                Resizable = true,
                Draggable = true
            };
            config.SetWidth(950);
            config.SetOk(LanguageManager.Instance.GetText("Editor.ImportStart", "确定"));
            config.SetCancel(LanguageManager.Instance.GetText("Common.Cancel", "取消"));
            config.OnOk = cfg =>
            {
                if (!picker.ConfirmStart()) return false;
                selectedFiles = picker.Files;
                return true;
            };
            AntdUI.Modal.open(config);

            if (selectedFiles == null || selectedFiles.Count == 0) return;

            int total = selectedFiles.Count;
            var importedRelPaths = new List<string>();
            var status = await RunWithCancelAsync(LanguageManager.Instance.GetText("Editor.ImportLabel", "导入"), (progress, ct) =>
            {
                for (int i = 0; i < selectedFiles.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (sourcePath, relPath) = selectedFiles[i];
                    if (!CheckImportNameUsable(relPath, progress)) continue;
                    string destPath = _session.GetTempFilePath(relPath);
                    string dir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    WriteImportedFileToTemp(sourcePath, relPath, destPath, progress);
                    importedRelPaths.Add(relPath);
                    _importedPaths.Add(relPath);
                    progress.ReportLog(string.Format(
                        LanguageManager.Instance.GetText("Editor.ImportProgress", "导入: ({0}/{1}) {2}"),
                        i + 1, total, relPath));
                }
                return true;
            });

            if (status == OperationResult.Cancelled) return;

            foreach (string relPath in importedRelPaths)
            {
                bool isNewFile = !_allKnownPaths.Contains(relPath);
                if (isNewFile)
                    _addedFiles.Add(relPath);
                _modifiedFiles.Add(relPath);
                _deletedFiles.Remove(relPath);
                RecordStatusChange(relPath);

                // 文件级变更记录：MOD 作者必须完整看到"哪些文件进了包"，故**一律记录**。
                // 「详细日志」开关只用于过程性进度行（解包/打包的 (N/M) 行），不得吞掉变更记录——
                // 否则变更报告与汇总会漏掉导入的文件（曾因此出现"导入了文件却没记录"）。
                LogFileChange(relPath, isNewFile ? LogService.VerbAdd : LogService.VerbMod);
            }
            if (importedRelPaths.Count > 0)
            {
                var parentNodesToSort = new HashSet<AntdUI.TreeItem>();
                _fileTree.PauseLayout = true;
                try
                {
                    foreach (string relPath in importedRelPaths)
                    {
                        var parent = InsertFileNodeWithFolders(relPath);
                        if (parent != null)
                            parentNodesToSort.Add(parent);
                    }
                }
                finally
                {
                    BuildFileNodeMap();
                    _fileTree.PauseLayout = false;
                }
                foreach (string relPath in importedRelPaths)
                {
                    if (_addedFiles.Contains(relPath))
                        SetNodeState(relPath, "a");
                    else
                        SetNodeState(relPath, "m");
                }
                foreach (var pn in parentNodesToSort)
                    SortNodeChildren(pn.Sub);
                // 收尾：树节点色 + 文件夹链路色 + 文件列表**增量插入行** + 右侧列表。
                // 原实现调 RefreshAllAfterChange()：平铺模式会全量重扫 6 万文件重建列表并弹 Spin。
                foreach (string relPath in importedRelPaths)
                {
                    RefreshFileTreeNodeColor(relPath);
                    RefreshFolderTreeNodeColor(relPath);
                    InsertFileListRow(relPath);
                }
                UpdateModifiedList();
            }
            _statusLabel.Text = string.Format(
                LanguageManager.Instance.GetText("Editor.ImportDone", "导入完成: {0} 个文件"), importedRelPaths.Count);
        }

        private void SaveCurrentEditorIfDirty()
        {
            // 保存 / 切换文件 / 固实前：先把源码模式的行级改动落进变更记录。
            // 必须早于 _editorDirty 判断——源码模式编辑不会置脏，若放在其后会被直接 return 掉而漏记录。
            LogSourceLineChanges();

            if (!_editorDirty || string.IsNullOrEmpty(_currentFileRelPath)) return;

            bool handled = false;
            bool ok = true;
            if (_editorTabControl.Visible)
            {
                if (_currentEditorMode == EditorMode.DbrGrid)
                {
                    handled = true;
                    ok = SaveCurrentDbrToTemp();
                    if (ok) RefreshFileListRow(_currentFileRelPath);
                }
                else if (_currentEditorMode == EditorMode.SourceEditor)
                {
                    handled = true;
                    _fileListLoadCts?.Cancel();
                    string tempFile = _session.GetTempFilePath(_currentFileRelPath);
                    if (TryWriteTempText(tempFile, _dbrSourceEditor.Text))
                    {
                        _modifiedFiles.Add(_currentFileRelPath);
                        RefreshFileListRow(_currentFileRelPath);
                    }
                    else
                    {
                        NotifyTempWriteFailed(_currentFileRelPath);
                        ok = false;
                    }
                }
            }
            if (!handled || ok) _editorDirty = false;
        }

        // ======================== Modified Files Management ========================

        private void RecordStatusChange(string relPath)
        {
            _statusChangeLog.RemoveAll(p => string.Equals(p, relPath, StringComparison.OrdinalIgnoreCase));
            _statusChangeLog.Add(relPath);
        }

        private void RemoveStatusChange(string relPath)
        {
            _statusChangeLog.RemoveAll(p => string.Equals(p, relPath, StringComparison.OrdinalIgnoreCase));
        }

        private void MarkFileModified(string relPath, bool immediateRefresh = true)
        {
            _session.GetTempFilePath(relPath); // ensure dir exists
            _modifiedFiles.Add(relPath);
            var st = GetNodeState(relPath);
            if (st != "a" && st != "d")
                SetNodeState(relPath, "m");
            _deletedFiles.Remove(relPath);
            if (GetNodeState(relPath) == "d")
                SetNodeState(relPath, "");
            RecordStatusChange(relPath);
            if (immediateRefresh)
            {
                // 改值/状态变化（树结构未变）：只做**局部刷新** —— 该文件节点色、其所在文件夹链路色、
                // 文件列表该行**着色**（不重建行集合）、右侧已修改列表。
                // 原实现走 RefreshAllAfterChange(rebuildFolderTree:false)（仍整树重建）+ RefreshFileListRow（全量重绑行）：
                // 平铺 6 万文件时改一格就会"整树重建 + 6 万行重绑 + 逐个文件重扫"，表现为明显卡顿。
                RefreshFileTreeNodeColor(relPath);
                RefreshFolderTreeNodeColor(relPath);
                UpdateFileListRowVisual(relPath);
                UpdateModifiedList();
            }
        }

        private void UnmarkFileModified(string relPath)
        {
            // 该文件是"新增/导入"的吗？—— 撤销新增时它**不属于原包**（只存在于临时目录），
            // 必须从文件树里移除，否则列表重建后仍显示这个已撤销的文件（原实现的缺陷）
            bool wasAdded = _addedFiles.Contains(relPath);
            _modifiedFiles.Remove(relPath);
            _modifiedDbrFields.Remove(relPath);
            _addedFiles.Remove(relPath);
            _deletedFiles.Remove(relPath);
            _deletedFileRecordIndices.Remove(relPath);
            RemoveStatusChange(relPath);
            SetNodeState(relPath, "");
            if (wasAdded)
            {
                // 撤销"新增/导入"：该文件不属于原包 → 从文件树（UI 与数据两层）移除，重建映射与树。
                // 用 RemoveFileNodeFromTree（先走 TreeItem 层、失败兜底 NodeData 层），比只调
                // RemoveNodeDataByPath 更可靠（父目录为懒加载占位时后者会找不到节点）。
                RemoveFileNodeFromTree(relPath);
                BuildFileNodeMap();
                RemoveFileListRow(relPath);      // 列表增量移除该行（不再全量重扫）
                UpdateModifiedList();
            }
            else
            {
                RefreshFileTreeNodeColor(relPath);
                RefreshFolderTreeNodeColor(relPath);
                UpdateFileListRowVisual(relPath);
                UpdateModifiedList();
            }
        }

                private void UpdateModifiedList()
        {
            var rows = new List<ModifiedFileRow>();

            foreach (var f in _addedFiles)
            {
                rows.Add(new ModifiedFileRow
                {
                    FilePath = f,
                    CellTags = new AntdUI.CellTag[] { new AntdUI.CellTag(
                        LanguageManager.Instance.GetText("ModifiedStatus.Added", "增"), AntdUI.TTypeMini.Success) },
                    CellLinks = new AntdUI.CellLink[] { new AntdUI.CellButton("undo",
                        "×", AntdUI.TTypeMini.Error) }
                });
            }

            foreach (var f in _modifiedFiles)
            {
                if (_addedFiles.Contains(f)) continue;
                rows.Add(new ModifiedFileRow
                {
                    FilePath = f,
                    CellTags = new AntdUI.CellTag[] { new AntdUI.CellTag(
                        LanguageManager.Instance.GetText("ModifiedStatus.Modified", "改"), AntdUI.TTypeMini.Error) },
                    CellLinks = new AntdUI.CellLink[] { new AntdUI.CellButton("undo",
                        "×", AntdUI.TTypeMini.Error) }
                });
            }

            foreach (var f in _deletedFiles)
            {
                rows.Add(new ModifiedFileRow
                {
                    FilePath = f,
                    CellTags = new AntdUI.CellTag[] { new AntdUI.CellTag(
                        LanguageManager.Instance.GetText("ModifiedStatus.Deleted", "删"), AntdUI.TTypeMini.Default) },
                    CellLinks = new AntdUI.CellLink[] { new AntdUI.CellButton("undo",
                        "×", AntdUI.TTypeMini.Error) }
                });
            }

            _modifiedList.DataSource = rows;
        }

        private void SetNodeState(string relPath, string state)
        {
            var nd = FindNodeDataByPath(relPath);
            if (nd != null)
                nd.State = state;
        }

        private string GetNodeState(string relPath)
        {
            var nd = FindNodeDataByPath(relPath);
            return nd?.State ?? "";
        }

        private void BuildFileNodeMap()
        {
            _fileNodeMap.Clear();
            if (_fileTree.Items.Count > 0 && _fileTree.Items[0].Tag == null)
            {
                _fileNodeMap[""] = _fileTree.Items[0];
                BuildFileNodeMapRecurse(_fileTree.Items[0].Sub, "");
            }
            else
            {
                BuildFileNodeMapRecurse(_fileTree.Items, "");
            }
            RebuildPathCache();
            RefreshFolderTree(rebuildStructure: true);
        }

        private void BuildFileNodeMapRecurse(AntdUI.TreeItemCollection nodes, string parentPath)
        {
            foreach (AntdUI.TreeItem node in nodes)
            {
                var data = node.Tag as NodeData;
                if (data == null)
                {
                    BuildFileNodeMapRecurse(node.Sub, parentPath);
                    continue;
                }
                string path = string.IsNullOrEmpty(parentPath) ? data.Name : parentPath + "/" + data.Name;
                _fileNodeMap[path] = node;
                if (data.IsDirectory)
                    BuildFileNodeMapRecurse(node.Sub, path);
            }
        }

        private void RebuildPathCache()
        {
            var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_fileTreeRootNodes != null)
            {
                foreach (var nd in _fileTreeRootNodes)
                    CollectPathsFromNodeData(nd, "", allPaths, dirSet);
            }

            // 补充：从 _fileNodeMap 收集目录路径（确保深层目录不遗漏）
            // 移除 dirSet.Count==0 限制，始终补充以防止 CollectPathsFromNodeData 遗漏深层
            if (_fileNodeMap != null && _fileNodeMap.Count > 0)
            {
                foreach (string key in _fileNodeMap.Keys)
                {
                    if (!string.IsNullOrEmpty(key) &&
                        _fileNodeMap.TryGetValue(key, out var tn) &&
                        tn.Tag is NodeData nd && nd.IsDirectory)
                        dirSet.Add(key);
                }
            }

            _allKnownPaths = allPaths;
            _allFolderPaths = dirSet.OrderBy(d => d).ToList();
        }

        private void CollectPathsFromNodeData(NodeData nd, string parentPath, HashSet<string> allPaths, HashSet<string> dirs)
        {
            string path = string.IsNullOrEmpty(parentPath) ? nd.Name : parentPath + "/" + nd.Name;
            allPaths.Add(path);
            if (nd.IsDirectory)
            {
                dirs.Add(path);
                foreach (var child in nd.Children)
                    CollectPathsFromNodeData(child, path, allPaths, dirs);
            }
        }

        // ======================== Folder-Only Tree Sync ========================

        // 文件夹树滚动面板：窗口缩放时同步Tree高度，宽度按内容计算
        private void OnFolderTreeScrollPanelResize(object sender, EventArgs e)
        {
            if (_folderTreeScrollPanel == null || _folderTree == null) return;
            int panelW = _folderTreeScrollPanel.ClientSize.Width;
            int panelH = _folderTreeScrollPanel.ClientSize.Height;
            // 高度随面板变化；宽度取面板宽度与内容宽度的较大值
            _folderTree.Height = panelH;
            int contentW = CalculateFolderTreeMaxWidth();
            _folderTree.Width = Math.Max(panelW, contentW);
            _folderTreeScrollPanel.AutoScrollMinSize = new Size(contentW, 0);
        }

        // 计算文件夹树所有可见节点文本的最大宽度
        private int CalculateFolderTreeMaxWidth()
        {
            if (_folderTree == null || _folderTree.Items.Count == 0) return 0;
            using (var g = CreateGraphics())
            {
                int maxWidth = 0;
                var font = _folderTree.Font ?? Font;
                WalkFolderTreeWidth(g, font, _folderTree.Items, 0, ref maxWidth);
                return maxWidth;
            }
        }

        private void WalkFolderTreeWidth(Graphics g, Font font, AntdUI.TreeItemCollection nodes, int depth, ref int maxWidth)
        {
            foreach (AntdUI.TreeItem node in nodes)
            {
                if (!node.Visible) continue;
                int indent = 20 + depth * 16; // 图标20px + 每级缩进16px
                int textW = (int)g.MeasureString(node.Text, font).Width + indent + 16; // +16 右边距
                if (textW > maxWidth) maxWidth = textW;
                if (node.Sub != null && node.Sub.Count > 0 && node.Expand)
                    WalkFolderTreeWidth(g, font, node.Sub, depth + 1, ref maxWidth);
            }
        }

        private void RefreshFolderTree(bool rebuildStructure)
        {
            if (_folderTree == null) return;

            _folderTree.PauseLayout = true;
            try
            {
                if (rebuildStructure)
                {
                    _folderTree.Items.Clear();

                    string rootText = _archiveFilePath != null
                        ? Path.GetFileName(_archiveFilePath)
                        : LanguageManager.Instance.GetText("Editor.ArchiveRoot", "档案");

                    var rootNode = new AntdUI.TreeItem(rootText);
                    rootNode.Tag = _fileTree.Items.Count > 0 ? _fileTree.Items[0] : null;
                    _folderTree.Items.Add(rootNode);

                    if (_allFolderPaths != null && _allFolderPaths.Count > 0)
                    {
                        foreach (string dir in _allFolderPaths)
                        {
                            string[] parts = dir.Split('/');
                            AntdUI.TreeItem parent = rootNode;
                            for (int i = 0; i < parts.Length; i++)
                            {
                                AntdUI.TreeItem existing = FindChildTreeNode(parent, parts[i]);
                                if (existing == null)
                                    {
                                        string currentPath = string.Join("/", parts, 0, i + 1);
                                        var data = FindNodeDataByPath(currentPath);
                                        string nodeText = data != null && data.IsDirectory
                                            ? $"{data.Name} [{data.TotalItemCount}]{GetAnnotationSuffix(data.Name)}"
                                            : parts[i];
                                        existing = new AntdUI.TreeItem(nodeText);
                                        existing.Icon = _closedFolderIcon;
                                        if (_fileNodeMap.TryGetValue(currentPath, out var internalNode))
                                            existing.Tag = internalNode;
                                        // 重建时恢复保存前的展开状态（创建阶段 PARENT 为 null，仅置标志位，无动画）
                                        if (_preservedExpandedPaths != null && _preservedExpandedPaths.Contains(currentPath))
                                            existing.Expand = true;
                                        parent.Sub.Add(existing);
                                    }
                                parent = existing;
                            }
                        }
                    }

                    rootNode.Expand = true;
                    rootNode.Icon = _openFolderIcon;
                }
            }
            finally
            {
                _folderTree.PauseLayout = false;
            }

            // PauseLayout=false 触发 Tree 布局重建（ChangeList/BuildFlatList），
            // 此时 ParentItem 才被正确设置。着色必须在此之后执行，
            // 否则 ParentItem 为 null 导致 GetFolderTreePath 返回空串，所有节点颜色被重置。
            if (_folderTree.Items.Count > 0)
            {
                UpdateFolderTreeNodeTextRecursive(_folderTree.Items[0]);
                RefreshFolderNodeColorsFromRoot();
                UpdateFolderTreeNodeIconsRecursive(_folderTree.Items[0]);
            }

            if (rebuildStructure)
            {
                if (!string.IsNullOrEmpty(_currentFolderPath))
                    SelectFolderNode(_currentFolderPath);
            }

            // 内容加载后重算文件夹树宽度（水平滚动条）
            UpdateFolderTreeScrollWidth();
        }

        // 更新文件夹树滚动宽度（内容变化后调用）
        private void UpdateFolderTreeScrollWidth()
        {
            if (_folderTreeScrollPanel == null || _folderTree == null) return;
            int panelW = _folderTreeScrollPanel.ClientSize.Width;
            int panelH = _folderTreeScrollPanel.ClientSize.Height;
            _folderTree.Height = panelH;
            int contentW = CalculateFolderTreeMaxWidth();
            _folderTree.Width = Math.Max(panelW, contentW);
            _folderTreeScrollPanel.AutoScrollMinSize = new Size(contentW, 0);
        }

        private void UpdateFolderTreeNodeTextRecursive(AntdUI.TreeItem node)
        {
            // 先处理子节点：后序遍历，避免父节点 Text 被追加 [N](备注) 后
            // GetFolderTreePath 取 cur.Text 构建路径会出错
            if (node.Sub != null)
                foreach (AntdUI.TreeItem child in node.Sub)
                    UpdateFolderTreeNodeTextRecursive(child);

            string nodePath = GetFolderTreePath(node);
            if (!string.IsNullOrEmpty(nodePath))
            {
                var data = FindNodeDataByPath(nodePath);
                if (data != null && data.IsDirectory)
                {
                    node.Text = $"{data.Name} [{data.TotalItemCount}]{GetAnnotationSuffix(data.Name)}";
                }
            }
        }

        private void UpdateFolderTreeNodeColorRecursive(AntdUI.TreeItem node)
        {
            string nodePath = GetFolderTreePath(node);
            SetFolderNodeColorByPath(node, nodePath);
            if (node.Sub != null)
                foreach (AntdUI.TreeItem child in node.Sub)
                    UpdateFolderTreeNodeColorRecursive(child);
        }

        /// <summary>
        /// 自顶向下遍历文件夹树着色，不依赖 ParentItem（ParentItem 仅在 PauseLayout=false 后由 Tree 布局设置，
        /// 遍历期间 PauseLayout=true 导致 ParentItem 为 null，GetFolderTreePath 返回空串）。
        /// 此方法从根节点开始，通过逐级拼接节点名称构建路径，对所有层级的节点正确着色。
        /// </summary>
        private void RefreshFolderNodeColorsFromRoot()
        {
            if (_folderTree == null || _folderTree.Items.Count == 0) return;
            var root = _folderTree.Items[0];
            if (root.Sub != null)
                foreach (AntdUI.TreeItem child in root.Sub)
                    RefreshFolderNodeColorsRecursive(child, GetFolderTreeNodeName(child));
        }

        private void RefreshFolderNodeColorsRecursive(AntdUI.TreeItem node, string nodePath)
        {
            SetFolderNodeColorByPath(node, nodePath);
            if (node.Sub != null)
                foreach (AntdUI.TreeItem child in node.Sub)
                    RefreshFolderNodeColorsRecursive(child, nodePath + "/" + GetFolderTreeNodeName(child));
        }

        private void UpdateFolderTreeNodeIconsRecursive(AntdUI.TreeItem node)
        {
            if (node.Sub != null && node.Sub.Count > 0)
            {
                node.Icon = node.Expand ? _openFolderIcon : _closedFolderIcon;
                foreach (AntdUI.TreeItem child in node.Sub)
                    UpdateFolderTreeNodeIconsRecursive(child);
            }
        }

        private static AntdUI.TreeItem FindChildTreeNode(AntdUI.TreeItem parent, string name)
        {
            if (parent.Sub == null) return null;
            foreach (AntdUI.TreeItem child in parent.Sub)
            {
                if (string.Equals(child.Text, name, StringComparison.OrdinalIgnoreCase))
                    return child;
                // Text 可能已包含 [N] 或 (备注)，改由 NodeData.Name 匹配
                var fileNode = child.Tag as AntdUI.TreeItem;
                var data = fileNode?.Tag as NodeData;
                if (data != null && string.Equals(data.Name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
                // Tag 未设置时（深层目录未展开），剥离 Text 后缀后匹配
                string stripped = GetFolderTreeNodeName(child);
                if (string.Equals(stripped, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            }
            return null;
        }

        private void SelectFolderNode(string folderPath)
        {
            if (_folderTree.Items.Count == 0) return;
            AntdUI.TreeItem rootNode = _folderTree.Items[0];
            if (string.IsNullOrEmpty(folderPath))
            {
                _folderTree.SelectItem = rootNode;
                return;
            }
            AntdUI.TreeItem node = FindNodeByPath(rootNode, folderPath);
            _folderTree.SelectItem = node;
        }

        private static AntdUI.TreeItem FindNodeByPath(AntdUI.TreeItem root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            string[] parts = path.Split('/');
            AntdUI.TreeItem current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                AntdUI.TreeItem child = FindChildTreeNode(current, parts[i]);
                if (child == null) return null;
                current = child;
            }
            return current;
        }

        private void RecalculateAllNodeDataCounts()
        {
            if (_fileTreeRootNodes == null) return;
            foreach (var nd in _fileTreeRootNodes)
                RecalculateNodeDataCountRecurse(nd);
            UpdateDirectoryTextsInTree();
        }

        private static int RecalculateNodeDataCountRecurse(NodeData nodeData)
        {
            if (!nodeData.IsDirectory) return 1;
            foreach (var child in nodeData.Children)
                RecalculateNodeDataCountRecurse(child);
            nodeData.TotalItemCount = nodeData.Children.Sum(c => c.IsDirectory ? c.TotalItemCount : 1);
            return nodeData.TotalItemCount;
        }

        private void UpdateDirectoryTextsInTree()
        {
            void Walk(AntdUI.TreeItemCollection nodes)
            {
                foreach (AntdUI.TreeItem node in nodes)
                {
                    var data = node.Tag as NodeData;
                    if (data != null && data.IsDirectory)
                        node.Text = $"{data.Name} [{data.TotalItemCount}]{GetAnnotationSuffix(data.Name)}";
                    Walk(node.Sub);
                }
            }
            Walk(_fileTree.Items);
        }

        // AntdUI.Tree 自动处理节点绘制，无需自定义 DrawNode

        private void RefreshFileTreeColors()
        {
            _fileTree.PauseLayout = true;
            foreach (var kv in _fileNodeMap)
            {
                var nd = kv.Value.Tag as NodeData;
                if (nd == null) continue;
                if (nd.State == "a")
                    kv.Value.Fore = ThemeManager.Added;
                else if (nd.State == "d")
                    kv.Value.Fore = ThemeManager.Deleted;
                else if (nd.State == "m")
                    kv.Value.Fore = ThemeManager.Modified;
                else
                {
                    bool descendantModified = _modifiedFiles.Any(m =>
                        m.StartsWith(kv.Key + "/", StringComparison.OrdinalIgnoreCase));
                    kv.Value.Fore = descendantModified ? ThemeManager.Modified : ThemeManager.Text;
                }
            }
            _fileTree.PauseLayout = false;
        }

        private void RefreshAllAfterChange(bool rebuildFolderTree = true, bool refreshFileList = true,
            bool updateModifiedList = true, bool refreshColors = true)
        {
            // ⚠️ 只在"树结构变化"（新增/删除/改名）时才重建映射：BuildFileNodeMap 末尾会调用
            // RefreshFolderTree(rebuildStructure:true) 整树重建 + 全树 GDI+ 测宽。
            // 原条件是 "rebuildFolderTree || refreshFileList || ..."：调用方传 rebuildFolderTree:false
            // （改值/批量收尾等"结构未变"场景）时仍会整树重建 —— 与调用点注释的意图相反，
            // 也是"改一个单元格就卡"的主因。映射在打开包时已建立，结构未变时沿用即可。
            if (rebuildFolderTree)
                BuildFileNodeMap();
            if (refreshColors)
                RefreshFileTreeColors();
            if (refreshFileList)
            {
                if (_flatViewMode) RefreshFileListAll();
                else RefreshFileList();
            }
            if (updateModifiedList)
                UpdateModifiedList();
        }

        private void RefreshFolderTreeNodeColor(string relPath)
        {
            if (_folderTree == null || _folderTree.Items.Count == 0) return;
            string[] parts = relPath.Split('/');
            AntdUI.TreeItem current = _folderTree.Items[0];
            for (int i = 0; i < parts.Length; i++)
            {
                AntdUI.TreeItem child = FindChildTreeNode(current, parts[i]);
                if (child == null) break;
                current = child;
            }
            RefreshFolderNodeChain(current);
        }

        private void RefreshFolderNodeChain(AntdUI.TreeItem node)
        {
            while (node != null)
            {
                string path = GetFolderTreePath(node);
                SetFolderNodeColorByPath(node, path);
                node = node.ParentItem;
            }
        }

        private void SetFolderNodeColorByPath(AntdUI.TreeItem node, string nodePath)
        {
            if (string.IsNullOrEmpty(nodePath))
            {
                node.Fore = ThemeManager.Text;
                return;
            }
            string prefix = nodePath + "/";

            // 按 _statusChangeLog 反序查找该文件夹下最后变更的文件，以最后操作的颜色为准
            string lastStatus = null;
            for (int i = _statusChangeLog.Count - 1; i >= 0; i--)
            {
                string fp = _statusChangeLog[i];
                if (!fp.Equals(nodePath, StringComparison.OrdinalIgnoreCase) &&
                    !fp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (_deletedFiles.Contains(fp))
                { lastStatus = "deleted"; break; }
                if (_addedFiles.Contains(fp))
                { lastStatus = "added"; break; }
                if (_modifiedFiles.Contains(fp))
                { lastStatus = "modified"; break; }
                // 该文件已不在任何状态集中（已撤销），继续查找
            }

            if (lastStatus != null)
            {
                if (lastStatus == "deleted")
                    node.Fore = ThemeManager.Deleted;
                else if (lastStatus == "added")
                    node.Fore = ThemeManager.Added;
                else if (lastStatus == "modified")
                    node.Fore = ThemeManager.Modified;
            }
            else
            {
                // 回退：直接检查所有状态集
                bool hasDeleted = _deletedFiles.Any(f =>
                    f.Equals(nodePath, StringComparison.OrdinalIgnoreCase) ||
                    f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                bool hasAdded = _addedFiles.Any(f =>
                    f.Equals(nodePath, StringComparison.OrdinalIgnoreCase) ||
                    f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                bool hasModified = _modifiedFiles.Any(f =>
                    f.Equals(nodePath, StringComparison.OrdinalIgnoreCase) ||
                    f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (hasDeleted)
                    node.Fore = ThemeManager.Deleted;
                else if (hasAdded)
                    node.Fore = ThemeManager.Added;
                else if (hasModified)
                    node.Fore = ThemeManager.Modified;
                else
                    node.Fore = ThemeManager.Text;
            }
        }

        private void OnBackupOriginalChanged(object sender, EventArgs e)
        {
            SettingsManager.BackupOriginalFile = _backupCheckBox.Checked;
            SettingsManager.Save();
        }

        private void RefreshFolderAnnotations()
        {
            _fileTree.PauseLayout = true;
            foreach (var kv in _fileNodeMap)
            {
                var data = kv.Value.Tag as NodeData;
                if (data != null && data.IsDirectory)
                {
                    kv.Value.Text = $"{data.Name} [{data.TotalItemCount}]{GetAnnotationSuffix(data.Name)}";
                }
            }
            _fileTree.PauseLayout = false;

            // 同步更新左侧文件夹导航树节点文本（含备注后缀）
            if (_folderTree != null && _folderTree.Items.Count > 0)
            {
                _folderTree.PauseLayout = true;
                UpdateFolderTreeNodeTextRecursive(_folderTree.Items[0]);
                _folderTree.PauseLayout = false;
            }

            // 全量重建使备注立即生效（增量文本刷新在 AntdUI 中不触发重绘，需先展开节点才可见）；
            // 重建前保留展开状态，避免目录被全部折叠
            _preservedExpandedPaths = CollectExpandedFolderPaths();
            try
            {
                RefreshFolderTree(rebuildStructure: true);
            }
            finally
            {
                _preservedExpandedPaths = null;
            }
        }

        // 收集当前文件夹树所有已展开节点的路径，供重建后恢复
        private HashSet<string> CollectExpandedFolderPaths()
        {
            var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_folderTree == null || _folderTree.Items.Count == 0) return expandedPaths;

            void Walk(AntdUI.TreeItem node)
            {
                if (node.Expand)
                {
                    string path = GetFolderTreePath(node);
                    if (!string.IsNullOrEmpty(path))
                        expandedPaths.Add(path);
                }
                if (node.Sub != null)
                {
                    foreach (AntdUI.TreeItem child in node.Sub)
                        Walk(child);
                }
            }

            Walk(_folderTree.Items[0]);
            return expandedPaths;
        }

        private NodeData FindNodeDataByPath(string path)
        {
            if (_fileTreeRootNodes == null || string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split('/');
            foreach (var root in _fileTreeRootNodes)
            {
                var result = FindNodeDataInTree(root, parts, 0);
                if (result != null) return result;
            }
            return null;
        }

        private static NodeData FindNodeDataInTree(NodeData node, string[] parts, int index)
        {
            if (index >= parts.Length || !string.Equals(node.Name, parts[index], StringComparison.OrdinalIgnoreCase))
                return null;
            if (index == parts.Length - 1) return node;
            foreach (var child in node.Children)
            {
                var result = FindNodeDataInTree(child, parts, index + 1);
                if (result != null) return result;
            }
            return null;
        }

        private void ShowHistoryModal()
        {
            // TODO: 实现历史记录模态框
            MessageBox.Show("历史记录功能开发中...", "提示");
        }

        /// <summary>
        /// 设置窗保存本地化配置后统一调用的公开刷新入口（含忙状态守卫，加载中跳过）。
        /// </summary>
        public void RefreshLocalization()
        {
            if (_isLoadingFile || string.IsNullOrEmpty(_archiveFilePath)) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                ApplyLanguagePacks(SettingsManager.ActivePackKeys);
                RefreshFolderAnnotations();
                DbrKeyMapping.Rebuild();
                RefreshDbrGrid();
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void ApplyLanguagePacks(List<int> activeKeys)
        {
            // 复用 MOD合并窗的公共实现，保证两个窗口行为完全一致：
            // 字典为空或该 tag 未命中时，LookupLocalizedTag 会回退返回原始 tag
            _currentLanguageDict = LocalizationHelper.MergeLanguagePacks(activeKeys);
            _isLanguagePackActive = _currentLanguageDict != null;
            // 语言包变化 → 缓存的"本地化名/描述"已过时，整体作废（重建由下次刷新触发）
            _fileDisplayTextCache.Clear();
            _dbrTagCache = _isLanguagePackActive
                ? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                : null;

            if (_fileListAllRows != null)
            {
                foreach (var row in _fileListAllRows)
                {
                    string tag = row.AnnotationTag;
                    row.Notes = string.IsNullOrEmpty(tag)
                        ? ""
                        : LocalizationHelper.LookupLocalizedTag(tag, _currentLanguageDict);
                }
                // 强制重建视图行：文件列表为 VirtualMode 表格，仅就地改行对象属性可能不重绘；
                // 置空视图列表会让 RebindFileListView 新建列表并重绑，确保表格重新取数
                if (!_fileListSearchView) _fileListViewRows = null;
                ApplyFileListFilter();
            }
            else
            {
                // 静默加载：此处处于 RefreshOpenWindows 的窗口枚举期间，弹 Spin 会向
                // Application.OpenForms 插入顶层窗体导致枚举崩溃（设置保存后刷新场景）
                if (_flatViewMode) RefreshFileListAll(silent: true);
                else RefreshFileList(silent: true);
            }
            RefreshFileAnnotations();
        }

        private void RestoreActiveLanguagePacks()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                ApplyLanguagePacks(SettingsManager.ActivePackKeys);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private string LookupLanguageNote(string fullPath, NodeData nd)
        {
            var fields = GetDbrTagFields(fullPath, nd);
            if (fields == null) return "";

            foreach (var tagField in KnownTagFields)
            {
                if (fields.TryGetValue(tagField, out var tagValue) &&
                    !string.IsNullOrEmpty(tagValue))
                {
                    // 无本地化文本时回退显示原始 tag（与 MOD合并窗一致）
                    return LocalizationHelper.LookupLocalizedTag(tagValue, _currentLanguageDict);
                }
            }
            return "";
        }

        private Dictionary<string, string> GetDbrTagFields(string fullPath, NodeData nd)
        {
            if (_dbrTagCache == null)
                _dbrTagCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            lock (_dbrTagCacheLock)
            {
                if (_dbrTagCache.TryGetValue(fullPath, out var cached))
                    return cached;
            }

            string ext = Path.GetExtension(fullPath);
            if (!string.Equals(ext, ".dbr", StringComparison.OrdinalIgnoreCase) ||
                !nd.RecordIndex.HasValue)
                return null;

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string all = null;
                if (_isArz)
                {
                    var record = _arzReader.GetRecord(nd.RecordIndex.Value);
                    using var ms = new MemoryStream();
                    record.SaveToStream(ms);
                    ms.Position = 0;
                    using var sr = new StreamReader(ms, Utf8NoBom);
                    all = sr.ReadToEnd();
                }
                else
                {
                    var entry = _arcFile.toc[nd.RecordIndex.Value];
                    using var ms = new MemoryStream();
                    _arcFile.UnpackToStream(entry, ms);
                    all = TemplateHelper.DecodeText(ms.ToArray());
                }

                if (all != null)
                {
                    var parsed = DbrHelper.ParseDbrFields(all, includeDescription: false, includeTags: true, tagFields: KnownTagFields);
                    if (parsed.TagsByField != null)
                        fields = parsed.TagsByField;
                }
            }
            catch
            {
                return null;
            }

            lock (_dbrTagCacheLock) _dbrTagCache[fullPath] = fields;
            return fields;
        }

        private void RefreshFileAnnotations()
        {
            _fileTree.PauseLayout = true;
            foreach (var kv in _fileNodeMap)
            {
                var data = kv.Value.Tag as NodeData;
                if (data != null && !data.IsDirectory)
                {
                    string suffix = GetFileAnnotationSuffix(kv.Key);
                    if (data.FileTime.HasValue)
                        kv.Value.Text = $"{data.Name}  {data.FileTime.Value:yyyy-MM-dd HH:mm:ss}{suffix}";
                    else
                        kv.Value.Text = $"{data.Name}{suffix}";
                }
            }
            _fileTree.PauseLayout = false;
            RefreshFolderTree(rebuildStructure: false);
        }

        private void RefreshFileTreeNodeColor(string relPath)
        {
            // Color the file node and all parent directories
            string[] parts = relPath.Split('/');
            string cur = "";
            for (int i = 0; i < parts.Length; i++)
            {
                cur = i == 0 ? parts[i] : cur + "/" + parts[i];
                if (_fileNodeMap.TryGetValue(cur, out var node))
                {
                    if (_deletedFiles.Contains(cur))
                        node.Fore = ThemeManager.Deleted;
                    else if (_addedFiles.Contains(cur))
                        node.Fore = ThemeManager.AddedBlue;
                    else
                    {
                        bool isModified = _modifiedFiles.Any(m => m.Equals(cur, StringComparison.OrdinalIgnoreCase)
                            || m.StartsWith(cur + "/", StringComparison.OrdinalIgnoreCase));
                        node.Fore = isModified ? ThemeManager.Modified : ThemeManager.Text;
                    }
                }
            }
            RefreshFolderTreeNodeColor(relPath);
        }

        private void RefreshOutlineTreeColors()
        {
            _modifiedDbrFields.TryGetValue(_currentFileRelPath, out var modFields);
            RefreshOutlineNodeColorRecurse(_outlineTree.Items, modFields);
        }

        private void RefreshOutlineNodeColorRecurse(AntdUI.TreeItemCollection nodes, HashSet<string> modFields)
        {
            foreach (AntdUI.TreeItem node in nodes)
            {
                var tag = node.Tag as string;
                if (tag == "__all__")
                {
                    bool anyMod = modFields != null && _currentVariables.Any(v =>
                        v != null && modFields.Contains(v.Name));
                    node.Fore = anyMod ? ThemeManager.Modified : ThemeManager.Text;
                }
                else if (tag == "__with_value__")
                {
                    bool anyMod = modFields != null && _currentVariables.Any(v =>
                        v != null && !string.IsNullOrEmpty(v.Value) && modFields.Contains(v.Name));
                    node.Fore = anyMod ? ThemeManager.Modified : ThemeManager.Text;
                }
                else if (tag != null && !tag.StartsWith("__"))
                {
                    bool groupMod = modFields != null && _currentVariables.Any(v =>
                        v != null &&
                        (string.Equals(v.GroupName, tag, StringComparison.OrdinalIgnoreCase) ||
                         (v.GroupName != null && v.GroupName.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase))) &&
                        modFields.Contains(v.Name));
                    node.Fore = groupMod ? ThemeManager.Modified : ThemeManager.Text;
                }
                if (node.Sub != null && node.Sub.Count > 0)
                    RefreshOutlineNodeColorRecurse(node.Sub, modFields);
            }
        }

        private void OnModifiedListClick(object sender, EventArgs e)
        {
            if (_modifiedList.SelectedIndex < 1) return;
            var row = _modifiedList.GetRow(_modifiedList.SelectedIndex - 1)?.record as ModifiedFileRow;
            if (row == null) return;
            string relPath = row.FilePath;
            if (string.IsNullOrEmpty(relPath)) return;

            if (_deletedFiles.Contains(relPath)) return; // cannot open deleted file
            SelectFileInTree(relPath);
        }

        private bool FindFileInTree(string relPath)
        {
            var parts = relPath.Split('/');
            AntdUI.TreeItemCollection nodes = _fileTree.Items;
            if (nodes.Count == 1 && nodes[0].Tag == null)
                nodes = nodes[0].Sub;
            for (int i = 0; i < parts.Length; i++)
            {
                AntdUI.TreeItem match = null;
                foreach (AntdUI.TreeItem n in nodes)
                {
                    var nd = n.Tag as NodeData;
                    if (nd != null && string.Equals(nd.Name, parts[i], StringComparison.OrdinalIgnoreCase))
                    {
                        match = n;
                        if (!nd.IsDirectory && i == parts.Length - 1) break;
                        if (nd.IsDirectory)
                        {
                            if (n.Sub.Count == 1 &&
                                n.Sub[0].Text == LanguageManager.Instance.GetText("Common.Loading", "加载中..."))
                                PopulateChildren(n);
                            nodes = n.Sub;
                        }
                        break;
                    }
                }
                if (match == null) return false;
            }
            return true;
        }

        private void SelectFileInTree(string relPath)
        {
            var parts = relPath.Split('/');
            AntdUI.TreeItemCollection nodes = _fileTree.Items;
            if (nodes.Count == 1 && nodes[0].Tag == null)
                nodes = nodes[0].Sub;
            AntdUI.TreeItem match = null;
            for (int i = 0; i < parts.Length; i++)
            {
                match = null;
                foreach (AntdUI.TreeItem n in nodes)
                {
                    var nd = n.Tag as NodeData;
                    if (nd != null && string.Equals(nd.Name, parts[i], StringComparison.OrdinalIgnoreCase))
                    {
                        match = n;
                        if (!nd.IsDirectory && i == parts.Length - 1) break;
                        if (nd.IsDirectory && n.Sub.Count == 1 &&
                            n.Sub[0].Text == LanguageManager.Instance.GetText("Common.Loading", "加载中..."))
                            PopulateChildren(n);
                        nodes = n.Sub;
                        break;
                    }
                }
                if (match == null) return;
            }
            if (match != null)
            {
                _fileTree.SelectItem = match;
                var data = match.Tag as NodeData;
                if (data != null && !data.IsDirectory)
                {
                    if (data.RecordIndex.HasValue)
                        OpenFileForEditing(data, relPath);
                    else
                        OpenNewBlankFile(relPath);

                    // Update folder tree selection to parent folder
                    string parentFolder = Path.GetDirectoryName(relPath)?.Replace('\\', '/');
                    _currentFolderPath = parentFolder ?? "";
                    SelectFolderNode(_currentFolderPath);
                }
            }
        }

        
        private async void OnUndoAllClick(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                LanguageManager.Instance.GetText("Editor.ConfirmUndoAll", "确定要撤销所有修改吗？"),
                LanguageManager.Instance.GetText("Common.MessageBoxTitle.Question", "确认"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes) return;

            var addedCopy = _addedFiles.ToList();
            var modified = _session.GetModifiedRelativePaths();
            var deletedCopy = _deletedFiles.ToList();
            if (addedCopy.Count == 0 && modified.Length == 0 && deletedCopy.Count == 0) return;
            string savedCurrentFileRelPath = _currentFileRelPath;

            var status = await RunWithCancelAsync(LanguageManager.Instance.GetText("Editor.UndoAllLabel", "撤销全部修改"), (progress, ct) =>
            {
                foreach (string relPath in addedCopy)
                {
                    ct.ThrowIfCancellationRequested();
                    string tempFile = _session.GetTempFilePath(relPath);
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                }
                foreach (string relPath in modified)
                {
                    ct.ThrowIfCancellationRequested();
                    string tempFile = _session.GetTempFilePath(relPath);
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                }
                foreach (string relPath in deletedCopy)
                {
                    ct.ThrowIfCancellationRequested();
                    string tempFile = _session.GetTempFilePath(relPath);
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                }
                return true;
            });

            if (status == OperationResult.Cancelled) return;

            // Phase 4: UI 线程 — 文件树操作
            _fileTree.PauseLayout = true;
            try
            {
                var cleanupPaths = new HashSet<string>();
                foreach (string relPath in addedCopy)
                {
                    _modifiedFiles.Remove(relPath);
                    _modifiedDbrFields.Remove(relPath);
                    _addedFiles.Remove(relPath);
                    if (relPath == _currentFileRelPath)
                        ShowPlaceholder();
                    RemoveFileNodeFromTree(relPath);
                    cleanupPaths.Add(GetParentPath(relPath));
                }
                foreach (string path in cleanupPaths
                    .Where(p => !string.IsNullOrEmpty(p))
                    .OrderByDescending(p => p.Count(c => c == '/')))
                {
                    CleanupEmptyFolderChain(path);
                }

                foreach (string relPath in modified)
                    UnmarkFileModified(relPath);

                foreach (string relPath in deletedCopy)
                {
                    InsertFileNodeToTree(relPath);
                    // 撤销删除必须同时复位节点状态：否则状态仍是 "d"，文件树/文件列表会继续按"已删除"着色
                    //（这正是"撤销全部修改后文件列表颜色不恢复"的根因——单条撤销走 UnmarkFileModified → SetNodeState("") 所以正常）
                    SetNodeState(relPath, "");
                }

                RecalculateAllNodeDataCounts();

                BuildFileNodeMap();
                _deletedFiles.Clear();
                _deletedFileRecordIndices.Clear();
                _statusChangeLog.Clear();
            }
            finally
            {
                _fileTree.PauseLayout = false;
            }

            if (savedCurrentFileRelPath != null)
            {
                _editorDirty = false;
                SelectFileInTree(savedCurrentFileRelPath);
            }

            RefreshAllAfterChange();

            ShowFileList();   // 撤销后回到文件列表选项卡（不再停留在编辑器）

            // RefreshFileList() 只重建"当前文件夹"的行（CollectFolderFileRows），其它文件夹的行不会被重绘；
            // 这里对受影响路径逐个修补缓存行并统一重绑，保证"颜色复位 + 已隐藏项恢复显示"立即生效。
            // 若某行不在缓存中（例如单条删除时节点已从树里移除），退回一次常规列表刷新兜底。
            var undonePaths = new HashSet<string>(addedCopy, StringComparer.OrdinalIgnoreCase);
            undonePaths.UnionWith(modified);
            undonePaths.UnionWith(deletedCopy);
            bool allPatched = undonePaths.Count > 0;
            foreach (string relPath in undonePaths)
                if (!RefreshFileListRowCore(relPath)) allPatched = false;
            if (allPatched)
                RebindFileListView(_fileListFilterTextBox?.Text?.Trim() ?? "");
            else
                RefreshFileList();

            LogOutput(LanguageManager.Instance.GetText("Editor.UndoAllDone", "撤销全部修改完成"), "INFO");
            _statusLabel.Text = LanguageManager.Instance.GetText("Editor.UndoProgressDone", "撤销完成");
        }

        // ======================== Search ========================



        private void ShowContextMenu(Control target, Point location)
        {
            var node = _contextMenuNode;
            var data = node?.Tag as NodeData;
            bool isBlank = node == null;
            bool isRoot = node != null && data == null;
            bool isDir = data?.IsDirectory == true || isRoot;
            bool isFile = data != null && !data.IsDirectory;

            var items = new List<AntdUI.IContextMenuStripItem>();

            if (isRoot)
                items.Add(_contextReload);

            if (isBlank || isDir)
            {
                if (items.Count > 0) items.Add(new AntdUI.ContextMenuStripItemDivider());
                items.Add(_contextNewFolder);
                items.Add(_contextNewFile);
                items.Add(_contextImport);
            }

            if (isDir || isFile)
            {
                if (items.Count > 0) items.Add(new AntdUI.ContextMenuStripItemDivider());
                items.Add(_contextExport);
                items.Add(_contextDelete);
            }

            if (items.Count > 0) items.Add(new AntdUI.ContextMenuStripItemDivider());
            var addFavItem = new AntdUI.ContextMenuStripItem(LanguageManager.Instance.GetText("Editor.AddToFavorites", "添加到收藏夹")) { Tag = "addFavorite" };
            items.Add(addFavItem);

            AntdUI.ContextMenuStrip.open(target, it =>
            {
                string tag = it.Tag as string;
                switch (tag)
                {
                    case "reload": OnContextReloadClick(it, EventArgs.Empty); break;
                    case "newFolder": OnContextNewFolder(it, EventArgs.Empty); break;
                    case "newFile": OnContextNewFile(it, EventArgs.Empty); break;
                    case "import": OnContextImport(it, EventArgs.Empty); break;
                    case "export": OnContextExport(it, EventArgs.Empty); break;
                    case "delete": OnContextDelete(it, EventArgs.Empty); break;
                    case "addFavorite": OnContextAddToFavorites(it, EventArgs.Empty); break;
                }
            }, items.ToArray());
        }

        private void OnContextNewFolder(object sender, EventArgs e)
        {
            string parentPath = !string.IsNullOrEmpty(_contextMenuPath)
                ? _contextMenuPath
                : (_contextMenuNode != null ? GetNodePath(_contextMenuNode) : "");

            var txtFolderName = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Height = 42,
                PrefixText = LanguageManager.Instance.GetText("Editor.NewFolderPrompt", "请输入文件夹名称："),
                AllowClear = true
            };

            var grid = new AntdUI.GridPanel
            {
                Span = "52:100%",
                Gap = 8,
                BackColor = Color.Transparent,
                Width = 480,
                Height = 62
            };
            grid.SetIndex(txtFolderName, 0);
            grid.Controls.Add(txtFolderName);

            AntdUI.Modal.Config config = null;
            config = new AntdUI.Modal.Config(this,
                LanguageManager.Instance.GetText("Editor.NewFolderTitle", "新建文件夹"),
                grid)
            {
                Draggable = true
            };
            config.SetWidth(520);
            config.SetOk(LanguageManager.Instance.GetText("Common.OK", "确定"));
            config.SetCancel(LanguageManager.Instance.GetText("Common.Cancel", "取消"));
            config.OnOk = cfg =>
            {
                string folderName = txtFolderName.Text?.Trim();
                if (string.IsNullOrWhiteSpace(folderName)) return false;

                if (folderName.Contains('/') || folderName.Contains('\\'))
                {
                    MessageBox.Show(
                        LanguageManager.Instance.GetText("Editor.InvalidFolderName", "文件夹名称不能包含 / 或 \\"),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                string relPath = string.IsNullOrEmpty(parentPath) ? folderName : parentPath + "/" + folderName;

                if (_fileNodeMap.ContainsKey(relPath))
                {
                    MessageBox.Show(
                        string.Format(LanguageManager.Instance.GetText("Editor.FolderAlreadyExists", "文件夹 {0} 已存在"), relPath),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                _session.GetTempFilePath(relPath + "/.GDPackEditor_placeholder");
                InsertFolderNodeToTree(folderName, parentPath);
                LogFileChange(relPath, LogService.VerbAdd);   // 文件级变更记录：新增目录
                return true;
            };
            AntdUI.Modal.open(config);
        }

        private void OnContextNewFile(object sender, EventArgs e)
        {
            string parentPath = !string.IsNullOrEmpty(_contextMenuPath)
                ? _contextMenuPath
                : (_contextMenuNode != null ? GetNodePath(_contextMenuNode) : "");
            CreateNewFile(parentPath);
        }

        private async void OnContextImport(object sender, EventArgs e)
        {
            var node = _contextMenuNode;
            var data = node?.Tag as NodeData;
            string defaultFolder = !string.IsNullOrEmpty(_contextMenuPath)
                ? _contextMenuPath
                : (data != null && data.IsDirectory) ? GetNodePath(node) : "";

            var existing = new HashSet<string>(_allKnownPaths, StringComparer.OrdinalIgnoreCase);
            var allFolders = _allFolderPaths;

            var picker = new ImportDialog(existing, allFolders, defaultFolder, _archiveFilePath);
            picker.Width = 900;
            picker.Height = 650;

            List<(string sourcePath, string relPath)> selectedFiles = null;
            AntdUI.Modal.Config config = null;
            config = new AntdUI.Modal.Config(this,
                LanguageManager.Instance.GetText("Editor.ImportFileTitle", "导入文件"),
                picker)
            {
                Resizable = true,
                Draggable = true
            };
            config.SetWidth(950);
            config.SetOk(LanguageManager.Instance.GetText("Editor.ImportStart", "确定"));
            config.SetCancel(LanguageManager.Instance.GetText("Common.Cancel", "取消"));
            config.OnOk = cfg =>
            {
                if (!picker.ConfirmStart()) return false;
                selectedFiles = picker.Files;
                return true;
            };
            AntdUI.Modal.open(config);

            if (selectedFiles == null || selectedFiles.Count == 0) return;

            int total = selectedFiles.Count;
            var importedRelPaths = new List<string>();
            var status = await RunWithCancelAsync(LanguageManager.Instance.GetText("Editor.ImportLabel", "导入"), (progress, ct) =>
            {
                for (int i = 0; i < selectedFiles.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (sourcePath, relPath) = selectedFiles[i];
                    if (!CheckImportNameUsable(relPath, progress)) continue;
                    string destPath = _session.GetTempFilePath(relPath);
                    string dir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    WriteImportedFileToTemp(sourcePath, relPath, destPath, progress);
                    importedRelPaths.Add(relPath);
                    _importedPaths.Add(relPath);
                    progress.ReportLog(string.Format(
                        LanguageManager.Instance.GetText("Editor.ImportProgress", "导入: ({0}/{1}) {2}"),
                        i + 1, total, relPath));
                }
                return true;
            });

            if (status == OperationResult.Cancelled) return;

            foreach (string relPath in importedRelPaths)
            {
                bool isNewFile = !_allKnownPaths.Contains(relPath);
                if (isNewFile)
                    _addedFiles.Add(relPath);
                _modifiedFiles.Add(relPath);
                _deletedFiles.Remove(relPath);
                RecordStatusChange(relPath);

                // 文件级变更记录：MOD 作者必须完整看到"哪些文件进了包"，故**一律记录**。
                // 「详细日志」开关只用于过程性进度行（解包/打包的 (N/M) 行），不得吞掉变更记录——
                // 否则变更报告与汇总会漏掉导入的文件（曾因此出现"导入了文件却没记录"）。
                LogFileChange(relPath, isNewFile ? LogService.VerbAdd : LogService.VerbMod);
            }
            if (importedRelPaths.Count > 0)
            {
                var parentNodesToSort = new HashSet<AntdUI.TreeItem>();
                _fileTree.PauseLayout = true;
                try
                {
                    foreach (string relPath in importedRelPaths)
                    {
                        var parent = InsertFileNodeWithFolders(relPath);
                        if (parent != null)
                            parentNodesToSort.Add(parent);
                    }
                }
                finally
                {
                    BuildFileNodeMap();
                    _fileTree.PauseLayout = false;
                }
                foreach (string relPath in importedRelPaths)
                {
                    if (_addedFiles.Contains(relPath))
                        SetNodeState(relPath, "a");
                    else
                        SetNodeState(relPath, "m");
                }
                foreach (var pn in parentNodesToSort)
                    SortNodeChildren(pn.Sub);
                // 收尾：树节点色 + 文件夹链路色 + 文件列表**增量插入行** + 右侧列表。
                // 原实现调 RefreshAllAfterChange()：平铺模式会全量重扫 6 万文件重建列表并弹 Spin。
                foreach (string relPath in importedRelPaths)
                {
                    RefreshFileTreeNodeColor(relPath);
                    RefreshFolderTreeNodeColor(relPath);
                    InsertFileListRow(relPath);
                }
                UpdateModifiedList();
            }
            _statusLabel.Text = string.Format(
                LanguageManager.Instance.GetText("Editor.ImportDone", "导入完成: {0} 个文件"), importedRelPaths.Count);
        }

        private async void OnContextExport(object sender, EventArgs e)
        {
            var node = _contextMenuNode;
            var data = node?.Tag as NodeData;

            string relPath = !string.IsNullOrEmpty(_contextMenuPath)
                ? _contextMenuPath
                : (data != null ? GetNodePath(node) : "");

            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = LanguageManager.Instance.GetText("Editor.ExportFolderTitle", "选择导出目录");
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string baseDir = dlg.SelectedPath;

                var filesToExport = new List<(string path, NodeData nd)>();
                if (data == null)
                {
                    // 根节点或未识别节点：导出全部文件
                    foreach (var kv in _fileNodeMap)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;
                        var cd = kv.Value.Tag as NodeData;
                        if (cd != null && !cd.IsDirectory)
                            filesToExport.Add((kv.Key, cd));
                    }
                }
                else if (!data.IsDirectory)
                {
                    filesToExport.Add((relPath, data));
                }
                else
                {
                    _fileTree.PauseLayout = true;
                    EnsureChildrenPopulated(node);
                    BuildFileNodeMap();
                    _fileTree.PauseLayout = false;
                    foreach (var kv in _fileNodeMap)
                    {
                        if (kv.Key.StartsWith(relPath + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            var cd = kv.Value.Tag as NodeData;
                            if (cd != null && !cd.IsDirectory)
                                filesToExport.Add((kv.Key, cd));
                        }
                    }
                }

                if (filesToExport.Count == 0)
                {
                    LogOutput(
                        LanguageManager.Instance.GetText("Editor.ExportNoFiles", "导出：未找到可导出的文件"),
                        "WARN");
                    return;
                }

                int total = filesToExport.Count;
                LogOutput(string.Format(
                    LanguageManager.Instance.GetText("Editor.ExportStart", "开始导出 {0} 个文件到: {1}"),
                    total, baseDir));

                try
                {
                    await Task.Run(() =>
                    {
                        for (int i = 0; i < total; i++)
                        {
                            var (fp, nd) = filesToExport[i];
                            string dest = Path.Combine(baseDir, fp);
                            ExportSingleFile(fp, nd, dest);
                            int pct = (int)((i + 1) * 100.0 / total);
                            LogOutput(string.Format(
                                LanguageManager.Instance.GetText("Editor.ExportingFile", "正在导出: {0} ({1}/{2})"),
                                fp, i + 1, total));
                        }
                    });
                    LogOutput(string.Format(
                        LanguageManager.Instance.GetText("Editor.ExportComplete", "导出完成: {0} 个文件 -> {1}"),
                        total, baseDir));
                }
                catch (Exception ex)
                {
                    LogOutput(string.Format(
                        LanguageManager.Instance.GetText("Editor.ExportError", "导出失败: {0}"),
                        ex.Message), "ERROR");
                }
            }
        }

        private async void OnContextDelete(object sender, EventArgs e)
        {
            var node = _contextMenuNode;
            var data = node?.Tag as NodeData;
            if (data == null) return;

            string relPath = !string.IsNullOrEmpty(_contextMenuPath)
                ? _contextMenuPath
                : GetNodePath(node);

            var confirm = MessageBox.Show(
                string.Format(LanguageManager.Instance.GetText("Editor.ConfirmDelete", "确定要删除 {0} 吗？"), relPath),
                LanguageManager.Instance.GetText("Common.MessageBoxTitle.Question", "确认"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            if (data.IsDirectory)
            {
                _fileTree.PauseLayout = true;
                EnsureChildrenPopulated(node);
                BuildFileNodeMap();

                var fileDescendants = new List<(string, NodeData)>();
                foreach (var kv in _fileNodeMap)
                {
                    if (kv.Key.StartsWith(relPath + "/", StringComparison.OrdinalIgnoreCase) && kv.Key != relPath)
                    {
                        var cd = kv.Value.Tag as NodeData;
                        if (cd != null && !cd.IsDirectory)
                            fileDescendants.Add((kv.Key, cd));
                    }
                }

                if (fileDescendants.Count > 3)
                {
                    _fileTree.PauseLayout = false;
                    var dialog = new SaveProgressDialog(
                        LanguageManager.Instance.GetText("Editor.DeleteProgressTitle", "Delete Progress"));
                    dialog.ProgressBar.Value = 0;
                    var progress = new GuiProgressCallback(dialog.ProgressBar, dialog.LogView);
                    progress.ShowDetailLog = true;
                    dialog.Show(this);
                    dialog.FormClosed += (s2, e2) => { try { progress.Dispose(); } catch { } };

                    DateTime deleteStartTime = DateTime.Now;
                    progress.ReportLog(LanguageManager.Instance.GetText("Editor.DeleteProgressStart", "开始删除..."));

                    int done = 0;
                    foreach (var (cRelPath, cData) in fileDescendants)
                    {
                        DeleteFile(cRelPath, cData, suppressUiUpdates: true);
                        done++;
                        dialog.ProgressBar.Value = (float)done / fileDescendants.Count;
                        await Task.Delay(1);
                    }

                    var elapsed = DateTime.Now - deleteStartTime;
                    dialog.LogView.Enqueue($"[{DateTime.Now:HH:mm:ss}] [INFO] " +
                        LanguageManager.Instance.GetText("Editor.DeleteProgressDone", "删除完成"));
                    dialog.LogView.Enqueue($"[{DateTime.Now:HH:mm:ss}] [INFO] " +
                        string.Format(LanguageManager.Instance.GetText("Editor.DeleteProgressElapsed", "耗时: {0}"),
                        FormatDuration(elapsed)));
                    // 强制刷新（绕过节流）：保证收尾行可见且滚动到底
                    dialog.LogView.FlushNow(force: true);

                    dialog.OperationDone(() =>
                    {
                        _fileTree.PauseLayout = true;
                        foreach (var (cRelPath, _) in fileDescendants)
                            RemoveFileNodeFromTree(cRelPath);

                        if (fileDescendants.Any(f => f.Item1 == _currentFileRelPath))
                            ShowPlaceholder();

                        var parentNode = node.ParentItem;
                        node.Remove();
                        if (parentNode != null)
                        {
                            var parentData = parentNode.Tag as NodeData;
                            if (parentData != null)
                            {
                                parentData.Children.Remove(data);
                    parentData.TotalItemCount = parentData.Children.Sum(c => c.IsDirectory ? c.TotalItemCount : 1);
                                parentNode.Text = $"{parentData.Name} [{parentData.TotalItemCount}]{GetAnnotationSuffix(parentData.Name)}";
                            }
                        }
                        BuildFileNodeMap();
                        _fileTree.PauseLayout = false;
                        UpdateModifiedList();
                        RefreshFileTreeColors();
                        
                    }, LanguageManager.Instance.GetText("Common.Close", "关闭"));
                }
                else
                {
                    foreach (var (cRelPath, cData) in fileDescendants)
                        DeleteFile(cRelPath, cData, suppressUiUpdates: true);

                    foreach (var (cRelPath, _) in fileDescendants)
                    {
                        RemoveFileNodeFromTree(cRelPath);
                        if (cRelPath == _currentFileRelPath)
                            ShowPlaceholder();
                    }

                    var parentNode = node.ParentItem;
                    node.Remove();
                    if (parentNode != null)
                    {
                        var parentData = parentNode.Tag as NodeData;
                        if (parentData != null)
                        {
                            parentData.Children.Remove(data);
                            parentData.TotalItemCount = parentData.Children.Sum(c => c.IsDirectory ? c.TotalItemCount + 1 : 1);
                            parentNode.Text = $"{parentData.Name} [{parentData.TotalItemCount}]{GetAnnotationSuffix(parentData.Name)}";
                        }
                    }

                    BuildFileNodeMap();
                    _fileTree.PauseLayout = false;
                }
            }
            else
            {
                DeleteFile(relPath, data);
            }

            RefreshAllAfterChange();
        }

        private void InsertFolderNodeToTree(string folderName, string parentPath)
        {
            AntdUI.TreeItem parentNode = null;
            NodeData parentData = null;

            if (string.IsNullOrEmpty(parentPath))
                _fileNodeMap.TryGetValue("", out parentNode);
            else
                _fileNodeMap.TryGetValue(parentPath, out parentNode);

            if (parentNode == null && !string.IsNullOrEmpty(parentPath))
                parentNode = FindOrCreateLazyNode(parentPath);

            if (parentNode != null)
                parentData = parentNode.Tag as NodeData;

            if (parentData != null)
            {
                var dirData = new NodeData { Name = folderName, IsDirectory = true, Children = new List<NodeData>() };
                parentData.Children.Add(dirData);
                parentData.TotalItemCount++;

                if (parentNode.Sub.Count == 1 && parentNode.Sub[0].Tag == null)
                    PopulateChildren(parentNode);
                else
                {
                    string nodeParentPath = GetNodePath(parentNode);
                    var newNode = CreateTreeNode(dirData, nodeParentPath);
                    parentNode.Sub.Add(newNode);
                    SortNodeChildren(parentNode.Sub);
                }

                parentNode.Text = $"{parentData.Name} [{parentData.TotalItemCount}]{GetAnnotationSuffix(parentData.Name)}";
            }
            else
            {
                var dirData = new NodeData { Name = folderName, IsDirectory = true, Children = new List<NodeData>() };
                var newNode = CreateTreeNode(dirData, "");
                if (_fileTree.Items.Count > 0 && _fileTree.Items[0].Tag == null)
                {
                    _fileTree.Items[0].Sub.Add(newNode);
                    SortNodeChildren(_fileTree.Items[0].Sub);
                }
                else
                {
                    _fileTree.Items.Add(newNode);
                    SortNodeChildren(_fileTree.Items);
                }
            }

            RefreshAllAfterChange(refreshFileList: false, updateModifiedList: false);
        }

        private void SortNodeChildren(AntdUI.TreeItemCollection nodes)
        {
            var sorted = nodes.Cast<AntdUI.TreeItem>()
                .OrderBy(n => (n.Tag as NodeData)?.IsDirectory == false)
                .ThenBy(n => (n.Tag as NodeData)?.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            nodes.Clear();
            foreach (var n in sorted)
                nodes.Add(n);
        }

        private void ExportSingleFile(string relPath, NodeData data, string destPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 导出遵循"包内时间戳"：写盘后按包内条目原时间恢复文件修改时间
                //（与主界面解包 GDPackEditorAPI / FileWriteHelper 口径一致）
                DateTime packTime = default;

                if (_isArz && data.RecordIndex.HasValue)
                {
                    var record = _arzReader.GetRecord(data.RecordIndex.Value);
                    packTime = record.rdFileTime;
                    Encoding unpackTarget = EncodingService.UnpackTarget;
                    using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                    {
                        if (unpackTarget == null)
                            record.SaveToStreamPreservingEncodings(fs);   // 原样档：按包内原始编码落盘
                        else
                            record.SaveToStream(fs, unpackTarget);        // 统一档：按目标编码写出
                    }
                }
                else if (!_isArz && data.RecordIndex.HasValue)
                {
                    var entry = _arcFile.toc[data.RecordIndex.Value];
                    packTime = entry.FileTime;
                    _arcFile.UnpackToFile(entry, destPath);
                }

                if (packTime != default)
                {
                    try { File.SetLastWriteTime(destPath, packTime); } catch { }
                }
            }
            catch (Exception ex)
            {
                Program.Log.Warn(LanguageManager.Instance.GetText("Editor.ExportFileFailed",
                    "导出文件失败: {0} - {1}"), relPath, ex.Message);
            }
        }

        private void ExportDirectory(string relPath, string baseDir)
        {
            foreach (var kv in _fileNodeMap)
            {
                if (kv.Key.StartsWith(relPath + "/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kv.Key, relPath, StringComparison.OrdinalIgnoreCase))
                {
                    var childData = kv.Value.Tag as NodeData;
                    if (childData == null || childData.IsDirectory) continue;
                    string destPath = Path.Combine(baseDir, kv.Key);
                    ExportSingleFile(kv.Key, childData, destPath);
                }
            }
        }

        private void EnsureChildrenPopulated(AntdUI.TreeItem node)
        {
            if (node == null) return;
            var data = node.Tag as NodeData;
            if (data == null || !data.IsDirectory) return;
            if (node.Sub.Count == 1 && node.Sub[0].Tag == null)
                PopulateChildren(node);
            foreach (AntdUI.TreeItem child in node.Sub)
            {
                var cd = child.Tag as NodeData;
                if (cd != null && cd.IsDirectory)
                    EnsureChildrenPopulated(child);
            }
        }

        private void CreateNewFile(string parentRelPath)
        {
            if (parentRelPath == null) parentRelPath = "";

            string title = LanguageManager.Instance.GetText("Editor.NewFileTitle", "新建文件");

            var txtFileName = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Height = 42,
                PrefixText = LanguageManager.Instance.GetText("Editor.NewFileInputLabel", "文件名") + "：",
                PlaceholderText = LanguageManager.Instance.GetText("Editor.NewFilePlaceholder", "需要输入扩展名"),
                AllowClear = true
            };

            AntdUI.Select templateCombo = null;
            List<string> allTemplateKeys = _templates?.Keys?.OrderBy(k => k).ToList();

            var grid = new AntdUI.GridPanel
            {
                Span = "52:100%",
                Gap = 8,
                BackColor = Color.Transparent,
                Width = 520,
                Height = allTemplateKeys != null && allTemplateKeys.Count > 0 ? 114 : 62
            };
            grid.SetIndex(txtFileName, 0);
            grid.Controls.Add(txtFileName);

            if (allTemplateKeys != null && allTemplateKeys.Count > 0)
            {
                templateCombo = new AntdUI.Select
                {
                    Dock = DockStyle.Fill,
                    Height = 42,
                    DropDownArrow = true,
                    MaxCount = 20,
                    PrefixText = LanguageManager.Instance.GetText("Editor.NewFileTemplate", "模板：（可选）"),
                    PlaceholderText = LanguageManager.Instance.GetText("Editor.NewFileTemplatePH", "输入关键字筛选模板")
                };
                templateCombo.Items.Add(new AntdUI.SelectItem("", ""));
                foreach (var key in allTemplateKeys)
                    templateCombo.Items.Add(new AntdUI.SelectItem(key, key));
                templateCombo.SelectedIndex = 0;

                grid.Span = "52:100%;52:100%";
                grid.SetIndex(templateCombo, 1);
                grid.Controls.Add(templateCombo);
            }

            AntdUI.Modal.Config config = null;
            config = new AntdUI.Modal.Config(this, title, grid)
            {
                Draggable = true
            };
            config.SetWidth(560);
            config.SetOk(LanguageManager.Instance.GetText("Common.OK", "确定"));
            config.SetCancel(LanguageManager.Instance.GetText("Common.Cancel", "取消"));
            config.OnOk = cfg =>
            {
                string fileName = txtFileName.Text?.Trim();
                if (string.IsNullOrWhiteSpace(fileName)) return false;

                int lastSlash = fileName.LastIndexOfAny(new[] { '/', '\\' });
                if (lastSlash >= 0)
                    fileName = fileName.Substring(lastSlash + 1);
                if (string.IsNullOrWhiteSpace(fileName)) return false;

                string fullRelPath = string.IsNullOrEmpty(parentRelPath)
                    ? fileName
                    : parentRelPath + "/" + fileName;

                if (_fileNodeMap.ContainsKey(fullRelPath))
                {
                    MessageBox.Show(
                        string.Format(LanguageManager.Instance.GetText("Editor.FileAlreadyExists", "文件 {0} 已存在"), fullRelPath),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                string tplKey = templateCombo?.SelectedValue as string;
                if (string.IsNullOrEmpty(tplKey)) tplKey = null;

                string tempFile = _session.GetTempFilePath(fullRelPath);
                if (!string.IsNullOrEmpty(tplKey))
                {
                    string content = $"templateName,{tplKey},\r\n";
                    File.WriteAllText(tempFile, content, EncodingService.TempWriteEncoding);
                }
                else
                {
                    File.WriteAllText(tempFile, "", EncodingService.TempWriteEncoding);
                }

                _addedFiles.Add(fullRelPath);
                InsertFileNodeToTree(fullRelPath);
                BuildFileNodeMap();
                SetNodeState(fullRelPath, "a");
                RecordStatusChange(fullRelPath);
                LogFileChange(fullRelPath, LogService.VerbAdd);
                RefreshFileTreeNodeColor(fullRelPath);
                UpdateModifiedList();

                if (_fileNodeMap.TryGetValue(fullRelPath, out var treeNode))
                    _fileTree.SelectItem = treeNode;
                OpenNewBlankFile(fullRelPath);
                return true;
            };
            AntdUI.Modal.open(config);
        }

        private void DeleteFile(string relPath, NodeData data, bool suppressUiUpdates = false)
        {
            // 如果是在新增阶段删除，直接清理
            if (_addedFiles.Contains(relPath))
            {
                string tempFile = _session.GetTempFilePath(relPath);
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                _addedFiles.Remove(relPath);
                SetNodeState(relPath, "");
                _modifiedFiles.Remove(relPath);
                SetNodeState(relPath, "");
                _modifiedDbrFields.Remove(relPath);
                RemoveStatusChange(relPath);
                // 净零操作：刚新增的文件又被删除，撤销变更记录里的"新增"，避免报告出现与结果不符的条目
                LogService.RemovePendingChange(relPath, LogService.MarkFile, LogService.VerbAdd);
                if (!suppressUiUpdates)
                {
                    RemoveFileNodeFromTree(relPath);
                    BuildFileNodeMap();
                    UpdateModifiedList();
                    if (relPath == _currentFileRelPath)
                        ShowPlaceholder();
                }
                return;
            }

            // 删除现有文件
            _deletedFiles.Add(relPath);
            _modifiedFiles.Remove(relPath);
            SetNodeState(relPath, "d");
            _modifiedDbrFields.Remove(relPath);
            _deletedFileRecordIndices[relPath] = data.RecordIndex;
            RecordStatusChange(relPath);
            LogFileChange(relPath, LogService.VerbDel);

            // 清理 temp 文件（如果有）
            string existingTemp = _session.GetTempFilePath(relPath);
            try { if (File.Exists(existingTemp)) File.Delete(existingTemp); } catch { }

            if (!suppressUiUpdates)
            {
                RemoveFileNodeFromTree(relPath);
                BuildFileNodeMap();
                UpdateModifiedList();
                if (relPath == _currentFileRelPath)
                    ShowPlaceholder();
            }
        }

        private string GetParentPath(string relPath)
        {
            int idx = relPath.LastIndexOf('/');
            return idx >= 0 ? relPath.Substring(0, idx) : "";
        }

        private string GetFileName(string relPath)
        {
            int idx = relPath.LastIndexOf('/');
            return idx >= 0 ? relPath.Substring(idx + 1) : relPath;
        }

        private bool InsertFileNodeToTree(string relPath)
        {
            string parentPath = GetParentPath(relPath);
            string fileName = GetFileName(relPath);

            if (!_fileNodeMap.TryGetValue(parentPath, out var parentNode))
            {
                parentNode = EnsureTreePath(parentPath);
                if (parentNode == null) return false;
            }

            var parentData = parentNode.Tag as NodeData;
            if (parentData == null) return false;

            // 确保 NodeData 中存在
            var fileData = parentData.Children.FirstOrDefault(c =>
                string.Equals(c.Name, fileName, StringComparison.OrdinalIgnoreCase));
            if (fileData == null)
            {
                fileData = new NodeData
                {
                    Name = fileName,
                    IsDirectory = false
                };
                // 如果撤销删除，恢复原始的 RecordIndex
                if (_deletedFileRecordIndices.TryGetValue(relPath, out var recIdx))
                    fileData.RecordIndex = recIdx;
                parentData.Children.Add(fileData);
                parentData.TotalItemCount++;
                // 更新父节点文字
                            parentNode.Text = $"{parentData.Name} [{parentData.TotalItemCount}]{GetAnnotationSuffix(parentData.Name)}";
            }

            // 确保父节点已展开
            if (parentNode.Sub.Count == 1 && parentNode.Sub[0].Tag == null)
                PopulateChildren(parentNode);

            // 避免重复插入
            bool exists = false;
            foreach (AntdUI.TreeItem child in parentNode.Sub)
            {
                var cd = child.Tag as NodeData;
                if (cd != null && string.Equals(cd.Name, fileName, StringComparison.OrdinalIgnoreCase))
                { exists = true; break; }
            }
            if (!exists)
            {
                string nodeParentPath = GetNodePath(parentNode);
                var newNode = CreateTreeNode(fileData, nodeParentPath);
                parentNode.Sub.Add(newNode);
                // 按名称排序子节点
                var sorted = parentNode.Sub.Cast<AntdUI.TreeItem>()
                    .OrderBy(n => (n.Tag as NodeData)?.IsDirectory == false)
                    .ThenBy(n => (n.Tag as NodeData)?.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                parentNode.Sub.Clear();
                foreach (var n in sorted)
                    parentNode.Sub.Add(n);
            }

            return true;
        }

        private AntdUI.TreeItem GetRootNode()
        {
            if (_fileTree.Items.Count > 0 && _fileTree.Items[0].Tag == null)
                return _fileTree.Items[0];
            return _fileTree.Items.Count > 0 ? _fileTree.Items[0] : null;
        }

        private AntdUI.TreeItem EnsureTreePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return GetRootNode();

            var segments = path.Split('/');
            AntdUI.TreeItem current = GetRootNode();
            if (current == null) return null;

            foreach (var segment in segments)
            {
                var found = current.Sub
                    .Cast<AntdUI.TreeItem>()
                    .FirstOrDefault(n =>
                    {
                        var nd = n.Tag as NodeData;
                        return nd != null && string.Equals(nd.Name, segment, StringComparison.OrdinalIgnoreCase);
                    });
                if (found == null)
                {
                    var parentData = current.Tag as NodeData;

                    // NodeData 中已有该 segment（占位符挡住了），展开后重新定位
                    if (parentData != null && current.Sub.Count == 1 && current.Sub[0].Tag == null)
                    {
                        var existsInNodeData = parentData.Children
                            .Any(c => string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
                        if (existsInNodeData)
                        {
                            PopulateChildren(current);
                            found = current.Sub
                                .Cast<AntdUI.TreeItem>()
                                .FirstOrDefault(n =>
                                {
                                    var nd = n.Tag as NodeData;
                                    return nd != null && string.Equals(nd.Name, segment, StringComparison.OrdinalIgnoreCase);
                                });
                            current = found;
                            continue;
                        }
                    }

                    var dirData = new NodeData { Name = segment, IsDirectory = true, Children = new List<NodeData>() };
                    parentData?.Children.Add(dirData);
                    if (parentData != null)
                    {
                        parentData.TotalItemCount++;
                        current.Text = $"{parentData.Name} [{parentData.TotalItemCount}]{GetAnnotationSuffix(parentData.Name)}";
                    }

                    if (current.Sub.Count == 1 && current.Sub[0].Tag == null)
                    {
                        PopulateChildren(current);
                    }
                    else
                    {
                        string nodeParentPath2 = GetNodePath(current);
                        found = CreateTreeNode(dirData, nodeParentPath2);
                        current.Sub.Add(found);
                        SortNodeChildren(current.Sub);
                    }

                    if (found == null)
                    {
                        found = current.Sub
                            .Cast<AntdUI.TreeItem>()
                            .FirstOrDefault(n =>
                            {
                                var nd = n.Tag as NodeData;
                                return nd != null && string.Equals(nd.Name, segment, StringComparison.OrdinalIgnoreCase);
                            });
                    }
                }
                current = found;
            }
            return current;
        }

        private AntdUI.TreeItem InsertFileNodeWithFolders(string relPath)
        {
            string parentPath = GetParentPath(relPath);
            string fileName = GetFileName(relPath);

            AntdUI.TreeItem parentNode = EnsureTreePath(parentPath);
            if (parentNode == null) return null;

            var parentData = parentNode.Tag as NodeData;

            // 根路径文件：parentNode 是虚拟根节点（Tag == null）
            bool isRootFile = parentData == null && parentNode == _fileTree.Items[0];

            // 展开占位符后再做重复检查
            if (parentNode.Sub.Count == 1 && parentNode.Sub[0].Tag == null)
                PopulateChildren(parentNode);

            // 重复检查**以 NodeData 层为准**：文件列表（树/平铺）都由 NodeData 重建，
            // 而 TreeItem 层可能残留"已删除但 UI 未清掉"或占位节点 —— 旧实现先判 TreeItem 层，
            // 一旦残留就会误判"已存在"而跳过插入，于是"导入→删除→再导入"时列表不显示（本次修复）。
            bool existsInData = isRootFile
                ? _fileTreeRootNodes.Any(c => !c.IsDirectory && string.Equals(c.Name, fileName, StringComparison.OrdinalIgnoreCase))
                : (parentData != null && parentData.Children.Any(c =>
                    !c.IsDirectory && string.Equals(c.Name, fileName, StringComparison.OrdinalIgnoreCase)));

            if (existsInData) return parentNode;

            // NodeData 里没有 → 清掉树 UI 里的同名残留节点（若有），避免插入后重复显示
            for (int i = parentNode.Sub.Count - 1; i >= 0; i--)
            {
                var nd = parentNode.Sub[i].Tag as NodeData;
                if (nd != null && !nd.IsDirectory && string.Equals(nd.Name, fileName, StringComparison.OrdinalIgnoreCase))
                    parentNode.Sub.RemoveAt(i);
            }

            if (isRootFile)
            {
                var fileData = new NodeData { Name = fileName, IsDirectory = false };
                _fileTreeRootNodes.Add(fileData);
                string newParentPath = "";
                var newNode = CreateTreeNode(fileData, newParentPath);
                parentNode.Sub.Add(newNode);
                return parentNode;
            }

            if (parentData == null) return null;

            var fileNodeData = new NodeData { Name = fileName, IsDirectory = false };
            parentData.Children.Add(fileNodeData);
            parentData.TotalItemCount++;
            parentNode.Text = $"{parentData.Name} [{parentData.TotalItemCount}]{GetAnnotationSuffix(parentData.Name)}";

            string fileParentPath = GetNodePath(parentNode);
            var newNode2 = CreateTreeNode(fileNodeData, fileParentPath);
            parentNode.Sub.Add(newNode2);
            return parentNode;
        }

        private void CleanupEmptyFolderChain(string parentPath)
        {
            if (string.IsNullOrEmpty(parentPath)) return;

            var segments = parentPath.Split('/');
            AntdUI.TreeItem current = GetRootNode();
            if (current == null) return;

            // 遍历到最深层的文件夹
            for (int i = 0; i < segments.Length; i++)
            {
                var found = current.Sub
                    .Cast<AntdUI.TreeItem>()
                    .FirstOrDefault(n =>
                    {
                        var nd = n.Tag as NodeData;
                        return nd != null && nd.IsDirectory &&
                            string.Equals(nd.Name, segments[i], StringComparison.OrdinalIgnoreCase);
                    });
                if (found == null) return;
                current = found;
            }

            // 从最深层往上，逐层删除空文件夹
            while (current != null)
            {
                var data = current.Tag as NodeData;
                if (data == null || !data.IsDirectory) break;

                string path = GetNodePath(current);
                if (string.IsNullOrEmpty(path)) break;

                bool hasChildNodes = current.Sub.Count > 0;
                bool hasChildData = data.Children.Count > 0;
                if (hasChildNodes || hasChildData) break;

                var parent = current.ParentItem;
                var parentData = parent?.Tag as NodeData;
                current.Remove();
                if (parentData != null)
                {
                    var childData = parentData.Children.FirstOrDefault(c =>
                        string.Equals(c.Name, data.Name, StringComparison.OrdinalIgnoreCase));
                    if (childData != null)
                        parentData.Children.Remove(childData);
                    parentData.TotalItemCount = parentData.Children.Sum(c => c.IsDirectory ? c.TotalItemCount + 1 : 1);
                    parent.Text = $"{parentData.Name} [{parentData.TotalItemCount}]{GetAnnotationSuffix(parentData.Name)}";
                }
                current = parent;
            }
        }

        private void RemoveFileNodeFromTree(string relPath)
        {
            if (_fileNodeMap.TryGetValue(relPath, out var node))
            {
                var parentNode = node.ParentItem;
                node.Remove();

                if (parentNode != null)
                {
                    var parentData = parentNode.Tag as NodeData;
                    if (parentData != null)
                    {
                        var childData = parentData.Children.FirstOrDefault(c =>
                            string.Equals(c.Name, GetFileName(relPath), StringComparison.OrdinalIgnoreCase));
                        if (childData != null)
                        {
                            parentData.Children.Remove(childData);
                            parentData.TotalItemCount = parentData.Children.Sum(c => c.IsDirectory ? c.TotalItemCount : 1);
                parentNode.Text = $"{parentData.Name} [{parentData.TotalItemCount}]{GetAnnotationSuffix(parentData.Name)}";
                        }
                    }
                }
                else
                {
                    // ParentItem 为 null（PauseLayout 期间创建的节点），直接通过 NodeData 树移除
                    RemoveNodeDataByPath(relPath);
                }
            }
            else
            {
                RemoveNodeDataByPath(relPath);
            }
        }

        private void RemoveNodeDataByPath(string relPath)
        {
            string parentPath = GetParentPath(relPath);
            string fileName = GetFileName(relPath);
            var parentData = FindNodeDataByPath(parentPath);
            if (parentData != null)
            {
                var childData = parentData.Children.FirstOrDefault(c =>
                    string.Equals(c.Name, fileName, StringComparison.OrdinalIgnoreCase));
                if (childData != null)
                {
                    parentData.Children.Remove(childData);
                    parentData.TotalItemCount = parentData.Children.Sum(c => c.IsDirectory ? c.TotalItemCount : 1);
                }
            }
        }

        // ======================== Save Logic ========================

        private async void OnSaveClick(object sender, EventArgs e)
        {
            SaveCurrentEditorIfDirty();
            // 仅处理实际修改/新增的文件，避免“打开但未修改”的文件被重写（防止内容被意外篡改）
            var modified = _modifiedFiles.Concat(_addedFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (modified.Length == 0 && _deletedFiles.Count == 0)
                return;

            var result = MessageBox.Show(
                LanguageManager.Instance.GetText("Editor.ConfirmSave", "确定要保存修改吗？"),
                LanguageManager.Instance.GetText("Common.MessageBoxTitle.Question", "确认"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            string restorePath = _currentFileRelPath;
            var modifiedNames = new HashSet<string>(modified, StringComparer.OrdinalIgnoreCase);

            var status = await RunWithCancelAsync(LanguageManager.Instance.GetText("Editor.SaveLabel", "保存"), (progress, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (_isArz)
                    SaveArzChangesWithProgress(modifiedNames, progress, ct);
                else
                    SaveArcChangesWithProgress(modifiedNames, progress, ct);
                return true;
            });

            if (status != OperationResult.Success)
            {
                // 失败/取消都不得清理会话状态：保留临时文件与变更集合，用户关闭占用程序后可直接重试
                if (status == OperationResult.Failed && _lastOperationError is SessionPackageWriter.ReplaceFailedException replaceFail)
                {
                    // 用户定案：保存失败提示为四行式（目标文件 / 无法保存：错误码 / 原因 / 本次修改：未丢失，暂存至…），
                    // 只给结论不给解决方案（见 WriteFailureDiagnosis）；Diagnostic 为空才退回旧文案
                    string body = string.IsNullOrEmpty(replaceFail.Diagnostic)
                        ? string.Format(LanguageManager.Instance.GetText("Editor.SaveReplaceFailed",
                            "无法替换 {0}。\n\n本次修改已完整构建为同目录的临时包，未丢失：\n{1}"),
                            replaceFail.TargetPath, replaceFail.TempPath)
                        : replaceFail.Diagnostic;
                    MessageBox.Show(
                        body,
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Error", "错误"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            // ===== 保存 / 固实成功：生成全量变更报告 + 会话汇总 =====
            // 实时日志按行数滚动（默认 2000 行），完整明细由报告文件承载，便于 MOD 作者直接用于发布说明。
            string changeReportPath = LogService.WriteChangeReport(System.IO.Path.GetFileName(_archiveFilePath), _lastSaveBackupPath);
            if (!string.IsNullOrEmpty(changeReportPath))
            {
                LogOutputPanelOnly(LogService.LastSummary);
                LogOutput(string.Format(
                    LanguageManager.Instance.GetText("Editor.ChangeReportSaved", "变更报告已保存：{0}"),
                    changeReportPath), "INFO");
            }
            _lastSaveBackupPath = null;   // 用后即清：下次保存若未生成备份，不会误带旧备份路径

            _session.Clear();
            _modifiedFiles.Clear();
            _modifiedDbrFields.Clear();
            _addedFiles.Clear();
            _deletedFiles.Clear();
            _deletedFileRecordIndices.Clear();
            _currentFileRelPath = null;
            _editorDirty = false;
            ShowPlaceholder();
            UpdateModifiedList();

            LogOutput(LanguageManager.Instance.GetText("Editor.SaveReloading", "保存完成，重新加载"), "INFO");
            CloseArchiveStream();
            if (_isArz) _arzReader = null;
            else _arcFile = null;
            LoadArchive();
            _fileTree.Focus();
            if (restorePath != null)
                SelectFileInTree(restorePath);

            _statusLabel.Text = LanguageManager.Instance.GetText("Editor.SaveComplete", "保存完成");
        }

        /// <summary>
        /// 已修改文件的临时内容丢失（TEMP 被清理软件清空、被手工删除等）时的明确告警。
        /// 以前这里是静默 `continue` → 用户会以为"保存成功"却丢了改动（本次补上，属"判断时机"的一环）。
        /// </summary>
        private static void ReportMissingTempFiles(IProgressCallback progress, List<string> missing)
        {
            if (missing == null || missing.Count == 0) return;
            string sample = string.Join("、", missing.Take(5));
            if (missing.Count > 5) sample += " …";
            progress?.ReportWarning(string.Format(
                LanguageManager.Instance.GetText("Editor.TempFilesMissing",
                    "有 {0} 个已修改文件的临时内容丢失（临时目录可能被清理软件清空），本次未写入包：{1}。请重新打开这些文件、修改后再保存"),
                missing.Count, sample));
        }

        private void SaveArzChangesWithProgress(HashSet<string> modifiedNames, IProgressCallback progress, CancellationToken ct)
        {
            // 中间包建在目标包同目录：同卷 rename 原子替换（见 SessionPackageWriter 说明）
            string tempFile = SessionPackageWriter.CreateTempPackagePath(_archiveFilePath);
            bool keepTempFile = false;   // 替换原包失败时置位：保留已构建好的临时包供用户抢救
            try
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(10, LanguageManager.Instance.GetText("Editor.ReadingArchive", "读取存档数据..."));
                // 流式保存：旧数据区不整体读入内存，保存时按块直写新包（大包内存峰值减半）
                long srcDataOffset = ARZHeader.HEADER_SIZE;
                long srcDataLength = _arzReader.hdr.RecordTableStart - ARZHeader.HEADER_SIZE;
                // ⚠ FileShare.Delete 必须带上：替换原包（同卷 rename）要求目标文件的**所有句柄都允许删除共享**，
                // 否则会被本进程自己的读句柄挡住，报"拒绝访问(5)/目标被占用"。详见下方替换前的提前释放。
                var srcStream = new FileStream(_archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                try
                {
                    var oldStrings = _arzReader.GetStringTable();
                    var origFileTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < _arzReader.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string name = _arzReader.GetRecordName(i);
                        if (!string.IsNullOrEmpty(name))
                            origFileTimes[name] = _arzReader.GetRecordInfo(i).rdFileTime;
                    }

                    BackupOriginalFile(progress);

                    ct.ThrowIfCancellationRequested();
                    progress.Report(30, LanguageManager.Instance.GetText("Editor.BuildingArchive", "构建存档..."));
                    using (var fs = new FileStream(tempFile, FileMode.Create))
                    {
                        var writer = new ARZWriter(_templates, srcStream, srcDataOffset, srcDataLength);
                        writer.strtable.PrepopulateFromOldTable(oldStrings);

                    var originalNames = new HashSet<string>(origFileTimes.Keys, StringComparer.OrdinalIgnoreCase);
                    var modifiedToWrite = new HashSet<string>(modifiedNames, StringComparer.OrdinalIgnoreCase);
                    var tombstoneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    tombstoneNames.UnionWith(_deletedFiles);

                    if (_overwriteOnSaveCheckBox.Checked)
                    {
                        tombstoneNames.UnionWith(modifiedNames);
                    }
                    else
                    {
                        modifiedToWrite.ExceptWith(originalNames);
                    }

                    // 移除非 DBR 修改文件：它们没有模板，WriteFromLines 会失败
                    var nonDbrList = new List<string>();
                    foreach (string p in modifiedNames)
                    {
                        string ext = Path.GetExtension(p);
                        if (!ext.Equals(".dbr", StringComparison.OrdinalIgnoreCase))
                            nonDbrList.Add(p);
                    }
                    string nonDbrLogFmt = LanguageManager.Instance.GetText("Editor.SaveSkippedNonDbr", "跳过非DBR文件: {0}");
                    for (int i = 0; i < nonDbrList.Count; i++)
                    {
                        string p = nonDbrList[i];
                        tombstoneNames.Remove(p);
                        modifiedToWrite.Remove(p);
                        LogOutput(string.Format(nonDbrLogFmt, p), "WARN");
                    }

                    int totalOld = origFileTimes.Count;
                    int processedOld = 0;
                    for (int i = 0; i < _arzReader.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string name = _arzReader.GetRecordName(i);
                        var info = _arzReader.GetRecordInfo(i);
                        if (string.IsNullOrEmpty(name))
                        {
                            writer.WriteTombstone(info);
                            continue;
                        }
                        if (tombstoneNames.Contains(name))
                            writer.WriteTombstone(info);
                        else
                            writer.CopyRecord(name, info, oldStrings);
                        processedOld++;
                        if (processedOld % 100 == 0 || processedOld == totalOld)
                            progress.LogOverwrite(
                                LanguageManager.Instance.GetText("Editor.ProcessingOldRecords", "处理原始记录")
                                + $": {processedOld}/{totalOld}");
                    }
                    progress.LogFinalize();

                    int totalNew = modifiedToWrite.Count;
                    int processedNew = 0;
                    var missingTemp = new List<string>();
                    // 字段级问题与记录级跳过计数（对齐打包侧：逐条上报 + 末尾只报数量汇总）
                    int saveFixTotal = 0, saveWarnTotal = 0, saveSkipTotal = 0;
                    foreach (string relPath in modifiedToWrite)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (_deletedFiles.Contains(relPath)) continue;
                        string srcFile = _session.GetTempFilePath(relPath);
                        if (!File.Exists(srcFile)) { missingTemp.Add(relPath); continue; }
                        // 临时文件按实际编码读取，再按【设置 → 编码 → 内容打包】决定写回包内的编码：
                        // 原样档 = 跟临时文件（零转码）；统一档 = 统一到目标编码
                        string[] lines = TemplateHelper.ReadDbrLines(srcFile, out Encoding tempEnc);
                        // 时间戳规则：编辑器内手改的已有记录保留原包内时间；
                        // 导入（含同名覆盖，见 _importedPaths）与新增记录遵循"实体文件时间戳"，
                        // 取临时文件（导入时已按源文件时间落盘）的修改时间
                        DateTime ft = !_importedPaths.Contains(relPath) && origFileTimes.TryGetValue(relPath, out var oft)
                            ? oft
                            : File.GetLastWriteTime(srcFile);

                        // 构造与写入分离（BuildRecord，与打包侧共用入口）+ 记录级异常不阻断（决策1，与打包对齐）：
                        // 模板未找到/写入异常 → 警告后跳过该记录继续保存，该记录保持原包原样；
                        // 原实现无逐记录 try/catch，单条记录模板解析异常会炸掉整个保存。
                        ARZRecord rec;
                        try
                        {
                            rec = writer.BuildRecord(relPath, lines, ft, EncodingService.ResolveWriteEncoding(tempEnc));
                        }
                        catch (KeyNotFoundException)
                        {
                            saveSkipTotal++;
                            RecordWarningReporter.ReportTemplateMissing(progress, relPath);
                            continue;
                        }

                        // 字段级日志（公共上报，带 groupKey=记录名 → 日志区按记录折叠分组）
                        var (recFixes, recWarns) = RecordWarningReporter.ReportRecordIssues(progress, relPath, rec);
                        saveFixTotal += recFixes;
                        saveWarnTotal += recWarns;

                        try { writer.WriteRecord(rec); }
                        catch (Exception ex)
                        {
                            saveSkipTotal++;
                            progress?.ReportWarning(string.Format(
                                LanguageManager.Instance.GetText("Editor.ErrorWritingRecord",
                                    "写入记录时出错，该记录已跳过（保持原样）：{0}，{1}"), relPath, ex.Message), relPath);
                            continue;
                        }
                        processedNew++;
                        progress.Report(70 + (int)(20.0 * processedNew / totalNew),
                            LanguageManager.Instance.GetText("Editor.WritingNewRecords", "写入新记录")
                            + $": {processedNew}/{totalNew}  {relPath}");
                    }
                    if (missingTemp.Count > 0) ReportMissingTempFiles(progress, missingTemp);

                    // 保存统计（对齐打包侧）：跳过名单与数量必须可见，避免用户误以为全部保存成功
                    if (saveSkipTotal > 0)
                        progress?.ReportWarning(string.Format(
                            LanguageManager.Instance.GetText("Editor.SaveStatSkipped",
                                "保存统计: {0} 条记录因模板未找到或写入异常被跳过（保持原包原样）"), saveSkipTotal));
                    if (saveFixTotal > 0 || saveWarnTotal > 0)
                        progress?.ReportWarning(string.Format(
                            LanguageManager.Instance.GetText("Editor.SaveDataIssueSummary",
                                "本次保存：行格式已修复 {0} 处、可疑 {1} 处（不影响出包，建议核对源文件；逐条明细见上方日志）"),
                            saveFixTotal, saveWarnTotal));

                    ct.ThrowIfCancellationRequested();
                    progress.Report(90, LanguageManager.Instance.GetText("Editor.WritingArchive", "写入存档文件..."));
                    writer.SaveToStream(fs);
                }

                ct.ThrowIfCancellationRequested();
                progress.Report(95, LanguageManager.Instance.GetText("Editor.ReplacingFile", "替换原文件..."));
                // 旧数据区读流必须在替换**之前**释放（SaveToStream 之后不再需要）：
                // 替换（同卷 rename）要求目标文件的所有句柄允许删除共享，晚释放会被本进程自己挡住 → ARZ 保存必失败。
                try { srcStream.Dispose(); } catch { }
                CloseArchiveStream();
                try
                {
                    SessionPackageWriter.ReplaceWithRetry(tempFile, _archiveFilePath);
                }
                catch (Exception ex)
                {
                    // 包已构建完成、仅"替换原文件"失败（多为文件被占用）：保留临时包交由调用方提示用户，不销毁成果
                    keepTempFile = true;
                    throw new SessionPackageWriter.ReplaceFailedException(_archiveFilePath, tempFile, ex);
                }
                tempFile = null;
                progress.ReportLog($"{LanguageManager.Instance.GetText("Editor.ReplacedFile", "已替换原文件")}: {_archiveFilePath}");
                }
                finally
                {
                    // 兜底释放（上面替换前已提前释放；Dispose 幂等）
                    try { srcStream.Dispose(); } catch { }
                }
            }
            finally
            {
                // 仅在"未走到替换步骤"时清理临时包；替换失败时保留（见 keepTempFile）
                try { if (!keepTempFile && tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        private void SaveArcChangesWithProgress(HashSet<string> modifiedNames, IProgressCallback progress, CancellationToken ct)
        {
            // 中间包建在目标包同目录：同卷 rename 原子替换（见 SessionPackageWriter 说明）
            string tempFile = SessionPackageWriter.CreateTempPackagePath(_archiveFilePath);
            bool keepTempFile = false;   // 替换原包失败时置位：保留已构建好的临时包供用户抢救
            try
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(10, LanguageManager.Instance.GetText("Editor.ReadingArchive", "读取存档数据..."));
                var origFileTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _arcFile.toc.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = _arcFile.toc[i].GetEntryString(_arcFile.strs);
                    if (!string.IsNullOrEmpty(name))
                        origFileTimes[name] = _arcFile.toc[i].FileTime;
                }

                BackupOriginalFile(progress);
                _arcFile.fstream = new FileStream(_archiveFilePath, FileMode.Open, FileAccess.Read);

                ct.ThrowIfCancellationRequested();
                progress.Report(30, LanguageManager.Instance.GetText("Editor.BuildingArchive", "构建存档..."));
                using (var outfs = new FileStream(tempFile, FileMode.Create))
                using (var writer = new ARCWriter(outfs))
                {
                    var originalNames = new HashSet<string>(origFileTimes.Keys, StringComparer.OrdinalIgnoreCase);
                    var modifiedToWrite = new HashSet<string>(modifiedNames, StringComparer.OrdinalIgnoreCase);
                    var tombstoneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    tombstoneNames.UnionWith(_deletedFiles);

                    if (_overwriteOnSaveCheckBox.Checked)
                    {
                        tombstoneNames.UnionWith(modifiedNames);
                    }
                    // else: 非覆盖模式下旧条目原样保留（不墓碑），修改内容通过末尾追加写入生效

                    int totalOld = 0;
                    for (int i = 0; i < _arcFile.toc.Count; i++)
                    {
                        var entry = _arcFile.toc[i];
                        string name = entry.GetEntryString(_arcFile.strs);
                        if (!string.IsNullOrEmpty(name) && entry.EntryType == 3)
                            totalOld++;
                    }
                    int processedOld = 0;
                    for (int i = 0; i < _arcFile.toc.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = _arcFile.toc[i];
                        string name = entry.GetEntryString(_arcFile.strs);
                        if (string.IsNullOrEmpty(name))
                        {
                            // 空名条目（游戏原生 EntryType=0 墓碑/空条目）原样保留，维持原始 TOC 结构
                            writer.WriteFromTocEntry(_arcFile, entry);
                            continue;
                        }
                        if (entry.EntryType != 3) continue;
                        if (tombstoneNames.Contains(name))
                            writer.WriteTombstoneEntry(_arcFile, entry);
                        else
                            writer.WriteFromTocEntry(_arcFile, entry);
                        processedOld++;
                        if (processedOld % 100 == 0 || processedOld == totalOld)
                            progress.LogOverwrite(
                                LanguageManager.Instance.GetText("Editor.ProcessingOldRecords", "处理原始记录")
                                + $": {processedOld}/{totalOld}");
                    }
                    progress.LogFinalize();

                    int totalNew = modifiedToWrite.Count;
                    int processedNew = 0;
                    var missingTemp = new List<string>();
                    foreach (string relPath in modifiedToWrite)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (_deletedFiles.Contains(relPath)) continue;
                        string srcFile = _session.GetTempFilePath(relPath);
                        if (!File.Exists(srcFile)) { missingTemp.Add(relPath); continue; }
                        // 时间戳规则：编辑器内手改的已有条目保留原包内时间（小改不全体刷新时间）；
                        // 导入（含同名覆盖，见 _importedPaths）与新增条目遵循"实体文件时间戳"——
                        // 取临时文件（导入时已按源文件时间落盘）的修改时间
                        DateTime ft = !_importedPaths.Contains(relPath) && origFileTimes.TryGetValue(relPath, out var oft)
                            ? oft
                            : File.GetLastWriteTime(srcFile);
                        // 临时文件按【设置 → 编码 → 内容打包】写回包内：
                        // 原样档 = 直接搬临时文件字节（零转码）；统一档 = 先转码再写。
                        // 二进制/白名单外文件永不动。
                        if (EncodingService.PackNeedsConvert && TextFileHelper.IsTextFile(relPath))
                        {
                            byte[] srcBytes = File.ReadAllBytes(srcFile);
                            EncodingService.NormalizeForPack(srcBytes, out byte[] packed, out _);
                            using (var ms = new MemoryStream(packed, false))
                                writer.WriteFromStream(relPath, ft, ms);
                        }
                        else
                        {
                            using (var stream = File.OpenRead(srcFile))
                                writer.WriteFromStream(relPath, ft, stream);
                        }
                        processedNew++;
                        progress.Report(70 + (int)(20.0 * processedNew / totalNew),
                            LanguageManager.Instance.GetText("Editor.WritingNewRecords", "写入新记录")
                            + $": {processedNew}/{totalNew}  {relPath}");
                    }
                    if (missingTemp.Count > 0) ReportMissingTempFiles(progress, missingTemp);
                }

                ct.ThrowIfCancellationRequested();
                progress.Report(95, LanguageManager.Instance.GetText("Editor.ReplacingFile", "替换原文件..."));
                CloseArchiveStream();
                try
                {
                    SessionPackageWriter.ReplaceWithRetry(tempFile, _archiveFilePath);
                }
                catch (Exception ex)
                {
                    // 包已构建完成、仅"替换原文件"失败（多为文件被占用）：保留临时包交由调用方提示用户，不销毁成果
                    keepTempFile = true;
                    throw new SessionPackageWriter.ReplaceFailedException(_archiveFilePath, tempFile, ex);
                }
                tempFile = null;
                progress.ReportLog($"{LanguageManager.Instance.GetText("Editor.ReplacedFile", "已替换原文件")}: {_archiveFilePath}");
            }
            finally
            {
                // 仅在"未走到替换步骤"时清理临时包；替换失败时保留（见 keepTempFile）
                try { if (!keepTempFile && tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        // ======================== Backup Helper ========================

        private void CloseArchiveStream()
        {
            if (_archiveStream != null)
            {
                _archiveStream.Close();
                _archiveStream = null;
            }
            if (_arcFile?.fstream != null)
            {
                _arcFile.fstream.Close();
                _arcFile.fstream = null;
            }
        }

        private static string GetSequentialBackupPath(string dir, string name, string ext)
        {
            int maxNum = 0;
            // 备份名固定用 backup（国际惯例，不做多语言）：<包名>-backup-001.arz
            string prefix = $"{name}-backup-";
            if (Directory.Exists(dir))
            {
                foreach (string f in Directory.GetFiles(dir, $"{prefix}*{ext}"))
                {
                    string fn = Path.GetFileNameWithoutExtension(f);
                    if (fn.Length > prefix.Length && int.TryParse(fn.Substring(prefix.Length), out int n) && n > maxNum)
                        maxNum = n;
                }
            }
            return Path.Combine(dir, $"{prefix}{maxNum + 1:D3}{ext}");
        }

        private void BackupOriginalFile(IProgressCallback progress = null)
        {
            // 每次保存前先清空：只有本次真正生成了备份，"修改前备份"才会写进变更报告（便于回滚）
            _lastSaveBackupPath = null;
            if (!_backupCheckBox.Checked || string.IsNullOrEmpty(_archiveFilePath)) return;
            CloseArchiveStream();
            string dir = Path.GetDirectoryName(_archiveFilePath);
            string name = Path.GetFileNameWithoutExtension(_archiveFilePath);
            string ext = Path.GetExtension(_archiveFilePath);
            string backup = GetSequentialBackupPath(dir, name, ext);
            try
            {
                string msg = string.Format(
                    LanguageManager.Instance.GetText("Editor.BackupCreating", "备份文件: {0}"), backup);
                if (progress != null)
                    progress.ReportLog(msg);
                else
                    _statusLabel.Text = msg;
                File.Copy(_archiveFilePath, backup, overwrite: false);
                _lastSaveBackupPath = backup;   // 备份成功才记录，供变更报告写入"修改前备份"行
            }
            catch (Exception ex)
            {
                // 备份失败不再静默：明确告警"本次未生成备份"（后台线程，仅走进度回调/状态栏，不弹窗）
                string failMsg = string.Format(
                    LanguageManager.Instance.GetText("Editor.BackupFailed", "备份失败，本次未生成备份: {0}"), ex.Message);
                if (progress != null)
                    progress.ReportWarning(failMsg);
                else
                    _statusLabel.Text = failMsg;
            }
        }

        // ======================== Compact Logic ========================

        private async void OnCompactClick(object sender, EventArgs e)
        {
            SaveCurrentEditorIfDirty();

            var result = MessageBox.Show(
                LanguageManager.Instance.GetText("Editor.ConfirmCompact", "固实将重新打包整个文件，确定要继续吗？"),
                LanguageManager.Instance.GetText("Common.MessageBoxTitle.Question", "确认"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes) return;

            string restorePath = _currentFileRelPath;
            string arcPath = _archiveFilePath;
            bool shouldBackup = _backupCheckBox.Checked;

            CloseArchiveStream();

            var status = await RunWithCancelAsync(LanguageManager.Instance.GetText("Editor.CompactLabel", "固实"), (progress, ct) =>
            {
                Action backupAction = null;
                if (shouldBackup)
                {
                    string dir = Path.GetDirectoryName(arcPath);
                    string name = Path.GetFileNameWithoutExtension(arcPath);
                    string ext = Path.GetExtension(arcPath);
                    string backup = GetSequentialBackupPath(dir, name, ext);
                    backupAction = () =>
                    {
                        progress.ReportLog(LanguageManager.Instance.GetText("Editor.CompactBackingUp", "正在备份原文件..."));
                        File.Copy(arcPath, backup, overwrite: false);
                    };
                }

                if (_isArz)
                    return GDPackEditorAPI.CompactArz(arcPath, arcPath, progress, ct, backupAction);
                else
                    return GDPackEditorAPI.CompactArc(arcPath, arcPath, progress, ct, backupAction);
            });

            if (status == OperationResult.Cancelled) return;

            if (status == OperationResult.Success)
            {
                _session.Clear();
                _modifiedFiles.Clear();
                _modifiedDbrFields.Clear();
                _addedFiles.Clear();
                _deletedFiles.Clear();
                _deletedFileRecordIndices.Clear();
                _statusChangeLog.Clear();
                _currentFileRelPath = null;
                _editorDirty = false;
                ShowPlaceholder();
                UpdateModifiedList();

                LogOutput(LanguageManager.Instance.GetText("Editor.CompactDoneReload", "固实完成，重新加载"), "INFO");
                CloseArchiveStream();
                if (_isArz) _arzReader = null;
                else _arcFile = null;
                LoadArchive();
                _fileTree.Focus();
                if (restorePath != null)
                    SelectFileInTree(restorePath);

                _statusLabel.Text = LanguageManager.Instance.GetText("Editor.CompactDone", "固实完成");
            }
            else
            {
                _statusLabel.Text = LanguageManager.Instance.GetText("Editor.CompactFailed", "固实失败");
                LogOutput(LanguageManager.Instance.GetText("Editor.CompactFailed", "固实失败"), "ERROR");
            }
        }

        /// <summary>
        /// 内容检测（等价 CLI --check 的 ARZ 内容检查）：逐记录检查值含换行 / 逗号 / 控制字符。
        /// 进度以「检测中（当前/总数）XX%」在日志区单行原地刷新，完成后汇总一条。
        /// </summary>
        private async void OnDetectClick(object sender, EventArgs e)
        {
            if (!_isArz)
            {
                LogOutput(LanguageManager.Instance.GetText("Editor.DetectArzOnly",
                    "内容检测仅支持 ARZ 包（ARC 为二进制容器，无此项检查）"), "WARN");
                return;
            }
            if (string.IsNullOrEmpty(_archiveFilePath) || !File.Exists(_archiveFilePath))
            {
                LogOutput(LanguageManager.Instance.GetText("Editor.DetectNoFile",
                    "未找到已打开的包文件，无法检测"), "WARN");
                return;
            }
            if (_logSplit != null && _logSplit.Panel2Collapsed)
                _logSplit.Panel2Collapsed = false;   // 保证检测过程与结果可见

            // 进度只反映到按钮上（转圈 + 百分比），**不写日志**——避免进度行污染检测结果
            string detectIdleText = _detectButton.Text;
            void OnDetectProgress(int pct, string msg)
            {
                if (_detectButton == null) return;
                void apply()
                {
                    _detectButton.Loading = true;
                    _detectButton.Text = detectIdleText + " " + pct + "%";
                }
                if (_detectButton.InvokeRequired)
                {
                    try { _detectButton.BeginInvoke((Action)apply); } catch { }
                }
                else apply();
            }

            int detectNewlineIssues = 0;
            try
            {
                await RunWithCancelAsync(
                    LanguageManager.Instance.GetText("Editor.Detect", "检测"),
                    (progress, ct) =>
                    {
                        // 逐条可疑与结尾汇总由 CheckArzContent 经 progress 输出；进度走 OnDetectProgress
                        var r = GDPackEditorAPI.CheckArzContent(_archiveFilePath, progress, ct, OnDetectProgress);
                        detectNewlineIssues = r.NewlineIssues;
                        return true;
                    });
            }
            finally
            {
                if (_detectButton != null)
                {
                    _detectButton.Loading = false;
                    _detectButton.Text = detectIdleText;
                }
            }

            // 换行说明放"检测完成"之后：此处用户注意力最高（解释"为何导出/源码里看不到换行"）
            if (detectNewlineIssues > 0)
                LogOutput(LanguageManager.Instance.GetText("LOG.CheckArzNewlineNote",
                    "说明：值里的换行会在解包 / 编辑器源码模式被自动删除（否则会破坏 DBR 行结构），"
                    + "所以导出的文件与源码视图里看不到它——本检测读取的是包内原始数据"), "INFO");
        }

        private async void OnCheckTombstoneClick(object sender, EventArgs e)
        {
            LogOutput(LanguageManager.Instance.GetText("Editor.TombstoneCheckStart", "开始检查墓碑记录..."), "INFO");
            _statusLabel.Text = LanguageManager.Instance.GetText("Editor.TombstoneCheckStart", "检查墓碑记录...");

            int tombstoneCount = 0;
            int totalCount = 0;
            long totalSize = 0;
            await Task.Run(() =>
            {
                if (_isArz && _arzReader != null)
                {
                    totalCount = _arzReader.Count;
                    for (int i = 0; i < totalCount; i++)
                    {
                        string name = _arzReader.GetRecordName(i);
                        if (string.IsNullOrEmpty(name))
                        {
                            tombstoneCount++;
                            var info = _arzReader.GetRecordInfo(i);
                            if (info != null)
                                totalSize += info.rdSizeCompressed;
                        }
                    }
                }
                else if (!_isArz && _arcFile != null)
                {
                    totalCount = _arcFile.toc?.Count ?? 0;
                    for (int i = 0; i < totalCount; i++)
                    {
                        var entry = _arcFile.toc[i];
                        string name = entry.GetEntryString(_arcFile.strs);
                        if (string.IsNullOrEmpty(name) || entry.EntryType != 3)
                        {
                            tombstoneCount++;
                            totalSize += entry.CompressedSize;
                        }
                    }
                }
            });

            if (tombstoneCount > 0)
            {
                LogOutput(string.Format(
                    LanguageManager.Instance.GetText("Editor.TombstoneCheckResult", "总记录数: {0}, 墓碑记录: {1}"),
                    totalCount, tombstoneCount), "INFO");
                if (totalSize > 0)
                {
                    LogOutput(string.Format(
                        LanguageManager.Instance.GetText("Editor.TombstoneCheckSize", "预计墓碑记录文件总大小: {0:N0} 字节 ({1:N2} KB)"),
                        totalSize, totalSize / 1024.0), "INFO");
                }
                LogOutput(LanguageManager.Instance.GetText("Editor.TombstoneCheckHint", "如需清理，请点击固实。"), "INFO");
                _statusLabel.Text = string.Format(
                    LanguageManager.Instance.GetText("Editor.TombstoneCheckResult", "总记录数: {0}, 墓碑记录: {1}"),
                    totalCount, tombstoneCount);
            }
            else
            {
                LogOutput(LanguageManager.Instance.GetText("Editor.TombstoneCheckNone", "没有墓碑记录。"), "INFO");
                _statusLabel.Text = LanguageManager.Instance.GetText("Editor.TombstoneCheckNone", "没有墓碑记录");
            }
        }

        // ======================== Menu Actions ========================

        private void OnOpenArz(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = LanguageManager.Instance.GetText("Editor.SelectArchiveFile", "选择归档文件"),
                Filter = LanguageManager.Instance.GetText("Editor.ArchiveFileFilter", "归档文件|*.arz;*.arc|ARZ 文件|*.arz|ARC 文件|*.arc"),
                InitialDirectory = AppDirectory
            })
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    OpenArchiveFile(dlg.FileName);
                }
            }
        }

        private void OnOpenArc(object sender, EventArgs e)
        {
            OnOpenArz(sender, e);
        }

        /// <summary>
        /// 统一打开归档文件：当前窗口无文件时直接加载，否则开新窗口。
        /// </summary>
        private void OpenArchiveFile(string filePath)
        {
            SettingsManager.AddRecentFile(filePath);
            if (string.IsNullOrEmpty(_archiveFilePath))
                LoadNewArchive(filePath);
            else
            {
                var editor = new ArzArcEditorForm(filePath);
                editor.Show();
            }
        }

        private string TruncateFilePath(string filePath, int maxLength = 80)
        {
            if (string.IsNullOrEmpty(filePath) || filePath.Length <= maxLength)
                return filePath;
            int charsToShow = maxLength - 3;
            return "..." + filePath.Substring(filePath.Length - charsToShow);
        }

        private void OnHistoryClick(object sender, EventArgs e)
        {
            var recentFiles = SettingsManager.GetRecentFiles();
            if (recentFiles.Count == 0)
            {
                MessageBox.Show(LanguageManager.Instance.GetText("Editor.NoRecentFiles", "暂无历史记录"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var table = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = true,
                Bordered = true,
                MultipleRows = false,
                EditMode = AntdUI.TEditMode.None,
                LostFocusClearSelection = false,
                Font = FontHelper.UiFont(9),
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill
            };

            table.Columns.Add(new AntdUI.Column("FileName",
                LanguageManager.Instance.GetText("Common.File", "文件")) { Width = "50%", LineBreak = true });
            table.Columns.Add(new AntdUI.Column("OpenTime",
                LanguageManager.Instance.GetText("Editor.OpenTime", "打开时间")) { Width = "30%", Align = AntdUI.ColumnAlign.Right });
            table.Columns.Add(new AntdUI.Column("CellLinks", "") { Width = "130" });

            var dataSource = new List<object>();
            foreach (var file in recentFiles)
            {
                bool exists = PathCheckHelper.Exists(file.FilePath);
                dataSource.Add(new
                {
                    FileName = file.FilePath,
                    FilePath = file.FilePath,
                    Exists = exists,
                    OpenTime = file.OpenTime.ToString("yyyy-MM-dd HH:mm"),
                    CellLinks = new AntdUI.CellLink[]
                    {
                        new AntdUI.CellButton("open", LanguageManager.Instance.GetText("Common.Open", "打开"), AntdUI.TTypeMini.Primary),
                        new AntdUI.CellButton("clear", LanguageManager.Instance.GetText("Common.Clear", "清除"))
                    }
                });
            }
            table.DataSource = dataSource;

            AntdUI.Modal.Config config = null;
            table.CellButtonClick += (sender, e) =>
            {
                var btn = e.Btn;
                var data = e.Record as dynamic;
                string filePath = data.FilePath;

                if (btn.Id == "open")
                {
                    config?.Close();
                    BeginInvoke((Action)(() => OpenArchiveFile(filePath)));
                }
                else if (btn.Id == "clear")
                {
                    SettingsManager.RemoveRecentFile(filePath);
                    var updatedFiles = SettingsManager.GetRecentFiles();
                    if (updatedFiles.Count == 0)
                    {
                        config?.Close();
                        MessageBox.Show(LanguageManager.Instance.GetText("Editor.NoRecentFiles", "暂无历史记录"),
                            LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        var newDataSource = new List<object>();
                        foreach (var file in updatedFiles)
                        {
                            newDataSource.Add(new
                            {
                                FileName = file.FilePath,
                                FilePath = file.FilePath,
                                Exists = PathCheckHelper.Exists(file.FilePath),
                                OpenTime = file.OpenTime.ToString("yyyy-MM-dd HH:mm"),
                                CellLinks = new AntdUI.CellLink[]
                                {
                                    new AntdUI.CellButton("open", LanguageManager.Instance.GetText("Common.Open", "打开"), AntdUI.TTypeMini.Primary),
                                    new AntdUI.CellButton("clear", LanguageManager.Instance.GetText("Common.Clear", "清除"))
                                }
                            });
                        }
                        table.DataSource = newDataSource;
                    }
                }
            };

            table.CellPaintBegin += (s, e) =>
            {
                if (e.RowType == AntdUI.RowType.Column || e.RowType == AntdUI.RowType.Summary || e.Record == null) return;
                if (e.Column == null || e.Column.Key != "FileName") return;
                bool missing = !(bool)((dynamic)e.Record).Exists;
                PathCheckHelper.ApplyMissingCellStyle(e, "FileName", missing, table.Font);
            };
            table.Size = new Size(760, 400);

            var historyContent = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent,
                Size = new Size(760, 440)
            };

            var historyFooter = new AntdUI.Panel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                Back = Color.Transparent
            };
            var historyClearAllBtn = new AntdUI.Button
            {
AutoSize = true,
            Font = FontHelper.UiFont(9),
                Text = LanguageManager.Instance.GetText("Editor.ClearAll", "清空"),
                Type = AntdUI.TTypeMini.Error,
                
                Dock = DockStyle.Left
            };
            historyClearAllBtn.Click += (s, ev) =>
            {
                SettingsManager.ClearRecentFiles();
                var updatedFiles = SettingsManager.GetRecentFiles();
                if (updatedFiles.Count == 0)
                {
                    config?.Close();
                    MessageBox.Show(LanguageManager.Instance.GetText("Editor.NoRecentFiles", "暂无历史记录"),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    var newDataSource = new List<object>();
                    foreach (var file in updatedFiles)
                    {
                        newDataSource.Add(new
                        {
                            FileName = file.FilePath,
                            FilePath = file.FilePath,
                            OpenTime = file.OpenTime.ToString("yyyy-MM-dd HH:mm"),
                            CellLinks = new AntdUI.CellLink[]
                            {
                                new AntdUI.CellButton("open", LanguageManager.Instance.GetText("Common.Open", "打开"), AntdUI.TTypeMini.Primary),
                                new AntdUI.CellButton("clear", LanguageManager.Instance.GetText("Common.Clear", "清除"))
                            }
                        });
                    }
                    table.DataSource = newDataSource;
                }
            };
            historyFooter.Controls.Add(historyClearAllBtn);
            historyContent.Controls.Add(table);
            historyContent.Controls.Add(historyFooter);

            config = new AntdUI.Modal.Config(this,
                LanguageManager.Instance.GetText("Editor.OpenHistory", "打开历史"),
                historyContent)
            {
                Resizable = true,
                Draggable = true
            };
            config.SetWidth(800);
            config.SetCancel(LanguageManager.Instance.GetText("Common.Cancel", "取消"));
            config.SetOk(LanguageManager.Instance.GetText("Common.Confirm", "确定"));
            AntdUI.Modal.open(config);
        }

        // ======================== Form Events ========================

        private DialogResult TrySaveChanges()
        {
            var modified = _session.GetModifiedRelativePaths();
            if (modified.Length > 0 || _deletedFiles.Count > 0)
            {
                var result = MessageBox.Show(
                    LanguageManager.Instance.GetText("Editor.CloseWithUnsaved", "有未保存的更改，是否保存？"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Question", "确认"),
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                return result;
            }
            return DialogResult.No;
        }

        private void ReloadArchive()
        {
            LogOutput(LanguageManager.Instance.GetText("Editor.Reloading", "重新加载档案..."), "INFO");
            string restorePath = _currentFileRelPath;
            if (_currentFileRelPath != null)
            {
                _editorDirty = false;
                ShowPlaceholder();
            }
            _session.Clear();
            _modifiedFiles.Clear();
            _modifiedDbrFields.Clear();
            _addedFiles.Clear();
            _deletedFiles.Clear();
            _deletedFileRecordIndices.Clear();
            _statusChangeLog.Clear();
            _currentFileRelPath = null;
            UpdateModifiedList();
            CloseArchiveStream();
            if (_isArz) _arzReader = null;
            else _arcFile = null;
            LoadArchive();
            _folderTree.Focus();
            if (restorePath != null)
                SelectFileInTree(restorePath);
        }

        private void OnContextReloadClick(object sender, EventArgs e)
        {
            var result = TrySaveChanges();
            if (result == DialogResult.Yes)
            {
                OnSaveClick(null, EventArgs.Empty);
            }
            else if (result == DialogResult.No)
            {
                ReloadArchive();
            }
        }

        /// <summary>
        /// 保存本窗体两个表格（文件列表 + DBR编辑器）的列宽记忆。
        /// 供主窗体退出时代理兜底调用：主窗体先关时本窗体收不到自身 OnFormClosing（消息循环直接结束），
        /// 列宽会永远丢失。Save 内部去重，重复调用无 IO；表未创建 / 开关关闭时内部直接跳过。
        /// </summary>
        internal void SaveColumnWidths()
        {
            TableWidthStore.Save(TableWidthStore.TableId.FileList, _fileListGrid);
            TableWidthStore.Save(TableWidthStore.TableId.DbrGrid, _dbrGrid);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            var result = TrySaveChanges();
            if (result == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            // 关窗快照列宽记忆（TableWidthStore：文件列表 + DBR编辑器两表；设置开关关闭时内部直接跳过）
            TableWidthStore.Save(TableWidthStore.TableId.FileList, _fileListGrid);
            TableWidthStore.Save(TableWidthStore.TableId.DbrGrid, _dbrGrid);

            // 关窗属于"一批改动的结束"：把源码模式里尚未落盘的行级改动记进变更记录
            // （与 CloseCurrentFile 一致地无条件记录；取消关窗已在上面 return，不会记录未确认的改动）
            LogSourceLineChanges();

            FlushLogQueue();
            _logTimer?.Dispose();
            _debounceTimer?.Dispose();
            _sourceLogTimer?.Dispose();
            _session.Dispose();
            _archiveStream?.Dispose();
            base.OnFormClosing(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // DPI-aware min sizes for splitters
            float dpiScale = DpiManager.Scale;
            _mainSplit.SetMinSizes((int)(200 * dpiScale), (int)(500 * dpiScale));
            _centerRightSplit.SetMinSizes((int)(400 * dpiScale), 0);
            _logSplit.SetMinSizes((int)(200 * dpiScale), (int)(40 * dpiScale));
            _dbrSplit.SetMinSizes((int)(120 * dpiScale), null);

            // DPI-scaled editor panel padding
            int pad = (int)(6 * dpiScale);
            _editorPanel.Padding = new Padding(pad);

            // DPI-aware line number margin width
            if (_dbrSourceEditor.Margins.Count > 0)
                _dbrSourceEditor.Margins[0].Width = (int)(50 * dpiScale);



            // AntdUI.Tree 使用内置箭头图标，无需 ImageList

            _mainSplit.SplitterDistance = Math.Min((int)(300 * dpiScale), _mainSplit.Width / 4);
            _mainSplit.SplitterDistance = Math.Max(_mainSplit.SplitterDistance, _mainSplit.Panel1MinSize);
            _mainSplit.SplitterDistance = Math.Min(_mainSplit.SplitterDistance, _mainSplit.Width - _mainSplit.Panel2MinSize - _mainSplit.SplitterWidth);
            _centerRightSplit.SplitterDistance = Math.Max((int)(400 * dpiScale), _centerRightSplit.Width - (int)(300 * dpiScale));
            _centerRightSplit.SplitterDistance = Math.Max(_centerRightSplit.SplitterDistance, _centerRightSplit.Panel1MinSize);
            _centerRightSplit.SplitterDistance = Math.Min(_centerRightSplit.SplitterDistance, _centerRightSplit.Width - _centerRightSplit.Panel2MinSize - _centerRightSplit.SplitterWidth);
            _logSplit.SplitterDistance = Math.Max(_logSplit.Height - (int)(260 * dpiScale), _logSplit.Panel1MinSize);
            _logSplit.SplitterDistance = Math.Max(_logSplit.SplitterDistance, _logSplit.Panel1MinSize);
            _logSplit.SplitterDistance = Math.Min(_logSplit.SplitterDistance, _logSplit.Height - _logSplit.Panel2MinSize - _logSplit.SplitterWidth);

            // 恢复上次的折叠状态
            if (SettingsManager.EditorRightPanelCollapsed) _centerRightSplit.Collapse();
            if (SettingsManager.EditorLogPanelCollapsed) _logSplit.Collapse();
            if (SettingsManager.EditorLeftPanelCollapsed)
            {
                _mainSplit.Collapse();
                _mainSplit.Panel1MinSize = 0;
                _mainSplit.SplitterDistance = 0;
            }

            if (!string.IsNullOrEmpty(_archiveFilePath))
                LoadArchive();

            // 初始加载时恢复上次生效的语言包
            if (SettingsManager.ActivePackKeys.Count > 0)
            {
                RestoreActiveLanguagePacks();
            }
            _folderTree.Focus();

            // 初始显示占位提示
            ShowPlaceholder();
        }

        private static string FormatDuration(TimeSpan elapsed)
        {
            if (elapsed.TotalHours >= 1)
            {
                int hours = (int)elapsed.TotalHours;
                int mins = elapsed.Minutes;
                double secs = elapsed.Seconds + elapsed.Milliseconds / 1000.0;
                return string.Format(
                    LanguageManager.Instance.GetText("Common.DurationHours", "{0} 时 {1} 分 {2:F1} 秒"),
                    hours, mins, secs);
            }
            if (elapsed.TotalMinutes >= 1)
            {
                int mins = (int)elapsed.TotalMinutes;
                double secs = elapsed.Seconds + elapsed.Milliseconds / 1000.0;
                return string.Format(
                    LanguageManager.Instance.GetText("Common.DurationMinutes", "{0} 分 {1:F1} 秒"),
                    mins, secs);
            }
            return string.Format(
                LanguageManager.Instance.GetText("Common.DurationSeconds", "{0:F2} 秒"),
                elapsed.TotalSeconds);
        }
    }

    public class ArrowSplitter : AntdUI.Splitter
    {
        private const int VisualBarWidth = 4;
        private const int TextPadding = 12;
        private bool _disposed;

        public string CollapsedTextKey { get; set; }
        public string ExpandedTextKey { get; set; }

        /// <summary>
        /// 面板1最大尺寸（像素）。0 表示不限制。消费者在 SizeChanged 中自行 clamp。
        /// </summary>
        public int Panel1MaxSize { get; set; }

        private string _collapsedText = "";
        private string _expandedText = "";

        public ArrowSplitter()
        {
            // 构造时 MinSize 必须保持 0：AntdUI 自动缩放(DpiLS)会在窗体创建瞬间按"旧值×DPI"恢复这些最小值，
            // 此时控件尚未布局、宽度不足，会抛 SplitterDistance 越界异常。真实约束由 SetMinSizes 统一设置。
            Panel1MinSize = 0;
            Panel2MinSize = 0;
            SplitterWidth = 24;
            SplitterSize = 40;
            // 显式设置常规字体：防止继承父容器加粗字体（如编辑器 Tabs 页签 Bold）导致折叠按钮文字加粗
            Font = FontHelper.UiFont(9);
            BackColor = ThemeManager.BgLayout;
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            // 尺寸变化时尝试补设尚未应用的最小尺寸（用于 Tab 页内尚未布局的分栏）
            SizeChanged += (s, e) => TryApplyMinSizes();
        }

        // 待应用的最小尺寸（延迟设置用）：位于未布局 Tab 页内的分栏，OnLoad 立即设置会因宽度不足抛异常，
        // 记录目标值并等控件获得尺寸后自动补设。
        private int? _pendingP1Min;
        private int? _pendingP2Min;
        private bool _minSizesApplied;

        /// <summary>
        /// 安全设置面板最小尺寸：立即尝试应用；若控件当前宽度不足（如位于未激活/不可见的 Tab 页内），
        /// 自动在尺寸变化时补设，避免 SplitContainer 校验越界崩溃。
        /// </summary>
        public void SetMinSizes(int? panel1Min, int? panel2Min)
        {
            _pendingP1Min = panel1Min;
            _pendingP2Min = panel2Min;
            TryApplyMinSizes();
        }

        private void TryApplyMinSizes()
        {
            if (_minSizesApplied) return;
            if (!_pendingP1Min.HasValue && !_pendingP2Min.HasValue) return;
            bool ok = true;
            try
            {
                if (_pendingP1Min.HasValue) Panel1MinSize = _pendingP1Min.Value;
                if (_pendingP2Min.HasValue) Panel2MinSize = _pendingP2Min.Value;
            }
            catch (InvalidOperationException) { ok = false; }
            catch (ArgumentOutOfRangeException) { ok = false; }
            if (ok) _minSizesApplied = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RefreshTexts();
        }

        private void OnLanguageChanged()
        {
            RefreshTexts();
        }

        private void RefreshTexts()
        {
            _collapsedText = string.IsNullOrEmpty(CollapsedTextKey) ? "" : LanguageManager.Instance.GetText(CollapsedTextKey, CollapsedTextKey);
            _expandedText = string.IsNullOrEmpty(ExpandedTextKey) ? "" : LanguageManager.Instance.GetText(ExpandedTextKey, ExpandedTextKey);
            if (!IsHandleCreated) return;

            string s = _collapsedText.Length >= _expandedText.Length ? _collapsedText : _expandedText;
            int len = SplitterSize;
            try
            {
                using (var g = CreateGraphics())
                {
                    var sz = g.MeasureString(s, Font);
                    // 与 OnPaint 中 size = SplitterSize * Config.Dpi 保持一致，保证按钮宽度与命中区域随 DPI 正确缩放
                    if (Orientation == Orientation.Horizontal)
                        len = (int)Math.Ceiling(sz.Width * AntdUI.Config.Dpi) + TextPadding * 2;
                    else
                        len = (int)Math.Ceiling(sz.Height * s.Length * AntdUI.Config.Dpi) + TextPadding * 2;
                }
            }
            catch
            {
                // 测量异常时按字体行高估算，保证按钮尺寸仍能容纳文字
                len = s.Length * Font.Height + TextPadding * 2;
            }
            if (SplitterSize != len) SplitterSize = len;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = AntdUI.Helper.High(e.Graphics);
            var rect = SplitterRectangle;

            g.Fill(ThemeManager.SplitterBar, rect);

            Rectangle barRect = rect;
            if (Orientation == Orientation.Horizontal)
            {
                barRect.Y = (Height - VisualBarWidth) / 2;
                barRect.Height = VisualBarWidth;
            }
            else
            {
                barRect.X = (Width - VisualBarWidth) / 2;
                barRect.Width = VisualBarWidth;
            }
            g.Fill(SplitterBack ?? ThemeManager.SplitterBack, barRect);

            if (CollapsePanel == AntdUI.Splitter.ADCollapsePanel.None) return;

            int size = (int)(SplitterSize * AntdUI.Config.Dpi);
            Rectangle btnArea = rect;
            if (Orientation == Orientation.Horizontal)
            {
                btnArea.X = (Width - size) / 2;
                btnArea.Width = size;
            }
            else
            {
                btnArea.Y = (Height - size) / 2;
                btnArea.Height = size;
            }

            g.Fill(Color.FromArgb(25, ThemeManager.SplitterButton), btnArea);

            string text = SplitPanelState ? _collapsedText : _expandedText;
            if (string.IsNullOrEmpty(text))
                text = SplitPanelState ? CollapsedTextKey : ExpandedTextKey;
            if (string.IsNullOrEmpty(text)) return;

            if (Orientation == Orientation.Horizontal)
            {
                // 绘制时实时测量文字，构造恰好包住文字的小矩形再居中，避免预测量与 DPI 缩放后渲染尺寸
                // 不一致（AddString 在过小矩形中裁剪）导致文字不显示。
                SizeF tsz = e.Graphics.MeasureString(text, Font);
                var txtRect = new RectangleF(0, 0, tsz.Width + 4, tsz.Height + 4);
                txtRect.X = btnArea.X + (btnArea.Width - txtRect.Width) / 2f;
                txtRect.Y = btnArea.Y + (btnArea.Height - txtRect.Height) / 2f;
                g.String(text, Font, ThemeManager.SplitterButton, txtRect, AntdUI.FormatFlags.Center);
            }
            else
            {
                // 竖排逐字绘制：GDI+ 的 DirectionVertical 在按钮高度不足以容纳文字时会整段不显示，
                // 改为逐字居中、纵向排列，保证文字必然可见。
                SizeF chSize = e.Graphics.MeasureString("我", Font);
                float x = btnArea.X + (btnArea.Width - chSize.Width) / 2f;
                float y = btnArea.Y + (btnArea.Height - chSize.Height * text.Length) / 2f;
                foreach (char c in text)
                {
                    g.String(c.ToString(), Font, ThemeManager.SplitterButton,
                        new RectangleF(x, y, chSize.Width, chSize.Height), AntdUI.FormatFlags.Center);
                    y += chSize.Height;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing) LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;
            }
            base.Dispose(disposing);
        }
    }
}
