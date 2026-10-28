using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Linq;
using System.Collections.Concurrent;
using K4os.Compression.LZ4;

namespace GDPackEditor
{
    /// <summary>单个"本机码页表示不了"的名字（含首个坏字符与其码位）
    public sealed class ArcNameCheckItem
    {
        public readonly string RelativePath;
        public readonly char BadChar;
        public readonly int BadCodePoint;

        public ArcNameCheckItem(string relativePath, char badChar, int badCodePoint)
        {
            RelativePath = relativePath;
            BadChar = badChar;
            BadCodePoint = badCodePoint;
        }

        /// <summary>坏字符展示文本：非 BMP 时用完整码位取字符，避免打印成代理对</summary>
        public string BadCharText
        {
            get
            {
                if (BadCodePoint > 0xFFFF)
                {
                    try { return char.ConvertFromUtf32(BadCodePoint); } catch { }
                }
                return BadChar.ToString();
            }
        }

        public string CodePointText => "U+" + BadCodePoint.ToString("X4");

        /// <summary>形如：a/b.txt（字符 'ệ' U+1EC7）</summary>
        public string Describe()
        {
            return string.Format(
                LanguageManager.Instance.GetText("ArcName.BadCharItem", "{0}（字符 '{1}' {2}）"),
                RelativePath, BadCharText, CodePointText);
        }
    }

    /// <summary>
    /// 打包前的文件名编码检查结果。三类名字各自全量留档，日志按类分别汇总：
    /// 全 ASCII = Info；非 ASCII 但本机码页能表示 = Warn（照常入包）；
    /// 「忽略非ASCII文件名」开启时改为 PolicySkipped（跳过不入包）；
    /// 本机码页表示不了 = BadItems（跳过不入包）。
    /// </summary>
    public sealed class ArcNameCheckReport
    {
        public int Total;

        /// <summary>非 ASCII 名的总数（含被跳过的）</summary>
        public int NonAsciiCount;

        /// <summary>非 ASCII 但本机码页能表示的名字（全量）</summary>
        public readonly List<string> NonAsciiOk = new List<string>();

        /// <summary>按「忽略非ASCII文件名」设置跳过的名字（全量）</summary>
        public readonly List<string> PolicySkipped = new List<string>();

        /// <summary>本机码页表示不了、必须跳过的名字（全量）</summary>
        public readonly List<ArcNameCheckItem> BadItems = new List<ArcNameCheckItem>();

        /// <summary>面向用户的编码名（如 "GBK（CP936）"）</summary>
        public string AcpFriendlyName => NameCodec.AcpFriendlyName;
    }

    /// <summary>
    /// GDPackEditor 的统一API接口
    /// </summary>
    public static class GDPackEditorAPI
    {
        /// <summary>
        /// 解包ARC文件
        /// </summary>
        /// <param name="inputFile">输入的ARC文件路径</param>
        /// <param name="outputDirectory">输出目录路径</param>
        /// <param name="progress">进度回调接口</param>
        /// <returns>操作是否成功</returns>
        public static async Task<bool> UnpackArcAsync(string inputFile, string outputDirectory, IProgressCallback progress = null, System.Threading.CancellationToken ct = default)
        {
            return await Task.Run(() => UnpackArc(inputFile, outputDirectory, progress, ct), ct);
        }

        public static bool UnpackArc(string inputFile, string outputDirectory, IProgressCallback progress = null, System.Threading.CancellationToken ct = default)
        {
            DateTime startTime = DateTime.Now;
            try
            {
                progress?.Report(0, LanguageManager.Instance.GetText("LOG.StartUnpacking", "开始解包"));

                if (!File.Exists(inputFile))
                {
                    progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.InputFileNotFound", "输入文件不存在：")} {inputFile}");
                    return false;
                }

                using (var arcStream = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    ARCFile archive = new ARCFile();
                    if (progress != null) archive.NameReporter = progress.ReportLog;   // 读侧编码判定结论写入本次操作日志
                    archive.ReadStream(arcStream);

                    if (string.IsNullOrEmpty(outputDirectory))
                    {
                        outputDirectory = Path.Combine(
                            Path.GetDirectoryName(inputFile),
                            Path.GetFileNameWithoutExtension(inputFile)
                        );
                    }

                    if (!Directory.Exists(outputDirectory))
                        Directory.CreateDirectory(outputDirectory);

                    // 里程碑信息：包名+大小、输出目录（条目数见下一行"正在解包 N"）
                    progress?.Report(0, string.Format(
                        LanguageManager.Instance.GetText("LOG.PackageInfo", "包：{0}（{1}）"),
                        inputFile, LogService.FileSizeText(inputFile)));
                    progress?.Report(0, string.Format(
                        LanguageManager.Instance.GetText("LOG.OutputDir", "输出目录：{0}"), outputDirectory));

                    progress?.Report(0, $"{LanguageManager.Instance.GetText("LOG.Unpacking", "正在解包")} {archive.toc.Count}");

                    // 收集有效条目信息
                    var entries = new List<(ARCTocEntry entry, string name)>();
                    for (int i = 0; i < archive.toc.Count; i++)
                    {
                        var entry = archive.toc[i];
                        string name = entry.GetEntryString(archive.strs);
                        if (string.IsNullOrEmpty(name)) continue;
                        entries.Add((entry, name));
                    }

                    if (entries.Count == 0)
                    {
                        progress?.ReportWarning(LanguageManager.Instance.GetText("LOG.NoValidEntry", "没有找到有效条目"));
                        return true;
                    }

                    // 顺序解压（流式读取源文件，避免整包载入内存导致大 ARC 卡顿/内存暴涨）
                    // 【内容解包】设置：原样档 = 全程字节直搬（零成本）；统一档才对**文本文件**转码
                    bool convertUnpackText = EncodingService.UnpackNeedsConvert;
                    if (convertUnpackText)
                    {
                        string targetName = string.Equals(SettingsManager.ContentUnpackMode,
                            SettingsManager.ContentModeAnsi, StringComparison.OrdinalIgnoreCase)
                            ? NameCodec.AcpFriendlyName : "UTF-8";
                        progress?.Report(0, string.Format(
                            LanguageManager.Instance.GetText("LOG.UnpackTextTarget", "解出文本文件统一编码：{0}"), targetName));
                    }
                    int done = 0;
                    int skipCount = 0;
                    foreach (var item in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var (entry, entryName) = item;
                            // 包内路径安全化：绝对路径/`..` 一律不允许逃出输出目录（官方包确实会存绝对路径）
                            string safeRel = ResolveUnpackPath(entryName, progress);
                            if (safeRel == null)
                            {
                                skipCount++;
                                continue;
                            }
                            string filename = Path.Combine(outputDirectory, safeRel.Replace('/', Path.DirectorySeparatorChar));
                            string dir = Path.GetDirectoryName(filename);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                Directory.CreateDirectory(dir);

                            using (FileStream output = new FileStream(filename, FileMode.Create))
                            {
                                if (convertUnpackText && TextFileHelper.IsTextFile(entryName))
                                {
                                    // 统一档：先解到内存，按设置转码后再落盘
                                    using (var ms = new MemoryStream())
                                    {
                                        archive.UnpackToStream(entry, ms);
                                        byte[] srcBytes = ms.ToArray();
                                        var outcome = EncodingService.NormalizeForUnpack(srcBytes, out byte[] dst, out _);
                                        byte[] write = outcome == TextFileHelper.NormalizeOutcome.Skipped ? srcBytes : dst;
                                        output.Write(write, 0, write.Length);
                                    }
                                }
                                else
                                {
                                    archive.UnpackToStream(entry, output);
                                }
                            }

                            File.SetLastWriteTime(filename, entry.FileTime);
                        }
                        catch (Exception ex)
                        {
                            skipCount++;
                            progress?.ReportError(string.Format(
                                LanguageManager.Instance.GetText("LOG.UnpackItemFailed", "解包失败: {0}, {1}"),
                                item.name, ex.Message));
                        }
                        finally
                        {
                            done++;
                            int pct = (int)((double)done / entries.Count * 100);
                            progress?.Report(pct, $"{LanguageManager.Instance.GetText("LOG.Unpacking", "正在解包")}: ({done}/{entries.Count}) {item.name}");
                        }
                    }

                    if (skipCount > 0)
                        progress?.ReportWarning($"{LanguageManager.Instance.GetText("LOG.SkippedFileCount", "共跳过")} {skipCount} {LanguageManager.Instance.GetText("LOG.FilesDueToError", "个文件")}");
                }

