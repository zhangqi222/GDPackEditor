using System;
using System.Collections.Generic;
using System.Text;

namespace GDPackEditor
{
    /// <summary>门禁级别：Error=阻断写入，Warn=可继续但必须提示，Info=仅说明</summary>
    internal enum DbrValueSeverity { Info, Warn, Error }

    /// <summary>一条门禁结论</summary>
    internal sealed class DbrValueIssue
    {
        internal DbrValueSeverity Severity { get; }
        /// <summary>多语言 key（便于定位与测试断言）</summary>
        internal string Code { get; }
        /// <summary>已本地化的说明文案</summary>
        internal string Text { get; }

        internal DbrValueIssue(DbrValueSeverity severity, string code, string text)
        {
            Severity = severity;
            Code = code;
            Text = text;
        }
    }

    /// <summary>
    /// 被校验对象的"处境"：同一个值在不同字段/不同文件上，结论可能不同
    /// （例如空值在"原文有这一行"时是清空、在"原文无这一行"时等于什么都不做）。
    /// </summary>
    internal sealed class DbrValueContext
    {
        internal string FieldName;
        /// <summary>该字段是否为数组（模板 Class == "array"）</summary>
        internal bool IsArray;
        /// <summary>该字段是否为预设下拉/bool</summary>
        internal bool IsPicklist;
        /// <summary>该文件原文里是否存在这个字段行（false = 写入会新增一行，属结构性变更）</summary>
        internal bool KeyExistsInOrigLines = true;
        /// <summary>该字段在原文出现的次数（&gt;1 表示只会替换最后一条）</summary>
        internal int KeyOccurrences = 1;
        /// <summary>本文件该字段当前数组段数（-1 = 非数组或未知）</summary>
        internal int TargetArrayLength = -1;
        /// <summary>基准文件该字段的数组段数（-1 = 非数组或未知）</summary>
        internal int BaseArrayLength = -1;
    }

    /// <summary>
    /// 写入前的**值门禁**。
    ///
    /// 为什么必须有这一层：DBR 的行格式是 `字段名,值,`（下面**没有引号、没有转义**），
    /// 而读取时只取第 2 段（`TemplateHelper` 里 `parts[1]`）。于是：
    /// ① 值里出现**半角逗号** → 从第 2 个逗号起被静默丢弃（写进去、读回来是两个东西）；
    /// ② 值里出现**换行** → 写进文件就多出一条字段行，破坏文件结构。
    ///
    /// 处理规则：
    /// ① **半角逗号 → 一律阻断**（不自动修正）。因为"替换成什么"是语义选择（全角/下划线/删除），
    ///    程序猜不准；而且变更日志记的是"用户填的值"，程序偷改会让日志与落盘不一致。
    /// ② **换行/控制字符 → 自动清理并逐行记录**（<see cref="AutoClean"/>）。这类字符在 DBR 里
    ///    没有任何合法语义，移除属"结构修复"而非"改语义"；自动处理 + 明确记录，日志与落盘仍一致。
    /// </summary>
    internal static class DbrValueGuard
    {
        /// <summary>单值长度上限：超过即阻断（几乎一定是误粘贴）</summary>
        internal const int MaxValueLength = 4096;
        /// <summary>单值长度提示阈值</summary>
        internal const int LongValueLength = 256;

        private static string T(string key, string fallback) => LanguageManager.Instance.GetText(key, fallback);

