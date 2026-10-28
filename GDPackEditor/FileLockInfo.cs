using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GDPackEditor
{
    /// <summary>
    /// 查询「哪个进程正在占用某个文件」—— Windows **Restart Manager** API（<c>rstrtmgr.dll</c>，系统自带）。
    ///
    /// <para>为什么用它：这是 Windows 上"查出谁锁着文件"的行业标准做法（VS / 各类安装程序同款），
    /// 只读查询、不需要管理员权限、不引入任何第三方二进制（系统 DLL，与单文件发布完全兼容）。</para>
    ///
    /// <para>用途：写包（打包/保存/合并/固实）替换目标包失败时，把原本笼统的
    /// 「常见原因：目标正被占用」升级为「具体进程名 + PID」，让用户能直接定位并关闭占用程序。</para>
    ///
    /// <para>能力边界（调用方文案需据此兜底）：
    /// ① 只能枚举"以该文件为资源持有句柄"的进程；② 跨会话/跨用户/对方提权运行时可能查不到
    /// （RM 返回 PermissionDenied）；③ 仅目录句柄、未注册的映射文件可能漏；
    /// ④ 会把杀毒软件、索引器、网盘同步一并列出（有参考价值）。</para>
    ///
    /// <para>安全约定：**任何失败都返回空结果，绝不抛异常**（本类只服务于失败提示，不能把失败提示本身变成新故障）。</para>
    /// </summary>
    internal static class FileLockInfo
    {
        private const int CCH_RM_SESSION_KEY = 32;
        private const int CCH_RM_MAX_APP_NAME = 255;
        private const int CCH_RM_MAX_SVC_NAME = 63;
        private const int ERROR_MORE_DATA = 234;
        private const int MaxLockers = 32;   // RmGetList 一次最多取回的进程数（防御异常值）

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        private enum RM_APP_TYPE
        {
            RmUnknownApp = 0, RmMainWindow = 1, RmOtherWindow = 2,
            RmService = 3, RmExplorer = 4, RmConsole = 5, RmCritical = 1000
        }

        private enum RmRebootReason
        {
            RmRebootReasonNone = 0,
            RmRebootReasonPermissionDenied = 1,
            RmRebootReasonSessionMismatch = 2,
            RmRebootReasonCriticalProcess = 4,
            RmRebootReasonCriticalService = 8,
            RmRebootReasonDetectedSelf = 16
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
            public string strServiceShortName;
            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, [In] RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded,
            ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref RmRebootReason lpdwRebootReasons);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        /// <summary>
        /// 占用该文件的一个进程。<see cref="IsSelf"/> 为真表示**本工具自身** —— 用于自我指认
        /// "内部句柄未释放导致自己锁住自己"这类回归（此前静默过滤自身，会把这类问题误导向
        /// "权限/第三方保护"的结论，反而掩盖真相）。
        /// </summary>
        public readonly struct FileLocker
        {
            public readonly string Name;
            public readonly int Pid;
            public readonly bool IsSelf;

            public FileLocker(string name, int pid, bool isSelf)
            {
                Name = name;
                Pid = pid;
                IsSelf = isSelf;
            }
        }

        /// <summary>
        /// 取占用指定文件的进程列表（**含本工具自身**，用 <see cref="FileLocker.IsSelf"/> 区分；按 PID 去重）。
        /// 无占用、查询失败、非 Windows 场景一律返回空列表。
        /// </summary>
        public static IReadOnlyList<FileLocker> GetLockers(string path)
        {
            var result = new List<FileLocker>();
            if (string.IsNullOrEmpty(path)) return result;

            uint handle = 0;
            try
            {
                var key = new StringBuilder(CCH_RM_SESSION_KEY + 1);
                if (RmStartSession(out handle, 0, key) != 0)
                {
                    handle = 0;
                    return result;
                }
                if (RmRegisterResources(handle, 1, new[] { path }, 0, null, 0, null) != 0)
                    return result;

                uint needed = 0, count = 0;
                var reason = RmRebootReason.RmRebootReasonNone;
                int rc = RmGetList(handle, out needed, ref count, null, ref reason);
                if (rc != ERROR_MORE_DATA && rc != 0) return result;   // 异常返回码 → 空结果
                if (needed == 0) return result;                        // 无进程占用该文件

                count = Math.Min(needed, MaxLockers);
                var infos = new RM_PROCESS_INFO[count];
                if (RmGetList(handle, out needed, ref count, infos, ref reason) != 0) return result;

                var seen = new HashSet<int>();
                int self = Environment.ProcessId;
                for (int i = 0; i < count; i++)
                {
                    int pid = infos[i].Process.dwProcessId;
                    if (pid <= 0) continue;
                    if (!seen.Add(pid)) continue;            // 去重
                    result.Add(new FileLocker(ResolveName(infos[i].strAppName, pid), pid, pid == self));
                }
            }
            catch
            {
                result.Clear();   // 查询失败不得影响主流程（失败提示本身不能变成新故障）
            }
            finally
            {
                if (handle != 0)
                {
                    try { RmEndSession(handle); } catch { }
                }
            }
            return result;
        }

        /// <summary>
        /// 把占用进程列表格式化为一行（形如 <c>a.exe (PID 1)、GDPackEditor (PID 2)（本工具自身）</c>）；
        /// 超过 <paramref name="maxCount"/> 时省略并追加"另 N 个"；无进程返回 null。
        /// </summary>
        public static string FormatLockList(IReadOnlyList<FileLocker> lockers, int maxCount = 8)
        {
            if (lockers == null || lockers.Count == 0) return null;

            var sb = new StringBuilder();
            int shown = Math.Min(lockers.Count, Math.Max(1, maxCount));
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append('、');
                sb.Append(string.Format(T("LOCK.Item", "{0} (PID {1})"), lockers[i].Name, lockers[i].Pid));
                if (lockers[i].IsSelf) sb.Append(T("LOCK.Self", "（本工具自身）"));
            }
            if (lockers.Count > shown)
                sb.Append(string.Format(T("LOCK.MoreSuffix", "，另 {0} 个"), lockers.Count - shown));
            return sb.ToString();
        }

        /// <summary>列表中是否存在**其它进程**占用（用于区分"外部占用"与"本工具自身未释放"）</summary>
        public static bool AnyForeign(IReadOnlyList<FileLocker> lockers)
        {
            if (lockers == null) return false;
            foreach (var l in lockers)
                if (!l.IsSelf) return true;
            return false;
        }

        /// <summary>进程名解析：优先 RM 给的 AppName（含路径则取文件名主干），否则用进程名兜底</summary>
        private static string ResolveName(string appName, int pid)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(appName))
                {
                    string name = appName.Trim();
                    if (name.IndexOf('\\') >= 0) name = Path.GetFileNameWithoutExtension(name);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch { }
            try { return Process.GetProcessById(pid).ProcessName; } catch { }
            return "?";
        }

        private static string T(string key, string fallback) => LanguageManager.Instance.GetText(key, fallback);
    }
}