                TimeSpan duration = DateTime.Now - startTime;
                progress?.Report(100, $"{LanguageManager.Instance.GetText("LOG.UnpackingCompleteUseTime", "解包完成，耗时:")} {duration.TotalSeconds:F2}s");
                return true;
            }
            catch (OperationCanceledException)
            {
                progress?.ReportLog(LanguageManager.Instance.GetText("LOG.OperationTerminated", "操作已终止"));
                return false;
            }
            catch (Exception ex)
            {
                TimeSpan duration = DateTime.Now - startTime;
                progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.UnpackingFailedUseTime", "解包失败，耗时:")} {duration.TotalSeconds:F2}s , {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 打包ARC文件
        /// </summary>
        /// <param name="inputDirectory">输入目录路径</param>
        /// <param name="outputFile">输出的ARC文件路径</param>
        /// <param name="progress">进度回调接口</param>
        /// <returns>操作是否成功</returns>
        public static async Task<bool> PackArcAsync(string inputDirectory, string outputFile, IProgressCallback progress = null, System.Threading.CancellationToken ct = default)
        {
            return await Task.Run(() => PackArc(inputDirectory, outputFile, progress, ct), ct);
        }

        public static bool PackArc(string inputDirectory, string outputFile, IProgressCallback progress = null, System.Threading.CancellationToken ct = default)
        {
            DateTime startTime = DateTime.Now;
            // 输出走「同目录临时包 → 原子替换」：直接写目标包会在取消/异常/替换失败时
            // 把用户的原有包截断或删掉（见 SessionPackageWriter 说明）。null = 尚未建立临时包
            string tempFile = null;
            try
            {
                progress?.Report(0, $"{LanguageManager.Instance.GetText("LOG.StartPacking", "开始打包")}...");

                // 里程碑信息：源目录、输出包（文件数见下方"N 个文件已找到"行）
                progress?.Report(0, string.Format(
                    LanguageManager.Instance.GetText("LOG.SourceDir", "源目录：{0}"), inputDirectory));
                progress?.Report(0, string.Format(
                    LanguageManager.Instance.GetText("LOG.OutputPkg", "输出包：{0}"), outputFile));
                progress?.Report(0, string.Format(
                    LanguageManager.Instance.GetText("ArcName.EncodingInfo", "名字表编码：{0}"), NameCodec.AcpName));

                if (!Directory.Exists(inputDirectory))
                {
                    //progress?.ReportError($"输入目录不存在: {inputDirectory}");
                    progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.InputDirectoryNotFound", "输入目录不存在")}: {inputDirectory}");

                    return false;
                }

                // 自动生成输出文件名
                if (string.IsNullOrEmpty(outputFile))
                {
                    string cleanDir = inputDirectory.TrimEnd('\\', '/');
                    outputFile = Path.Combine(
                        Path.GetDirectoryName(cleanDir) ?? cleanDir,
                        Path.GetFileName(cleanDir) + ".arc"
                    );
                }

                // 确保输出文件的目录存在
                string outputDir = Path.GetDirectoryName(outputFile);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                // 扫描文件
                //progress?.Report(10, "正在扫描文件...");

                progress?.Report(0, $"{LanguageManager.Instance.GetText("LOG.ScanningFiles", "正在扫描文件")}...");

                // 【顺序确定化】Directory.GetFiles 的返回顺序**未定义**（NTFS 上通常接近字母序，但不保证）。
                // 按**包内相对路径 ordinal 升序**显式重排：写包顺序确定、可复现，
                // 且与官方"新建包"的条目顺序一致（实测官方 Conversations/UI/Items.arc 均为严格升序）。
                string[] allFiles = Directory.GetFiles(inputDirectory, "*", SearchOption.AllDirectories)
                    .OrderBy(p => MakeArcRelativePath(inputDirectory, p), StringComparer.Ordinal)
                    .ToArray();
                progress?.Report(0, $"{allFiles.Length} {LanguageManager.Instance.GetText("LOG.FoundFilesAndPack", "个文件已找到，开始打包...")}");

                
                // 转码统计（只统计文本文件的转码结果；汇总行始终输出）
                bool normalizeArcText = SettingsManager.ShouldNormalizeArcText;
                int normConverted = 0, normUnchanged = 0;
                // 实际写入包内的文件数（名字不合规被跳过的文件不计入）
                int packedCount = 0;

                // 文件名编码检查（写侧：不可表示的名字跳过，不写 ? 坏名）
                var nameReport = new ArcNameCheckReport();
                // 设置项「过滤非ASCII文件名」：开启时含非 ASCII 的名字一律不入包
                // （作用范围 = ARC 文件名 + ARZ 记录名；不再逐次弹窗，结束后统一汇总告警）
                bool skipNonAsciiNames = EncodingService.NameFilterEnabled;

                tempFile = SessionPackageWriter.CreateTempPackagePath(outputFile);
                using (FileStream fs = new FileStream(tempFile, FileMode.Create))
                using (ARCWriter awriter = new ARCWriter(fs))
                {
                    for (int i = 0; i < allFiles.Length; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string afile = allFiles[i];
                        DateTime afiletime = File.GetLastWriteTime(afile);
                        string relativePath = MakeArcRelativePath(inputDirectory, afile);

                        // 名字检查与文本编码转换都先做完，结论拼进同一行进度：
                        // 每个文件只占一行，避免"文本编码转换"明细行把日志刷爆
                        string note = null;
                        byte[] packedBytes = null;

                        NameCheckResult nameCheck = AccumulateNameCheck(
                            nameReport, relativePath, skipNonAsciiNames, out _);
                        if (nameCheck != NameCheckResult.Ok)
                        {
                            note = nameCheck == NameCheckResult.PolicySkipped
                                ? LanguageManager.Instance.GetText("ArcName.SkipNote", "（跳过：文件名带非ASCII字符）")
                                : LanguageManager.Instance.GetText("ArcName.SkipNoteBad", "（跳过：无法识别文件名）");
                        }
                        else if (normalizeArcText && TextFileHelper.IsTextFile(relativePath))
                        {
                            // 文本文件：按设置规范化为 UTF-8 无 BOM 后打包（非文本文件仍走原始字节流）
                            packedBytes = GetPackedTextBytes(afile, out string packReason, out var packOutcome);
                            switch (packOutcome)
                            {
                                case TextFileHelper.NormalizeOutcome.Converted:
                                    normConverted++;
                                    // 加「转码：」前缀（多语言），便于在日志面板里输入"转码"过滤出所有转码过的文件
                                    note = $"（{LanguageManager.Instance.GetText("LOG.TranscodePrefix", "转码：")}{packReason}）";
                                    break;
                                case TextFileHelper.NormalizeOutcome.Skipped:
                                    // 不计数：这类文件按原始字节入包，且已逐条给出警告行
                                    // （转码统计行不再出现"跳过"字样，避免与"跳过非ASCII文件名"混淆）
                                    progress?.ReportWarning(string.Format(
                                        LanguageManager.Instance.GetText("LOG.SkipTextTranscode", "跳过文本编码转换: {0}（{1}）"),
                                        relativePath, packReason));
                                    break;
                                default:
                                    normUnchanged++;
                                    break;
                            }
                        }

                        // 报告进度
                        int percentage = (int)((double)(i + 1) / allFiles.Length * 100);
                        progress?.Report(percentage, $"{LanguageManager.Instance.GetText("LOG.Packing", "正在打包")}: ({i + 1}/{allFiles.Length}) {relativePath}{note}");

                        if (nameCheck != NameCheckResult.Ok)
                            continue;

                        if (packedBytes != null)
                        {
                            using (var ms = new MemoryStream(packedBytes, false))
                                awriter.WriteFromStream(relativePath, afiletime, ms);
                        }
                        else
                        {
                            using (FileStream ifs = new FileStream(afile, FileMode.Open))
                            {
                                ifs.Seek(0, SeekOrigin.Begin);
                                awriter.WriteFromStream(relativePath, afiletime, ifs);
                            }
                        }
                        packedCount++;
                    }
                }

                // 包已完整构建 → 同卷原子替换目标包；替换失败保留临时包（不销毁成果），目标包保持原样
                try
                {
                    SessionPackageWriter.ReplaceWithRetry(tempFile, outputFile);
                    tempFile = null;
                }
                catch (Exception ex)
                {
                    // 写盘类失败（含替换失败）→ 四行式提示；其它 → 原消息
                    string prompt = WriteFailureDiagnosis.Prompt(ex, outputFile, tempFile);
                    progress?.ReportError(prompt.Length > 0 ? prompt : ex.Message);
                    return false;
                }

                // 转码统计：只关于"文本文件转码"，故不含跳过数字（跳过/存不了的文件见下一行的打包统计）
                if (normalizeArcText)
                    progress?.ReportLog(string.Format(
                        LanguageManager.Instance.GetText("LOG.TranscodeStat", "转码统计: 转换 {0} 个, 已是 UTF-8 {1} 个"),
                        normConverted, normUnchanged));

                // 打包统计：文件总数与实际写入数（含被跳过的非 ASCII 文件统计）
                int skippedTotal = nameReport.PolicySkipped.Count + nameReport.BadItems.Count;
                progress?.ReportLog(skippedTotal > 0
                    ? string.Format(
                        LanguageManager.Instance.GetText("LOG.PackStatWithSkip", "打包统计: 找到 {0} 个文件，写入 {1} 个，跳过 {2} 个"),
                        allFiles.Length, packedCount, skippedTotal)
                    : string.Format(
                        LanguageManager.Instance.GetText("LOG.PackStat", "打包统计: 找到 {0} 个文件，写入 {1} 个"),
                        allFiles.Length, packedCount));

                TimeSpan duration = DateTime.Now - startTime;
                progress?.Report(100, $"{LanguageManager.Instance.GetText("LOG.PackingCompleteUseTime", "打包完成，耗时:")} {duration.TotalSeconds:F2}s");

                // 成功后补充生成包大小（失败/取消不产生该行；文件不存在时只报路径）
                string outPkgSize = LogService.FileSizeText(outputFile);
                progress?.Report(100, string.Format(
                    LanguageManager.Instance.GetText("LOG.OutputPkg", "输出包：{0}"),
                    outputFile + (string.IsNullOrEmpty(outPkgSize) ? "" : $"（{outPkgSize}）")));

                // 文件名编码检查结论：固定排在末尾（详情之后）
                EmitArcNameCheckLog(progress, nameReport);

                return true;
            }
            catch (OperationCanceledException)
            {
                // 只删半成品临时包；**绝不碰目标包**（取消打包不应毁掉用户的原有包）
                try { if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                progress?.ReportLog(LanguageManager.Instance.GetText("LOG.OperationTerminated", "操作已终止"));
                return false;
            }
            catch (Exception ex)
            {
                TimeSpan duration = DateTime.Now - startTime;
                // 写盘类失败 → 输出四行式提示（目标文件/错误码/原因/暂存）；其它失败 → 原消息
                string prompt = WriteFailureDiagnosis.Prompt(ex, outputFile, tempFile);
                progress?.ReportError(prompt.Length > 0 ? prompt
                    : $"{LanguageManager.Instance.GetText("LOG.PackingFailedUseTime", "打包失败，耗时:")} {duration.TotalSeconds:F2}s , {ex.Message}");

                return false;
            }
        }

        /// <summary>
        /// 解包ARZ文件
        /// </summary>
        /// <param name="inputFile">输入的ARZ文件路径</param>
        /// <param name="outputDirectory">输出目录路径</param>
        /// <param name="progress">进度回调接口</param>
        /// <returns>操作是否成功</returns>
        public static async Task<bool> UnpackArzAsync(string inputFile, string outputDirectory, IProgressCallback progress = null, System.Threading.CancellationToken ct = default)
        {
            return await Task.Run(() => UnpackArz(inputFile, outputDirectory, progress, ct), ct);
        }

        public static bool UnpackArz(string inputFile, string outputDirectory, IProgressCallback progress = null, System.Threading.CancellationToken ct = default)
        {
            DateTime startTime = DateTime.Now;
            try
            {
                progress?.Report(0, $"{LanguageManager.Instance.GetText("LOG.StartUnpacking", "开始解包")}...");

                if (!File.Exists(inputFile))
                {
                    progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.InputFileNotFound", "输入文件不存在")}: {inputFile}");
                    return false;
                }

                // 预解析ARZ结构 + 全程共用一个 FileStream：不再整包 ReadAllBytes 驻留内存，
                // 记录数据按需 seek 读取（大包内存峰值大幅下降）
                int totalCount;
                FileStream archiveFs = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                ARZReader arzr;
                try
                {
                    arzr = new ARZReader(archiveFs);
                    totalCount = arzr.Count;
                }
                catch
                {
                    archiveFs.Dispose();
                    throw;
                }
                try
                {
                // 输出目录准备
                if (string.IsNullOrEmpty(outputDirectory))
                    outputDirectory = Path.GetDirectoryName(inputFile);
                if (!Directory.Exists(outputDirectory))
                    Directory.CreateDirectory(outputDirectory);

                // 与解包ARC一致：预解析后显示包内记录数量
                // 里程碑信息：包名+大小、输出目录（条目数见下一行"正在解包 N"）
                progress?.Report(0, string.Format(
                    LanguageManager.Instance.GetText("LOG.PackageInfo", "包：{0}（{1}）"),
                    inputFile, LogService.FileSizeText(inputFile)));
                progress?.Report(0, string.Format(
                    LanguageManager.Instance.GetText("LOG.OutputDir", "输出目录：{0}"), outputDirectory));

                progress?.Report(0, $"{LanguageManager.Instance.GetText("LOG.Unpacking", "正在解包")} {totalCount}");

                // 生产者-消费者：单线程解析条目并序列化为文本字节入队，消费者多线程写盘
                // 队列传已序列化的 .dbr 文本字节而非对象树：对象树解析后立即丢弃，
                // 避免无限缓存导致内存暴涨与 GC 全量回收暂停；消费者只写盘不做任何转换
                // ⚠️ 队列积压必须同时按**字节量**门控：只限条数挡不住大记录（单条解压后可达几十 MB），
                //    200 条积压可能达数 GB —— 这是解包内存峰值/卡死的主因。此处限制在途字节上限。
                //    注：不能用 SemaphoreSlim.Wait(n)——它没有"按数量申请"的重载（会被解释为毫秒超时），
                //    这里用 Monitor + 计数器（条件变量）实现按字节门控，单条超上限时允许独占进入（不会死锁）。
                const int MaxQueuedBytes = 64 * 1024 * 1024;   // 在途文本字节上限 64MB（超出则生产者等待写盘）
                object queueGateLock = new object();
                long queuedBytes = 0;
                // dataLen：实际有效长度（GetBuffer 容量通常大于长度，用长度写盘，省一次 ToArray 拷贝）
                var queue = new System.Collections.Concurrent.BlockingCollection<(string name, byte[] data, int dataLen, DateTime fileTime)>(boundedCapacity: 200);
                int done = 0;
                int totalValid = 0;
                object progressLock = new object();

                // 先扫出有效记录索引，避免生产者运行时再去 GetRecordInfo（非线程安全）
                // 扫描阶段进度：0→5%，与消费者阶段（5→100%）无缝衔接，全程单调递增
                var validIndices = new List<int>();
                {
                    int scanInterval = Math.Max(1, totalCount / 200);
                    for (int i = 0; i < totalCount; i++)
                    {
                        var info = arzr.GetRecordInfo(i);
                        if (info == null) continue;
                        string name = arzr.GetRecordName(i);
                        if (string.IsNullOrEmpty(name)) continue;
                        validIndices.Add(i);
                        if ((i + 1) % scanInterval == 0 || i == totalCount - 1)
                            progress?.UpdateProgress((int)((double)(i + 1) / totalCount * 5), $"{LanguageManager.Instance.GetText("LOG.Unpacking", "正在解包")}: 扫描记录 {i + 1}/{totalCount}");
                    }
                }
                totalValid = validIndices.Count;
                if (totalValid == 0)
                {
                    progress?.ReportError(LanguageManager.Instance.GetText("LOG.NoValidRecord", "没有找到有效记录"));
                    return false;
                }

                // 【内容解包】设置：原样档（null）= 按包内原始编码逐字节还原（官方行为，零成本）；
                // 统一档 = 整条记录按目标编码重写
                Encoding arzUnpackTarget = EncodingService.UnpackTarget;
                if (arzUnpackTarget != null)
                {
                    string targetName = string.Equals(SettingsManager.ContentUnpackMode,
                        SettingsManager.ContentModeAnsi, StringComparison.OrdinalIgnoreCase)
                        ? NameCodec.AcpFriendlyName : "UTF-8";
                    progress?.Report(0, string.Format(
                        LanguageManager.Instance.GetText("LOG.UnpackTextTarget", "解出文本文件统一编码：{0}"), targetName));
                }

                // 统一内容检测回调：换行=已自动删除；逗号/控制字符=原样保留。
                // 逐条上报（GUI 日志框 / CLI 控制台均可见）+ 分类计数，结束后按类汇总数量
                int newlineFixCount = 0, commaIssueCount = 0, controlIssueCount = 0;
                Action<string, string> onContentIssue = (kind, loc) =>
                {
                    switch (kind)
                    {
                        case DbrContentCheck.KindNewline: System.Threading.Interlocked.Increment(ref newlineFixCount); break;
                        case DbrContentCheck.KindComma: System.Threading.Interlocked.Increment(ref commaIssueCount); break;
                        case DbrContentCheck.KindControlChar: System.Threading.Interlocked.Increment(ref controlIssueCount); break;
                    }
                    progress?.ReportWarning(loc);
                };

                // 生产者：单线程顺序解析记录，解析完立即序列化为 .dbr 文本字节入队，对象树用完即弃
                long bigRecordSince = 0;   // 自上次 LOH 整理以来处理的大记录数（大记录会占用 LOH，及时整理控峰值）
                Task producer = Task.Run(() =>
                {
                    try
                    {
                        foreach (int idx in validIndices)
                        {
                            ct.ThrowIfCancellationRequested();
                            // 无缓存读取重载（不写入 loadedRecords），避免内存无限增长
                            ARZRecord record = arzr.GetRecord(idx, archiveFs);
                            string name = arzr.GetRecordName(idx);
                            if (string.IsNullOrEmpty(name))
                            {
                                record?.DiscardData();
                                continue;
                            }

                            try
                            {
                                byte[] data;
                                int dataLen;
                                var ms = new MemoryStream();
                                try
                                {
                                    if (arzUnpackTarget == null)
                                        // 【原样档】按包内每项的原始编码逐字节还原（零转码），
                                        // 与官方工具"字段值字节原样落盘"一致；恒 UTF-8 会把包内 GBK 值写坏
                                        record.SaveToStreamPreservingEncodings(ms, onContentIssue);
                                    else
                                        // 【统一档】整条记录按目标编码写出（值先按用途判定解码，再用目标编码重写）
                                        record.SaveToStream(ms, arzUnpackTarget, onContentIssue);
                                    // 用 GetBuffer + 实际长度：不再 ToArray 复制一份（大记录下省下的瞬时内存可观）
                                    data = ms.GetBuffer();
                                    dataLen = (int)ms.Length;
                                }
                                catch
                                {
                                    ms.Dispose();
                                    throw;
                                }
                                finally
                                {
                                    // 记录数据用完立即断开引用，让大数组尽早可回收
                                    // （DiscardData 只清 entries/cData，解压块 aData 需显式断开）
                                    record.DiscardData();
                                    record.aData = null;
                                }
                                // 按字节量申请在途额度（条件变量；单条超上限时允许独占进入，避免死锁）
                                lock (queueGateLock)
                                {
                                    while (queuedBytes >= MaxQueuedBytes && !ct.IsCancellationRequested)
                                        Monitor.Wait(queueGateLock, 100);   // 100ms 轮询兜底，避免漏唤醒
                                    ct.ThrowIfCancellationRequested();
                                    queuedBytes += dataLen;
                                }
                                try { queue.Add((name, data, dataLen, record.rdFileTime), ct); }
                                catch
                                {
                                    lock (queueGateLock) { queuedBytes -= dataLen; Monitor.PulseAll(queueGateLock); }
                                    throw;
                                }

                                // 大记录（≥4MB）落在 LOH 上，而 LOH 默认不压缩回收 → 工作集居高不下。
                                // 每积累若干条主动触发一次非阻塞 LOH 压缩回收，压住解包大包时的内存峰值。
                                if (dataLen >= 4 * 1024 * 1024 && ++bigRecordSince >= 8)
                                {
                                    bigRecordSince = 0;
                                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                                        System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                                    GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
                                }
                            }
                            catch (Exception ex)
                            {
                                lock (progressLock)
                                    progress?.ReportWarning(string.Format(
                                        LanguageManager.Instance.GetText("LOG.UnpackItemFailed", "解包失败: {0}, {1}"),
                                        name, ex.Message));
                            }
                        }
                    }
                    finally
                    {
                        queue.CompleteAdding();
                    }
                });

                // 消费者：从队列取记录，写盘。线程数由用户选择：
                // 单线程（默认）：机械硬盘顺序写小文件避免寻道抖动，往往更快；
                // 多线程：充分利用 SSD 并发能力。多线程时取核数一半（2~6 之间），
                // 过高写盘并发会加剧文件系统元数据竞争与缓存刷盘压力。
                int consumerCount = SettingsManager.UnpackSingleThread ? 1 : Math.Max(2, Math.Min(6, Environment.ProcessorCount / 2));
                Task[] consumers = Enumerable.Range(0, consumerCount).Select(_ => Task.Run(() =>
                {
                    foreach (var item in queue.GetConsumingEnumerable())
                    {
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            // 包内路径安全化：绝对路径/`..` 一律不允许逃出输出目录
                            // （不用 continue：下面还有进度记账，跳过去会让进度永远到不了 100%）
                            string safeRel = ResolveUnpackPath(item.name, progress);
                            if (safeRel != null)
                            {
                                string filename = Path.Combine(outputDirectory, safeRel.Replace('/', Path.DirectorySeparatorChar));
                                string dir = Path.GetDirectoryName(filename);
                                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                    Directory.CreateDirectory(dir);

                                // 统一写盘入口（公共化）：建文件 → 写 → 恢复包内时间戳；小文件用最小缓冲（原为默认 4KB）
                                FileWriteHelper.WriteAllBytesWithTime(filename, item.data, item.dataLen, item.fileTime);
                            }

                            int current = Interlocked.Increment(ref done);
                            int pct = 5 + (int)((double)current / totalValid * 95);
                            lock (progressLock)
                                progress?.Report(pct, $"{LanguageManager.Instance.GetText("LOG.Unpacking", "正在解包")}: ({current}/{totalValid}) {item.name}");
                        }
                        catch (OperationCanceledException)
                        {
                            // 取消：归还额度后由外层统一处理（不能让 finally 漏掉）
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lock (progressLock)
                                progress?.ReportWarning(string.Format(
                                    LanguageManager.Instance.GetText("LOG.UnpackItemFailed", "解包失败: {0}, {1}"),
                                    item.name, ex.Message));
                        }
                        finally
                        {
                            // 写盘完成（或异常）：归还该条的字节额度，唤醒可能等待的生产者
                            lock (queueGateLock)
                            {
                                queuedBytes -= item.dataLen;
                                Monitor.PulseAll(queueGateLock);
                            }
                        }
                    }
                })).ToArray();

                try
                {
                    Task.WaitAll(new[] { producer }.Concat(consumers).ToArray());
                }
                catch (AggregateException)
                {
                    // 生产者/消费者可能因取消抛出异常，统一在下方按取消状态处理
                }

                if (ct.IsCancellationRequested)
                {
                    progress?.ReportLog(LanguageManager.Instance.GetText("LOG.OperationTerminated", "操作已终止"));
                    return false;
                }

                // 内容检测分类汇总：**只报数量**（逐条明细已在上方日志，可筛选或查日志文件，避免双份显示）
                if (newlineFixCount > 0)
                    progress?.ReportWarning(string.Format(
                        LanguageManager.Instance.GetText("LOG.UnpackNewlineFixSummary",
                            "解包共有 {0} 处字段值含换行，已删除换行（逐条明细见上方日志）"),
                        newlineFixCount));
                if (commaIssueCount > 0)
                    progress?.ReportWarning(string.Format(
                        LanguageManager.Instance.GetText("LOG.UnpackCommaSummary",
                            "解包共有 {0} 处字段值含逗号，已按原样保留（逐条明细见上方日志）"),
                        commaIssueCount));
                if (controlIssueCount > 0)
                    progress?.ReportWarning(string.Format(
                        LanguageManager.Instance.GetText("LOG.UnpackControlCharSummary",
                            "解包共有 {0} 处字段值含控制字符，已按原样保留（逐条明细见上方日志）"),
                        controlIssueCount));

                TimeSpan duration = DateTime.Now - startTime;
                progress?.Report(100, $"{LanguageManager.Instance.GetText("LOG.UnpackingCompleteUseTime", "解包完成，耗时")}:{duration.TotalSeconds:F2}s");
                return true;
                }
                finally
                {
                    // 包文件流全程贯穿（扫描/生产者），流程结束统一关闭
                    archiveFs.Dispose();
                }
            }
            catch (Exception ex)
            {
                TimeSpan duration = DateTime.Now - startTime;
                progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.UnpackingFailedUseTime", "解包失败，耗时")}:{duration.TotalSeconds:F2}s , {ex.Message}");
                return false;
            }
        }

        
        
        /// <summary>
        /// 构建ARZ文件（仅打包指定子目录）
        /// </summary>
        /// <param name="modPath">Mod源码目录路径</param>
        /// <param name="buildPath">构建输出目录路径</param>
        /// <param name="gameFolder">游戏根目录路径</param>
        /// <param name="selectedSubDirs">用户选择的子目录列表</param>
        /// <param name="progress">进度回调接口</param>
        /// <returns>操作是否成功</returns>
        public static async Task<bool> BuildArzWithSelectedDirsAsync(string modPath, string buildPath, string gameFolder, 
                                                                     List<string> selectedSubDirs,
IProgressCallback progress = null,
                                                                      string outputFile = null,
                                                                      System.Threading.CancellationToken ct = default,
                                                                      List<TemplateEntry> templateEntries = null)
        {
            return await Task.Run(() => BuildArzWithSelectedDirs(modPath, buildPath, gameFolder, selectedSubDirs, progress, outputFile, ct, templateEntries), ct);
        }

        public static bool BuildArzWithSelectedDirs(string modPath, string buildPath, string gameFolder, 
                                           List<string> selectedSubDirs,
IProgressCallback progress = null,
                                            string outputFile = null,
                                            System.Threading.CancellationToken ct = default,
                                            List<TemplateEntry> templateEntries = null)
        {
            DateTime startTime = DateTime.Now;
            // 输出走「同目录临时包 → 原子替换」（同 PackArc）：直接写目标包会在取消/异常时毁掉用户的原有包
            string tempFile = null;
            FileStream packFs = null;   // 直写目标流（临时包），随流程结束统一关闭
            try
            {
                progress?.Report(0, $"{LanguageManager.Instance.GetText("LOG.Packing", "正在打包")}...");

                // 里程碑信息：源目录、输出包
                progress?.Report(0, string.Format(
                    LanguageManager.Instance.GetText("LOG.SourceDir", "源目录：{0}"), modPath));
                progress?.Report(0, string.Format(
                    LanguageManager.Instance.GetText("LOG.OutputPkg", "输出包：{0}"), outputFile));

                if (!Directory.Exists(modPath))
                {
                    progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.ModPathNotExist", "Mod路径不存在")}: {modPath}");
                    return false;
                }

                // 游戏目录为空时跳过检查，允许打包
                if (!string.IsNullOrEmpty(gameFolder) && !File.Exists(Path.Combine(gameFolder, "Grim Dawn.exe")))
                {
                    progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.GamePathNotRight", "游戏路径不正确")}: {gameFolder}");
                    return false;
                }

                // 收集用户选择的子目录下的所有dbr文件
                List<string> selectedDbrFiles = new List<string>();
                string databaseDir = Path.Combine(modPath, "database");
                
                // 检查selectedSubDirs是否有效
                if (selectedSubDirs == null || selectedSubDirs.Count == 0)
                {
                    progress?.ReportWarning($"{LanguageManager.Instance.GetText("LOG.MessageBoxNoDirSelected", "未选择子目录")}");
                }
                
                if (Directory.Exists(databaseDir))
                {
                    
                    if (selectedSubDirs == null || selectedSubDirs.Count == 0)
                    {
                        progress?.ReportWarning($"{LanguageManager.Instance.GetText("LOG.MessageBoxNoDirSelected", "未选择子目录")}");
                    }
                    else
                    {
                        foreach (string subDir in selectedSubDirs)
                        {
                            string fullSubDirPath = Path.Combine(databaseDir, subDir);
                            if (Directory.Exists(fullSubDirPath))
                            {
                                string[] dbrFiles = Directory.GetFiles(fullSubDirPath, "*.dbr", SearchOption.AllDirectories);
                                selectedDbrFiles.AddRange(dbrFiles);
                                progress?.ReportLog($"{LanguageManager.Instance.GetText("LOG.AddingFilesFromDir", "添加目录中的文件")}: {fullSubDirPath} ({dbrFiles.Length} {LanguageManager.Instance.GetText("LOG.Files", "个文件")})");
                            }
                            else
                            {
                                progress?.ReportWarning($"{LanguageManager.Instance.GetText("LOG.MessageBoxDirNotFound", "目录不存在")}: {fullSubDirPath}");
                            }
                        }
                    }
                }
                else
                {
                    progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.DatabaseDirNotExist", "database目录不存在")}: {databaseDir}");
                    return false;
                }
                

                
                if (selectedDbrFiles.Count == 0)
                {
                    progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.NoFilesToPack", "没有找到要打包的文件")}");
                    return false;
                }

                // 解析模板
                progress?.Report(5, $"{LanguageManager.Instance.GetText("LOG.ParsingTemplates", "解析模板")}...");

                Dictionary<string, TemplateNode> templates;
                List<Program.TemplateSourceCount> templateSources;
                Dictionary<string, string> templateKeySource;
                try
                {
                    (templates, _, _, templateSources, templateKeySource) = Program.LoadTemplates(templateEntries ?? SettingsManager.TemplateEntries);
                }
                catch (Exception e)
                {
                    progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.TemplateParseError", "解析模板时出错")}: {e.Message}");
                    return false;
                }
                // 模板来源汇总：分组显示（内置 / 自定义 / 最终），各来源给**最终生效数**（数字自洽）
                progress?.ReportLog(Program.FormatTemplateSources(templateSources, templates.Count));

                string outputBuildPath = buildPath ?? modPath;
                string modName = Path.GetFileName(modPath.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(outputFile))
                    outputFile = Path.Combine(outputBuildPath, "database", modName + ".arz");

                // 确保输出目录存在
                string outputDir = Path.GetDirectoryName(outputFile);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                // 打包dbr文件 - 并发读取+串行编译写入
                // 记录名编码检查报告：与 ARC 名字表**同一套规则与同一张报告类**（官方：名字都跟本机 ACP），
                // 但政策不同 —— ARZ **一律入包**（见下方 AccumulateNameCheck 的实参说明），只出汇总告警
                var nameReport = new ArcNameCheckReport();
                // 【内容打包】设置：null = 原样档（跟源文件编码，零转码），否则统一到目标编码
                Encoding arzPackTarget = EncodingService.PackTarget;
                // 直写模式：数据区边压边直写同目录临时包（不再内存累积整包数据 → 峰值从"整包"降到"单条记录+块级"）
                ARZWriter arzw = null;
                try
                {
                    tempFile = SessionPackageWriter.CreateTempPackagePath(outputFile);
                    // ⚠️ 必须 ReadWrite：直写模式收尾要重读数据区计算总校验和
                    packFs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite);
                    arzw = new ARZWriter(templates, packFs);
                    // Phase 1: 只收集文件清单（记录名/路径/时间戳）——**不再预读全部文本**。
                    // 全量预读会让所有 dbr 文本在内存里驻留两份（ConcurrentBag + 排序列表），
                    // 是大 MOD 打包内存峰值的大头；改为 Phase 2 边读边压，单文件文本用完即弃（流式）。
                    // 记录名计算是纯字符串操作（不读内容），足够轻，无需并发。
                    var dbrFiles = new List<(string name, string path, DateTime fileTime)>();
                    foreach (var dbrfile in selectedDbrFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            string recordName = Program.DbrFileToRecName(dbrfile, modPath);
                            DateTime ft = File.GetLastWriteTime(dbrfile);
                            dbrFiles.Add((recordName, dbrfile, ft));
                        }
                        catch (Exception e)
                        {
                            progress?.ReportWarning($"{LanguageManager.Instance.GetText("LOG.ErrorPackingFile", "打包文件时出错")}: {dbrfile}, {e.Message}");
                        }
                    }

                    // Phase 2: 串行 读取→检测→压缩写入
                    // 【写包顺序确定化】按**记录名 ordinal 升序**排序后逐个写：
                    //   · 与 ARC 侧"按目录枚举顺序（NTFS 即字母序）写入"的行为对齐；
                    //   · 让打包结果**可复现**（同输入 → 同字节），便于发布与比对。
                    // 注：官方 ARZ 的记录表顺序是其内部容器的顺序（实测非排序、非 rfid、非数据偏移，
                    // 四种常见哈希也不匹配），**无法复现**；此处取"确定且像官方新建包"的排序顺序。
                    List<(string name, string path, DateTime fileTime)> orderedFiles =
                        dbrFiles.OrderBy(x => x.name, StringComparer.Ordinal).ToList();

                    int processedCount = 0;
                    int packedCount = 0;
                    int totalFiles = orderedFiles.Count;
                    // 已改流式：文本在本循环内逐文件读取，不再有"先读取后编译"两个阶段
                    progress?.ReportLog(string.Format(
                        LanguageManager.Instance.GetText("LOG.ReadFilesAndCompile", "已读取 {0} 个文件，开始编译..."), totalFiles));
                    // 名字表编码（写侧 = 本机 ANSI 码页；与 ARC 名字表同规则）
                    progress?.Report(5, string.Format(
                        LanguageManager.Instance.GetText("ArcName.EncodingInfo", "名字表编码：{0}"), NameCodec.AcpName));
                    // 内容编码：统一档提示目标编码；原样档 = 跟随每个源文件自己的编码（零转码）
                    if (arzPackTarget != null)
                    {
                        string packTargetName = string.Equals(SettingsManager.ContentPackMode,
                            SettingsManager.ContentModeAnsi, StringComparison.OrdinalIgnoreCase)
                            ? NameCodec.AcpFriendlyName : "UTF-8";
                        progress?.ReportLog(string.Format(
                            LanguageManager.Instance.GetText("LOG.PackTextTarget", "包内文本统一编码：{0}"), packTargetName));
                    }
                    else
                    {
                        progress?.ReportLog(LanguageManager.Instance.GetText("LOG.PackTextKeepAsIs",
                            "包内文本：原样（跟随每个源文件自己的编码）"));
                    }

                    // 行格式修复/可疑的计数（逐条已上报，末尾只报数量）
                    int packFixTotal = 0, packWarnTotal = 0;
                    int tplMissingSkip = 0;   // 因"模板未找到"整文件不入包的条数（旧实现只报逐条警告、不进统计）

                    foreach (var (recordName, dbrfile, srcFileTime) in orderedFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            // 流式读取当前文件（读出内容的同时记下源文件实际用的编码；
                            // 写包内值时按同一编码 → 零转码）。单文件文本用完即弃，不驻留。
                            string[] recstrings = TemplateHelper.ReadDbrLines(dbrfile, out Encoding srcEncoding);

                            // 记录名走与 ARC 名字表同一套检查，并**同样遵循「过滤非ASCII文件名」设置**：
                            // 开启时含非 ASCII 的记录名一律不写进包（末尾 EmitArcNameCheckLog 汇总，不静默）；
                            // 另外，**本机码页根本表示不了**的名字始终跳过（写进包会变成 '?'，等于毁掉记录）。
                            NameCheckResult nameCheck = AccumulateNameCheck(
                                nameReport, recordName, EncodingService.NameFilterEnabled, out ArcNameCheckItem badItem);
                            string nameNote = null;
                            if (nameCheck == NameCheckResult.PolicySkipped)
                            {
                                nameNote = LanguageManager.Instance.GetText("ArcName.SkipNote", "（跳过：文件名带非ASCII字符）");
                            }
                            else if (nameCheck == NameCheckResult.Unrepresentable)
                            {
                                nameNote = LanguageManager.Instance.GetText("ArcName.SkipNoteBad", "（跳过：无法识别文件名）");
                                ReportSkippedName(progress, badItem);
                            }

                            processedCount++;
                            int percentage = 5 + (int)((double)processedCount / totalFiles * 95);
                            progress?.Report(percentage, $"{LanguageManager.Instance.GetText("LOG.Packing", "正在打包")} ({processedCount}/{totalFiles}) {recordName}{nameNote}");

                            if (nameCheck != NameCheckResult.Ok)
                                continue;   // 不写进包；全量名单由末尾 EmitArcNameCheckLog 汇总，不静默

                            // 【内容打包】设置：统一档 = 用目标编码写值；原样档 = 跟随源文件编码（零转码）
                            // 编码必须在构造记录**之前**压入（它参与字符串表去重键），否则同文不同编码会串味
                            // 构造与写入分离（BuildRecord，打包/保存/合并共用入口）：
                            // 构造阶段才知道"模板未找到"与字段级问题，先上报再决定写入或跳过
                            Encoding writeEnc = arzPackTarget ?? srcEncoding;
                            ARZRecord rec;
                            try { rec = arzw.BuildRecord(recordName, recstrings, srcFileTime, writeEnc); }
                            catch (KeyNotFoundException)
                            {
                                // 模板未找到 → **整个文件不入包**（原有行为）；单独计数，让末尾"找到/写入/跳过"能对上
                                tplMissingSkip++;
                                RecordWarningReporter.ReportTemplateMissing(progress, recordName);
                                continue;
                            }

                            // 字段级日志（公共上报，带 groupKey=记录名 → 日志区按记录折叠分组）
                            var (recFixes, recWarns) = RecordWarningReporter.ReportRecordIssues(progress, recordName, rec);
                            packFixTotal += recFixes;
                            packWarnTotal += recWarns;

                            arzw.WriteRecord(rec);
                            packedCount++;
                        }
                        catch (Exception e)
                        {
                            progress?.ReportWarning($"{LanguageManager.Instance.GetText("LOG.ErrorPackingFile", "打包文件时出错")}: {recordName}, {e.Message}", recordName);
                        }
                    }

                    // 打包统计：记录总数与实际写入数（含被跳过的"本机码页表示不了"的记录名 + "模板未找到"整文件不入包）
                    // ⚠ 模板缺失的那部分**必须计入**，否则"找到 X / 写入 Y / 跳过 Z"里 X−Y ≠ Z，用户会以为文件莫名丢了
                    int skippedTotal = nameReport.PolicySkipped.Count + nameReport.BadItems.Count + tplMissingSkip;
                    progress?.ReportLog(skippedTotal > 0
                        ? string.Format(
                            LanguageManager.Instance.GetText("LOG.PackStatWithSkip", "打包统计: 找到 {0} 个文件，写入 {1} 个，跳过 {2} 个"),
                            totalFiles, packedCount, skippedTotal)
                        : string.Format(
                            LanguageManager.Instance.GetText("LOG.PackStat", "打包统计: 找到 {0} 个文件，写入 {1} 个"),
                            totalFiles, packedCount));

                    // 行格式修复/可疑汇总：**只报数量**（逐条明细已在上方日志，可筛选或查日志文件，避免双份显示）
                    if (packFixTotal > 0 || packWarnTotal > 0)
                        progress?.ReportWarning(string.Format(
                            LanguageManager.Instance.GetText("LOG.PackDataIssueSummary",
                                "本次打包：行格式已修复 {0} 处、可疑 {1} 处（不影响出包，建议核对源文件；逐条明细见上方日志）"),
                            packFixTotal, packWarnTotal));

                    // 模板缺失导致整文件不入包：单独明示（原因 / 影响 / 该怎么办）
                    if (tplMissingSkip > 0)
                        progress?.ReportWarning(string.Format(
                            LanguageManager.Instance.GetText("LOG.PackTemplateMissingSummary",
                                "本次打包：因「模板未找到」跳过了 {0} 个文件（**整个文件未入包**）；请在设置中添加对应自定义模板后重新打包"),
                            tplMissingSkip));

                    // 保护：一条记录都没写进去（例如开启「过滤非ASCII文件名」后名字全不合规）
                    // → 不写空包、不动原文件，直接失败退出（明细已在上面逐条 + 末尾汇总给出）
                    if (packedCount == 0)
                    {
                        progress?.ReportError(LanguageManager.Instance.GetText("LOG.NoRecordWritten",
                            "没有任何记录写入包里（名字全不合规被跳过？），已取消输出，原包未被改动"));
                        packFs.Dispose();
                        packFs = null;
                        try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                        tempFile = null;
                        return false;
                    }
                }
                catch (Exception e)
                {
                    // 该 catch 同时兜底"解析失败"与"临时包创建失败"：只有写盘类才给四行式提示
                    string prompt = WriteFailureDiagnosis.Prompt(e, outputFile, tempFile);
                    progress?.ReportError(prompt.Length > 0 ? prompt
                        : $"{LanguageManager.Instance.GetText("LOG.ErrorWhileParsingRecords", "解析记录时出错，请检查dbr文件与模板是否匹配")}: {e.Message}");
                    return false;
                }

                // 收尾：回填 header + 表/串表/footer，关闭临时包后原子替换目标包
                bool packageBuilt = false;
                try
                {
                    arzw.SaveToStream(packFs);
                    packFs.Dispose();
                    packFs = null;
                    packageBuilt = true;
                    SessionPackageWriter.ReplaceWithRetry(tempFile, outputFile);
                    tempFile = null;
                }
                catch (Exception e)
                {
                    // 写盘类失败（含"替换失败"与"写临时包失败"）→ 统一输出四行式提示
                    string prompt = WriteFailureDiagnosis.Prompt(e, outputFile, tempFile);
                    if (prompt.Length > 0)
                    {
                        progress?.ReportError(prompt);
                    }
                    else if (packageBuilt && !string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                    {
                        // 兜底（诊断块为空时）：保留临时包供抢救，目标包保持原样
                        progress?.ReportError(string.Format(
                            LanguageManager.Instance.GetText("LOG.AtomicReplaceFailed",
                                "输出包替换失败：{0}；本次已完整构建的包保留在：{1}；常见原因：目标正被占用（游戏/其它编辑器/杀毒扫描/网盘同步）、文件或目录只读。可直接重试。"),
                            outputFile, tempFile));
                    }
                    else
                    {
                        progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.ErrorSavingDatabase", "保存数据库时出错")}: {e.Message}");
                    }
                    return false;
                }

                TimeSpan duration = DateTime.Now - startTime;
                progress?.Report(100, $"{LanguageManager.Instance.GetText("LOG.PackingCompleteUseTime", "打包完成，耗时")}: {duration.TotalSeconds:F2}s");

                // 成功后补充生成包大小（失败/取消不产生该行）
                string outPkgSize = LogService.FileSizeText(outputFile);
                progress?.Report(100, string.Format(
                    LanguageManager.Instance.GetText("LOG.OutputPkg", "输出包：{0}"),
                    outputFile + (string.IsNullOrEmpty(outPkgSize) ? "" : $"（{outPkgSize}）")));

                // 记录名编码检查结论：固定排在末尾（详情之后），与 ARC 打包同款
                EmitArcNameCheckLog(progress, nameReport);

                return true;
            }
            catch (OperationCanceledException)
            {
                // 只删半成品临时包；**绝不碰目标包**（取消打包不应毁掉用户的原有包）
                // 先关闭直写流再删（Windows 上打开中的文件无法删除）
                try { packFs?.Dispose(); } catch { }
                packFs = null;
                try { if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                progress?.ReportLog(LanguageManager.Instance.GetText("LOG.OperationTerminated", "操作已终止"));
                return false;
            }
            catch (Exception ex)
            {
                try { packFs?.Dispose(); } catch { }
                packFs = null;
                try { if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                TimeSpan duration = DateTime.Now - startTime;
                string prompt = WriteFailureDiagnosis.Prompt(ex, outputFile, tempFile);
                progress?.ReportError(prompt.Length > 0 ? prompt
                    : $"{LanguageManager.Instance.GetText("LOG.PackingFailedUseTime", "打包失败，耗时")}:{duration.TotalSeconds:F2}s , {ex.Message}");
                return false;
            }
        }

        #region ARZ固实

        /// <summary>
        /// 固实ARZ文件：删除其中的墓碑记录，只保留有效记录
        /// </summary>
        public static async Task<bool> CompactArzAsync(string inputFile, string outputFile, IProgressCallback progress = null, System.Threading.CancellationToken ct = default)
        {
            return await Task.Run(() => CompactArz(inputFile, outputFile, progress, ct), ct);
        }

        public static bool CompactArz(string inputFile, string outputFile, IProgressCallback progress = null, System.Threading.CancellationToken ct = default, Action onBeforeWork = null)
        {
            DateTime startTime = DateTime.Now;
            try
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(inputFile))
                {
                    progress?.ReportError(string.Format(LanguageManager.Instance.GetText("Compact.FileNotFound", "文件不存在: {0}"), inputFile));
                    return false;
                }

                if (string.IsNullOrEmpty(outputFile))
                    outputFile = inputFile;

                progress?.Report(0, LanguageManager.Instance.GetText("Compact.Start", "开始固实..."));

                // 读取旧ARZ：先只统计有效/墓碑数量（不读数据），随后逐条 读取→重压→直写临时包。
                // 全程流式：不再 GetRawRecordData 式全量解压，也不在内存重建整包（峰值 ≈ 单条记录）。
                int tombstoneCount = 0;
                string compactTemp = SessionPackageWriter.CreateTempPackagePath(outputFile);

                using (FileStream fs = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    ct.ThrowIfCancellationRequested();
                    ARZReader reader = new ARZReader(fs);

                    // 有效记录数（非空名 + 去重），不触发任何数据解压
                    var seenNames = new HashSet<string>(StringComparer.Ordinal);
                    int validCount = 0;
                    for (int i = 0; i < reader.Count; i++)
                    {
                        var info = reader.GetRecordInfo(i);
                        if (info == null) continue;
                        string nm = reader.GetRecordName(i);
                        if (string.IsNullOrEmpty(nm)) continue;
                        if (!seenNames.Add(nm)) continue;
                        validCount++;
                    }
                    tombstoneCount = reader.Count - validCount;

                    progress?.ReportLog(string.Format(LanguageManager.Instance.GetText("Compact.ValidAndTombstone", "有效记录: {0}, 墓碑记录: {1}"), validCount, tombstoneCount));

                    if (tombstoneCount == 0)
                    {
                        progress?.ReportLog(LanguageManager.Instance.GetText("Compact.NoTombstone", "该固实没有墓碑记录"));
                        return true;
                    }

                    onBeforeWork?.Invoke();

                    // 直写重建：数据区边重压边写临时包（ARZWriter 直写模式），内存峰值从"整包"降到"单条记录"
                    var templates = new Dictionary<string, TemplateNode>();
                    try
                    {
                        using (FileStream ofs = new FileStream(compactTemp, FileMode.Create))
                        {
                            ct.ThrowIfCancellationRequested();
                            ARZWriter arzw = new ARZWriter(templates, ofs);
                            // 预填充旧字符串表，确保新旧记录的字串ID对应关系有效
                            arzw.strtable.PrepopulateFromOldTable(reader.GetStringTable());
                            int i = 0;
                            int totalValid = validCount;
                            reader.ForEachRecordData((name, info, decompressed) =>
                            {
                                ct.ThrowIfCancellationRequested();
                                int pct = (int)((double)i / Math.Max(1, totalValid) * 90);
                                progress?.Report(pct, string.Format(LanguageManager.Instance.GetText("Compact.Progress", "固实: ({0}/{1}) {2}"), i + 1, totalValid, name));

                                ARZRecord rec = new ARZRecord();
                                rec.strtable = arzw.strtable;
                                rec.rfid = arzw.strtable.AddString(name);
                                rec.rtype = info.rtype;
                                rec.rdFileTime = info.rdFileTime;
                                rec.entries = null;

                                rec.rdSizeDecompressed = decompressed.Length;
                                rec.cData = new byte[LZ4Codec.MaximumOutputSize(decompressed.Length)];
                                int compressedLength = LZ4Codec.Encode(decompressed, 0, decompressed.Length, rec.cData, 0, rec.cData.Length);
                                if (compressedLength < rec.cData.Length)
                                {
                                    byte[] temp = new byte[compressedLength];
                                    Buffer.BlockCopy(rec.cData, 0, temp, 0, compressedLength);
                                    rec.cData = temp;
                                }
                                rec.rdSizeCompressed = rec.cData.Length;

                                arzw.WriteRecord(rec);
                                i++;
                            });

                            progress?.Report(95, LanguageManager.Instance.GetText("Compact.Saving", "保存固实数据..."));
                            arzw.SaveToStream(ofs);
                        }
                    }
                    catch
                    {
                        try { if (File.Exists(compactTemp)) File.Delete(compactTemp); } catch { }
                        throw;
                    }
                }

                ct.ThrowIfCancellationRequested();
                // 同目录中间包 + 同卷 rename：不再直接写原包，避免中途失败/被占用把原包写坏
                // ⚠️ 必须在源包流关闭之后替换（源包正被打开时 rename 会失败）
                try
                {
                    SessionPackageWriter.ReplaceWithRetry(compactTemp, outputFile);
                }
                catch
                {
                    try { if (File.Exists(compactTemp)) File.Delete(compactTemp); } catch { }
                    throw;
                }

                TimeSpan duration = DateTime.Now - startTime;
                progress?.Report(100, string.Format(LanguageManager.Instance.GetText("Compact.Done", "固实完成，耗时: {0:F2}s (删除 {1} 个墓碑记录)"), duration.TotalSeconds, tombstoneCount));
                return true;
            }
            catch (OperationCanceledException)
            {
                progress?.ReportLog(LanguageManager.Instance.GetText("Compact.Cancelled", "固实已取消"));
                return false;
            }
            catch (Exception ex)
            {
                TimeSpan duration = DateTime.Now - startTime;
                string prompt = WriteFailureDiagnosis.Prompt(ex, outputFile);
                progress?.ReportError(prompt.Length > 0 ? prompt
                    : string.Format(LanguageManager.Instance.GetText("Compact.Error", "固实失败，耗时: {0:F2}s, {1}"), duration.TotalSeconds, ex.Message));
                return false;
            }
        }

        #endregion

        #region ARC 名字表编码预检（写侧）

        /// <summary>
        /// 由本地文件路径算出写入 ARC 名字表的相对路径（/ 分隔、小写、无前导 /）。
        /// 打包与预检必须共用本方法，否则两处口径会漂移。
        /// </summary>
        public static string MakeArcRelativePath(string inputDirectory, string filePath)
        {
            return Path.GetFullPath(filePath)
                .Substring(Path.GetFullPath(inputDirectory).Length)
                .Replace(Path.DirectorySeparatorChar, '/')
                .ToLower()
                .TrimStart('/');
        }

        /// <summary>
        /// 把包内条目名规范化为**安全的相对路径**（解包落盘前必须调用）。
        /// <para>为什么需要：落盘用 `Path.Combine(输出目录, 条目名)`，而 `Path.Combine` 遇到
        /// **带盘符的根路径**（如 `g:/x/y.dbr`，官方工具就允许这种写法）会直接返回它、丢弃输出目录，
        /// 于是解包会写到磁盘任意位置（实测可复现）。同理 `..` 会上跳越界。</para>
        /// <para>规则：反斜杠归一为 `/` → 去掉盘符前缀与开头所有 `/` → 逐段丢弃空段与 `.`，
        /// 遇 `..` 或非法文件名字符即拒绝。</para>
        /// </summary>
        /// <returns>安全相对路径；null = 该条目名不可用，调用方应跳过并告警（不得静默）</returns>
        private static string ToSafeRelativePath(string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return null;

            string s = entryName.Replace('\\', '/');

            // 去盘符前缀（仅认 "X:" 形式，避免误伤名字里含冒号的正常条目）
            if (s.Length >= 2 && s[1] == ':' && char.IsLetter(s[0]))
                s = s.Substring(2);

            s = s.TrimStart('/');

            string[] parts = s.Split('/');
            var keep = new List<string>(parts.Length);
            foreach (string p in parts)
            {
                if (p.Length == 0 || p == ".") continue;
                if (p == "..") return null;                                     // 上跳 → 越界，拒绝
                if (p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                keep.Add(p);
            }
            if (keep.Count == 0) return null;
            return string.Join("/", keep);
        }

        /// <summary>
        /// 解包落盘用的"安全相对路径"解析：返回 null 时已记账并告警（原文与归一结果都给出）。
        /// 归一结果与原文不同（即包内写了绝对路径/多余前缀）时也会告警，让用户知道包不规范。
        /// </summary>
        private static string ResolveUnpackPath(string entryName, IProgressCallback progress)
        {
            // 快路径：绝大多数条目名本就是规范相对路径 → 直接返回，省掉 ToSafeRelativePath 的
            // Replace/Split/Join 与随后的二次比较（解包时每条记录都会走一次）。
            // ⚠️ 判定刻意保守：只要有一点不确定就走下面的完整安全化（宁可多花一次处理，不可放过不安全路径）。
            if (IsCanonicalRelativePath(entryName))
                return entryName;

            string safe = ToSafeRelativePath(entryName);
            if (safe == null)
            {
                progress?.ReportWarning(string.Format(
                    LanguageManager.Instance.GetText("LOG.UnsafeEntryNameSkipped",
                        "跳过条目：包内路径不安全（绝对路径或含 \"..\"）: {0}"), entryName));
                return null;
            }
            if (!string.Equals(safe, entryName.Replace('\\', '/'), StringComparison.Ordinal))
            {
                progress?.ReportWarning(string.Format(
                    LanguageManager.Instance.GetText("LOG.EntryNameSanitized",
                        "包内路径不规范，已按相对路径解出: {0} → {1}"), entryName, safe));
            }
            return safe;
        }

        /// <summary>
        /// 条目名是否"已是规范相对路径"（即 <see cref="ToSafeRelativePath"/> 的结果与输入完全相同）。
        /// 只认可确定为安全的形态：无 `\`、无盘符/非法字符、不以 `/` 开头、无空段、无 `.`/`..` 段。
        /// 任一条件不满足即返回 false（走完整安全化路径）。
        /// </summary>
        private static bool IsCanonicalRelativePath(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name[0] == '/') return false;                       // 绝对路径
            if (name.IndexOf('\\') >= 0) return false;              // 需替换为 '/'
            if (name.IndexOf(':') >= 0) return false;               // 盘符形式或非法字符
            if (name.IndexOf("//", StringComparison.Ordinal) >= 0) return false;    // 空段
            if (name.StartsWith("./", StringComparison.Ordinal) || name.EndsWith("/.", StringComparison.Ordinal)
                || name == ".") return false;
            if (name.IndexOf("/./", StringComparison.Ordinal) >= 0) return false;
            if (name.StartsWith("../", StringComparison.Ordinal) || name.EndsWith("/..", StringComparison.Ordinal)
                || name == "..") return false;
            if (name.IndexOf("/../", StringComparison.Ordinal) >= 0) return false;
            if (name[name.Length - 1] == '/') return false;         // 尾随 '/'（空尾段）
            foreach (char c in name)
            {
                if (c < 32 || c == '"' || c == '<' || c == '>' || c == '|' || c == '*' || c == '?')
                    return false;                                    // 非法文件名字符（与 ToSafeRelativePath 同口径）
            }
            return true;
        }

        /// <summary>单名检查结论</summary>
        private enum NameCheckResult
        {
            /// <summary>可入包（纯 ASCII，或非 ASCII 但本机码页能表示且未开启"忽略非ASCII名"）</summary>
            Ok,
            /// <summary>含非 ASCII 字符，按「忽略非ASCII文件名」设置跳过</summary>
            PolicySkipped,
            /// <summary>本机码页表示不了，必须跳过</summary>
            Unrepresentable
        }

        /// <summary>
        /// 检查单个名字并累积到报告。
        /// 返回非 Ok 表示该文件应跳过、不写入包：
        /// PolicySkipped = 按设置跳过（含非 ASCII），Unrepresentable = 本机码页表示不了。
        /// </summary>
        private static NameCheckResult AccumulateNameCheck(ArcNameCheckReport report, string relativePath,
            bool skipNonAsciiNames, out ArcNameCheckItem badItem)
        {
            badItem = null;
            if (report == null || string.IsNullOrEmpty(relativePath)) return NameCheckResult.Ok;

            report.Total++;
            if (NameCodec.IsAscii(relativePath)) return NameCheckResult.Ok;

            report.NonAsciiCount++;

            // 设置项「忽略非ASCII文件名」（建议开启）：直接跳过，不再细究本机码页能否表示
            if (skipNonAsciiNames)
            {
                report.PolicySkipped.Add(relativePath);
                return NameCheckResult.PolicySkipped;
            }

            if (NameCodec.TryEncode(relativePath, out _, out char badChar, out int badCodePoint))
            {
                report.NonAsciiOk.Add(relativePath);
                return NameCheckResult.Ok;
            }

            badItem = new ArcNameCheckItem(relativePath, badChar, badCodePoint);
            report.BadItems.Add(badItem);
            return NameCheckResult.Unrepresentable;
        }

        /// <summary>
        /// ARZ 包**内容检查**（供 CLI --check / 编辑器检测按钮 / 打包解包页检测按钮复用）：
        /// 逐记录解压并检查**值层面**的可疑内容：值含换行、值含逗号、值含控制字符。
        /// <para>说明：① 缺尾逗号 / 行尾多余内容属**文本层**问题（包内名值分离、不留痕迹），不在本检查范围（解包时会报告）；
        /// ② 非数组字段值含分号需模板判定，本方法不加载模板，故不在范围内（打包/保存侧已有该项检查）；
        /// ③ 曾实现过"值尾疑似粘字段名"检测，因值与字段名撞车率过高（误报无法接受）已按使用者要求移除。</para>
        /// <para>仅报告、不改动包；进度通过 <paramref name="onProgress"/>（百分比, 文本）上报，
        /// 由调用方决定呈现方式（CLI 用底部单行、GUI 用按钮转圈）——**本方法不把进度写进日志**，避免污染检测结果。</para>
        /// </summary>
        /// <returns>(可疑条数, 已检查记录数, 含换行的可疑条数)</returns>
        public static (int Issues, int Records, int NewlineIssues) CheckArzContent(string arzPath, IProgressCallback progress, CancellationToken ct,
            Action<int, string> onProgress = null, Dictionary<string, TemplateNode> templates = null)
        {
            int issues = 0, checkedRecords = 0, totalRecords = 0, newlineCount = 0, commaCount = 0, controlCount = 0, corruptCount = 0;
            int trimCount = 0, longCount = 0, dupCount = 0, noTplCount = 0;
            int keyTrimCount = 0;
            int semicolonCount = 0, tplMissingCount = 0, tplFormatCount = 0, tplNotFoundCount = 0;

            // 检测项逐项开关（批B）：方法开头一次性读取，循环内零设置开销。
            // 「解压失败 / 无字段」属结构性异常，不可关闭，始终报告。
            bool chkNewline = SettingsManager.CheckNewline;
            bool chkComma = SettingsManager.CheckComma;
            bool chkControl = SettingsManager.CheckControlChar;
            bool chkTrim = SettingsManager.CheckTrimWhitespace;
            bool chkKeyTrim = SettingsManager.CheckKeyTrimWhitespace;
            bool chkLong = SettingsManager.CheckLongValue;
            bool chkDup = SettingsManager.CheckDuplicateField;
            bool chkNoTpl = SettingsManager.CheckNoTemplateName;
            bool chkSemicolon = SettingsManager.CheckSemicolon;
            bool chkTplMissing = SettingsManager.CheckTplFieldMissing;
            bool chkTplFormat = SettingsManager.CheckTplValueFormat;
            int longThreshold = SettingsManager.LongValueThreshold;

            // 模板类检测（批C）：需要模板时加载（调用方已传入则直接用，避免重复解析）。
            // 加载失败/为空 → 模板类检测整体跳过（与"模板未启用时自动跳过"的设置项说明一致），不影响其余检测。
            // 本次检测的"非标准模板路径"提示去重表清零（同一种非标准写法本次只提示一次）
            GDPackEditorAPI.ResetNonStandardTemplateNotices();
            bool needTpl = chkSemicolon || chkTplMissing || chkTplFormat;
            if (needTpl && templates == null)
                (templates, _, _, _, _) = Program.LoadTemplates(SettingsManager.TemplateEntries);
            bool tplReady = needTpl && templates != null && templates.Count > 0;
            DbrCheckCore tplCore = tplReady ? new DbrCheckCore(templates, Program.LastTemplateKeySource) : null;

            // 流式读取：不再整包 ReadAllBytes 驻留内存，ARZReader 按需 seek 读块（大包内存峰值大幅下降）
            using (FileStream fs = new FileStream(arzPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                ARZReader reader = new ARZReader(fs);
                totalRecords = reader.Count;
                int reportInterval = Math.Max(1, totalRecords / 100);   // 约 1% 上报一次，避免刷屏
                for (int i = 0; i < totalRecords; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (i % reportInterval == 0 || i == totalRecords - 1)
                    {
                        int pct = (int)((double)(i + 1) / totalRecords * 100);
                        // 进度只走 onProgress：CLI 显示底部单行、GUI 显示按钮转圈；不写日志（避免污染检测结果）
                        onProgress?.Invoke(pct, string.Format(LanguageManager.Instance.GetText("LOG.CheckArzProgress",
                            "检测中（{0}/{1}）{2}%"), i + 1, totalRecords, pct));
                    }
                    if (reader.GetRecordInfo(i) == null) continue;
                    string recName = reader.GetRecordName(i);
                    if (string.IsNullOrEmpty(recName)) continue;

                    ARZRecord record;
                    try { record = reader.GetRecord(i, fs); }
                    catch (Exception ex)
                    {
                        issues++;
                        corruptCount++;   // 损坏记录单独计数：保证汇总"各类合计 = 总数"（此前 48=17+30+1 的差值就在这）
                        string failMsg = string.Format(LanguageManager.Instance.GetText("LOG.CheckArzRecordFailed",
                            "内容可疑：记录解压失败（{1}）：{0}"), recName, ex.Message);
                        progress?.ReportWithFile(failMsg, "WARN", recName);
                        continue;
                    }
                    checkedRecords++;

                    // 记录没有任何字段 = 内容异常（正常记录至少含 templateName 项）→ 报告（底层解压异常会被吞掉，此处兜底）
                    if (record.entries == null || record.entries.Count == 0)
                    {
                        issues++;
                        corruptCount++;   // 与解压失败同属"损坏记录"类
                        string corruptMsg = string.Format(LanguageManager.Instance.GetText("LOG.CheckArzCorruptRecord",
                            "内容可疑：记录内容异常（无字段，可能已损坏）：{0}"), recName);
                        progress?.ReportWithFile(corruptMsg, "WARN", recName);
                        continue;
                    }

                    // ═══ 模板优先（需求：模板不存在就不必再检测字段）═══
                    // 取本记录的 templateName → 查模板，先判"有没有基线"：
                    //   ① 记录没有 templateName 行      → 报「缺 templateName 行」后**直接跳过该记录其余检测**
                    //   ② 有 templateName 但模板不存在  → 报「模板不存在」后**直接跳过该记录其余检测**
                    //   ③ 模板存在                      → 正常跑记录级（重复字段）+ 字段级检测
                    // 边界：模板环境整体未就绪（tplReady == false：模板类检测未勾选 / 模板加载失败 / 模板集为空）
                    //   时**不得**报"模板不存在"（否则会全包刷屏），维持原行为：只报缺模板名，字段检测照跑。
                    string recTplName = null;
                    foreach (var te in record.entries)
                    {
                        if (te.Name != "templateName") continue;
                        string tl = te.ToString();
                        int tf = tl.IndexOf(','), tlL = tl.LastIndexOf(',');
                        if (tf >= 0) recTplName = tlL > tf ? tl.Substring(tf + 1, tlL - tf - 1) : "";
                        break;
                    }

                    Dictionary<string, TemplateNode> tplFields = null;
                    if (tplReady)
                    {
                        if (string.IsNullOrEmpty(recTplName))
                        {
                            // 报告仍受 CheckNoTemplateName 开关控制（与既有口径一致）；短路则始终生效
                            if (chkNoTpl)
                            {
                                issues++;
                                noTplCount++;
                                progress?.ReportWithFile(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzNoTemplateName",
                                    "内容可疑：记录缺 templateName 行：{0}"), recName), "WARN", recName);
                            }
                            continue;
                        }

                        var tplRes = tplCore.Resolve(recTplName);
                        if (tplRes.Template == null)
                        {
                            issues++;
                            tplNotFoundCount++;
                            progress?.ReportWithFile(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzTemplateNotFound",
                                "内容可疑：模板不存在（已跳过该记录的检测）：{0} 模板 {1}"), recName, recTplName), "WARN", recName);
                            continue;   // 整条跳过：模板不存在 → 字段/内容检测都没有基线，报什么都没有意义
                        }
                        tplFields = tplCore.GetTemplateFields(recTplName);
                        ReportNonStandardTemplate(tplCore, tplRes, recTplName, recName,
                            msg => progress?.ReportWithFile(msg, "WARN", recName));
                    }
                    else if (chkNoTpl)
                    {
                        // 模板环境未就绪：保持原行为（只报缺模板名，不短路字段检测）
                        bool hasTplEntry = false;
                        foreach (var te in record.entries)
                            if (te.Name == "templateName") { hasTplEntry = true; break; }
                        if (!hasTplEntry)
                        {
                            issues++;
                            noTplCount++;
                            progress?.ReportWithFile(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzNoTemplateName",
                                "内容可疑：记录缺 templateName 行：{0}"), recName), "WARN", recName);
                        }
                    }

                    // 记录级检测（批B）：同名字段重复出现 → 后值覆盖前值（既有写入行为，此处只让它可见）
                    if (chkDup && record.entries.Count > 1)
                    {
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        var dupReported = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var de in record.entries)
                        {
                            string dn = de.Name;
                            if (string.IsNullOrEmpty(dn) || dn == "templateName") continue;
                            if (seen.Add(dn) || !dupReported.Add(dn)) continue;
                            int occ = 0;
                            foreach (var x in record.entries)
                                if (x.Name == dn) occ++;
                            issues++;
                            dupCount++;
                            progress?.ReportWithFile(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzDuplicateField",
                                "内容可疑：同名字段重复出现 {2} 次（后值覆盖前值）：{0} 字段 {1}"), recName, dn, occ), "WARN", recName);
                        }
                    }

                    foreach (var e in record.entries)
                    {
                        string nmRaw = e.Name;
                        if (string.IsNullOrEmpty(nmRaw) || nmRaw == "templateName") continue;
                        string nm = nmRaw.Trim();   // 后续匹配/展示统一用 Trim 后的名字

                        // 字段名首尾空白（独立检测项，批D+）：外来包的 key 可能带空白，
                        // 会导致模板匹配失败/读取时被去掉——包检测侧此前检不出（会被误报成"字段不在模板"）
                        if (chkKeyTrim && nm != nmRaw)
                        {
                            issues++;
                            keyTrimCount++;
                            progress?.ReportWithFile(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzKeyTrim",
                                "内容可疑：字段名首尾空白（读取时会被去掉）：{0} 字段 {1}"), recName, nmRaw), "WARN", recName);
                        }

                        // ⚠️ 必须按**整行**判定：值里若含逗号，只切第 2 段会漏检——
                        //    完整值 = 第一个逗号之后、最后一个逗号之前（如 `k,v,a,e,` → 值为 `v,a,e`）
                        string line = e.ToString();
                        int first = line.IndexOf(',');
                        int last = line.LastIndexOf(',');
                        if (first < 0) continue;
                        string val = last > first ? line.Substring(first + 1, last - first - 1) : "";

                        // 模板类检测（批C）：字段不在模板中 / 非数组含分号 / 值格式错误。
                        // 与内容类检测相互独立（同一字段可同时报两类），故放在 flags 提前 return 之前
                        if (tplFields != null)
                        {
                            if (!tplFields.TryGetValue(nm, out var vart))
                            {
                                issues++;
                                tplMissingCount++;
                                progress?.ReportWithFile(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzTplFieldMissing",
                                    "内容可疑：字段不在模板中：{0} 字段 {1} → {2}"), recName, nm, LogService.TruncateValueForLog(val)), "WARN", recName);
                            }
                            else
                            {
                                vart.values.TryGetValue("class", out string cls);
                                bool isArray = cls == "array";
                                vart.values.TryGetValue("type", out string ftype);
                                bool isStringLike = ftype == "string" || ftype == "equation"
                                    || (!string.IsNullOrEmpty(ftype) && ftype.StartsWith("file_"));
                                // 非数组、非字符串类型含分号：解析时按第一段截断（与打包侧同口径，arzfile.CheckDbrLine）
                                if (chkSemicolon && !isArray && !isStringLike && val.IndexOf(';') >= 0)
                                {
                                    issues++;
                                    semicolonCount++;
                                    progress?.ReportWithFile(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzSemicolon",
                                        "内容可疑：非数组字段含分号（解析时按第一段截断）：{0} 字段 {1} → {2}"),
                                        recName, nm, LogService.TruncateValueForLog(val)), "WARN", recName);
                                }
                                // 值格式：按模板 type 验证可解析性（int/real/bool；数组逐段）；string/equation/file_* 不判
                                if (chkTplFormat && !isArray)
                                {
                                    string badSeg = DbrCheckCore.FindBadFormatSegment(ftype, val);
                                    if (badSeg != null)
                                    {
                                        issues++;
                                        tplFormatCount++;
                                        progress?.ReportWithFile(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzTplValueFormat",
                                            "内容可疑：值格式错误（模板要求 {2}）：{0} 字段 {1} → {3}"),
                                            recName, nm, ftype, LogService.TruncateValueForLog(badSeg)), "WARN", recName);
                                    }
                                }
                            }
                        }

                        // 统一判定（与解包/打包共用 DbrContentCheck，零分配位掩码）：多个问题并存时一次性全部列出。
                        // 逐项开关（批B）：未勾选的类别直接从位掩码中清除
                        var flags = DbrContentCheck.CheckFlags(val);
                        if (!chkNewline) flags &= ~DbrContentFlags.Newline;
                        if (!chkComma) flags &= ~DbrContentFlags.Comma;
                        if (!chkControl) flags &= ~DbrContentFlags.ControlChar;
                        // 批B 新增值级检测：首尾空白 / 值过长（与字符类问题并存时合并进同一条明细）
                        bool hasTrim = chkTrim && val.Length > 0 && val.Trim().Length != val.Length;
                        bool hasLong = chkLong && val.Length > longThreshold;
                        if (flags == DbrContentFlags.None && !hasTrim && !hasLong) continue;
                        issues++;
                        if ((flags & DbrContentFlags.Newline) != 0) newlineCount++;
                        if ((flags & DbrContentFlags.Comma) != 0) commaCount++;
                        if ((flags & DbrContentFlags.ControlChar) != 0) controlCount++;
                        if (hasTrim) trimCount++;
                        if (hasLong) longCount++;

                        // 换行/控制字符报"位置"，其余用统一文案（仅命中时才构造原因列表）
                        var kinds = DbrContentCheck.ToKindList(flags);
                        var reasons = new List<string>(kinds.Count);
                        foreach (var k in kinds)
                        {
                            if (k == DbrContentCheck.KindNewline)
                            {
                                int nlPos = val.IndexOfAny(NewlineChars);
                                reasons.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzNewlineAt",
                                    "值含换行（位于第 {0} 个字符处）"), nlPos + 1));
                            }
                            else if (k == DbrContentCheck.KindControlChar)
                            {
                                // 与换行同口径：报第一个控制字符的位置（口径与 HasControlChar 一致，排除 \r\n）
                                int cPos = DbrContentCheck.IndexOfControlChar(val);
                                reasons.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzControlAt",
                                    "值含控制字符（位于第 {0} 个字符处）"), cPos + 1));
                            }
                            else reasons.Add(DbrContentCheck.Describe(k));
                        }
                        if (hasTrim)
                            reasons.Add(LanguageManager.Instance.GetText("LOG.CheckArzTrim",
                                "值首尾空白（读取时会被去掉）"));
                        if (hasLong)
                            reasons.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzLongValue",
                                "值过长（{0} 字符，阈值 {1}）"), val.Length, longThreshold));

                        // 值经 TruncateValueForLog：控制字符可视化 + 超长值前 50 字符（字段名与记录路径始终完整）
                        string msg = string.Format(LanguageManager.Instance.GetText("LOG.CheckArzItem",
                            "内容可疑：{0}：{1} 字段 {2} → {3}"),
                            string.Join("、", reasons), recName, nm, LogService.TruncateValueForLog(val));
                        progress?.ReportWithFile(msg, "WARN", recName);
                    }
                }
            }

            // 换行项的"为何导出看不到"说明**不在此输出**：由调用方在"检测完成"之后输出，
            // 那是对用户注意力最高的位置（见各调用点）
            if (issues > 0)
            {
                // 动态拼接类别：只列计数 > 0 的类别，保证「各类合计 = 总数」（需求5）；
                // 批C（模板类）/批D 只需在此追加类别，不再改文案结构
                var cats = new List<string>(8);
                if (newlineCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatNewline", "换行 {0}"), newlineCount));
                if (commaCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatComma", "逗号 {0}"), commaCount));
                if (controlCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatControlChar", "控制字符 {0}"), controlCount));
                if (trimCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatTrim", "首尾空白 {0}"), trimCount));
                if (keyTrimCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatKeyTrim", "字段首尾空白 {0}"), keyTrimCount));
                if (longCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatLong", "值过长 {0}"), longCount));
                if (dupCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatDup", "重复字段 {0}"), dupCount));
                if (noTplCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatNoTpl", "缺模板名行 {0}"), noTplCount));
                if (tplNotFoundCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatTplNotFound", "模板不存在 {0}"), tplNotFoundCount));
                if (semicolonCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatSemicolon", "分号截断 {0}"), semicolonCount));
                if (tplMissingCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatTplMissing", "字段不在模板 {0}"), tplMissingCount));
                if (tplFormatCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatTplFormat", "值格式错误 {0}"), tplFormatCount));
                if (corruptCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatCorrupt", "损坏记录 {0}"), corruptCount));

                progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzContentSummary",
                    "内容检查：共 {0} 条记录中发现 {1} 处可疑值（{2}；逐条明细见上方日志）"),
                    checkedRecords, issues, string.Join("、", cats)));
            }
            else
                progress?.ReportLog(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzContentOk",
                    "内容检查：{0} 条记录，未发现可疑值"), checkedRecords));

            return (issues, checkedRecords, newlineCount);
        }

        /// <summary>
        /// 目录 DBR **文本检测**（plan_20260923 §批D，需求6）：遍历目录（含子目录）下全部 .dbr，
        /// 按【内容检测】同一套规则逐文件检查。与包检测共用 DbrCheckCore 与检测开关设置。
        /// <para>与包检测的口径差异：目录文件是**原始文本**——① 数值/字符串都按文本原样参与检测，
        /// 所以「值格式错误」「非数组含分号」在这里能真实命中（包内为内联位存储，结构性难命中）；
        /// ② 行格式层（缺尾逗号等）默认不启用（打包时本就自动修复并提示），本方法不做行格式判定。</para>
        /// <para>仅报告、不改动任何文件；进度经 <paramref name="onProgress"/>（百分比, 文本）上报，
        /// 由调用方决定呈现方式，**本方法不把进度写进日志**。</para>
        /// </summary>
        /// <returns>(可疑条数, 已检查文件数)</returns>
        public static (int Issues, int Files) CheckDbrDirectory(string dirPath, IProgressCallback progress, CancellationToken ct,
            Action<int, string> onProgress = null, Dictionary<string, TemplateNode> templates = null)
        {
            int issues = 0, fileCount = 0;
            int newlineCount = 0, commaCount = 0, controlCount = 0, trimCount = 0, longCount = 0, dupCount = 0, noTplCount = 0;
            int keyTrimCount = 0;
            int semicolonCount = 0, tplMissingCount = 0, tplFormatCount = 0, tplNotFoundCount = 0;

            // 检测项逐项开关：与包检测同一套设置（方法开头一次性读取）
            bool chkNewline = SettingsManager.CheckNewline;
            bool chkComma = SettingsManager.CheckComma;
            bool chkControl = SettingsManager.CheckControlChar;
            bool chkTrim = SettingsManager.CheckTrimWhitespace;
            bool chkKeyTrim = SettingsManager.CheckKeyTrimWhitespace;
            bool chkLong = SettingsManager.CheckLongValue;
            bool chkDup = SettingsManager.CheckDuplicateField;
            bool chkNoTpl = SettingsManager.CheckNoTemplateName;
            bool chkSemicolon = SettingsManager.CheckSemicolon;
            bool chkTplMissing = SettingsManager.CheckTplFieldMissing;
            bool chkTplFormat = SettingsManager.CheckTplValueFormat;
            int longThreshold = SettingsManager.LongValueThreshold;

            // 本次检测的"非标准模板路径"提示去重表清零（同一种非标准写法本次只提示一次）
            GDPackEditorAPI.ResetNonStandardTemplateNotices();
            bool needTpl = chkSemicolon || chkTplMissing || chkTplFormat;
            if (needTpl && templates == null)
                (templates, _, _, _, _) = Program.LoadTemplates(SettingsManager.TemplateEntries);
            bool tplReady = needTpl && templates != null && templates.Count > 0;
            DbrCheckCore tplCore = tplReady ? new DbrCheckCore(templates, Program.LastTemplateKeySource) : null;

            var files = new List<string>(Directory.EnumerateFiles(dirPath, "*.dbr", SearchOption.AllDirectories));
            int total = files.Count;
            int reportInterval = Math.Max(1, total / 100);
            for (int fi = 0; fi < total; fi++)
            {
                ct.ThrowIfCancellationRequested();
                if (fi % reportInterval == 0 || fi == total - 1)
                {
                    int pct = total > 0 ? (int)((double)(fi + 1) / total * 100) : 100;
                    onProgress?.Invoke(pct, string.Format(LanguageManager.Instance.GetText("LOG.CheckArzProgress",
                        "检测中（{0}/{1}）{2}%"), fi + 1, total, pct));
                }

                string fullPath = files[fi];
                string relPath = fullPath.Substring(dirPath.Length).TrimStart('\\', '/');
                fileCount++;

                // 读文本：自动检测编码（UTF-8 有/无 BOM / 本机 ANSI），与编辑器读侧同一入口
                string text;
                try { text = EncodingService.ReadTextFile(fullPath); }
                catch (Exception ex)
                {
                    issues++;
                    progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckDbrFileReadFailed",
                        "内容可疑：文件读取失败（{1}）：{0}"), relPath, ex.Message), relPath);
                    continue;
                }

                // 解析名值对（检测口径：name = 首逗号前；value = 首逗号后、末逗号前——与包内 e.ToString() 一致）。
                // 行无逗号/空行：行格式层问题，本检测不做判定（打包时自动修复并提示）。
                // key 不做静默 Trim：首尾空白本身是检测项（读取/打包时会被去掉）
                string tplName = null;
                var pairs = new List<KeyValuePair<string, string>>();
                foreach (var rawLine in text.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (line.Trim().Length == 0) continue;
                    int first = line.IndexOf(',');
                    if (first < 0) continue;
                    int last = line.LastIndexOf(',');
                    string nameRaw = line.Substring(0, first);
                    string name = nameRaw.Trim();
                    if (name.Length == 0) continue;
                    string val = last > first ? line.Substring(first + 1, last - first - 1) : "";
                    if (name == "templateName") { tplName = val; continue; }
                    if (chkKeyTrim && nameRaw != name)
                    {
                        issues++;
                        keyTrimCount++;
                        progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzKeyTrim",
                            "内容可疑：字段名首尾空白（读取时会被去掉）：{0} 字段 {1}"), relPath, nameRaw), relPath);
                    }
                    pairs.Add(new KeyValuePair<string, string>(name, val));
                }

                // ═══ 模板优先（与包检测同口径）：模板不存在 → 报明后跳过该文件其余字段检测 ═══
                // 边界：模板环境未就绪（tplCore == null）时保持原行为（只报缺模板名，字段检测照跑）
                Dictionary<string, TemplateNode> tplFields = null;
                if (tplCore != null)
                {
                    if (string.IsNullOrEmpty(tplName))
                    {
                        if (chkNoTpl)
                        {
                            issues++;
                            noTplCount++;
                            progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzNoTemplateName",
                                "内容可疑：记录缺 templateName 行：{0}"), relPath), relPath);
                        }
                        continue;
                    }
                    var tplResF = tplCore.Resolve(tplName);
                    if (tplResF.Template == null)
                    {
                        issues++;
                        tplNotFoundCount++;
                        progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzTemplateNotFound",
                            "内容可疑：模板不存在（已跳过该记录的检测）：{0} 模板 {1}"), relPath, tplName), relPath);
                        continue;   // 整条跳过：模板不存在 → 字段/内容检测都没有基线
                    }
                    tplFields = tplCore.GetTemplateFields(tplName);
                    ReportNonStandardTemplate(tplCore, tplResF, tplName, relPath,
                        msg => progress?.ReportWarning(msg, relPath));
                }
                else if (chkNoTpl && tplName == null)
                {
                    issues++;
                    noTplCount++;
                    progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzNoTemplateName",
                        "内容可疑：记录缺 templateName 行：{0}"), relPath), relPath);
                }

                // 同名字段重复（候选检测）：文本里同名多次 = 后值覆盖前值（与包检测同口径）
                if (chkDup && pairs.Count > 1)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var dupReported = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in pairs)
                    {
                        if (seen.Add(p.Key) || !dupReported.Add(p.Key)) continue;
                        int occ = 0;
                        foreach (var q in pairs)
                            if (q.Key == p.Key) occ++;
                        issues++;
                        dupCount++;
                        progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzDuplicateField",
                            "内容可疑：同名字段重复出现 {2} 次（后值覆盖前值）：{0} 字段 {1}"), relPath, p.Key, occ), relPath);
                    }
                }

                // 逐字段检测（与包检测同一套判定）
                foreach (var p in pairs)
                {
                    string nm = p.Key, val = p.Value;

                    // 模板类检测（文本层可真实命中：见 DbrCheckCore.FindBadFormatSegment 注释）
                    if (tplFields != null)
                    {
                        if (!tplFields.TryGetValue(nm, out var vart))
                        {
                            issues++;
                            tplMissingCount++;
                            progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzTplFieldMissing",
                                "内容可疑：字段不在模板中：{0} 字段 {1} → {2}"), relPath, nm, LogService.TruncateValueForLog(val)), relPath);
                        }
                        else
                        {
                            vart.values.TryGetValue("class", out string cls);
                            bool isArray = cls == "array";
                            vart.values.TryGetValue("type", out string ftype);
                            bool isStringLike = ftype == "string" || ftype == "equation"
                                || (!string.IsNullOrEmpty(ftype) && ftype.StartsWith("file_"));
                            if (chkSemicolon && !isArray && !isStringLike && val.IndexOf(';') >= 0)
                            {
                                issues++;
                                semicolonCount++;
                                progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzSemicolon",
                                    "内容可疑：非数组字段含分号（解析时按第一段截断）：{0} 字段 {1} → {2}"),
                                    relPath, nm, LogService.TruncateValueForLog(val)), relPath);
                            }
                            if (chkTplFormat && !isArray)
                            {
                                string badSeg = DbrCheckCore.FindBadFormatSegment(ftype, val);
                                if (badSeg != null)
                                {
                                    issues++;
                                    tplFormatCount++;
                                    progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzTplValueFormat",
                                        "内容可疑：值格式错误（模板要求 {2}）：{0} 字段 {1} → {3}"),
                                        relPath, nm, ftype, LogService.TruncateValueForLog(badSeg)), relPath);
                                }
                            }
                        }
                    }

                    // 内容类检测（与包检测同一套 DbrContentCheck 判定）
                    var flags = DbrContentCheck.CheckFlags(val);
                    if (!chkNewline) flags &= ~DbrContentFlags.Newline;
                    if (!chkComma) flags &= ~DbrContentFlags.Comma;
                    if (!chkControl) flags &= ~DbrContentFlags.ControlChar;
                    bool hasTrim = chkTrim && val.Length > 0 && val.Trim().Length != val.Length;
                    bool hasLong = chkLong && val.Length > longThreshold;
                    if (flags == DbrContentFlags.None && !hasTrim && !hasLong) continue;
                    issues++;
                    if ((flags & DbrContentFlags.Newline) != 0) newlineCount++;
                    if ((flags & DbrContentFlags.Comma) != 0) commaCount++;
                    if ((flags & DbrContentFlags.ControlChar) != 0) controlCount++;
                    if (hasTrim) trimCount++;
                    if (hasLong) longCount++;

                    var kinds = DbrContentCheck.ToKindList(flags);
                    var reasons = new List<string>(kinds.Count + 2);
                    foreach (var k in kinds)
                    {
                        if (k == DbrContentCheck.KindNewline)
                        {
                            int nlPos = val.IndexOfAny(NewlineChars);
                            reasons.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzNewlineAt",
                                "值含换行（位于第 {0} 个字符处）"), nlPos + 1));
                        }
                        else if (k == DbrContentCheck.KindControlChar)
                        {
                            int cPos = DbrContentCheck.IndexOfControlChar(val);
                            reasons.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzControlAt",
                                "值含控制字符（位于第 {0} 个字符处）"), cPos + 1));
                        }
                        else reasons.Add(DbrContentCheck.Describe(k));
                    }
                    if (hasTrim)
                        reasons.Add(LanguageManager.Instance.GetText("LOG.CheckArzTrim", "值首尾空白（读取时会被去掉）"));
                    if (hasLong)
                        reasons.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzLongValue",
                            "值过长（{0} 字符，阈值 {1}）"), val.Length, longThreshold));
                    progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckArzItem",
                        "内容可疑：{0}：{1} 字段 {2} → {3}"),
                        string.Join("、", reasons), relPath, nm, LogService.TruncateValueForLog(val)), relPath);
                }
            }

            // 汇总（动态类别，与包检测同口径；"记录"语境换为"文件"）
            if (issues > 0)
            {
                var cats = new List<string>(11);
                if (newlineCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatNewline", "换行 {0}"), newlineCount));
                if (commaCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatComma", "逗号 {0}"), commaCount));
                if (controlCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatControlChar", "控制字符 {0}"), controlCount));
                if (trimCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatTrim", "首尾空白 {0}"), trimCount));
                if (keyTrimCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatKeyTrim", "字段首尾空白 {0}"), keyTrimCount));
                if (longCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatLong", "值过长 {0}"), longCount));
                if (dupCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatDup", "重复字段 {0}"), dupCount));
                if (noTplCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatNoTpl", "缺模板名行 {0}"), noTplCount));
                if (tplNotFoundCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatTplNotFound", "模板不存在 {0}"), tplNotFoundCount));
                if (semicolonCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatSemicolon", "分号截断 {0}"), semicolonCount));
                if (tplMissingCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatTplMissing", "字段不在模板 {0}"), tplMissingCount));
                if (tplFormatCount > 0) cats.Add(string.Format(LanguageManager.Instance.GetText("LOG.CheckCatTplFormat", "值格式错误 {0}"), tplFormatCount));

                progress?.ReportWarning(string.Format(LanguageManager.Instance.GetText("LOG.CheckDbrDirSummary",
                    "目录检测：共 {0} 个文件中发现 {1} 处可疑值（{2}；逐条明细见上方日志）"),
                    fileCount, issues, string.Join("、", cats)));
            }
            else
                progress?.ReportLog(string.Format(LanguageManager.Instance.GetText("LOG.CheckDbrDirOk",
                    "目录检测：{0} 个文件，未发现可疑值"), fileCount));

            return (issues, fileCount);
        }

        /// <summary>
        /// 内容检查发现换行项时的收尾说明：向使用者解释"为什么导出 / 源码里看不到"——
        /// 由调用方在输出"检测完成"之后调用，置于注意力最高的位置。
        /// </summary>
        public static void EmitCheckNewlineNote(IProgressCallback progress, int newlineIssues)
        {
            if (newlineIssues <= 0) return;
            progress?.ReportLog(LanguageManager.Instance.GetText("LOG.CheckArzNewlineNote",
                "说明：值里的换行会在解包 / 编辑器源码模式被自动删除（否则会破坏 DBR 行结构），"
                + "所以导出的文件与源码视图里看不到它——本检测读取的是包内原始数据"));
        }

        /// <summary>换行字符集（CR/LF）：检测与定位换行用</summary>
        private static readonly char[] NewlineChars = { '\r', '\n' };

        /// <summary>
        /// 输出文件名编码检查的结论。固定在日志末尾（详情之后），不受"显示详细日志"开关影响。
        /// 三类问题各自独立成行：既有能表示的非 ASCII 名、又有表示不了的名时，两行都会输出。
        /// </summary>
        public static void EmitArcNameCheckLog(IProgressCallback progress, ArcNameCheckReport report)
        {
            if (progress == null || report == null) return;

            progress.ReportLog(LanguageManager.Instance.GetText("ArcName.CheckHeader", "── 文件名编码检查 ──"));

            // ① 按设置跳过的（含非 ASCII，未入包）
            if (report.PolicySkipped.Count > 0)
            {
                progress.ReportWarning(string.Format(
                    LanguageManager.Instance.GetText("ArcName.CheckSkipNonAscii",
                        "{0} 个文件名带非 ASCII 字符，已按设置跳过（没写进包里）：{1}；建议改成只用{2}，不要出现本地区域特有的文字。"),
                    report.PolicySkipped.Count, string.Join(", ", report.PolicySkipped), AsciiCharsText()));
            }

            // ② 非 ASCII 但能入包（只在"忽略非ASCII文件名"关闭时出现）
            if (report.NonAsciiOk.Count > 0)
            {
                progress.ReportWarning(string.Format(
                    LanguageManager.Instance.GetText("ArcName.CheckWarn",
                        "{0} 个文件名带非 ASCII 字符，会按本机编码 {1} 存进包里（{2}）；换到别的语言区域可能认不出来；建议改成只用{3}，不要出现本地区域特有的文字。"),
                    report.NonAsciiOk.Count, report.AcpFriendlyName, string.Join(", ", report.NonAsciiOk), AsciiCharsText()));
            }

            // ③ 本机码页表示不了、必须跳过的
            if (report.BadItems.Count > 0)
            {
                var bad = new List<string>(report.BadItems.Count);
                foreach (var item in report.BadItems) bad.Add(item.RelativePath);
                progress.ReportError(string.Format(
                    LanguageManager.Instance.GetText("ArcName.CheckError",
                        "{0} 个名字无法识别，已跳过（没写进包里）：{1}；建议把文件名改成字母、数字组合，不出现本地区域特有文字。"),
                    report.BadItems.Count, string.Join(", ", bad)));
            }

            if (report.NonAsciiCount == 0)
                progress.ReportLog(LanguageManager.Instance.GetText("ArcName.CheckInfo",
                    "文件名检查：全部为 ASCII（跨区域无兼容风险）"));
        }

        /// <summary>
        /// "ASCII 名字能用哪些字符"的说明文本（单独一个多语言键）：
        /// 该文本本身含 { } 花括号，若内联进 string.Format 的格式串会被当成占位符，故独立成键、不参与格式化。
        /// </summary>
        private static string AsciiCharsText()
            => LanguageManager.Instance.GetText("ArcName.AsciiChars",
                "英文字母、数字、空格，以及 ! # $ % & ' ( ) + , - . ; = @ [ ] ^ _ { } ~ 这些符号");

        /// <summary>逐条输出被跳过的文件（详情级，受"显示详细日志"开关控制）</summary>
        private static void ReportSkippedName(IProgressCallback progress, ArcNameCheckItem item)
        {
            if (!SettingsManager.ShowDetailLog || progress == null || item == null) return;
            progress.ReportLog(string.Format(
                LanguageManager.Instance.GetText("ArcName.SkipItem", "跳过（名字无法识别）：{0}"),
                item.Describe()));
        }

        #endregion

        #region ARC增量打包

        public static async Task<bool> PackArcIncrementalAsync(string inputDirectory, string inputArcFile, string outputArcFile,
            IProgressCallback progress = null, Func<List<string>, bool> confirmCallback = null, bool includeDeletions = true,
            System.Threading.CancellationToken ct = default)
        {
            return await Task.Run(() => PackArcIncremental(inputDirectory, inputArcFile, outputArcFile, progress, confirmCallback, includeDeletions, ct), ct);
        }

        public static bool PackArcIncremental(string inputDirectory, string inputArcFile, string outputArcFile,
            IProgressCallback progress = null, Func<List<string>, bool> confirmCallback = null, bool includeDeletions = true,
            System.Threading.CancellationToken ct = default)
        {
            DateTime startTime = DateTime.Now;
            var changedNames = new List<string>();
            FileStream oldArcFs = null;
            string finalOutputArcFile = null;
            try
            {
                if (!Directory.Exists(inputDirectory))
                {
                    progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.InputDirectoryNotFound", "输入目录不存在")}: {inputDirectory}");
                    return false;
                }

                if (!File.Exists(inputArcFile))
                {
                    progress?.ReportError($"{LanguageManager.Instance.GetText("LOG.OldArcFileNotFound", "旧ARC文件不存在")}: {inputArcFile}");
                    return false;
                }

                FileInfo fi = new FileInfo(inputArcFile);
                if (fi.Length == 0)
                {
                    progress?.ReportError(LanguageManager.Instance.GetText("LOG.OldArcFileEmpty", "旧ARC文件为空"));
                    return false;
                }

                if (string.IsNullOrEmpty(outputArcFile))
                    outputArcFile = inputArcFile + ".new";

                string outputDir = Path.GetDirectoryName(outputArcFile);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                // 一律先写「与目标包同目录的临时包」，全部完成后原子替换目标包。
                // 直接写目标包的风险：取消/异常会删掉或截断用户的原有包；跨卷/被占用还会中途失败。
                finalOutputArcFile = SessionPackageWriter.CreateTempPackagePath(outputArcFile);
                bool needReplace = string.Equals(Path.GetFullPath(outputArcFile), Path.GetFullPath(inputArcFile), StringComparison.OrdinalIgnoreCase);

                // 名字表编码（写侧 = 本机 ANSI 码页）
                progress?.Report(0, string.Format(
                    LanguageManager.Instance.GetText("ArcName.EncodingInfo", "名字表编码：{0}"), NameCodec.AcpName));
                // 文件名编码检查（写侧：不可表示的名字跳过，且不纳入"新增/修改"）
                var nameReport = new ArcNameCheckReport();
                var nameReportLock = new object();
                var skippedNames = new HashSet<string>();
                // 设置项「过滤非ASCII文件名」：开启时含非 ASCII 的名字一律不入包
                // （作用范围 = ARC 文件名 + ARZ 记录名；不再逐次弹窗，结束后统一汇总告警）
                bool skipNonAsciiNames = EncodingService.NameFilterEnabled;

                // Phase 1: 打开旧ARC文件句柄读取索引（不整包读入内存，写入时按需随机读取）
                progress?.Report(0, LanguageManager.Instance.GetText("LOG.ReadOldArcIndex", "读取旧ARC索引..."));
                oldArcFs = new FileStream(inputArcFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                ARCFile afile = new ARCFile();
                if (progress != null) afile.NameReporter = progress.ReportLog;   // 读侧编码判定结论写入本次操作日志
                afile.ReadStream(oldArcFs);

                var oldHashMap = afile.GetEntryHashMap();
                var oldTocByName = new Dictionary<string, ARCTocEntry>();
                var oldTocOrder = new List<(string name, ARCTocEntry entry)>();
                for (int i = 0; i < afile.toc.Count; i++)
                {
                    string name = afile.toc[i].GetEntryString(afile.strs);
                    oldTocOrder.Add((name, afile.toc[i]));
                    if (!string.IsNullOrEmpty(name))
                        oldTocByName[name] = afile.toc[i];
                }
                progress?.ReportLog(string.Format(
                    LanguageManager.Instance.GetText("LOG.OldArcEntryCount", "旧ARC包含 {0} 个有效条目, {1} 个总条目"),
                    oldHashMap.Count, oldTocOrder.Count));

                // Phase 2: 扫描源目录
                progress?.Report(5, LanguageManager.Instance.GetText("LOG.ScanSourceFiles", "扫描源文件..."));
                // 顺序确定化：与全量打包同规则（按包内相对路径 ordinal 升序）
                string[] allFiles = Directory.GetFiles(inputDirectory, "*", SearchOption.AllDirectories)
                    .OrderBy(p => MakeArcRelativePath(inputDirectory, p), StringComparer.Ordinal)
                    .ToArray();
                progress?.ReportLog(string.Format(
                    LanguageManager.Instance.GetText("LOG.SourceFileCount", "源目录包含 {0} 个文件"), allFiles.Length));

                // Phase 3: 并发计算源文件校验和（只存元数据，不驻留文件内容，避免大目录内存暴涨）
                progress?.ReportLog(LanguageManager.Instance.GetText("LOG.ReadingSourceFiles", "并发读取源文件..."));
                bool normalizeArcText = SettingsManager.ShouldNormalizeArcText;
                var fileInfos = new ConcurrentBag<(string relPath, DateTime fileTime, int hash)>();
                Parallel.ForEach(allFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, filePath =>
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        string relativePath = MakeArcRelativePath(inputDirectory, filePath);

                        // 名字不能入包（本机码页表示不了，或按设置忽略非 ASCII 名）：
                        // 跳过该文件（剔除出"新增/修改"，也不当作"删除"）
                        NameCheckResult nameCheck;
                        ArcNameCheckItem badItem;
                        lock (nameReportLock)
                        {
                            nameCheck = AccumulateNameCheck(nameReport, relativePath, skipNonAsciiNames, out badItem);
                            if (nameCheck != NameCheckResult.Ok)
                                skippedNames.Add(relativePath);
                        }
                        if (nameCheck == NameCheckResult.Unrepresentable)
                        {
                            ReportSkippedName(progress, badItem);
                            return;
                        }
                        if (nameCheck == NameCheckResult.PolicySkipped)
                            return;

                        DateTime fileTime = File.GetLastWriteTime(filePath);
                        // ★校验和必须按"将要写入包内的字节"计算：开启文本规范化时，文本文件先转换再算 Adler32。
                        //   否则包内 hash（转换后）与源文件 hash（转换前）永远不等 → 每次都判为已修改。
                        int hash;
                        if (normalizeArcText && TextFileHelper.IsTextFile(relativePath))
                        {
                            byte[] packed = GetPackedTextBytes(filePath, out _, out _);
                            hash = packed != null ? ComputeAdler32(packed) : ComputeFileAdler32(filePath);
                        }
                        else
                        {
                            hash = ComputeFileAdler32(filePath);
                        }
                        fileInfos.Add((relativePath, fileTime, hash));
                    }
                    catch (Exception ex)
                    {
                        progress?.ReportWarning(string.Format(
                            LanguageManager.Instance.GetText("LOG.ReadFileFailed", "读取文件失败: {0}, {1}"),
                            filePath, ex.Message));
                    }
                });
                progress?.ReportLog(string.Format(
                    LanguageManager.Instance.GetText("LOG.FilesReadCount", "已读取 {0} 个文件"), fileInfos.Count));

                // Phase 4: 比对分类
                progress?.ReportLog(LanguageManager.Instance.GetText("LOG.CompareChecksums", "比对文件校验和..."));
                var sourceNames = new HashSet<string>();
                var unchangedNames = new HashSet<string>();
                var changedFiles = new List<(string relPath, DateTime fileTime, int hash)>();
                var newFiles = new List<(string relPath, DateTime fileTime, int hash)>();
                var deletedNames = new HashSet<string>();

                foreach (var fileInfo in fileInfos)
                {
                    sourceNames.Add(fileInfo.relPath);
                    if (oldHashMap.TryGetValue(fileInfo.relPath, out int oldHash))
                    {
                        if (oldHash == fileInfo.hash)
                            unchangedNames.Add(fileInfo.relPath);
                        else
                            changedFiles.Add(fileInfo);
                    }
                    else
                    {
                        newFiles.Add(fileInfo);
                    }
                }

                // 被跳过的名字：不参与"新增/修改"分类，也不当作"删除"
                // （既避免"清单算了它、实际没写入"，又保留旧包中的同名条目原样不动）
                // 跳过原因与全量名单统一由末尾的 EmitArcNameCheckLog 汇总，此处不再重复输出
                foreach (string skipped in skippedNames)
                    sourceNames.Add(skipped);

                if (includeDeletions)
                {
                    foreach (string name in oldHashMap.Keys)
                    {
                        if (!sourceNames.Contains(name))
                            deletedNames.Add(name);
                    }
                }
                else
                {
                    foreach (string name in oldHashMap.Keys)
                    {
                        if (!sourceNames.Contains(name))
                            unchangedNames.Add(name);
                    }
                }

                progress?.ReportLog(string.Format(
                    LanguageManager.Instance.GetText("LOG.ClassifySummary", "分类: 未变={0}, 修改={1}, 新增={2}, 删除={3}"),
                    unchangedNames.Count, changedFiles.Count, newFiles.Count, deletedNames.Count));

                changedNames.AddRange(changedFiles.Select(f => $"[修改] {f.relPath}"));
                changedNames.AddRange(newFiles.Select(f => $"[新增] {f.relPath}"));
                if (includeDeletions)
                    changedNames.AddRange(deletedNames.Select(n => $"[删除] {n}"));

                // Phase 5: 变更确认回调（在写入之前调用）
                if (confirmCallback != null && changedNames.Count > 0)
                {
                    bool proceed = confirmCallback(changedNames);
                    if (!proceed)
                    {
                        progress?.ReportLog(LanguageManager.Instance.GetText("LOG.UserCancelled", "用户取消了操作"));
                        try { if (File.Exists(finalOutputArcFile)) File.Delete(finalOutputArcFile); } catch { }
                        return false;
                    }
                }

                // Phase 6: 写入新ARC（分两步：先按旧顺序写+墓碑，再在结尾插入新内容）
                progress?.Report(5, LanguageManager.Instance.GetText("LOG.WritingArc", "写入ARC文件..."));
                int unchangedCount = 0, modifiedCount = 0, addedCount = 0, deletedCount = 0;
                int totalSteps = Math.Max(1, oldTocOrder.Count + changedFiles.Count + newFiles.Count);
                int step = 0;
                int arcProgressInterval = Math.Max(1, totalSteps / 50);
                // 已修改文件名的集合（循环外建一次）：原实现在旧 TOC 循环内用 changedFiles.Any(...)
                // 线性扫描，旧条目数 × 修改数 的字符串比较（O(n²)）。原比较是 ==（Ordinal），此处等价。
                var changedNameSet = new HashSet<string>(changedFiles.Select(f => f.relPath), StringComparer.Ordinal);

                using (FileStream ofs = new FileStream(finalOutputArcFile, FileMode.Create))
                using (ARCWriter awriter = new ARCWriter(ofs))
                {
                    // Step A: 按旧TOC顺序写入条目
                    foreach (var (oldName, oldEntry) in oldTocOrder)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (string.IsNullOrEmpty(oldName))
                        {
                            // 空名条目（游戏原生 EntryType=0 墓碑/空条目）原样保留，维持原始 TOC 结构
                            awriter.WriteFromTocEntry(afile, oldEntry);
                            step++;
                            continue;
                        }

                        step++;
                        if (step % arcProgressInterval == 0 || step == totalSteps)
                        {
                            double pct = 5 + (double)step / totalSteps * 95;
                            progress?.Report((int)pct, string.Format(
                                LanguageManager.Instance.GetText("LOG.WritingArcProgress", "写入ARC: {0}/{1} ({2}%)"),
                                step, totalSteps, pct.ToString("F1")));
                        }

                        if (unchangedNames.Contains(oldName))
                        {
                            awriter.WriteFromTocEntry(afile, oldEntry);
                            unchangedCount++;
                        }
                        else if (skippedNames.Contains(oldName))
                        {
                            // 名字无法用本机码页表示的文件：不重写、不墓碑，旧条目原样保留
                            awriter.WriteFromTocEntry(afile, oldEntry);
                        }
                        else if (changedNameSet.Contains(oldName) || deletedNames.Contains(oldName))
                        {
                            // 已修改或已删除 → 写墓碑（空名称，数据保留）
                            awriter.WriteTombstoneEntry(afile, oldEntry);
                            if (deletedNames.Contains(oldName))
                                deletedCount++;
                        }
                        // 新文件不在旧TOC中，跳过
                    }

                    // Step B: 在结尾插入修改后的新内容（重新打开源文件，避免驻留内存）
                    foreach (var (relPath, fileTime, fileHash) in changedFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        step++;
                        string srcFullPath = Path.Combine(inputDirectory, relPath.Replace('/', Path.DirectorySeparatorChar));
                        // 与 Phase 3 校验和口径保持一致：文本文件先规范化再写入
                        byte[] packedBytes = (normalizeArcText && TextFileHelper.IsTextFile(relPath))
                            ? GetPackedTextBytes(srcFullPath, out _, out _) : null;
                        if (packedBytes != null)
                        {
                            using (var ms = new MemoryStream(packedBytes, false))
                                awriter.WriteFromStream(relPath, fileTime, ms);
                        }
                        else
                        {
                            using (FileStream fs = new FileStream(srcFullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                                awriter.WriteFromStream(relPath, fileTime, fs);
                        }
                        modifiedCount++;
                        if (step % arcProgressInterval == 0 || step == totalSteps)
                        {
                            double pct = 5 + (double)step / totalSteps * 95;
                            progress?.Report((int)pct, string.Format(
                                LanguageManager.Instance.GetText("LOG.WritingArcProgress", "写入ARC: {0}/{1} ({2}%)"),
                                step, totalSteps, pct.ToString("F1")));
                        }
                    }

                    // Step C: 在结尾插入新增文件（重新打开源文件，避免驻留内存）
                    foreach (var (relPath, fileTime, fileHash) in newFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        step++;
                        string srcFullPath = Path.Combine(inputDirectory, relPath.Replace('/', Path.DirectorySeparatorChar));
                        // 与 Phase 3 校验和口径保持一致：文本文件先规范化再写入
                        byte[] packedBytes = (normalizeArcText && TextFileHelper.IsTextFile(relPath))
                            ? GetPackedTextBytes(srcFullPath, out _, out _) : null;
                        if (packedBytes != null)
                        {
                            using (var ms = new MemoryStream(packedBytes, false))
                                awriter.WriteFromStream(relPath, fileTime, ms);
                        }
                        else
                        {
                            using (FileStream fs = new FileStream(srcFullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                                awriter.WriteFromStream(relPath, fileTime, fs);
                        }
                        addedCount++;
                        if (step % arcProgressInterval == 0 || step == totalSteps)
                        {
                            double pct = 5 + (double)step / totalSteps * 95;
                            progress?.Report((int)pct, string.Format(
                                LanguageManager.Instance.GetText("LOG.WritingArcProgress", "写入ARC: {0}/{1} ({2}%)"),
                                step, totalSteps, pct.ToString("F1")));
                        }
                    }
                }

                // 输出=输入时必须先关闭源文件句柄；随后统一原子替换为最终文件
                if (needReplace)
                {
                    oldArcFs.Close();
                    oldArcFs = null;
                }
                try
                {
                    SessionPackageWriter.ReplaceWithRetry(finalOutputArcFile, outputArcFile);
                    finalOutputArcFile = null;
                }
                catch (Exception ex)
                {
                    // 写盘类失败（含替换失败）→ 四行式提示；其它 → 原消息
                    string prompt = WriteFailureDiagnosis.Prompt(ex, outputArcFile, finalOutputArcFile);
                    progress?.ReportError(prompt.Length > 0 ? prompt : ex.Message);
                    return false;
                }

                TimeSpan duration = DateTime.Now - startTime;
                if (normalizeArcText)
                    progress?.ReportLog(LanguageManager.Instance.GetText("LOG.NormalizeEnabled",
                        "文本编码规范化(→UTF-8 无 BOM)已启用：文本文件按规范化后的字节比对与写入。"
                        + "注意：切换该设置会使下次打包时全部文本文件被判为已修改（属预期行为）。"));
                progress?.Report(100, string.Format(
                    LanguageManager.Instance.GetText("LOG.IncrementalDone",
                        "ARC增量打包完成，耗时: {0}s。未变: {1}, 修改: {2}, 新增: {3}, 删除: {4}"),
                    duration.TotalSeconds.ToString("F2"), unchangedCount, modifiedCount, addedCount, deletedCount));

                // 文件名编码检查结论：固定排在末尾（详情之后）
                EmitArcNameCheckLog(progress, nameReport);
                return true;
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(finalOutputArcFile)) File.Delete(finalOutputArcFile); } catch { }
                progress?.ReportLog(LanguageManager.Instance.GetText("LOG.OperationTerminated", "操作已终止"));
                return false;
            }
            catch (Exception ex)
            {
                TimeSpan duration = DateTime.Now - startTime;
                string prompt = WriteFailureDiagnosis.Prompt(ex, outputArcFile);
                progress?.ReportError(prompt.Length > 0 ? prompt
                    : string.Format(
                        LanguageManager.Instance.GetText("LOG.IncrementalFailed", "ARC增量打包失败，耗时: {0}s, {1}"),
                        duration.TotalSeconds.ToString("F2"), ex.Message));
                return false;
            }
            finally
            {
                try { if (oldArcFs != null) oldArcFs.Close(); } catch { }
            }
        }

        /// <summary>
        /// 取文本文件用于打包的规范字节（UTF-8 无 BOM）。返回 null 表示应按原始字节处理（跳过或不适用）。
        /// 【重要】增量打包的校验和计算与实际写入必须共用本方法，否则会出现"每次打包都判定为已修改"。
        /// </summary>
        private static byte[] GetPackedTextBytes(string filePath, out string reason, out TextFileHelper.NormalizeOutcome outcome)
        {
            reason = null;
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Length > TextFileHelper.MaxNormalizeBytes)
                {
                    outcome = TextFileHelper.NormalizeOutcome.Skipped;
                    reason = string.Format(LanguageManager.Instance.GetText("LOG.Norm.OverLimit", "超过 {0}MB 上限"),
                        TextFileHelper.MaxNormalizeBytes / (1024 * 1024));
                    return null;
                }
                byte[] src = File.ReadAllBytes(filePath);
                // 目标编码来自【设置 → 编码 → 内容打包】（原样档 = null → 直接返回源字节）
                outcome = EncodingService.NormalizeForPack(src, out byte[] dst, out reason);
                return outcome == TextFileHelper.NormalizeOutcome.Skipped ? null : dst;
            }
            catch (Exception ex)
            {
                outcome = TextFileHelper.NormalizeOutcome.Skipped;
                reason = string.Format(LanguageManager.Instance.GetText("LOG.Norm.ReadFailed", "读取失败: {0}"), ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 把"用户编辑后的临时文件"写进 ARC：按【设置 → 编码 → 内容打包】处理文本白名单文件。
        /// 原样档 / 非文本文件 = 直接搬字节（与既有行为一致）；统一档 = 先转码再写。
        /// fileTime = 条目入包时间（时间戳规则：基准包已有条目保留原包内时间；纯新增自定义条目随实体文件时间）。
        /// </summary>
        private static void WriteMergeCustomEntry(ARCWriter writer, string entryName, string customPath, DateTime fileTime)
        {
            if (EncodingService.PackNeedsConvert && TextFileHelper.IsTextFile(entryName))
            {
                byte[] srcBytes = File.ReadAllBytes(customPath);
                EncodingService.NormalizeForPack(srcBytes, out byte[] packed, out _);
                using (var ms = new MemoryStream(packed, false))
                    writer.WriteFromStream(entryName, fileTime, ms);
                return;
            }
            using (var fs = File.OpenRead(customPath))
                writer.WriteFromStream(entryName, fileTime, fs);
        }

        /// <summary>计算字节数组的 Adler32（与 ARCWriter 写入时使用的校验口径一致）</summary>
        private static int ComputeAdler32(byte[] data)
        {
            Adler32 adler = new Adler32();
            adler.ComputeHash(data, 0, data.Length);
            return (int)adler.checksum;
        }

        /// <summary>
        /// 流式计算文件的Adler32校验和（分块读取，避免整文件读入内存）
        /// </summary>
        private static int ComputeFileAdler32(string filePath)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Adler32 adler = new Adler32();
                byte[] buffer = new byte[ARCWriter.MAX_BLOCK_SIZE];
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    adler.ComputeHash(buffer, 0, read);
                return (int)adler.checksum;
            }
        }

        #endregion

        #region ARC固实

        public static async Task<bool> CompactArcAsync(string inputFile, string outputFile, IProgressCallback progress = null, System.Threading.CancellationToken ct = default)
        {
            return await Task.Run(() => CompactArc(inputFile, outputFile, progress, ct), ct);
        }

        public static bool CompactArc(string inputFile, string outputFile, IProgressCallback progress = null, System.Threading.CancellationToken ct = default, Action onBeforeWork = null)
        {
            DateTime startTime = DateTime.Now;
            try
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(inputFile))
                {
                    progress?.ReportError(string.Format(LanguageManager.Instance.GetText("Compact.ArcFileNotFound", "ARC文件不存在: {0}"), inputFile));
                    return false;
                }

                if (string.IsNullOrEmpty(outputFile))
                    outputFile = inputFile;

                progress?.Report(0, LanguageManager.Instance.GetText("Compact.ArcOpen", "打开ARC文件..."));
                ARCFile afile = new ARCFile();
                if (progress != null) afile.NameReporter = progress.ReportLog;   // 读侧编码判定结论写入本次操作日志
                // 内存优化：不再整包 ReadAllBytes 驻留内存，直接用 FileStream 打开源包（CompactToStream 按 part 读）
                FileStream srcFs = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                afile.ReadStream(srcFs);

                ct.ThrowIfCancellationRequested();

                int totalEntries = afile.toc?.Count ?? 0;
                progress?.ReportLog(string.Format(LanguageManager.Instance.GetText("Compact.ArcTotalEntries", "总条目: {0}"), totalEntries));

                // 统计有效和无效条目
                int validCount = 0;
                int skippedCount = 0;
                for (int i = 0; i < totalEntries; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = afile.toc[i].GetEntryString(afile.strs);
                    if (string.IsNullOrEmpty(name) || afile.toc[i].EntryType != 3)
                        skippedCount++;
                    else
                        validCount++;
                }

                progress?.ReportLog(string.Format(LanguageManager.Instance.GetText("Compact.ArcValidAndSkipped", "有效条目: {0}, 将被清理: {1}"), validCount, skippedCount));

                if (skippedCount == 0)
                {
                    progress?.ReportLog(LanguageManager.Instance.GetText("Compact.ArcNoSkipped", "无需固实，没有无效条目"));
                    return true;
                }

                onBeforeWork?.Invoke();

                progress?.Report(5, LanguageManager.Instance.GetText("Compact.Start", "开始固实..."));
                ct.ThrowIfCancellationRequested();
                // 同目录中间包 + 同卷 rename：与 CompactArz 同一策略
                string compactTemp = SessionPackageWriter.CreateTempPackagePath(outputFile);
                try
                {
                    using (FileStream ofs = new FileStream(compactTemp, FileMode.Create))
                    {
                        afile.CompactToStream(ofs);
                    }
                    // 替换前必须关闭源包流（源包正被打开时 rename 会失败）
                    srcFs.Dispose();
                    srcFs = null;
                    SessionPackageWriter.ReplaceWithRetry(compactTemp, outputFile);
                }
                catch
                {
                    try { if (File.Exists(compactTemp)) File.Delete(compactTemp); } catch { }
                    throw;
                }

                TimeSpan duration = DateTime.Now - startTime;
                progress?.Report(100, string.Format(LanguageManager.Instance.GetText("Compact.ArcDone", "ARC固实完成，耗时: {0:F2}s (清理 {1} 个无效条目)"), duration.TotalSeconds, skippedCount));
                return true;
                }
                finally
                {
                    srcFs?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                progress?.ReportLog(LanguageManager.Instance.GetText("Compact.Cancelled", "固实已取消"));
                return false;
            }
            catch (Exception ex)
            {
                TimeSpan duration = DateTime.Now - startTime;
                string prompt = WriteFailureDiagnosis.Prompt(ex, outputFile);
                progress?.ReportError(prompt.Length > 0 ? prompt
                    : string.Format(LanguageManager.Instance.GetText("Compact.ArcError", "ARC固实失败，耗时: {0:F2}s, {1}"), duration.TotalSeconds, ex.Message));
                return false;
            }
        }

        #endregion

        #region MOD合并

        /// <summary>
        /// 扫描对比ARZ文件
        /// </summary>
        public static List<ModMergeToolForm.CompareItem> MergeArzScan(string basePath, string modPath, IProgressCallback progress, CancellationToken ct)
        {
            var items = new List<ModMergeToolForm.CompareItem>();
            var baseStream = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var modStream = new FileStream(modPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var baseReader = new ARZReader(baseStream);
                var modReader = new ARZReader(modStream);

                var baseNames = baseReader.GetAllRecordNames().Where(n => !string.IsNullOrEmpty(n));
                var modNames = modReader.GetAllRecordNames().Where(n => !string.IsNullOrEmpty(n));
                var allNames = new HashSet<string>(baseNames.Concat(modNames), StringComparer.OrdinalIgnoreCase);

                var baseInfoMap = baseReader.GetRecordInfoMap();
                var modInfoMap = modReader.GetRecordInfoMap();

                int total = allNames.Count;
                int current = 0;

                foreach (var record in allNames)
                {
                    ct.ThrowIfCancellationRequested();
                    current++;
                    progress?.UpdateProgress((int)((double)current / total * 100), record);

                    var item = new ModMergeToolForm.CompareItem { FileName = record };
                    bool hasBase = baseInfoMap.ContainsKey(record);
                    bool hasMod = modInfoMap.ContainsKey(record);

                    if (hasBase && hasMod)
                    {
                        var baseInfo = baseInfoMap[record];
                        var modInfo = modInfoMap[record];
                        item.Size1 = baseInfo.rdSizeDecompressed;
                        item.Size2 = modInfo.rdSizeDecompressed;

                        // Phase 0 流式验证：LZ4解压每条记录 → 按 name,value 重排序消除插入顺序差异 → 哈希排序后规范文本 → 即时释放
                        string hash1 = null, hash2 = null;
                        ARZRecord baseRec = null, modRec = null;
                        try
                        {
                            baseRec = baseReader.GetLoadedRecordByName(record, baseInfoMap);
                            modRec = modReader.GetLoadedRecordByName(record, modInfoMap);
                            if (baseRec?.entries != null && modRec?.entries != null)
                            {
                                string canon1 = BuildCanonicalDbrText(baseRec);
                                string canon2 = BuildCanonicalDbrText(modRec);
                                uint h1 = Adler32.Compute(Encoding.UTF8.GetBytes(canon1));
                                uint h2 = Adler32.Compute(Encoding.UTF8.GetBytes(canon2));
                                hash1 = h1.ToString("X8");
                                hash2 = h2.ToString("X8");
                                if (h1 == h2 && canon1.Length == canon2.Length)
                                {
                                    item.Status = ModMergeToolForm.CompareStatus.Same;
                                }
                                else
                                {
                                    item.Status = ModMergeToolForm.CompareStatus.Conflict;
                                }
                            }
                            else
                            {
                                hash1 = baseInfo.rdSizeDecompressed.ToString("X8");
                                hash2 = modInfo.rdSizeDecompressed.ToString("X8");
                                if (baseInfo.rdSizeDecompressed == modInfo.rdSizeDecompressed)
                                {
                                    item.Status = ModMergeToolForm.CompareStatus.Same;
                                }
                                else
                                {
                                    item.Status = ModMergeToolForm.CompareStatus.Conflict;
                                }
                            }

                            // 扫描期提取说明/本地化标签（基准优先，为空取并入包）：成功路径此前漏调导致说明列空白
                            ExtractDbrMetadata(baseRec, item);
                            if (string.IsNullOrEmpty(item.Description))
                                ExtractDbrMetadata(modRec, item);
                        }
                        catch
                        {
                            hash1 = baseInfo.rdSizeDecompressed.ToString("X8");
                            hash2 = modInfo.rdSizeDecompressed.ToString("X8");
                            if (baseInfo.rdSizeDecompressed == modInfo.rdSizeDecompressed)
                            {
                                item.Status = ModMergeToolForm.CompareStatus.Same;
                            }
                            else
                            {
                                item.Status = ModMergeToolForm.CompareStatus.Conflict;
                            }
                            ExtractDbrMetadata(baseRec, item);
                        }
                        finally
                        {
                            baseRec?.DiscardData();
                            modRec?.DiscardData();
                        }
                        item.Hash1 = hash1;
                        item.Hash2 = hash2;
                    }
                    else if (hasBase)
                    {
                        var baseInfo = baseInfoMap[record];
                        item.Size1 = baseInfo.rdSizeDecompressed;
                        item.Hash1 = baseInfo.rdSizeDecompressed.ToString("X8");
                                    item.Status = ModMergeToolForm.CompareStatus.Only1;
                        ARZRecord baseRec = null;
                        try { baseRec = baseReader.GetLoadedRecordByName(record, baseInfoMap); ExtractDbrMetadata(baseRec, item); }
                        catch { }
                        finally { baseRec?.DiscardData(); }
                    }
                    else
                    {
                        var modInfo = modInfoMap[record];
                        item.Size2 = modInfo.rdSizeDecompressed;
                        item.Hash2 = modInfo.rdSizeDecompressed.ToString("X8");
                                    item.Status = ModMergeToolForm.CompareStatus.Only2;
                        ARZRecord modRec = null;
                        try { modRec = modReader.GetLoadedRecordByName(record, modInfoMap); ExtractDbrMetadata(modRec, item); }
                        catch { }
                        finally { modRec?.DiscardData(); }
                    }

                    items.Add(item);
                }
            }
            finally
            {
                baseStream?.Dispose();
                modStream?.Dispose();
            }
            return items;
        }

        /// <summary>
        /// 构建用于哈希验证的规范化 DBR 文本（流式验证后即释放，按 name,value 重排序消除插入顺序差异）
        /// </summary>
        private static string BuildCanonicalDbrText(ARZRecord rec)
        {
            if (rec.entries == null || rec.entries.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            // templateName 保持首位不动（与 ARZRecord 构造一致），其余按字段名排序
            sb.Append(rec.entries[0].ToString()).Append('\n');
            if (rec.entries.Count > 1)
            {
                var sorted = rec.entries.Skip(1).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
                foreach (var e in sorted)
                    sb.Append(e.ToString()).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 从 ARZRecord 中同步提取 fileDescription 和本地化标签字段（仅.dbr文件有效）
        /// </summary>
        private static void ExtractDbrMetadata(ARZRecord rec, ModMergeToolForm.CompareItem item)
        {
            if (rec?.entries == null || rec.entries.Count == 0) return;
            string recName = rec.Name ?? "";
            bool isDbr = recName.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase);
            if (!isDbr) return;

            foreach (var entry in rec.entries)
            {
                if (entry == null || entry.values == null || entry.values.Length == 0) continue;
                string fieldName = entry.Name ?? "";
                if (entry.dtype == ARZEntryType.String && entry.values.Length > 0)
                {
                    string val = entry.AsString(0);
                    if (string.IsNullOrEmpty(val)) continue;
                    if (string.Equals(fieldName, "fileDescription", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Description = val;
                    }
                    else if (Array.IndexOf(LocalizationHelper.KnownTagFields, fieldName) >= 0)
                    {
                        if (item.TagDict == null)
                            item.TagDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        item.TagDict[fieldName] = val;
                    }
                }
            }
        }


        /// <summary>
        /// 扫描对比ARC文件
        /// </summary>
        public static List<ModMergeToolForm.CompareItem> MergeArcScan(string basePath, string modPath, IProgressCallback progress, CancellationToken ct)
        {
            var items = new List<ModMergeToolForm.CompareItem>();
            var baseArc = new ARCFile();
            var modArc = new ARCFile();
            // 双方各判一次名字编码，结论加前缀区分归属
            if (progress != null)
            {
                baseArc.NameReporter = progress.ReportLog;
                modArc.NameReporter = progress.ReportLog;
            }
            baseArc.NameReporterPrefix = "[" + LanguageManager.Instance.GetText("ModMergeTool.HistoryBase", "基准包") + "]";
            modArc.NameReporterPrefix = "[" + LanguageManager.Instance.GetText("ModMergeTool.HistoryMod", "并入包") + "]";
            var baseStream = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var modStream = new FileStream(modPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                baseArc.ReadStream(baseStream);
                modArc.ReadStream(modStream);

                var baseHashMap = baseArc.GetEntryHashMap();
                var modHashMap = modArc.GetEntryHashMap();
                var allEntries = new HashSet<string>(baseHashMap.Keys.Concat(modHashMap.Keys), StringComparer.OrdinalIgnoreCase);

                // 一次性构建条目名→TOC索引映射（避免循环内线性扫描导致 O(n²)）
                var baseIdxMap = baseArc.GetEntryIndexMap();
                var modIdxMap = modArc.GetEntryIndexMap();

                int total = allEntries.Count;
                int current = 0;

                foreach (var entryName in allEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    current++;
                    progress?.UpdateProgress((int)((double)current / total * 100), entryName);

                    var item = new ModMergeToolForm.CompareItem { FileName = entryName };
                    bool hasBase = baseHashMap.ContainsKey(entryName);
                    bool hasMod = modHashMap.ContainsKey(entryName);

                    if (hasBase && hasMod)
                    {
                        int baseHash = baseHashMap[entryName];
                        int modHash = modHashMap[entryName];
                        long baseSize = baseIdxMap.TryGetValue(entryName, out int bi) && bi >= 0 ? baseArc.toc[bi].DecompressedSize : 0;
                        long modSize = modIdxMap.TryGetValue(entryName, out int mi) && mi >= 0 ? modArc.toc[mi].DecompressedSize : 0;

                        item.Size1 = baseSize;
                        item.Size2 = modSize;
                        item.Hash1 = baseHash.ToString("X8");
                        item.Hash2 = modHash.ToString("X8");

                        if (baseHash == modHash)
                        {
                            item.Status = ModMergeToolForm.CompareStatus.Same;
                        }
                        else
                        {
                            item.Status = ModMergeToolForm.CompareStatus.Conflict;
                        }
                    }
                    else if (hasBase)
                    {
                        int baseIdx = baseIdxMap.TryGetValue(entryName, out int b1) && b1 >= 0 ? b1 : 0;
                        item.Size1 = baseArc.toc[baseIdx].DecompressedSize;
                        item.Hash1 = baseHashMap[entryName].ToString("X8");
                                    item.Status = ModMergeToolForm.CompareStatus.Only1;
                    }
                    else
                    {
                        int modIdx = modIdxMap.TryGetValue(entryName, out int m1) && m1 >= 0 ? m1 : 0;
                        item.Size2 = modArc.toc[modIdx].DecompressedSize;
                        item.Hash2 = modHashMap[entryName].ToString("X8");
                                    item.Status = ModMergeToolForm.CompareStatus.Only2;
                    }

                    items.Add(item);
                }
            }
            finally
            {
                baseStream?.Dispose();
                modStream?.Dispose();
            }
            return items;
        }

        /// <summary>合并耗时显示（ARZ/ARC 合并统计行共用）：不足 1 分钟显示 "12.3s"，超过显示 "1m05s"</summary>
        private static string FormatMergeElapsed(TimeSpan elapsed)
            => elapsed.TotalMinutes >= 1
                ? string.Format("{0}m{1:00}s", (int)elapsed.TotalMinutes, elapsed.Seconds)
                : string.Format("{0:F1}s", elapsed.TotalSeconds);

        /// <summary>
        /// 合并ARZ文件
        /// </summary>
        public static bool MergeArz(string basePath, string modPath, string outputPath,
            Dictionary<string, string> decisions, Dictionary<string, string> customPaths,
            Dictionary<string, string> renamed, IProgressCallback progress, CancellationToken ct)
        {
            try
            {
                var baseStream = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var modStream = new FileStream(modPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                // 输出包走「同目录临时包 → 原子替换」：直接写目标包时失败/取消会留下半个包
                string mergeTemp = SessionPackageWriter.CreateTempPackagePath(outputPath);
                FileStream mergeFs = null;
                try
                {
                    var baseReader = new ARZReader(baseStream);
                    var modReader = new ARZReader(modStream);
                    var (templates, _, _, _, _) = Program.LoadTemplates(SettingsManager.TemplateEntries);
                    // 直写模式：数据区边写边落盘到临时包，不再内存累积整包 + SaveToStream 二次复制
                    // ⚠️ 必须 ReadWrite：直写模式收尾要重读数据区计算总校验和
                    mergeFs = new FileStream(mergeTemp, FileMode.Create, FileAccess.ReadWrite);
                    var writer = new ARZWriter(templates, mergeFs);
                    writer.BeginWrite();
                    int total = decisions.Count;
                    int current = 0;
                    int writtenCount = 0;
                    int skipCount = 0;        // 跳过总数（未找到数据 + 模板未找到 + 写入异常，三者口径一致：决策了但没写进包）
                    int tplMissingSkip = 0;   // 其中"模板未找到"整条不入包的条数（单独计数，末尾明示原因）
                    int mergeFixTotal = 0, mergeWarnTotal = 0;   // 行格式修复/可疑计数（逐条已上报，末尾只报数量）
                    var mergeSw = System.Diagnostics.Stopwatch.StartNew();

                    // 一次性构建记录名→元信息映射（避免循环内重复全量重建导致 O(n²)）
                    progress?.UpdateProgress(15, LanguageManager.Instance.GetText("Merge.StageRead", "正在读取包结构..."));
                    var baseInfoMap = baseReader.GetRecordInfoMap();
                    var modInfoMap = modReader.GetRecordInfoMap();
                    progress?.UpdateProgress(30, LanguageManager.Instance.GetText("Merge.StageRead", "正在读取包结构..."));

                    foreach (var kvp in decisions)
                    {
                        ct.ThrowIfCancellationRequested();
                        current++;
                        string recordName = kvp.Key;
                        string decision = kvp.Value;
                        bool handled = false;
                        bool skipReported = false;   // 跳过原因已各自上报（模板缺失/写入异常），不再重复报"[跳过] 未找到数据"

                        progress?.UpdateProgress(30 + (int)((double)current / total * 60), recordName);

                        // 单条记录异常仅跳过当前记录、继续合并（对齐打包/保存，见 plan_20291013 3.3C）
                        try
                        {
                            switch (decision)
                            {
                                case "1":
                                    var baseRecord = baseReader.GetLoadedRecordByName(recordName, baseInfoMap);
                                    if (baseRecord != null)
                                    {
                                        writer.WriteFromRecord(baseRecord);
                                        writtenCount++; handled = true;
                                    }
                                    break;
                                case "2":
                                    var modRecord = modReader.GetLoadedRecordByName(recordName, modInfoMap);
                                    if (modRecord != null)
                                    {
                                        writer.WriteFromRecord(modRecord);
                                        writtenCount++; handled = true;
                                    }
                                    break;
                                case "C":
                                    if (customPaths.TryGetValue(recordName, out string customPath) && File.Exists(customPath))
                                    {
                                        // 按实际编码读取（BOM→严格UTF-8→本机ACP），再按【内容打包】设置决定写回包内的编码
                                        var lines = TemplateHelper.ReadDbrLines(customPath, out Encoding customEnc);
                                        // 时间戳规则：基准包已有记录保留原包内时间；纯新增记录随实体文件（临时文件）的修改时间
                                        DateTime fileTime = baseInfoMap.ContainsKey(recordName)
                                            ? baseInfoMap[recordName].rdFileTime
                                            : File.GetLastWriteTime(customPath);
                                        // 构造与写入分离（BuildRecord，打包/保存/合并共用入口）：
                                        // 构造阶段才知道"模板未找到"与字段级问题，先上报再写入
                                        ARZRecord rec = writer.BuildRecord(recordName, lines, fileTime, EncodingService.ResolveWriteEncoding(customEnc));
                                        var (recFixes, recWarns) = RecordWarningReporter.ReportRecordIssues(progress, recordName, rec);
                                        mergeFixTotal += recFixes; mergeWarnTotal += recWarns;
                                        writer.WriteRecord(rec);
                                        writtenCount++; handled = true;
                                    }
                                    break;
                                case "R":
                                    if (renamed.TryGetValue(recordName, out string newName))
                                    {
                                        byte[] data = null;
                                        DateTime ft = DateTime.Now;
                                        var mRec = modReader.GetLoadedRecordByName(recordName, modInfoMap);
                                        if (mRec != null)
                                        {
                                            data = mRec.GetRawDecompressedData();
                                            ft = modInfoMap[recordName].rdFileTime;
                                        }
                                        else
                                        {
                                            var bRec = baseReader.GetLoadedRecordByName(recordName, baseInfoMap);
                                            if (bRec != null)
                                            {
                                                data = bRec.GetRawDecompressedData();
                                                ft = baseInfoMap[recordName].rdFileTime;
                                            }
                                        }
                                        if (data != null)
                                        {
                                            string text = TemplateHelper.DecodeText(data, out Encoding rEnc);
                                            var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                                            ARZRecord renRec = writer.BuildRecord(newName, lines, ft, EncodingService.ResolveWriteEncoding(rEnc));
                                            var (renFixes, renWarns) = RecordWarningReporter.ReportRecordIssues(progress, newName, renRec);
                                            mergeFixTotal += renFixes; mergeWarnTotal += renWarns;
                                            writer.WriteRecord(renRec);
                                            writtenCount++; handled = true;
                                        }
                                    }
                                    break;
                                case "RC":
                                    if (renamed.TryGetValue(recordName, out string rcOrigRec))
                                    {
                                        var rcRec = modReader.GetLoadedRecordByName(rcOrigRec, modInfoMap);
                                        if (rcRec != null)
                                        {
                                            byte[] rcData = rcRec.GetRawDecompressedData();
                                            if (rcData != null)
                                            {
                                                string rcText = TemplateHelper.DecodeText(rcData, out Encoding rcEnc);
                                                var rcLines = rcText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                                                ARZRecord rcNewRec = writer.BuildRecord(recordName, rcLines, modInfoMap[rcOrigRec].rdFileTime, EncodingService.ResolveWriteEncoding(rcEnc));
                                                var (rcFixes, rcWarns) = RecordWarningReporter.ReportRecordIssues(progress, recordName, rcNewRec);
                                                mergeFixTotal += rcFixes; mergeWarnTotal += rcWarns;
                                                writer.WriteRecord(rcNewRec);
                                                writtenCount++; handled = true;
                                            }
                                        }
                                    }
                                    break;
                            }
                        }
                        catch (OperationCanceledException) { throw; }   // 取消不当作单条记录失败
                        catch (KeyNotFoundException)
                        {
                            // 模板未找到 → 整条记录不入包（对齐打包/保存）：警告后跳过继续
                            tplMissingSkip++;
                            skipReported = true;
                            RecordWarningReporter.ReportTemplateMissing(progress, recordName);
                        }
                        catch (Exception e)
                        {
                            skipReported = true;
                            progress?.ReportWarning(string.Format(
                                LanguageManager.Instance.GetText("Merge.ErrorWritingRecord",
                                    "写入记录失败，已跳过：{0}，原因：{1}"), recordName, e.Message), recordName);
                        }

                        if (!handled)
                        {
                            skipCount++;
                            if (!skipReported)
                                progress?.ReportLog(string.Format(LanguageManager.Instance.GetText("Merge.Skip", "[跳过] 未找到数据: {0}"), recordName));
                        }
                    }

                    // 合并统计（写入/跳过与决策数能对上；跳过含"未找到数据/模板未找到/写入异常"三类）
                    mergeSw.Stop();
                    progress?.ReportLog(string.Format(
                        LanguageManager.Instance.GetText("Merge.Stat",
                            "合并统计: 决策 {0} 条，写入 {1} 条，跳过 {2} 条，耗时 {3}"),
                        total, writtenCount, skipCount, FormatMergeElapsed(mergeSw.Elapsed)));

                    // 行格式修复/可疑汇总：只报数量（逐条明细已在上方日志，避免双份显示）
                    if (mergeFixTotal > 0 || mergeWarnTotal > 0)
                        progress?.ReportWarning(string.Format(
                            LanguageManager.Instance.GetText("Merge.DataIssueSummary",
                                "本次合并：行格式已修复 {0} 处、可疑 {1} 处（不影响出包，建议核对源文件；逐条明细见上方日志）"),
                            mergeFixTotal, mergeWarnTotal));

                    // 模板未找到导致整条不入包：单独明示（原因 / 该怎么办）
                    if (tplMissingSkip > 0)
                        progress?.ReportWarning(string.Format(
                            LanguageManager.Instance.GetText("Merge.TemplateMissingSummary",
                                "本次合并：因「模板未找到」跳过了 {0} 条记录（整条未入包）；请在设置中添加对应自定义模板后重新合并"),
                            tplMissingSkip));

                    if (writtenCount == 0)
                    {
                        progress?.ReportError(string.Format(LanguageManager.Instance.GetText("Merge.NothingWritten", "没有写入任何条目，输出为空。请检查决策中引用的条目在对应包中是否存在。")));
                        mergeFs.Dispose();
                        mergeFs = null;
                        try { if (File.Exists(mergeTemp)) File.Delete(mergeTemp); } catch { }
                        return false;
                    }

                    progress?.UpdateProgress(92, LanguageManager.Instance.GetText("Merge.StageWrite", "正在写入输出包..."));
                    try
                    {
                        // 直写模式收尾（回填 header + 表/串表/footer）
                        writer.SaveToStream(mergeFs);
                        // 替换前必须关闭临时包流
                        mergeFs.Dispose();
                        mergeFs = null;
                        SessionPackageWriter.ReplaceWithRetry(mergeTemp, outputPath);
                    }
                    catch (Exception ex)
                    {
                        progress?.ReportError(string.Format(
                            LanguageManager.Instance.GetText("LOG.AtomicReplaceFailed",
                                "输出包替换失败：{0}；本次已完整构建的包保留在：{1}；常见原因：目标正被占用（游戏/其它编辑器/杀毒扫描/网盘同步）、文件或目录只读。可直接重试。"),
                            outputPath, mergeTemp) + "  " + ex.Message);
                        return false;
                    }
                    progress?.UpdateProgress(100, LanguageManager.Instance.GetText("Merge.StageWrite", "正在写入输出包..."));
                }
                finally
                {
                    baseStream?.Dispose();
                    modStream?.Dispose();
                    if (mergeFs != null)
                    {
                        // 未走到正常收尾（异常/取消）：关闭并清理半成品临时包
                        try { mergeFs.Dispose(); } catch { }
                        try { if (File.Exists(mergeTemp)) File.Delete(mergeTemp); } catch { }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                // 写盘类失败 → 四行式提示；其它失败 → 原消息
                string prompt = WriteFailureDiagnosis.Prompt(ex, outputPath);
                progress?.ReportError(prompt.Length > 0 ? prompt : ex.Message);
                return false;
            }
        }

        /// <summary>        /// 合并ARC文件
        /// </summary>
        public static bool MergeArc(string basePath, string modPath, string outputPath,
            Dictionary<string, string> decisions, Dictionary<string, string> customPaths,
            Dictionary<string, string> renamed, IProgressCallback progress, CancellationToken ct)
        {
            try
            {
                var baseArc = new ARCFile();
                var modArc = new ARCFile();
                // 双方各判一次名字编码，结论加前缀区分归属
                if (progress != null)
                {
                    baseArc.NameReporter = progress.ReportLog;
                    modArc.NameReporter = progress.ReportLog;
                }
                baseArc.NameReporterPrefix = "[" + LanguageManager.Instance.GetText("ModMergeTool.HistoryBase", "基准包") + "]";
                modArc.NameReporterPrefix = "[" + LanguageManager.Instance.GetText("ModMergeTool.HistoryMod", "并入包") + "]";
                var baseStream = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var modStream = new FileStream(modPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    baseArc.ReadStream(baseStream);
                    modArc.ReadStream(modStream);

                // 输出包走「同目录临时包 → 原子替换」：直接写目标包时失败/取消会留下半个包
                string mergeTemp = SessionPackageWriter.CreateTempPackagePath(outputPath);
                using (var outputStream = new FileStream(mergeTemp, FileMode.Create, FileAccess.Write))
                        using (var writer = new ARCWriter(outputStream))
                        {
                            writer.BeginWrite();
                            int total = decisions.Count;
                            int current = 0;
                            int writtenCount = 0;
                            int skipCount = 0;   // 跳过总数（未找到数据 + 写入异常）
                            var mergeSw = System.Diagnostics.Stopwatch.StartNew();

                            // 一次性构建条目名→TOC索引映射（避免循环内线性扫描导致 O(n²)）
                            progress?.UpdateProgress(15, LanguageManager.Instance.GetText("Merge.StageRead", "正在读取包结构..."));
                            var baseIdxMap = baseArc.GetEntryIndexMap();
                            var modIdxMap = modArc.GetEntryIndexMap();
                            progress?.UpdateProgress(30, LanguageManager.Instance.GetText("Merge.StageRead", "正在读取包结构..."));

                            foreach (var kvp in decisions)
                            {
                                ct.ThrowIfCancellationRequested();
                                current++;
                                string entryName = kvp.Key;
                                string decision = kvp.Value;
                                bool handled = false;
                                bool skipReported = false;   // 跳过原因已各自上报（写入异常），不再重复报"[跳过] 未找到数据"

                                progress?.UpdateProgress(30 + (int)((double)current / total * 60), entryName);

                                // 单个条目异常仅跳过当前条目、继续合并（对齐打包/保存与 ARZ 合并）
                                try
                                {
                                    switch (decision)
                                    {
                                        case "1":
                                            if (baseIdxMap.TryGetValue(entryName, out int baseIdx) && baseIdx >= 0)
                                            {
                                                writer.WriteFromTocEntry(baseArc, baseArc.toc[baseIdx]);
                                                writtenCount++; handled = true;
                                            }
                                            break;
                                        case "2":
                                            if (modIdxMap.TryGetValue(entryName, out int modIdx) && modIdx >= 0)
                                            {
                                                writer.WriteFromTocEntry(modArc, modArc.toc[modIdx]);
                                                writtenCount++; handled = true;
                                            }
                                            break;
                                        case "C":
                                            if (customPaths.TryGetValue(entryName, out string customPath) && File.Exists(customPath))
                                            {
                                                // 编辑结果（临时文件）按【设置 → 编码 → 内容打包】写回：
                                                // 原样档 = 直接搬字节；统一档 = 先转码再写。二进制/白名单外文件永不动。
                                                // 注：R / RC 是"原样搬运未改动条目"，不受统一档影响（见 G4 边界说明）。
                                                // 时间戳规则（对齐 ARZ 合并）：基准包已有条目保留原包内时间；
                                                // 纯新增的自定义条目无包内时间，随实体文件（临时文件）的修改时间。
                                                DateTime customFt = baseIdxMap.TryGetValue(entryName, out int baseIdxC) && baseIdxC >= 0
                                                    ? baseArc.toc[baseIdxC].FileTime
                                                    : File.GetLastWriteTime(customPath);
                                                WriteMergeCustomEntry(writer, entryName, customPath, customFt);
                                                writtenCount++; handled = true;
                                            }
                                            break;
                                        case "R":
                                            if (renamed.TryGetValue(entryName, out string newName))
                                            {
                                                using (var ms = new MemoryStream())
                                                {
                                                    var sourceArc = modArc;
                                                    if (!modIdxMap.TryGetValue(entryName, out int sourceIdx) || sourceIdx < 0)
                                                    {
                                                        sourceArc = baseArc;
                                                        baseIdxMap.TryGetValue(entryName, out sourceIdx);
                                                    }
                                                    if (sourceIdx >= 0)
                                                    {
                                                        var sourceEntry = sourceArc.toc[sourceIdx];
                                                        sourceArc.UnpackToStream(sourceEntry, ms);
                                                        ms.Position = 0;
                                                        writer.WriteFromStream(newName, sourceEntry.FileTime, ms);
                                                        writtenCount++; handled = true;
                                                    }
                                                }
                                            }
                                            break;
                                        case "RC":
                                            if (renamed.TryGetValue(entryName, out string rcOrigName))
                                            {
                                                if (modIdxMap.TryGetValue(rcOrigName, out int modSrcIdx) && modSrcIdx >= 0)
                                                {
                                                    using (var ms = new MemoryStream())
                                                    {
                                                        modArc.UnpackToStream(modArc.toc[modSrcIdx], ms);
                                                        ms.Position = 0;
                                                        writer.WriteFromStream(entryName, modArc.toc[modSrcIdx].FileTime, ms);
                                                        writtenCount++; handled = true;
                                                    }
                                                }
                                            }
                                            break;
                                    }
                                }
                                catch (OperationCanceledException) { throw; }   // 取消不当作单条目失败
                                catch (Exception e)
                                {
                                    skipReported = true;
                                    progress?.ReportWarning(string.Format(
                                        LanguageManager.Instance.GetText("Merge.ErrorWritingRecord",
                                            "写入记录失败，已跳过：{0}，原因：{1}"), entryName, e.Message), entryName);
                                }

                                if (!handled)
                                {
                                    skipCount++;
                                    if (!skipReported)
                                        progress?.ReportLog(string.Format(LanguageManager.Instance.GetText("Merge.Skip", "[跳过] 未找到数据: {0}"), entryName));
                                }
                            }

                            // 合并统计（与 ARZ 合并同款口径：决策/写入/跳过 + 耗时）
                            mergeSw.Stop();
                            progress?.ReportLog(string.Format(
                                LanguageManager.Instance.GetText("Merge.Stat",
                                    "合并统计: 决策 {0} 条，写入 {1} 条，跳过 {2} 条，耗时 {3}"),
                                total, writtenCount, skipCount, FormatMergeElapsed(mergeSw.Elapsed)));

                            if (writtenCount == 0)
                            {
                                progress?.ReportError(string.Format(LanguageManager.Instance.GetText("Merge.NothingWritten", "没有写入任何条目，输出为空。请检查决策中引用的条目在对应包中是否存在。")));
                                try { outputStream.Dispose(); if (File.Exists(mergeTemp)) File.Delete(mergeTemp); } catch { }
                                return false;
                            }

                            progress?.UpdateProgress(92, LanguageManager.Instance.GetText("Merge.StageWrite", "正在写入输出包..."));
                            writer.FinishWrite();
                            progress?.UpdateProgress(100, LanguageManager.Instance.GetText("Merge.StageWrite", "正在写入输出包..."));
                        }

                        // 关闭输出流后再原子替换目标包
                        try
                        {
                            SessionPackageWriter.ReplaceWithRetry(mergeTemp, outputPath);
                        }
                        catch (Exception ex)
                        {
                            progress?.ReportError(string.Format(
                                LanguageManager.Instance.GetText("LOG.AtomicReplaceFailed",
                                    "输出包替换失败：{0}；本次已完整构建的包保留在：{1}；常见原因：目标正被占用（游戏/其它编辑器/杀毒扫描/网盘同步）、文件或目录只读。可直接重试。"),
                                outputPath, mergeTemp) + "  " + ex.Message);
                            return false;
                        }
                }
                finally
                {
                    baseStream?.Dispose();
                    modStream?.Dispose();
                }
                return true;
            }
            catch (Exception ex)
            {
                // 写盘类失败 → 四行式提示；其它失败 → 原消息
                string prompt = WriteFailureDiagnosis.Prompt(ex, outputPath);
                progress?.ReportError(prompt.Length > 0 ? prompt : ex.Message);
                return false;
            }
        }

        #endregion

        #region 非标准模板路径提示（检测侧共用）

        /// <summary>
        /// 已提示过的"非标准模板路径"（去重键 = 归一化原路径 + " → " + 命中规范键）。
        /// 每次检测开始前调用 <see cref="ResetNonStandardTemplateNotices"/> 清空 → "本次同模板只提示一次"。
        /// </summary>
        private static readonly HashSet<string> _nonStandardTemplateNotified = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>清空"非标准模板路径"提示去重表（每次检测开始前调用）</summary>
        internal static void ResetNonStandardTemplateNotices()
        {
            lock (_nonStandardTemplateNotified) _nonStandardTemplateNotified.Clear();
        }

        /// <summary>
        /// 报「非标准模板路径」（同一写法只在本次运行提示一次）。
        /// <para>触发条件：DBR 的 templateName 不是 <c>database/templates/…</c> 标准写法，
        /// 但**去掉多余前缀后能匹配到**模板（典型：<c>mods/&lt;mod名&gt;/database/templates/x.tpl</c>）。
        /// 给出**原路径 → 实际使用路径 + 来源**两个路径，用户可自行核对；并提示可能有字段缺失、
        /// 建议在设置中添加对应自定义模板 —— 目的是引导用户回到官方标准模板。</para>
        /// <para>去重键 = 原路径写法 + 命中的规范键（**不是按文件**）→ 整包同一种写法只提示一次，避免刷屏。</para>
        /// </summary>
        internal static void ReportNonStandardTemplate(DbrCheckCore core, TemplateNameResolver.Result r,
            string rawName, string recordName, Action<string> emit)
        {
            if (core == null || emit == null || !r.NonStandard) return;
            string key = TemplateNameResolver.Normalize(rawName) + " → " + r.StandardKey;
            lock (_nonStandardTemplateNotified)
                if (!_nonStandardTemplateNotified.Add(key)) return;

            string sourceLabel = core.SourceLabelOf(r);
            if (string.IsNullOrEmpty(sourceLabel))
                sourceLabel = LanguageManager.Instance.GetText("Template.UnknownSource", "未知来源");
            emit(string.Format(LanguageManager.Instance.GetText("LOG.TemplateNonStandardPath",
                "非标准模板路径：{0} 的 {1} → 已按「{2}」的 {3} 匹配，可能有字段缺失，建议添加对应自定义模板（同模板仅提示一次）"),
                recordName, rawName, sourceLabel, r.StandardKey));
        }

        #endregion
    }

    /// <summary>
    /// 进度回调接口
    /// </summary>
    public interface IProgressCallback
    {
        void Report(int percentage, string message);
        void ReportLog(string message);
        void ReportError(string error);
        /// <summary>
        /// 上报一条警告。<paramref name="groupKey"/> 为记录名/文件名（生成侧打标）：带键的警告
        /// 在 GUI 日志区按"连续同名段"折叠分组（头行=记录名+问题计数）；不传则按普通警告显示。
        /// </summary>
        void ReportWarning(string warning, string groupKey = null);
        void LogOverwrite(string message);
        void LogFinalize();
        /// <summary>仅更新进度条与状态标签，不写日志（用于过程性静默进度，如解包前的记录扫描）</summary>
        void UpdateProgress(int percentage, string message);

        /// <summary>
        /// 带"包内记录相对路径"的进度行：编辑器日志对该行提供**双击跳转到包内文件**的能力
        /// （结构化标记：路径随行在内存流转，不写入日志文本）。
        /// <para>默认实现把 WARN/FATAL 的 relPath（记录名/文件名）兼作折叠分组键传给
        /// <see cref="ReportWarning"/>——包内容检测的逐记录警告因此在 GUI 日志区按记录折叠
        /// （主界面/CLI：GUI 折叠显示，CLI 忽略分组键行为不变）；INFO 级别照常按普通行上报。</para>
        /// </summary>
        void ReportWithFile(string message, string level, string relPath)
        {
            switch ((level ?? string.Empty).ToUpperInvariant())
            {
                case "ERROR": ReportError(message); break;
                case "WARN":
                case "FATAL": ReportWarning(message, relPath); break;
                default: ReportLog(message); break;
            }
        }
    }

    /// <summary>
    /// DBR 文件工具方法
    /// </summary>
    public static class DbrHelper
    {
        public static string GetFieldValue(string dbrText, string fieldName)
        {
            string[] lines = dbrText.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(fieldName + ",", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = trimmed.Split(',');
                    if (parts.Length >= 2) return parts[1].Trim();
                }
            }
            return "";
        }

        public static string GetFieldValue(byte[] data, string fieldName)
        {
            string text = TemplateHelper.DecodeText(data);
            return GetFieldValue(text, fieldName);
        }

        /// <summary>
        /// 从 DBR 文本一次拆行、按需提取 记录名/描述/本地化标签字段 的公共结果。
        /// </summary>
        public sealed class DbrFieldExtraction
        {
            public string RecordName = "";
            public string Description = "";
            public Dictionary<string, string> TagsByField = null;
            public string FirstTagValue = "";

            public bool HasTags { get { return TagsByField != null && TagsByField.Count > 0; } }
        }

        /// <summary>
        /// 一次拆行收集 DBR 的 记录名/描述/标签 字段（忽略大小写，同字段重复时取首个匹配值）。
        /// recordName 由调用方透传（不写入文本）；tagFields 为 null 时使用默认 KnownTagFields，
        /// 传空数组或不传 includeTags=false 时不提取标签；传入数组则按该顺序决定 FirstTagValue。
        /// </summary>
        public static DbrFieldExtraction ParseDbrFields(
            string dbrText,
            string recordName = null,
            bool includeDescription = true,
            bool includeTags = true,
            string[] tagFields = null)
        {
            var result = new DbrFieldExtraction { RecordName = recordName ?? "" };
            if (string.IsNullOrEmpty(dbrText)) return result;

            string[] effectiveTags = includeTags ? (tagFields ?? LocalizationHelper.KnownTagFields) : null;
            var tagSet = effectiveTags != null && effectiveTags.Length > 0
                ? new HashSet<string>(effectiveTags.Where(t => !string.IsNullOrEmpty(t)), StringComparer.OrdinalIgnoreCase)
                : null;

            if (!includeDescription && tagSet == null)
                return result;

            string[] lines = dbrText.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                int comma = trimmed.IndexOf(',');
                if (comma <= 0) continue;
                string fieldName = trimmed.Substring(0, comma).Trim();
                if (string.IsNullOrEmpty(fieldName)) continue;

                if (includeDescription && string.IsNullOrEmpty(result.Description) &&
                    string.Equals(fieldName, "fileDescription", StringComparison.OrdinalIgnoreCase))
                {
                    result.Description = TrimDbrValue(trimmed.Substring(comma + 1));
                }

                if (tagSet != null && tagSet.Contains(fieldName))
                {
                    string val = TrimDbrValue(trimmed.Substring(comma + 1));
                    if (result.TagsByField == null)
                        result.TagsByField = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!result.TagsByField.ContainsKey(fieldName))
                        result.TagsByField[fieldName] = val;
                }
            }

            if (result.TagsByField != null && effectiveTags != null)
            {
                foreach (var tag in effectiveTags)
                {
                    if (result.TagsByField.TryGetValue(tag, out var tv) && !string.IsNullOrEmpty(tv))
                    {
                        result.FirstTagValue = tv;
                        break;
                    }
                }
            }
            return result;
        }

        private static string TrimDbrValue(string raw)
        {
            return raw.Trim().TrimEnd(',');
        }
    }

    /// <summary>
    /// DBR 检测的**模板类共享核心**（plan_20260923 §批C/批D）：包内检测（CheckArzContent）与
    /// 目录 dbr 文本检测（CheckDbrDirectory）共用同一套模板解析与值格式判定，保证两端口径一致。
    /// </summary>
    internal sealed class DbrCheckCore
    {
        private readonly Dictionary<string, TemplateNode> _templates;
        /// <summary>键 → 来源全名（"非标准模板路径"提示里报"来自哪个模板源"用；可为 null）</summary>
        private readonly Dictionary<string, string> _keySource;
        private readonly Dictionary<string, Dictionary<string, TemplateNode>> _fieldCache = new Dictionary<string, Dictionary<string, TemplateNode>>(StringComparer.Ordinal);
        private readonly Dictionary<string, TemplateNameResolver.Result> _resolveCache = new Dictionary<string, TemplateNameResolver.Result>(StringComparer.Ordinal);

        public DbrCheckCore(Dictionary<string, TemplateNode> templates, Dictionary<string, string> keySource = null)
        {
            _templates = templates;
            _keySource = keySource;
        }

        /// <summary>
        /// 解析模板名（带缓存）。口径见 <see cref="TemplateNameResolver"/>（**全项目唯一**）：
        /// 支持非标准写法（如 <c>mods/&lt;mod名&gt;/database/templates/x.tpl</c>），并回报"是否非标准"与"命中的规范键"。
        /// </summary>
        public TemplateNameResolver.Result Resolve(string tplName)
        {
            if (tplName == null) return new TemplateNameResolver.Result();
            if (_resolveCache.TryGetValue(tplName, out var cached)) return cached;
            var r = TemplateNameResolver.Resolve(_templates, tplName);
            _resolveCache[tplName] = r;
            return r;
        }

        /// <summary>命中的规范键属于哪个模板来源（如"内置模板 1.3"）；未知返回 null</summary>
        public string SourceLabelOf(TemplateNameResolver.Result r)
        {
            if (_keySource == null || string.IsNullOrEmpty(r.StandardKey)) return null;
            return _keySource.TryGetValue(r.StandardKey, out var label) ? label : null;
        }

        /// <summary>
        /// 取模板的字段表（字段名→变量节点，忽略大小写；含 includes）。每模板只线性收集一次，
        /// 替代逐字段 FindVariable 递归查找（6 万记录 × 数百字段 = O(n²) → O(n)）。
        /// <para><b>模板不存在时返回 null</b>（调用方据此报「模板不存在」并整条跳过该记录）。
        /// 注意与"模板存在但自身没有任何 variable"区分 —— 后者返回**空表**，不是 null；
        /// 否则"存在但零字段"会被误报成"模板不存在"。</para>
        /// </summary>
        public Dictionary<string, TemplateNode> GetTemplateFields(string tplName)
        {
            if (_fieldCache.TryGetValue(tplName, out var cached)) return cached;
            Dictionary<string, TemplateNode> dict = null;
            var r = Resolve(tplName);
            if (r.Template != null)
            {
                dict = new Dictionary<string, TemplateNode>(StringComparer.OrdinalIgnoreCase);
                CollectTemplateVariables(r.Template, dict);
            }
            _fieldCache[tplName] = dict;   // 允许存 null（= 不存在），下次直接命中，不再重复解析
            return dict;
        }

        private static void CollectTemplateVariables(TemplateNode node, Dictionary<string, TemplateNode> dict)
        {
            if (node.kind == "variable" && node.values.TryGetValue("name", out var n))
                dict[n] = node;
            foreach (var s in node.subitems) CollectTemplateVariables(s, dict);
            foreach (var inc in node.includes) CollectTemplateVariables(inc, dict);
        }

        /// <summary>
        /// 值格式校验：按模板 type 验证；数组按 ';' 分段逐段验证。
        /// 返回 null = 合法（含未知类型不判）；否则返回"非法段的值文本"。
        /// <para>**容差口径（对齐打包侧 TryAssign，arzfile.cs:2312-2362，零误报优先）**：
        /// ① 官方 DBR 文本所有数值强制 6 位小数（如 int 字段写 "0.000000"）→ int/bool 接受
        ///    "float 解析恰为整值"的写法（截断不丢值）；② int 还接受 0x 十六进制；
        /// ③ 只报**解析会丢值或变默认**的值：int "1.5"（截断丢 0.5）、任何解析不了的段（变 0/默认）。
        /// <para>注意（批C 实测结论）：int/real/bool 在包内为内联位存储，包内数据格式恒合法 ——
        /// 本判定真正能命中的是**目录 dbr 原始文本**（如 int 字段写了 "4x"）。</para>
        /// </summary>
        public static string FindBadFormatSegment(string ftype, string val)
        {
            if (string.IsNullOrEmpty(ftype) || string.IsNullOrEmpty(val)) return null;
            bool isInt = ftype == "int";
            bool isReal = ftype == "real";
            bool isBool = ftype == "bool";
            if (!isInt && !isReal && !isBool) return null;   // string/equation/file_* 等自由文本不判
            foreach (var raw in val.Split(';'))
            {
                string seg = raw.Trim();
                if (seg.Length == 0) continue;
                bool ok;
                if (isReal)
                {
                    // real：能按 float 解析即合法（TryAssign 解析不了才默认 0.0）
                    ok = float.TryParse(seg, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        || float.TryParse(seg, out _);
                }
                else if (isInt)
                {
                    if (long.TryParse(seg, out _)) ok = true;   // 纯整数
                    else if (seg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                             && int.TryParse(seg.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                        ok = true;                              // 十六进制（TryAssign 支持）
                    else if (float.TryParse(seg, NumberStyles.Float, CultureInfo.InvariantCulture, out float fi)
                             || float.TryParse(seg, out fi))
                        ok = fi == Math.Truncate(fi);           // 6 位小数约定：截断不丢值才合法（"1.5" 会丢 0.5 → 报告）
                    else ok = false;                            // 解析不了 → TryAssign 静默默认 0（值被改 → 报告）
                }
                else
                {
                    // bool："0"/"1"；官方 6 位小数写法 "0.000000"/"1.000000" 一律接受（零误报优先）
                    if (seg == "0" || seg == "1") ok = true;
                    else if (float.TryParse(seg, NumberStyles.Float, CultureInfo.InvariantCulture, out float fb)
                             || float.TryParse(seg, out fb))
                        ok = fb == 0f || fb == 1f;
                    else ok = false;                            // 解析不了 → TryAssign 静默默认 false
                }
                if (!ok) return seg;
            }
            return null;
        }
    }
}