        /// <summary>校验一个**最终值**（已完成 模式→插值→前后缀→补位 的产物）</summary>
        internal static List<DbrValueIssue> Validate(string value, DbrValueContext ctx)
        {
            var list = new List<DbrValueIssue>();
            ctx = ctx ?? new DbrValueContext();
            string v = value ?? "";

            // ── 阻断级：会破坏文件结构 ──
            int comma = v.IndexOf(',');
            if (comma >= 0)
                list.Add(new DbrValueIssue(DbrValueSeverity.Error, "Editor.BatchGuard.Comma",
                    T("Editor.BatchGuard.Comma",
                        "值含半角逗号（,）：DBR 行按逗号分段，写入后第 2 个逗号起的内容会被丢弃")));

            int crlf = IndexOfCrLf(v);
            if (crlf >= 0)
                list.Add(new DbrValueIssue(DbrValueSeverity.Error, "Editor.BatchGuard.CrLf",
                    T("Editor.BatchGuard.CrLf",
                        "值含换行：写进文件会多出一条字段行，破坏文件结构")));

            if (HasOtherControlChar(v))
                list.Add(new DbrValueIssue(DbrValueSeverity.Error, "Editor.BatchGuard.ControlChar",
                    T("Editor.BatchGuard.ControlChar",
                        "值含控制字符（如制表符）：DBR 解析结果不可预期")));

            if (v.Length > MaxValueLength)
                list.Add(new DbrValueIssue(DbrValueSeverity.Error, "Editor.BatchGuard.ValueTooLong",
                    string.Format(T("Editor.BatchGuard.ValueTooLong",
                        "值过长（{0} 字符，上限 {1}）：几乎可以确定是误粘贴"), v.Length, MaxValueLength)));

            // ── 警告级：能写，但语义可能不是用户想的那样 ──
            bool isEmpty = v.Trim().Length == 0;
            if (isEmpty)
            {
                if (ctx.KeyExistsInOrigLines)
                    list.Add(new DbrValueIssue(DbrValueSeverity.Warn, "Editor.BatchGuard.EmptyValue",
                        T("Editor.BatchGuard.EmptyValue",
                            "值为空：会把该字段清空（写成「字段,,」），不等于恢复模板默认值")));
                else
                    list.Add(new DbrValueIssue(DbrValueSeverity.Warn, "Editor.BatchGuard.EmptyNoChange",
                        T("Editor.BatchGuard.EmptyNoChange",
                            "值为空且原文没有这一行：该文件不会发生任何变化")));
            }
            else
            {
                if (!ctx.KeyExistsInOrigLines)
                    list.Add(new DbrValueIssue(DbrValueSeverity.Warn, "Editor.BatchGuard.WillAddLine",
                        T("Editor.BatchGuard.WillAddLine",
                            "原文没有这一行：将新增一个字段行（结构性变更）")));

                if (v.IndexOf(';') >= 0 && !ctx.IsArray && !ctx.IsPicklist)
                    list.Add(new DbrValueIssue(DbrValueSeverity.Warn, "Editor.BatchGuard.Semicolon",
                        T("Editor.BatchGuard.Semicolon",
                            "值含分号（;）：该字段既不是数组也不是下拉，分号会让它变成多段")));

                if (v.Length != v.Trim().Length)
                    list.Add(new DbrValueIssue(DbrValueSeverity.Warn, "Editor.BatchGuard.Trimmed",
                        T("Editor.BatchGuard.Trimmed",
                            "值首尾有空格：读取时会被去掉，落盘内容与所见不一致")));

                if (v.Length > LongValueLength)
                    list.Add(new DbrValueIssue(DbrValueSeverity.Warn, "Editor.BatchGuard.ValueLong",
                        string.Format(T("Editor.BatchGuard.ValueLong", "值较长（{0} 字符），请确认无误"), v.Length)));

                // 数值精度：仅在"本来就是个数字"时才提示
                if (DbrArrayFormula.TryParsePlainNumber(v, out double num))
                {
                    int decimals = CountDecimals(v);
                    if (Math.Abs(num) >= 1e15 || decimals > 6)
                        list.Add(new DbrValueIssue(DbrValueSeverity.Warn, "Editor.BatchGuard.PrecisionLoss",
                            T("Editor.BatchGuard.PrecisionLoss",
                                "数值精度：最多保留 6 位小数，且绝对值 ≥1e15 会退化为小数输出")));
                }
            }

            if (ctx.KeyOccurrences > 1)
                list.Add(new DbrValueIssue(DbrValueSeverity.Warn, "Editor.BatchGuard.DuplicateKey",
                    string.Format(T("Editor.BatchGuard.DuplicateKey",
                        "该字段在原文出现 {0} 次：只会替换最后一条"), ctx.KeyOccurrences)));

            if (ctx.IsArray && ctx.TargetArrayLength >= 0 && ctx.BaseArrayLength >= 0
                && ctx.TargetArrayLength != ctx.BaseArrayLength)
                list.Add(new DbrValueIssue(DbrValueSeverity.Warn, "Editor.BatchGuard.ArrayLenDiff",
                    string.Format(T("Editor.BatchGuard.ArrayLenDiff",
                        "数组段数与基准不同（基准 {0} 段，本文件 {1} 段）"),
                        ctx.BaseArrayLength, ctx.TargetArrayLength)));

            return list;
        }

