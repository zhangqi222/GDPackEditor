using System;
using System.Windows.Forms;

namespace GDPackEditor
{
    public class GuiProgressCallback : IProgressCallback, IDisposable
    {
        private AntdUI.Progress progressBar;
        /// <summary>日志面板（公共组件：自带过滤/清空/导出与级别着色，内部负责队列与节流刷新）</summary>
        private LogView logView;
        private Control statusLabel;
        /// <summary>
        /// 状态文字出口（新通道，Spin 遮罩流程使用）：收到 (百分比, 文字) 后由实现方自行调度到 UI 线程。
        /// 优先于 progressBar/statusLabel；设置本回调后旧字段不再参与刷新。
        /// </summary>
        private readonly Action<int, string> onStatus;
        /// <summary>本回调注册到 LogService.UI 通道的处理委托（Dispose 时用于判断是否仍为当前接收者）</summary>
        private readonly Action<string> _uiSinkHandler;
        /// <summary>进度刷新节流（与 CLI 共用同一实现，保证两端口径一致）</summary>
        private readonly ProgressThrottle _throttle = new ProgressThrottle();
        private System.Threading.CancellationTokenSource _cts;

        /// <summary>是否显示详细过程日志（解包/打包每文件的 "(N/M)" 行），默认关闭以提升性能</summary>
        public bool ShowDetailLog { get; set; } = false;

        // 过程/详情行的判定统一走 LogService.IsProgressLine（含 (N/M) 与每文件「文本编码转换: …」明细），
        // 避免此处另立一套正则导致"详情记录在日志文件中"开关在 GUI 路径失效（曾有此偏差）。
        //
        // 队列 / 定时器批量刷新 / 行数上限 / 行级着色 / 覆盖行语义 均已下沉到 LogView，
        // 本类只保留"哪些行该显示、进度怎么节流"这两件与具体操作相关的事。

        /// <summary>
        /// 旧构造：进度条 + 状态标签目标（编辑器导出弹窗、搜索页签仍在使用）。
        /// </summary>
        public GuiProgressCallback(AntdUI.Progress progressBar = null, LogView logView = null, Control statusLabel = null)
        {
            this.progressBar = progressBar;
            this.logView = logView;
            this.statusLabel = statusLabel;
            if (progressBar != null) progressBar.Animation = 0;
            _cts = new System.Threading.CancellationTokenSource();

            // 接管日志门面的 UI 通道：LogService 的日志行（含 Program.Log.* 与带级别的告警/错误）投递到本操作日志框
            _uiSinkHandler = OnLogLine;
            LogService.UiSink = _uiSinkHandler;
        }

        /// <summary>
        /// 新构造：状态文字走回调出口（主界面 4 页签 Spin 遮罩流程使用）。
        /// 回调在后台线程被调用，实现方需自行 BeginInvoke 调度到 UI 线程（如更新 Spin.Config.Text）。
        /// </summary>
        public GuiProgressCallback(LogView logView, Action<int, string> onStatus) : this(null, logView, null)
        {
            this.onStatus = onStatus;
        }

        /// <summary>当前操作的取消令牌源（Spin 遮罩取消按钮接线用：CancelTokenSource = TokenSource）</summary>
        public System.Threading.CancellationTokenSource TokenSource => _cts;

        /// <summary>当前操作的取消令牌</summary>
        public System.Threading.CancellationToken Token => _cts.Token;

        /// <summary>是否已请求取消</summary>
        public bool IsCancellationRequested => _cts.IsCancellationRequested;

        /// <summary>请求取消当前操作</summary>
        public void Cancel()
        {
            try { _cts.Cancel(); } catch { }
        }

        /// <summary>重置取消状态（每次开始新操作前调用，获得全新的令牌）</summary>
        public void Reset()
        {
            FlushLog();
            _throttle.Reset();   // 新操作从"未上报过"开始（首条必刷）
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
            _cts = new System.Threading.CancellationTokenSource();
        }

        public void Dispose()
        {
            // 仅当自己仍是当前 UI 通道接收者时才注销，避免注销掉后来注册的方案
            if (ReferenceEquals(LogService.UiSink, _uiSinkHandler))
                LogService.UiSink = null;
            FlushLog();
            try { _cts.Dispose(); } catch { }
        }

