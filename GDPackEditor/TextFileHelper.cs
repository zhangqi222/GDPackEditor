using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GDPackEditor
{
    /// <summary>
    /// 文本类文件的统一入口（唯一真源）：
    /// ① 扩展名白名单定义（与 ARZ/ARC 编辑器的“源码识别”集合一致，供多处共用）；
    /// ② 编码检测（BOM → 严格 UTF-8 → **本机 ACP** 回退）；
    /// ③ 编码规范化（转为 UTF-8 无 BOM，供 ARC 打包链路统一包内文本编码）。
    /// 说明：包内名称（ARC 名字表 / ARZ 记录名）使用**本机 ACP**属容器格式约定，
    /// 与本类的【文件内容】编码是两套独立机制，不得混用（见 skill §1.2 R2.4）。
    /// </summary>
    public static class TextFileHelper
    {
        /// <summary>
        /// 文本类扩展名白名单（唯一真源）。
        /// 来源：ARZ/ARC 编辑器的源码识别集合（ArzArcEditorForm.IsSearchableTextFile
        /// 与 ApplySourceSyntaxCurrent 的语法映射：.lua→lua、.txt→props、.tpl→json、.dbr→逗号染色）。
        /// </summary>
        public static readonly string[] TextExtensions = { ".dbr", ".txt", ".lua", ".tpl" };

        /// <summary>转码原因文本（多语言；缺失时回落中文，供打包行与导入警告共用）</summary>
        private static string Reason(string key, string fallback)
            => LanguageManager.Instance.GetText(key, fallback);

        /// <summary>单个文本文件参与编码规范化的体积上限，超过则跳过（避免超大文件拖慢打包/占用内存）</summary>
        public const long MaxNormalizeBytes = 8L * 1024 * 1024;

        /// <summary>编码检测的体积上限：超过则不做事先检测（避免仅为判定而读入超大文件），按默认 UTF-8 处理</summary>
        public const long MaxDetectBytes = 8L * 1024 * 1024;

        /// <summary>UTF-8 无 BOM：规范化输出统一使用（注意 Encoding.UTF8 静态实例是带 BOM 的）</summary>
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// 非 UTF-8 的兜底编码 = **本机 ANSI 码页**（简中机 = GBK/CP936，日文机 = CP932，繁体机 = CP950…）。
        /// 唯一真源见 <see cref="NameCodec.Acp"/>；宽松解码（解不出的字节替换），永不抛异常。
        /// </summary>
        private static Encoding LocalAnsi => NameCodec.Acp;

        /// <summary>按扩展名判断是否为文本类文件</summary>
        public static bool IsTextFile(string pathOrRelPath)
        {
            if (string.IsNullOrEmpty(pathOrRelPath)) return false;
            return IsTextFileExtension(Path.GetExtension(pathOrRelPath));
        }

        /// <summary>按扩展名（含点，如 ".txt"）判断是否属于文本类白名单</summary>
        public static bool IsTextFileExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            for (int i = 0; i < TextExtensions.Length; i++)
            {
                if (string.Equals(ext, TextExtensions[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ─────────────────────────── 编码判定 ───────────────────────────

        /// <summary>UTF-8 BOM（EF BB BF）判定</summary>
        public static bool IsUtf8Bom(byte[] data)
            => data != null && data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;

        /// <summary>字节级严格 UTF-8 校验（零分配、不抛异常）。空数组视为合法。</summary>
        public static bool IsValidUtf8(byte[] data) => IsValidUtf8(data, 0, data?.Length ?? 0);

        /// <summary>字节级严格 UTF-8 校验（指定区间，零分配、不抛异常）</summary>
        public static bool IsValidUtf8(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0) return true;
            var span = new ReadOnlySpan<byte>(data, offset, count);
            while (!span.IsEmpty)
            {
                if (Rune.DecodeFromUtf8(span, out _, out int consumed) != OperationStatus.Done)
                    return false;
                span = span.Slice(consumed);
            }
            return true;
        }

        /// <summary>
        /// 检测文本文件实际编码：BOM 优先；无 BOM 时按**严格 UTF-8**判定，不合法则回退**本机 ACP**。
        /// 兼容 UTF-8（有/无 BOM）与本机 ANSI（如简体 GBK）源文件；游戏官方包内文本实测为 UTF-8。
        /// 超过 <see cref="MaxDetectBytes"/> 的文件不做判定，按默认 UTF-8 处理。
        /// </summary>
        public static Encoding DetectEncodingFromFile(string filePath)
        {
            if (new FileInfo(filePath).Length > MaxDetectBytes) return Encoding.UTF8;
            return DetectEncoding(File.ReadAllBytes(filePath));
        }

        /// <summary>按字节内容检测编码（BOM → 严格 UTF-8 → 本机 ACP 回退）</summary>
        public static Encoding DetectEncoding(byte[] data)
        {
            if (data == null || data.Length == 0) return Encoding.UTF8;
            if (IsUtf8Bom(data)) return Encoding.UTF8;
            return IsValidUtf8(data) ? Encoding.UTF8 : LocalAnsi;
        }

        /// <summary>
        /// 读取文本文件的行（**只读一次文件**）：读字节 → 检测编码 → 解码成行。
        /// 与 File.ReadAllLines(path, enc) 行为一致：BOM 自动识别并剥离、换行按 ReadLine 规则。
        /// </summary>
        public static string[] ReadTextLines(string filePath) => ReadTextLines(filePath, out _);

        /// <summary>
        /// 同上，并回带**实际用到的编码**（供调用方按同一编码回写，实现零转码）。
        /// </summary>
        public static string[] ReadTextLines(string filePath, out Encoding usedEncoding)
        {
            byte[] data = File.ReadAllBytes(filePath);
            Encoding fallback = data.Length > MaxDetectBytes ? Encoding.UTF8 : DetectEncoding(data);
            using (var ms = new MemoryStream(data, writable: false))
            using (var sr = new StreamReader(ms, fallback, detectEncodingFromByteOrderMarks: true))
            {
                var lines = new List<string>();
                string line;
                while ((line = sr.ReadLine()) != null) lines.Add(line);
                usedEncoding = sr.CurrentEncoding;   // BOM 检测完成后的实际编码
                return lines.ToArray();
            }
        }

        /// <summary>
        /// 读取文本文件的**全部内容**（自动检测编码），并回带实际编码 —— 等价于
        /// `File.ReadAllText(path, 检测到的编码)`，但**检测口径收敛在本类内**（含 BOM 处理与体积上限）。
        /// <para>编辑器 / MOD 合并的**临时文件读取统一走这里**：临时文件的编码取决于
        /// 【设置 → 编码 → 内容解包】或用户外部编辑器的保存结果，不能假定是 UTF-8。</para>
        /// </summary>
        public static string ReadText(string filePath, out Encoding usedEncoding)
        {
            byte[] data = File.ReadAllBytes(filePath);
            // 与 DetectEncodingFromFile 同口径：超上限不做事先检测，按 UTF-8 处理
            Encoding fallback = data.Length > MaxDetectBytes ? Encoding.UTF8 : DetectEncoding(data);
            using (var ms = new MemoryStream(data, writable: false))
            using (var sr = new StreamReader(ms, fallback, detectEncodingFromByteOrderMarks: true))
            {
                string text = sr.ReadToEnd();
                usedEncoding = sr.CurrentEncoding;
                return text;
            }
        }

        /// <summary>读文本文件全部内容（不需要编码时用这个重载）</summary>
        public static string ReadText(string filePath) => ReadText(filePath, out _);

        /// <summary>
        /// 解码文本字节：剥离 UTF-8 BOM 后按 UTF-8 解码，出现非法序列则回退**本机 ACP**。
        /// 剥离 BOM 是为了避免 U+FEFF 混入文本，在后续编辑/保存时被当作乱码写入而破坏文件内容。
        /// </summary>
        public static string DecodeText(byte[] data) => DecodeText(data, out _);

        /// <summary>同上，并回带**实际用到的编码**（供调用方按同一编码回写，实现零转码）。</summary>
        public static string DecodeText(byte[] data, out Encoding usedEncoding)
        {
            if (data == null || data.Length == 0) { usedEncoding = Encoding.UTF8; return ""; }
            if (IsUtf8Bom(data))
            {
                byte[] body = new byte[data.Length - 3];
                Buffer.BlockCopy(data, 3, body, 0, body.Length);
                if (IsValidUtf8(body)) { usedEncoding = Encoding.UTF8; return Encoding.UTF8.GetString(body); }
                usedEncoding = LocalAnsi;
                return LocalAnsi.GetString(body);
            }
            if (IsValidUtf8(data)) { usedEncoding = Encoding.UTF8; return Encoding.UTF8.GetString(data); }
            usedEncoding = LocalAnsi;
            return LocalAnsi.GetString(data);
        }

        // ─────────────────────────── 编码规范化 ───────────────────────────

        /// <summary>
        /// 规范化结果：Unchanged=内容无需改动（已是目标编码 / 全 ASCII / 原样档）；Converted=已转换；Skipped=按规则跳过。
        /// </summary>
        public enum NormalizeOutcome
        {
            Unchanged,
            Converted,
            Skipped
        }

        /// <summary>目标编码之一：UTF-8 无 BOM（对外暴露，供"设置 → 目标编码"映射共用）</summary>
        public static Encoding Utf8NoBomTarget => Utf8NoBom;

        /// <summary>
        /// 【唯一真源】把文本字节转为**目标编码**，打包 / 导入 / 解包各链路共用。
        /// <para><paramref name="target"/> = <c>null</c> → **【原样档】**：不做任何转换，<c>dst</c> 即源字节（官方行为）。
        /// 放在最前短路：零扫描、零分配。</para>
        /// <para><paramref name="target"/> = <see cref="NameCodec.Acp"/> → 统一转**本机 ACP**；
        /// = <see cref="Utf8NoBomTarget"/> → 统一转 **UTF-8 无 BOM**。</para>
        /// <para>规则：含 NUL 字节判为二进制跳过；**全 ASCII 短路**（两种目标编码下字节完全相同）；
        /// 源编码按 `UTF-8 BOM → UTF-16 BOM → 严格 UTF-8 → 本机 ACP` 判定；源与目标同编码则无需改动。
        /// 换行符一律不动（只改编码）。</para>
        /// </summary>
        public static NormalizeOutcome NormalizeToTarget(byte[] src, Encoding target, out byte[] dst, out string reason)
        {
            dst = src;
            reason = null;

            if (src == null || src.Length == 0)
            {
                reason = Reason("LOG.Norm.EmptyFile", "空文件");
                return NormalizeOutcome.Skipped;
            }

            // 【原样档】目标为空 → 不转换、不动字节（官方行为）
            if (target == null)
            {
                reason = Reason("LOG.Norm.Original", "原样（未做编码转换）");
                return NormalizeOutcome.Unchanged;
            }

            // 一趟扫描同时取两个信息：二进制保险（NUL）与全 ASCII 短路
            bool hasNul = false, allAscii = true;
            for (int i = 0; i < src.Length; i++)
            {
                byte b = src[i];
                if (b == 0) hasNul = true;
                else if (b >= 0x80) allAscii = false;
            }
            if (hasNul)
            {
                reason = Reason("LOG.Norm.BinaryNul", "含NUL字节，判为二进制");
                return NormalizeOutcome.Skipped;
            }
            if (allAscii)
            {
                // 全 ASCII：UTF-8 与本机 ACP 的字节完全相同，无需任何解码/编码
                reason = Reason("LOG.Norm.AlreadyAscii", "全ASCII，两种目标编码下字节相同");
                return NormalizeOutcome.Unchanged;
            }

            // 源编码判定：BOM 优先 → 严格 UTF-8 → 本机 ACP
            Encoding srcEnc = Utf8NoBom;
            int start = 0;
            bool srcHasBom = false;
            if (IsUtf8Bom(src))
            {
                srcHasBom = true;
                start = 3;
                // BOM 之后若已不是合法 UTF-8，按本机 ACP 兜底（与原实现一致：宁可当 ACP，
                // 也不能当 UTF-8 解码 —— 后者会用替换字符把原始字节毁掉）
                byte[] body = new byte[src.Length - 3];
                Buffer.BlockCopy(src, 3, body, 0, body.Length);
                if (!IsValidUtf8(body)) srcEnc = LocalAnsi;
            }
            else if (src.Length >= 2 && ((src[0] == 0xFF && src[1] == 0xFE) || (src[0] == 0xFE && src[1] == 0xFF)))
            {
                // 注：UTF-16 文本必然含 NUL 字节，正常路径已在上方被判为二进制；
                // 保留此分支以维持与原实现一致的兜底语义。
                srcEnc = src[0] == 0xFE ? Encoding.BigEndianUnicode : Encoding.Unicode;
                srcHasBom = true;
                start = 2;
            }
            else if (!IsValidUtf8(src))
            {
                srcEnc = LocalAnsi;
            }

            // 源与目标同编码且无 BOM → 字节已一致，无需改动
            if (!srcHasBom && string.Equals(srcEnc.WebName, target.WebName, StringComparison.OrdinalIgnoreCase))
            {
                reason = Reason("LOG.Norm.AlreadyTarget", "已是目标编码，无需转换");
                return NormalizeOutcome.Unchanged;
            }

            string text = srcEnc.GetString(src, start, src.Length - start);
            dst = target.GetBytes(text);
            reason = string.Format(Reason("LOG.Norm.Converted", "{0} → {1}"),
                SourceEncodingName(srcEnc, srcHasBom), TargetEncodingName(target));
            return NormalizeOutcome.Converted;
        }

        /// <summary>
        /// 把文本字节规范化为 UTF-8 无 BOM（<see cref="NormalizeToTarget"/> 的 UTF-8 目标特例）。
        /// 规则：含 NUL 字节判为二进制跳过；**全 ASCII 直接判为已是 UTF-8**（零解码）；
        /// UTF-8 带 BOM 剥 BOM；UTF-16 转 UTF-8；其余按**本机 ACP**解码后转 UTF-8。
        /// 换行符一律不动（只改编码）。
        /// </summary>
        public static NormalizeOutcome NormalizeToUtf8NoBom(byte[] src, out byte[] dst, out string reason)
            => NormalizeToTarget(src, Utf8NoBom, out dst, out reason);

        /// <summary>源编码的展示名（用于转码日志，如 "UTF-8 带BOM" / "GBK（CP936）"）</summary>
        private static string SourceEncodingName(Encoding srcEnc, bool hasBom)
        {
            if (string.Equals(srcEnc.WebName, Utf8NoBom.WebName, StringComparison.OrdinalIgnoreCase))
                return hasBom
                    ? Reason("LOG.Norm.SrcUtf8Bom", "UTF-8 带BOM")
                    : Reason("LOG.Norm.SrcUtf8", "UTF-8 无BOM");
            if (string.Equals(srcEnc.WebName, Encoding.Unicode.WebName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(srcEnc.WebName, Encoding.BigEndianUnicode.WebName, StringComparison.OrdinalIgnoreCase))
                return Reason("LOG.Norm.SrcUtf16", "UTF-16");
            return NameCodec.AcpFriendlyName;
        }

        /// <summary>目标编码的展示名（用于转码日志）</summary>
        private static string TargetEncodingName(Encoding target)
            => string.Equals(target.WebName, Utf8NoBom.WebName, StringComparison.OrdinalIgnoreCase)
                ? Reason("LOG.Norm.DstUtf8", "UTF-8 无BOM")
                : NameCodec.AcpFriendlyName;
    }
}
