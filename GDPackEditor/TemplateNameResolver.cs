using System;
using System.Collections.Generic;

namespace GDPackEditor
{
    /// <summary>
    /// 模板名解析（**全项目唯一口径**）。
    /// <para>背景：DBR 首行的 templateName 有三种写法 ——</para>
    /// <list type="bullet">
    /// <item>官方标准：<c>database/templates/x.tpl</c>（主流，实测 47331/47332）</item>
    /// <item>非标准：<c>mods/&lt;mod名&gt;/database/templates/x.tpl</c>（AM 也能识别，一般可用，但要提示用户）</item>
    /// <item>裸文件名 / 绝对路径等其它写法</item>
    /// </list>
    /// <para>原先"原样 → 补 database/templates/ 前缀"这套查找散落在 6 处，且**都没有处理非标准前缀**，
    /// 于是同一份数据会得出互相矛盾的结论：内容检测说"模板不存在"、合并对比说"找到了"、打包直接把文件丢掉。</para>
    /// <para>本类把它收成一处：**生成候选 → 逐个查字典 → 命中即用 → 全不中才算不存在**。
    /// 候选按"从标准到宽松"排列；命中位置不属前两个候选即判为非标准路径（供上层提示）。</para>
    /// </summary>
    public static class TemplateNameResolver
    {
        private const string Prefix = "database/templates/";
        private const string Seg = "templates/";

        /// <summary>解析结果</summary>
        public struct Result
        {
            /// <summary>命中的模板节点；null = 模板不存在</summary>
            public TemplateNode Template;
            /// <summary>命中的字典键（规范形式，如 database/templates/x.tpl）；未命中为 null</summary>
            public string StandardKey;
            /// <summary>是否非标准写法（前两个标准候选没中、靠后的宽松候选中了）</summary>
            public bool NonStandard;
        }

        /// <summary>归一化：小写 + 反斜杠转正斜杠 + 去掉开头多余的斜杠</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string n = path.Replace('\\', '/').ToLowerInvariant();
            if (n.StartsWith("/")) n = n.TrimStart('/');
            return n;
        }

        /// <summary>
        /// 规范键（**用于产出模板字典的键**）：取**最后一个** `templates/` 段之后的相对路径，
        /// 统一拼成 <c>database/templates/&lt;rest&gt;</c>。
        /// <para>⚠ **没有 `templates/` 段时必须保留整条相对路径**（只补前缀）—— 内置 templates*.arc 里就有
        /// <c>backup/…</c>、<c>engine/…</c>、<c>ingameui/…</c> 这类**子目录条目**，压成裸文件名会互相撞名、
        /// 把原有键整批弄丢（实测：压成裸名会丢 289 个键、1131 条记录找不到模板）。</para>
        /// <para>对标准条目（`templates/x.tpl`、`x.tpl`、`database/templates/x.tpl`、`engine/x.tpl`）结果与旧的
        /// "补前缀"完全一致；只对"段之前自带多余前缀"的条目（如 `mods/&lt;mod&gt;/database/templates/x.tpl`）做纠正。</para>
        /// </summary>
        public static string CanonicalKey(string path)
        {
            string n = Normalize(path);
            if (n.Length == 0) return n;
            if (n.StartsWith(Prefix, StringComparison.Ordinal)) return n;

            int at = LastSegmentIndex(n);
            if (at >= 0) return Prefix + n.Substring(at + Seg.Length);   // 剥掉 templates/ 之前的多余前缀
            return Prefix + n;                                            // 无 templates/ 段 → 整条保留（与旧逻辑一致）
        }

        /// <summary>
        /// 生成候选键（按"从标准到宽松"顺序）。每个元素 = (候选键, 是否标准写法)。
        /// <para>① 原样 ② 补前缀 ③ 从最后一个 `templates/` 段截取 ④ 从第一个（含中间）截取。</para>
        /// </summary>
        public static List<KeyValuePair<string, bool>> BuildCandidates(string rawName)
        {
            var list = new List<KeyValuePair<string, bool>>();
            string n = Normalize(rawName);
            if (n.Length == 0) return list;

            // ① 原样 ② 补前缀（这两个算"标准写法"，命中它们不提示）
            AddCandidate(list, n, true);
            if (!n.StartsWith(Prefix, StringComparison.Ordinal))
                AddCandidate(list, Prefix + n, true);

            // ③④ 按 `templates/` 段截取：先最后一个（更贴近文件），再其余（从后往前）
            var idx = SegmentIndexes(n);
            for (int i = idx.Count - 1; i >= 0; i--)
                AddCandidate(list, Prefix + n.Substring(idx[i] + Seg.Length), false);

            return list;
        }

