using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace GDPackEditor
{
    /// <summary>
    /// 编辑器会话临时目录（编辑中的包内容副本）。
    ///
    /// 位置：`%TEMP%\GDPackEditor_editor\<会话ID>\<包名>\...`（**保持系统临时目录不变**——软件目录可能只读、
    /// 可能位于 OneDrive 同步夹（会被反复扫描），不适合放临时文件）。
    ///
    /// 生命周期与清理（本次补充）：
    /// ① 每个会话在 `RootPath` 下写一个 `<会话ID>.owner` 标记（进程号 + 进程启动时间 + 包名），
    ///    **标记放在包目录之外**——因为 <see cref="GetModifiedRelativePaths"/> 基于磁盘扫描包目录，
    ///    多放任何文件都会被误当成"已修改文件"；
    /// ② 进程启动时 <see cref="CleanupStaleSessions"/> 清理陈旧会话：标记里的进程已不存在才删（不会误删多开实例的目录），
    ///    标记缺失/损坏时按目录年龄保守清理（> 1 天）；
    /// ③ 保存/固实成功后 <see cref="Clear"/>（清空本次临时内容，便于继续编辑）、关闭窗口时 <see cref="Dispose"/>，
    ///    删除失败不再静默，写一行 WARN 便于排查。
    /// </summary>
    public class TempSessionManager : IDisposable
    {
        /// <summary>会话目录的根（`%TEMP%\GDPackEditor_editor`）</summary>
        public static string RootPath => Path.Combine(Path.GetTempPath(), "GDPackEditor_editor");

        /// <summary>无 owner 标记时的保守清理阈值：目录最后写入时间早于该时长才删除</summary>
        private static readonly TimeSpan NoMarkerMaxAge = TimeSpan.FromDays(1);

        private bool _clearFailedLogged;   // Clear 失败只记一次日志，避免每次保存都刷屏

        public string SessionId { get; }
        public string BasePath { get; }
        public string ArchiveTempDir { get; }

        public TempSessionManager(string archiveFileName)
        {
            SessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss-ffff") + "-" + Random.Shared.Next(1000, 9999).ToString();
            BasePath = Path.Combine(RootPath, SessionId);
            string archiveName = Path.GetFileNameWithoutExtension(archiveFileName);
            ArchiveTempDir = Path.Combine(BasePath, archiveName);
            Directory.CreateDirectory(ArchiveTempDir);
            EnsureOwnerMarker();
        }

        public string GetTempFilePath(string relativePath)
        {
            string normalized = relativePath.Replace('/', '\\');
            string fullPath = Path.Combine(ArchiveTempDir, normalized);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            EnsureOwnerMarker();   // Clear() 会删标记，继续编辑时按需重建
            return fullPath;
        }

        public bool IsModified(string relativePath)
        {
            return File.Exists(GetTempFilePath(relativePath));
        }

        public string[] GetModifiedRelativePaths()
        {
            if (!Directory.Exists(ArchiveTempDir))
                return Array.Empty<string>();
            var files = new List<string>();
            CollectRelativePaths(ArchiveTempDir, ArchiveTempDir.Length + 1, files);
            return files.ToArray();
        }

        private void CollectRelativePaths(string dir, int baseLen, List<string> result)
        {
            foreach (string file in Directory.GetFiles(dir))
                result.Add(file.Substring(baseLen).Replace('\\', '/'));
            foreach (string sub in Directory.GetDirectories(dir))
                CollectRelativePaths(sub, baseLen, result);
        }

        public void Clear()
        {
            if (Directory.Exists(BasePath))
            {
                try
                {
                    Directory.Delete(BasePath, true);
                }
                catch (Exception ex)
                {
                    // 不再静默：被占用/被杀软锁住的残留目录会一直留着，必须能在日志里看到
                    if (!_clearFailedLogged)
                    {
                        _clearFailedLogged = true;
                        try { LogService.Warn(string.Format(Text("Log.TempClearFailed", "清理会话临时目录失败（可能被占用）：{0} - {1}"), BasePath, ex.Message)); } catch { }
                    }
                }
            }
            try { string marker = MarkerPath(SessionId); if (File.Exists(marker)) File.Delete(marker); } catch { }
        }

        public void Dispose()
        {
            Clear();
        }

        // ===== owner 标记与过期清理 =====

        /// <summary>owner 标记文件路径（与包目录**同级**，不会被 GetModifiedRelativePaths 扫到）</summary>
        private static string MarkerPath(string sessionId) => Path.Combine(RootPath, sessionId + ".owner");

        /// <summary>写入/补齐 owner 标记（进程号 + 进程启动时间 + 包名），供下次启动判断"该会话是否还有人用"</summary>
        private void EnsureOwnerMarker()
        {
            try
            {
                string marker = MarkerPath(SessionId);
                if (File.Exists(marker)) return;
                Directory.CreateDirectory(RootPath);
                long startTicks = 0;
                try { using var p = Process.GetCurrentProcess(); startTicks = p.StartTime.Ticks; } catch { }
                File.WriteAllLines(marker, new[]
                {
                    Environment.ProcessId.ToString(),
                    startTicks.ToString(),
                    Process.GetCurrentProcess().ProcessName,
                    Path.GetFileName(ArchiveTempDir),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
            catch { }
        }

        /// <summary>标记里的进程是否仍存活（同一进程名且启动时间吻合，避免 PID 复用误判）</summary>
        private static bool IsOwnerAlive(string sessionId)
        {
            try
            {
                string marker = MarkerPath(sessionId);
                if (!File.Exists(marker)) return false;
                var lines = File.ReadAllLines(marker);
                if (lines.Length < 2) return false;
                if (!int.TryParse(lines[0], out int pid)) return false;
                long.TryParse(lines[1], out long startTicks);
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return false;
                if (startTicks > 0)
                {
                    try { if (p.StartTime.Ticks != startTicks) return false; } catch { return false; }
                }
                return true;
            }
            catch
            {
                return false;   // 进程不存在 / 无权限读取 → 视为已结束
            }
        }

        /// <summary>
        /// 进程启动时清理陈旧会话目录（异常退出/断电遗留）。安全边界：
        /// 只处理 <see cref="RootPath"/> 之下的条目；owner 进程仍存活则**一律跳过**（多开实例安全）；
        /// 无标记时仅清理超过 <see cref="NoMarkerMaxAge"/> 的目录；删除失败只记一次 WARN，绝不影响启动。
        /// </summary>
        public static void CleanupStaleSessions()
        {
            try
            {
                string root = RootPath;
                if (!Directory.Exists(root)) return;

                int removed = 0, failed = 0;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (IsOwnerAlive(name)) continue;
                    bool hasMarker = File.Exists(MarkerPath(name));
                    if (!hasMarker)
                    {
                        try { if (DateTime.Now - Directory.GetLastWriteTime(dir) < NoMarkerMaxAge) continue; }
                        catch { continue; }
                    }
                    try
                    {
                        Directory.Delete(dir, true);
                        removed++;
                    }
                    catch
                    {
                        failed++;
                    }
                    try { string m = MarkerPath(name); if (File.Exists(m)) File.Delete(m); } catch { }
                }

                // 孤儿标记：目录已被手工删除，标记留下无意义
                foreach (var marker in Directory.GetFiles(root, "*.owner"))
                {
                    try
                    {
                        string name = Path.GetFileNameWithoutExtension(marker);
                        if (!Directory.Exists(Path.Combine(root, name))) File.Delete(marker);
                    }
                    catch { }
                }

                if (removed > 0)
                    LogService.Debug(string.Format(Text("Log.TempCleanup", "临时目录清理：已删除 {0} 个陈旧会话目录"), removed));
                if (failed > 0)
                    LogService.Warn(string.Format(Text("Log.TempCleanupFailed", "临时目录清理：{0} 个陈旧会话目录未能删除（可能仍被占用）"), failed));
            }
            catch
            {
                // 清理失败绝不影响启动
            }
        }

        private static string Text(string key, string fallback)
        {
            try { return LanguageManager.Instance.GetText(key, fallback); }
            catch { return fallback; }
        }

        /// <summary>
        /// 统计**仍被存活进程持有**的编辑器临时会话数（owner 标记里的进程还活着才算）。
        /// 供"修改编码设置"时提醒用户：这些会话里已生成的临时文件仍是旧设置编码。
        /// </summary>
        public static int CountLiveEditorSessions()
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(RootPath)) return 0;
                foreach (var dir in Directory.GetDirectories(RootPath))
                    if (IsOwnerAlive(Path.GetFileName(dir))) n++;
            }
            catch { }
            return n;
        }

        /// <summary>
        /// 是否存在 MOD 合并留下的临时文件（`%TEMP%\GDPackEditor_merge` 下任一文件）。
        /// 与 <see cref="CountLiveEditorSessions"/> 一起供编码设置变更提醒使用。
        /// </summary>
        public static bool HasMergeTempFiles()
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "GDPackEditor_merge");
                if (!Directory.Exists(root)) return false;
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    return true;
            }
            catch { }
            return false;
        }
    }
}