        internal static bool HasError(IReadOnlyList<DbrValueIssue> issues)
        {
            if (issues == null) return false;
            for (int i = 0; i < issues.Count; i++)
                if (issues[i].Severity == DbrValueSeverity.Error) return true;
            return false;
        }

        internal static int Count(IReadOnlyList<DbrValueIssue> issues, DbrValueSeverity severity)
        {
            if (issues == null) return 0;
            int n = 0;
            for (int i = 0; i < issues.Count; i++)
                if (issues[i].Severity == severity) n++;
            return n;
        }

        /// <summary>取最严重的一条（用于列表里只显示一行摘要）</summary>
        internal static DbrValueIssue Primary(IReadOnlyList<DbrValueIssue> issues)
        {
            if (issues == null || issues.Count == 0) return null;
            DbrValueIssue best = null;
            for (int i = 0; i < issues.Count; i++)
            {
                if (best == null || issues[i].Severity > best.Severity) best = issues[i];
                if (best.Severity == DbrValueSeverity.Error) break;
            }
            return best;
        }

        // ─────────────────────── 自动清理（程序自动，不做按钮） ───────────────────────

        /// <summary>
        /// 【自动清理】移除 dbr 标量值里**必然非法**的字符：换行（CR/LF/CRLF）与其它控制字符。
        /// <para>这类字符在 DBR 里**没有任何合法语义**（换行会多出一条字段行、控制字符解析结果不可预期），
        /// 且移除不影响用户想表达的内容 —— 属"结构修复"而非"改语义"，因此由程序自动处理，**逐行记录**。</para>
        /// <para>半角逗号**不在清理范围**：替换成什么是语义选择（全角/下划线/删除），程序无法判断，
        /// 自动改可能改坏数据含义 —— 仍由门禁阻断。</para>
        /// <para>返回清理后的值；<paramref name="removedWhat"/> 为移除内容的描述（如"换行、回车"），未清理时为 null。</para>
        /// </summary>
        internal static string AutoClean(string value, out string removedWhat)
        {
            removedWhat = null;
            string v = value ?? "";
            var sb = new System.Text.StringBuilder(v.Length);
            bool lf = false, cr = false, ctrl = false;
            foreach (char c in v)
            {
                if (c == '\n') { lf = true; continue; }
                if (c == '\r') { cr = true; continue; }
                if (char.IsControl(c)) { ctrl = true; continue; }
                sb.Append(c);
            }
            string cleaned = sb.ToString();
            if (cleaned == v) return v;

            var parts = new List<string>();
            if (lf) parts.Add("换行");
            if (cr) parts.Add("回车");
            if (ctrl) parts.Add("控制字符");
            removedWhat = string.Join("、", parts);
            return cleaned;
        }

        // ─────────────────────── 内部小工具 ───────────────────────

        private static int IndexOfCrLf(string v)
        {
            for (int i = 0; i < v.Length; i++)
                if (v[i] == '\r' || v[i] == '\n') return i;
            return -1;
        }

        /// <summary>是否含除 CR/LF 以外的 C0 控制字符（含制表符）</summary>
        private static bool HasOtherControlChar(string v)
        {
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c < 0x20 && c != '\r' && c != '\n') return true;
                if (c == 0x7F) return true;
            }
            return false;
        }

        private static int CountDecimals(string v)
        {
            int dot = v.IndexOf('.');
            return dot < 0 ? 0 : v.Length - dot - 1;
        }
    }
}