        /// <summary>解析模板名。命中即返回；全不中时 <see cref="Result.Template"/> 为 null。</summary>
        public static Result Resolve(IDictionary<string, TemplateNode> templates, string rawName)
        {
            var res = new Result();
            if (templates == null || templates.Count == 0 || string.IsNullOrWhiteSpace(rawName)) return res;

            foreach (var c in BuildCandidates(rawName))
            {
                if (templates.TryGetValue(c.Key, out var t) && t != null)
                {
                    res.Template = t;
                    res.StandardKey = c.Key;
                    res.NonStandard = !c.Value;
                    return res;
                }
            }

            // 末级文件名兜底：**仅当路径里没有 `templates/` 段、且该文件名在字典中唯一时**才用。
            // 唯一性保护是必须的 —— 否则 a/x.tpl 与 b/x.tpl 会静默串成同一个模板（比找不到更危险）。
            string n = Normalize(rawName);
            if (SegmentIndexes(n).Count == 0)
            {
                string baseName = BaseName(n);
                string foundKey = null;
                bool unique = true;
                foreach (var kv in templates)
                {
                    if (kv.Value == null) continue;
                    if (!string.Equals(BaseName(Normalize(kv.Key)), baseName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (foundKey == null) { foundKey = kv.Key; }
                    else { unique = false; break; }
                }
                if (unique && foundKey != null && templates.TryGetValue(foundKey, out var t2) && t2 != null)
                {
                    res.Template = t2;
                    res.StandardKey = foundKey;
                    res.NonStandard = true;
                }
            }
            return res;
        }

        /// <summary>
        /// 模板名 → 来源标签（**公共查询入口**，查询与展示分离）。
        /// <para>数据源：<see cref="Program.LastTemplateKeySource"/>（最近一次模板加载产出的
        /// 「规范键 → 来源标签」表，键为 <see cref="CanonicalKey"/> 形式、天然小写）。</para>
        /// <para>本方法按与 <see cref="Resolve"/> 相同的候选口径归一化后查询——DBR 里 templateName
        /// 无论标准/非标准写法、大小写差异，均可命中；未命中（模板未加载/来源未启用）返回 null，
        /// 由调用方自行决定展示方式（回退纯文本、隐藏 tag 等）。</para>
        /// <para>典型复用：模板列/模板筛选/模板相关提示等任何需要"该模板来自哪个来源"的展示处。</para>
        /// </summary>
        /// <param name="rawName">templateName 原始值（如 database/templates/x.tpl）</param>
        /// <returns>来源标签（如"内置模板 1.3"、自定义来源备注）；无来源为 null</returns>
        public static string ResolveSourceLabel(string rawName)
        {
            var map = Program.LastTemplateKeySource;
            if (map == null || map.Count == 0 || string.IsNullOrWhiteSpace(rawName)) return null;
            foreach (var c in BuildCandidates(rawName))
                if (map.TryGetValue(c.Key, out var label) && !string.IsNullOrEmpty(label))
                    return label;
            return null;
        }

        // ─────────────────────────── 内部工具 ───────────────────────────

        private static void AddCandidate(List<KeyValuePair<string, bool>> list, string key, bool standard)
        {
            if (string.IsNullOrEmpty(key)) return;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Key, key, StringComparison.Ordinal)) return;   // 去重，保持原顺序
            list.Add(new KeyValuePair<string, bool>(key, standard));
        }

        /// <summary>`templates/` 作为"路径段"出现的所有下标（串首，或前一个字符是 '/'）</summary>
        private static List<int> SegmentIndexes(string normalized)
        {
            var idx = new List<int>();
            if (string.IsNullOrEmpty(normalized)) return idx;
            int at = normalized.IndexOf(Seg, StringComparison.Ordinal);
            while (at >= 0)
            {
                if (at == 0 || normalized[at - 1] == '/') idx.Add(at);
                at = normalized.IndexOf(Seg, at + 1, StringComparison.Ordinal);
            }
            return idx;
        }

        private static int LastSegmentIndex(string normalized)
        {
            var idx = SegmentIndexes(normalized);
            return idx.Count == 0 ? -1 : idx[idx.Count - 1];
        }

        private static string BaseName(string normalized)
        {
            int slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        }
    }
}
