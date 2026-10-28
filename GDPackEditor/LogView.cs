using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using ScintillaNET;

namespace GDPackEditor
{
    /// <summary>
    /// 公共日志面板：工具条（过滤 / 清空日志 / 导出日志）+ 只读 Scintilla 文本区。
    /// 4 个解包/打包页签共用（ARZ/ARC 编辑器后续可接入），保证日志外观与操作一致。
    ///
    /// 设计要点：
    /// ① 着色只作用于界面：`[WARN]` 黄色背景、`[ERROR]`/`[FATAL]` 红色背景，INFO 沿用默认前景/背景不着色；
    /// ② 后台线程只入队（<see cref="Enqueue"/>，零阻塞），200ms 定时器批量出队后**在 UI 线程**改 Scintilla；
    /// ③ 保留行数上限（默认 <see cref="DefaultMaxLines"/> 行），超出从头部裁掉，避免长时间运行把内存撑满
    ///    （原实现是按 40 万**字符**砍开头，改按行数更直观）；
    /// ④ 过滤只影响显示，内部行集合完整保留，导出导的是"当前可见（过滤后）"的行；
    /// ⑤ 折叠分组：带 groupKey（记录名/文件名）的警告/错误行自动按"连续同名段"归组——
    ///    组头行是真实行（参与着色/筛选/导出/行数上限，"（N 个问题）"计数含在头行文本里），
    ///    组一有子行即收起（打包过程中也保持折叠，决策：默认收起不占空间、收起后只显示头行，不写折叠概述）；折叠逻辑见 <see cref="LogFoldSupport"/>。
    /// </summary>
    public class LogView : AntdUI.Panel
    {
        /// <summary>marker 槽位：警告（黄底）。marker 是"每个 Scintilla 实例各自独立"的，不与编辑器冲突</summary>
        private const int MarkerWarn = 0;
        /// <summary>marker 槽位：错误（红底）</summary>
        private const int MarkerError = 1;

        /// <summary>界面保留行数上限（超出从头部裁掉）</summary>
        public const int DefaultMaxLines = 50000;

        /// <summary>批量刷新间隔：后台线程高频入队，界面按此节流重绘</summary>
        private const int FlushIntervalMs = 200;

        private readonly Scintilla _editor;
        private readonly AntdUI.Panel _toolbar;
        private readonly AntdUI.Segmented _levelFilter;
        private readonly AntdUI.Input _filterBox;
        private readonly AntdUI.Button _clearButton;
        private readonly AntdUI.Button _exportButton;
        private readonly System.Windows.Forms.Timer _flushTimer;
        private readonly System.Windows.Forms.Timer _filterTimer;

        /// <summary>待显示的行（后台线程入队 → UI 线程出队；groupKey 非空表示该行属于某个折叠组）</summary>
        private readonly ConcurrentQueue<(string text, bool overwrite, string groupKey)> _queue =
            new ConcurrentQueue<(string, bool, string)>();

        /// <summary>已显示的行（全量，不受过滤影响；含折叠组头行）</summary>
        private readonly List<string> _lines = new List<string>();

        /// <summary>行类型（与 <see cref="_lines"/> 一一对应）：折叠分组与渲染的并行元数据</summary>
        private readonly List<LogRowKind> _rowKinds = new List<LogRowKind>();

        /// <summary>行分组键（与 <see cref="_lines"/> 一一对应；普通行为 null）</summary>
        private readonly List<string> _rowKeys = new List<string>();

        /// <summary>最近一个组（封口≠失效：显示流中相邻的同名子行仍可续入，跨批次不断组）</summary>
        private LogFoldGroup _openGroup;

        /// <summary>待渲染处理的折叠动作：头行文本更新（批次内只积累，渲染层统一消费）</summary>
        private readonly List<LogFoldGroup> _headerUpdates = new List<LogFoldGroup>();

        /// <summary>待收起的组（封口时机由内容决定：普通行/新组到达，或操作收尾信号）</summary>
        private readonly List<LogFoldGroup> _pendingCollapse = new List<LogFoldGroup>();

        /// <summary>操作收尾信号（后台线程置位，UI 线程的 FlushQueue 消费：封口末组并收起）</summary>
        private int _finalizeRequested;

        /// <summary>瞬态进度行（覆盖行）在 <see cref="_lines"/> 中的下标；-1 表示当前没有</summary>
        private int _progressLineIndex = -1;

        /// <summary>上次实际刷新的时间戳（TickCount64）：用于 FlushNow 节流，避免高频全量重绘</summary>
        private long _lastFlushTicks;

        /// <summary>已渲染到 Scintilla 的行数（无过滤时 = _lines.Count）；-1 表示需要全量重建</summary>
        private int _renderedCount = -1;

        /// <summary>覆盖行（瞬态进度行）内容被替换：增量渲染下只需替换最后一行文本</summary>
        private bool _overwriteDirty;

        /// <summary>单次刷新最多出队的条数（非强制）：批量插入到末尾，避免一次构造超大字符串/一次插入过多行</summary>
        private const int DequeueBudgetNormal = 500;

        /// <summary>强制刷新时的出队上限（收尾时尽快追平积压，仍分批插入）</summary>
        private const int DequeueBudgetForce = 2000;

        private string _filter = string.Empty;
        private int _maxLines = DefaultMaxLines;

        /// <summary>级别筛选项下标：0=全部 1=信息 2=警告 3=错误（与工具条分段控制器一致）</summary>
        private int _levelFilterIndex;

        /// <summary>级别筛选项的显示文本（次序与 <see cref="_levelFilterIndex"/> 一致）——实现委托给公共判定（见 LogLevelFilter）</summary>
        private string[] LevelFilterTexts() => LogLevelFilter.LevelTexts();

