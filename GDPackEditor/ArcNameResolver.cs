using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GDPackEditor
{
    /// <summary>非 ASCII 名字的"严格合法性"分桶结果</summary>
    public enum ArcNameKind
    {
        /// <summary>纯 ASCII（任何码页下字节相同，无歧义）</summary>
        Ascii,
        /// <summary>只有本机 ACP 能解</summary>
        OnlyAcp,
        /// <summary>只有 UTF-8 能解</summary>
        OnlyUtf8,
        /// <summary>只有某个其它码页能解（如 CP932）</summary>
        OnlyOther,
        /// <summary>两种以上编码都能解（歧义区）</summary>
        Ambiguous,
        /// <summary>任何编码都解不了</summary>
        Invalid
    }

    /// <summary>包级判定结果：整包一次定出的"包内编码"及其依据</summary>
    public sealed class ArcNameResolveResult
    {
        /// <summary>定出的包内编码（全 ASCII 时为本机 ACP）</summary>
        public Encoding Chosen = NameCodec.Acp;

        /// <summary>定出编码的可读名，如 "CP936 (gb2312)" / "UTF-8"</summary>
        public string ChosenName = NameCodec.AcpName;

        /// <summary>判定依据（L1~L5 说明 + 分桶计数），写日志用</summary>
        public string Evidence = "";

        /// <summary>L3 选中的其它码页编号；未命中为 0</summary>
        public int OtherCodePage = 0;

        public int Scanned;
        public int NonAscii;
        public int AmbiguousCount;
        public int InvalidCount;

        /// <summary>详情级样例：歧义名的两种解读</summary>
        public readonly List<string> AmbiguousSamples = new List<string>();

        /// <summary>全 ASCII 包（不输出任何编码相关记录）</summary>
        public bool IsAsciiOnly => NonAscii == 0;
    }

    /// <summary>
    /// ARC 读取侧名字编码的包级判定（规范见 skill §5）。
    /// 逐名猜必错（单字非 ASCII 名有 13.7%~18.1% 落在两种编码的重叠区），
    /// 故用"整包单一编码"这一全局约束消解局部歧义。
    /// 本类只做判定与解码，不产生任何 UI 依赖。
    /// </summary>
    public static class ArcNameResolver
    {
        /// <summary>L3 多码页试探的候选码页（仅试探本机已安装者）。skill §5.4</summary>
        private static readonly int[] OtherCandidates = { 936, 950, 932, 949, 1251, 1252, 1258, 874 };

        /// <summary>
        /// 包级判定：一次定出整包编码。
        /// 传入的是名字的【原始字节】（不含 NUL），已跳过死条目与空名。
        /// </summary>
        public static ArcNameResolveResult Resolve(IReadOnlyList<byte[]> nameBytes, int sampleLimit = 5)
        {
            ArcNameResolveResult r = new ArcNameResolveResult();
            if (nameBytes == null) return r;

            r.Scanned = nameBytes.Count;

            // ② 逐名分桶（只测含非 ASCII 字节的名字）
            int onlyAcp = 0, onlyUtf8 = 0, onlyOther = 0, ambiguous = 0, invalid = 0;
            int utf8Implausible = 0, acpImplausible = 0; // L4 字符集合理性（方向互补，见 IsPlausibleName）
            int otherTotal = 0;
            Dictionary<int, int> otherHits = new Dictionary<int, int>();

            foreach (byte[] raw in nameBytes)
            {
                if (raw == null || raw.Length == 0) continue;
                if (NameCodec.IsAscii(raw)) continue;
                r.NonAscii++;

                string acpStr = null, utf8Str = null;
                bool okAcp = NameCodec.TryDecodeAcp(raw, out acpStr);
                bool okUtf8 = NameCodec.TryDecodeUtf8(raw, out utf8Str);

                // 其它码页试探（跳过本机 ACP，已在上面测过）
                int hitCount = 0;
                foreach (int cp in OtherCandidates)
                {
                    if (cp == NameCodec.AcpCodePage) continue;
                    if (!NameCodec.IsCodePageAvailable(cp)) continue;
                    if (NameCodec.TryDecodeOther(cp, raw, out string _))
                    {
                        hitCount++;
                        otherTotal++;
                        otherHits.TryGetValue(cp, out int n);
                        otherHits[cp] = n + 1;
                    }
                }

                // 分桶只用【本机 ACP / UTF-8】两把尺子：
                // 其它码页是 L3 的弱证据，不能因为"恰好也能解"就把 ACP/UTF-8 之争降级为歧义。
                if (okAcp && okUtf8)
                {
                    ambiguous++;
                    r.AmbiguousCount++;
                    if (r.AmbiguousSamples.Count < sampleLimit)
                    {
                        r.AmbiguousSamples.Add(string.Format(
                            "'{0}'：ACP={1} / UTF-8={2}",
                            acpStr, acpStr, utf8Str));
                    }
                }
                else if (okAcp) onlyAcp++;
                else if (okUtf8) onlyUtf8++;
                else if (hitCount > 0) onlyOther++;
                else
                {
                    invalid++;
                    r.InvalidCount++;
                }

                // 铁证：只在"两种解读都成立"时才有裁决力
                if (okAcp && okUtf8)
                {
                    bool plausAcp = IsPlausibleName(acpStr);
                    bool plausUtf8 = IsPlausibleName(utf8Str);
                    if (plausAcp && !plausUtf8) utf8Implausible++;
                    else if (plausUtf8 && !plausAcp) acpImplausible++;
                }
            }

            string buckets = string.Format(
                LanguageManager.Instance.GetText("ArcName.EvidenceBuckets",
                    "按 {0} 可解 {1} 个 / 按 UTF-8 可解 {2} 个 / 按其它码页可解 {3} 个 / 两种都可解 {4} 个 / 都无法解 {5} 个"),
                NameCodec.AcpName, onlyAcp, onlyUtf8, onlyOther, ambiguous, invalid);

            if (r.NonAscii == 0)
            {
                r.Chosen = NameCodec.Acp;
                r.ChosenName = NameCodec.AcpName;
                r.Evidence = buckets;
                return r;
            }

            // ③ 包级定案：证据分级，命中即停
            // 说明文本一律"就事论事"描述包内名字本身（哪种编码能解通 / 解出的名字是否合理），
            // 不写"某台机器能解/不能解"这类说法 —— 同名编码环境下任何机器结果都一样。
            string reason;

            // L1 决定性：只有一种编码合法，且反向为 0
            if (onlyAcp > 0 && onlyUtf8 == 0 && onlyOther == 0)
            {
                SetAcp(r);
                reason = string.Format(
                    LanguageManager.Instance.GetText("ArcName.L1AcpOnly",
                        "L1 只有 {0} 能解通全部名字，其它候选编码均解不通"), r.ChosenName);
            }
            else if (onlyUtf8 > 0 && onlyAcp == 0 && onlyOther == 0)
            {
                SetUtf8(r);
                reason = LanguageManager.Instance.GetText("ArcName.L1Utf8Only",
                    "L1 只有 UTF-8 能解通全部名字，其它候选编码均解不通");
            }
            // L3 强证据：唯一非零的其它码页（读取方装得下才试探）
            else if (otherHits.Count == 1 && otherTotal > 0 && onlyAcp == 0 && onlyUtf8 == 0)
            {
                int cp = FirstKey(otherHits);
                SetOther(r, cp);
                reason = string.Format(
                    LanguageManager.Instance.GetText("ArcName.L3OtherCodePage",
                        "L3 只有 {0} 能解通全部名字，其它候选编码均解不通"), r.ChosenName);
            }
            // L4 弱证据：字符集合理性铁证（能识破"GBK 名被当 UTF-8 解"）
            else if (utf8Implausible > 0 && acpImplausible == 0)
            {
                SetAcp(r);
                reason = string.Format(
                    LanguageManager.Instance.GetText("ArcName.L4Utf8Implausible",
                        "L4 两种编码都能解通，但按 UTF-8 解出的名字含 {0} 个不合理字符，取 {1}"),
                    utf8Implausible, r.ChosenName);
            }
            else if (acpImplausible > 0 && utf8Implausible == 0)
            {
                SetUtf8(r);
                reason = string.Format(
                    LanguageManager.Instance.GetText("ArcName.L4AcpImplausible",
                        "L4 两种编码都能解通，但按 {0} 解出的名字含 {1} 个不合理字符，取 UTF-8"),
                    NameCodec.AcpName, acpImplausible);
            }
            // L5 默认：一致地按单一码页解（同区写入最常见）
            else
            {
                SetAcp(r);
                reason = string.Format(
                    LanguageManager.Instance.GetText("ArcName.L5DefaultAcp",
                        "L5 两种编码都能解通且无其它线索，默认取 {0}"), r.ChosenName);
            }

            r.Evidence = string.Format("{0}（{1}）", reason, buckets);
            return r;
        }

        private static void SetAcp(ArcNameResolveResult r)
        {
            r.Chosen = NameCodec.Acp;
            r.ChosenName = NameCodec.AcpName;
            r.OtherCodePage = 0;
        }

        private static void SetUtf8(ArcNameResolveResult r)
        {
            r.Chosen = new UTF8Encoding(false, false);
            r.ChosenName = "UTF-8";
            r.OtherCodePage = 0;
        }

        private static void SetOther(ArcNameResolveResult r, int codePage)
        {
            r.Chosen = NameCodec.GetStrictEncoding(codePage) ?? NameCodec.Acp;
            r.ChosenName = string.Format("CP{0} ({1})", codePage,
                NameCodec.GetStrictEncoding(codePage)?.WebName ?? "unknown");
            r.OtherCodePage = codePage;
        }

        private static int FirstKey(Dictionary<int, int> map)
        {
            foreach (int k in map.Keys) return k;
            return 0;
        }

        /// <summary>
        /// 套用判定结果解码单个名字；解失败则逐名兜底：判定编码 → 本机 ACP → UTF-8 → GBK → 有损。
        /// </summary>
        /// <param name="fellBackTo">走了兜底时给出所用编号（否则为 null）</param>
        public static string Decode(byte[] bytes, ArcNameResolveResult r, out string fellBackTo)
        {
            fellBackTo = null;
            if (bytes == null || bytes.Length == 0) return string.Empty;
            if (r == null) r = new ArcNameResolveResult();

            if (NameCodec.IsAscii(bytes))
                return NameCodec.DecodeLossy(bytes, Encoding.ASCII);

            // 1) 判定编码
            string s;
            int chosenCp = r.Chosen != null ? r.Chosen.CodePage : NameCodec.AcpCodePage;
            if (TryDecodeByCodePage(chosenCp, bytes, out s))
                return s;

            // 2) 本机 ACP → 3) UTF-8 → 4) GBK
            if (chosenCp != NameCodec.AcpCodePage && NameCodec.TryDecodeAcp(bytes, out s))
            {
                fellBackTo = NameCodec.AcpName;
                return s;
            }
            if (chosenCp != 65001 && NameCodec.TryDecodeUtf8(bytes, out s))
            {
                fellBackTo = "UTF-8";
                return s;
            }
            if (chosenCp != 936 && NameCodec.TryDecodeGbk(bytes, out s))
            {
                fellBackTo = "CP936 (gb2312)";
                return s;
            }

            // 5) 有损（永不抛）
            fellBackTo = string.Format(
                LanguageManager.Instance.GetText("ArcName.LossySuffix", "{0}（有损）"), r.ChosenName);
            return NameCodec.DecodeLossy(bytes, chosenCp);
        }

        private static bool TryDecodeByCodePage(int codePage, byte[] bytes, out string s)
        {
            if (codePage == NameCodec.AcpCodePage) return NameCodec.TryDecodeAcp(bytes, out s);
            if (codePage == 65001) return NameCodec.TryDecodeUtf8(bytes, out s);
            if (codePage == 936) return NameCodec.TryDecodeGbk(bytes, out s);
            return NameCodec.TryDecodeOther(codePage, bytes, out s);
        }

        // ─────────────────── 免费铁证：字符集合理性（两个解读方向互补） ───────────────────
        //
        // 关于 skill §5.3 的另一个铁证「扩展名完整性」，实现说明（已验证，不再保留该级）：
        //   任何一个候选码页里，前导字节/续字节都 ≥0x40（CP936 续字节 0x40–0xFE、CP932 0x40–0xFC、
        //   CP949 0x41–0xFE、UTF-8 续字节 0x80–0xBF），而 `.` = 0x2E < 0x40 —— 因此 『.』
        //   永远不可能被当作续字节吃掉，任何**严格**解码都逐字节保留全部 ASCII 字符。
        //   ⇒ 「扩展名完整性」对"同一串字节的两种候选解读"恒定给出相同结果，永远无法裁决歧义
        //     （skill 中的 `涓枃鍚?txt` 是**有损**解码的产物；有损解码在本实现里只用于最后的兜底，
        //      不参与候选比较）。故本实现只保留真正有裁决力的「字符集合理性」。

        /// <summary>
        /// 免费铁证：字符集合理性。资源名由"拉丁字母/数字/CJK/假名/_-. ()[]"构成；
        /// 出现组合记号、修饰字母、半角片假名、希腊/西里尔/阿拉伯等"与资源名明显不符"的文字即判为不合理。
        /// 能识破"GBK 名被当 UTF-8 解"（如 `图.txt` → `ͼ.txt`、`路.txt` → `·.txt`）；
        /// 反方向（UTF-8 名被当 ACP 解）通常由 L1 的"仅 UTF-8 可解"直接定案。
        /// </summary>
        public static bool IsPlausibleName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in name)
            {
                if (c < 0x80) continue;                     // ASCII 一律合理
                if (IsImplausibleChar(c)) return false;
            }
            return true;
        }

        private static bool IsImplausibleChar(char c)
        {
            // 资源名由"拉丁字母/数字/CJK 汉字/假名/常用符号"构成。
            // U+0080–U+074F 覆盖：拉丁补充与扩展、修饰字母、组合记号、希腊、西里尔、
            // 亚美尼亚、希伯来、阿拉伯、叙利亚 —— 出现在资源名里极不合理，
            // 多为"按错编码解读"的产物（如 图.txt → ͼ.txt、路.txt → ·.txt）。
            if (c >= 0x0080 && c <= 0x074F) return true;
            // 半角片假名
            if (c >= 0xFF61 && c <= 0xFF9F) return true;

            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.Surrogate:
                    return true;
            }

            return false;
        }
    }
}
