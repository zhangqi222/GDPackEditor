using System;
using ScintillaNET;

namespace GDPackEditor
{
    /// <summary>
    /// 日志行类型（行集合的并行元数据，供折叠分组与渲染使用）。
    /// </summary>
    internal enum LogRowKind
    {
        /// <summary>普通日志行</summary>
        Normal = 0,
        /// <summary>折叠组头行（真实行：参与着色、级别筛选、导出与行数上限）</summary>
        GroupHeader = 1,
        /// <summary>折叠组子行（组内原始警告/错误行）</summary>
        GroupChild = 2,
    }

    /// <summary>
    /// 一个折叠组 = 显示流中**相邻**的同 groupKey（记录名/文件名）警告/错误行段。
    /// 同名行一旦被普通行/别的组隔开即视为不连续，各自成组（不跨段合并、不重排时间线）。
    /// </summary>
    internal class LogFoldGroup
    {
        /// <summary>分组键（记录名/文件名），生成侧打标传入，不从日志文本解析</summary>
        public string Key;
        /// <summary>组头行在行集合中的下标；头部裁剪后可能为负 → 组失效（不再续组/不再渲染）</summary>
        public int HeadLineIndex = -1;
        /// <summary>组首行的时间戳标签（"[HH:mm:ss]"，取自首条子行）</summary>
        public string TimeTag = "";
        /// <summary>组内最高级别标签（"WARN"/"ERROR"，头行着色与级别筛选联动靠它）</summary>
        public string LevelTag = "WARN";
        /// <summary>组内子行数（组头行"（N 个问题）"的 N）</summary>
        public int ChildCount;
        /// <summary>是否已封口（封口后同名子行继续到来时静默并入，只更新头行文本，不重复收起）</summary>
        public bool Collapsed;
        /// <summary>折叠结构是否已应用（只在首次渲染时收起一次；头行后续刷新不重设，保留用户手动开合）</summary>
        public bool FoldApplied;
    }

    /// <summary>
    /// 日志折叠**公共支撑**：折叠 UI 初始化、组头行文本构造、折叠状态设置。
    /// 主界面 <see cref="LogView"/> 与 ARZ 编辑器日志区（同方案独立实现）共用，保证两处折叠行为一致。
    ///
    /// <para>设计要点：</para>
    /// <para>① 文件名/记录名由生成侧打标（ReportWarning 的 groupKey 参数），**不从日志文本解析**——
    /// 日志文案随多语言变化，解析文本会把分组做脆；</para>
    /// <para>② 折叠结构：头行 FoldLevel=1025|Header、子行 FoldLevel=1026（大于头行才可折叠）、
    /// 普通行保持默认 1024 —— 组与组之间天然互不嵌套、互不吞并；</para>
    /// <para>③ 收起后仅显示头行自身（"（N 个问题）"计数已含在头行文本里），不写折叠概述文本
/// ——概述信息冗余，只留计数更简洁直观（2026-10-13 用户决策）。</para>
    /// </summary>
    internal static class LogFoldSupport
    {
        /// <summary>折叠组头行的 FoldLevel（Scintilla 默认级别为 1024，头行需高于普通行）</summary>
        public const int FoldLevelHeader = 1025;

        /// <summary>折叠组子行的 FoldLevel（须大于头行，折叠范围才成立）</summary>
        public const int FoldLevelChild = 1026;

