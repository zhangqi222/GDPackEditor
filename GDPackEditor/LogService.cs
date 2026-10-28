using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace GDPackEditor
{
    /// <summary>日志级别（与门面方法一一对应）</summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error,
        Fatal
    }

    /// <summary>
    /// 统一日志门面（替代 NLog）。双通道：
    /// ① 文件通道：%APPDATA%\GDPackEditor\GDPackEditor.log（与 settings.jsonc 同目录，不加子目录），
    ///    UTF-8 无 BOM，单文件按【行数】滚动（超出上限从文件头部裁剪，默认 2000 行）；
    /// ② UI 通道：<see cref="UiSink"/>，供当前活动操作的日志框显示（同一时刻只有一个活动操作）。
    ///
    /// 写入策略：500ms 防抖批量落盘（有新日志时重置计时器，到期一次性写出）；
    /// 程序退出（ProcessExit / ApplicationExit）与保存、固实、关档等关键节点必须调用 <see cref="Flush"/>，
    /// 保证不残留未保存日志。
    /// 行格式：[HH:mm:ss] [LEVEL] message；每次启动由 <see cref="StartSession"/> 写一行会话分隔（含日期）。
    /// </summary>
    public static class LogService
    {
        private const string FileName = "GDPackEditor.log";

        /// <summary>防抖间隔：距上次写入超过该时间即落盘</summary>
        private const int FlushDelayMs = 500;

        /// <summary>默认单文件行数上限</summary>
        public const int DefaultMaxLines = 2000;

        /// <summary>过程性进度行匹配（与 GuiProgressCallback 同一规则）：如 "正在解包: (123/45678) path"——
        /// 判定实现见 <see cref="ContainsProgressMarker"/>（手写扫描，不用正则：本判定在热路径上）。</summary>

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly object _lock = new object();
        private static readonly StringBuilder _pending = new StringBuilder();

        private static int _pendingLines;
        private static int _fileLines = -1;      // -1 表示尚未统计物理行数
        private static Timer _timer;
        private static bool _writeFailed;

        /// <summary>单文件行数上限（超出从头部裁剪）。取值来自设置项 LogMaxLines，并夹紧到 200~20000。</summary>
        public static int MaxLines
        {
            get
            {
                try { return Math.Max(10000, Math.Min(1000000, SettingsManager.LogMaxLines)); }
                catch { return DefaultMaxLines; }
            }
        }

        /// <summary>是否启用日志（关闭后既不落盘也不投递 UI）</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// 「保存日志」开关（设置项 LogFileEnabled）：控制主日志 GDPackEditor.log 是否写入。
        /// 关闭时**只停文件通道**——界面日志显示（UiSink）不受影响，照常实时可见。
        /// </summary>
        public static bool FileEnabled { get; set; } = true;

        /// <summary>
        /// 「单独保存操作日志」开关（设置项 PackageLogEnabled）：控制以包命名的日志文件
        /// （GDPackEditor-&lt;包名&gt;-&lt;日期&gt;.log，保存/固实成功时生成）是否写入。
        /// 关闭时不生成文件、待报告集合即时清空（防内存累积）；[SUMMARY] 汇总仍走主日志通道。
        /// </summary>
        public static bool PackageLogEnabled { get; set; } = true;

        /// <summary>UI 通道：由当前活动操作的进度回调注册/注销；为空时仅落盘</summary>
        public static Action<string> UiSink { get; set; }

        /// <summary>日志文件完整路径</summary>
        public static string LogFilePath => Path.Combine(SettingsManager.SettingsDirectory, FileName);

        /// <summary>
        /// 会话开始：统计既有文件行数并写入一行会话分隔（含日期，故行首时间戳只需 HH:mm:ss）。
        /// 程序启动时调用一次；同时注册进程退出兜底 Flush。
        /// </summary>
        public static void StartSession()
        {
            try { AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush(); } catch { }

            try
            {
                string path = LogFilePath;
                _fileLines = File.Exists(path) ? File.ReadAllLines(path, Utf8NoBom).Length : 0;
            }
            catch { _fileLines = -1; }

            WriteRaw(string.Format(Text("Log.SessionStart", "===== 会话开始 {0} ====="),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        public static void Debug(string message) => Write(LogLevel.Debug, message);
        public static void Info(string message) => Write(LogLevel.Info, message);
        public static void Warn(string message) => Write(LogLevel.Warn, message);
        public static void Error(string message) => Write(LogLevel.Error, message);
        public static void Fatal(string message) => Write(LogLevel.Fatal, message);

        /// <summary>
        /// 组装标准日志行：`[HH:mm:ss] [LEVEL] 内容`。
        /// 行前缀格式只在此一处定义 —— Write / WriteFileOnly / WriteUiOnly 与需要
        /// "界面行与落盘行完全一致"的调用方（GuiProgressCallback 的折叠分组分支等）统一走这里。
        /// </summary>
        public static string FormatLine(LogLevel level, string message)
            => $"[{DateTime.Now:HH:mm:ss}] [{LevelTag(level)}] {message}";

        /// <summary>写一行日志（带级别与时间戳；落盘 + UI 双通道）</summary>
        public static void Write(LogLevel level, string message)
            => EnqueueLine(FormatLine(level, message), toUi: true);

        /// <summary>
        /// 仅写文件通道、不投递 UI。供"已经自行把日志显示到界面"的日志源使用
        /// （编辑器日志面板 LogOutput、CLI 控制台输出），避免同一行在界面上重复出现；
        /// 落盘格式与 <see cref="Write"/> 完全一致。
        /// </summary>
        public static void WriteFileOnly(LogLevel level, string message)
            => EnqueueLine(FormatLine(level, message), toUi: false);

        /// <summary>写一行原始文本（调用方自行组织内容，如会话分隔行；双通道）</summary>
        public static void WriteRaw(string line) => EnqueueLine(line, toUi: true);

        /// <summary>
        /// 批量"详情行"（每文件一条、数量可能上千）：编辑器导入文件时的「文本编码转换: 路径（源→目标）」明细。
        /// 它与过程行性质相同（低频排查价值、极易淹没日志），故与过程行走同一套双开关分流。
        /// 该行文案已走多语言（LOG.TextTranscode），无法再靠单一中文字面量识别，故中英文前缀都列出。
        /// 判定实现见 <see cref="StartsWithDetailPrefix"/>（StartsWith，不用正则）。
        /// </summary>

        /// <summary>
        /// 是否"详情行"（过程性进度行如 `(123/45678)`、"正在解包: …"，以及每文件明细如「文本编码转换: …」）。
        /// 「日志显示详情」决定是否**显示**这类行、「详情记录在日志文件中」决定是否**记录到文件**。
        /// <para>⚠️ 本方法在每条记录/每行日志上都会被调用（解包 6 万条 ≈ 十几万次），故**不用正则**：
        /// 手写扫描与原子正则逐字符等价 —— `\d` 用 <see cref="char.IsDigit"/>（同为 Unicode Nd 类别）、
        /// `\s` 用 <see cref="char.IsWhiteSpace"/>、字面前缀用 <see cref="string.StartsWith(string, StringComparison)"/>。</para>
        /// </summary>
        public static bool IsProgressLine(string message)
            => message != null && (ContainsProgressMarker(message) || StartsWithDetailPrefix(message));

        /// <summary>行内是否含 `(` 数字 [空白] `/` [空白] 数字 `)`（等价原正则 `\(\d+\s*/\s*\d+\)`）</summary>
        private static bool ContainsProgressMarker(string s)
        {
            int idx = s.IndexOf('(');
            while (idx >= 0)
            {
                int i = idx + 1;
                int d1 = 0;
                while (i < s.Length && char.IsDigit(s[i])) { i++; d1++; }
                if (d1 > 0)
                {
                    while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                    if (i < s.Length && s[i] == '/')
                    {
                        i++;
                        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                        int d2 = 0;
                        while (i < s.Length && char.IsDigit(s[i])) { i++; d2++; }
                        if (d2 > 0 && i < s.Length && s[i] == ')') return true;
                    }
                }
                idx = s.IndexOf('(', idx + 1);
            }
            return false;
        }

        /// <summary>行首是否为详情前缀（等价原正则 `^(文本编码转换|Text transcoding): `）</summary>
        private static bool StartsWithDetailPrefix(string s)
            => s.StartsWith("文本编码转换: ", StringComparison.Ordinal)
               || s.StartsWith("Text transcoding: ", StringComparison.Ordinal);

        /// <summary>
        /// 日志用：把控制字符可视化（换行 → `\n`、回车 → `\r`、制表 → `\t`、其它 → `\xNN`），
        /// 便于在**单行**日志里看清换行/控制字符到底在哪（否则它们会让日志断行或不可见）。
        /// </summary>
        public static string VisualizeControlChars(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            bool need = false;
            foreach (char c in s) { if (char.IsControl(c)) { need = true; break; } }
            if (!need) return s;

            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (char.IsControl(c)) sb.Append("\\x").Append(((int)c).ToString("X2"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 日志用：把过长的值裁短，避免超长数组值（如 defaultRecipes 的几十条
        /// `records/...;records/...` 路径）刷屏。
        /// <para>规则：先做控制字符可视化；≤ max 字符原样返回；超过取**前 max 字符 + "..."**——
        /// 前截取让异常（粘连、截断）更容易被一眼发现；字段名与记录路径不经过本方法，始终完整显示。</para>
        /// </summary>
        public static string TruncateValueForLog(string s, int max = 50)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string vis = VisualizeControlChars(s);
            return vis.Length <= max ? vis : vis.Substring(0, max) + "...";
        }

        /// <summary>
        /// 包上下文标记：变更记录行靠它追溯"这些改动属于哪个包"。
        /// 打开包时输出一条 [PACKAGE] 边界行，之后的 [CHANGE-*] 行按时间顺序归属该包
        /// （不在每条变更行里重复包名，避免冗余）。
        /// </summary>
        public const string PackageMarker = "[PACKAGE]";

        /// <summary>当前包上下文（如 "xmod.arz（24456 个条目）"）；由打开包成功后设置，供裁剪时补上下文行</summary>
        public static string PackageContext { get; set; }

        // ===== 变更记录标记与动词 =====
        // 集中定义：标记是「仅看变更」过滤、导出变更清单、变更报告解析的唯一依据，散落各处会难以统一维护。

        /// <summary>变更记录标记：字段级改动（DBR 网格改值）</summary>
        public const string MarkField = "[field]";
        /// <summary>变更记录标记：行级改动（源码模式编辑）</summary>
        public const string MarkLine = "[line]";
        /// <summary>变更记录标记：文件级改动（导入 / 新建 / 删除）</summary>
        public const string MarkFile = "[file]";

        /// <summary>变更动词：新增（首字母沿用项目右侧修改列表的 a/d/m 约定）</summary>
        public const string VerbAdd = "[add]";
        /// <summary>变更动词：删除</summary>
        public const string VerbDel = "[del]";
        /// <summary>变更动词：修改</summary>
        public const string VerbMod = "[mod]";

        /// <summary>是否为变更记录行（「仅看变更」过滤与导出清单统一走此判断）</summary>
        public static bool IsChangeLine(string line)
            => line != null
               && (line.Contains(MarkField) || line.Contains(MarkLine) || line.Contains(MarkFile));

        // ===== 多语言文本与格式化 =====

        /// <summary>取多语言文本（缺失或语言未就绪时用中文兜底，保证不抛异常）</summary>
        private static string Text(string key, string fallback)
        {
            try { return LanguageManager.Instance.GetText(key, fallback); }
            catch { return fallback; }
        }

        /// <summary>行号多语言文本（如 "第 18 行" / "line 18"）</summary>
        public static string LineNumber(int lineNo)
            => string.Format(Text("Log.LineNumber", "第 {0} 行"), lineNo);

        /// <summary>
        /// 文件大小文本：仅用国际通用单位（B/KB/MB/GB），**不做多语言** —— 各语言环境均通用。
        /// 例：55MB、512KB、1.2GB。
        /// </summary>
        public static string FormatSize(long bytes)
        {
            const double KB = 1024d, MB = KB * 1024d, GB = MB * 1024d;
            if (bytes >= GB) return (bytes / GB).ToString("0.#") + "GB";
            if (bytes >= MB) return (bytes / MB).ToString("0.#") + "MB";
            if (bytes >= KB) return (bytes / KB).ToString("0.#") + "KB";
            return bytes + "B";
        }

        /// <summary>取文件大小文本（文件不存在或读取失败时返回空串）</summary>
        public static string FileSizeText(string path)
        {
            try { return File.Exists(path) ? FormatSize(new FileInfo(path).Length) : string.Empty; }
            catch { return string.Empty; }
        }

        // ===== 变更记录的收集与全量报告 =====

        /// <summary>单条变更记录（结构化）：实时日志行与保存时的全量报告都由它渲染，避免两处各写一套格式</summary>
        public class ChangeRecord
        {
            /// <summary>发生时间</summary>
            public DateTime Time = DateTime.Now;
            /// <summary>变更层级：<see cref="MarkField"/> / <see cref="MarkLine"/> / <see cref="MarkFile"/></summary>
            public string Kind;
            /// <summary>变更动词：<see cref="VerbAdd"/> / <see cref="VerbDel"/> / <see cref="VerbMod"/></summary>
            public string Verb;
            /// <summary>包内相对路径</summary>
            public string File;
            /// <summary>位置：字段名 / "第 N 行" / 空（文件级）</summary>
            public string Detail;
            /// <summary>旧值（新增时为 null）</summary>
            public string OldValue;
            /// <summary>新值（删除时为 null）</summary>
            public string NewValue;
        }

        /// <summary>
        /// 自上次生成报告以来登记的【全部】变更（不受实时日志 2000 行滚动裁剪影响）。
        /// 保存/固实成功后写入变更报告并清空，故每份报告 = 该次保存区间的完整改动清单。
        /// </summary>
        private static readonly List<ChangeRecord> _pendingChanges = new List<ChangeRecord>();

        /// <summary>已登记的待报告变更条数</summary>
        public static int PendingChangeCount { get { lock (_lock) return _pendingChanges.Count; } }

        /// <summary>最近一次变更报告的汇总文本（供调用方在自己的日志面板中显示）</summary>
        public static string LastSummary { get; private set; }

        /// <summary>
        /// 移除符合条件的待报告变更：用于"新增后又删除"这类净零操作
        /// （例如新建文件随即删除），避免变更报告出现与最终结果不符的条目。
        /// 传 null 表示该条件不参与筛选。
        /// </summary>
        public static void RemovePendingChange(string file, string kind, string verb)
        {
            if (string.IsNullOrEmpty(file)) return;
            lock (_lock)
            {
                _pendingChanges.RemoveAll(r => r != null
                    && string.Equals(r.File, file, StringComparison.OrdinalIgnoreCase)
                    && (kind == null || r.Kind == kind)
                    && (verb == null || r.Verb == verb));
            }
        }

        /// <summary>
        /// 登记一条变更：① 渲染为实时日志行（**仅落盘**，界面由各自窗口自行显示，避免同一行落盘两次）；
        /// ② 记入待报告集合（保存成功后生成全量报告）。返回渲染出的日志行，调用方可据此显示到自己的日志面板。
        /// </summary>
        public static string AddChange(ChangeRecord record)
        {
            if (record == null) return string.Empty;
            string line = RenderChange(record);
            lock (_lock) _pendingChanges.Add(record);
            WriteFileOnly(LogLevel.Info, line);
            return line;
        }

        /// <summary>渲染变更行正文（不含时间戳/级别：实时通道与报告各自补时间戳）</summary>
        public static string RenderChange(ChangeRecord r)
        {
            if (r == null) return string.Empty;
            if (r.Kind == MarkFile)
                return $"{MarkFile} {r.Verb} {r.File}";
            if (r.Kind == MarkLine)
            {
                string body = r.OldValue == null ? r.NewValue
                            : (r.NewValue == null ? r.OldValue : $"{r.OldValue} → {r.NewValue}");
                return $"{MarkLine} {r.Verb} {r.File} | {r.Detail} | {body}";
            }
            return $"{MarkField} {r.Verb} {r.File} | {r.Detail} | {r.OldValue} → {r.NewValue}";
        }

        /// <summary>
        /// 生成【全量变更报告】（保存 / 固实成功后调用）：区间内全部明细（按文件分组、不做任何截断）+ 汇总，
        /// 便于 MOD 作者直接用于发布说明。文件：`%APPDATA%\GDPackEditor\GDPackEditor-<包名>-<yyyyMMdd>.log`。
        /// 成功后清空待报告集合，并按 <see cref="MaxLines"/> 对该报告按行数裁剪（用户决定：不再按保留天数删除）。
        /// 无变更返回 null（不产生垃圾文件）。
        /// </summary>
        public static string WriteChangeReport(string packageName, string backupPath = null)
        {
            // 「单独保存操作日志」关闭：不生成包日志文件；清空待报告集合防止内存无限累积。
            // [SUMMARY] 汇总仍会经主日志通道输出（由「保存日志」开关 FileEnabled 控制）。
            if (!PackageLogEnabled)
            {
                lock (_lock) _pendingChanges.Clear();
                return null;
            }

            List<ChangeRecord> items;
            lock (_lock)
            {
                if (_pendingChanges.Count == 0) return null;
                items = new List<ChangeRecord>(_pendingChanges);
            }

            try
            {
                string safeName = MakeSafeFileName(string.IsNullOrWhiteSpace(packageName)
                    ? "GDPackEditor" : Path.GetFileNameWithoutExtension(packageName));
                string stamp = DateTime.Now.ToString("yyyyMMdd");
                // 按天一份、同日追加：避免"每保存一次就一个文件"的碎片化；
                // 每次保存以分节标题区分，既能整天复盘，也能定位到某一次保存。
                // 统一前缀 GDPackEditor-：一眼可辨"本工具生成的包日志"，也便于清理逻辑识别
                string reportPath = Path.Combine(LogDirectory, $"GDPackEditor-{safeName}-{stamp}.log");
                bool newReportFile = !File.Exists(reportPath);

                // 按文件分组（保持登记顺序，不用 LINQ 以降低依赖）
                var order = new List<string>();
                var groups = new Dictionary<string, List<ChangeRecord>>(StringComparer.OrdinalIgnoreCase);
                var addSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var modSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var delSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int contentCount = 0;

                foreach (var r in items)
                {
                    string key = r.File ?? string.Empty;
                    if (!groups.TryGetValue(key, out var lst))
                    {
                        lst = new List<ChangeRecord>();
                        groups[key] = lst;
                        order.Add(key);
                    }
                    lst.Add(r);

                    // 文件级计数：按文件去重（字段级/行级改动一律视为"修改"）
                    if (r.Verb == VerbAdd) addSet.Add(key);
                    else if (r.Verb == VerbDel) delSet.Add(key);
                    else modSet.Add(key);
                    if (r.Kind != MarkFile) contentCount++;
                }

                var sb = new StringBuilder();
                if (newReportFile)
                {
                    sb.AppendLine(Text("Log.Report.Title", "===== 变更报告 ====="));
                    sb.AppendLine(string.Format(Text("Log.Report.Package", "包：{0}"), packageName));
                    sb.AppendLine(string.Format(Text("Log.Report.Date", "日期：{0}"), DateTime.Now.ToString("yyyy-MM-dd")));
                    sb.AppendLine();
                }
                // 同日追加：分节前留 2 行空行，便于人眼一眼区分各次保存节点（用户反馈：首次生成正常，追加时节间无间距）
                if (!newReportFile)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                }
                sb.AppendLine(string.Format(Text("Log.Report.SaveSection", "===== 保存 {0} ====="), DateTime.Now.ToString("HH:mm:ss")));
                sb.AppendLine(string.Format(Text("Log.Report.Range", "区间：{0} ~ {1}"),
                    items[0].Time.ToString("HH:mm:ss"), items[items.Count - 1].Time.ToString("HH:mm:ss")));
                sb.AppendLine();
                sb.AppendLine(Text("Log.Report.Details", "----- 明细（按文件分组，完整不截断）-----"));
                foreach (var key in order)
                {
                    sb.AppendLine(key);
                    foreach (var r in groups[key])
                    {
                        if (r.Kind == MarkFile)
                            sb.AppendLine($"  [{r.Time:HH:mm:ss}] {MarkFile} {r.Verb}");
                        else if (r.Kind == MarkLine)
                        {
                            string body = r.OldValue == null ? r.NewValue
                                        : (r.NewValue == null ? r.OldValue : $"{r.OldValue} → {r.NewValue}");
                            sb.AppendLine($"  [{r.Time:HH:mm:ss}] {MarkLine} {r.Verb} {r.Detail} | {body}");
                        }
                        else
                            sb.AppendLine($"  [{r.Time:HH:mm:ss}] {MarkField} {r.Verb} {r.Detail} | {r.OldValue} → {r.NewValue}");
                    }
                    sb.AppendLine();
                }
                string summary = string.Format(Text("Log.Report.Summary",
                    "本次保存：涉及文件 {0} 个（新增 {1}、修改 {2}、删除 {3}）；内容级改动 {4} 处；变更记录合计 {5} 条"),
                    addSet.Count + modSet.Count + delSet.Count, addSet.Count, modSet.Count, delSet.Count,
                    contentCount, items.Count);
                sb.AppendLine(Text("Log.Report.SummaryHeader", "----- 汇总 -----"));
                sb.AppendLine(summary);
                if (!string.IsNullOrEmpty(backupPath))
                    sb.AppendLine(string.Format(Text("Log.Report.Backup", "修改前备份：{0}"), backupPath));

                string dir = LogDirectory;
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(reportPath, sb.ToString(), Utf8NoBom);   // 同日追加，不覆盖已有分节
                lock (_lock) _pendingChanges.Clear();
                TrimReportByLines(reportPath);   // 按 LogMaxLines 行数裁剪（用户决定：不再按保留天数删整份）

                LastSummary = summary;
                Write(LogLevel.Info, $"[SUMMARY] {summary}");   // 汇总同时进入实时日志（文件 + 主窗体日志框）
                // "修改前备份"只写日志/报告文件（便于回滚），不投递 UI 面板（用户要求：仅日志文件）
                if (!string.IsNullOrEmpty(backupPath))
                    WriteFileOnly(LogLevel.Info, string.Format(Text("Log.Report.Backup", "修改前备份：{0}"), backupPath));
                return reportPath;
            }
            catch (Exception ex)
            {
                // 报告失败绝不影响保存流程本身；但要**留痕**（否则报告长期写不出来用户也不知道）
                try { UiSink?.Invoke($"[{DateTime.Now:HH:mm:ss}] [WARN] 变更报告写入失败（不影响保存）：{ex.Message}"); } catch { }
                return null;
            }
        }

        /// <summary>日志所在目录（与配置文件同目录，不加子目录）</summary>
        public static string LogDirectory => Path.GetDirectoryName(LogFilePath);

        /// <summary>文件名安全化：包名可能含非法文件名字符，避免报告写不出去</summary>
        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "GDPackEditor";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>
        /// 变更报告按【行数】裁剪（用户决定：去掉"报告保留天数"，与主日志共用 LogMaxLines 上限）。
        /// 超出上限时从文件头部丢弃、仅保留末尾 MaxLines 行（各包日志按天追加分节，保留末尾即保留最近的保存分节）。
        /// 只处理调用方传入的报告文件，绝不动 GDPackEditor.log 本身；失败不影响保存流程。
        /// </summary>
        private static void TrimReportByLines(string reportPath)
        {
            try
            {
                if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath)) return;
                var all = File.ReadAllLines(reportPath, Utf8NoBom);
                if (all.Length <= MaxLines) return;

                var tail = new string[MaxLines];
                Array.Copy(all, all.Length - MaxLines, tail, 0, MaxLines);
                File.WriteAllLines(reportPath, tail, Utf8NoBom);
            }
            catch
            {
                // 裁剪失败不影响任何流程
            }
        }

        /// <summary>
        /// 清除日志目录下全部 .log 文件（主日志 GDPackEditor.log + 各包变更报告）。
        /// 顺序很关键：① 先丢弃待写缓冲并重置行数计数（否则缓冲里的旧行会在下次 Flush 写回来、
        /// 行数统计错位）；② 再逐个删除（单个失败只跳过该文件，例如被其它进程占用）；
        /// ③ 最后写一行清除分隔，使日志文件重新可用且可读出"被清过"。只删 .log，
        /// 绝不触碰 settings.jsonc 等其它文件。返回实际删除的文件数。
        /// </summary>
        public static int ClearLogFiles()
        {
            int deleted = 0;

            lock (_lock)
            {
                _pending.Clear();
                _pendingLines = 0;
                _fileLines = -1;   // -1 = 行数未知，下次裁剪时重新统计
            }

            try
            {
                string dir = LogDirectory;
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.log"))
                    {
                        try { File.Delete(f); deleted++; } catch { }
                    }
                }
            }
            catch
            {
                // 目录枚举失败时仍走 ③，保证日志通道可用
            }

            WriteRaw(string.Format(Text("Log.Cleared", "===== 日志已清除 {0} ====="),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

            return deleted;
        }

        /// <summary>
        /// 仅投递 UI 通道、**不落盘**。用于「日志显示详情」开启而「详情记录在日志文件中」关闭时的过程行：
        /// 界面照常显示明细，但不让 (N/M) 这类高频行污染日志文件。
        /// </summary>
        public static void WriteUiOnly(LogLevel level, string message)
            => EnqueueLine(FormatLine(level, message), toUi: true, toFile: false);

        private static void EnqueueLine(string line, bool toUi, bool toFile = true)
        {
            if (!Enabled || line == null) return;

            // UI 通道先投递：界面即时可见，且投递异常不影响落盘
            if (toUi)
            {
                try { UiSink?.Invoke(line); } catch { }
            }

            if (!toFile) return;

            // 「保存日志」关闭：只停文件通道，UI 显示照常
            if (!FileEnabled) return;

            lock (_lock)
            {
                _pending.Append(line).Append("\r\n");
                _pendingLines++;
                if (_timer == null)
                    _timer = new Timer(_ => Flush(), null, FlushDelayMs, Timeout.Infinite);
                else
                    _timer.Change(FlushDelayMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// 强制落盘：程序退出、保存/固实/关档等关键节点调用，保证不残留未保存日志。
        /// 同时按行数上限裁剪文件（超限则重写，仅保留最后 MaxLines 行）。
        /// </summary>
        public static void Flush()
        {
            string text = null;
            int lines = 0;
            lock (_lock)
            {
                if (_pendingLines > 0)
                {
                    text = _pending.ToString();
                    lines = _pendingLines;
                    _pending.Clear();
                    _pendingLines = 0;
                }
            }

            if (text != null)
            {
                try
                {
                    string path = LogFilePath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.AppendAllText(path, text, Utf8NoBom);
                    lock (_lock) { if (_fileLines >= 0) _fileLines += lines; }
                }
                catch (Exception ex)
                {
                    NotifyWriteFailed(ex);
                    return;
                }
            }

            // 主日志写满 MaxLines 行（设置「日志行数上限」，默认 10 万）→ **归档**为 GDPackEditor-<序号>.log 并新建继续。
            // 追加式分卷：永不重写文件、永不丢历史，写成本 O(新增行) 与文件大小无关。
            // 分卷阈值跟随设置项（原为硬编码 50000，未与设置联动）。
            try
            {
                lock (_lock)
                {
                    if (_fileLines >= MaxLines)
                    {
                        string path = LogFilePath;
                        if (File.Exists(path))
                            File.Move(path, NextArchiveName(), true);
                        _fileLines = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                // 归档失败只告警一次：主日志会继续追加（超出阈值也仍可写），不影响使用
                NotifyWriteFailed(ex);
            }
        }

        /// <summary>主日志分卷阈值已改为跟随设置项 <see cref="MaxLines"/>（见 Flush），常量删除。</summary>

        /// <summary>归档文件名：GDPackEditor-<序号>.log（序号 = 现有最大 + 1，保证统一且不覆盖既有文件）</summary>
        private static string NextArchiveName()
        {
            string dir = SettingsManager.SettingsDirectory;
            int max = 0;
            try
            {
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "GDPackEditor-*.log"))
                    {
                        string name = Path.GetFileName(f);
                        // 只认 GDPackEditor-<纯数字>.log（变更报告带包名/日期，不会误匹配）
                        string body = name.StartsWith("GDPackEditor-", StringComparison.Ordinal) && name.EndsWith(".log", StringComparison.Ordinal)
                            ? name.Substring("GDPackEditor-".Length, name.Length - "GDPackEditor-".Length - ".log".Length)
                            : null;
                        if (body != null && int.TryParse(body, out int n))
                            max = Math.Max(max, n);
                    }
            }
            catch { }
            return Path.Combine(dir, $"GDPackEditor-{max + 1:000}.log");
        }

        /// <summary>写盘失败只告警一次，之后静默降级为仅 UI 输出（不弹窗、不崩溃）</summary>
        private static void NotifyWriteFailed(Exception ex)
        {
            if (_writeFailed) return;
            _writeFailed = true;
            try
            {
                UiSink?.Invoke($"[{DateTime.Now:HH:mm:ss}] [WARN] 日志文件写入失败，后续日志仅显示在界面：{ex.Message}");
            }
            catch { }
        }

        private static string LevelTag(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "DEBUG";
                case LogLevel.Warn: return "WARN";
                case LogLevel.Error: return "ERROR";
                case LogLevel.Fatal: return "FATAL";
                default: return "INFO";
            }
        }
    }
}
