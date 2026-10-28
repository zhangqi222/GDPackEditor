using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace GDPackEditor
{
    /// <summary>
    /// ARC 名字表编码助手（名字编码的唯一真源）。
    /// 只管【包内名字（文件名）】，与文件内容编码（TextFileHelper）不得混用：
    /// 名字 = 写入机的系统 ANSI 码页（GetACP），内容 = UTF-8 无 BOM 或原样字节。
    ///
    /// 写侧：TryEncode —— 严格按本机 ACP 编码；本机码页表示不了的字符返回首个坏字符与码位。
    /// 读侧：TryDecodeAcp / TryDecodeUtf8 / TryDecodeGbk / TryDecodeOther —— 供 ArcNameResolver 做包级判定。
    /// </summary>
    public static class NameCodec
    {
        [DllImport("kernel32.dll")]
        private static extern uint GetACP();

        /// <summary>本机 ANSI 码页：936 简中 / 932 日 / 1252 西欧 / 65001（UTF-8 模式）</summary>
        public static readonly int AcpCodePage;

        /// <summary>形如 "CP936 (gb2312)"，用于日志与提示</summary>
        public static readonly string AcpName;

        /// <summary>
        /// 给普通用户看的写法：简中机 = "GBK（CP936）"，其它码页 = "WEBNAME（CPxxxx）"。
        /// 只在面向用户的提示里用；技术日志仍用 <see cref="AcpName"/>。
        /// </summary>
        public static readonly string AcpFriendlyName;

        /// <summary>本机 ACP 的非严格编码器（解不出的字节替换为 '?'，永不抛）</summary>
        public static readonly Encoding Acp;

        private static readonly Encoding AcpStrict;   // 严格本机 ACP：解/编失败即抛
        private static readonly Encoding Utf8Strict;  // 严格 UTF-8：无 BOM、失败即抛
        private static readonly Encoding GbkStrict;   // 严格 GBK：非中文机上的兜底

        static NameCodec()
        {
            int cp = 936;
            try { cp = (int)GetACP(); } catch { }

            AcpCodePage = cp;
            Acp = CreateLoose(cp) ?? Encoding.UTF8;
            AcpStrict = CreateStrict(cp) ?? Encoding.UTF8;
            Utf8Strict = new UTF8Encoding(false, true);
            GbkStrict = CreateStrict(936) ?? Utf8Strict;
            AcpName = Describe(cp, Acp);
            // 简中机写 GBK（用户熟悉的叫法）；其它码页没有约定俗成的简称，退回 webName 大写
            AcpFriendlyName = cp == 936
                ? "GBK（CP936）"
                : $"{(string.IsNullOrEmpty(Acp?.WebName) ? "unknown" : Acp.WebName.ToUpperInvariant())}（CP{cp}）";
        }

        private static Encoding CreateLoose(int codePage)
        {
            try { return Encoding.GetEncoding(codePage); } catch { return null; }
        }

        private static Encoding CreateStrict(int codePage)
        {
            try
            {
                return Encoding.GetEncoding(codePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
            }
            catch { return null; }
        }

        private static string Describe(int codePage, Encoding enc)
        {
            string web = enc != null ? enc.WebName : "unknown";
            return $"CP{codePage} ({web})";
        }

        // ─────────────────────────── 写侧 ───────────────────────────

        /// <summary>纯 ASCII 判定（字符串形态，快速路径）</summary>
        public static bool IsAscii(string name)
        {
            return string.IsNullOrEmpty(name) || Ascii.IsValid(name);
        }

        /// <summary>纯 ASCII 判定（字节形态，快速路径）</summary>
        public static bool IsAscii(byte[] bytes)
        {
            return bytes == null || bytes.Length == 0 || Ascii.IsValid(bytes);
        }

        /// <summary>
        /// 严格按本机 ACP 编码名字（不含 NUL 终止符）。
        /// 失败时先试 NFC / NFD 规范化再判失败（CP1258 等码页对部分越南文声调用组合字符，
        /// 字符串往返校验会误判，故只能用严格编码器 + 规范化）。
        /// </summary>
        /// <param name="name">包内相对路径（/ 分隔）</param>
        /// <param name="bytes">成功时为编码结果</param>
        /// <param name="badChar">失败时为首个不可表示字符（非 BMP 时为该码元）</param>
        /// <param name="badCodePoint">失败时为首个不可表示字符的 Unicode 码位</param>
        public static bool TryEncode(string name, out byte[] bytes, out char badChar, out int badCodePoint)
        {
            bytes = null;
            badChar = '\0';
            badCodePoint = -1;
            if (name == null) name = string.Empty;

            if (TryEncodeWith(AcpStrict, name, out bytes)) return true;

            // 规范化后再试：避免把"组合式写法"误杀
            string nfc = name.Normalize(NormalizationForm.FormC);
            if (!string.Equals(nfc, name, StringComparison.Ordinal) && TryEncodeWith(AcpStrict, nfc, out bytes))
                return true;

            string nfd = name.Normalize(NormalizationForm.FormD);
            if (!string.Equals(nfd, name, StringComparison.Ordinal) && TryEncodeWith(AcpStrict, nfd, out bytes))
                return true;

            // 定位首个不可表示字符（按码位逐个试探，代理对合并为一个码位）
            for (int i = 0; i < name.Length; i++)
            {
                int size = 1;
                int cp;
                char c = name[i];
                if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
                {
                    cp = char.ConvertToUtf32(c, name[i + 1]);
                    size = 2;
                }
                else
                {
                    cp = c;
                }

                if (!TryEncodeWith(AcpStrict, name.Substring(i, size), out _))
                {
                    badChar = c;
                    badCodePoint = cp;
                    return false;
                }
                i += size - 1;
            }

            // 理论上不可达（字符逐个都能编码时整体必可编码）
            badChar = name.Length > 0 ? name[0] : '\0';
            badCodePoint = badChar;
            return false;
        }

        private static bool TryEncodeWith(Encoding enc, string s, out byte[] bytes)
        {
            bytes = null;
            try { bytes = enc.GetBytes(s); return true; }
            catch (EncoderFallbackException) { return false; }
        }

        // ─────────────────────────── 读侧 ───────────────────────────

        /// <summary>严格本机 ACP 解码</summary>
        public static bool TryDecodeAcp(byte[] b, out string s)
        {
            return TryDecodeWith(AcpStrict, b, out s);
        }

        /// <summary>严格 UTF-8 解码</summary>
        public static bool TryDecodeUtf8(byte[] b, out string s)
        {
            return TryDecodeWith(Utf8Strict, b, out s);
        }

        /// <summary>严格 GBK 解码（本机 ACP 非 936 时的兜底）</summary>
        public static bool TryDecodeGbk(byte[] b, out string s)
        {
            return TryDecodeWith(GbkStrict, b, out s);
        }

        /// <summary>严格解码指定码页（L3 多码页试探用）；该码页不可用或解码失败均返回 false</summary>
        public static bool TryDecodeOther(int codePage, byte[] b, out string s)
        {
            s = null;
            if (codePage == AcpCodePage) return TryDecodeAcp(b, out s);
            Encoding enc = GetStrictEncoding(codePage);
            if (enc == null) return false;
            return TryDecodeWith(enc, b, out s);
        }

        /// <summary>
        /// 取得该码页的严格编码器（编/解失败即抛）；码页未安装或不可用时返回 null。
        /// 结果做了缓存，供批量判定反复调用。
        /// </summary>
        public static Encoding GetStrictEncoding(int codePage)
        {
            lock (StrictCache)
            {
                if (StrictCache.TryGetValue(codePage, out Encoding cached))
                    return cached;
                Encoding enc = CreateStrict(codePage);
                StrictCache[codePage] = enc;
                return enc;
            }
        }

        private static readonly Dictionary<int, Encoding> StrictCache = new Dictionary<int, Encoding>();

        /// <summary>该码页是否可用于解码试探（本机已安装）</summary>
        public static bool IsCodePageAvailable(int codePage)
        {
            return codePage == AcpCodePage || GetStrictEncoding(codePage) != null;
        }

        private static bool TryDecodeWith(Encoding enc, byte[] b, out string s)
        {
            s = null;
            if (enc == null || b == null) return false;
            try { s = enc.GetString(b); return true; }
            catch (DecoderFallbackException) { return false; }
        }

        /// <summary>有损兜底解码：永不抛（解不出的字节按编码器默认行为替换）</summary>
        public static string DecodeLossy(byte[] b, Encoding enc)
        {
            if (b == null) return string.Empty;
            try { return (enc ?? Acp).GetString(b); }
            catch { return string.Empty; }
        }

        /// <summary>有损兜底解码（按码页）</summary>
        public static string DecodeLossy(byte[] b, int codePage)
        {
            return DecodeLossy(b, CreateLoose(codePage) ?? Acp);
        }
    }
}