        /// <summary>
        /// 日志门面的 UI 通道回调：交给日志面板入队（后台线程零阻塞，面板内部按 200ms 节流重绘）。
        /// 替代原 NLog 目标（原实现会 LoggingRules.Clear() 造成全局副作用，且丢弃 Info/Debug）。
        /// </summary>
        private void OnLogLine(string line) => logView?.Enqueue(line);

        /// <summary>
        /// 把待显示日志刷到界面。<paramref name="force"/>=false 时由 LogView 节流：
        /// 逐条警告（解包大包时可达数万条）若每条都同步全量重绘，内存与界面都会被拖垮。
        /// </summary>
        public void FlushLog(bool force = true) => logView?.FlushNow(force);

        public void Report(int percentage, string message) => ReportCore(percentage, message, false);

        /// <summary>
        /// 日志/进度统一入口。
        /// alreadyLoggedToFile=true 表示调用方已按具体级别写入文件通道（如错误=ERROR、警告=WARN），
        /// 此处只负责界面显示，避免同一行在日志文件中重复出现。
        /// </summary>
        private void ReportCore(int percentage, string message, bool alreadyLoggedToFile)
        {
            int clamped = Math.Max(0, Math.Min(100, percentage));
            bool isEnd = clamped >= 100;

            // 过程行判定只做一次（本方法在解包 6 万条等热路径上被高频调用，重复判定会翻倍开销）
            bool isProgressLine = LogService.IsProgressLine(message);

            // 过程性进度行（如 "正在解包/正在打包/导入: (N/M) path"）：数量巨大且无排查价值。
            // 两个开关各管一条通道：「日志显示详情」管**界面**是否显示、「详情记录在日志文件中」管**是否落盘**。
            if (!ShowDetailLog && isProgressLine)
            {
                // 文件通道：开启"详情记录在日志文件中"时逐行落盘
                // （必须放在下面的节流 return 之前，否则一部分行会被节流吞掉）
                if (SettingsManager.LogDetailToFile)
                    LogService.WriteFileOnly(LogLevel.Info, message);

                // 界面通道：节流（首/末条或 Δ≥1%）后才更新进度出口，
                // 其余在后台线程直接返回，零 UI 开销，避免高频同步 Invoke 阻塞后台解包线程。
                if (!_throttle.ShouldReport(clamped))
                    return;

                if (onStatus == null && progressBar?.InvokeRequired == true)
                {
                    Action action = () => UpdateProgressOnly(clamped, message);
                    try { progressBar?.BeginInvoke(action); } catch { }
                    return;
                }
                UpdateProgressOnly(clamped, message);
                return;
            }

            // 日志统一漏斗：操作日志 -> LogService（落盘 + 经 UiSink 回投到本日志框显示）。
            // LogService 内部为入队 + 500ms 防抖落盘，后台线程调用不会阻塞解包/打包；
            // 非过程行（开始/完成/警告/错误）低频，立即刷新日志框保证即时可见；过程行交给面板的定时器批量刷新。
            if (message != null && !alreadyLoggedToFile)
            {
                // 过程行且"未开详情记录到文件"：界面照常显示，但不落盘（避免污染日志文件）
                if (isProgressLine && !SettingsManager.LogDetailToFile)
                    LogService.WriteUiOnly(LogLevel.Info, message);
                else
                    LogService.Write(LogLevel.Info, message);
                if (!isProgressLine || isEnd)
                    FlushLog();
            }

            // 节流：进度出口仅在 百分比变化≥1% / 首条 / 末条 / 进度回退 时刷新（与 CLI 同一实现）。
            // 用 BeginInvoke 异步投递（不等待 UI 执行完），后台解包线程在任何情况下都不被 UI 阻塞
            if (_throttle.ShouldReport(clamped))
            {
                if (onStatus == null && (progressBar?.InvokeRequired == true || statusLabel?.InvokeRequired == true))
                {
                    Action action = () => UpdateProgressOnly(clamped, message);
                    try { progressBar?.BeginInvoke(action); } catch { }
                    return;
                }
                UpdateProgressOnly(clamped, message);
            }
        }