        public LogView()
        {
            Back = Color.Transparent;

            // ── 工具条（Dock=Top）──
            // 布局（左→右）：四级筛选（全部/信息/警告/错误） + 过滤框 …… 导出日志 + 清空日志
            // 用绝对定位手工排版（见 LayoutToolbar）：AntdUI 的 Dock 与 FlowPanel 都受"反向处理/倒序显示"影响，
            // 手工排可让左右两组的先后与间距完全确定，不受插入顺序影响（AGENTS §6.6 的坑）。
            _toolbar = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = DpiManager.S(40),
                Back = Color.Transparent
            };

            // 四级筛选：等宽铺满（Full），中英文标签长短不同也不会互相挤压。
            // 底色显式取"组件容器色"（浅色主题即白色），与工具栏所在的页签底色区分开；
            // 选中项用主色填充、文字用主色上的文字色；SelectIndex 必须显式给 0 —— AntdUI 默认是 -1（无选中项）。
            _levelFilter = new AntdUI.Segmented
            {
                Name = "LogView.LevelFilter",
                Full = true,
                Width = DpiManager.S(208),
                Height = DpiManager.S(30),          // 首帧初值；实际高度在 LayoutToolbar 里跟随右侧按钮
                Font = FontHelper.UiFont(8.5f),
                BackColor = ThemeManager.BgContainer,
                BorderColor = ThemeManager.BorderColor,
                BackActive = ThemeManager.Primary,
                ForeActive = ThemeManager.PrimaryColor,
                SelectIndex = 0                     // 默认选中「全部」→ 不过滤，显示全部日志
            };
            foreach (string text in LevelFilterTexts())
                _levelFilter.Items.Add(new AntdUI.SegmentedItem { Text = text });
            _levelFilter.SelectIndexChanged += (s, e) =>
            {
                if (e.Value < 0 || e.Value > 3) return;
                _levelFilterIndex = e.Value;
                RefreshDisplay();
            };

            _filterBox = new AntdUI.Input
            {
                Name = "LogView.Filter",
                Width = DpiManager.S(260),
                Height = DpiManager.S(36),   // 与筛选控件同高（原 Dock=Left 撑满工具栏的可视高度）
                Font = FontHelper.UiFont(8.5f),
                AllowClear = true
            };
            // 输入防抖：连续输入只在停顿后重绘一次
            _filterBox.TextChanged += (s, e) => { _filterTimer?.Stop(); _filterTimer?.Start(); };

            _clearButton = new AntdUI.Button
            {
                Name = "LogView.Clear",
                AutoSize = true,
                MinimumSize = new Size(DpiManager.S(80), DpiManager.S(22)),
                Padding = new Padding(DpiManager.S(4), 0, DpiManager.S(4), 0),
                Font = FontHelper.UiFont(9)
            };
            _clearButton.Click += (s, e) => ClearWithConfirm();

            _exportButton = new AntdUI.Button
            {
                Name = "LogView.Export",
                AutoSize = true,
                MinimumSize = new Size(DpiManager.S(80), DpiManager.S(22)),
                Padding = new Padding(DpiManager.S(4), 0, DpiManager.S(4), 0),
                Font = FontHelper.UiFont(9)
            };
            _exportButton.Click += (s, e) => ExportVisibleLines();

            _toolbar.Controls.Add(_exportButton);
            _toolbar.Controls.Add(_clearButton);
            _toolbar.Controls.Add(_filterBox);
            _toolbar.Controls.Add(_levelFilter);

            // 工具条或值控件宽度变化（自适应/换语言）后重排一次
            _toolbar.Layout += (s, e) => LayoutToolbar();
            foreach (Control c in new Control[] { _levelFilter, _filterBox, _clearButton, _exportButton })
                c.SizeChanged += (s, e) => LayoutToolbar();

            // ── 日志文本区（Dock=Fill）──
            // 用只读 Scintilla 的两个理由（与编辑器一致）：
            // ① AntdUI.Input 的多行框不提供滚动条，长日志无法回看；
            // ② 支持 Marker 行级背景着色，可把警告/错误整行标色（仅界面，.log 文件保持纯文本）
            _editor = new Scintilla
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                TabWidth = 4,
                UseTabs = false,
                Technology = ScintillaNET.Technology.Default,  // GDI 渲染（定稿）：DirectWrite 模式下缺字形的字体回退常落日文字形，GDI 走字体链接回退宋体无此问题
                FontQuality = ScintillaNET.FontQuality.LcdOptimized   // ClearType 抗锯齿：GDI 下的最佳清晰度（仅 GDI 生效，DirectWrite 模式忽略此项）
            };
            _editor.Margins[0].Width = 0;          // 隐藏行号边距（日志已有时间戳，行号无参考价值）
            _editor.CaretLineBackColor = Color.Transparent;   // 透明 = 不显示当前行高亮，避免与标记底色冲突
            ScintillaSyntax.ApplyReadOnlyLog(_editor, FontHelper.MonoFontName, (int)(9f * DpiManager.Scale));
            ApplyMarkerColors();                   // 必须放在 ApplyReadOnlyLog 之后：StyleClearAll 会重置 marker 颜色
            LogFoldSupport.ConfigureFoldUi(_editor);   // 折叠边距/点击收放/概述样式（含折叠符号配色）

            Controls.Add(_editor);     // Fill 先加 → 最后处理 Dock，占满剩余空间
            Controls.Add(_toolbar);    // Top

            _flushTimer = new System.Windows.Forms.Timer { Interval = FlushIntervalMs };
            _flushTimer.Tick += (s, e) => FlushQueue();
            _flushTimer.Start();

            _filterTimer = new System.Windows.Forms.Timer { Interval = FlushIntervalMs };
            _filterTimer.Tick += (s, e) =>
            {
                _filterTimer.Stop();
                _filter = _filterBox.Text ?? string.Empty;
                RefreshDisplay();
            };

