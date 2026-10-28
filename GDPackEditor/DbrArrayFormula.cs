using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GDPackEditor
{
    /// <summary>
    /// DBR 数值与公式工具（**共享实现**）：数组编辑器与批量编辑共用，避免两份拷贝各自漂移。
    ///
    /// 口径（两处必须一致）：
    /// ① 结果最多 6 位小数；② 补位只补整数部分（负号占一位）；
    /// ③ 「普通十进制数」不接受科学计数法（`1e5` 视为非数字）；
    /// ④ 递推链使用**精确值**（下一项 u = 上一项公式的原始计算结果，不取整），
    ///    要整数请在公式里用 round/floor 等（帮助见内嵌资源 !FormulaHelpCN/EN.md）。
    /// </summary>
    internal static class DbrArrayFormula
    {
        /// <summary>
        /// 格式化：最多 6 位小数（AwayFromZero）；整数且 |v| &lt; 1e15 走整数输出；
        /// padNum &gt; 0 时补整数位（左侧补 0，负号占一位）。
        /// </summary>
        internal static string FormatValue(double num, int padNum)
        {
            double v = Math.Round(num, 6, MidpointRounding.AwayFromZero);
            string s;
            if (v == Math.Floor(v) && Math.Abs(v) < 1e15)
                s = ((long)v).ToString(CultureInfo.InvariantCulture);
            else
                s = v.ToString("0.######", CultureInfo.InvariantCulture);

            if (padNum > 0)
            {
                bool isNeg = s.StartsWith("-");
                string body = isNeg ? s.Substring(1) : s;
                int dot = body.IndexOf('.');
                string intPart = dot >= 0 ? body.Substring(0, dot) : body;
                string fracPart = dot >= 0 ? body.Substring(dot) : "";
                intPart = intPart.PadLeft(Math.Max(0, padNum - (isNeg ? 1 : 0)), '0');
                s = (isNeg ? "-" : "") + intPart + fracPart;
            }
            return s;
        }

        /// <summary>
        /// 宽容解析（接受 `1e5` 这类科学计数法）：用于**输入框**，保持既有输入容忍度。
        /// 与 <see cref="TryParsePlainNumber"/> 的区别见后者注释。
        /// </summary>
        internal static double ParseDouble(string raw, double fallback)
        {
            string s = (raw ?? "").Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
            return fallback;
        }

        /// <summary>
        /// 严格解析「普通十进制数」：可有正负号与小数点，**拒绝科学计数法**（`1e5`）与任何杂字符。
        /// 用于批量编辑的"模式可用性判断 / 值形态识别"，保证与界面口径一致。
        /// （宽严两套并存是有意的：输入框容忍 `1e5`，但"是否为普通数字"的判定必须严格。）
        /// </summary>
        internal static bool TryParsePlainNumber(string raw, out double value)
        {
            value = 0;
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return false;

            // 手动排除科学计数法与千分位等 NumberStyles 允许但不属于"普通十进制"的写法
            if (s.IndexOf('e') >= 0 || s.IndexOf('E') >= 0) return false;
            if (s.IndexOf(',') >= 0 || s.IndexOf(' ') >= 0) return false;

            return double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value);
        }

        /// <summary>是否为「普通十进制数」</summary>
        internal static bool IsPlainNumber(string raw) => TryParsePlainNumber(raw, out _);

        /// <summary>
        /// 公式白名单（2026-09 定案）：开放的数学函数 + 条件函数；
        /// 三角函数、判空等名单外的内置函数一律禁用；大小写不敏感，文档统一小写。
        /// ⚠️ round / if / ifs 必须列在此处：禁用名单是按内置函数**全名单**动态生成的，
        ///    漏列即被拦（复利闭式 round(...) 与条件公式 if(...) 曾因此失效）。
        /// </summary>
        private static readonly string[] EnabledFunctions =
            { "abs", "floor", "ceiling", "truncate", "round", "min", "max", "sqrt", "exp", "log", "pow", "sign", "if", "ifs" };

        /// <summary>白名单外函数的统一拦截（求值时抛出 → 上层判定"公式无效"）</summary>
        private static readonly NCalc.Handlers.ExpressionFunction DisabledFunction =
            args => throw new NCalc.Exceptions.NCalcException("function disabled");

        /// <summary>round(x) → Round(x, 0)：内置 Round 必须两参，这里给单参数补默认小数位 0</summary>
        private static string NormalizeRoundArgs(string formula)
        {
            string Rec(string s)
            {
                var sb = new StringBuilder(s.Length + 16);
                int i = 0, n = s.Length;
                bool IdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
                while (i < n)
                {
                    if (i + 5 <= n && string.Compare(s, i, "round", 0, 5, StringComparison.OrdinalIgnoreCase) == 0
                        && (i == 0 || !IdentChar(s[i - 1])) && (i + 5 >= n || !IdentChar(s[i + 5])))
                    {
                        int j = i + 5;
                        while (j < n && char.IsWhiteSpace(s[j])) j++;
                        if (j < n && s[j] == '(')
                        {
                            int depth = 0, topCommas = 0, k = j;
                            for (; k < n; k++)
                            {
                                char c = s[k];
                                if (c == '(') depth++;
                                else if (c == ')') { depth--; if (depth == 0) break; }
                                else if (c == ',' && depth == 1) topCommas++;
                            }
                            if (k < n && depth == 0)
                            {
                                sb.Append("Round(").Append(Rec(s.Substring(j + 1, k - (j + 1))));
                                if (topCommas == 0) sb.Append(", 0");
                                sb.Append(')');
                                i = k + 1;
                                continue;
                            }
                        }
                    }
                    sb.Append(s[i]);
                    i++;
                }
                return sb.ToString();
            }
            return Rec(formula);
        }

        /// <summary>
        /// 单项求值（`^` 需在入口替换为 `**`）；解析/求值异常向上抛，由调用方判定"公式无效"。
        /// ⚠️ NCalc 7.x 默认**禁用全部内置函数**，必须经 ExpressionConfiguration 开启；
        /// 同时把白名单外的内置函数逐个禁用（大小写双变体，防大小写绕过）。
        /// </summary>
        internal static double EvalStep(string formula, double u, double n)
        {
            var cfg = NCalc.ExpressionConfiguration.FromOptions(
                NCalc.ExpressionOptions.IgnoreCaseAtBuiltInFunctions | NCalc.ExpressionOptions.NoCache);
            var expr = new NCalc.Expression(NormalizeRoundArgs(formula.Replace("^", "**")), cfg, null, CultureInfo.InvariantCulture);
            // 白名单外的内置函数统统禁用。⚠️ 必须按"原拼写 + 全小写 + 全大写"三种变体注册：
            //    IgnoreCase 模式下调用名会先按表达式原样查字典（区分大小写），
            //    只注册 Pascal 拼写会让小写调用绕过拦截（sin(1) 曾因此漏网）。
            foreach (var fn in NCalc.Helpers.BuiltInFunctionHelper.GetBuiltInFunctionNames())
            {
                if (Array.IndexOf(EnabledFunctions, fn.ToLowerInvariant()) >= 0) continue;
                expr.Functions[fn] = DisabledFunction;
                expr.Functions[fn.ToLowerInvariant()] = DisabledFunction;
                expr.Functions[fn.ToUpperInvariant()] = DisabledFunction;
            }
            expr.Parameters["u"] = u;
            expr.Parameters["n"] = n;
            return Convert.ToDouble(expr.Evaluate(), CultureInfo.InvariantCulture);
        }

        /// <summary>单数步进的生成结果</summary>
        internal sealed class StepResult
        {
            /// <summary>每项为**格式化后的纯数值字符串**（不含缩进与前后缀）；无效项（除零/无实数解）为 null</summary>
            internal readonly List<string> Values = new List<string>();
            /// <summary>公式解析或求值整体失败</summary>
            internal bool FormulaInvalid;
        }

        /// <summary>
        /// 递推生成：`v(i) = eval(公式, u = 上一项公式的原始计算值, n = i)`，首个 u = 基准值。
        /// ⚠️ 精确递推（与 Excel 同一标准）：链上不取整，要整数在公式里 round/floor。
        /// 无效行（除零/无实数解）**不产出值、也不推进 u**（与数组编辑器既有语义一致）；
        /// maxRows &gt; 0 时最多生成 maxRows 项。
        /// </summary>
        internal static StepResult GenerateStep(string formula, double baseValue, int count,
            int pad, int maxRows = 0)
        {
            var result = new StepResult();
            if (string.IsNullOrWhiteSpace(formula))
            {
                result.FormulaInvalid = true;
                return result;
            }

            double prev = baseValue;
            for (int i = 1; i <= count; i++)
            {
                if (maxRows > 0 && i > maxRows) break;

                double val;
                try { val = EvalStep(formula, prev, i); }
                catch
                {
                    result.FormulaInvalid = true;
                    return result;
                }

                if (double.IsNaN(val) || double.IsInfinity(val))
                {
                    result.Values.Add(null);   // 无效项：调用方跳过不写（或按既有方式标 !）
                    continue;
                }

                result.Values.Add(FormatValue(val, pad));
                prev = val;                    // 精确递推：只有有效项才推进 u（用原始计算值，不取整）
            }
            return result;
        }

        /// <summary>
        /// 「全部相同」模式：count 项都填基准值的格式化结果（纯数值，不含前后缀）。
        /// 基准值不是普通十进制数时返回 null（调用方据此提示"该模式要求基准为单个数值"）。
        /// </summary>
        internal static List<string> GenerateAllSame(string baseValueRaw, int count, int pad)
        {
            if (!TryParsePlainNumber(baseValueRaw, out double baseVal)) return null;
            string text = FormatValue(baseVal, pad);
            var list = new List<string>(count);
            for (int i = 0; i < count; i++) list.Add(text);
            return list;
        }

        /// <summary>
        /// 「常用公式」公共来源（生成数组弹窗与批量编辑弹窗共用，避免两份漂移）。
        /// 文案样式统一为「场景说明（公式）」；与内嵌帮助 !FormulaHelpCN/EN.md 的"常用配方"保持一致。
        /// </summary>
        internal static (string Display, string Formula)[] CommonFormulas { get; } =
        {
            (LanguageManager.Instance.GetText("Editor.FormulaEqAdd", "等量累加：每项 +5（u + 5）"), "u + 5"),
            (LanguageManager.Instance.GetText("Editor.FormulaEqSub", "等量递减：每项 -5（u - 5）"), "u - 5"),
            (LanguageManager.Instance.GetText("Editor.FormulaGrowth", "比例成长：每项 ×1.06（u * 1.06）"), "u * 1.06"),
            (LanguageManager.Instance.GetText("Editor.FormulaDecay", "比例衰减：每项 ×0.9（u * 0.9）"), "u * 0.9"),
            (LanguageManager.Instance.GetText("Editor.FormulaStairs", "每N进1：每 4 项进 1（1 + floor((n - 1) / 4)）"), "1 + floor((n - 1) / 4)"),
            (LanguageManager.Instance.GetText("Editor.FormulaCapped", "递增封顶：每项 +5，封顶 100（min(u + 5, 100)）"), "min(u + 5, 100)"),
            (LanguageManager.Instance.GetText("Editor.FormulaFloored", "递减保底：每项 -1，不低于 0（max(u - 1, 0)）"), "max(u - 1, 0)"),
            (LanguageManager.Instance.GetText("Editor.FormulaTiered", "条件：前 10 项每项 +1，之后每项 +5（if(n <= 10, u + 1, u + 5)）"), "if(n <= 10, u + 1, u + 5)"),
            (LanguageManager.Instance.GetText("Editor.FormulaSquare", "逐项加平方：每项 +n²（u + n**2）"), "u + n**2"),
            (LanguageManager.Instance.GetText("Editor.FormulaCycleNoZero", "去0周期循环：1~5 循环、不出现 0（(n - 1) % 5 + 1）"), "(n - 1) % 5 + 1"),
            (LanguageManager.Instance.GetText("Editor.FormulaClamp", "上下限限制：每项 +2，限制在 10~100（min(max(u + 2, 10), 100)）"), "min(max(u + 2, 10), 100)"),
            (LanguageManager.Instance.GetText("Editor.FormulaExpBig", "指数爆炸增长：从 100 起每项 ×1.5、用序号直接算（100 * 1.5 ^ (n - 1)）"), "100 * 1.5 ^ (n - 1)")
        };
    }
}