        /// <summary>仅更新进度出口（onStatus 回调 / 状态标签 / 进度条），不写日志</summary>
        private void UpdateProgressOnly(int clamped, string message)
        {
            if (onStatus != null)
            {
                // 新通道：实现方自行调度到 UI 线程（如 BeginInvoke 更新 Spin.Config.Text）
                onStatus(clamped, message);
                return;
            }
            if (statusLabel != null)
                statusLabel.Text = message;
            if (progressBar != null)
                progressBar.Value = clamped / 100f;
        }

        /// <summary>
        /// 仅更新进度条与状态标签，不写日志（用于过程性静默进度，如解包前的记录扫描）。
        /// 与 <see cref="Report"/> 共用同一节流：原实现每次静默进度都刷 UI（扫描阶段条目级调用时会刷成高频 BeginInvoke）。
        /// </summary>
        public void UpdateProgress(int percentage, string message)
        {
            int clamped = Math.Max(0, Math.Min(100, percentage));
            if (!_throttle.ShouldReport(clamped)) return;
            UpdateProgressDispatch(clamped, message);
        }

        /// <summary>投递一次进度刷新（已过节的）：旧目标跨线程时异步投递，避免阻塞后台线程</summary>
        private void UpdateProgressDispatch(int clamped, string message)
        {
            if (onStatus == null && (progressBar?.InvokeRequired == true || statusLabel?.InvokeRequired == true))
            {
                try { progressBar?.BeginInvoke((Action)(() => UpdateProgressOnly(clamped, message))); } catch { }
                return;
            }
            UpdateProgressOnly(clamped, message);
        }

        public void ReportLog(string message)
        {
            Report(0, message);
            FlushLog();
        }

        public void ReportError(string error)
        {
            // 级别前缀走多语言（原为写死中文"错误: "，英文环境会夹中文）
            string text = string.Format(
                LanguageManager.Instance.GetText("Log.PrefixError", "错误: {0}"), error);
            LogService.Write(LogLevel.Error, text);   // 文件通道按 ERROR 级别记录
            ReportCore(0, text, alreadyLoggedToFile: true);
            FlushLog();
        }

        public void ReportWarning(string warning, string groupKey = null)
        {
            // 级别前缀走多语言（原为写死中文"警告: "，英文环境会夹中文）
            string text = string.Format(
                LanguageManager.Instance.GetText("Log.PrefixWarn", "警告: {0}"), warning);

            if (groupKey != null && logView != null)
            {
                // 带分组键（记录名/文件名）：行文本走文件通道落盘、界面直接入队（折叠分组信息不经
                // UiSink 文本通道流转）。行格式与 LogService.Write 完全一致（[HH:mm:ss] [WARN] 前缀）——
                // 该前缀由 LogService 在其内部添加，因此绕过 UiSink 的入队侧必须用同一 FormatLine
                // 自行拼齐：缺前缀时子行没有时间戳，组头行也提取不到时间标签（头行会带前导空格）。
                // 子行在行首带 "- " 标记：与组头行（文件名+问题总数行）区分层级（用户决策：不缩进，行首短横）。
                // 仅作用于界面/导出（二者同源）；LogService 自动日志文件保持纯净——该文件本无组头行，短横无层级含义。
                LogService.WriteFileOnly(LogLevel.Warn, text);
                logView.Enqueue("- " + LogService.FormatLine(LogLevel.Warn, text), groupKey: groupKey);
                // 警告可能逐条高频出现（解包大包的内容检测）：走节流刷新，最终一致性由操作结束时的强制刷新保证
                FlushLog(force: false);
                return;
            }

            LogService.Write(LogLevel.Warn, text);    // 文件通道按 WARN 级别记录
            ReportCore(0, text, alreadyLoggedToFile: true);
            // 警告可能逐条高频出现（解包大包的内容检测）：走节流刷新，最终一致性由操作结束时的强制刷新保证
            FlushLog(force: false);
        }

        /// <summary>
        /// 覆盖行（界面替换上一行显示的瞬态进度，如"处理原始记录: 100/100"）。
        /// 【约定】只作用于界面，不落盘：否则每个进度点都会写一行，日志文件会被淹没。
        /// </summary>
        public void LogOverwrite(string message) => logView?.Enqueue(message, overwriteLast: true);

        public void LogFinalize()
        {
            logView?.RequestFinalize();   // 操作收尾：封口并收起末尾折叠组（随后强制刷新渲染）
            FlushLog();
        }
    }
}