            ApplyLocalizedTexts();
            LayoutToolbar();           // 首帧先排一次，避免工具条控件挤在左上角后跳位
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
        }

        /// <summary>界面保留的最大行数（超出从头部裁掉）</summary>
        public int MaxLines
        {
            get => _maxLines;
            set => _maxLines = Math.Max(100, value);
        }

        /// <summary>导出时的默认文件名（不含路径）；为空则用「GDPackEditor-log-时间戳.log」</summary>
        public string ExportFileName { get; set; }

        /// <summary>导出文件头里的标题（可选，如「包：xxx.arc」）</summary>
        public string ExportTitle { get; set; }

        /// <summary>
        /// 线程安全：追加一行日志（可从后台解包/打包线程直接调用，只入队不碰 UI）。
        /// overwriteLast=true 表示这是一条"瞬态进度行"，会替换当前最后一条瞬态进度行。
        /// groupKey 为记录名/文件名（生成侧打标）：带键的警告/错误行自动按"连续同名段"折叠分组，
        /// 不带键的行（普通信息行）永远不成组。
        /// </summary>
        public void Enqueue(string line, bool overwriteLast = false, string groupKey = null)
        {
            if (line == null || IsDisposed) return;
            _queue.Enqueue((line, overwriteLast, groupKey));
        }

        /// <summary>
        /// 操作收尾信号：封口当前开组并收起（末组不再有后续行，靠此收尾）。
        /// 线程安全：只置位标志，实际封口与渲染在 UI 线程的 FlushQueue 中完成（操作收尾一般跟一次 FlushNow(force)）。
        /// </summary>
        public void RequestFinalize() => System.Threading.Interlocked.Exchange(ref _finalizeRequested, 1);

        /// <summary>
        /// 立即把队列刷到界面。非强制调用**带节流**：距上次刷新不足 <see cref="FlushIntervalMs"/> 时只保证
        /// 定时器在跑，不同步重绘——大日志下每次刷新都要全量重建 Scintilla 文本（O(总行数)），
        /// 逐条警告若同步刷新，几万次全量重绘会把内存与界面一起拖垮（解包大包逐条警告时实测内存飙升）。
        /// 操作结束 / 错误 / 需要即时可见的场合传 <paramref name="force"/>=true。
        /// </summary>
        public void FlushNow(bool force = false)
        {
            if (IsDisposed || _editor == null || _editor.IsDisposed) return;
            if (!force && Environment.TickCount64 - _lastFlushTicks < CurrentFlushInterval)
            {
                _flushTimer?.Start();   // 交给定时器批量刷新（已启动时无副作用）
                return;
            }
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)(() => FlushQueue(force))); } catch { }
                return;
            }
            FlushQueue(force);
        }

        /// <summary>
        /// 当前刷新间隔：全量重绘是 O(总行数)，行数越多越贵 —— 日志量大时自适应降频，
        /// 避免"长时间操作 + 大量警告行"时刷新本身成为瓶颈。
        /// </summary>
        private int CurrentFlushInterval =>
            _lines.Count > 20000 ? 800 : _lines.Count > 5000 ? 400 : FlushIntervalMs;

        /// <summary>清空日志（含二次确认）</summary>
        public void ClearWithConfirm()
        {
            DialogResult result = MessageBox.Show(
                LanguageManager.Instance.GetText("Editor.ConfirmClearLog", "确定要清空日志吗？"),
                LanguageManager.Instance.GetText("Common.MessageBoxTitle.Confirm", "确认"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes) Clear();
        }

        /// <summary>
        /// 清空：三处一起清 —— 内部行集合 / 待显示队列 / Scintilla 视图，缺一处都会"看着没清干净"。
        /// 折叠分组状态一并清零（组/待收起/待更新/行类型与分组键），避免清空后残留旧折叠结构。
        /// </summary>
        public void Clear()
        {
            _lines.Clear();
            _rowKinds.Clear();
            _rowKeys.Clear();
            _openGroup = null;
            _pendingCollapse.Clear();
            _headerUpdates.Clear();
            _finalizeRequested = 0;
            while (_queue.TryDequeue(out _)) { }
            _progressLineIndex = -1;
            _renderedCount = -1;      // 清空后重新全量建立渲染基准
            _overwriteDirty = false;

            if (_editor != null && !_editor.IsDisposed)
            {
                // Scintilla 平时只读，写文本前临时关闭只读是本项目既有惯例
                _editor.ReadOnly = false;
                _editor.ClearAll();
                _editor.ReadOnly = true;
            }
        }

        /// <summary>当前界面可见的行（过滤后），供导出使用</summary>
        public IReadOnlyList<string> GetVisibleLines() => FilterLines();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { LanguageManager.Instance.LanguageChanged -= OnLanguageChanged; } catch { }
                try { _flushTimer?.Stop(); _flushTimer?.Dispose(); } catch { }
                try { _filterTimer?.Stop(); _filterTimer?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        // ─────────────────────────── 工具条排版 ───────────────────────────

        /// <summary>
        /// 工具条手工排版：左组「四级筛选 + 过滤框」从左边依次排，右组「导出日志 + 清空日志」从右边依次排（清空在最右）。
        /// 手工定位而不依赖 Dock/FlowPanel：AntdUI 两者都按"插入顺序的反向"处理，顺序会随代码增删控件而悄悄变化。
        /// 过滤框宽度按左右两组之间的剩余空间自适应（上限 260），很窄的面板里优先保住宅、不压住右侧按钮。
        /// </summary>
        private void LayoutToolbar()
        {
            if (_toolbar == null || _toolbar.IsDisposed) return;
            if (_levelFilter == null || _filterBox == null || _clearButton == null || _exportButton == null) return;

            int h = _toolbar.ClientSize.Height;
            int w = _toolbar.ClientSize.Width;
            if (h <= 0 || w <= 0) return;

            int pad = DpiManager.S(4);
            int gap = DpiManager.S(8);

            // 四级筛选的高度跟随右侧按钮的实际高度（按钮 AutoSize，高度随字体/DPI 变），
            // 这样一行内筛选、导出、清空的观感一致；只给一个下限，避免极端缩放下文字被压扁。
            int segH = Math.Max(DpiManager.S(26), _exportButton.Height);
            if (_levelFilter.Height != segH) _levelFilter.Height = segH;

            // 右组：先定「导出日志」与「清空日志」的位置 —— 清空日志在最右
            _clearButton.Location = new Point(Math.Max(0, w - pad - _clearButton.Width), CenterY(_clearButton, h));
            _exportButton.Location = new Point(Math.Max(0, _clearButton.Left - gap - _exportButton.Width), CenterY(_exportButton, h));
            int rightStart = _exportButton.Left;

            // 左组：四级筛选固定宽度，过滤框吃掉中间剩余空间
            _levelFilter.Location = new Point(pad, CenterY(_levelFilter, h));
            int filterX = pad + _levelFilter.Width + gap;
            int filterAvail = rightStart - gap - filterX;
            int filterW = Math.Max(DpiManager.S(120), Math.Min(DpiManager.S(260), filterAvail));
            if (_filterBox.Width != filterW) _filterBox.Width = filterW;
            _filterBox.Location = new Point(filterX, CenterY(_filterBox, h));
        }

        /// <summary>垂直居中：控件在整行高度里的 Y 坐标 —— 实现委托给公共判定（见 LogLevelFilter）</summary>
        private static int CenterY(Control c, int rowHeight) => LogLevelFilter.CenterY(c, rowHeight);

        // ─────────────────────────── 队列 → 界面 ───────────────────────────

        private void FlushQueue(bool force = false)
        {
            if (IsDisposed || _editor == null || _editor.IsDisposed) return;

            // 操作收尾信号：即使队列已空也要消费一次（封口末组收起），不能因 changed=false 提前返回
            bool finalize = System.Threading.Interlocked.Exchange(ref _finalizeRequested, 0) == 1;

            bool changed = false;
            // 单次限量出队：一次插入几百行（批量、避免超大字符串），剩余积压交给下一次定时器继续追平
            int budget = force ? DequeueBudgetForce : DequeueBudgetNormal;
            while (budget-- > 0 && _queue.TryDequeue(out var entry))
            {
                if (entry.overwrite)
                {
                    CollapseOpenGroup();   // 覆盖行属普通行语义：中断分组
                    if (_progressLineIndex >= 0)
                    {
                        // 连续覆盖行：始终替换同一行（瞬态进度只保留最后一个值）
                        _lines[_progressLineIndex] = entry.text;
                        _overwriteDirty = true;   // 增量渲染下只需替换末行文本
                    }
                    else
                    {
                        _progressLineIndex = _lines.Count;
                        _lines.Add(entry.text);
                        _rowKinds.Add(LogRowKind.Normal);
                        _rowKeys.Add(null);
                    }
                }
                else if (entry.groupKey != null)
                {
                    // 带分组键的警告/错误行：按"连续同名段"归组（跨批次不断组，详见 ProcessGroupedLine）
                    ProcessGroupedLine(entry.text, entry.groupKey);
                }
                else
                {
                    // 普通行到来 → 封口当前组；之前那条瞬态进度行已定稿，之后的覆盖行要另起一行
                    CollapseOpenGroup();
                    _progressLineIndex = -1;
                    _lines.Add(entry.text);
                    _rowKinds.Add(LogRowKind.Normal);
                    _rowKeys.Add(null);
                }
                changed = true;
            }
            if (!_queue.IsEmpty)
                _flushTimer?.Start();   // 还有积压：保证定时器继续消费（不受本次限量影响）

            if (finalize)
                CollapseOpenGroup();   // 收尾：末组封口收起（决策：默认收起）

            // 批次末：开组头行文本按最新计数/级别统一刷新一次（组开着时"N 个问题"实时增长）
            if (_openGroup != null && _openGroup.HeadLineIndex >= 0 && _openGroup.ChildCount > 0)
            {
                string newText = LogFoldSupport.BuildHeaderText(_openGroup);
                if (!string.Equals(_lines[_openGroup.HeadLineIndex], newText, StringComparison.Ordinal))
                {
                    _lines[_openGroup.HeadLineIndex] = newText;
                    QueueHeaderUpdate(_openGroup);
                }
            }

            if (!changed && !finalize && _headerUpdates.Count == 0 && _pendingCollapse.Count == 0)
            {
                // 无新内容：强制刷新时也要把视图滚到底（检测/解包收尾：日志早已显示，但视图可能停在中间）
                if (force) ScrollToEnd();
                return;
            }

            _lastFlushTicks = Environment.TickCount64;   // 实际重绘时间（FlushNow 节流基准）

            if (_lines.Count > _maxLines)
            {
                // 【批量裁剪】一次性裁掉一大块（约上限的 1/5，至少 1 万行）：
                // List 的头部 RemoveRange 会把后面所有元素整体前移，逐行裁在上限大时成本是 O(总量)/行
                //（50 万行时每行要移动 4MB 指针）。批量裁把频率摊薄到"每 1 万行一次"。
                int cut = Math.Max(_lines.Count - _maxLines, _maxLines / 5);
                _lines.RemoveRange(0, cut);
                _rowKinds.RemoveRange(0, cut);
                _rowKeys.RemoveRange(0, cut);
                // 被裁掉的行里包含瞬态进度行时要同步修正下标，否则会越界或错行
                _progressLineIndex = _progressLineIndex >= cut ? _progressLineIndex - cut : -1;
                // 折叠组引用同步平移；头行已被裁掉的组直接废弃（历史概述随头行一起没了，子行按普通行显示）
                if (_openGroup != null)
                {
                    _openGroup.HeadLineIndex -= cut;
                    if (_openGroup.HeadLineIndex < 0) _openGroup = null;
                }
                foreach (var g in _pendingCollapse) g.HeadLineIndex -= cut;
                _pendingCollapse.RemoveAll(g => g.HeadLineIndex < 0);
                foreach (var g in _headerUpdates) g.HeadLineIndex -= cut;
                _headerUpdates.RemoveAll(g => g.HeadLineIndex < 0);
                _renderedCount = -1;   // 头部被裁 → 已渲染前缀失效，下次全量重建
            }

            RefreshDisplay();
        }

        /// <summary>
        /// 处理带分组键的警告/错误行：与"最近组"显示流相邻且同名 → 续入；否则封口旧组、插入新组头行后作为首条子行。
        /// 组头行是真实行（先于子行进入行集合），参与着色/筛选/导出/行数上限。
        /// </summary>
        private void ProcessGroupedLine(string text, string key)
        {
            bool continues = _openGroup != null && _openGroup.HeadLineIndex >= 0
                && _lines.Count > 0
                && _rowKinds[_lines.Count - 1] == LogRowKind.GroupChild
                && string.Equals(_rowKeys[_lines.Count - 1], key, StringComparison.Ordinal);

            if (!continues)
            {
                CollapseOpenGroup();
                var g = new LogFoldGroup { Key = key, HeadLineIndex = _lines.Count, ChildCount = 0 };
                LogFoldSupport.ExtractLeadTags(text, out g.TimeTag, out g.LevelTag);
                _lines.Add(LogFoldSupport.BuildHeaderText(g));
                _rowKinds.Add(LogRowKind.GroupHeader);
                _rowKeys.Add(key);
                _openGroup = g;
            }

            var grp = _openGroup;
            grp.ChildCount++;
            // 组内最高级别（ERROR > WARN）：头行标签随组内问题升级，级别筛选/着色随之联动
            LogFoldSupport.ExtractLeadTags(text, out _, out string lineTag);
            if (LogFoldSupport.LevelRank(lineTag) > LogFoldSupport.LevelRank(grp.LevelTag))
                grp.LevelTag = lineTag;

            _lines.Add(text);
            _rowKinds.Add(LogRowKind.GroupChild);
            _rowKeys.Add(key);
        }

        /// <summary>封口当前组：头行文本按最终计数定稿，待渲染层收起（默认收起策略）。组引用保留——同名相邻行仍可续入。</summary>
        private void CollapseOpenGroup()
        {
            var g = _openGroup;
            if (g != null && g.HeadLineIndex >= 0 && g.ChildCount > 0 && !g.Collapsed)
            {
                g.Collapsed = true;
                _lines[g.HeadLineIndex] = LogFoldSupport.BuildHeaderText(g);   // 内部行集合先行定稿
                QueueHeaderUpdate(g);
                _pendingCollapse.Add(g);
            }
        }

        /// <summary>登记一个头行更新（渲染层统一 ReplaceTarget + 概述 + marker 重打）；同批重复登记只留最后一次</summary>
        private void QueueHeaderUpdate(LogFoldGroup g)
        {
            if (g == null || g.HeadLineIndex < 0) return;
            if (_headerUpdates.Count == 0 || !ReferenceEquals(_headerUpdates[_headerUpdates.Count - 1], g))
                _headerUpdates.Add(g);
        }

        // 行首标签提取已公共化到 LogFoldSupport.ExtractLeadTags（编辑器日志面板共用同一实现）

        /// <summary>
        /// 把新增行渲染到 Scintilla。
        /// <para>增量路径（默认）：无过滤时只把新增行**追加**到文本末尾，并为新行补级别着色与折叠层级——
        /// 长日志（几万~十几万行）下避免每次都全量重建整篇文本（原实现每次 O(总行数)，是解包大包内存/卡顿的主因）。</para>
        /// <para>全量路径：首次渲染 / 头部裁剪 / 清空之后（行与行不再一一对应）——重建后按行集合重新投影折叠结构并默认收起。</para>
        /// <para>过滤路径：级别筛选或文本过滤生效时（显示行与内部行不一一对应）——不做折叠，平铺显示
        /// （过滤是人工排查场景，展开全览更直观；内部行集合的折叠结构原样保留，取消过滤后随全量重建恢复）。</para>
        /// </summary>
        private void RefreshDisplay()
        {
            if (_editor == null || _editor.IsDisposed) return;

            ApplyMarkerColors();   // 每次重绘都重设：明暗主题变化后下一条日志自动跟随（StyleClearAll 会清掉 marker 颜色）

            bool noFilter = _levelFilterIndex == 0 && string.IsNullOrEmpty(_filter);
            if (!noFilter)
            {
                // 过滤生效：显示行与内部行不一一对应 → 全量重建（人工查看才启用过滤，规模可控）
                // 过滤视图不做折叠：待处理折叠动作清掉（取消过滤后的全量重建会从行集合重新投影，不丢状态）
                _headerUpdates.Clear();
                _pendingCollapse.Clear();
                var visF = FilterLines();
                _editor.ReadOnly = false;
                _editor.Text = string.Join("\r\n", visF);
                _editor.ReadOnly = true;
                ApplyLineColors(visF);
                _renderedCount = -1;   // 过滤结束后需要重新全量
                _overwriteDirty = false;
                ScrollToEnd();
                return;
            }

            int target = _lines.Count;
            bool canAppend = _renderedCount >= 0 && _renderedCount <= target;

            if (canAppend && _renderedCount == target)
            {
                // 无新增行：仅覆盖行可能替换了末行内容 / 头行文本可能待更新（收尾封口等）
                if (_overwriteDirty && target > 0)
                {
                    ReplaceLastLineText(_lines[target - 1]);
                    _overwriteDirty = false;
                }
                ApplyPendingFoldActions();
                ScrollToEnd();
                return;
            }

            if (canAppend)
            {
                // 增量：批量追加新增行（一次插入几百行）
                var sb = new StringBuilder();
                for (int i = _renderedCount; i < target; i++)
                {
                    if (i > 0) sb.Append("\r\n");
                    sb.Append(_lines[i]);
                }
                _editor.ReadOnly = false;
                _editor.AppendText(sb.ToString());
                _editor.ReadOnly = true;
                ApplyFoldLevelsRange(_renderedCount, target);     // 新行的折叠层级（头行/子行/普通行）
                ApplyPendingFoldActions();                       // 头行文本/概述更新 + 收起的组
                ApplyLineColorsRange(_lines, _renderedCount);   // 只给新行补着色
                _renderedCount = target;
                _overwriteDirty = false;
                ScrollToEnd();
                return;
            }

            // 全量重建
            _editor.ReadOnly = false;
            _editor.Text = string.Join("\r\n", _lines);
            _editor.ReadOnly = true;
            RebuildFoldStructure();    // 折叠重投影（Text 重置会清掉全部折叠状态）+ 默认收起
            ApplyLineColors(_lines);
            _renderedCount = target;
            _overwriteDirty = false;
            ScrollToEnd();
        }

        /// <summary>
        /// 给 [fromIndex, toIndex) 范围的新行设置折叠层级：组头行 1025|Header、子行 1026；
        /// 普通行保持 Scintilla 默认 1024（从未被改过，无需逐行设置）。
        /// 折叠范围的成立依赖"子行 level > 头行 level"，组与组、组与普通行因此天然互不嵌套。
        /// </summary>
        private void ApplyFoldLevelsRange(int fromIndex, int toIndex)
        {
            var s = _editor;
            if (s == null || s.IsDisposed) return;
            int lineCount = s.Lines.Count;
            for (int i = Math.Max(0, fromIndex); i < toIndex && i < lineCount && i < _rowKinds.Count; i++)
            {
                var kind = _rowKinds[i];
                if (kind == LogRowKind.GroupHeader)
                {
                    var line = s.Lines[i];
                    line.FoldLevel = LogFoldSupport.FoldLevelHeader;
                    line.FoldLevelFlags = FoldLevelFlags.Header;
                }
                else if (kind == LogRowKind.GroupChild)
                {
                    s.Lines[i].FoldLevel = LogFoldSupport.FoldLevelChild;
                }
            }
        }

        /// <summary>
        /// 消费待处理的折叠动作：① 头行文本更新（ReplaceTarget 同步行文本 + marker 级别重打）；
        /// ② 组首次渲染时应用折叠结构并收起（打包过程中也保持折叠；已封口的组由待收起队列再统一收起）；
        /// ③ 流式追加进"已收起开组"的子行补一对展开→收起强制重算（等效"插入即隐藏"，详见块内注释）。
        /// </summary>
        private void ApplyPendingFoldActions()
        {
            var s = _editor;
            if (s == null || s.IsDisposed) return;

            if (_headerUpdates.Count > 0)
            {
                foreach (var g in _headerUpdates)
                {
                    int idx = g.HeadLineIndex;
                    if (idx < 0 || idx >= s.Lines.Count || idx >= _lines.Count) continue;

                    // ① 行文本同步（_lines 已是最新文本，ReplaceTarget 只换本行内容、不影响行数）。
                    // ⚠ Scintilla 的 Line.Text/Length/EndPosition 均【包含行尾换行符】：目标区间一旦含换行符，
                    // ReplaceTarget 会把它一并替换掉 —— 头行与下一行拼接、文档行数永久减一，之后所有按下标
                    // 定位的头行更新都会向下漂移（子行被覆盖丢失、头行重复出现在别的组、同名组看似"断组"）。
                    // 故终点必须剔除行尾换行；位置单位是字符（编辑器侧 BuildLinkRange 有同样的结论注释）。
                    var line = s.Lines[idx];
                    string oldText = line.Text ?? string.Empty;
                    if (oldText.EndsWith("\r\n", StringComparison.Ordinal))
                        oldText = oldText.Substring(0, oldText.Length - 2);
                    else if (oldText.EndsWith("\n", StringComparison.Ordinal) || oldText.EndsWith("\r", StringComparison.Ordinal))
                        oldText = oldText.Substring(0, oldText.Length - 1);
                    s.ReadOnly = false;
                    s.TargetStart = line.Position;
                    s.TargetEnd = line.Position + oldText.Length;
                    s.ReplaceTarget(_lines[idx]);
                    s.ReadOnly = true;

                    // ② 折叠结构 + 收起：只在组首次渲染时应用一次（组一有子行就收起，打包过程中也保持折叠）。
                    // 头行后续刷新只换文本、不重设折叠 —— 用户手动展开的组不会被下一批刷新强行收回（封口时才统一收起）。
                    if (!g.FoldApplied)
                    {
                        LogFoldSupport.ApplyFoldToHeader(s, idx);
                        g.FoldApplied = true;
                    }

                    // ③ marker 重打：头行级别可能随组内问题升级（WARN→ERROR），背景色要跟上
                    var rel = s.Lines[idx];
                    rel.MarkerDelete(MarkerWarn);
                    rel.MarkerDelete(MarkerError);
                    int lv = LineLevel(_lines[idx]);
                    if (lv == 3) rel.MarkerAdd(MarkerError);
                    else if (lv == 2) rel.MarkerAdd(MarkerWarn);
                }
                _headerUpdates.Clear();
            }

            if (_pendingCollapse.Count > 0)
            {
                foreach (var g in _pendingCollapse)
                {
                    // SCI_FOLDLINE 真收起（会隐藏子行）；SetFoldExpanded 只改标记不藏行，已废弃
                    if (g.HeadLineIndex >= 0 && g.HeadLineIndex < s.Lines.Count)
                        s.Lines[g.HeadLineIndex].FoldLine(FoldAction.Contract);
                }
                _pendingCollapse.Clear();
            }

            // ③ 流式追加的子行必须批量末补收起：组第一次收起那一刻 Scintilla 按"当时范围"藏行，
            //    之后追加的新子行只是把折叠范围拉长，不会被自动并入收起区（保持可见）；
            //    且对已收起的头行直接再发 Contract 是空操作（展开标记未变，跳过重算）。
            //    故对仍收起的开组补一对 展开→收起：两者都按当前完整折叠层级重新计算范围，
            //    净效果 = 新旧子行全部隐藏（等效"插入即隐藏"）；同批次内完成、无绘制窗口、无闪烁。
            //    头行处于展开状态（用户手动展开正在增长的组）时不补，保留用户开合，封口时统一收起。
            if (_openGroup != null && _openGroup.FoldApplied
                && _openGroup.HeadLineIndex >= 0 && _openGroup.HeadLineIndex < s.Lines.Count)
            {
                var head = s.Lines[_openGroup.HeadLineIndex];
                if (!head.Expanded)   // Expanded=账面展开标记（非可见状态）；收起中的组才需要补重算
                {
                    head.FoldLine(FoldAction.Expand);     // 先按当前层级显示完整范围（新子行纳入计算）
                    head.FoldLine(FoldAction.Contract);   // 再整体收起（新子行一并藏掉）
                }
            }
        }

        /// <summary>
        /// 全量重建后的折叠重投影：按行类型重设折叠层级，再从行集合重新扫描分组（头行文本是权威，子行只数个数），默认收起。
        /// 头部裁剪可能留下"头行被裁、子行残留"的孤儿段——按普通内容显示，不折叠（不为其再造头行）。
        /// </summary>
        private void RebuildFoldStructure()
        {
            var s = _editor;
            if (s == null || s.IsDisposed) return;
            int n = Math.Min(s.Lines.Count, Math.Min(_lines.Count, _rowKinds.Count));

            // ① 逐行折叠层级
            for (int i = 0; i < n; i++)
            {
                var kind = _rowKinds[i];
                if (kind == LogRowKind.GroupHeader)
                {
                    var line = s.Lines[i];
                    line.FoldLevel = LogFoldSupport.FoldLevelHeader;
                    line.FoldLevelFlags = FoldLevelFlags.Header;
                }
                else if (kind == LogRowKind.GroupChild)
                {
                    s.Lines[i].FoldLevel = LogFoldSupport.FoldLevelChild;
                }
            }

            // ② 重新扫描分组（头行文本是权威，子行只数个数）→ 默认收起（决策：折叠后不占空间、只显示头行）
            for (int i = 0; i < n; i++)
            {
                if (_rowKinds[i] != LogRowKind.GroupHeader) continue;

                int j = i + 1;
                while (j < n && _rowKinds[j] == LogRowKind.GroupChild && string.Equals(_rowKeys[j], _rowKeys[i], StringComparison.Ordinal))
                    j++;

                if (j > i + 1)
                    LogFoldSupport.ApplyFoldToHeader(s, i);
                i = j - 1;
            }
        }

        /// <summary>替换 Scintilla 最后一行的文本（覆盖行专用：只改一行，避免全量重建）</summary>
        private void ReplaceLastLineText(string text)
        {
            var s = _editor;
            if (s == null || s.IsDisposed || s.Lines.Count == 0) return;
            var line = s.Lines[s.Lines.Count - 1];
            // 与 ApplyPendingFoldActions 同一类坑：Line.EndPosition 含行尾换行符，
            // 剔除后再作为替换终点，避免将来在"末行带换行"的场景下吞掉换行引发拼接
            string oldText = line.Text ?? string.Empty;
            if (oldText.EndsWith("\r\n", StringComparison.Ordinal))
                oldText = oldText.Substring(0, oldText.Length - 2);
            else if (oldText.EndsWith("\n", StringComparison.Ordinal) || oldText.EndsWith("\r", StringComparison.Ordinal))
                oldText = oldText.Substring(0, oldText.Length - 1);
            s.ReadOnly = false;
            s.TargetStart = line.Position;
            s.TargetEnd = line.Position + oldText.Length;
            s.ReplaceTarget(text ?? "");
            s.ReadOnly = true;
        }

        /// <summary>
        /// 滚动到底：**纯滚动**（不动光标、不展开折叠），按文档行数滚、Scintilla 自动钳制在末尾。
        /// ⚠ 不能用 GotoPosition(TextLength)/ScrollCaret 定位末尾：SCI_GOTOPOS 的官方语义是
        /// "自动展开折叠以让该位置可见"——打包过程中末尾恰是当前开组的收起区，每次刷新都会把
        /// 隐藏的子行强制撑开（子项闪现），组封口被收起后再次刷新又撑开，形成"闪现后又消失"。
        /// </summary>
        private void ScrollToEnd()
        {
            var s = _editor;
            if (s == null || s.IsDisposed) return;
            s.LineScroll(s.Lines.Count, 0);
        }

        /// <summary>
        /// 当前可见行：级别筛选与文本过滤叠加生效（两者都只影响显示，内部行集合始终完整保留）。
        /// </summary>
        private List<string> FilterLines()
            => _lines.Where(l => LogLevelFilter.Match(l, _levelFilterIndex, _filter)).ToList();

        /// <summary>
        /// 行级别：3=错误、2=警告、1=信息（与工具条四级筛选的 1/2/3 对应）。
        /// 没有级别标记的行（如瞬态进度行）按信息算，避免切到"信息"时整行凭空消失。
        /// ——实现委托给公共判定（见 LogLevelFilter）。
        /// </summary>
        private static int LineLevel(string line) => LogLevelFilter.LineLevel(line);

        // ─────────────────────────── 行级着色 ───────────────────────────

        private void ApplyMarkerColors()
        {
            var s = _editor;
            if (s == null || s.IsDisposed) return;

            s.Markers[MarkerWarn].Symbol = MarkerSymbol.Background;
            s.Markers[MarkerWarn].SetBackColor(ThemeManager.LogWarnBack);
            s.Markers[MarkerError].Symbol = MarkerSymbol.Background;
            s.Markers[MarkerError].SetBackColor(ThemeManager.LogErrorBack);
            LogFoldSupport.ApplyFoldColors(s);   // 折叠符号颜色跟随明暗主题（同一刷新机制）
        }

        /// <summary>
        /// 按级别给整行加背景标记（错误红、警告黄）。级别判定与筛选共用 <see cref="LineLevel"/>，避免两处规则走偏；
        /// 颜色每次重绘都重设，因此自动跟随明暗主题；仅作用于界面，导出的 .log 文件仍是纯文本。
        /// 信息行不着色（保持默认前景/背景）。
        /// </summary>
        private void ApplyLineColors(IList<string> lines)
        {
            var s = _editor;
            if (s == null || s.IsDisposed || lines == null) return;

            s.MarkerDeleteAll(MarkerWarn);
            s.MarkerDeleteAll(MarkerError);

            int lineCount = s.Lines.Count;
            for (int i = 0; i < lines.Count && i < lineCount; i++)
            {
                int level = LineLevel(lines[i]);
                int marker = level == 3 ? MarkerError : level == 2 ? MarkerWarn : -1;
                if (marker >= 0) s.Lines[i].MarkerAdd(marker);
            }
        }

        /// <summary>
        /// 只给从 <paramref name="fromIndex"/> 起的新行补级别着色（增量路径用；不清已有 marker）。
        /// </summary>
        private void ApplyLineColorsRange(IList<string> lines, int fromIndex)
        {
            var s = _editor;
            if (s == null || s.IsDisposed || lines == null) return;
            int lineCount = s.Lines.Count;
            for (int i = Math.Max(0, fromIndex); i < lines.Count && i < lineCount; i++)
            {
                int level = LineLevel(lines[i]);
                int marker = level == 3 ? MarkerError : level == 2 ? MarkerWarn : -1;
                if (marker >= 0) s.Lines[i].MarkerAdd(marker);
            }
        }

        // ─────────────────────────── 导出 ───────────────────────────

        /// <summary>导出当前可见（过滤后）的行另存为 .log，与日志格式完全一致，便于直接使用</summary>
        private void ExportVisibleLines()
        {
            var lines = FilterLines();
            if (lines.Count == 0)
            {
                MessageBox.Show(
                    LanguageManager.Instance.GetText("Editor.LogExportEmpty", "当前没有可导出的日志。"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Info", "提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string defaultName = string.IsNullOrEmpty(ExportFileName)
                ? $"GDPackEditor-log-{DateTime.Now:yyyyMMdd-HHmmss}.log"
                : ExportFileName;

            using (var dlg = new SaveFileDialog
            {
                Filter = LanguageManager.Instance.GetText("Editor.LogFilterLogFile",
                    "日志文件 (*.log)|*.log|所有文件 (*.*)|*.*"),
                FileName = defaultName,
                Title = LanguageManager.Instance.GetText("LogView.Export", "导出日志")
            })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

                try
                {
                    var sb = new StringBuilder();
                    if (!string.IsNullOrEmpty(ExportTitle)) sb.AppendLine(ExportTitle);
                    sb.AppendLine(string.Format(
                        LanguageManager.Instance.GetText("Editor.LogExportTime", "导出时间：{0}"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                    sb.AppendLine();
                    foreach (string l in lines) sb.AppendLine(l);

                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));

                    AppendImmediate(LogLevel.Info, string.Format(
                        LanguageManager.Instance.GetText("Editor.LogExportDone", "日志已导出：{0}"), dlg.FileName));
                }
                catch (Exception ex)
                {
                    AppendImmediate(LogLevel.Error, string.Format(
                        LanguageManager.Instance.GetText("Editor.LogExportFailed", "导出失败：{0}"), ex.Message));
                }
            }
        }

        /// <summary>面板自身动作（导出成功/失败）的回显：带时间戳与级别，立即刷新</summary>
        private void AppendImmediate(LogLevel level, string message)
        {
            string tag = level == LogLevel.Error ? "ERROR" : "INFO";
            Enqueue($"[{DateTime.Now:HH:mm:ss}] [{tag}] {message}");
            FlushNow(force: true);
        }

        // ─────────────────────────── 多语言 ───────────────────────────

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
            if (_filterBox == null || _filterBox.IsDisposed) return;
            _filterBox.PrefixText = LanguageManager.Instance.GetText("LogView.Filter", "过滤") + ":";
            _clearButton.Text = LanguageManager.Instance.GetText("LogView.Clear", "清空日志");
            _exportButton.Text = LanguageManager.Instance.GetText("LogView.Export", "导出日志");

            // 四级筛选项文本（等宽铺满，文本变长不会撑破控件，只是每格更挤一点）
            string[] levels = LevelFilterTexts();
            for (int i = 0; i < levels.Length && i < _levelFilter.Items.Count; i++)
                _levelFilter.Items[i].Text = levels[i];
            _levelFilter.Invalidate();

            // 强制重排 + 重绘：AntdUI 控件的排版/渲染缓存靠 EventType.LANG 更新
            // （见 LanguageManager.NotifyAntdUiLanguage）；此处兜底，避免"界面仍显示旧语言文字，
            //  直到切换选项卡才刷新"的现象（用户实测反馈）
            try { _toolbar?.PerformLayout(); } catch { }
            try { Invalidate(true); } catch { }

            LayoutToolbar();   // 按钮/过滤框宽度随文本变化，重排一次
        }
    }
}