        /// <summary>
        /// 折叠 UI 初始化（一次性）：折叠边距 + 点击收放 + 折叠符号样式。
        /// 调用时机：Scintilla 基础配置（ApplyReadOnlyLog 等）之后；颜色随后由 <see cref="ApplyFoldColors"/> 跟随主题。
        /// </summary>
        public static void ConfigureFoldUi(Scintilla ed)
        {
            if (ed == null || ed.IsDisposed) return;

            // 折叠边距：用边距 1（边距 0 已被"隐藏行号"置零占用）。
            // Mask 只放行折叠符号槽位（25~31），与警告/错误 marker 槽（0/1）互不干扰；
            // Sensitive 让鼠标可点击 +/-，AutomaticFold.Click 让点击自动收放。
            var margin = ed.Margins[1];
            margin.Type = MarginType.Symbol;
            margin.Mask = Marker.MaskFolders;
            margin.Sensitive = true;
            margin.Cursor = MarginCursor.Arrow;
            margin.Width = DpiManager.S(13);

            // 只开 Click：不开 Show（滚动到折叠行不自动展开，避免"滚动到底误展开末尾折叠组"）；
            // 不开 Change（折叠结构变化时自动展开会打断程序控制的收起状态）。
            ed.AutomaticFold = AutomaticFold.Click;
            // 收起组之后在其下方画一条横线，视觉上提示"下面还有被折叠的内容"
            ed.SetFoldFlags(FoldFlags.LineAfterContracted);

            // 折叠符号：只有组头行（真正的折叠点）显示 +/- 方框；子行相关槽位全部置空。
            // 原因：折叠点只有组头行，子行上出现方框会让人误以为可点击——实测组尾子行显示过
            // 无法点击的"□−"（尾部槽位曾被映射成 BoxMinusConnected），置空后子行彻底干净，
            // 子行归属靠行首"- "文本标记与展开后的层级关系表达。
            ed.Markers[Marker.Folder].Symbol = MarkerSymbol.BoxPlus;                   // 收起的组头行（唯一可点击的折叠点）
            ed.Markers[Marker.FolderOpen].Symbol = MarkerSymbol.BoxMinus;              // 展开的组头行
            ed.Markers[Marker.FolderEnd].Symbol = MarkerSymbol.Empty;                  // 折叠点兼上一组收尾（组不嵌套，永不出现）
            ed.Markers[Marker.FolderOpenMid].Symbol = MarkerSymbol.Empty;              // 同上（展开态）
            ed.Markers[Marker.FolderMidTail].Symbol = MarkerSymbol.Empty;              // 组尾子行（折叠范围在下方延续）
            ed.Markers[Marker.FolderTail].Symbol = MarkerSymbol.Empty;                 // 组尾子行（折叠到此结束）
            ed.Markers[Marker.FolderSub].Symbol = MarkerSymbol.Empty;                  // 组中段子行

            ApplyFoldColors(ed);
        }

        /// <summary>
        /// 折叠配色跟随明暗主题。每次重绘时调用（与 marker 颜色同一刷新机制）：
        /// StyleClearAll / 主题切换会重置颜色，重设后下一条日志自动跟随。
        /// <para>含折叠边距**整列背景**（SetFoldMarginHighlightColor）：此前只设了 +/- 符号颜色，
        /// 边距列仍用 Scintilla 默认浅色——暗色模式下出现刺眼亮条（2026-10-13 用户反馈）；
        /// 背景融入正文底色、折叠线用中性边框色，明暗两种模式下都协调。</para>
        /// </summary>
        public static void ApplyFoldColors(Scintilla ed)
        {
            if (ed == null || ed.IsDisposed) return;
            for (int i = Marker.FolderEnd; i <= Marker.FolderOpen; i++)
            {
                // 符号配色跟正文走（EditorFore 线条 + EditorBack 底）：此前 BorderColor 线 + BgContainer 底
                // 在暗色模式下"灰上灰"对比不足看不清（2026-10-13 用户反馈）；正文同款高对比明暗都清晰，
                // 且底色与下方边距列背景（SetFoldMarginHighlightColor=EditorBack）完全一致，无色块感。
                ed.Markers[i].SetForeColor(ThemeManager.EditorFore);
                ed.Markers[i].SetBackColor(ThemeManager.EditorBack);
            }

            // 折叠边距整列：主体背景融入正文底色；折叠线（收起组下方的提示横线）用中性边框色。
            // Scintilla 用这两个专门 API 控制边距列（不走 Style，StyleClearAll 不影响），必须显式设置。
            ed.SetFoldMarginColor(true, ThemeManager.BorderColor);
            ed.SetFoldMarginHighlightColor(true, ThemeManager.EditorBack);
        }

