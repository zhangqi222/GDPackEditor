using System;
using System.Text;

namespace GDPackEditor
{
    /// <summary>
    /// 编码相关工作的**统一入口（门面）**。所有"解 / 打 / 读"链路都从这里取设置与能力，
    /// 不再各自去读 SettingsManager、也不再各自拼编码逻辑。
    ///
    /// ⚠ **只做路由与设置取值，绝不合并两套逻辑**（plan §6.4 的边界）：
    ///   · 名字部分 → 委托 <see cref="NameCodec"/> / <see cref="ArcNameResolver"/>
    ///     —— 名字**跟机器走**（本机 ANSI 码页），规则来自容器格式约定；
    ///   · 内容部分 → 委托 <see cref="TextFileHelper"/>
    ///     —— 内容**零转码为底**（官方行为），统一档才按目标编码转。
    /// 名字与内容是两套独立机制，互相套用必然出错（skill §1.2 R2.4）。
    /// </summary>
    public static class EncodingService
    {
        // ─────────────────────── 名字部分（跟机器走）───────────────────────

        /// <summary>名字写出编码：恒为**本机 ANSI 码页**（官方行为，不受"内容"设置影响）</summary>
        public static Encoding NameEncoding => NameCodec.Acp;

        /// <summary>给用户看的名字编码名（如 "GBK（CP936）"）</summary>
        public static string NameEncodingFriendly => NameCodec.AcpFriendlyName;

        /// <summary>
        /// 「过滤非ASCII文件名」是否开启（**一次读取，供一个打包操作内复用**）。
        /// 作用范围：**ARC 名字表 + ARZ 记录名**（两者规则相同 —— 都跟本机码页走、都按本设置决定是否过滤）。
        /// 开启时含非 ASCII 的名字一律**不写进包**，并在结束时给【汇总告警】（绝不静默跳过）。
        /// </summary>
        public static bool NameFilterEnabled =>
            string.Equals(SettingsManager.NameMode, SettingsManager.NameModeFilterAscii,
                StringComparison.OrdinalIgnoreCase);

        /// <summary>名字能否用本机码页表示（表示不了的写进包会变成 '?'，必须跳过而非静默损坏）</summary>
        public static bool CanEncodeName(string name)
            => NameCodec.TryEncode(name, out _, out _, out _);

        // ─────────────────────── 内容部分（零转码为底）───────────────────────

        /// <summary>打包时内容的目标编码；null = **原样档**（零转码）</summary>
        public static Encoding PackTarget => SettingsManager.ContentPackTarget;

        /// <summary>解包时内容的目标编码；null = **原样档**（零转码）</summary>
        public static Encoding UnpackTarget => SettingsManager.ContentUnpackTarget;

        /// <summary>打包内容是否需要转码。false = 原样档 → 可整体跳过所有检测（零成本）</summary>
        public static bool PackNeedsConvert => PackTarget != null;

        /// <summary>解包内容是否需要转码。false = 原样档 → 可整体跳过所有检测（零成本）</summary>
        public static bool UnpackNeedsConvert => UnpackTarget != null;

        /// <summary>检测字节的内容编码（UTF-8 BOM → 严格 UTF-8 → 本机 ACP）</summary>
        public static Encoding Detect(byte[] data) => TextFileHelper.DetectEncoding(data);

        /// <summary>按**指定**目标编码转码；<paramref name="target"/> = null → 原样档，不做任何转换</summary>
        public static TextFileHelper.NormalizeOutcome Normalize(byte[] src, Encoding target, out byte[] dst, out string reason)
            => TextFileHelper.NormalizeToTarget(src, target, out dst, out reason);

        /// <summary>打包用：按【内容打包】设置把源字节转成应写入包的字节（原样档直接返回源字节）</summary>
        public static TextFileHelper.NormalizeOutcome NormalizeForPack(byte[] src, out byte[] dst, out string reason)
            => TextFileHelper.NormalizeToTarget(src, PackTarget, out dst, out reason);

        /// <summary>解包用：按【内容解包】设置把包内字节转成应落盘的字节（原样档直接返回源字节）</summary>
        public static TextFileHelper.NormalizeOutcome NormalizeForUnpack(byte[] src, out byte[] dst, out string reason)
            => TextFileHelper.NormalizeToTarget(src, UnpackTarget, out dst, out reason);

        /// <summary>读文本文件（自动检测编码并回带）。**编辑器 / MOD 合并读临时文件统一走这里**。</summary>
        public static string ReadTextFile(string path, out Encoding usedEncoding)
            => TextFileHelper.ReadText(path, out usedEncoding);

        /// <summary>读文本文件（不需要编码时用这个重载）</summary>
        public static string ReadTextFile(string path) => TextFileHelper.ReadText(path);

        /// <summary>
        /// **临时文件（编辑器 / MOD 合并的中间产物）落盘用的编码**。
        /// <para>取【内容打包】的目标：统一档 = 目标编码（与最终入包一致，不可表示的字符**当场**变 ?，
        /// 提前暴露而不是入包时悄悄丢）；原样档 = UTF-8 无 BOM（用户新输入的内容没有"原编码"可言，
        /// UTF-8 对外部编辑器友好且对输入无损）。</para>
        /// <para>读侧一律走 <see cref="ReadTextFile(string)"/> 自动检测，故两侧始终自洽。</para>
        /// </summary>
        public static Encoding TempWriteEncoding => PackTarget ?? TextFileHelper.Utf8NoBomTarget;

        /// <summary>
        /// 结果编码：**打包**时要写进包的编码。
        /// <para><paramref name="sourceEncoding"/> 是"这一份内容的来源编码"（源文件 / 临时文件检测所得）。</para>
        /// <para>原样档 → 用它（零转码）；统一档 → 用目标编码。</para>
        /// </summary>
        public static Encoding ResolveWriteEncoding(Encoding sourceEncoding)
            => PackTarget ?? sourceEncoding;
    }
}
