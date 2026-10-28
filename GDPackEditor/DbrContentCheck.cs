using System.Collections.Generic;

namespace GDPackEditor
{
    /// <summary>值内容问题的位掩码（热路径零分配用；判定与报告顺序同 <see cref="DbrContentCheck.Check"/>：换行→逗号→控制字符）</summary>
    [System.Flags]
    internal enum DbrContentFlags
    {
        None = 0,
        Newline = 1,
        Comma = 2,
        ControlChar = 4,
    }

    /// <summary>
    /// DBR 字段值内容问题的统一检测（值含换行 / 值含逗号 / 值含控制字符）。
    /// 解包 / 打包 / 检测按钮 / CLI --check 四条链路共用同一套判定与文案，
    /// 保证行为一致：出现即报告，末尾按分类汇总数量。
    /// <para>行为约定：换行会破坏 DBR 行结构（解包/编辑器侧自动删除），逗号与控制字符
    /// 仅报告不改动（按原样保留）。</para>
    /// <para>热路径（解包/检测的每条每字段，百万级调用）用零分配的 <see cref="CheckFlags"/>；
    /// 需要遍历种类列表的低频路径用 <see cref="Check"/>（内部同源）。</para>
    /// </summary>
    internal static class DbrContentCheck
    {
        public const string KindNewline = "newline";
        public const string KindComma = "comma";
        public const string KindControlChar = "control";

        /// <summary>零分配判定：返回问题位掩码（热路径用，不产生任何 GC 分配）</summary>
        public static DbrContentFlags CheckFlags(string value)
        {
            if (string.IsNullOrEmpty(value)) return DbrContentFlags.None;
            DbrContentFlags f = DbrContentFlags.None;
            if (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) f |= DbrContentFlags.Newline;
            if (value.IndexOf(',') >= 0) f |= DbrContentFlags.Comma;
            if (HasControlChar(value)) f |= DbrContentFlags.ControlChar;
            return f;
        }

        /// <summary>位掩码 → 种类列表（顺序与 <see cref="Check"/> 一致：换行→逗号→控制字符）</summary>
        public static List<string> ToKindList(DbrContentFlags flags)
        {
            var kinds = new List<string>(3);
            if ((flags & DbrContentFlags.Newline) != 0) kinds.Add(KindNewline);
            if ((flags & DbrContentFlags.Comma) != 0) kinds.Add(KindComma);
            if ((flags & DbrContentFlags.ControlChar) != 0) kinds.Add(KindControlChar);
            return kinds;
        }

        /// <summary>检查一个字段值文本，返回问题种类列表（顺序：换行→逗号→控制字符；可多个并存）</summary>
        public static List<string> Check(string value) => ToKindList(CheckFlags(value));

        /// <summary>是否含除换行外的控制字符（含制表符；与检测按钮/CLI --check 既有口径一致）</summary>
        public static bool HasControlChar(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (char.IsControl(c) && c != '\r' && c != '\n') return true;
            return false;
        }

        /// <summary>
        /// 第一个控制字符的位置（0 基；-1 = 无）。口径与 <see cref="HasControlChar"/> 完全一致
        /// （char.IsControl 且排除 \r \n）——供检测侧报"位于第 N 个字符处"。
        /// </summary>
        public static int IndexOfControlChar(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsControl(c) && c != '\r' && c != '\n') return i;
            }
            return -1;
        }

        /// <summary>问题种类的本地化短描述（检测/解包/打包逐条警告与汇总共用一套文案）</summary>
        public static string Describe(string kind) => kind switch
        {
            KindNewline => LanguageManager.Instance.GetText("LOG.ContentCheckNewline", "值含换行"),
            KindComma => LanguageManager.Instance.GetText("LOG.ContentCheckComma", "值含逗号"),
            KindControlChar => LanguageManager.Instance.GetText("LOG.ContentCheckControlChar", "值含控制字符"),
            _ => kind
        };
    }
}
