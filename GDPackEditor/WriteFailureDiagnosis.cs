using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GDPackEditor
{
    /// <summary>
    /// 写包失败后的**分类诊断**：按 Windows 反馈的错误码 + 文件属性 + 占用进程查询，
    /// 输出**四行式**简洁提示（用户定案版式）：
    /// <code>
    /// 目标文件：D:\...\x.arz
    /// 无法保存：错误码 5（拒绝访问…）
    /// 原因：目标文件带“只读”属性，替换被系统拒绝。
    /// 本次修改：未丢失，暂存至 D:\...\x.GDPackEditor-tmp-xxx.arz
    /// </code>
    ///
    /// <para>约定（用户定案）：**只给结论、不给解决方案**（由用户自行寻找解决办法）；
    /// 仅失败后诊断、不做预检、不做真写/真删探针、不自动改属性或 ACL；
    /// **任何探测失败都静默降级、永不抛异常**。</para>
    ///
    /// <para>证据强度：Win32 码 → 成因映射为文档结论；1295/4350 的码值以实测为准（未证实不写死）。
    /// 实测要点：**独占占用与只读属性返回的都是 5（ACCESS_DENIED）** → 必须结合占用进程查询下结论。</para>
    /// </summary>
    internal static class WriteFailureDiagnosis
    {
        // ── 常用 Win32 错误码 ──
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PATH_NOT_FOUND = 3;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_WRITE_PROTECT = 19;
        private const int ERROR_CRC = 23;
        private const int ERROR_SHARING_VIOLATION = 32;
        private const int ERROR_LOCK_VIOLATION = 33;
        private const int ERROR_HANDLE_DISK_FULL = 39;
        private const int ERROR_DISK_FULL = 112;
        private const int ERROR_INVALID_NAME = 123;
        private const int ERROR_FILENAME_EXCED_RANGE = 206;
        private const int ERROR_ELEVATION_REQUIRED = 740;
        private const int ERROR_DISK_QUOTA_EXCEEDED = 1295;
        private const int ERROR_FILE_CORRUPT = 1392;
        private const int ERROR_DISK_CORRUPT = 1393;
        private const int ERROR_FILE_OFFLINE = 4350;      // 云占位/脱机（码值以实测为准）
        private const int ERROR_IO_DEVICE = 1117;
        private const int ERROR_CANCELLED = 1223;
        private const int MaxPath = 260;

        /// <summary>
        /// 从异常链中提取 Win32 错误码（<c>HRESULT_FROM_WIN32</c> 惯例：低 16 位）。无法判定返回 -1（**不臆测**）。
        /// </summary>
        public static int GetWin32Code(Exception ex)
        {
            try
            {
                for (var e = ex; e != null; e = e.InnerException)
                {
                    uint hr = unchecked((uint)e.HResult);
                    if ((hr & 0xFFFF0000u) == 0x80070000u) return (int)(hr & 0xFFFFu);
                }
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// 生成四行式提示块（可直接作为失败提示正文）。**永不抛异常、永不返回 null**。
        /// </summary>
        /// <param name="ex">失败异常（沿 InnerException 找码）</param>
        /// <param name="targetPath">目标包路径</param>
        /// <param name="tempBuilt">临时包是否已成功建成（true ⇒ 打印"本次修改：未丢失，暂存至…"）</param>
        /// <param name="tempPath">临时包路径（tempBuilt 时必需）</param>
        /// <param name="lockers">占用进程列表（调用方已查则传入以复用，避免重复查询；null = 内部按需查询）</param>
        public static string Describe(Exception ex, string targetPath, bool tempBuilt, string tempPath = null,
                                      IReadOnlyList<FileLockInfo.FileLocker> lockers = null)
        {
            var sb = new StringBuilder();
            try
            {
                int code = GetWin32Code(ex);
                if (code == ERROR_CANCELLED) return string.Empty;   // 用户取消：不诊断（走取消分支）

                // 只有"占用码/权限码/取不到码"才需要查占用进程（实测 5 也可能是独占占用）
                if (lockers == null && (code == ERROR_ACCESS_DENIED || code == ERROR_SHARING_VIOLATION
                                        || code == ERROR_LOCK_VIOLATION || code <= 0))
                    lockers = FileLockInfo.GetLockers(targetPath);

                sb.Append(string.Format(T("DIAG.TargetFile", "目标文件：{0}"), targetPath ?? string.Empty));
                sb.Append('\n').Append(code > 0
                    ? string.Format(T("DIAG.CannotSave", "无法保存：错误码 {0}（{1}）"), code, DescribeCode(code))
                    : T("DIAG.CannotSaveNoCode", "无法保存：未能取得系统错误码。"));

                string reason = BuildReason(code, targetPath, lockers);
                if (!string.IsNullOrEmpty(reason))
                    sb.Append('\n').Append(string.Format(T("DIAG.Reason", "原因：{0}"), reason));

                if (tempBuilt && !string.IsNullOrEmpty(tempPath))
                    sb.Append('\n').Append(string.Format(T("DIAG.KeptTemp", "本次修改：未丢失，暂存至 {0}"), tempPath));
            }
            catch
            {
                // 诊断失败不得影响主流程：能输出多少算多少
            }
            return sb.ToString();
        }

        /// <summary>
        /// 供调用点**直接作为失败提示**使用的便捷入口：
        /// 替换失败 → 返回异常自带的诊断块；写盘类异常 → 生成四行块；其它异常 → 空串（调用方照旧走原消息）。
        /// </summary>
        public static string Prompt(Exception ex, string targetPath, string tempPath = null)
        {
            try
            {
                if (ex is SessionPackageWriter.ReplaceFailedException rf && !string.IsNullOrEmpty(rf.Diagnostic))
                    return rf.Diagnostic;
                if (!LooksLikeWriteFailure(ex)) return string.Empty;
                return Describe(ex, targetPath, tempBuilt: false, tempPath);
            }
            catch { return string.Empty; }
        }

        /// <summary>错误码 → 成因短句（未知码只给通用句，不臆测）</summary>
        public static string DescribeCode(int code)
        {
            switch (code)
            {
                case ERROR_FILE_NOT_FOUND: return T("DIAG.Code.2", "文件不存在");
                case ERROR_PATH_NOT_FOUND: return T("DIAG.Code.3", "路径不存在或磁盘未就绪");
                case ERROR_ACCESS_DENIED: return T("DIAG.Code.5", "拒绝访问（权限/只读/独占占用等多种可能）");
                case ERROR_WRITE_PROTECT: return T("DIAG.Code.19", "介质写保护");
                case ERROR_CRC: return T("DIAG.Code.23", "数据校验错误");
                case ERROR_SHARING_VIOLATION: return T("DIAG.Code.32", "共享冲突（被其它进程占用）");
                case ERROR_LOCK_VIOLATION: return T("DIAG.Code.33", "区域锁定冲突");
                case ERROR_HANDLE_DISK_FULL: return T("DIAG.Code.39", "磁盘已满");
                case ERROR_DISK_FULL: return T("DIAG.Code.112", "磁盘空间不足");
                case ERROR_INVALID_NAME: return T("DIAG.Code.123", "路径或文件名非法");
                case ERROR_FILENAME_EXCED_RANGE: return T("DIAG.Code.206", "路径过长");
                case ERROR_ELEVATION_REQUIRED: return T("DIAG.Code.740", "需要管理员权限");
                case ERROR_DISK_QUOTA_EXCEEDED: return T("DIAG.Code.1295", "超出磁盘配额（码值以实测为准）");
                case ERROR_FILE_CORRUPT: return T("DIAG.Code.1392", "文件损坏");
                case ERROR_DISK_CORRUPT: return T("DIAG.Code.1393", "磁盘结构损坏");
                case ERROR_FILE_OFFLINE: return T("DIAG.Code.4350", "文件处于脱机/云占位状态（码值以实测为准）");
                case ERROR_IO_DEVICE: return T("DIAG.Code.1117", "I/O 设备错误");
                default: return T("DIAG.CodeOther", "其它错误（未收录的码值不做猜测）");
            }
        }

        /// <summary>
        /// 判断异常链中是否含"写盘类"异常（<see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>）。
        /// <para>用途：某些 catch 同时兜底"解析失败"与"写盘失败"——只有写盘类才给写失败提示，
        /// 避免给模板/字段解析错误硬套诊断。</para>
        /// </summary>
        public static bool LooksLikeWriteFailure(Exception ex)
        {
            try
            {
                for (var e = ex; e != null; e = e.InnerException)
                    if (e is IOException || e is UnauthorizedAccessException) return true;
            }
            catch { }
            return false;
        }

        // ─────────────────────── 原因判定（只给结论，不给解决方案）───────────────────────

        private static string BuildReason(int code, string targetPath, IReadOnlyList<FileLockInfo.FileLocker> lockers)
        {
            if (lockers != null && lockers.Count > 0)
            {
                string list = FileLockInfo.FormatLockList(lockers);
                // 本工具自身持有句柄 = 内部未释放（自我指认，便于定位这类回归）
                return FileLockInfo.AnyForeign(lockers)
                    ? string.Format(T("DIAG.Reason.Locked", "目标文件正被其它进程占用：{0}"), list)
                    : string.Format(T("DIAG.Reason.SelfLocked", "本工具自身仍持有该文件句柄（内部未释放）：{0}"), list);
            }

            if (code == ERROR_SHARING_VIOLATION || code == ERROR_LOCK_VIOLATION)
                return T("DIAG.Reason.LockedNoProc", "目标文件被占用，但未能查到占用进程。");

            var (readOnly, system, offline, _) = ReadAttributes(targetPath);

            if (code == ERROR_ACCESS_DENIED)
            {
                if (readOnly || system)
                    return string.Format(T("DIAG.Reason.ReadOnly", "目标文件带“{0}”属性，替换被系统拒绝。"), DescribeAttrNames(readOnly, system));
                if (offline)
                    return T("DIAG.Reason.Offline", "目标文件是云盘“仅在线”占位文件。");
                return T("DIAG.Reason.AccessDenied", "写入被系统拒绝（权限或第三方保护）。");
            }
            if (code == ERROR_WRITE_PROTECT)
                return T("DIAG.Reason.WriteProtected", "目标位于写保护介质或只读共享。");
            if (code == ERROR_DISK_FULL || code == ERROR_HANDLE_DISK_FULL || code == ERROR_DISK_QUOTA_EXCEEDED)
            {
                string free = DescribeFreeSpace(targetPath);
                return T("DIAG.Reason.DiskFull", "磁盘空间不足（或超出配额）。")
                    + (free == null ? string.Empty : free);
            }
            if (code == ERROR_FILE_NOT_FOUND || code == ERROR_PATH_NOT_FOUND)
                return T("DIAG.Reason.PathMissing", "目标目录或文件不存在。");
            if (code == ERROR_FILENAME_EXCED_RANGE)
                return string.Format(T("DIAG.Reason.PathTooLong", "路径过长（{0} 字符，传统上限 260）。"), targetPath?.Length ?? 0);
            if (code == ERROR_INVALID_NAME)
                return T("DIAG.Reason.InvalidName", "路径或文件名含非法字符。");
            if (code == ERROR_FILE_OFFLINE || offline)
                return T("DIAG.Reason.Offline", "目标文件是云盘“仅在线”占位文件。");
            if (code == ERROR_FILE_CORRUPT || code == ERROR_DISK_CORRUPT || code == ERROR_CRC || code == ERROR_IO_DEVICE)
                return T("DIAG.Reason.MediaError", "磁盘或文件可能损坏。");
            if (code == ERROR_ELEVATION_REQUIRED)
                return T("DIAG.Reason.Elevation", "该位置需要管理员权限。");
            return null;   // 未收录 → 只显示错误码，不臆测原因
        }

        // ─────────────────────── 内部小工具（全部静默兜底）───────────────────────

        /// <summary>读取目标文件的相关属性（不存在或读取失败一律视为 false）</summary>
        private static (bool ReadOnly, bool System, bool Offline, bool ReparsePoint) ReadAttributes(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return (false, false, false, false);
                var a = File.GetAttributes(path);
                return ((a & FileAttributes.ReadOnly) != 0,
                        (a & FileAttributes.System) != 0,
                        (a & FileAttributes.Offline) != 0,
                        (a & FileAttributes.ReparsePoint) != 0);
            }
            catch { return (false, false, false, false); }
        }

        private static string DescribeAttrNames(bool readOnly, bool system)
        {
            if (readOnly && system) return T("DIAG.AttrReadOnly", "只读") + "、" + T("DIAG.AttrSystem", "系统");
            if (readOnly) return T("DIAG.AttrReadOnly", "只读");
            return T("DIAG.AttrSystem", "系统");
        }

        private static string DescribeFreeSpace(string targetPath)
        {
            try
            {
                string root = string.IsNullOrEmpty(targetPath) ? null : Path.GetPathRoot(targetPath);
                if (string.IsNullOrEmpty(root)) return null;
                var di = new DriveInfo(root);
                if (!di.IsReady) return null;
                return string.Format(T("DIAG.DiskFreeSuffix", "（所在磁盘剩余 {0:F2} GB）"),
                    di.AvailableFreeSpace / 1024.0 / 1024 / 1024);
            }
            catch { return null; }
        }

        private static string T(string key, string fallback) => LanguageManager.Instance.GetText(key, fallback);
    }
}
