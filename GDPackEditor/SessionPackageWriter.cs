using System;
using System.IO;
using System.Text;
using System.Threading;

namespace GDPackEditor
{
    /// <summary>
    /// ARZ/ARC **写包落盘**的通用工具：把"直接写目标包"改成
    /// **「目标包同目录的临时包 → 同卷原子替换」**。
    ///
    /// 使用方：编辑器保存、固实（Compact）、打包 ARZ/ARC（全量·增量）、MOD 合并。
    /// 动机：直接写目标包时，中途取消/异常/替换失败会**把目标包截断甚至删掉**；
    /// 临时包建在目标包同目录则 `File.Move` 是同卷 rename —— 原子、无复制、最快，
    /// 也不存在"跨卷复制被 OneDrive/杀软/游戏句柄打断"的风险。
    ///
    /// 【注意】本类只管"怎么安全落盘"。编辑器保存**额外**采用增量写回语义
    /// （墓碑 + 包尾追加 + 免校验），该语义**仅适用于编辑器**，勿与打包页/CLI 混用。
    /// </summary>
    internal static class SessionPackageWriter
    {
        /// <summary>
        /// 把"写包"包成「同目录临时包 → 原子替换」。<paramref name="writeBody"/> 负责把内容写进给定流。
        /// <para>成功返回 true；失败返回 false 并给出 <paramref name="error"/>（含原因；替换失败时还含临时包路径）。</para>
        /// <para>· **写入阶段**失败/取消：删除半成品临时包，**目标包保持原样**。</para>
        /// <para>· **替换阶段**失败：**保留**完整临时包供用户抢救（路径写进 error），目标包保持原样。</para>
        /// <para>· 取消（<see cref="OperationCanceledException"/>）按原样向上抛，调用方照旧走取消分支；
        /// 此时半成品临时包已删，调用方**不得**再去删目标包。</para>
        /// </summary>
        public static bool WritePackageAtomic(string targetPath, Action<Stream> writeBody, out string error)
        {
            error = null;
            string tempFile = CreateTempPackagePath(targetPath);
            bool built = false;
            try
            {
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
                    writeBody(fs);
                built = true;

                ReplaceWithRetry(tempFile, targetPath);
                return true;
            }
            catch (OperationCanceledException)
            {
                try { if (!built && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                if (built)
                {
                    error = WriteFailureDiagnosis.Describe(ex, targetPath, tempBuilt: true, tempFile);
                }
                else
                {
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                    error = ex.Message;
                }
                return false;
            }
        }

        /// <summary>
        /// 替换原包文件失败（通常目标正被游戏或其它程序占用）。
        /// TempPath 为**已完整构建好**的临时包，调用方应保留它并告知用户，避免用户成果丢失。
        /// <para><see cref="Diagnostic"/> 为完整诊断块 = 占用进程列表（Restart Manager，见 <see cref="FileLockInfo"/>）
        /// + 错误码/属性/推断结论（见 <see cref="WriteFailureDiagnosis"/>）：让"目标正被占用或只读"具体化为可操作结论。</para>
        /// </summary>
        internal sealed class ReplaceFailedException : Exception
        {
            public string TargetPath { get; }
            public string TempPath { get; }
            /// <summary>占用该文件的进程说明（仅进程列表部分；非占用类失败为空）</summary>
            public string LockDescription { get; }
            /// <summary>完整诊断块（占用进程列表 + 错误码/属性/结论），供 UI 直接追加显示</summary>
            public string Diagnostic { get; }

            public ReplaceFailedException(string targetPath, string tempPath, Exception inner)
                // 二次包装（编辑器保存会再包一层）时沿用内层结果，避免重复查询与文案分叉
                : this(targetPath, tempPath, inner, BuildInfo(targetPath, tempPath, inner))
            {
            }

            private ReplaceFailedException(string targetPath, string tempPath, Exception inner, DiagnosisInfo info)
                : base(ComposeMessage(inner, info.Diagnosis), inner)
            {
                TargetPath = targetPath;
                TempPath = tempPath;
                Diagnostic = info.Diagnosis;
                LockDescription = info.LockLine;
            }

            /// <summary>一次性算好的诊断结果（避免重复查询 Restart Manager）</summary>
            private readonly struct DiagnosisInfo
            {
                public readonly string LockLine;
                public readonly string Diagnosis;
                public DiagnosisInfo(string lockLine, string diagnosis) { LockLine = lockLine; Diagnosis = diagnosis; }
            }

            /// <summary>
            /// 拼装诊断块（四行式，见 <see cref="WriteFailureDiagnosis"/>）：
            /// **仅当错误码属"占用/权限/取不到码"时才查占用进程**——实测独占占用与只读属性都返回 5，
            /// 需靠"是否查到占用进程 + 文件属性"区分；查到本工具自身则点名"内部未释放"。
            /// </summary>
            private static DiagnosisInfo BuildInfo(string targetPath, string tempPath, Exception inner)
            {
                if (inner is ReplaceFailedException rf && !string.IsNullOrEmpty(rf.Diagnostic))
                    return new DiagnosisInfo(rf.LockDescription, rf.Diagnostic);

                int code = WriteFailureDiagnosis.GetWin32Code(inner);
                bool needLockQuery = code == 5 || code == 32 || code == 33 || code <= 0;
                var lockers = needLockQuery ? FileLockInfo.GetLockers(targetPath) : null;
                string lockLine = FileLockInfo.FormatLockList(lockers) ?? string.Empty;
                // tempBuilt: true —— 替换阶段失败时临时包已建成（会打印"本次修改：未丢失，暂存至…"）
                string diag = WriteFailureDiagnosis.Describe(inner, targetPath, tempBuilt: true, tempPath, lockers);
                return new DiagnosisInfo(lockLine, diag);
            }

            /// <summary>把诊断块拼进异常文案（内层消息已含同一块时不重复追加）</summary>
            private static string ComposeMessage(Exception inner, string diagnosis)
            {
                string msg = inner?.Message ?? string.Empty;
                if (string.IsNullOrEmpty(diagnosis)) return msg;
                if (msg.Length > 0 && msg.IndexOf(diagnosis, StringComparison.Ordinal) >= 0) return msg;
                return msg.Length > 0 ? msg + "\n" + diagnosis : diagnosis;
            }
        }

        /// <summary>
        /// 用临时包替换目标包。文件被占用往往是瞬时的（杀软扫描、游戏释放句柄、网盘同步），
        /// 故失败自动重试三次（300ms / 1s / 2s）；仍失败则抛出
        /// <see cref="ReplaceFailedException"/>（内附占用进程查询结果）。
        /// 因为中间包由 <see cref="CreateTempPackagePath"/> 建在**目标包同目录**，
        /// 这里的 Move 是同卷 rename：原子、无复制、最快，也不存在"跨卷复制被中途打断"的风险。
        /// </summary>
        public static void ReplaceWithRetry(string tempFile, string archivePath)
        {
            // 同步重试是刻意的：Move 必须在调用方的保存链路内完成，改成 async 会扩散到
            // 编辑器/打包/合并的多个入口；退避等待只发生在重命名失败后的异常路径。
            int[] delays = { 300, 1000, 2000 };
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tempFile, archivePath, overwrite: true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < delays.Length)
                {
                    Thread.Sleep(delays[attempt]);
                }
                catch (Exception ex)
                {
                    // 重试耗尽：仅对"占用/权限"类异常做占用进程诊断并包装；
                    // 其它异常（路径非法等）原样抛出，避免给出误导性的"占用"提示
                    if (ex is IOException || ex is UnauthorizedAccessException)
                        throw new ReplaceFailedException(archivePath, tempFile, ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// 在**目标包同目录**生成中间包路径（`<包名>.GDPackEditor-tmp-<随机><扩展名>`）。
        /// 为什么不用 %TEMP%：跨卷时 File.Move 会退化为"复制+删除"，非原子且更易被
        /// OneDrive/杀软/游戏句柄打断——这正是"删除包内文件后保存失败"最相关的环节。
        /// 同目录构建 + 同卷 rename 可把风险降到最低，且失败时保留的中间包就在包旁边，便于用户抢救。
        /// </summary>
        public static string CreateTempPackagePath(string archivePath)
        {
            string dir = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            string name = Path.GetFileNameWithoutExtension(archivePath);
            string ext = Path.GetExtension(archivePath);
            return Path.Combine(dir, $"{name}.GDPackEditor-tmp-{Guid.NewGuid().ToString("N").Substring(0, 8)}{ext}");
        }

        /// <summary>
        /// 清理目标包所在目录中**陈旧的**中间包残留（上次异常退出/断电遗留）。
        /// 只删除本工具命名规则（`*.GDPackEditor-tmp-*`）且**最后写入时间早于一天**的文件，
        /// 因此不会误删另一个正在运行的实例正在写的中间包。失败静默（不影响打开包）。
        /// </summary>
        public static void CleanupStaleTempPackages(string archivePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(archivePath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                string pattern = Path.GetFileNameWithoutExtension(archivePath) + ".GDPackEditor-tmp-*";
                foreach (var f in Directory.GetFiles(dir, pattern))
                {
                    try
                    {
                        if (DateTime.Now - File.GetLastWriteTime(f) < TimeSpan.FromDays(1)) continue;
                        File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
