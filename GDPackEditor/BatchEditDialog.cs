using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace GDPackEditor
{
    // ───────────────────────── 类型：三态 / 快照 / 意图 / 计划 / 结果 ─────────────────────────

    /// <summary>逐文件三态：正常（写入并占号）/ 跳过（算但不写，仍占号）/ 忽略（不参与，序号前移）</summary>
    internal enum BatchRowState { Normal = 0, Skip = 1, Ignore = 2 }

    /// <summary>批量模式：全部相同 / 单数步进 / 数组步进 / 固定分割</summary>
    internal enum BatchMode { AllSame = 0, Step = 1, ArrayStep = 2, Split = 3 }

    /// <summary>循环插值片段的操作（Keep = 不插：该文件原样输出，不插值）</summary>
    internal enum BatchFragmentOp { Keep = 0, Prepend, Append, Replace }

    /// <summary>循环插值片段：对模式生成的值做 前插/后插/替换/保持</summary>
    internal sealed class BatchFragment
    {
        internal BatchFragmentOp Op;
        internal string Text = "";
    }

    /// <summary>单个文件的快照（进入弹窗时取一次；点确定时重取比对）</summary>
    internal sealed class BatchFileSnap
    {
        internal string RelPath;
        /// <summary>列表里显示的名字（文件名，重名时调用方自行补目录尾）</summary>
        internal string DisplayName;
        /// <summary>变量名 → 现值（键不区分大小写）</summary>
        internal Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>变量名 → 原文是否有该行</summary>
        internal Dictionary<string, bool> KeyExists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        /// <summary>变量名 → 原文出现次数</summary>
        internal Dictionary<string, int> Occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>批量编辑的外部数据快照（由编辑器构建；点确定时会重取一份比对）</summary>
    internal sealed class BatchEditContext
    {
        /// <summary>文件清单（顺序 = 视觉列序，序号按它计）</summary>
        internal List<BatchFileSnap> Files = new List<BatchFileSnap>();
        internal string BasePath;
        /// <summary>可选字段（模板变量全量，不含 templateName 与只读行）</summary>
        internal List<string> FieldNames = new List<string>();
        /// <summary>字段 → 是否数组（模板级，同模板文件一致）</summary>
        internal Dictionary<string, bool> FieldIsArray = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        /// <summary>字段 → 是否预设下拉/bool</summary>
        internal Dictionary<string, bool> FieldIsPicklist = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        /// <summary>字段 → 下拉候选（仅下拉字段有）</summary>
        internal Dictionary<string, string[]> FieldPicklist = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        /// <summary>右键进入时预选的字段（可为空）</summary>
        internal string PreselectField;
    }

    /// <summary>用户意图：弹窗内的全部输入（三态 / 手改 / 参数）</summary>
    internal sealed class BatchIntent
    {
        /// <summary>path → 三态（缺省 = 正常）</summary>
        internal Dictionary<string, BatchRowState> StateByPath = new Dictionary<string, BatchRowState>(StringComparer.OrdinalIgnoreCase);
        /// <summary>path → 手改的最终值（缺省 = 无手改）</summary>
        internal Dictionary<string, string> ManualOverrideByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal BatchMode Mode = BatchMode.AllSame;
        /// <summary>基准值：以弹窗输入框的实时内容为准（非快照），改一个字重算一次</summary>
        internal string BaseValueRaw = "";
        internal string Formula = "";
        internal string Prefix = "";
        internal string Suffix = "";
        internal int Pad = 0;
        // ── 循环插值片段（②循环插值：不插/替换/前插/后插）──
        /// <summary>插值总开关：关闭时片段不参与计算</summary>
        internal bool FragmentEnabled = false;
        /// <summary>片段序列（顺序即应用顺序）</summary>
        internal List<BatchFragment> Fragments = new List<BatchFragment>();
        /// <summary>循环次数：留空 = 无限循环；填 N = 整套片段最多轮 N 次，用尽后其余文件不插值</summary>
        internal string FragmentRounds = "";
        // ── 固定分割 ──
        internal string SplitMethod = "Symbol";     // Symbol / Newline / Length
        internal string SplitSymbol = "/";
        internal string SplitContent = "";
        internal int SplitLength = 1;
        internal bool SplitLoop = false;
    }

    /// <summary>计划中的一条写入（状态=正常且通过门禁）</summary>
    internal sealed class PlannedEdit
    {
        internal string RelPath;
        internal string OldValue;
        internal string AutoValue;
        internal string FinalValue;
        internal bool IsNewFieldRow;
        internal bool IsManual;
    }

    /// <summary>计划中的一条 跳过/忽略（AutoValue 供列表灰字显示）</summary>
    internal sealed class PlannedSkip
    {
        internal string RelPath;
        internal BatchRowState State;
        internal string AutoValue;
    }

    /// <summary>计数（列表计数条、左侧摘要与确认框共用）</summary>
    internal sealed class BatchPlanCounters
    {
        internal int Total;        // 参与文件总数
        internal int Normal;       // 将写入
        internal int Skip;         // 跳过（算但不写，占号）
        internal int Ignore;       // 忽略（不参与，序号前移）
        internal int Manual;       // 其中手工覆盖的行
        internal int Blocked;      // 因门禁阻断而不写入的"正常"行
        internal int NewFieldRow;  // 其中将新增字段行的个数
        internal int Warn;         // 警告条数
        internal int AutoCleaned;  // 已自动清理（移除换行/控制字符）的行数
    }

    /// <summary>计算结果：写入计划（列表与写入层共用）</summary>
    internal sealed class BatchPlan
    {
        internal List<PlannedEdit> Edits = new List<PlannedEdit>();
        internal List<PlannedSkip> Skips = new List<PlannedSkip>();
        /// <summary>门禁结论（按文件分组；类而非结构体，便于判空）</summary>
        internal List<BatchIssueGroup> Issues = new List<BatchIssueGroup>();
        internal BatchPlanCounters Counters = new BatchPlanCounters();
        /// <summary>整批被阻断时的原因（如 公式无效 / 基准值非数值）</summary>
        internal string PrimaryBlockedText;
        /// <summary>提示级信息（如「分割段发生循环复用」）</summary>
        internal List<string> InfoNotes = new List<string>();
    }

    /// <summary>某文件的一组门禁结论</summary>
    internal sealed class BatchIssueGroup
    {
        internal PlannedEdit Edit;
        internal List<DbrValueIssue> Issues = new List<DbrValueIssue>();
    }

    /// <summary>弹窗返回结果：交给编辑器的写入引擎执行</summary>
    internal sealed class BatchEditResult
    {
        internal string FieldName;
        /// <summary>将写入的文件：path → 最终值（仅"正常且通过门禁"的行；跳过/忽略/阻断不在其中）</summary>
        internal Dictionary<string, string> FinalValuesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal List<string> SkipPaths = new List<string>();
        internal List<string> IgnorePaths = new List<string>();
        internal BatchPlanCounters Counters;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 弹窗本体：左参数区 / 右文件列表（预览 + 操作台）
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 批量编辑弹窗。
    ///
    /// 结构：Modal 宿主（Panel 内容）→ 顶部工具行（字段/计数）→ Splitter（左参数固定 320 / 右列表自适应）
    /// → 底部按钮区。
    ///
    /// 三条设计原则：
    /// ① **单一计算真源** —— 列表显示与最终写入出自同一个纯函数 <see cref="ComputePlan"/>，
    ///    点确定时**重新取快照重算并比对**，不一致则不关窗；
    /// ② **写入前必须过门禁** —— 值含逗号/换行等会破坏 DBR 行结构的内容一律挡住，**不自动修正**
    ///    （变更日志记的是用户填的值，程序偷改会让日志与落盘不一致）；
    /// ③ **一切按"文件路径"写** —— 序号只存在于显示层，写入层与三态/手改状态都按路径记账。
    /// </summary>
    internal sealed class BatchEditDialog : AntdUI.Panel
    {
        private readonly BatchEditContext _ctx;                        // 打开时的快照
        private readonly Func<BatchEditContext> _snapshotProvider;     // 点确定时重取快照用
        private readonly BatchIntent _intent = new BatchIntent();

        private BatchEditContext _current;                             // 当前生效的快照
        private string _fieldName;                                     // 当前选中的字段
        private BatchEditResult _result;
        private string _lastSignature = "";

        // ── 左参数区控件 ──
        private AntdUI.Select _selField;
        private AntdUI.Input _txtBase;
        private AntdUI.Button _btnGenArray;                 // 生成数组（仅数组步进可用）
        private AntdUI.Radio _radioAllSame;                 // 批量模式（4 个单选，2×2 排布）
        private AntdUI.Radio _radioStep;
        private AntdUI.Radio _radioArrayStep;
        private AntdUI.Radio _radioSplit;
        private AntdUI.Panel _stepPanel;                    // 步进子面板（单数/数组步进共用，模式切换时显隐）
        private AntdUI.Panel _splitPanel;                   // 固定分割子面板
        private AntdUI.Dropdown _ddCommonFormula;           // 常用公式（下拉菜单，填入公式框）
        private AntdUI.Input _txtFormula;
        private AntdUI.Button _btnFormulaHelp;
        private AntdUI.InputNumber _nudPad;
        private AntdUI.Input _txtPrefix;
        private AntdUI.Input _txtSuffix;
        private AntdUI.Input _txtSplitContent;              // 分割内容真源（多行文本，编辑器弹窗中显示）
        private AntdUI.Button _btnSplitContent;             // 打开分割内容编辑弹窗
        private AntdUI.Panel _splitSymbolRow;               // 按符号：符号输入行（按需显示）
        private AntdUI.Panel _splitLengthRow;               // 按长度：长度输入行（按需显示）
        private AntdUI.Radio _radioSplitNewline;            // 分割方式（3 个单选，互斥）
        private AntdUI.Radio _radioSplitSymbol;
        private AntdUI.Radio _radioSplitLength;
        private AntdUI.Input _txtSplitSymbol;               // 分割符号
        private AntdUI.InputNumber _nudSplitLength;         // 定长字符数
        private AntdUI.Checkbox _chkSplitLoop;              // 段用尽后循环复用
        private AntdUI.Input _txtFragments;                 // 循环插值片段真源（多行文本，编辑器弹窗中显示）
        private AntdUI.Switch _swFragmentEnabled;           // 插值总开关（真源）
        private string _fragmentRounds = "";                 // 循环次数：留空=无限（真源）
        private AntdUI.Button _btnFragmentSetup;            // 打开插值片段编辑弹窗
        private AntdUI.Select _selPreset;                   // 参数预设下拉
        private AntdUI.Button _btnPresetSave;
        private AntdUI.Button _btnPresetDelete;
        private AntdUI.Button _btnClearManual;              // 清手改（无手改时置灰）
        private AntdUI.Label _lblLeftCounters;              // 参与 / 占号 / 将写入

        // ── 右列表区控件 ──
        private AntdUI.Table _grid;
        private BindingList<BatchRow> _rows = new BindingList<BatchRow>();
        private AntdUI.Label _lblTopCounters;
        private AntdUI.Label _lblGateHint;                  // 门禁/错误动态提示（左栏底部，危险系颜色）
        private BatchPlan _lastPlan;                        // 最近一次计算结果（手改提交后刷新计数用）

        // ── 确定/取消：由 Modal 自带按钮承担（弹窗内不再放第二套） ──
        /// <summary>宿主窗体（Modal.Config 的 Target 需要它；Panel 不能直接当 owner）</summary>
        private readonly AntdUI.BaseForm _owner;

        // ── 状态标志 ──
        private bool _syncingRows;        // 勾选事件重入守卫（模型回写/批量刷新期间）
        private bool _suspendRecompute;   // 程序性改控件文本时抑制重算

        private System.Windows.Forms.Timer _recomputeTimer;   // 输入防抖（200ms）

        /// <summary>批量编辑的进入点：构建弹窗、打开、返回结果（取消/阻断返回 null）</summary>
        internal static BatchEditResult Show(AntdUI.BaseForm owner, BatchEditContext ctx, Func<BatchEditContext> snapshotProvider)
        {
            if (ctx == null || ctx.Files.Count == 0) return null;

            var dialog = new BatchEditDialog(ctx, snapshotProvider, owner);

            // ⚠️ 内容为 Control 时窗口宽高 = 内容控件 Size + 内边距（config.Width 被丢弃），
            //    所以默认尺寸必须直接设在内容控件上，并按屏幕工作区钳位（§9.1 L1）：
            //    dialog.Width 是设计像素（会被自动 DPI 再乘一次），MaximumSize 是物理像素，不能互赋
            float scale = DpiManager.Enabled ? DpiManager.Scale : 1f;
            var wa = Screen.PrimaryScreen.WorkingArea;
            int designW = Math.Min(1120, Math.Max(860, (int)((wa.Width - 48) / scale)));
            int designH = Math.Min(720, Math.Max(560, (int)((wa.Height - 96) / scale)));
            dialog.Size = new Size(DpiManager.S(designW), DpiManager.S(designH));

            var config = new AntdUI.Modal.Config(
                owner,
                LanguageManager.Instance.GetText("Editor.BatchEditTitle", "批量编辑"),
                dialog)
            {
                Resizable = true,
                MinimumSize = new Size(DpiManager.S(860), DpiManager.S(560)),
                MaximumSize = new Size(wa.Width - 32, wa.Height - 32),
                Padding = new Size(12, 10),
                OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                OkType = AntdUI.TTypeMini.Primary,
                MaskClosable = false,
                // ⚠️ 不设 Draggable：AntdUI 对 Draggable 弹窗会在底部按钮行 MouseMove 时触发
                //    DraggableMouseDown（模拟标题栏按下），鼠标扫过"确定"一行就会激活窗口抢前台
                // 右上角原生 X（= 取消，与 Cancel 语义一致）
                CloseIcon = true,
                // ⚠️ 不绑定回车=确定（DefaultAcceptButton）：分割内容/片段是多行输入，
                //    回车必须留给换行；确定请点 Modal 底部按钮
                DefaultAcceptButton = false,
                OnOk = c => dialog.ConfirmAndBuildResult()
            };
            AntdUI.Modal.open(config);
            return dialog._result;
        }

        private BatchEditDialog(BatchEditContext ctx, Func<BatchEditContext> provider, AntdUI.BaseForm owner)
        {
            _ctx = ctx;
            _current = ctx;
            _snapshotProvider = provider;
            _owner = owner;
            _lastSignature = BuildSignature(_current);

            BackColor = Color.Transparent;
            Font = FontHelper.UiFont(9);

            BuildUi();
            BuildRows();

            // 预选字段（右键进入时为被点字段；按钮进入时为第一个字段）
            _suspendRecompute = true;
            _selField.SelectedValue = string.IsNullOrEmpty(ctx.PreselectField)
                ? (ctx.FieldNames.Count > 0 ? ctx.FieldNames[0] : null)
                : ctx.PreselectField;
            _fieldName = _selField.SelectedValue as string;
            _suspendRecompute = false;
            _txtBase.Text = GetBaseValueRaw(_fieldName);   // 基准值预填（全部相同/步进/分割都以它为起点）

            RecomputeAll();   // 首次计算 + 填列表
            ReloadPresetList();   // 参数预设下拉（选中不触发重算，_suspendRecompute 在其内部控制）

            _recomputeTimer = new System.Windows.Forms.Timer { Interval = 200 };   // 输入防抖 200ms
            _recomputeTimer.Tick += (s, e) =>
            {
                _recomputeTimer.Stop();
                RecomputeAll();
            };
        }

        // ───────────── 界面构建 ─────────────

        private void BuildUi()
        {
            // ── 顶部工具行：字段下拉（左）+ 阻断/警告/将写入（右）──
            var topBar = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(44), Back = Color.Transparent };

            _selField = new AntdUI.Select
            {
                Width = DpiManager.S(320),
                Height = DpiManager.S(36),      // 正常大小（此前 30 太矮不明显）
                Font = FontHelper.UiFont(9),
                MaxCount = 20                   // 下拉列表最多显示 20 项（默认 4）
            };
            foreach (string name in _ctx.FieldNames)
            {
                // 显示「原字段名（本地化字段名）」；未命中本地化字典时只显示原名
                string loc = DbrKeyMapping.HasMapping(name) ? DbrKeyMapping.GetLocalizedName(name) : null;
                string display = string.IsNullOrEmpty(loc) || string.Equals(loc, name, StringComparison.Ordinal)
                    ? name
                    : name + "（" + loc + "）";
                _selField.Items.Add(new AntdUI.SelectItem(display, name));
            }
            // 宽度自适应：装不下最长项就自动加宽（上限 520，避免挤掉右上角统计）
            int needWidth = 0;
            foreach (AntdUI.SelectItem item in _selField.Items)
                needWidth = Math.Max(needWidth, TextRenderer.MeasureText(item.Text, _selField.Font).Width);
            _selField.Width = Math.Min(DpiManager.S(520),
                Math.Max(DpiManager.S(320), needWidth + DpiManager.S(48)));
            _selField.SelectedIndexChanged += (s, e) =>
            {
                if (_suspendRecompute) return;
                _fieldName = _selField.SelectedValue as string;
                _txtBase.Text = GetBaseValueRaw(_fieldName);   // 换字段时基准值跟随（自动触发重算）
                RecomputeAll();
            };

            _lblTopCounters = new AntdUI.Label
            {
                Text = "", AutoSize = true, Dock = DockStyle.Right,
                Font = FontHelper.UiFont(9), BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleRight, ForeColor = ThemeManager.Text
            };

            // 门禁/错误动态提示：左栏底部，危险系颜色（阻断=红、警告=橙、信息=主色）
            _lblGateHint = new AntdUI.Label
            {
                Text = "", Dock = DockStyle.Bottom, Height = DpiManager.S(36),
                Font = FontHelper.UiFont(9), BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = ThemeManager.Modified,
                Margin = new Padding(0)
            };

            topBar.Controls.Add(_lblTopCounters);    // Right（后加 → 处理靠前 → 最右）
            topBar.Controls.Add(_selField);
            // §6.6 反向 Dock：后加的在右 → 字段下拉在左、计数在右

            // ── 左参数区 ──
            // ⚠️ 左右两个内容面板必须 Dock=Fill 填满分栏：
            //    AntdUI 控件没有默认尺寸，不设 Dock/尺寸就是 0×0 —— 整栏不可见（弹窗"空白"的根因）
            var leftPanel = new AntdUI.Panel { Dock = DockStyle.Fill, Back = Color.Transparent, Margin = new Padding(0) };

            // 基准值[u] + 生成数组（生成数组仅"数组步进"可用）
            _txtBase = new AntdUI.Input
            {
                Dock = DockStyle.Fill, Font = FontHelper.UiFont(9),
                PrefixText = LanguageManager.Instance.GetText("Editor.BatchBaseValue", "基准值[u]："),
                BackColor = Color.Transparent
            };
            _txtBase.TextChanged += (s, e) => ScheduleRecompute();
            _btnGenArray = new AntdUI.Button
            {
AutoSize = true,
                Text = LanguageManager.Instance.GetText("Editor.BatchGenArray", "生成数组"),
                Dock = DockStyle.Right, 
                Font = FontHelper.UiFont(9), Enabled = false,
                // ⚠️ 按钮不要设 BackColor：显式赋值（含透明）会覆盖主色填充，变成"悬停才是主色"
                Type = AntdUI.TTypeMini.Primary
            };
            _btnGenArray.Click += (s, e) => GenerateBaseArray();
            var baseRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(40), Back = Color.Transparent };
            baseRow.Controls.Add(_txtBase);
            baseRow.Controls.Add(_btnGenArray);

            // 参与 / 占号 / 将写入
            _lblLeftCounters = new AntdUI.Label
            {
                Text = "", Dock = DockStyle.Top, Height = DpiManager.S(22),
                Font = FontHelper.UiFont(9), ForeColor = ThemeManager.TextTertiary,
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent
            };

            // 批量模式：4 个单选按钮，每行 2 个（一行 4 个太宽，两行正好）
            _radioAllSame = MakeModeRadio(LanguageManager.Instance.GetText("Editor.BatchModeAllSame", "全部相同"), true);
            _radioStep = MakeModeRadio(LanguageManager.Instance.GetText("Editor.BatchModeStep", "单数步进"), false);
            _radioArrayStep = MakeModeRadio(LanguageManager.Instance.GetText("Editor.BatchModeArrayStep", "数组步进"), false);
            _radioSplit = MakeModeRadio(LanguageManager.Instance.GetText("Editor.BatchModeSplit", "固定分割"), false);
            _radioAllSame.CheckedChanged += (s, e) => { UpdateModePanels(); RecomputeAll(); };
            _radioStep.CheckedChanged += (s, e) => { UpdateModePanels(); RecomputeAll(); };
            _radioArrayStep.CheckedChanged += (s, e) => { UpdateModePanels(); RecomputeAll(); };
            _radioSplit.CheckedChanged += (s, e) => { UpdateModePanels(); RecomputeAll(); };
            var modeRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(56), Back = Color.Transparent };
            var modeFlow = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Color.Transparent,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = true
            };
            // 定宽 = 半栏宽 → 每行正好 2 个；同一容器 → 自动互斥
            modeFlow.Controls.Add(_radioSplit);
            modeFlow.Controls.Add(_radioArrayStep);
            modeFlow.Controls.Add(_radioStep);
            modeFlow.Controls.Add(_radioAllSame);
            modeRow.Controls.Add(modeFlow);

            // 步进子面板（单数步进 / 数组步进共用：公式[含常用公式▾] / 取整+小数位）
            _stepPanel = new AntdUI.Panel { Dock = DockStyle.Top, Back = Color.Transparent, Height = DpiManager.S(80), Visible = false };

            _txtFormula = new AntdUI.Input
            {
                Dock = DockStyle.Fill, Font = FontHelper.UiFont(9),
                PrefixText = LanguageManager.Instance.GetText("Editor.BatchFormula", "公式："),
                BackColor = Color.Transparent
            };
            _txtFormula.TextChanged += (s, e) => ScheduleRecompute();
            // 常用公式 ▾（合并进公式行：点开选一条即填入公式框；默认样式 + 自带下拉箭头）
            // 来源统一走 DbrArrayFormula.CommonFormulas（与生成数组弹窗、内嵌帮助的"常用配方"一致）
            _ddCommonFormula = new AntdUI.Dropdown
            {
                Text = LanguageManager.Instance.GetText("Editor.BatchFormulaExamples", "示例"),
                Dock = DockStyle.Right, Width = DpiManager.S(66),
                Font = FontHelper.UiFont(9),
                ListAutoWidth = true, MaxCount = 13,
                ShowArrow = true,               // 组件自带箭头（用 ShowArrow 开启）
                Type = AntdUI.TTypeMini.Primary
            };
            foreach (var (display, formula) in DbrArrayFormula.CommonFormulas)
                _ddCommonFormula.Items.Add(new AntdUI.SelectItem(display, formula));
            _ddCommonFormula.SelectedValueChanged += (s, e) =>
            {
                var v = _ddCommonFormula.SelectedValue as string;
                if (!string.IsNullOrEmpty(v)) _txtFormula.Text = v;
            };
            _btnFormulaHelp = new AntdUI.Button
            {
AutoSize = true,
                Text = "?", Dock = DockStyle.Right, 
                Font = FontHelper.UiFont(9, FontStyle.Bold), Type = AntdUI.TTypeMini.Primary
            };
            _btnFormulaHelp.Click += (s, e) => ShowFormulaHelp();
            var formulaRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(40), Back = Color.Transparent };
            formulaRow.Controls.Add(_txtFormula);
            formulaRow.Controls.Add(_ddCommonFormula);
            formulaRow.Controls.Add(_btnFormulaHelp);
            _stepPanel.Controls.Add(formulaRow);

            // 取整已移入公式（round/floor/ceiling），不再有独立取整下拉与小数位（2026-09 定案）

            // 固定分割子面板（内容入口 / 方法 / 循环复用）—— 仅勾选"固定分割"时显示
            // ⚠️ 分割内容改弹窗编辑：_txtSplitContent 是真源（不挂界面，防 Modal 关闭被 Dispose），
            //    弹窗内每次新建输入控件双向同步，编辑中实时重算
            _splitPanel = new AntdUI.Panel { Dock = DockStyle.Top, Back = Color.Transparent, Height = DpiManager.S(106), Visible = false };

            _txtSplitContent = new AntdUI.Input { Multiline = true, Font = FontHelper.UiFont(9), Text = "" };
            _btnSplitContent = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Editor.BatchSplitContentBtn", "编辑分割内容…"),
                Dock = DockStyle.Fill, Font = FontHelper.UiFont(9),
                Type = AntdUI.TTypeMini.Primary
            };
            _btnSplitContent.Click += (s, e) => ShowSplitContentDialog();
            var splitContentRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(32), Back = Color.Transparent };
            splitContentRow.Controls.Add(_btnSplitContent);

            // 分割方式：单选（按换行 / 按符号 / 按长度）；符号与长度输入框按需显隐
            // 窄一点：3 个要在一行放下（左栏约 300 可用宽）
            _radioSplitNewline = MakeModeRadio(LanguageManager.Instance.GetText("Editor.BatchSplitByNewline", "按换行"), true);
            _radioSplitSymbol = MakeModeRadio(LanguageManager.Instance.GetText("Editor.BatchSplitBySymbol", "按符号"), false);
            _radioSplitLength = MakeModeRadio(LanguageManager.Instance.GetText("Editor.BatchSplitByLength", "按长度"), false);
            _radioSplitNewline.Width = _radioSplitSymbol.Width = _radioSplitLength.Width = DpiManager.S(94);
            _radioSplitNewline.CheckedChanged += (s, e) => { UpdateSplitRows(); RecomputeAll(); };
            _radioSplitSymbol.CheckedChanged += (s, e) => { UpdateSplitRows(); RecomputeAll(); };
            _radioSplitLength.CheckedChanged += (s, e) => { UpdateSplitRows(); RecomputeAll(); };
            var splitMethodRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(30), Back = Color.Transparent };
            var splitMethodFlow = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Color.Transparent,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false
            };
            splitMethodFlow.Controls.Add(_radioSplitLength);
            splitMethodFlow.Controls.Add(_radioSplitSymbol);
            splitMethodFlow.Controls.Add(_radioSplitNewline);
            splitMethodRow.Controls.Add(splitMethodFlow);

            // 按符号：只显示符号输入框；按长度：只显示长度输入框；按换行：都不显示
            _txtSplitSymbol = new AntdUI.Input
            {
                Dock = DockStyle.Fill, Font = FontHelper.UiFont(9),
                Text = "/", PrefixText = LanguageManager.Instance.GetText("Editor.BatchSplitSymbol", "符："),
                BackColor = Color.Transparent
            };
            _txtSplitSymbol.TextChanged += (s, e) => ScheduleRecompute();
            _splitSymbolRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(36), Back = Color.Transparent, Visible = false };
            _splitSymbolRow.Controls.Add(_txtSplitSymbol);

            _nudSplitLength = new AntdUI.InputNumber
            {
                Dock = DockStyle.Fill, Minimum = 1, Maximum = 4096, Value = 1,
                Font = FontHelper.UiFont(9), PrefixText = LanguageManager.Instance.GetText("Editor.BatchSplitLength", "长："),
                BackColor = Color.Transparent
            };
            _nudSplitLength.ValueChanged += (s, e) => ScheduleRecompute();
            _nudSplitLength.TextChanged += (s, e) => ScheduleRecompute();
            _splitLengthRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(36), Back = Color.Transparent, Visible = false };
            _splitLengthRow.Controls.Add(_nudSplitLength);

            _chkSplitLoop = new AntdUI.Checkbox
            {
                Text = LanguageManager.Instance.GetText("Editor.BatchSplitLoop", "分割段用尽后循环复用"),
                AutoSize = true, Font = FontHelper.UiFont(9), BackColor = Color.Transparent
            };
            _chkSplitLoop.CheckedChanged += (s, e) => ScheduleRecompute();
            var splitLoopRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(34), Back = Color.Transparent };
            splitLoopRow.Controls.Add(_chkSplitLoop);
            // ⚠️ 加入顺序 = Dock 处理逆序：最后加的在最上
            //    视觉自上而下 = 分割内容按钮 → 方式单选 → 符号行 → 长度行 → 循环复用
            _splitPanel.Controls.Add(splitLoopRow);
            _splitPanel.Controls.Add(_splitLengthRow);
            _splitPanel.Controls.Add(_splitSymbolRow);
            _splitPanel.Controls.Add(splitMethodRow);
            _splitPanel.Controls.Add(splitContentRow);

            // 循环插值：左栏只留入口按钮，编辑器在弹窗里（常态不占位）
            // ⚠️ _txtFragments/_swFragmentEnabled/_fragmentRounds 是真源（不挂到界面，防 Modal 关闭时被 Dispose）；
            //    每次打开弹窗新建输入控件，双向同步（编辑中即实时重算）
            _txtFragments = new AntdUI.Input { Multiline = true, Font = FontHelper.UiFont(8.5f), Text = "" };
            _swFragmentEnabled = new AntdUI.Switch { Checked = false };   // 插值总开关（默认关）
            _btnFragmentSetup = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Editor.BatchFragmentBtn", "插值片段设置…"),
                Dock = DockStyle.Fill, Font = FontHelper.UiFont(9),
                Type = AntdUI.TTypeMini.Primary
            };
            _btnFragmentSetup.Click += (s, e) => ShowFragmentDialog();
            var fragBtnRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(32), Back = Color.Transparent };
            fragBtnRow.Controls.Add(_btnFragmentSetup);

            // 参数预设：下拉选择 + 保存 + 删除
            _selPreset = new AntdUI.Select
            {
                Dock = DockStyle.Fill, Font = FontHelper.UiFont(9),
                PlaceholderText = LanguageManager.Instance.GetText("Editor.BatchPresetPlaceholder", "参数预设")
            };
            _selPreset.SelectedIndexChanged += (s, e) =>
            {
                if (_suspendRecompute) return;
                ApplyPreset(_selPreset.SelectedValue as string);
            };
            _btnPresetSave = new AntdUI.Button
            {
AutoSize = true,
                Text = LanguageManager.Instance.GetText("Editor.BatchPresetSave", "存"),
                Dock = DockStyle.Right, Font = FontHelper.UiFont(9),
                Type = AntdUI.TTypeMini.Primary
            };
            _btnPresetSave.Click += (s, e) => SavePresetDialog();
            _btnPresetDelete = new AntdUI.Button
            {
AutoSize = true,
                Text = LanguageManager.Instance.GetText("Editor.BatchPresetDelete", "删"),
                Dock = DockStyle.Right, Font = FontHelper.UiFont(9),
                Type = AntdUI.TTypeMini.Primary
            };
            _btnPresetDelete.Click += (s, e) => DeletePreset();
            var presetRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(32), Back = Color.Transparent };
            presetRow.Controls.Add(_selPreset);
            presetRow.Controls.Add(_btnPresetSave);
            presetRow.Controls.Add(_btnPresetDelete);

            // 补位 / 前缀 / 后缀
            _nudPad = new AntdUI.InputNumber
            {
                Dock = DockStyle.Fill, Minimum = 0, Maximum = 12, Value = 0,
                Font = FontHelper.UiFont(9), PrefixText = LanguageManager.Instance.GetText("Editor.BatchPad", "补位："),
                BackColor = Color.Transparent
            };
            _nudPad.ValueChanged += (s, e) => ScheduleRecompute();
            _nudPad.TextChanged += (s, e) => ScheduleRecompute();

            _txtPrefix = new AntdUI.Input { Dock = DockStyle.Fill, Font = FontHelper.UiFont(9), PrefixText = LanguageManager.Instance.GetText("Editor.BatchPrefix", "前缀："), BackColor = Color.Transparent };
            _txtPrefix.TextChanged += (s, e) => ScheduleRecompute();

            _txtSuffix = new AntdUI.Input { Dock = DockStyle.Fill, Font = FontHelper.UiFont(9), PrefixText = LanguageManager.Instance.GetText("Editor.BatchSuffix", "后缀："), BackColor = Color.Transparent };
            _txtSuffix.TextChanged += (s, e) => ScheduleRecompute();

            // 左栏不放提示区：阻断/警告的文字提示统一进右上角状态行（与状态统计同处）
            // ── 组装左栏（§6.6 反向 Dock：后加的在上）──
            // 视觉自上而下 = 基准值+生成数组 → 计数 → 模式 → 步进子面板 → 分割子面板
            //                → 补位 → 前缀 → 后缀 → 插值入口 → 预设
            leftPanel.Controls.Add(presetRow);
            leftPanel.Controls.Add(fragBtnRow);
            leftPanel.Controls.Add(Row(_txtSuffix));
            leftPanel.Controls.Add(Row(_txtPrefix));
            leftPanel.Controls.Add(Row(_nudPad));
            leftPanel.Controls.Add(_splitPanel);
            leftPanel.Controls.Add(_stepPanel);
            leftPanel.Controls.Add(modeRow);
            leftPanel.Controls.Add(_lblLeftCounters);
            leftPanel.Controls.Add(baseRow);

            // 重新计算：公式区下方、提示文字上方。保底手段——无条件「重算 + 填表格 + 刷新 UI」
            var recalcRow = new AntdUI.Panel { Dock = DockStyle.Bottom, Height = DpiManager.S(40), Back = Color.Transparent };
            var btnRecalc = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Editor.BatchRecalc", "重新计算"),
                Dock = DockStyle.Fill, Font = FontHelper.UiFont(9),
                Type = AntdUI.TTypeMini.Primary
            };
            btnRecalc.Click += (s, e) =>
            {
                RecomputeAll();      // 重算 + 填表格（内部含 LoadLayout + Invalidate）
                _grid?.Refresh();    // 保底再刷一次：不依赖内部重绘时机
            };
            recalcRow.Controls.Add(btnRecalc);

            leftPanel.Controls.Add(recalcRow);      // 后加 → 先处理 → 落在提示文字上方
            leftPanel.Controls.Add(_lblGateHint);   // Dock=Bottom：左栏最底部（与 Top 系列互不挤占）

            // ── 右列表区 ──
            // ⚠️ 同上：必须 Dock=Fill 填满右分栏
            var rightPanel = new AntdUI.Panel { Dock = DockStyle.Fill, Back = Color.Transparent, Margin = new Padding(0) };

            BuildGrid();
            // ⚠️ 加入顺序决定 Dock 处理顺序（后加的先处理）：
            //    表格(Fill) 必须最先加入 → 最后处理 → 只占工具行让出的中间区域；
            //    反过来的话表格会铺满整栏，工具行浮在表格上面（"按钮悬浮遮挡表格"的根因）
            rightPanel.Controls.Add(_grid);

            // 列表工具行：按钮放进 FlowLayoutPanel（自然顺序排布）；宽不够自动折行，不裁不叠
            // （原「作用范围」Segmented 已按定案移除：状态按钮一律作用于全部行）
            var listBar = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(64), Back = Color.Transparent };
            var btnFlow = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Color.Transparent,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = true
            };
            btnFlow.Controls.Add(MakeListButton("Editor.BatchBtnClearSkip", "全不跳过", (s, e) => ApplyStateByScope(BatchRowState.Normal, onlySkip: true)));
            btnFlow.Controls.Add(MakeListButton("Editor.BatchBtnClearIgnore", "全不忽略", (s, e) => ApplyStateByScope(BatchRowState.Normal, onlyIgnore: true)));
            btnFlow.Controls.Add(MakeListButton("Editor.BatchBtnAllSkip", "全部跳过…", (s, e) => ApplyStateByScopeWithConfirm(BatchRowState.Skip)));
            btnFlow.Controls.Add(MakeListButton("Editor.BatchBtnAllIgnore", "全部忽略…", (s, e) => ApplyStateByScopeWithConfirm(BatchRowState.Ignore)));
            _btnClearManual = MakeListButton("Editor.BatchBtnClearManual", "清手改", (s, e) => ClearManualOverrides());
            btnFlow.Controls.Add(_btnClearManual);   // 字段引用：无手改时置灰（否则点了没反应像坏按钮）
            btnFlow.Controls.Add(MakeListButton("Editor.BatchBtnCopyPreview", "复制预览", (s, e) => CopyPreviewToClipboard()));
            listBar.Controls.Add(btnFlow);
            rightPanel.Controls.Add(listBar);   // 后加 → 先处理 → 占顶部

            // ── 分栏 ──
            var split = new AntdUI.Splitter
            {
                Dock = DockStyle.Fill,
                SplitterWidth = DpiManager.S(5),
                FixedPanel = FixedPanel.Panel1,
                Panel1MinSize = 0,          // 构造期必须 0（宽度还是 0，自动 DPI 校验必失败）
                Panel2MinSize = 0,
                // ⚠️ 透明子控件的"透"是透出父控件背景：Panel1/Panel2 是 Splitter 的子控件，
                //    Splitter.BackColor 若给灰色，左右两栏透明后露出的就是这层灰。
                //    这里给与 Modal 壳完全一致的容器色（Modal 壳 = BgElevated），子面板透明后即与壳同色；
                //    分隔条颜色单独走 SplitterBack（Splitter 自己画条），与控件底色无关。
                BackColor = AntdUI.Style.Db.BgElevated,
                SplitterBack = ThemeManager.BorderColor
            };
            // 左右两栏背景透明（AntdUI Splitter 的 Panel1/Panel2 默认带底色，不设就是灰的）
            split.Panel1.BackColor = Color.Transparent;
            split.Panel2.BackColor = Color.Transparent;
            split.Panel1.Controls.Add(leftPanel);
            split.Panel2.Controls.Add(rightPanel);

            // ⚠️ SplitterDistance 必须等首次拿到真实宽度再设（ImportDialog 既有范式）：
            //    构造期宽度是 0，此时设距离会被钳成极小值 → 打开后左栏过窄，一拖又弹回 MinSize
            bool splitInit = false;
            split.SizeChanged += (s, e) =>
            {
                if (splitInit || split.Width < DpiManager.S(640)) return;
                splitInit = true;
                split.Panel1MinSize = DpiManager.S(320);   // 左栏下限：绝不压缩（压了公式框就看不见）
                split.SplitterDistance = DpiManager.S(320);
            };

            // ── 组装（§6.6 反向 Dock：后加的最先处理）──
            // 确定/取消只有一套 —— Modal 底部自带按钮承担（弹窗内不再放第二套）
            Controls.Add(split);        // Fill：最后处理，占中间
            Controls.Add(topBar);       // Top：最先处理，置顶

            ApplyLocalizedCounters();
        }

        private void BuildGrid()
        {
            _grid = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                Bordered = true,
                VisibleHeader = true,
                EnableHeaderResizing = true,           // 拖动表头只改列宽（不排序，序号与计算一致）
                MultipleRows = true,                   // Shift 多选，配合工具行批量设置
                EditMode = AntdUI.TEditMode.DoubleClick,
                LostFocusClearSelection = false,
                Font = FontHelper.UiFont(9),
                BackColor = Color.Transparent
            };

            // 序号（三态只用颜色与 — 区分，不加文字标签：窄列放不下，状态列已说明）
            var ordCol = new AntdUI.Column("Ordinal", LanguageManager.Instance.GetText("Editor.BatchColOrdinal", "序号")) { Width = DpiManager.S(52).ToString() };
            ordCol.Render = (v, r, i) =>
            {
                if (r is not BatchRow row) return null;
                switch (row.State)
                {
                    case BatchRowState.Skip:
                        return new AntdUI.CellText(row.Ordinal > 0 ? row.Ordinal.ToString() : "—", ThemeManager.TextTertiary);
                    case BatchRowState.Ignore:
                        return new AntdUI.CellText("—", ThemeManager.TextTertiary);
                    default:
                        return new AntdUI.CellText(row.Ordinal > 0 ? row.Ordinal.ToString() : "", ThemeManager.Text);
                }
            };

            // 跳过 / 忽略：两个 ColumnCheck，各自绑定行上的派生属性（互斥由 State 枚举天然成立）
            var skipCol = new AntdUI.ColumnCheck("Skip", LanguageManager.Instance.GetText("Editor.BatchColSkip", "跳过\n算·占号")) { Width = DpiManager.S(62).ToString() };
            var ignoreCol = new AntdUI.ColumnCheck("Ignore", LanguageManager.Instance.GetText("Editor.BatchColIgnore", "忽略\n不算·前移")) { Width = DpiManager.S(62).ToString() };

            var fileCol = new AntdUI.Column("File", LanguageManager.Instance.GetText("Editor.BatchColFile", "文件")) { Width = "fill" };
            var oldCol = new AntdUI.Column("OldValue", LanguageManager.Instance.GetText("Editor.BatchColOld", "旧值")) { Width = DpiManager.S(120).ToString() };
            var newCol = new AntdUI.Column("NewValue", LanguageManager.Instance.GetText("Editor.BatchColNew", "新值")) { Width = DpiManager.S(170).ToString() };
            // 预设/bool 字段：新值单元格附"▼"候选按钮（照 DBR 编辑器单元格 ▼ 的范式，复用 DbrFieldHelper）
            // ⚠️ 非 picklist 分支也必须显式返回 CellText：Render 返回 null 时单元格会定格在布局快照，
            //    之后数据变化只重绘不重建布局 → 新值列"不跟随设定走"的根因
            newCol.Render = (v, r, i) =>
            {
                if (r is not BatchRow row) return null;
                bool isPick = _current != null && _current.FieldIsPicklist.TryGetValue(_fieldName ?? "", out var pk) && pk;
                if (!isPick || row.State != BatchRowState.Normal)
                    return new AntdUI.CellText(row.NewValue ?? "");
                return new AntdUI.ICell[]
                {
                    new AntdUI.CellText(row.NewValue ?? ""),
                    new AntdUI.CellButton("drop_" + row.RelPath, "▼", AntdUI.TTypeMini.Default)
                };
            };

            var statusCol = new AntdUI.Column("Status", LanguageManager.Instance.GetText("Editor.BatchColStatus", "状态")) { Width = DpiManager.S(150).ToString() };
            statusCol.Render = (v, r, i) =>
            {
                if (r is not BatchRow row) return null;
                var cells = new List<AntdUI.ICell>();
                switch (row.State)
                {
                    case BatchRowState.Skip:
                        cells.Add(new AntdUI.CellTag(LanguageManager.Instance.GetText("Editor.BatchStatusSkip", "跳过·不写(影响下一行)"), AntdUI.TTypeMini.Default));
                        break;
                    case BatchRowState.Ignore:
                        cells.Add(new AntdUI.CellTag(LanguageManager.Instance.GetText("Editor.BatchStatusIgnore", "忽略·不计(后续前移)"), AntdUI.TTypeMini.Default));
                        break;
                    default:
                        if (row.HasError)
                            cells.Add(new AntdUI.CellTag(row.IssueText ?? "", AntdUI.TTypeMini.Error));
                        else
                        {
                            if (row.IsManual)
                                cells.Add(new AntdUI.CellTag(LanguageManager.Instance.GetText("Editor.BatchTagManual", "手改"), AntdUI.TTypeMini.Info));
                            if (row.HasWarn)
                                cells.Add(new AntdUI.CellTag(row.IssueText ?? "", AntdUI.TTypeMini.Warn));
                            if (row.IsNewFieldRow)
                                cells.Add(new AntdUI.CellTag(LanguageManager.Instance.GetText("Editor.BatchTagNewRow", "新增行"), AntdUI.TTypeMini.Warn));
                            if (row.IsManual)
                                cells.Add(new AntdUI.CellButton("revert_" + row.RelPath,
                                    LanguageManager.Instance.GetText("Editor.BatchRevertAuto", "↺ 恢复自动"), AntdUI.TTypeMini.Default));
                        }
                        break;
                }
                return cells.Count == 0 ? new AntdUI.CellText("") : cells.ToArray();
            };

            _grid.Columns.Add(new AntdUI.ColumnSort { Fixed = true });   // 拖拽手柄列（拖动行调整值分配顺序）
            _grid.Columns.Add(ordCol);
            _grid.Columns.Add(skipCol);
            _grid.Columns.Add(ignoreCol);
            _grid.Columns.Add(fileCol);
            _grid.Columns.Add(oldCol);
            _grid.Columns.Add(newCol);
            _grid.Columns.Add(statusCol);

            _grid.Binding(_rows);

            // 拖拽排序：拖动行改变「值分配顺序」（序号 / 步进 / 片段 / 分割段位都按新顺序走）。
            // 表格拖拽只改显示层 SortData，故在拖拽结束后按当前显示顺序重算（走 200ms 防抖）。
            _grid.SortRows += (s, e) => ScheduleRecompute();

            // 三态/手改变化：经事件回写模型（互斥与序号重排在模型层完成）
            _grid.CheckedChanged += (s, e) =>
            {
                if (_syncingRows) return;
                if (e.Record is not BatchRow row) return;

                _syncingRows = true;
                try
                {
                    // ⚠️ 必须先同步 _intent.StateByPath 再重算：
                    //    点击时 AntdUI 已写回行模型，但 RecomputeAll 以 _intent 为真源重刷行，
                    //    不同步的话勾选会立刻被重算冲掉（"复选框无效"的根因）
                    if (e.Column != null && e.Column.Key == "Skip")
                    {
                        row.Skip = e.Value;
                        _intent.StateByPath[row.RelPath] = e.Value ? BatchRowState.Skip
                            : (row.State == BatchRowState.Skip ? BatchRowState.Normal : row.State);
                    }
                    else if (e.Column != null && e.Column.Key == "Ignore")
                    {
                        row.Ignore = e.Value;
                        _intent.StateByPath[row.RelPath] = e.Value ? BatchRowState.Ignore
                            : (row.State == BatchRowState.Ignore ? BatchRowState.Normal : row.State);
                    }
                }
                finally { _syncingRows = false; }

                RecomputeAll();   // 忽略会重排序号 → 全量重算；跳过只影响自身与下游 u → 也重算最省心
            };

            // 新值列：仅"正常"行可编辑（跳过/忽略行禁止进入编辑态）
            _grid.CellBeginEdit += (s, e) =>
            {
                if (e.Column == null || e.Column.Key != "NewValue") return true;
                if (e.Record is not BatchRow row) return false;
                return row.State == BatchRowState.Normal;   // 只有正常行允许手改
            };

            // 手改提交：过门禁；阻断则拒绝并回退；通过则记入手改并刷新计数
            _grid.CellEndEdit += (s, e) =>
            {
                if (e.Column == null || e.Column.Key != "NewValue") return true;
                if (e.Record is not BatchRow row) return true;
                return ApplyManualValue(row, e.Value ?? "");
            };

            // 行前景色（三态统一灰化 + 阻断行红字），照 ArzArcEditorForm.cs:4106-4119 的既有范式
            _grid.SetRowStyle += (s, e) =>
            {
                if (e.Record is not BatchRow row) return null;
                if (row.HasError && row.State == BatchRowState.Normal)
                    return new AntdUI.Table.CellStyleInfo { ForeColor = ThemeManager.Modified };      // 阻断：红字
                if (row.State == BatchRowState.Ignore || row.State == BatchRowState.Skip)
                    return new AntdUI.Table.CellStyleInfo { ForeColor = ThemeManager.TextTertiary };  // 灰化
                return null;
            };

            // 手改行的 ↺ 恢复自动（行身份用 Record，不用行号——列表重排后行号会漂移）
            // 预设/bool 字段的 ▼：弹出候选下拉，结果按手改提交（与单元格编辑同一套门禁与登记）
            _grid.CellButtonClick += (s, e) =>
            {
                if (e.Btn == null || e.Btn.Id == null) return;

                if (e.Btn.Id.StartsWith("drop_", StringComparison.Ordinal))
                {
                    string dropPath = e.Btn.Id.Substring("drop_".Length);
                    var dropRow = _rows.FirstOrDefault(x => string.Equals(x.RelPath, dropPath, StringComparison.OrdinalIgnoreCase));
                    if (dropRow == null || _current == null) return;
                    if (!_current.FieldIsPicklist.TryGetValue(_fieldName ?? "", out var pk) || !pk) return;

                    var options = _current.FieldPicklist.TryGetValue(_fieldName ?? "", out var opts)
                        ? opts : Array.Empty<string>();
                    var tmpV = new TemplateVariableInfo
                    {
                        Type = options.Length > 0 ? null : "bool",
                        PicklistOptions = options
                    };
                    string current = _intent.ManualOverrideByPath.TryGetValue(dropPath, out var mv)
                        ? mv : (dropRow.ComputedValue ?? "");
                    var cellPt = _grid.PointToScreen(new Point(e.Rect.X, e.Rect.Bottom));
                    DbrFieldHelper.ShowPicklistDropdown(this, cellPt, tmpV, current, result =>
                    {
                        ApplyManualValue(dropRow, result ?? "");
                    });
                    return;
                }

                if (!e.Btn.Id.StartsWith("revert_", StringComparison.Ordinal)) return;
                string path = e.Btn.Id.Substring("revert_".Length);
                if (_intent.ManualOverrideByPath.Remove(path))
                    RecomputeAll();
            };
        }

        /// <summary>提交一次手改值：过门禁（不过则提示并返回 false）、登记手改、刷新行显示；计数重算延后一拍</summary>
        private bool ApplyManualValue(BatchRow row, string val)
        {
            var issues = DbrValueGuard.Validate(val, BuildGuardContext(row.RelPath));
            if (DbrValueGuard.HasError(issues))
            {
                ShowGateBanner(new List<DbrValueIssue> { DbrValueGuard.Primary(issues) });
                return false;    // 拒绝：单元格回退为原值 / 下拉不改值
            }

            bool sameAsAuto = string.Equals(val, row.ComputedValue, StringComparison.Ordinal);
            if (sameAsAuto) _intent.ManualOverrideByPath.Remove(row.RelPath);
            else _intent.ManualOverrideByPath[row.RelPath] = val;

            row.SetFinal(val, !sameAsAuto, row.IsNewFieldRow,
                DbrValueGuard.HasError(issues), DbrValueGuard.Count(issues, DbrValueSeverity.Warn) > 0,
                DbrValueGuard.Primary(issues)?.Text);

            // 手改影响计数（手改 N / 阻断 0）：延后到编辑回调之外重算，避免在表格编辑态内重排
            BeginInvoke((Action)(() => RecomputeAll()));
            return true;
        }

        /// <summary>按快照建行（每文件一行，顺序 = 视觉列序）；三态 / 序号 / 新值由 RecomputeAll 填充</summary>
        private void BuildRows()
        {
            foreach (var f in _ctx.Files)
            {
                _rows.Add(new BatchRow
                {
                    RelPath = f.RelPath,
                    File = f.DisplayName,
                    OldValue = LanguageManager.Instance.GetText("Editor.BatchOldUnknown", "…")
                });
            }
        }

        /// <summary>构建某文件该字段的门禁上下文（从快照取）</summary>
        private DbrValueContext BuildGuardContext(string relPath)
        {
            var ctx = new DbrValueContext { FieldName = _fieldName };
            if (_current.FieldIsArray.TryGetValue(_fieldName ?? "", out var arr)) ctx.IsArray = arr;
            if (_current.FieldIsPicklist.TryGetValue(_fieldName ?? "", out var pk)) ctx.IsPicklist = pk;

            var snap = _current.Files.FirstOrDefault(f =>
                string.Equals(f.RelPath, relPath, StringComparison.OrdinalIgnoreCase));
            if (snap == null) return ctx;

            ctx.KeyExistsInOrigLines = snap.KeyExists.TryGetValue(_fieldName ?? "", out var ke) && ke;
            ctx.KeyOccurrences = snap.Occurrences.TryGetValue(_fieldName ?? "", out var occ) ? occ : 0;
            return ctx;
        }

        // ───────────── 小工具 ─────────────

        /// <summary>模式单选：定宽 = 半栏宽（两行正好放 4 个）</summary>
        private AntdUI.Radio MakeModeRadio(string text, bool isChecked)
            => new AntdUI.Radio
            {
                Text = text, Checked = isChecked, AutoSize = false,
                Width = DpiManager.S(140), Height = DpiManager.S(26),
                Font = FontHelper.UiFont(9), Margin = new Padding(0)
            };

        private AntdUI.Button MakeListButton(string key, string fallback, EventHandler onClick)
        {
            var btn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText(key, fallback),
                AutoSize = true, MinimumSize = new Size(0, DpiManager.S(26)),
                Font = FontHelper.UiFont(9),
                Type = AntdUI.TTypeMini.Primary    // 主色填充（不设 BackColor，否则覆盖为透明）
            };
            if (onClick != null) btn.Click += onClick;   // ⚠️ 必须挂上：漏了这句按钮就是死的
            return btn;
        }

        /// <summary>打开与 ARZ/ARC 编辑器相同的「生成数组」弹窗，确定后把生成的数组（a;b;c…）填回基准值框</summary>
        private void GenerateBaseArray()
        {
            var dlg = new ArrayEditorDialog(_fieldName ?? "", _txtBase.Text, null);
            if (dlg.ShowDialog(_owner) == DialogResult.OK && !string.IsNullOrEmpty(dlg.Result))
                _txtBase.Text = dlg.Result;   // 触发 TextChanged → 200ms 防抖重算
        }

        /// <summary>一行 = [控件 Fill]（用 PrefixText 自带名称），避免左栏再挤一个标签列</summary>
        private AntdUI.Panel Row(Control ctl)
        {
            var row = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(40), Back = Color.Transparent };
            row.Controls.Add(ctl);
            return row;
        }

        private void ShowFormulaHelp()
        {
            // 公式帮助统一走公共 Markdown 弹窗组件（与「更新日志/关于」同源；内容为内嵌 md 资源）
            MarkdownViewer.Show(this,
                MarkdownViewer.HelpResourceSuffix("!FormulaHelpCN.md", "!FormulaHelpEN.md"),
                "Editor.FormulaHelp");
        }

        /// <summary>Dropdown 选中后同步按钮文字（AntdUI Dropdown 不会自动刷新 Text）</summary>
        private static void SyncDropdownText(AntdUI.Dropdown dd)
        {
            var item = dd.Items.OfType<AntdUI.SelectItem>().FirstOrDefault(x => Equals(x.Tag, dd.SelectedValue));
            if (item != null) dd.Text = item.Text;
        }

        /// <summary>分割方式的输入行显隐（按符号 → 显示符号；按长度 → 显示长度；按换行 → 都不显示），
        /// 面板高度随之调整（隐藏的行不占位）</summary>
        private void UpdateSplitRows()
        {
            if (_splitPanel == null) return;
            bool sym = _radioSplitSymbol.Checked;
            bool len = _radioSplitLength.Checked;
            if (_splitSymbolRow != null) _splitSymbolRow.Visible = sym;
            if (_splitLengthRow != null) _splitLengthRow.Visible = len;
            // 32(内容按钮) + 30(方式单选) + 34(循环复用) = 96 起，符号/长度各占 36
            _splitPanel.Height = DpiManager.S(96 + (sym ? 36 : 0) + (len ? 36 : 0));
        }

        /// <summary>当前分割方式（按换行 / 按符号 / 按长度）</summary>
        private string CurrentSplitMethod()
        {
            if (_radioSplitSymbol.Checked) return "Symbol";
            if (_radioSplitLength.Checked) return "Length";
            return "Newline";
        }

        /// <summary>模式子面板的显隐：步进（单数/数组）显示公式区，固定分割显示分割区；生成数组仅数组步进可用</summary>
        private void UpdateModePanels()
        {
            bool step = CurrentMode() is BatchMode.Step or BatchMode.ArrayStep;
            if (_stepPanel != null) _stepPanel.Visible = step;
            if (_btnGenArray != null) _btnGenArray.Enabled = CurrentMode() == BatchMode.ArrayStep;
            if (_splitPanel != null)
            {
                _splitPanel.Visible = CurrentMode() == BatchMode.Split;
                // 首次切到固定分割：分割内容预填基准值（常见诉求：把基准值切开分给各文件）
                if (CurrentMode() == BatchMode.Split && string.IsNullOrEmpty(_txtSplitContent.Text))
                    _txtSplitContent.Text = GetBaseValueRaw(_fieldName);
                UpdateSplitRows();
            }
        }

        /// <summary>打开插值片段编辑弹窗：每次新建输入控件（真源控件不进 Modal，避免关闭时被 Dispose），
        /// 编辑过程中实时同步回真源并重算，右侧表格即时刷新</summary>
        private void ShowFragmentDialog()
        {
            var panel = new AntdUI.Panel { Size = new Size(DpiManager.S(560), DpiManager.S(400)), BackColor = Color.Transparent };

            // ① 启用开关：不开启不参与插值，也不显示下面的设置
            var swRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(36), Back = Color.Transparent };
            var sw = new AntdUI.Switch
            {
                Checked = _swFragmentEnabled.Checked,
                Width = DpiManager.S(44), Dock = DockStyle.Left
            };
            var swLabel = new AntdUI.Label
            {
                Text = LanguageManager.Instance.GetText("Editor.BatchFragmentEnable", "启用插值片段"),
                Dock = DockStyle.Left, AutoSize = true, Font = FontHelper.UiFont(9),
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent,
                Padding = new Padding(DpiManager.S(8), DpiManager.S(9), 0, 0)
            };
            swRow.Controls.Add(swLabel);
            swRow.Controls.Add(sw);

            // ② 片段编辑器：表格 + 行拖拽排序
            //    列 = [拖拽] [不插] [替换] [前插] [后插] [内容]；每行单选（派生 bool 天然互斥）
            //    「拖拽」列用 AntdUI 的 ColumnSort 类型 —— 它就是行拖动把手，拖动即整行换序
            var body = new AntdUI.Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Visible = _swFragmentEnabled.Checked };
            var fragRows = new BindingList<FragRow>();
            foreach (var frag in ParseFragmentLines(_txtFragments.Text))
                fragRows.Add(new FragRow { Op = frag.Op, Text = frag.Text ?? "" });

            var table = new AntdUI.Table
            {
                Dock = DockStyle.Fill, Bordered = true, VisibleHeader = true,
                EditMode = AntdUI.TEditMode.DoubleClick,
                MultipleRows = true, LostFocusClearSelection = false,
                Font = FontHelper.UiFont(9), BackColor = Color.Transparent
            };
            var dragCol = new AntdUI.ColumnSort { Width = DpiManager.S(42).ToString() };
            dragCol.Title = LanguageManager.Instance.GetText("Editor.BatchFragmentColDrag", "拖拽");
            table.Columns.Add(dragCol);
            table.Columns.Add(new AntdUI.ColumnRadio("None", LanguageManager.Instance.GetText("Editor.BatchFragmentOpNone", "不插")) { Width = DpiManager.S(56).ToString() });
            table.Columns.Add(new AntdUI.ColumnRadio("Replace", LanguageManager.Instance.GetText("Editor.BatchFragmentOpReplace", "替换")) { Width = DpiManager.S(56).ToString() });
            table.Columns.Add(new AntdUI.ColumnRadio("Prepend", LanguageManager.Instance.GetText("Editor.BatchFragmentOpPrepend", "前插")) { Width = DpiManager.S(56).ToString() });
            table.Columns.Add(new AntdUI.ColumnRadio("Append", LanguageManager.Instance.GetText("Editor.BatchFragmentOpAppend", "后插")) { Width = DpiManager.S(56).ToString() });
            table.Columns.Add(new AntdUI.Column("Text", LanguageManager.Instance.GetText("Editor.BatchFragmentColText", "内容")) { Width = "fill" });
            table.Binding(fragRows);

            void SyncText()
            {
                _txtFragments.Text = SerializeFragments(fragRows.Select(r => new BatchFragment { Op = r.Op, Text = r.Text }));
                ScheduleRecompute();
            }

            table.CheckedChanged += (s, e) =>
            {
                if (e.Record is not FragRow row || e.Column == null) return;
                switch (e.Column.Key)
                {
                    case "None": row.None = e.Value; break;
                    case "Replace": row.Replace = e.Value; break;
                    case "Prepend": row.Prepend = e.Value; break;
                    case "Append": row.Append = e.Value; break;
                    default: return;
                }
                SyncText();
            };
            table.CellEndEdit += (s, e) =>
            {
                if (e.Column == null || e.Column.Key != "Text") return true;
                if (e.Record is FragRow row) { row.Text = e.Value ?? ""; SyncText(); }
                return true;
            };

            // 行拖拽排序：松手后按表格给出的视觉顺序重排模型，再整体重绑（清空表格内部排序映射）
            table.SortRows += (s, e) =>
            {
                int[] order = table.SortIndex();
                if (order == null || order.Length != fragRows.Count) return;
                var reordered = order.Where(i => i >= 0 && i < fragRows.Count).Select(i => fragRows[i]).ToList();
                if (reordered.Count != fragRows.Count) return;
                BeginInvoke((Action)(() =>
                {
                    var fresh = new BindingList<FragRow>(reordered);
                    table.Binding(fresh);
                    fragRows = fresh;
                    SyncText();
                }));
            };

            // 表格外：添加一行 / 删除选中行
            var barRow = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(34), Back = Color.Transparent };
            var barFlow = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Color.Transparent,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false
            };
            var addRowBtn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Editor.BatchFragmentAddRow", "添加一行"),
                AutoSize = true, MinimumSize = new Size(0, DpiManager.S(26)),
                Font = FontHelper.UiFont(9), Type = AntdUI.TTypeMini.Primary
            };
            addRowBtn.Click += (s, e) =>
            {
                fragRows.Add(new FragRow { Op = BatchFragmentOp.Keep, Text = "" });
                SyncText();
            };
            var delRowBtn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Editor.BatchFragmentDelRow", "删除选中行"),
                AutoSize = true, MinimumSize = new Size(0, DpiManager.S(26)),
                Font = FontHelper.UiFont(9), Type = AntdUI.TTypeMini.Primary
            };
            delRowBtn.Click += (s, e) =>
            {
                var sel = table.SelectedIndexsReal() ?? Array.Empty<int>();
                if (sel.Length == 0) return;
                // SelectedIndexsReal 返回 1 基（表头占 0），转为 0 基模型下标
                foreach (int idx in sel.Where(x => x >= 1).Select(x => x - 1).Distinct().OrderByDescending(x => x))
                    if (idx >= 0 && idx < fragRows.Count) fragRows.RemoveAt(idx);
                SyncText();
            };
            barFlow.Controls.Add(addRowBtn);
            barFlow.Controls.Add(delRowBtn);
            barRow.Controls.Add(barFlow);

            // 循环次数：留空 = 无限；填 N = 整套片段最多轮 N 次
            var roundsRow = new AntdUI.Panel { Dock = DockStyle.Bottom, Height = DpiManager.S(36), Back = Color.Transparent };
            var roundsInput = new AntdUI.Input
            {
                Dock = DockStyle.Fill, Font = FontHelper.UiFont(9),
                Text = _fragmentRounds ?? "",
                PrefixText = LanguageManager.Instance.GetText("Editor.BatchFragmentRounds", "循环次数："),
                PlaceholderText = LanguageManager.Instance.GetText("Editor.BatchFragmentRoundsHint", "留空=无限")
            };
            roundsInput.TextChanged += (s, e) =>
            {
                _fragmentRounds = roundsInput.Text ?? "";
                ScheduleRecompute();
            };
            roundsRow.Controls.Add(roundsInput);

            body.Controls.Add(table);        // Fill
            body.Controls.Add(roundsRow);    // Bottom
            body.Controls.Add(barRow);       // Top（后加先处理 → 置顶）

            sw.CheckedChanged += (s, e) =>
            {
                _swFragmentEnabled.Checked = sw.Checked;
                body.Visible = sw.Checked;
                RecomputeAll();
            };

            panel.Controls.Add(body);
            panel.Controls.Add(swRow);

            var cfg = new AntdUI.Modal.Config(_owner,
                LanguageManager.Instance.GetText("Editor.BatchFragmentTitle", "插值片段设置"), panel)
            {
                OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                OkType = AntdUI.TTypeMini.Primary,
                MaskClosable = false,
                Draggable = true,
                OnOk = c => true               // 编辑中已实时同步，确定仅关窗
            };
            AntdUI.Modal.open(cfg);
        }

        /// <summary>插值片段单行模型：操作用枚举真源 + 四个派生 bool（四个单选列天然互斥）</summary>
        private sealed class FragRow : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;
            internal BatchFragmentOp Op = BatchFragmentOp.Keep;
            private string _text = "";

            public string Text
            {
                get => _text;
                set { if (_text == value) return; _text = value; Notify(nameof(Text)); }
            }
            public bool None
            {
                get => Op == BatchFragmentOp.Keep;
                set { if (value) SetOp(BatchFragmentOp.Keep); }
            }
            public bool Replace
            {
                get => Op == BatchFragmentOp.Replace;
                set { if (value) SetOp(BatchFragmentOp.Replace); }
            }
            public bool Prepend
            {
                get => Op == BatchFragmentOp.Prepend;
                set { if (value) SetOp(BatchFragmentOp.Prepend); }
            }
            public bool Append
            {
                get => Op == BatchFragmentOp.Append;
                set { if (value) SetOp(BatchFragmentOp.Append); }
            }

            private void SetOp(BatchFragmentOp op)
            {
                if (Op == op) return;
                Op = op;
                Notify(nameof(None));
                Notify(nameof(Replace));
                Notify(nameof(Prepend));
                Notify(nameof(Append));
            }

            private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>片段序列 → 多行文本（持久化格式：* 不插 / ^ 前插 / $ 后插 / = 替换）</summary>
        private static string SerializeFragments(IEnumerable<BatchFragment> frags)
        {
            var lines = new List<string>();
            foreach (var f in frags ?? Enumerable.Empty<BatchFragment>())
            {
                if (f == null) continue;
                switch (f.Op)
                {
                    case BatchFragmentOp.Prepend: lines.Add("^" + (f.Text ?? "")); break;
                    case BatchFragmentOp.Append: lines.Add("$" + (f.Text ?? "")); break;
                    case BatchFragmentOp.Replace: lines.Add("=" + (f.Text ?? "")); break;
                    default: lines.Add("*"); break;    // 不插
                }
            }
            return string.Join("\r\n", lines);
        }

        /// <summary>打开分割内容编辑弹窗：Scintilla 多行编辑（带行号、跟随明暗主题、不着色），实时同步重算</summary>
        private void ShowSplitContentDialog()
        {
            var panel = new AntdUI.Panel { Size = new Size(DpiManager.S(560), DpiManager.S(420)), BackColor = Color.Transparent };

            var editor = new ScintillaNET.Scintilla
            {
                Dock = DockStyle.Fill,
                TabWidth = 4,
                UseTabs = false,
                WrapMode = ScintillaNET.WrapMode.Word,   // 自动换行
                ScrollWidth = 1,
                ScrollWidthTracking = true,
                Technology = ScintillaNET.Technology.Default,   // GDI 渲染（定稿）：DirectWrite 模式下缺字形的字体回退常落日文字形，GDI 走字体链接回退宋体无此问题
                FontQuality = ScintillaNET.FontQuality.LcdOptimized,   // ClearType 抗锯齿：GDI 下的最佳清晰度（仅 GDI 生效，DirectWrite 模式忽略此项）
                HScrollBar = false,                      // 既然换行，横向滚动条无意义
                VScrollBar = true
            };
            editor.Margins[0].Width = DpiManager.S(44);   // 显示行号（宽度必须非 0，否则 SetLineNumber 不动它）
            ScintillaSyntax.ApplyTheme(editor, FontHelper.MonoFontName, (int)(9f * DpiManager.Scale));
            editor.Text = _txtSplitContent.Text ?? "";
            editor.TextChanged += (s, e) =>
            {
                _txtSplitContent.Text = editor.Text;   // 同步真源
                ScheduleRecompute();
            };
            panel.Controls.Add(editor);

            var cfg = new AntdUI.Modal.Config(_owner,
                LanguageManager.Instance.GetText("Editor.BatchSplitContentTitle", "分割内容"), panel)
            {
                OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                OkType = AntdUI.TTypeMini.Primary,
                MaskClosable = false,
                Draggable = true,
                OnOk = c => true               // 编辑中已实时同步，确定仅关窗
            };
            AntdUI.Modal.open(cfg);
        }

        /// <summary>片段编辑器的语法说明</summary>
        /// <summary>解析片段文本 → 片段序列（* 不插 / ^前插 / $后插 / =替换；空行按不插处理）</summary>
        private static List<BatchFragment> ParseFragmentLines(string text)
        {
            var list = new List<BatchFragment>();
            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line == "*") { list.Add(new BatchFragment { Op = BatchFragmentOp.Keep }); continue; }
                if (line.StartsWith("^")) { list.Add(new BatchFragment { Op = BatchFragmentOp.Prepend, Text = line.Substring(1) }); continue; }
                if (line.StartsWith("$")) { list.Add(new BatchFragment { Op = BatchFragmentOp.Append, Text = line.Substring(1) }); continue; }
                if (line.StartsWith("=")) { list.Add(new BatchFragment { Op = BatchFragmentOp.Replace, Text = line.Substring(1) }); continue; }
                list.Add(new BatchFragment { Op = BatchFragmentOp.Prepend, Text = line });
            }
            return list;
        }

        /// <summary>按意图计算分割段序列（符号=字面量切；换行=兼容 \r\n \n \r 且**保留空行**（空行=空值段，勿需输入空格）；
        /// 定长=去换行按字符切、去空白、忽略空段）</summary>
        private static List<string> ComputeSegments(BatchIntent intent)
        {
            var segs = new List<string>();
            string content = intent.SplitContent ?? "";
            switch (intent.SplitMethod)
            {
                case "Newline":
                    if (content.Length > 0)
                        segs.AddRange(content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None));
                    break;
                case "Length":
                    int len = Math.Max(1, intent.SplitLength);
                    string flat = content.Replace("\r", "").Replace("\n", "");
                    for (int i = 0; i < flat.Length; i += len)
                    {
                        string seg = flat.Substring(i, Math.Min(len, flat.Length - i)).Trim();
                        if (seg.Length > 0) segs.Add(seg);
                    }
                    break;
                default:    // Symbol：按字面量符号切（取第一个字符；空符号时整个内容作为一段）
                    char sym = (intent.SplitSymbol ?? "").Length > 0 ? intent.SplitSymbol[0] : '\0';
                    if (sym == '\0') { if (content.Length > 0) segs.Add(content); }
                    else segs.AddRange(content.Split(new[] { sym }, StringSplitOptions.RemoveEmptyEntries));
                    break;
            }
            return segs;
        }

        // ───────────── 参数预设 ─────────────

        /// <summary>把当前弹窗参数读成一个预设对象（不含量与三态/手改）</summary>
        private BatchEditPreset ReadPresetFromUi(string name)
        {
            return new BatchEditPreset
            {
                Name = name,
                Mode = (int)CurrentMode(),
                Formula = _txtFormula.Text ?? "",
                Prefix = _txtPrefix.Text ?? "",
                Suffix = _txtSuffix.Text ?? "",
                Pad = (int)_nudPad.Value,
                FragmentText = _txtFragments.Text ?? "",
                FragmentEnabled = _swFragmentEnabled.Checked,
                FragmentRounds = _fragmentRounds ?? "",
                SplitMethod = CurrentSplitMethod(),
                SplitSymbol = _txtSplitSymbol.Text ?? "/",
                SplitContent = _txtSplitContent.Text ?? "",
                SplitLength = (int)_nudSplitLength.Value,
                SplitLoop = _chkSplitLoop.Checked
            };
        }

        private BatchMode CurrentMode()
        {
            if (_radioStep.Checked) return BatchMode.Step;
            if (_radioArrayStep.Checked) return BatchMode.ArrayStep;
            if (_radioSplit.Checked) return BatchMode.Split;
            return BatchMode.AllSame;
        }

        private void ReloadPresetList(string selectName = null)
        {
            _suspendRecompute = true;
            _selPreset.Items.Clear();
            foreach (var p in SettingsManager.BatchEditPresets)
                _selPreset.Items.Add(new AntdUI.SelectItem(p.Name, p.Name));
            if (selectName != null) _selPreset.SelectedValue = selectName;
            _suspendRecompute = false;
        }

        private void ApplyPreset(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var p = SettingsManager.BatchEditPresets.FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p == null) return;

            _suspendRecompute = true;
            try
            {
                switch ((BatchMode)Math.Max(0, Math.Min(3, p.Mode)))
                {
                    case BatchMode.Step: _radioStep.Checked = true; break;
                    case BatchMode.ArrayStep: _radioArrayStep.Checked = true; break;
                    case BatchMode.Split: _radioSplit.Checked = true; break;
                    default: _radioAllSame.Checked = true; break;
                }
                _txtFormula.Text = p.Formula ?? "";
                _txtPrefix.Text = p.Prefix ?? "";
                _txtSuffix.Text = p.Suffix ?? "";
                _nudPad.Value = Math.Max(0, Math.Min(12, p.Pad));
                _txtFragments.Text = p.FragmentText ?? "";
                _swFragmentEnabled.Checked = p.FragmentEnabled;
                _fragmentRounds = p.FragmentRounds ?? "";
                switch (p.SplitMethod)
                {
                    case "Symbol": _radioSplitSymbol.Checked = true; break;
                    case "Length": _radioSplitLength.Checked = true; break;
                    default: _radioSplitNewline.Checked = true; break;
                }
                _txtSplitSymbol.Text = string.IsNullOrEmpty(p.SplitSymbol) ? "/" : p.SplitSymbol;
                _txtSplitContent.Text = p.SplitContent ?? "";
                _nudSplitLength.Value = Math.Max(1, Math.Min(4096, p.SplitLength));
                _chkSplitLoop.Checked = p.SplitLoop;
                UpdateModePanels();
            }
            finally { _suspendRecompute = false; }
            RecomputeAll();
        }

        private void SavePresetDialog()
        {
            var panel = new AntdUI.Panel { Size = new Size(DpiManager.S(320), DpiManager.S(80)), BackColor = Color.Transparent };
            var input = new AntdUI.Input
            {
                Dock = DockStyle.Fill, Font = FontHelper.UiFont(9),
                PrefixText = LanguageManager.Instance.GetText("Editor.BatchPresetName", "名称")
            };
            panel.Controls.Add(input);

            var cfg = new AntdUI.Modal.Config(_owner,
                LanguageManager.Instance.GetText("Editor.BatchPresetSaveTitle", "保存参数预设"), panel)
            {
                OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                OkType = AntdUI.TTypeMini.Primary,
                MaskClosable = false,
                Draggable = true,
                OnOk = c =>
                {
                    string name = (input.Text ?? "").Trim();
                    if (name.Length == 0)
                    {
                        MessageBox.Show(this,
                            LanguageManager.Instance.GetText("Editor.BatchPresetNameRequired", "请输入预设名称"),
                            LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                    var preset = ReadPresetFromUi(name);
                    SettingsManager.BatchEditPresets.RemoveAll(x =>
                        string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    SettingsManager.BatchEditPresets.Add(preset);
                    if (SettingsManager.BatchEditPresets.Count > 30)
                        SettingsManager.BatchEditPresets.RemoveAt(0);   // 上限 30，挤掉最旧的
                    SettingsManager.Save();
                    ReloadPresetList(name);
                    return true;
                }
            };
            AntdUI.Modal.open(cfg);
        }

        private void DeletePreset()
        {
            string name = _selPreset.SelectedValue as string;
            if (string.IsNullOrEmpty(name)) return;
            var preset = SettingsManager.BatchEditPresets.FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset == null) return;
            SettingsManager.BatchEditPresets.Remove(preset);
            SettingsManager.Save();
            ReloadPresetList();
        }

        // ───────────── 复制预览（全量 TSV） ─────────────

        /// <summary>导出全量 TSV（序号/状态/文件路径/旧值/自动算值/将写入/门禁说明），带体积上限保护</summary>
        private void CopyPreviewToClipboard()
        {
            const int MaxRows = 5000;
            var plan = _lastPlan;
            if (plan == null) return;

            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\t",
                LanguageManager.Instance.GetText("Editor.BatchColOrdinal", "序号"),
                LanguageManager.Instance.GetText("Editor.BatchColStatus", "状态"),
                LanguageManager.Instance.GetText("Editor.BatchColFile", "文件"),
                LanguageManager.Instance.GetText("Editor.BatchColOld", "旧值"),
                LanguageManager.Instance.GetText("Editor.BatchColAuto", "自动算值"),
                LanguageManager.Instance.GetText("Editor.BatchColNew", "新值"),
                LanguageManager.Instance.GetText("Editor.BatchColGate", "门禁说明")));

            int exported = 0, truncated = 0;
            foreach (var row in _rows)
            {
                if (exported >= MaxRows) { truncated++; continue; }
                var edit = plan.Edits.FirstOrDefault(x => string.Equals(x.RelPath, row.RelPath, StringComparison.OrdinalIgnoreCase));
                var issue = plan.Issues.FirstOrDefault(x => string.Equals(x.Edit.RelPath, row.RelPath, StringComparison.OrdinalIgnoreCase));
                string stateText = row.State == BatchRowState.Skip
                    ? LanguageManager.Instance.GetText("Editor.BatchStatusSkip", "跳过·不写(影响下一行)")
                    : row.State == BatchRowState.Ignore
                    ? LanguageManager.Instance.GetText("Editor.BatchStatusIgnore", "忽略·不计(后续前移)")
                    : LanguageManager.Instance.GetText("Editor.BatchStatusNormal", "正常");
                sb.AppendLine(string.Join("\t",
                    row.Ordinal > 0 ? row.Ordinal.ToString() : "",
                    stateText,
                    row.RelPath,
                    row.OldValue ?? "",
                    row.AutoValue ?? "",
                    row.NewValue ?? "",
                    issue != null ? (DbrValueGuard.Primary(issue.Issues)?.Text ?? "") : ""));
                exported++;
            }

            try
            {
                Clipboard.SetText(sb.ToString());
                MessageBox.Show(this,
                    string.Format(LanguageManager.Instance.GetText("Editor.BatchCopyDone",
                        "已复制 {0} 行到剪贴板") + (truncated > 0
                        ? string.Format(LanguageManager.Instance.GetText("Editor.BatchCopyTruncated", "（超出上限，{0} 行未复制）"), truncated)
                        : ""), exported),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message,
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ───────────── 三态批量设置（工具行，按范围生效） ─────────────

        /// <summary>把状态作用于全部行（真源是 _intent.StateByPath，改完统一重算刷行）</summary>
        private void ApplyStateByScope(BatchRowState state, bool onlySkip = false, bool onlyIgnore = false)
        {
            if (_rows.Count == 0) return;

            foreach (var row in _rows)
            {
                BatchRowState newState;
                switch (state)
                {
                    case BatchRowState.Normal:
                        if (onlySkip && row.State != BatchRowState.Skip) continue;
                        if (onlyIgnore && row.State != BatchRowState.Ignore) continue;
                        newState = BatchRowState.Normal;
                        break;
                    case BatchRowState.Skip:
                        if (row.State == BatchRowState.Skip) continue;
                        newState = BatchRowState.Skip;
                        break;
                    default:
                        if (row.State == BatchRowState.Ignore) continue;
                        newState = BatchRowState.Ignore;
                        break;
                }
                _intent.StateByPath[row.RelPath] = newState;
            }
            RecomputeAll();
        }

        /// <summary>全部跳过/忽略：先确认（影响面大：不写入 / 序号重排）</summary>
        private void ApplyStateByScopeWithConfirm(BatchRowState state)
        {
            string text = state == BatchRowState.Skip
                ? string.Format(LanguageManager.Instance.GetText("Editor.BatchConfirmAllSkip",
                    "将全部 {0} 个文件设为「跳过」：这些文件不会有任何写入，确定继续？"), _rows.Count)
                : string.Format(LanguageManager.Instance.GetText("Editor.BatchConfirmAllIgnore",
                    "将全部 {0} 个文件设为「忽略」：不会写入，且后续序号将不再计入这些文件，确定继续？"), _rows.Count);
            if (MessageBox.Show(this, text,
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Confirm", "确认"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            ApplyStateByScope(state);
        }

        /// <summary>清掉全部手改（各文件回到自动算值）</summary>
        private void ClearManualOverrides()
        {
            if (_intent.ManualOverrideByPath.Count == 0) return;
            _intent.ManualOverrideByPath.Clear();
            RecomputeAll();
        }

        // ───────────── 重算与计划 ─────────────

        /// <summary>当前字段在基准文件上的现值（作为"基准值[u]"的预填来源；计算用输入框实时值）</summary>
        private string GetBaseValueRaw(string fieldName)
        {
            var baseSnap = _current.Files.FirstOrDefault(f =>
                string.Equals(f.RelPath, _current.BasePath, StringComparison.OrdinalIgnoreCase));
            if (baseSnap != null && baseSnap.Values.TryGetValue(fieldName ?? "", out var v)) return v ?? "";
            return "";
        }

        /// <summary>四舍五入并钳位到 [min, max]（InputNumber 实时文本转整数用）</summary>
        private static int Clamp(double v, int min, int max)
        {
            int n = (int)Math.Round(v, MidpointRounding.AwayFromZero);
            return n < min ? min : (n > max ? max : n);
        }

        /// <summary>
        /// 当前表格显示顺序（拖拽后的 RelPath 序列）；表格没有排序数据时返回 null（调用方回退快照顺序）。
        /// 拖拽只改表格显示层，值分配顺序以这里读到的为准。
        /// </summary>
        private List<string> CurrentDisplayOrder()
        {
            try
            {
                var list = _grid?.SortList();
                if (list == null || list.Length == 0) return null;
                var order = new List<string>(list.Length);
                foreach (var rec in list)
                    if (rec is BatchRow r && !string.IsNullOrEmpty(r.RelPath)) order.Add(r.RelPath);
                return order.Count > 0 ? order : null;
            }
            catch { return null; }
        }

        /// <summary>按显示顺序排列文件快照；order 为 null 时原样返回，未列入的文件追加到末尾（兜底不丢文件）</summary>
        private static List<BatchFileSnap> OrderFiles(List<BatchFileSnap> files, List<string> order)
        {
            if (order == null || order.Count == 0 || files == null || files.Count == 0) return files;

            var map = new Dictionary<string, BatchFileSnap>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) map[f.RelPath] = f;   // 不抛异常：重复键以最后一个为准

            var result = new List<BatchFileSnap>(files.Count);
            foreach (var path in order)
                if (map.TryGetValue(path, out var f)) { result.Add(f); map.Remove(path); }
            foreach (var f in files)
                if (map.ContainsKey(f.RelPath)) result.Add(f);
            return result;
        }

        /// <summary>
        /// 当前意图的写入计划（纯计算；不改 UI、不读全局）。
        /// order = 弹窗表格的显示顺序（用户可拖拽调整）；null 时按快照顺序。
        /// </summary>
        private BatchPlan ComputePlan(BatchEditContext snap, BatchIntent intent, string fieldName, List<string> order = null)
        {
            var plan = new BatchPlan();
            plan.Counters.Total = snap.Files.Count;

            // ⚠️ 基准值以弹窗输入框的实时内容为准（intent），不是快照——
            //    否则用户改了基准值，提示区还在报"基准值不是数值"
            string baseValueRaw = intent.BaseValueRaw ?? "";

            // ── 模式前置条件（整体性阻断：一旦触发，整批不写）──
            DbrArrayFormula.StepResult step = null;
            string[] baseArray = null;
            double[] arrayU = null;     // 数组步进的递推游标：上一个参与文件的整个数组

            if (intent.Mode == BatchMode.Step || intent.Mode == BatchMode.ArrayStep)
            {
                string formula = (intent.Formula ?? "").Trim();
                // ⚠️ 不再默认 u+n：无公式就不计算（默认值会静默算出用户没预期的结果）
                if (string.IsNullOrEmpty(formula))
                {
                    plan.PrimaryBlockedText = LanguageManager.Instance.GetText("Editor.BatchFormulaRequired",
                        "公式为空，无法计算");
                    plan.Counters.Blocked = snap.Files.Count;
                    return plan;
                }

                if (intent.Mode == BatchMode.Step)
                {
                    if (!DbrArrayFormula.TryParsePlainNumber(baseValueRaw, out double baseNum))
                    {
                        plan.PrimaryBlockedText = LanguageManager.Instance.GetText("Editor.BatchNeedNumericBase",
                            "单数步进要求基准值为单个数值（当前不是）");
                        plan.Counters.Blocked = snap.Files.Count;
                        return plan;
                    }
                    int participants = snap.Files.Count(f => !IsIgnore(intent, f.RelPath));
                    step = DbrArrayFormula.GenerateStep(formula.Replace("^", "**"), baseNum, participants,
                        intent.Pad);
                    if (step.FormulaInvalid)
                    {
                        plan.PrimaryBlockedText = LanguageManager.Instance.GetText("Editor.BatchFormulaInvalid", "公式无效");
                        plan.Counters.Blocked = snap.Files.Count;
                        return plan;
                    }
                }
                else
                {
                    // 数组步进：基准必须是数组，且每个元素都是普通数值
                    baseArray = (baseValueRaw ?? "").Split(';')
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .ToArray();
                    if (baseArray.Length == 0 || baseArray.Any(x => !DbrArrayFormula.IsPlainNumber(x)))
                    {
                        plan.PrimaryBlockedText = LanguageManager.Instance.GetText("Editor.BatchNeedNumericArrayBase",
                            "数组步进要求基准为数组，且每个元素都是普通数值（如 1;2;3）");
                        plan.Counters.Blocked = snap.Files.Count;
                        return plan;
                    }
                    // 公式整体有效性先用一次试探求值判定（元素级异常 → 单文件跳过，见下方循环）
                    try { DbrArrayFormula.EvalStep(formula.Replace("^", "**"), 1, 1); }
                    catch
                    {
                        plan.PrimaryBlockedText = LanguageManager.Instance.GetText("Editor.BatchFormulaInvalid", "公式无效");
                        plan.Counters.Blocked = snap.Files.Count;
                        return plan;
                    }
                    arrayU = baseArray.Select(x => DbrArrayFormula.ParseDouble(x, 0)).ToArray();
                }
            }

            // 固定分割：段序列一次性算好；为空 → 整批阻断
            List<string> segments = null;
            if (intent.Mode == BatchMode.Split)
            {
                segments = ComputeSegments(intent);
                if (segments.Count == 0)
                {
                    plan.PrimaryBlockedText = LanguageManager.Instance.GetText("Editor.BatchSplitEmpty",
                        "分割内容为空或没有有效段");
                    plan.Counters.Blocked = snap.Files.Count;
                    return plan;
                }
            }

            var fragments = intent.Fragments ?? new List<BatchFragment>();
            // 循环次数：留空（或非正整数）= 无限；填 N = 整套片段最多轮 N 次
            int fragRounds = -1;
            if (!string.IsNullOrWhiteSpace(intent.FragmentRounds)
                && int.TryParse(intent.FragmentRounds.Trim(), out int parsedRounds) && parsedRounds > 0)
                fragRounds = parsedRounds;

            // ── 数组段数对比的基准：基准文件该字段的现值段数（仅数组字段；-1 = 非数组或未知）──
            bool fieldIsArray = snap.FieldIsArray.TryGetValue(fieldName ?? "", out var _fia) && _fia;
            int baseArrayLen = -1;
            if (fieldIsArray)
            {
                var baseSnap = snap.Files.FirstOrDefault(x =>
                    string.Equals(x.RelPath, snap.BasePath, StringComparison.OrdinalIgnoreCase));
                if (baseSnap != null && baseSnap.Values.TryGetValue(fieldName ?? "", out var bv)
                    && !string.IsNullOrWhiteSpace(bv))
                    baseArrayLen = bv.Split(';').Count(x => x.Trim().Length > 0);
            }

            // ── 逐文件（按弹窗表格显示顺序，可拖拽调整；无 order 时按快照顺序 = 编辑器视觉列序）；
            //    统一游标：序号 / 片段位 / 分割段位，只对"参与序号的文件"递进 ──
            int ordinal = 0;
            int stepIdx = -1;
            int fragIdx = -1;
            int segIdx = -1;
            var counters = plan.Counters;
            int warnCount = 0;
            bool segLooped = false;

            foreach (var f in OrderFiles(snap.Files, order))
            {
                var state = intent.StateByPath.TryGetValue(f.RelPath, out var st) ? st : BatchRowState.Normal;

                // 忽略：不参与（不占号、不算、不写、不吃片段位/分割段位）
                if (state == BatchRowState.Ignore)
                {
                    counters.Ignore++;
                    plan.Skips.Add(new PlannedSkip { RelPath = f.RelPath, State = BatchRowState.Ignore, AutoValue = null });
                    continue;
                }

                ordinal++;                        // 参与序号：正常/跳过
                int n = ordinal;
                if (intent.Mode == BatchMode.Step) stepIdx++;
                if (fragments.Count > 0) fragIdx++;
                if (intent.Mode == BatchMode.Split) segIdx++;

                f.Values.TryGetValue(fieldName ?? "", out var oldVal);
                oldVal ??= "";

                string autoValue;
                bool invalid = false;

                switch (intent.Mode)
                {
                    case BatchMode.Step:
                        autoValue = (stepIdx >= 0 && stepIdx < step.Values.Count) ? step.Values[stepIdx] : null;
                        if (autoValue == null) invalid = true;    // 除零/无实数解 → 该文件跳过不写（计入数值异常）
                        break;

                    case BatchMode.ArrayStep:
                    {
                        // 整体递推（与单数步进的公共实现不同，数组独立处理）：
                        //   第 1 个参与文件 = 基准数组本身（保持不变）；
                        //   第 n 个文件：每个元素 = eval(公式, u = 上一个参与文件的同位元素, n = 序号)。
                        //   u 随之推进为本次结果（取整后的值），任一元素无效 → 整文件跳过且 u 不推进。
                        //   例：基准 1;2;3;4;5，公式 u+1 → 1;2;3;4;5 / 2;3;4;5;6 / 3;4;5;6;7
                        if (n == 1)
                        {
                            autoValue = string.Join(";", baseArray);   // 基准原样（不重排格式，避免基准文件被误标修改）
                        }
                        else
                        {
                            string formula = (intent.Formula ?? "").Trim().Replace("^", "**");   // 前置已保证非空
                            var outElems = new List<string>(arrayU.Length);
                            var nextU = new List<double>(arrayU.Length);
                            bool fileInvalid = false;
                            foreach (var uEl in arrayU)
                            {
                                double val;
                                try { val = DbrArrayFormula.EvalStep(formula, uEl, n); }
                                catch { fileInvalid = true; break; }
                                if (double.IsNaN(val) || double.IsInfinity(val)) { fileInvalid = true; break; }
                                nextU.Add(val);   // 精确递推：u 用原始计算值，取整交给公式
                                outElems.Add(DbrArrayFormula.FormatValue(val, intent.Pad));
                            }
                            if (fileInvalid) { invalid = true; autoValue = null; }   // 一个元素无效 → 整文件跳过（§16.2）
                            else
                            {
                                autoValue = string.Join(";", outElems);
                                arrayU = nextU.ToArray();
                            }
                        }
                        break;
                    }

                    case BatchMode.Split:
                    {
                        if (segIdx >= segments.Count)
                        {
                            if (intent.SplitLoop) { segLooped = true; autoValue = segments[segIdx % segments.Count]; }
                            else { invalid = true; autoValue = null; }    // 段不够分且未开循环 → 该文件跳过
                        }
                        else autoValue = segments[segIdx];
                        break;
                    }

                    default:
                        autoValue = baseValueRaw;                 // 全部相同：原样（可含文本）
                        break;
                }

                // ② 循环插值：片段位只对"参与序号的文件"递进（跳过占位、忽略不占）；
                //    片段序列按序轮转；循环次数留空=无限，填 N=整套片段最多轮 N 次，用尽后其余文件不插值
                int myFrag = -1;
                if (fragments.Count > 0)
                {
                    if (fragRounds < 0) myFrag = fragIdx % fragments.Count;
                    else if (fragIdx < fragments.Count * fragRounds) myFrag = fragIdx % fragments.Count;
                    else myFrag = -1;   // 轮次用尽 → 不插
                }

                var ctx = new DbrValueContext
                {
                    FieldName = fieldName,
                    IsArray = snap.FieldIsArray.TryGetValue(fieldName ?? "", out var arr) && arr,
                    IsPicklist = snap.FieldIsPicklist.TryGetValue(fieldName ?? "", out var pk) && pk,
                    KeyExistsInOrigLines = f.KeyExists.TryGetValue(fieldName ?? "", out var ke) && ke,
                    KeyOccurrences = f.Occurrences.TryGetValue(fieldName ?? "", out var occ) ? occ : 1
                };
                // 数组段数对比（旧报告 R4：之前从未赋值，ArrayLenDiff 警告永不触发）
                if (ctx.IsArray && !string.IsNullOrWhiteSpace(oldVal))
                    ctx.TargetArrayLength = oldVal.Split(';').Count(x => x.Trim().Length > 0);
                if (ctx.IsArray && baseArrayLen >= 0)
                    ctx.BaseArrayLength = baseArrayLen;

                // 手改优先（手改值不走 片段/前后缀/补位 的合成）
                string final;
                bool isManual = intent.ManualOverrideByPath.TryGetValue(f.RelPath, out var mv);
                if (isManual)
                {
                    // 【自动清理】换行/控制字符必然非法且无语义歧义 → 程序移除并计数（不静默）
                    final = DbrValueGuard.AutoClean(mv, out string mcw);
                    if (mcw != null) counters.AutoCleaned++;
                    var mi = DbrValueGuard.Validate(final, ctx);
                    // 跳过行的错误不阻断：该行不写入，降级为警告（旧报告 R2）
                    if (DbrValueGuard.HasError(mi) && state != BatchRowState.Skip)
                    {
                        counters.Blocked++;
                        plan.Issues.Add(new BatchIssueGroup
                        {
                            Edit = new PlannedEdit { RelPath = f.RelPath, OldValue = oldVal, FinalValue = final, IsManual = true },
                            Issues = mi
                        });
                        continue;
                    }
                }
                else if (invalid)
                {
                    if (state == BatchRowState.Skip)
                    {
                        // 跳过行：步进/分割无效只归入跳过，不拦整批（旧报告 R2）
                        counters.Skip++;
                        plan.Skips.Add(new PlannedSkip { RelPath = f.RelPath, State = BatchRowState.Skip, AutoValue = null });
                        continue;
                    }
                    counters.Blocked++;                       // 步进/分割无效 → 不写（计入阻断）
                    plan.Skips.Add(new PlannedSkip { RelPath = f.RelPath, State = BatchRowState.Normal, AutoValue = null });
                    continue;
                }
                else
                {
                    // ④补位 → ②循环插值 → ③前后缀（2026-09 定案：先对数值段补位再插值。
                    // 例：分割段 1 → 补位 01 → 插值 01a；旧顺序会把补位套在插值后的非数字上而失效。
                    // 补位只对纯数值生效，数组值（含分号）不补。）
                    string core = autoValue ?? "";
                    bool numeric = DbrArrayFormula.TryParsePlainNumber(core, out double num);
                    if (numeric && intent.Pad > 0)
                        core = DbrArrayFormula.FormatValue(num, intent.Pad);
                    if (myFrag >= 0)
                    {
                        var fr = fragments[myFrag];
                        switch (fr.Op)
                        {
                            case BatchFragmentOp.Prepend: core = (fr.Text ?? "") + core; break;
                            case BatchFragmentOp.Append: core = core + (fr.Text ?? ""); break;
                            case BatchFragmentOp.Replace: core = fr.Text ?? ""; break;   // 替换：丢弃计算值（补位结果一并丢弃），仍套全局前后缀
                            // Keep（不插）：原样输出，什么都不做
                        }
                    }
                    final = (intent.Prefix ?? "") + core + (intent.Suffix ?? "");
                }

                // 【自动清理】换行/控制字符在 dbr 值里必然非法且无语义歧义 → 程序移除并计数（不静默）
                final = DbrValueGuard.AutoClean(final, out string acw);
                if (acw != null) counters.AutoCleaned++;

                var issues = DbrValueGuard.Validate(final, ctx);
                bool hasError = DbrValueGuard.HasError(issues);
                int warns = DbrValueGuard.Count(issues, DbrValueSeverity.Warn);
                warnCount += warns;

                var edit = new PlannedEdit
                {
                    RelPath = f.RelPath,
                    OldValue = oldVal,
                    AutoValue = autoValue,
                    FinalValue = final,
                    IsNewFieldRow = !ctx.KeyExistsInOrigLines && !string.IsNullOrEmpty(final),
                    IsManual = isManual
                };

                if (hasError && state != BatchRowState.Skip)
                {
                    counters.Blocked++;
                    plan.Issues.Add(new BatchIssueGroup { Edit = edit, Issues = issues });
                    continue;
                }

                if (state == BatchRowState.Skip)
                {
                    counters.Skip++;
                    // 跳过行的错误降级为警告：该行不写入，问题只提示不拦整批（旧报告 R2）
                    if (hasError)
                    {
                        warnCount += issues.Count(x => x.Severity == DbrValueSeverity.Error);
                        var demoted = issues.Select(x => x.Severity == DbrValueSeverity.Error
                            ? new DbrValueIssue(DbrValueSeverity.Warn, x.Code, x.Text)
                            : x).ToList();
                        plan.Issues.Add(new BatchIssueGroup { Edit = edit, Issues = demoted });
                    }
                    plan.Skips.Add(new PlannedSkip { RelPath = f.RelPath, State = BatchRowState.Skip, AutoValue = autoValue });
                }
                else
                {
                    counters.Normal++;
                    if (edit.IsManual) counters.Manual++;
                    if (edit.IsNewFieldRow) counters.NewFieldRow++;
                    plan.Edits.Add(edit);
                }
            }

            if (segLooped)
                plan.InfoNotes.Add(LanguageManager.Instance.GetText("Editor.BatchSplitLooped",
                    "分割段已用完并从头循环复用"));

            counters.Warn = warnCount;
            return plan;
        }

        private static bool IsIgnore(BatchIntent intent, string path)
            => intent.StateByPath.TryGetValue(path, out var st) && st == BatchRowState.Ignore;

        /// <summary>从左侧控件读出当前意图（三态/手改沿用内存里的，再叠加界面参数）。
        /// ⚠️ 基准值与数字框一律读**输入框实时文本**（InputNumber 的 Value 要回车/失焦才提交，
        /// 读 Value 就成了"回车才生效"——用户要的是按键即算）</summary>
        private BatchIntent ReadIntentFromUi()
        {
            var intent = new BatchIntent
            {
                Mode = CurrentMode(),
                BaseValueRaw = _txtBase.Text ?? "",
                Formula = (_txtFormula.Text ?? "").Trim(),
                Prefix = _txtPrefix.Text ?? "",
                Suffix = _txtSuffix.Text ?? "",
                Pad = Clamp(DbrArrayFormula.ParseDouble(_nudPad.Text, 0), 0, 12),
                Fragments = _swFragmentEnabled.Checked
                    ? ParseFragmentLines(_txtFragments.Text)
                    : new List<BatchFragment>(),      // 开关关闭 → 不插
                FragmentEnabled = _swFragmentEnabled.Checked,
                FragmentRounds = _fragmentRounds ?? "",
                SplitMethod = CurrentSplitMethod(),
                SplitSymbol = _txtSplitSymbol.Text ?? "",
                SplitContent = _txtSplitContent.Text ?? "",
                SplitLength = Math.Max(1, Clamp(DbrArrayFormula.ParseDouble(_nudSplitLength.Text, 1), 1, 4096)),
                SplitLoop = _chkSplitLoop.Checked
            };
            foreach (var kv in _intent.StateByPath)
                intent.StateByPath[kv.Key] = kv.Value;
            foreach (var kv in _intent.ManualOverrideByPath)
                intent.ManualOverrideByPath[kv.Key] = kv.Value;
            return intent;
        }

        /// <summary>重算并把结果刷到列表 / 计数条 / 门禁详情</summary>
        private void RecomputeAll()
        {
            if (_current == null) return;

            var intent = ReadIntentFromUi();
            var displayOrder = CurrentDisplayOrder();          // 拖拽后的显示顺序（无排序数据时为 null）
            var plan = ComputePlan(_current, intent, _fieldName, displayOrder);
            _lastPlan = plan;
            _intent.Mode = intent.Mode;
            _intent.Formula = intent.Formula;
            _intent.Prefix = intent.Prefix;
            _intent.Suffix = intent.Suffix;
            _intent.Pad = intent.Pad;
            _intent.Fragments = intent.Fragments;
            _intent.FragmentEnabled = intent.FragmentEnabled;
            _intent.FragmentRounds = intent.FragmentRounds;
            _intent.SplitMethod = intent.SplitMethod;
            _intent.SplitSymbol = intent.SplitSymbol;
            _intent.SplitContent = intent.SplitContent;
            _intent.SplitLength = intent.SplitLength;
            _intent.SplitLoop = intent.SplitLoop;
            _intent.StateByPath = intent.StateByPath;
            _intent.ManualOverrideByPath = intent.ManualOverrideByPath;

            // 一次性更新行（Quiet 抑制逐行重建，收尾一次 LoadLayout 重排）
            // try/finally 兜底：中途抛异常也保证摘牌，否则静态 Quiet 永久卡 true → 表格静默失效（旧报告 R1）
            BatchRow.Quiet = true;
            try
            {
                var rowsByPath = _rows.ToDictionary(r => r.RelPath, r => r, StringComparer.OrdinalIgnoreCase);
                var filesInOrder = OrderFiles(_current.Files, displayOrder);   // 与 ComputePlan 同一顺序

                // 参与"序号"的文件集合（正常+跳过，按显示顺序）→ 序号
                int ordinal = 0;
                var ordinalByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in filesInOrder)
                {
                    var st = intent.StateByPath.TryGetValue(f.RelPath, out var s) ? s : BatchRowState.Normal;
                    if (st == BatchRowState.Ignore) continue;
                    ordinal++;
                    ordinalByPath[f.RelPath] = ordinal;
                }

                foreach (var f in filesInOrder)
                {
                    if (!rowsByPath.TryGetValue(f.RelPath, out var row)) continue;

                    var state = intent.StateByPath.TryGetValue(f.RelPath, out var s) ? s : BatchRowState.Normal;

                    var edit = plan.Edits.FirstOrDefault(x => string.Equals(x.RelPath, f.RelPath, StringComparison.OrdinalIgnoreCase));
                    var skip = plan.Skips.FirstOrDefault(x => string.Equals(x.RelPath, f.RelPath, StringComparison.OrdinalIgnoreCase));
                    var issue = plan.Issues.FirstOrDefault(x => string.Equals(x.Edit.RelPath, f.RelPath, StringComparison.OrdinalIgnoreCase));

                    bool isIgnore = state == BatchRowState.Ignore;
                    bool isSkip = state == BatchRowState.Skip;
                    bool hasErr = issue != null && issue.Issues.Any(x => x.Severity == DbrValueSeverity.Error);
                    bool hasWarn = issue != null && issue.Issues.Any(x => x.Severity == DbrValueSeverity.Warn);
                    string issueText = DbrValueGuard.Primary(issue?.Issues)?.Text;

                    // 旧值列（此前从未回填，永远显示"…"占位）
                    f.Values.TryGetValue(_fieldName ?? "", out var oldDisp);
                    f.KeyExists.TryGetValue(_fieldName ?? "", out var oldKe);
                    row.SetOld(oldDisp ?? "", oldKe);

                    row.SetState(state, ordinalByPath.TryGetValue(f.RelPath, out var o) ? o : 0);

                    string auto = edit != null ? edit.AutoValue
                                : skip != null ? skip.AutoValue
                                : isIgnore ? "—" : "—";
                    row.SetAuto(auto);

                    string final = edit != null ? edit.FinalValue : (isIgnore ? "—" : "");
                    bool isManual = edit != null && edit.IsManual;
                    bool isNew = edit != null && edit.IsNewFieldRow;

                    row.SetFinal(final, isManual, isNew, hasErr, hasWarn, issueText);
                }
            }
            finally
            {
                BatchRow.Quiet = false;
            }
            // ⚠️ AntdUI 的范式是 LoadLayout() + Invalidate()：Quiet 抑制了逐行属性通知，
            //    这里若不重绘，新值/旧值就停留在上一次布局快照（"不跟随设定走"的根因之一）
            _grid.LoadLayout();
            _grid.Invalidate();

            UpdateCounters(plan);
        }

        /// <summary>右上角状态统计（含状态提示汇总）/ 左侧计数 / 门禁详情</summary>
        private void UpdateCounters(BatchPlan plan)
        {
            var c = plan.Counters;
            int warn = c.Warn;

            // 右上角：状态统计 + 状态提示汇总（状态列已去掉，这里是对账总入口）
            // ⚠️ 占位符数量必须与语言文件 Editor.BatchTopCounters 一致（9 个，含 🧹已自动清理 {8}），
            //    少传参数会直接 FormatException 崩弹窗
            _lblTopCounters.Text = string.Format(
                LanguageManager.Instance.GetText("Editor.BatchTopCounters",
                    "将写入 {0}/{1} · 跳过 {2} · 忽略 {3} · 手改 {4} · 新增行 {5} · ⚠警告 {6} · ✗阻断 {7} · 🧹已自动清理 {8}"),
                c.Normal, c.Total, c.Skip, c.Ignore, c.Manual, c.NewFieldRow, warn, c.Blocked, c.AutoCleaned);

            SetGateHint(
                !string.IsNullOrEmpty(plan.PrimaryBlockedText) ? "✗ " + plan.PrimaryBlockedText
                : (plan.InfoNotes.Count > 0 ? "ℹ " + plan.InfoNotes[0] : ""),
                hasError: !string.IsNullOrEmpty(plan.PrimaryBlockedText),
                hasWarn: string.IsNullOrEmpty(plan.PrimaryBlockedText) && c.Blocked > 0);

            _lblLeftCounters.Text = string.Format(
                LanguageManager.Instance.GetText("Editor.BatchLeftCounters", "参与 {0} · 占号 {1} · 将写入 {2}"),
                c.Total - c.Ignore, c.Normal + c.Skip, c.Normal);

            // 清手改：没有手改时置灰（点了没反应会被当成按钮坏了）
            if (_btnClearManual != null) _btnClearManual.Enabled = c.Manual > 0;
        }

        // ───────────── 确定 / 确认 ─────────────

        /// <summary>点【确定】：重取快照重算比对 → 确认框（计数+差异+我已确认）→ 产出结果</summary>
        private bool ConfirmAndBuildResult()
        {
            // 1) 重取快照（列顺序/各文件现值可能已变化）
            var fresh = _snapshotProvider?.Invoke() ?? _current;
            string freshSig = BuildSignature(fresh);

            // 2) 期间外部数据变化 → 阻断（三态/手改按路径存，不会错位；但值与序号要复核）
            if (!string.Equals(freshSig, _lastSignature, StringComparison.Ordinal))
            {
                _current = fresh;
                _lastSignature = freshSig;
                RecomputeAll();
                MessageBox.Show(this,
                    LanguageManager.Instance.GetText("Editor.BatchDataChanged",
                        "批量编辑期间数据已发生变化，列表已按最新数据刷新，请复核后再确定。"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;    // 不关窗
            }

            // 3) 以当前输入重算（不信任列表）；值分配按表格当前显示顺序（含拖拽调整）
            var intent = ReadIntentFromUi();
            var plan = ComputePlan(_current, intent, _fieldName, CurrentDisplayOrder());

            if (plan.Counters.Blocked > 0)
            {
                string hint = string.Format(LanguageManager.Instance.GetText("Editor.BatchBlockedHint",
                    "仍有 {0} 行未通过校验，请在列表中处理后再确定"), plan.Counters.Blocked);
                if (!string.IsNullOrEmpty(plan.PrimaryBlockedText)) hint = plan.PrimaryBlockedText;
                MessageBox.Show(this, hint,
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (plan.Counters.Normal == 0)
            {
                MessageBox.Show(this,
                    LanguageManager.Instance.GetText("Editor.BatchNothingToWrite", "没有要写入的文件"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // 4) 确认框：计数 + 前 10 条差异（不再要求勾选"我已确认"）
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(
                LanguageManager.Instance.GetText("Editor.BatchConfirmSummary",
                    "将写入 {0} 个文件（其中 {1} 个将新增字段行）"),
                plan.Counters.Normal, plan.Counters.NewFieldRow));
            sb.AppendLine(string.Format(
                LanguageManager.Instance.GetText("Editor.BatchConfirmSkips",
                    "跳过 {0}（占位不写）· 忽略 {1}（序号已前移）· 警告 {2}"),
                plan.Counters.Skip, plan.Counters.Ignore, plan.Counters.Warn));
            if (plan.Counters.Ignore > 0 || plan.Counters.Manual > 0 || plan.Counters.NewFieldRow > 0)
                sb.AppendLine(LanguageManager.Instance.GetText("Editor.BatchConfirmUndoHint",
                    "注意：回退请使用「撤销选中修改」（文件级，会连带回退该文件此前的编辑），或右键「撤销本次批量修改」"));
            sb.AppendLine();

            int shown = 0;
            foreach (var edit in plan.Edits)
            {
                var name = _current.Files.FirstOrDefault(f =>
                    string.Equals(f.RelPath, edit.RelPath, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? edit.RelPath;
                sb.AppendLine($"{name}：{edit.OldValue} → {edit.FinalValue}");
                if (++shown >= 10) break;
            }
            if (plan.Edits.Count > shown)
                sb.AppendLine(string.Format(LanguageManager.Instance.GetText("Editor.BatchConfirmMore", "…等共 {0} 个文件"), plan.Edits.Count));

            var ackPanel = new AntdUI.Panel { Size = new Size(DpiManager.S(560), DpiManager.S(340)), BackColor = Color.Transparent };
            var lbl = new AntdUI.Input
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                Text = sb.ToString(), Font = FontHelper.UiFont(9), BackColor = Color.Transparent
            };
            ackPanel.Controls.Add(lbl);

            bool ok = false;
            var cfg = new AntdUI.Modal.Config(_owner,
                LanguageManager.Instance.GetText("Editor.BatchConfirmTitle", "确认批量写入"), ackPanel)
            {
                OkText = LanguageManager.Instance.GetText("Common.OK", "确定"),
                CancelText = LanguageManager.Instance.GetText("Common.Cancel", "取消"),
                OkType = AntdUI.TTypeMini.Primary,
                MaskClosable = false,
                Draggable = true,
                OnOk = c2 => { ok = true; return true; }
            };
            AntdUI.Modal.open(cfg);
            if (!ok) return false;    // 用户取消确认框：不关批量窗

            // 5) 产出结果（仅"正常且通过门禁"的行）
            var result = new BatchEditResult
            {
                FieldName = _fieldName,
                Counters = plan.Counters
            };
            foreach (var edit in plan.Edits)
                result.FinalValuesByPath[edit.RelPath] = edit.FinalValue;
            foreach (var skip in plan.Skips.Where(x => x.State == BatchRowState.Skip))
                result.SkipPaths.Add(skip.RelPath);
            foreach (var skip in plan.Skips.Where(x => x.State == BatchRowState.Ignore))
                result.IgnorePaths.Add(skip.RelPath);

            _result = result;
            return true;
        }

        /// <summary>外部数据快照的指纹：文件集合 / 顺序 / 各文件现值 任一变化即不同</summary>
        private static string BuildSignature(BatchEditContext ctx)
        {
            var sb = new StringBuilder();
            sb.Append(ctx.BasePath).Append('|');
            foreach (var f in ctx.Files)
            {
                sb.Append(f.RelPath).Append(':');
                foreach (var kv in f.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
                sb.Append('|');
            }
            // 简单可复现的指纹（无需加密强度）
            int hash = 17;
            unchecked { foreach (char c in sb.ToString()) hash = hash * 31 + c; }
            return hash.ToString("X8");
        }

        // ───────────── 多语言 ─────────────

        /// <summary>语言切换时刷新计数文案（文本由重算再生成）</summary>
        private void ApplyLocalizedCounters()
        {
            UpdateCountersText();
        }

        /// <summary>仅刷新计数类文案（不触发重算）</summary>
        private void UpdateCountersText()
        {
            // 由 RecomputeAll 统一生成；语言切换后靠 RecomputeAll 的调用方（LanguageChanged）触发
        }

        // ───────────── 防抖与重算 ─────────────

        private void ScheduleRecompute()
        {
            if (_suspendRecompute) return;
            if (_recomputeTimer == null) return;   // 构造期预填文本会触发 TextChanged，此时定时器还没建（首次重算由构造函数显式调 RecomputeAll）
            _recomputeTimer.Stop();
            _recomputeTimer.Start();
        }

        /// <summary>写左栏底部门禁提示（阻断=红、警告=橙、信息=主色；非模态，下次重算覆盖）</summary>
        private void SetGateHint(string text, bool hasError, bool hasWarn)
        {
            _lblGateHint.Text = text;
            _lblGateHint.ForeColor = hasError ? ThemeManager.Modified
                : hasWarn ? AntdUI.Style.Db.Warning
                : ThemeManager.Primary;
        }

        /// <summary>门禁拦截提示（非模态：写进左栏底部提示行，不弹 MessageBox；下次重算会覆盖）</summary>
        private void ShowGateBanner(List<DbrValueIssue> issues)
        {
            if (issues == null || issues.Count == 0) return;
            var lines = issues.Select(x => $"{(x.Severity == DbrValueSeverity.Error ? "✗" : "⚠")} {x.Text}").ToList();
            bool hasError = issues.Any(x => x.Severity == DbrValueSeverity.Error);
            SetGateHint(string.Join(" · ", lines), hasError, !hasError);
        }

        // ───────────── 列表行模型 ─────────────

        /// <summary>
        /// 一行 = 一个文件。三态真源是 State 枚举，两个复选框列绑定其派生属性。
        /// 注意：Quiet 由弹窗在批量更新时置位，避免 N 次"整行重建"（一次重排代替）。
        /// </summary>
        private sealed class BatchRow : INotifyPropertyChanged
        {
            /// <summary>批量更新期间的静默开关（同弹窗内所有行共用）</summary>
            internal static bool Quiet;

            public event PropertyChangedEventHandler PropertyChanged;
            private BatchRowState _state = BatchRowState.Normal;

            // ⚠️ 绑定属性必须 public：AntdUI 表格绑定走 TypeDescriptor，只识别 public 属性；
            //    internal 属性绑定后整列空白（"没有文件名列"的根因）
            public string RelPath { get; set; }
            public string File { get; set; }
            /// <summary>显示用旧值（含"（原文无此行）"等灰字说明）</summary>
            public string OldValue { get; set; }
            /// <summary>自动算值（跳过行也可见，灰字；复制预览用）</summary>
            public string AutoValue { get; set; }
            /// <summary>最终值（= 手改值 ?? 自动算值）</summary>
            public string NewValue { get; set; }
            /// <summary>自动算值（手改行用它比对，一致时自动摘掉手改标记）</summary>
            internal string ComputedValue => AutoValue;
            internal bool IsNewFieldRow { get; private set; }
            internal bool HasError { get; private set; }
            internal bool HasWarn { get; private set; }
            internal string IssueText { get; private set; }
            internal bool IsManual { get; private set; }
            /// <summary>手改值（仅手改行有）</summary>
            internal string ManualValue { get; private set; }
            /// <summary>序号（0 = 忽略，不占号）</summary>
            internal int Ordinal { get; private set; }

            internal BatchRowState State => _state;

            /// <summary>设置三态与序号</summary>
            internal void SetState(BatchRowState state, int ordinal)
            {
                bool changed = _state != state || Ordinal != ordinal;
                _state = state;
                Ordinal = ordinal;
                if (!changed) return;
                Notify(nameof(Skip));
                Notify(nameof(Ignore));
                Notify(nameof(Ordinal));
            }

            /// <summary>设置旧值显示（原文无此行时用说明文案占位）</summary>
            internal void SetOld(string oldValue, bool keyExists)
            {
                string text = keyExists ? oldValue
                    : LanguageManager.Instance.GetText("Editor.BatchNoOrigLine", "（原文无此行）");
                if (OldValue == text) return;
                OldValue = text;
                Notify(nameof(OldValue));
            }

            /// <summary>设置自动算值（跳过行也显示，灰字由行样式负责）</summary>
            internal void SetAuto(string autoValue)
            {
                if (AutoValue == autoValue) return;
                AutoValue = autoValue;
                Notify(nameof(AutoValue));
            }

            /// <summary>
            /// 设置最终值与门禁/手改标记。
            /// 手改值与自动值一致时**自动摘掉手改标记**，避免"假手改"污染计数。
            /// </summary>
            internal void SetFinal(string finalValue, bool isManual, bool isNewFieldRow, bool hasError, bool hasWarn, string issueText)
            {
                if (!isManual && IsManual && string.Equals(finalValue, ComputedValue, StringComparison.Ordinal))
                    IsManual = false;
                if (isManual) IsManual = true;

                NewValue = finalValue;
                IsNewFieldRow = isNewFieldRow;
                HasError = hasError;
                HasWarn = hasWarn;
                IssueText = issueText;
                Notify(nameof(NewValue));
            }

            /// <summary>设置手改值（并标记手改）</summary>
            internal void SetManual(string manualValue)
            {
                IsManual = true;
                ManualValue = manualValue;
                NewValue = manualValue;
                Notify(nameof(NewValue));
            }

            /// <summary>清掉手改（回到自动算值）</summary>
            internal void ClearManual(string autoValue)
            {
                IsManual = false;
                ManualValue = null;
                NewValue = autoValue;
                Notify(nameof(NewValue));
            }

            // 供绑定层读取的三态（复选框列）
            public bool Skip
            {
                get => _state == BatchRowState.Skip;
                set { if (value) SetStateRaw(BatchRowState.Skip); else if (_state == BatchRowState.Skip) SetStateRaw(BatchRowState.Normal); }
            }
            public bool Ignore
            {
                get => _state == BatchRowState.Ignore;
                set { if (value) SetStateRaw(BatchRowState.Ignore); else if (_state == BatchRowState.Ignore) SetStateRaw(BatchRowState.Normal); }
            }

            private void SetStateRaw(BatchRowState state)
            {
                if (_state == state) return;
                _state = state;
                Notify(nameof(Skip));
                Notify(nameof(Ignore));
            }

            private void Notify(string name)
            {
                if (Quiet) return;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