        /// <summary>
        /// 把组头行的折叠结构设好并收起（组一有子行就折叠，打包过程中也保持收起；幂等）。
        /// 收起后仅显示头行自身——"（N 个问题）"计数已含在头行文本里，不写折叠概述文本。
        /// 调用方用 <see cref="LogFoldGroup.FoldApplied"/> 门控只应用一次：头行后续文本刷新不重设折叠，
        /// 用户手动展开不会被下一批刷新覆盖（组封口时由待收起队列统一收起）。
        /// </summary>
        /// <param name="ed">目标 Scintilla</param>
        /// <param name="lineIndex">头行行号（无过滤时与行集合下标一一对应）</param>
        public static void ApplyFoldToHeader(Scintilla ed, int lineIndex)
        {
            if (ed == null || ed.IsDisposed || lineIndex < 0 || lineIndex >= ed.Lines.Count) return;

            var line = ed.Lines[lineIndex];
            // 折叠结构：头行 1025|Header（下一行子行 1026 > 1025，折叠才成立）
            line.FoldLevel = FoldLevelHeader;
            line.FoldLevelFlags = FoldLevelFlags.Header;

            // 真正收起（SCI_FOLDLINE）。注意：SCI_SETFOLDEXPANDED 只改"账面"展开标记、
            // 不会隐藏/显示任何行（实测出现"符号是 + 但子行全部可见"的假收起），必须用 FoldLine。
            line.FoldLine(FoldAction.Contract);
        }

        /// <summary>
        /// 组头行文本：`[HH:mm:ss] [级别] 记录名（N 个问题）`。
        /// 级别标签取组内最高（错误 &gt; 警告），头行的级别着色与四级筛选因此整组联动。
        /// </summary>
        public static string BuildHeaderText(LogFoldGroup g)
        {
            string suffix = LanguageManager.Instance.GetText("LogView.FoldGroupSuffix", "（{0} 个问题）");
            return $"{g.TimeTag} [{g.LevelTag}] {g.Key}{string.Format(suffix, g.ChildCount)}";
        }

        /// <summary>级别标签 → 级别权重（与四级筛选的 LineLevel 语义一致：ERROR/FATAL=3 &gt; WARN=2）</summary>
        public static int LevelRank(string levelTag)
        {
            switch (levelTag)
            {
                case "ERROR":
                case "FATAL": return 3;
                case "WARN": return 2;
                default: return 1;
            }
        }

        /// <summary>
        /// 从行文本提取行首 "[时间戳] [级别]" 两个标签（组头行用首条子行的时间戳；FATAL 归并为 ERROR，与筛选语义一致）。
        /// 主界面 LogView 与编辑器日志面板共用（LogView 的私有实现公共化）。
        /// </summary>
        public static void ExtractLeadTags(string line, out string timeTag, out string levelTag)
        {
            timeTag = string.Empty;
            levelTag = "WARN";
            if (string.IsNullOrEmpty(line)) return;
            // 子行带行首 "- " 标记（与组头行区分层级）：提取标签前先剥掉，头行才能取到时间/级别
            if (line[0] == '-') { line = line.TrimStart('-', ' '); if (line.Length == 0) return; }
            if (line[0] != '[') return;
            int c1 = line.IndexOf(']');
            if (c1 < 0) return;
            timeTag = line.Substring(0, c1 + 1);
            int p = c1 + 1;
            while (p < line.Length && line[p] == ' ') p++;
            if (p < line.Length && line[p] == '[')
            {
                int c2 = line.IndexOf(']', p);
                if (c2 > p)
                {
                    string tag = line.Substring(p + 1, c2 - p - 1);
                    levelTag = tag == "FATAL" ? "ERROR" : tag;
                }
            }
        }
    }
}
