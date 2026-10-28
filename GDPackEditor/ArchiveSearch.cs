using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace GDPackEditor
{
    /// <summary>包类型</summary>
    public enum ArchiveKind
    {
        Arz,
        Arc
    }

    /// <summary>
    /// 跨包搜索参数（对一个包生效）。
    /// <para>**漏斗式条件**：<see cref="NameCondition"/> 与 <see cref="ContentCondition"/> 可单独使用，也可同时使用
    /// ——同时使用时先按名字筛（**不读数据**）、只对剩下的读内容。两者都空时引擎视为"无有效条件"直接返回空。</para>
    /// </summary>
    public sealed class ArchiveSearchOptions
    {
        /// <summary>
        /// 文件名筛选条件（null/空 = 不筛名字）。语法见 <see cref="SearchCondition"/>：
        /// 空格=与、`|`=或、`"…"`=整体短语、`*`/`?`=通配、词首 `-`/`!`=排除。
        /// </summary>
        public string NameCondition;

        /// <summary>
        /// 内容匹配条件（null/空 = 不搜内容）。语法同 <see cref="NameCondition"/>（含通配符；引号内为字面量）。
        /// </summary>
        public string ContentCondition;

        /// <summary>
        /// **目录排除**（null/空 = 不生效）：包文件**完整磁盘路径**命中任一排除条件 → 该包**及其所在子树**不扫描。
        /// <para>语法同包含条件（`*`/`?` 通配、空格=与、`|`=或、引号=字面量；`|` 语义 = "A 和 B 都不要"）。
        /// ⚠ 由**枚举阶段**使用（GUI 侧 <c>CollectArchiveFiles</c>），引擎内不消费；
        /// 大小写**固定不敏感**（Windows 路径习惯），`\` 与 `/` 由调用方归一后等价。</para>
        /// </summary>
        public string DirExcludeCondition;

        /// <summary>
        /// **文件名排除**（null/空 = 不生效）：包内相对路径命中任一排除条件 → 该条目整条丢弃（不读数据）。
        /// <para>大小写**固定不敏感**（批21 起：Aa 开关只管内容，见 <see cref="CaseSensitive"/>）。</para>
        /// </summary>
        public string NameExcludeCondition;

        /// <summary>
        /// **内容排除**（null/空 = 不生效）：**文件级**——全文任一行命中任一排除条件 → 整条记录丢弃
        /// （即使包含条件命中也不出现在结果里，不计入命中数）。
        /// <para>⚠ 不能按"行"排除：包含条件是"任一行满足即命中"，行级排除的"该行不含排除词"几乎永远成立，
        /// 等于没排除（方案 §30 与用户确认的口径）。仅对已通过包含筛选的文件多一次行扫描。</para>
        /// </summary>
        public string ContentExcludeCondition;

        /// <summary>区分大小写（默认不区分，与编辑器顶部工具条口径一致）。
        /// <para>批21 起**仅作用于内容条件**（包含/排除）；文件名条件与名称排除**固定不敏感**
        /// —— UI 的 Aa 开关放在内容行，语义与位置一致（方案 plan_20261008 §3.1）。</para></summary>
        public bool CaseSensitive = false;

        /// <summary>
        /// **完全匹配（全）**：内容包含条件按**字面字符串完全相等**判定（方案 plan_20261008 §3）。
        /// <list type="bullet">
        /// <item>dbr 行先**剥掉行尾结束逗号**（与残留 <c>\r</c>，AGENTS §8.2：原数据每行结尾通常有一个逗号，
        /// 不剥掉则 `爆破者` 对 `爆破者,` 永不相等 → 搜不到）</item>
        /// <item>词元带逗号 → 与剥尾后的整行相等（词元行尾逗号同样剥掉，`k,v,` == `k,v`）</item>
        /// <item>词元不带逗号 + dbr 行 → 按逗号**分段**，任一段与词元相等即命中
        /// （字段名 + 所有字段值都比；dbr 值可多值 `1,2,3` = 三段）</item>
        /// <item>非 dbr（tpl/txt/lua）→ 整行相等</item>
        /// <item>`*`/`?` 按**字面量**，不再通配；与/或/引号语法仍有效</item>
        /// <item>反查（本地化反查，批23 v 级）：勾「全」时反查对译名 v 的求值同样走 exact
        /// （v 与词元**完全相等**才命中，<c>BuildLangReverseTagList</c> 同参透传）；
        /// 引擎侧反查 tag 集（<see cref="ContentGroupTags"/>）的行命中判据为**整词边界**包含，
        /// dbr 逗号分隔值天然满足 tag 的词边界</item>
        /// </list>
        /// <para>仅作用于**内容包含条件**：内容排除（<see cref="ContentExcludeCondition"/>）、
        /// 文件名条件、名称排除均不受影响（排除保持包含匹配 —— 用户定稿，排除是扩展非核心）。</para>
        /// <para>false（默认）= 走原有"包含匹配"分支，行为与引入前逐字节一致。</para>
        /// </summary>
        public bool ContentExactMatch = false;

        /// <summary>
        /// 内容搜索：内容列**取前几处命中行本身**（以 `" / "` 分隔；≤ 命中数）。
        /// <para>例如 `5` = 命中 12 次时显示前 5 处；命中 3 次就显示 3 处。`0` 或负 = 按 5 处理。
        /// 命中数**始终统计全部**（不受此值影响）。</para>
        /// </summary>
        public int MaxContentHits = 5;

        /// <summary>
        /// 内容搜索：上下文**总长上限**（0 = 不限）。
        /// <para>⚠ 为什么必须有：AntdUI 的单元格测量会把文本交给 GDI+ `MeasureString`，**"多行 + 超长"** 时
        /// GDI+ 换行排版溢出、抛 `ExternalException 0x80004005`，且异常发生在 UI 线程 → 直接闪退。
        /// 实测（真实包）：40,905 字符的上下文必崩、32,000 字符正常、去换行后 40,905 也正常。</para>
        /// <para>因此本引擎产出的上下文**单行化 + 限长**：既能给表格安全显示，又保留命中处可读性。</para>
        /// </summary>
        public int MaxContextChars = 500;

        /// <summary>内容搜索：单文件扫描上限（超过则跳过该文件，避免为超大文件拖垮内存/耗时）</summary>
        public long MaxScanBytes = TextFileHelper.MaxDetectBytes;

        /// <summary>
        /// 是否读取 `.dbr` 的**说明 / 本地化名称 / 模板**（三项由同一次读取 + 同一次解析产出）。
        /// <para>成本提示：只筛文件名时开启它 = 每条命中额外读一次全文；有内容条件时本来就要读全文，几乎零额外成本。</para>
        /// <para>只对 `.dbr` 生效，非 dbr 文件不读取、不解析。</para>
        /// </summary>
        public bool WithDbrMeta = false;

        /// <summary>语言包字典（tag → 本地化名），由调用方在每次搜索开始时构建一次并传入；null = 不做本地化（回退原始 tag）</summary>
        public Dictionary<string, string> LanguageTags;

        /// <summary>
        /// **本地化搜索 · v 级反查**（批23）：内容包含条件对语言包每条「k=v」的 **v 整体求值**
        /// （与/或/全同一套 <c>MatchLine</c> 语义），v 满足条件组 → 该条目 tag 收集进本集合。
        /// <para>行命中判据**扩展**（OR，不减少任何原有命中）：行按原条件满足 **或** 行内含本集合任一 tag
        /// （**整词边界**，<c>SearchCondition.IndexOfAnyTag</c>）。
        /// null/空 = 不启用本地化搜索，引擎行为与未引入该字段前**逐字节一致**。
        /// 仅作用于**内容包含**；排除条件与文件名条件不受影响。</para>
        /// </summary>
        public string[] ContentGroupTags;
    }

    /// <summary>
    /// 搜索条件的分词结果（文件名框与内容框共用一套语法）。
    /// <para>外层 = **或组**：任一或组满足即算条件满足；内层 = **与项**：须全部满足。</para>
    /// </summary>
    internal sealed class SearchTerm
    {
        /// <summary>词面（引号内空格已并入；`-`/`!` 是普通字符——排除已统一收敛到独立的"排除"框）</summary>
        public string Text;
        /// <summary>是否含通配符 `*` / `?`（引号内的不算；见 <see cref="Quoted"/>）</summary>
        public bool Wildcard;
        /// <summary>该项是否被引号包裹过（`"…"`）—— 引号内一律**字面量**：不通配</summary>
        public bool Quoted;
    }

    /// <summary>
    /// 搜索条件的解析与匹配（唯一口径，见方案 §21.2 / §30）：
    /// <list type="bullet">
    /// <item>归一：全角 `｜`→`|`、`－`/`—`/`–`→`-`、`！`→`!`、`“”＂`→`"`、全角空格→半角空格</item>
    /// <item>空格 = **与**；`|` = **或**（优先级最低 → 先切或组，组内再取与）</item>
    /// <item>`"…"` = 整体短语（不分词）+ **字面量**（内含的 `*`/`?` 不当通配，等于给了转义手段，如 `"SELECT *"`）</item>
    /// <item>`*` / `?` = 通配，**文件名框与内容框都支持**；锚定规则见 <see cref="AnchorPattern"/>：
    /// 与"包含"一致（`tag*` / `tag*class` 不必再手写两端的 `*`），只有**以 `*` 开头**时才按"后缀"</item>
    /// <item>**排除（逻辑非）不在这里**：老版"词首 `-` / `!`"已移除，`-`/`!` 是普通字符；
    /// 排除统一走独立的"排除"输入框（目录/文件名/内容各一个，见方案 §30），由调用方解析成独立条件</item>
    /// <item>**不做**：括号、嵌套、正则、字符集、转义符</item>
    /// </list>
    /// </summary>
    internal static class SearchCondition
    {
        /// <summary>解析条件为"或组"列表（空条件 → 空列表 = 该条件不生效）</summary>
        public static List<List<SearchTerm>> Parse(string condition, bool allowWildcard)
        {
            var groups = new List<List<SearchTerm>>();
            if (string.IsNullOrWhiteSpace(condition)) return groups;

            var current = new List<SearchTerm>();
            var sb = new StringBuilder();
            bool inQuote = false;
            bool quotedInTerm = false;   // 当前词里出现过引号 → 该词按**字面量**处理（不通配）

            void FlushTerm()
            {
                if (sb.Length == 0) { quotedInTerm = false; return; }
                string text = sb.ToString();
                sb.Clear();
                current.Add(new SearchTerm
                {
                    Text = text,
                    Wildcard = allowWildcard && !quotedInTerm
                               && (text.IndexOf('*') >= 0 || text.IndexOf('?') >= 0),
                    Quoted = quotedInTerm
                });
                quotedInTerm = false;
            }
            void FlushGroup()
            {
                FlushTerm();
                if (current.Count > 0) { groups.Add(current); current = new List<SearchTerm>(); }
            }

            foreach (char c in condition)
            {
                // 引号：开/闭都可（“ ” 都当引号），引号本身不入词；出现即标记该词为**字面量**
                if (c == '"' || c == '\u201C' || c == '\u201D' || c == '\uFF02')
                { inQuote = !inQuote; quotedInTerm = true; continue; }

                if (!inQuote)
                {
                    if (c == '|' || c == '\uFF5C') { FlushGroup(); continue; }   // 或（含全角）
                    if (c == ' ' || c == '\t' || c == '\u3000') { FlushTerm(); continue; }  // 与（含全角空格）
                }

                sb.Append(c);
            }
            FlushGroup();
            return groups;
        }

        /// <summary>
        /// 通配项的**锚定规则**（用户口径：写 `tag*` 就能搜到"含 tag 的名字"，不必再手写两端的 `*`）：
        /// <list type="bullet">
        /// <item><b>以 `*` 开头</b> → 按"**后缀**"匹配（右端不再补 `*`）：`*.dbr` = 以 .dbr 结尾、
        /// `*class.dbr` = 以 class.dbr 结尾 —— 保留"扩展名限定"这类惯用写法的严格语义</item>
        /// <item><b>其余</b> → 两端补 `*`，即"**包含**"（与非通配项的语义一致）：
        /// `tag*` / `tag*class` / `?ag_*` 都能命中路径中间的名字</item>
        /// </list>
        /// <para>⚠ 老实现是"整条路径全匹配标准 glob"：`tag*` 只在**路径以 tag 开头**时才命中，
        /// 而包内路径形如 `records/xxx/tag_class.dbr` → 永远搜不到（用户实测反馈）。
        /// 想要字面量 `*` 请用引号（`"SELECT *"`）。</para>
        /// </summary>
        private static string AnchorPattern(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return pattern;
            return pattern[0] == '*' ? pattern : "*" + pattern + "*";
        }

        /// <summary>
        /// 通配项的**上下文定位锚点**：取词头第一段字面量（到第一个 `*`/`?` 为止）在行中的位置，
        /// 供内容列裁剪窗口对准命中处；词头就是通配符（如 `*class`）时返回 0（无从对准）。
        /// <para>只用于"窗口对准"，不做正确性判定，所以取近似值即可、必须够便宜
        /// （内容搜索对每行都要跑）。</para>
        /// </summary>
        private static int FocusIndex(string line, string pattern, StringComparison cmp)
        {
            if (line == null || string.IsNullOrEmpty(pattern)) return 0;
            int end = 0;
            while (end < pattern.Length && pattern[end] != '*' && pattern[end] != '?') end++;
            if (end == 0) return 0;
            int idx = line.IndexOf(pattern.Substring(0, end), cmp);
            return idx < 0 ? 0 : idx;
        }

        /// <summary>
        /// 名字匹配（文件名包含条件与"文件名/目录排除"共用同一套求值）：
        /// 任一或组满足即命中；通配项走 <see cref="GlobMatch"/>（锚定见 <see cref="AnchorPattern"/>），其余走"包含"。
        /// <para>⚠ 目录排除也复用本方法：把**完整磁盘路径**传进来即可（调用方负责 `\`/`/` 归一与固定不敏感）。</para>
        /// </summary>
        public static bool MatchName(string name, List<List<SearchTerm>> groups, bool caseSensitive)
        {
            if (groups == null || groups.Count == 0) return true;   // 无该条件 = 不筛
            if (string.IsNullOrEmpty(name)) return false;
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            foreach (var group in groups)
            {
                bool all = true;
                foreach (var t in group)
                {
                    bool hit = t.Wildcard
                        ? GlobMatch(name, AnchorPattern(t.Text), caseSensitive)
                        : name.IndexOf(t.Text, cmp) >= 0;
                    if (!hit) { all = false; break; }
                }
                if (all) return true;
            }
            return false;
        }

        /// <summary>
        /// 内容**单行**匹配（与/或/短语/通配）：任一行满足任一或组 = 该行命中。
        /// <para>命中时输出首个命中词在本行中的位置（供上下文裁剪对准关键词）。</para>
        /// <para><paramref name="exact"/> = **完全匹配（全）**：一切按字面字符串相等判定
        /// （通配符不解释、规则见 <see cref="ArchiveSearchOptions.ContentExactMatch"/>）；
        /// 默认 false 走原"包含匹配"分支，行为不变。<paramref name="isDbr"/> 仅在 exact 时消费
        /// （dbr 行剥掉行尾结束逗号后按逗号分段比较）。</para>
        /// </summary>
        public static bool MatchLine(string line, List<List<SearchTerm>> groups, bool caseSensitive, out int firstIndex,
            bool exact = false, bool isDbr = false)
        {
            firstIndex = 0;
            if (groups == null || groups.Count == 0 || string.IsNullOrEmpty(line)) return false;
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            foreach (var group in groups)
            {
                bool all = true;
                int minIdx = int.MaxValue;
                foreach (var t in group)
                {
                    int idx;
                    if (exact)
                    {
                        // 全模式：字面字符串完全相等（* ? 按字面量，不走 GlobMatch）
                        idx = ExactIndexOf(line, t, cmp, isDbr);
                        if (idx < 0) { all = false; break; }
                    }
                    else if (t.Wildcard)
                    {
                        if (!GlobMatch(line, AnchorPattern(t.Text), caseSensitive)) { all = false; break; }
                        idx = FocusIndex(line, t.Text, cmp);
                    }
                    else
                    {
                        // 普通包含匹配：行内含原词（批23 起 v 级反查 tag 集改走 ContentGroupTags OR 判据，不再逐词替代）
                        idx = line.IndexOf(t.Text, cmp);
                        if (idx < 0) { all = false; break; }
                    }
                    if (idx < minIdx) minIdx = idx;
                }
                if (all)
                {
                    firstIndex = minIdx == int.MaxValue ? 0 : minIdx;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// **内容排除**（文件级）：全文**任一行**满足任一或组 = true（调用方据此把整条记录丢弃）。
        /// <para>为什么是文件级而不是行级：内容包含条件是"任一行满足即命中"，若排除也按行，
        /// "该行不含排除词"几乎永远成立 → 等于没排除（方案 §30 与用户确认的口径）。</para>
        /// <para>与包含条件共用同一套求值（与/或/通配/引号字面量）；行拆分口径与 <c>MatchContent</c> 一致
        /// （`\r\n` 归一后按 `\n` 拆）。</para>
        /// </summary>
        public static bool MatchAnyLine(string text, List<List<SearchTerm>> groups, bool caseSensitive)
        {
            if (groups == null || groups.Count == 0 || string.IsNullOrEmpty(text)) return false;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (string line in lines)
                if (MatchLine(line, groups, caseSensitive, out _)) return true;
            return false;
        }

        /// <summary>
        /// tag 的**整词边界**查找：命中处的前后一个字符都不能是字母/数字/下划线（tag 是标识符）。
        /// <para>例：行内 `tagShamanBlack,` 对 tag `tagShaman` **不命中**（后面跟着 'B'），
        /// 而 `xxx,tagShaman,yyy` 命中。</para>
        /// </summary>
        private static int IndexOfWholeTag(string line, string tag, StringComparison cmp)
        {
            int i = line.IndexOf(tag, cmp);
            while (i >= 0)
            {
                int end = i + tag.Length;
                bool leftOk = i == 0 || !IsTagChar(line[i - 1]);
                bool rightOk = end >= line.Length || !IsTagChar(line[end]);
                if (leftOk && rightOk) return i;
                i = line.IndexOf(tag, i + 1, cmp);
            }
            return -1;
        }

        private static bool IsTagChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// 行内含 **tag 集合**中任一 tag（**整词边界**，同 <see cref="IndexOfWholeTag"/>）→ 返回**首个命中位置**（供上下文对焦）；都不含返回 -1。
        /// <para>批23 v 级反查的行命中 OR 判据：条件组整体反查命中的 tag 集（<see cref="ArchiveSearchOptions.ContentGroupTags"/>）
        /// 经本方法参与行匹配 —— 行按原条件满足 **或** 行含集合任一 tag。</para>
        /// </summary>
        public static int IndexOfAnyTag(string line, string[] tags, StringComparison cmp)
        {
            if (string.IsNullOrEmpty(line) || tags == null) return -1;
            int best = -1;
            foreach (string tag in tags)
            {
                if (string.IsNullOrEmpty(tag)) continue;
                int a = IndexOfWholeTag(line, tag, cmp);
                if (a >= 0 && (best < 0 || a < best)) best = a;
            }
            return best;
        }

        /// <summary>
        /// **完全匹配（全）**的词元判定：一切按**字面字符串**相等（通配符 `*` `?` 不解释）。
        /// <para>非 dbr：整行相等（替代 tag 同样整行比）。</para>
        /// <para>dbr 行：先剥掉**行尾结束逗号**（与残留 `\r`，AGENTS §8.2 —— 原数据每行结尾通常有一个逗号，
        /// 不剥掉则 `爆破者` 对 `爆破者,` 永不相等 → 「全」搜不到）：</para>
        /// <list type="bullet">
        /// <item>词元带逗号 → 与剥尾后的整行相等（词元行尾逗号同样剥掉，`k,v,` == `k,v`）</item>
        /// <item>词元不带逗号 → 按逗号**分段**，任一段（字段名或任一字段值；dbr 值可多值）相等即命中</item>
        /// </list>
        /// <para>返回命中处的**行内位置**（供上下文裁剪窗口对准）；未命中返回 -1。</para>
        /// </summary>
        private static int ExactIndexOf(string line, SearchTerm t, StringComparison cmp, bool isDbr)
        {
            if (string.IsNullOrEmpty(t.Text)) return -1;

            if (!isDbr)
            {
                if (string.Equals(line, t.Text, cmp)) return 0;
                return -1;
            }

            // dbr 行：剥掉行尾结束逗号与残留 \r（只动行尾，不影响命中位置的行内坐标）
            ReadOnlySpan<char> s = TrimDbrLineEnd(line.AsSpan());

            if (t.Text.IndexOf(',') >= 0)
            {
                // 词元本身带逗号（用户直接输入 `k,v` 整行）→ 与整行完全相等（词元行尾逗号同样剥掉）
                if (s.Equals(TrimDbrLineEnd(t.Text.AsSpan()), cmp)) return 0;
            }
            else
            {
                int idx = SegmentIndexOf(s, t.Text, cmp);
                if (idx >= 0) return idx;
            }
            return -1;
        }

        /// <summary>剥掉 dbr 的**行尾结束逗号**与残留 `\r`（`k,v,` → `k,v`；只处理行尾，不动行内分隔逗号）。</summary>
        private static ReadOnlySpan<char> TrimDbrLineEnd(ReadOnlySpan<char> s)
        {
            while (!s.IsEmpty && (s[s.Length - 1] == ',' || s[s.Length - 1] == '\r'))
                s = s.Slice(0, s.Length - 1);
            return s;
        }

        /// <summary>
        /// dbr 行的**分段相等**查找：按逗号切段，任一段与 <paramref name="term"/> 完全相等即返回段首位置。
        /// <para>字段名与所有字段值都参与比较（值可多值，如 `1,2,3` = 三段）。</para>
        /// </summary>
        private static int SegmentIndexOf(ReadOnlySpan<char> line, ReadOnlySpan<char> term, StringComparison cmp)
        {
            if (term.IsEmpty) return -1;
            int pos = 0;
            while (true)
            {
                ReadOnlySpan<char> slice = line.Slice(pos);
                int c = slice.IndexOf(',');
                ReadOnlySpan<char> seg = c < 0 ? slice : slice.Slice(0, c);
                if (seg.Equals(term, cmp)) return pos;
                if (c < 0) return -1;
                pos += c + 1;
            }
        }

        /// <summary>
        /// 从文件名条件里提取"被限定的扩展名"（形如 `*.dbr` / `.dbr` 的正向词），
        /// 供 UI 做"内容搜索必然为空"的**搜索前警告**（如只写了 `*.mp4` 又填了内容条件）。
        /// </summary>
        public static List<string> ExtractExtensions(List<List<SearchTerm>> nameGroups)
        {
            var list = new List<string>();
            if (nameGroups == null) return list;
            foreach (var group in nameGroups)
            {
                if (group.Count != 1) continue;   // 只在"单一正向词"时判定，避免多词组合误报
                string t = group[0].Text;
                if (t.StartsWith("*.") && IsExtLike(t.Substring(1))) list.Add(t.Substring(1));
                else if (t.StartsWith(".") && IsExtLike(t)) list.Add(t);
            }
            return list;
        }
        private static bool IsExtLike(string ext)
        {
            if (ext == null || ext.Length < 2 || ext[0] != '.') return false;
            for (int i = 1; i < ext.Length; i++)
            {
                char c = ext[i];
                if (!char.IsLetterOrDigit(c)) return false;
            }
            return true;
        }

        /// <summary>
        /// 通配匹配：`*` 任意多字符、`?` 单字符（经典 `*` 回溯，最坏 O(n·m)；**不用正则**）。
        /// </summary>
        private static bool GlobMatch(string text, string pattern, bool caseSensitive)
        {
            int ti = 0, pi = 0, star = -1, mark = 0;
            while (ti < text.Length)
            {
                if (pi < pattern.Length && (pattern[pi] == '?' || Eq(pattern[pi], text[ti], caseSensitive)))
                { pi++; ti++; }
                else if (pi < pattern.Length && pattern[pi] == '*')
                { star = pi++; mark = ti; }
                else if (star >= 0)
                { pi = star + 1; ti = ++mark; }
                else return false;
            }
            while (pi < pattern.Length && pattern[pi] == '*') pi++;
            return pi == pattern.Length;
        }

        private static bool Eq(char a, char b, bool caseSensitive)
            => caseSensitive ? a == b : char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }

    /// <summary>
    /// 一条搜索命中。
    /// </summary>
    public sealed class ArchiveSearchHit
    {
        // ── 定位信息 ──
        public string ArchivePath;      // 包文件全路径（表格「包名」列直接显示它）
        public ArchiveKind Kind;
        public string RelPath;          // 包内相对路径（ARZ 记录名 / ARC 条目名，形如 aaa/bbb/ccc.dbr）
        public int RecordIndex;         // 记录 / 条目索引（供编辑器定位）

        // ── 命中信息 ──
        public int MatchCount;          // 命中次数（文件名模式 = 1）
        public string Context;          // 内容列文本：前 N 处命中行（由 MaxContentHits 控制），以 " / " 分隔

        // ── 扩展列：元数据级（零额外读取，随命中直接回填）──
        public DateTime? FileTime;      // 包内文件时间戳
        public long Size;               // 解压后大小

        // ── 扩展列：解包级（WithDbrMeta 且为 .dbr 时才有）──
        public string Description;      // 说明
        public string LocalizedName;    // 本地化名称（无语言包时回落原始 tag）
        public string RawTag;           // 原始 tag（切语言时据此重算，无需重新解包）
        public string Template;         // 模板名
    }

    /// <summary>
    /// **跨包搜索的公共引擎**（不打开包、只读、绝不改动原始包）。
    /// <para>职责边界：本类只负责**一个包**的搜索；跨包调度（并行、汇总、取消、进度）由调用方负责。</para>
    /// <para>编码口径按链路走，与编辑器逐字一致（wiki 开发侧 2.4）：</para>
    /// <para>· ARZ 文件名 → 记录名（字符串表按项解码）；ARZ 内容 → `SaveToStream(ms)` 的 UTF-8 内存序列化字节；</para>
    /// <para>· ARC 文件名 → 条目名；ARC 内容 → 包内原始字节，按 TextFileHelper.DecodeText 检测。</para>
    /// <para>句柄约定：一律 `FileShare.Read | FileShare.Delete`，且用完即释放（wiki 2.5 硬约束）。</para>
    /// </summary>
    public static class ArchiveSearcher
    {
        /// <summary>按扩展名判断包类型（.arz / .arc，忽略大小写）</summary>
        public static bool TryGetKind(string archivePath, out ArchiveKind kind)
        {
            kind = ArchiveKind.Arz;
            if (string.IsNullOrEmpty(archivePath)) return false;
            string ext = Path.GetExtension(archivePath);
            if (string.Equals(ext, ".arz", StringComparison.OrdinalIgnoreCase)) { kind = ArchiveKind.Arz; return true; }
            if (string.Equals(ext, ".arc", StringComparison.OrdinalIgnoreCase)) { kind = ArchiveKind.Arc; return true; }
            return false;
        }

        /// <summary>
        /// 搜索单个包。命中通过 <paramref name="onMatch"/> 实时回调（**在调用线程上同步回调**，调用方自行切 UI 线程）。
        /// </summary>
        /// <param name="progress">进度：(已扫描条目数, 总条目数, 当前条目名)，约每 1% 上报一次</param>
        public static List<ArchiveSearchHit> SearchArchive(
            string archivePath, ArchiveSearchOptions options, CancellationToken ct,
            IProgress<(int current, int total, string path)> progress = null,
            Action<ArchiveSearchHit> onMatch = null,
            Action<string> onWarning = null)
        {
            if (options == null) return new List<ArchiveSearchHit>();
            if (!TryGetKind(archivePath, out ArchiveKind kind)) return new List<ArchiveSearchHit>();

            // 条件解析一次（每条目不再重复分词）；两个条件都空 = 无有效条件 → 直接返回空
            var nameGroups = SearchCondition.Parse(options.NameCondition, allowWildcard: true);
            // 内容框同样支持通配符（引号内为字面量）——与文件名框一套语法，避免"同一套写法两处行为不同"
            var contentGroups = SearchCondition.Parse(options.ContentCondition, allowWildcard: true);
            if (nameGroups.Count == 0 && contentGroups.Count == 0) return new List<ArchiveSearchHit>();

            // 排除条件（文件级，方案 §30）：任一排除条件命中 → 该条目整条丢弃。
            // 语法与包含条件完全一致（`|` = "A 和 B 都不要"）；**没有** `-`/`!`（逻辑非整体收敛到排除框）
            var nameExGroups = SearchCondition.Parse(options.NameExcludeCondition, allowWildcard: true);
            var contentExGroups = SearchCondition.Parse(options.ContentExcludeCondition, allowWildcard: true);

            // 批23：本地化搜索改为 **v 级反查**（条件组整体命中，GuiMainForm.BuildLangReverseTagList 构建），
            // tag 集经 options.ContentGroupTags 在 MatchContent 里以 OR 判据消费；不再按词回填 Alternatives。

            // FileShare.Delete 是硬约束：搜索期间别人要替换/删除这个包时不被我们挡住（wiki 2.5）
            using (var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                return kind == ArchiveKind.Arz
                    ? SearchArz(fs, archivePath, options, nameGroups, contentGroups, nameExGroups, contentExGroups, ct, progress, onMatch, onWarning)
                    : SearchArc(fs, archivePath, options, nameGroups, contentGroups, nameExGroups, contentExGroups, ct, progress, onMatch, onWarning);
            }
        }

        // ─────────────────────────── ARZ ───────────────────────────

        private static List<ArchiveSearchHit> SearchArz(Stream fs, string archivePath, ArchiveSearchOptions options,
            List<List<SearchTerm>> nameGroups, List<List<SearchTerm>> contentGroups,
            List<List<SearchTerm>> nameExGroups, List<List<SearchTerm>> contentExGroups, CancellationToken ct,
            IProgress<(int current, int total, string path)> progress, Action<ArchiveSearchHit> onMatch,
            Action<string> onWarning)
        {
            bool hasName = nameGroups.Count > 0, hasContent = contentGroups.Count > 0;
            bool hasNameEx = nameExGroups.Count > 0, hasContentEx = contentExGroups.Count > 0;
            var results = new List<ArchiveSearchHit>();
            var reader = new ARZReader(fs);
            reader.EnsureRecordTableIsLoaded();
            int total = reader.Count;
            int reportInterval = Math.Max(1, total / 100);
            var ms = new MemoryStream();   // 每包复用（SetLength(0)），避免每文件 new

            try
            {
                for (int i = 0; i < total; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = reader.GetRecordName(i);
                    if (i % reportInterval == 0) progress?.Report((i, total, name));
                    if (string.IsNullOrEmpty(name)) continue;   // 墓碑 / 无效记录

                    // ── 漏斗第 1 层：文件名条件（**不读数据**，快路径不退化）──
                    if (hasName && !SearchCondition.MatchName(name, nameGroups, caseSensitive: false)) continue;   // 批21：Aa 只管内容，文件名固定不敏感
                    // ── 文件名排除（同样不读数据）：命中排除条件 → 整条丢弃 ──
                    if (hasNameEx && SearchCondition.MatchName(name, nameExGroups, caseSensitive: false)) continue;   // 批21：名称排除同样固定不敏感

                    var info = reader.GetRecordInfo(i);
                    ArchiveSearchHit hit;

                    if (hasContent)
                    {
                        // ── 漏斗第 2 层：内容（仅对通过第 1 层的条目读全文）──
                        if (!TextFileHelper.IsTextFileExtension(Path.GetExtension(name))) continue;
                        string text = ReadArzRecordText(reader, i, fs, ms, options, size => onWarning?.Invoke(TooLargeText(name, size, options)));
                        if (text == null) continue;
                        // ── 内容排除（**文件级**）：任一行命中 → 整条丢弃（不出结果、不计命中数）──
                        if (hasContentEx && SearchCondition.MatchAnyLine(text, contentExGroups, options.CaseSensitive)) continue;

                        MatchContent(text, contentGroups, options, IsDbr(name), out int matchCount, out string context);
                        if (matchCount == 0) continue;

                        hit = MakeHit(archivePath, ArchiveKind.Arz, name, i, info, matchCount, context);
                        if (options.WithDbrMeta && IsDbr(name)) FillDbrMeta(hit, text, name, options);
                    }
                    else
                    {
                        hit = MakeHit(archivePath, ArchiveKind.Arz, name, i, info, 1, null);
                        if (options.WithDbrMeta && IsDbr(name))
                        {
                            string text = ReadArzRecordText(reader, i, fs, ms, options, size => onWarning?.Invoke(TooLargeText(name, size, options)));
                            if (text != null) FillDbrMeta(hit, text, name, options);
                        }
                    }

                    results.Add(hit);
                    onMatch?.Invoke(hit);
                }

                progress?.Report((total, total, null));
                return results;
            }
            finally
            {
                ms.Dispose();
            }
        }

        /// <summary>读出一条 ARZ 记录的 DBR 文本（内存序列化 → UTF-8，与编辑器同口径）；超上限/异常返回 null</summary>
        /// <param name="onTooLarge">文件超过扫描上限时回调（参数 = 实际字节数），由调用方决定怎么提示</param>
        private static string ReadArzRecordText(ARZReader reader, int index, Stream fs, MemoryStream ms,
            ArchiveSearchOptions options, Action<long> onTooLarge = null)
        {
            try
            {
                var record = reader.GetRecord(index, fs);   // 双参：无缓存、线程安全重载
                if (record == null) return null;
                try
                {
                    ms.SetLength(0);
                    ms.Position = 0;
                    record.SaveToStream(ms);
                    if (ms.Length > options.MaxScanBytes)
                    {
                        onTooLarge?.Invoke(ms.Length);
                        return null;   // 超上限：跳过，不为它拖垮整体
                    }
                    ms.Position = 0;
                    // SaveToStream 的产物是 "string → UTF-8 无 BOM" 的内存序列化字节，按 UTF-8 读回即自洽
                    // ⚠ leaveOpen:true —— ms 是**每包复用**的；StreamReader 默认会连带 Dispose 底层流，
                    //   第一条之后 ms 就被关闭，后续全部记录读不出文本（实测导致"ARZ 内容搜索恒为空"）
                    using (var sr = new StreamReader(ms, Encoding.UTF8, false, 1024, leaveOpen: true))
                        return sr.ReadToEnd();
                }
                finally
                {
                    record.DiscardData();   // 逐条释放（wiki 3.1）
                }
            }
            catch
            {
                return null;
            }
        }

        // ─────────────────────────── ARC ───────────────────────────

        private static List<ArchiveSearchHit> SearchArc(Stream fs, string archivePath, ArchiveSearchOptions options,
            List<List<SearchTerm>> nameGroups, List<List<SearchTerm>> contentGroups,
            List<List<SearchTerm>> nameExGroups, List<List<SearchTerm>> contentExGroups, CancellationToken ct,
            IProgress<(int current, int total, string path)> progress, Action<ArchiveSearchHit> onMatch,
            Action<string> onWarning)
        {
            bool hasName = nameGroups.Count > 0, hasContent = contentGroups.Count > 0;
            bool hasNameEx = nameExGroups.Count > 0, hasContentEx = contentExGroups.Count > 0;
            var results = new List<ArchiveSearchHit>();
            // NameReporter = null → 静默：搜索会反复读很多包，名字编码结论刷屏没有意义
            var arc = new ARCFile { NameReporter = null };
            arc.ReadStream(fs);
            int total = arc.toc.Count;
            int reportInterval = Math.Max(1, total / 100);
            var ms = new MemoryStream();   // 每包复用

            try
            {
                for (int i = 0; i < total; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = arc.toc[i];
                    string name = entry.GetEntryString(arc.strs);
                    if (i % reportInterval == 0) progress?.Report((i, total, name));
                    if (string.IsNullOrEmpty(name)) continue;   // 墓碑 / 无效条目

                    // ── 漏斗第 1 层：文件名条件（**不读数据**）──
                    if (hasName && !SearchCondition.MatchName(name, nameGroups, caseSensitive: false)) continue;   // 批21：Aa 只管内容，文件名固定不敏感
                    // ── 文件名排除（同样不读数据）：命中排除条件 → 整条丢弃 ──
                    if (hasNameEx && SearchCondition.MatchName(name, nameExGroups, caseSensitive: false)) continue;   // 批21：名称排除同样固定不敏感

                    ArchiveSearchHit hit;

                    if (hasContent)
                    {
                        // ── 漏斗第 2 层：内容 ──
                        if (!TextFileHelper.IsTextFileExtension(Path.GetExtension(name))) continue;
                        string text = ReadArcEntryText(arc, i, fs, ms, options, size => onWarning?.Invoke(TooLargeText(name, size, options)));
                        if (text == null) continue;
                        // ── 内容排除（**文件级**）：任一行命中 → 整条丢弃（不出结果、不计命中数）──
                        if (hasContentEx && SearchCondition.MatchAnyLine(text, contentExGroups, options.CaseSensitive)) continue;

                        MatchContent(text, contentGroups, options, IsDbr(name), out int matchCount, out string context);
                        if (matchCount == 0) continue;

                        hit = MakeHit(archivePath, ArchiveKind.Arc, name, i, null, matchCount, context);
                        hit.FileTime = entry.FileTime;
                        hit.Size = entry.DecompressedSize;
                        if (options.WithDbrMeta && IsDbr(name)) FillDbrMeta(hit, text, name, options);
                    }
                    else
                    {
                        hit = MakeHit(archivePath, ArchiveKind.Arc, name, i, null, 1, null);
                        hit.FileTime = entry.FileTime;
                        hit.Size = entry.DecompressedSize;
                        if (options.WithDbrMeta && IsDbr(name))
                        {
                            string text = ReadArcEntryText(arc, i, fs, ms, options, size => onWarning?.Invoke(TooLargeText(name, size, options)));
                            if (text != null) FillDbrMeta(hit, text, name, options);
                        }
                    }

                    results.Add(hit);
                    onMatch?.Invoke(hit);
                }

                progress?.Report((total, total, null));
                return results;
            }
            finally
            {
                ms.Dispose();
            }
        }

        /// <summary>解压一条 ARC 条目并解码为文本（包内原始字节 → 检测口径解码，与编辑器同口径）；超上限/异常返回 null</summary>
        private static string ReadArcEntryText(ARCFile arc, int index, Stream fs, MemoryStream ms,
            ArchiveSearchOptions options, Action<long> onTooLarge = null)
        {
            try
            {
                var entry = arc.toc[index];
                ms.SetLength(0);
                ms.Position = 0;
                arc.UnpackToStream(entry, fs, ms);   // 三参：外部流、线程安全重载
                if (ms.Length > options.MaxScanBytes)
                {
                    onTooLarge?.Invoke(ms.Length);
                    return null;
                }
                return TextFileHelper.DecodeText(ms.ToArray());
            }
            catch
            {
                return null;
            }
        }

        // ─────────────────────────── 共用 ───────────────────────────

        /// <summary>超大文件跳过的告警文案（含实际大小与上限，便于用户理解为何搜不到）</summary>
        private static string TooLargeText(string relPath, long size, ArchiveSearchOptions options)
        {
            return string.Format(
                LanguageManager.Instance.GetText("SearchTab.WarnTooLarge",
                    "内容搜索跳过超大文件：{0}（{1:0.#}MB，超过上限 {2:0.#}MB）"),
                relPath, size / 1024.0 / 1024.0, options.MaxScanBytes / 1024.0 / 1024.0);
        }

        private static bool IsDbr(string relPath)
            => string.Equals(Path.GetExtension(relPath), ".dbr", StringComparison.OrdinalIgnoreCase);

        private static ArchiveSearchHit MakeHit(string archivePath, ArchiveKind kind, string relPath, int index,
            ARZReader.ARZRecordInfo info, int matchCount, string context)
        {
            return new ArchiveSearchHit
            {
                ArchivePath = archivePath,
                Kind = kind,
                RelPath = relPath,
                RecordIndex = index,
                MatchCount = matchCount,
                Context = context,
                FileTime = info?.rdFileTime,
                Size = info?.rdSizeDecompressed ?? 0
            };
        }

        /// <summary>
        /// 在整份文本里**逐行**匹配内容条件：
        /// <list type="number">
        /// <item>**统计全部满足条件的行数** → 结果表「命中数」列（不受取样上限影响）</item>
        /// <item>内容列**取前 <see cref="ArchiveSearchOptions.MaxContentHits"/> 处命中行本身**，
        /// 以 `" / "` 分隔（多次命中时能看到多个结果；仍**不是全量**，全量在编辑器里看）</item>
        /// </list>
        /// <para>⚙ 口径：一次匹配以"行"为单位 —— 空格（与）要求**同一行**同时含各词，
        /// 或组（`|`）则是"任一组在**同一行**内全部满足"。</para>
        /// </summary>
        private static void MatchContent(string text, List<List<SearchTerm>> contentGroups,
            ArchiveSearchOptions options, bool isDbr, out int matchCount, out string context)
        {
            matchCount = 0;
            context = null;
            if (string.IsNullOrEmpty(text) || contentGroups == null || contentGroups.Count == 0) return;

            // SaveToStream 产物为 "\n" 分隔；ARC 原文可能 "\r\n" —— 统一替换后按 '\n' 拆
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int limit = options.MaxContentHits > 0 ? options.MaxContentHits : 5;

            // 一行扫完：命中数**全统计**，但只**取样**前 limit 处的行与其命中位置
            var sampleIdx = new List<int>(limit);
            var sampleCol = new List<int>(limit);
            var cmp = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            string[] groupTags = options.ContentGroupTags;
            for (int i = 0; i < lines.Length; i++)
            {
                // exact = 完全匹配（全）：默认 false 走原包含分支，行为不变
                if (!SearchCondition.MatchLine(lines[i], contentGroups, options.CaseSensitive, out int col,
                    options.ContentExactMatch, isDbr))
                {
                    // 批23 v 级反查 OR 判据：行含条件组整体反查命中的 tag 集中任一（整词边界）也算该行命中
                    int tcol = SearchCondition.IndexOfAnyTag(lines[i], groupTags, cmp);
                    if (tcol < 0) continue;
                    col = tcol;
                }
                matchCount++;
                if (sampleIdx.Count < limit)
                {
                    sampleIdx.Add(i);
                    sampleCol.Add(col);
                }
            }
            if (sampleIdx.Count == 0) return;

            // 把总预算均分到取样行，保证"整段 ≤ MaxContextChars"（分隔符另计）
            int count = sampleIdx.Count;
            int budget = options.MaxContextChars;
            int perLine = budget > 0
                ? Math.Max(40, (budget - (count - 1) * Sep.Length) / count)
                : 0;

            var sb = new StringBuilder();
            for (int k = 0; k < count; k++)
            {
                if (sb.Length > 0) sb.Append(Sep);
                // 每行都把窗口对准自己那处命中词
                sb.Append(ClipLine(lines[sampleIdx[k]], perLine, sampleCol[k]));
            }
            context = sb.ToString();
        }

        /// <summary>
        /// **多处命中行之间的分隔符 = 换行**。
        /// <para>为什么用换行而不是" / "：AntdUI 表格有**自带单元格 tip**（文本溢出时弹出），
        /// tip 文本就是单元格的原始值 → 值里带 `\n` 时 tip 即为**换行显示**；右键"复制"也原样带换行。
        /// 单元格本身因 `NoWrap` + 26px 行高裁剪，仍只显示第一段（行高不变）。</para>
        /// </summary>
        private const string Sep = "\n";

        /// <summary>
        /// 单行裁剪：把一行文本裁成**安全的一行**（GDI+ 长文本陷阱的对策）。
        /// <para>`focus` = 关键词在本行中的位置，截断时把窗口对准它，保证命中处可见。</para>
        /// <para>`maxLen` = 本行上限（0 = 不限，但仍会去掉行内残留的 \r 与制表符）。</para>
        /// </summary>
        private static string ClipLine(string line, int maxLen, int focus)
        {
            if (line == null) return "";
            // 行内残留的 \r / 制表符一并归一：表格单元格必须是"没有换行的一行"才安全
            line = line.Replace('\r', ' ').Replace('\t', ' ');

            if (maxLen <= 0 || line.Length <= maxLen) return line;

            int start = 0;
            if (focus > maxLen / 2)
                start = Math.Min(focus - maxLen / 2, line.Length - maxLen);
            if (start < 0) start = 0;

            int len = Math.Min(maxLen, line.Length - start);
            string body = line.Substring(start, len);
            return (start > 0 ? "…" : "") + body + (start + len < line.Length ? "…" : "");
        }

        /// <summary>
        /// 填充 `.dbr` 的三项扩展列：**一次解析同时产出** 说明 / 首tag(→本地化名称) / 模板。
        /// 口径与编辑器 `GetFileDisplayTexts` 一致（模板取首行、tag 走 LookupLocalizedTag，无语言包回退原始 tag）。
        /// </summary>
        private static void FillDbrMeta(ArchiveSearchHit hit, string text, string relPath, ArchiveSearchOptions options)
        {
            try
            {
                // 模板名取自第一行（与编辑器同口径，避免对大文本整体 Split 两次）
                int nl = text.IndexOf('\n');
                hit.Template = TemplateHelper.GetDbrTemplateName(new[] { nl >= 0 ? text.Substring(0, nl) : text });

                var parsed = DbrHelper.ParseDbrFields(text, recordName: relPath);
                hit.Description = parsed.Description;
                hit.RawTag = parsed.FirstTagValue;
                hit.LocalizedName = LocalizationHelper.LookupLocalizedTag(parsed.FirstTagValue, options.LanguageTags);
            }
            catch
            {
                // 单条解析失败不影响搜索整体，三项留空
            }
        }
    }
}
