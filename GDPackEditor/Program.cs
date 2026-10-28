using System;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Runtime.InteropServices;




namespace GDPackEditor
{   
    class Program
    {   
        

        // 单实例互斥锁：防止多开导致设置文件互相覆盖
        static Mutex _appMutex;
        // 重启请求标志：设置窗内触发重启（清除配置 / 主题缩放保存）时置位，
        // 主窗体关闭、Application.Run 返回后由 Main 统一启动新进程（见 Main 中 PendingRestart 处理）
        internal static bool PendingRestart;
        //基于0.2b5.2改造，基本已经彻底改造，底层API版本
        public const string VERSION = "lz0.3";
        //GUI版本
        public const string GUI_VERSION = "v4.4.0";
        static byte[] footer = new byte[16];
        /// <summary>日志入口：转发到 <see cref="LogService"/>（保留原 NLog Logger 的调用形状，47 处调用点无需改动）</summary>
        public static ArzLogger Log = new ArzLogger();
        public static List<string> strtable = null;
        public static ARZStrings astrtable = null;
        public static List<int> strrefcount = null;
        public static SortedDictionary<string, int> strsearchlist = null;
        public static HashSet<string> resfiles = null;
        public static HashSet<string> dbrfiles = null;
        [STAThread]
        static int Main(string[] args)
        {
            // 注册编码提供程序以支持GBK编码
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // 启动日志会话（写会话分隔行，并注册进程退出兜底落盘）；GUI 与 CLI 共用
            LogService.StartSession();

            // 清理编辑器上次异常退出遗留的临时会话目录（owner 进程仍存活的会被跳过，多开安全）
            TempSessionManager.CleanupStaleSessions();

            // 关闭触屏手势滚动，避免鼠标左键拖拽被误判为触摸滚动（桌面场景无需手势）
            AntdUI.Config.TouchEnabled = false;
            
            // 如果没有命令行参数，或者任意位置含 --gui 参数（不区分大小写），则启动GUI模式
            if (args.Length == 0 || args.Any(a => string.Equals(a, "--gui", StringComparison.OrdinalIgnoreCase)))
            {
                // 本程序为控制台子系统（Exe）：双击启动时系统会临时创建控制台窗口，立即脱离并隐藏；
                // 从 cmd/PowerShell 启动时仅脱离自身，不动 shell 自己的控制台窗口
                GuiDetachConsole();

                // 预应用 --language（若提供）：与 GUI 语言设置同步后启动界面；无效值静默忽略
                // （GUI 启动场景不打印任何 CLI 输出，导出类用法在 GUI 路径下也只是顺带执行）
                try { CliPresetLanguage(args, out _, out _, out _, out _); } catch { }

                // 单实例保护：多开时等待旧实例退出（兼容清除配置重启场景），仍失败则提示
                _appMutex = new Mutex(false, "GDPackEditor-2F3A5B1C", out _);
                bool gotLock = false;
                try { gotLock = _appMutex.WaitOne(TimeSpan.FromSeconds(3)); }
                catch (AbandonedMutexException) { gotLock = true; }
                catch { gotLock = false; }
                if (!gotLock)
                {
                    MessageBox.Show(
                        "程序已在运行，请勿重复启动。\nProgram is already running.",
                        "GDPackEditor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return 0;
                }

                Thread.CurrentThread.CurrentUICulture = new CultureInfo("zh");
                // 启用DPI感知（PerMonitorV2），使界面跟随Windows系统缩放且文字清晰（矢量渲染，非位图拉伸）
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                InstallGlobalExceptionHandlers();
                ArzArcEditorForm.AppDirectory = GetAppDirectory();
                // 确保内置 DBR 字段名字典条目存在（此时 SettingsManager 静态初始化已完成，避免循环依赖）
                SettingsManager.EnsureDefaultDbrKeyMappingEntries();
                // 确保内置文件夹注释字典条目存在（默认不启用）
                SettingsManager.EnsureDefaultFolderMappingEntries();
            AntdUI.Config.Animation = true;
            // 主题：明暗模式(跟随系统/亮/暗) + 主色方案，重启生效
            ThemeManager.ApplyTheme();
                // 统一界面缩放（开关+最大150%，重启生效）
                DpiManager.Apply();
                ThreadPool.QueueUserWorkItem(_ => CleanupBundleCache());
                // 退出前强制落盘，保证日志不残留未写出内容
                Application.ApplicationExit += (s, e) => LogService.Flush();
                Application.Run(new GuiMainForm());
                LogService.Flush();
                // 「清除配置」/ 主题缩放保存触发的重启：主循环已完全退出（单实例 Mutex 随进程
                // 释放，新进程 3 秒等待必定成功）后再启动新实例。不可用 Application.Restart()：
                // 它在模态对话框场景下关窗时序不可靠，新进程会被单实例保护拦截导致重启失败。
                if (PendingRestart)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = Application.ExecutablePath,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex) { try { LogService.Warn($"[restart] 重启失败：{ex.Message}"); } catch { } }
                }
                return 0;
            }

            // CLI：统一入口（命令语法与语义详见 plan_20260907_CLI修复.md）
            return CliMain(args);
        }

        private static int _unhandledErrorShown;

        /// <summary>
        /// GUI 全局异常兜底：async void 事件、后台任务与未捕获的 UI 异常都在这里落日志，
        /// 避免进程直接退出或弹系统错误框。只对 UI 线程异常弹一次提示，防止连环异常刷屏。
        /// </summary>
        private static void InstallGlobalExceptionHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowUnhandledError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex) LogUnhandled(ex);
                else LogUnhandled(new Exception(e.ExceptionObject?.ToString() ?? "Unknown unhandled error"));
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogUnhandled(e.Exception);
                e.SetObserved();
            };
        }

        private static void LogUnhandled(Exception ex)
        {
            try { LogService.Error("[unhandled] " + ex); } catch { }
        }

        private static void ShowUnhandledError(Exception ex)
        {
            LogUnhandled(ex);
            if (Interlocked.Exchange(ref _unhandledErrorShown, 1) != 0) return;

            try
            {
                string message = string.Format(
                    LanguageManager.Instance.GetText("App.UnhandledErrorMessage",
                        "程序遇到未处理的错误，详情已写入日志。\n\n{0}"),
                    ex?.Message ?? "");
                MessageBox.Show(
                    message,
                    LanguageManager.Instance.GetText("App.UnhandledErrorTitle", "未处理的错误"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        // 保留原来的GUI入口点作为兼容性支持
        [STAThread]
        static void MainGUI()
        {
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("zh");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            InstallGlobalExceptionHandlers();
            ArzArcEditorForm.AppDirectory = GetAppDirectory();
            AntdUI.Config.Animation = true;
            DpiManager.Apply();
            ThreadPool.QueueUserWorkItem(_ => CleanupBundleCache());
            Application.Run(new GuiMainForm());
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        static extern int GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, int nSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleProcessList(uint[] lpdwProcessList, uint dwProcessCount);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_HIDE = 0;

        // GUI 模式脱离控制台：双击启动（Exe 子系统）时系统临时建的控制台只有自身进程 attached，脱离后窗口自动关闭（再补一次隐藏）；
        // 从 cmd/PowerShell 启动时控制台属于 shell，仅脱离自身、绝不隐藏
        private static void GuiDetachConsole()
        {
            try
            {
                IntPtr hwnd = GetConsoleWindow();
                if (hwnd == IntPtr.Zero) return;
                uint myPid = GetCurrentProcessId();
                var pids = new uint[16];
                uint count = GetConsoleProcessList(pids, 16);
                bool onlyOurs = count >= 1;
                for (int i = 0; i < count && i < pids.Length; i++)
                {
                    if (pids[i] != myPid) { onlyOurs = false; break; }
                }
                FreeConsole();
                if (onlyOurs) ShowWindow(hwnd, SW_HIDE);
            }
            catch { }
        }

        static string GetAppDirectory()
        {
            var sb = new StringBuilder(260);
            GetModuleFileName(IntPtr.Zero, sb, sb.Capacity);
            var path = sb.ToString();
            if (!string.IsNullOrEmpty(path))
                return Path.GetDirectoryName(path);
            return AppContext.BaseDirectory;
        }

        /// <summary>
        /// 清理单文件发布运行时解压缓存中残留的旧目录（保留1天内的本次使用目录）。
        /// .NET 单文件应用（PublishSingleFile）启动时会把 native 库等内容解压到
        /// %TEMP%\.net\GDPackEditor，正常退出会自删，异常退出（强杀/崩溃/被杀软锁定）会残留累积。
        /// 此方法在后台线程执行，删除失败静默忽略，不影响启动。
        /// </summary>
        static void CleanupBundleCache()
        {
            try
            {
                var cacheRoot = Path.Combine(Path.GetTempPath(), ".net", "GDPackEditor");
                if (!Directory.Exists(cacheRoot)) return;
                var cutoff = DateTime.Now.AddDays(-1);
                foreach (var dir in Directory.GetDirectories(cacheRoot))
                {
                    try
                    {
                        if (new DirectoryInfo(dir).LastWriteTime < cutoff)
                            Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static string DbrFileToRecName(string dbrfile, string packfolder)
        {
            string recname = dbrfile.Substring(packfolder.Length).ToLower().Replace(Path.DirectorySeparatorChar, '/').TrimStart('/');
            if (recname.StartsWith("database/")) recname = recname.Substring("database/".Length);
            return recname;
        }

        // 2025-8-27修改，当遇到无法解析的模板时，将无法解析的模板记录下来，跳过
        public static Dictionary<string, TemplateNode> BuildTemplateDict(string[] tfolders, Dictionary<string, TemplateNode> templates = null)
        {
            if (templates == null) templates = new Dictionary<string, TemplateNode>();
            foreach (string tfolder in tfolders)
            {
                string tfullpath = Path.GetFullPath(tfolder).TrimEnd(Path.DirectorySeparatorChar);
                // 文件夹名以 template 开头（含 templates，2026-10-14 放宽：导入限制同步改为 template 不带 s）
                // → 该目录整体视为 templates 目录本身：模板键一律 = database/templates/<文件夹内相对路径>
                //（template、templates、templates2、templates-官方 等同样成立）；
                // 恰好叫 templates 时与旧的"上跳一级取相对路径"结果完全一致（零行为变化）。
                bool isTemplatesDir = Path.GetFileName(tfullpath).StartsWith("template", StringComparison.OrdinalIgnoreCase);
                if (!isTemplatesDir)
                {
                    // 非 templates 目录：保留旧的"可能是错误的模板基础文件夹"提示
                    //（templates* 目录自身即是模板目录，不再按父目录推导，也就不会误报）
                    if (!Directory.Exists(Path.Combine(tfullpath, "database")) && !Directory.Exists(Path.Combine(tfullpath, "templates")))
                    {
                        Log.Info($"{LanguageManager.Instance.GetText("LOG.PossiblyWrongTemplateBaseFolder", "可能是错误的模板基础文件夹")} \"{tfullpath}\"");
                    }
                }

                string[] alltemplates = Directory.GetFiles(tfullpath, "*.tpl", SearchOption.AllDirectories);
                foreach (string tfile in alltemplates)
                {
                    try
                    {
                        // 按实际编码读取（UTF-8/GBK 自动判定），避免模板文件中文乱码
                        string[] tstrings = TemplateHelper.ReadDbrLines(tfile);
                        string tpath = tfile.Substring(tfullpath.Length).ToLower().Replace(Path.DirectorySeparatorChar, '/');
                        if (tpath.StartsWith("/")) tpath = tpath.Substring(1);

                        // templates* 目录 → 补上 templates/ 路径段（该段来自文件夹名，保证键落回 database/templates/…）
                        if (isTemplatesDir)
                            tpath = "templates/" + tpath;

                        if (!tpath.StartsWith("database/"))
                        {
                            tpath = "database/" + tpath;
                        }

                        TemplateNode ntemplate = new TemplateNode(null, tpath);
                        ntemplate.ParseNode(tstrings, 0);
                        templates[tpath] = ntemplate;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"\"{tfile}\" {LanguageManager.Instance.GetText("LOG.TemplateParseError", "模板解析错误，将跳过：")} {ex.Message}");
                        continue;
                    }
                }
            }
            return templates;
        } // BuildTemplateDict



        /// <summary>
        /// 已详细输出过日志的内置模板版本（进程内）。内置模板是程序集内嵌资源、内容确定，
        /// 同一版本重复加载的结果与日志完全相同，因此只在首次输出明细，避免每打开一个包就重复刷屏。
        /// </summary>
        private static readonly HashSet<string> _builtInTemplateLogEmitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>标记内置模板日志：返回本次是否为该版本的首次加载（首次 true，后续同版本 false）</summary>
        public static bool MarkBuiltInTemplateLog(string version)
        {
            lock (_builtInTemplateLogEmitted)
                return _builtInTemplateLogEmitted.Add(version ?? string.Empty);
        }

        /// <summary>
        /// 模板"引用缺失"警告抑制开关：重复加载同版本内置模板时置位，
        /// 避免同一批警告在每次打开包时重复输出（仅抑制日志，不影响模板加载与引用解析）。
        /// </summary>
        public static bool SuppressTemplateWarnings { get; set; }

        // ─────────────────────────── 内置模板包（templates\templates-*.arc 内嵌） ───────────────────────────

        /// <summary>内置模板包标识符前缀（templates 目录下 templates-*.arc 通配内嵌，文件名去扩展名即标识符）</summary>
        private const string BuiltInTemplatePrefix = "templates-";

        /// <summary>
        /// 动态发现内置模板包：扫描程序集资源清单中的 templates-*.arc，
        /// 返回标识符列表（文件名去扩展名，如 templates-01-1.2.1.6-tt300），按文件名自然排序。
        /// </summary>
        public static List<string> GetBuiltInTemplateVersions()
        {
            var list = new List<string>();
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                // 资源名形如 GDPackEditor.templates.templates-01-1.2.1.6-tt300.arc。
                // 注意：文件名本身可能含点（版本号 1.2.1.6），不能按"最后一个点"切分，
                // 必须按固定前缀"命名空间.templates."截取文件名部分（内嵌目录固定为 templates）。
                string nsPrefix = asm.GetName().Name + ".templates.";
                foreach (var res in asm.GetManifestResourceNames())
                {
                    if (!res.EndsWith(".arc", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!res.StartsWith(nsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string id = res.Substring(nsPrefix.Length);                     // templates-01-1.2.1.6-tt300.arc
                    id = id.Substring(0, id.Length - ".arc".Length);                // templates-01-1.2.1.6-tt300
                    if (id.StartsWith(BuiltInTemplatePrefix, StringComparison.OrdinalIgnoreCase))
                        list.Add(id);
                }
            }
            catch { }
            list.Sort(NaturalFileNameCompare);
            return list;
        }

        /// <summary>
        /// 内置包默认是否勾选：文件名含 official 的包默认勾选，其余默认不勾（仅生成默认配置时生效，
        /// 用户之后的勾选/导入状态一律保留）。若当前没有任何 official 包，则全部默认勾选，
        /// 避免新装环境一个内置模板都不加载。
        /// </summary>
        public static bool BuiltInDefaultChecked(List<string> packs, string id)
        {
            if (packs == null || packs.Count == 0 || string.IsNullOrEmpty(id)) return false;
            if (!packs.Any(p => p.Contains("official", StringComparison.OrdinalIgnoreCase)))
                return true;   // 没有 official 包：退回"全部默认勾选"，保证内置模板可用
            return id.Contains("official", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>内置模板显示短名：标识符去掉 templates- 前缀（如 01-1.2.1.6-tt300），用于备注与日志显示</summary>
        public static string BuiltInShortName(string id)
        {
            if (string.IsNullOrEmpty(id)) return id ?? "";
            return id.StartsWith(BuiltInTemplatePrefix, StringComparison.OrdinalIgnoreCase)
                ? id.Substring(BuiltInTemplatePrefix.Length) : id;
        }

        /// <summary>
        /// 文件名自然排序比较器：数字段按数值比较（01 &lt; 02 &lt; 10），其余按 OrdinalIgnoreCase。
        /// 自写实现、无第三方依赖；对序号手工补零的命名同样正确。
        /// </summary>
        private static int NaturalFileNameCompare(string a, string b)
        {
            int ia = 0, ib = 0;
            while (ia < a.Length && ib < b.Length)
            {
                char ca = a[ia], cb = b[ib];
                if (char.IsDigit(ca) && char.IsDigit(cb))
                {
                    long na = 0, nb = 0;
                    while (ia < a.Length && char.IsDigit(a[ia])) { na = na * 10 + (a[ia] - '0'); ia++; }
                    while (ib < b.Length && char.IsDigit(b[ib])) { nb = nb * 10 + (b[ib] - '0'); ib++; }
                    if (na != nb) return na.CompareTo(nb);
                }
                else
                {
                    int cmp = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
                    if (cmp != 0) return cmp;
                    ia++; ib++;
                }
            }
            return (a.Length - ia).CompareTo(b.Length - ib);
        }

        /// <summary>
        /// 从嵌入式资源加载内置模板
        /// </summary>
        /// <param name="templates">模板字典，加载的模板将添加到这里</param>
        /// <param name="version">内置包标识符 = 文件名去扩展名（如 templates-01-1.2.1.6-tt300）；null = 取文件名自然序第一个</param>
        /// <param name="logDetail">是否详细输出加载日志；null = 自动（首次加载该版本时输出）</param>
        /// <returns>加载的模板数量</returns>
        public static int LoadBuiltInTemplates(Dictionary<string, TemplateNode> templates, string version = null, bool? logDetail = null)
        {
            if (string.IsNullOrEmpty(version))
                version = GetBuiltInTemplateVersions().FirstOrDefault();
            bool verbose = logDetail ?? MarkBuiltInTemplateLog(version);
            int count = 0;
            try
            {
                // 获取当前程序集
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();

                // 获取所有嵌入式资源名称
                var resourceNames = assembly.GetManifestResourceNames();

                // 按标识符匹配嵌入式ARC资源（资源全名形如 GDPackEditor.templates.templates-01-1.2.1.6-tt300.arc）
                string targetSuffix = version + ".arc";
                string arcResourceName = resourceNames.FirstOrDefault(r => r.EndsWith(targetSuffix, StringComparison.OrdinalIgnoreCase));
                if (arcResourceName != null)
                {
                    // 内部实现细节（资源名/后缀匹配），不属于用户可见日志，不再输出
                    try
                    {
                        // 读取ARC文件资源
                        using (Stream arcStream = assembly.GetManifestResourceStream(arcResourceName))
                        {
                            if (arcStream != null)
                            {
                                ARCFile arcFile = new ARCFile();
                                arcFile.ReadStream(arcStream);
                                
                                // 遍历ARC文件中的所有条目
                                foreach (ARCTocEntry aentry in arcFile.toc)
                                {
                                    try
                                    {
                                        string entryname = aentry.GetEntryString(arcFile.strs);
                                        // 只处理.tpl文件
                                        if (!string.IsNullOrEmpty(entryname) && entryname.ToLower().EndsWith(".tpl"))
                                        {
                                            List<string> strlist = new List<string>();
                                            using (MemoryStream tplmem = new MemoryStream())
                                            {
                                                arcFile.UnpackToStream(aentry, tplmem);
                                                tplmem.Seek(0, SeekOrigin.Begin);
                                                using (StreamReader sr = new StreamReader(tplmem))
                                                {
                                                    while (!sr.EndOfStream)
                                                    {
                                                        strlist.Add(sr.ReadLine());
                                                    }
                                                }
                                            }

                                            if (strlist.Count > 0)
                                            {
                                                // 解析模板
                                            TemplateNode node = new TemplateNode(null, entryname);
                                            node.ParseNode(strlist.ToArray());
                                             
                                            // 键规范化（全项目唯一口径 TemplateNameResolver）：条目内部自带 mods/ 前缀等
                                            // 非标准写法也归一到 database/templates/…；对标准条目结果与旧的"补前缀"完全一致
                                            string templateKey = TemplateNameResolver.CanonicalKey(entryname);
                                             
                                            // 添加到模板字典中（后者覆盖前者，实现从上到下优先级递增）
                                            templates[templateKey] = node;
                                            count++;
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        if (verbose)
                                            Log.Error("Failed to load built-in template: {0}. Error: {1}", aentry.GetEntryString(arcFile.strs), ex.Message);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (verbose)
                            Log.Error("Error loading built-in ARC template file: {0}", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                if (verbose)
                    Log.Error("Error loading built-in templates: {0}", ex.Message);
            }
            
            if (count == 0)
            {
                if (verbose) Log.Warn("No built-in templates loaded for version {0}", version);
            }
            // 加载成功的数量统计不再在此输出：由 LoadTemplates 汇总"按勾选来源逐个 + 合并后有效数"的
            // 一条总览日志（见 LoadTemplates 的 sources 返回值与打包/打开包处的输出）。

            return count;
        }

        /// <summary>
        /// 从ARC文件中加载模板
        /// </summary>
        /// <param name="arcFilePath">ARC文件路径</param>
        /// <param name="templates">模板字典，加载的模板将添加到这里</param>
        /// <returns>加载的模板数量</returns>
        public static int LoadTemplatesFromArcFile(string arcFilePath, Dictionary<string, TemplateNode> templates)
        {
            int count = 0;
            try
            {
                if (!File.Exists(arcFilePath))
                {
                    Log.Warn("ARC template file not found: {0}", arcFilePath);
                    return 0;
                }

                byte[] arcData = File.ReadAllBytes(arcFilePath);
                using (MemoryStream ms = new MemoryStream(arcData))
                {
                    ARCFile arcFile = new ARCFile();
                    arcFile.ReadStream(ms);

                    foreach (ARCTocEntry aentry in arcFile.toc)
                    {
                        try
                        {
                            string entryname = aentry.GetEntryString(arcFile.strs);
                            if (!string.IsNullOrEmpty(entryname) && entryname.ToLower().EndsWith(".tpl"))
                            {
                                List<string> strlist = new List<string>();
                                using (MemoryStream tplmem = new MemoryStream())
                                {
                                    arcFile.UnpackToStream(aentry, tplmem);
                                    tplmem.Seek(0, SeekOrigin.Begin);
                                    using (StreamReader sr = new StreamReader(tplmem))
                                    {
                                        while (!sr.EndOfStream)
                                            strlist.Add(sr.ReadLine());
                                    }
                                }

                                if (strlist.Count > 0)
                                {
                                    TemplateNode node = new TemplateNode(null, entryname);
                                    node.ParseNode(strlist.ToArray());

                                    string templateKey = TemplateNameResolver.CanonicalKey(entryname);

                                    // 后者覆盖前者，实现从上到下优先级递增
                                    templates[templateKey] = node;
                                    count++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Failed to load template from ARC: {0}. Error: {1}", aentry.GetEntryString(arcFile.strs), ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error loading templates from ARC file \"{0}\": {1}", arcFilePath, ex.Message);
            }
            return count;
        }

        /// <summary>
        /// 统一加载模板：按TemplateEntries列表从上到下依次加载（后者覆盖前者同名模板）
        /// 遵循用户定义的顺序和启用状态，实现"可多选，从上到下依次生效"。
        /// </summary>
        /// <param name="templateEntries">模板条目列表（可选，可为null）</param>
        /// <returns>(templates字典, 内置数量, 自定义数量)</returns>
        /// <summary>
        /// 单个模板来源的统计。
        /// <para><b>Label</b>=给用户提示消息用的全名（如"内置模板 1.3"）；
        /// <b>ShortName</b>=给"模板来源"汇报用的短名（如"1.3"、"D3"）；
        /// <b>Kind</b>=分组用（"builtin" / "custom"）；
        /// <b>Count</b>=该来源**加载**了几个；
        /// <b>EffectiveCount</b>=该来源在**合并后的最终字典**里生效了几个（同名被后面的来源覆盖则不计入）。
        /// 各来源 EffectiveCount 之和 = 最终有效模板数。</para>
        /// </summary>
        public sealed class TemplateSourceCount
        {
            public string Label;         // 用户提示用全名
            public string ShortName;     // 汇报用短名（内置=包短名，自定义=备注/文件名）
            public int Count;            // 该来源加载的模板数
            public int EffectiveCount;   // 合并后该来源最后覆盖生效的模板数（各来源之和=最终有效数）
        }

        public static (Dictionary<string, TemplateNode> templates, int builtInCount, int customCount,
            List<TemplateSourceCount> sources, Dictionary<string, string> keySource) LoadTemplates(List<TemplateEntry> templateEntries = null)
        {
            var templates = new Dictionary<string, TemplateNode>();
            int builtInCount = 0;
            int customCount = 0;
            var sources = new List<TemplateSourceCount>();
            // 最终字典里"每个键属于哪个来源"：按**最后写入者**记账 —— 后加载的同名模板覆盖先加载的，归属跟后者
            var keySourceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            bool builtInDetail = true;   // 本次内置模板加载是否详细输出日志（首次 true）

            // 按TemplateEntries用户顺序依次加载（从上到下，后加载覆盖先加载）
            if (templateEntries != null && templateEntries.Count > 0)
            {
                foreach (var entry in templateEntries)
                {
                    if (!entry.IsChecked) continue;

                    // 本来源加载前的快照：加载后凡是"新出现的键"或"值被换掉的键"，都归本来源
                    var before = new Dictionary<string, TemplateNode>(templates, StringComparer.Ordinal);

                    switch (entry.Type)
                    {
                        case "BuiltIn":
                            if (string.IsNullOrEmpty(entry.BuiltInVersion)) continue;
                            // 内置模板同版本只在进程内首次详细输出日志（详见 MarkBuiltInTemplateLog）
                            builtInDetail = MarkBuiltInTemplateLog(entry.BuiltInVersion);
                            int cb = LoadBuiltInTemplates(templates, entry.BuiltInVersion, builtInDetail);
                            builtInCount += cb;
                            sources.Add(new TemplateSourceCount
                            {
                                Label = string.Format(T("Template.LoadedBuiltIn", "内置模板 {0}"), BuiltInShortName(entry.BuiltInVersion)),
                                ShortName = BuiltInShortName(entry.BuiltInVersion),
                                Count = cb
                            });
                            break;

                        case "Arc":
                            if (string.IsNullOrEmpty(entry.Path)) continue;
                            int ca = LoadTemplatesFromArcFile(entry.Path, templates);
                            customCount += ca;
                            sources.Add(new TemplateSourceCount
                            {
                                Label = CustomTemplateSourceLabel(entry),
                                ShortName = CustomTemplateShortName(entry),
                                Count = ca
                            });
                            break;

                        case "Folder":
                            if (string.IsNullOrEmpty(entry.Path) || !Directory.Exists(entry.Path)) continue;
                            int cf = 0;
                            try
                            {
                                cf = Directory.GetFiles(Path.GetFullPath(entry.Path), "*.tpl", SearchOption.AllDirectories).Length;
                            }
                            catch { }
                            customCount += cf;
                            templates = BuildTemplateDict(new[] { entry.Path }, templates);
                            sources.Add(new TemplateSourceCount
                            {
                                Label = CustomTemplateSourceLabel(entry),
                                ShortName = CustomTemplateShortName(entry),
                                Count = cf
                            });
                            break;

                        default:
                            continue;
                    }

                    // 记账：本来源新增或被它覆盖的键 → 归本来源
                    int sourceIndex = sources.Count - 1;
                    foreach (var kv in templates)
                        if (!before.TryGetValue(kv.Key, out var oldTpl) || !ReferenceEquals(oldTpl, kv.Value))
                            keySourceIndex[kv.Key] = sourceIndex;
                }
            }

            // 每个来源的"最终生效数"：合并后仍属于它的键数（各来源之和 = 最终有效模板数）
            foreach (var kv in keySourceIndex)
                if (kv.Value >= 0 && kv.Value < sources.Count) sources[kv.Value].EffectiveCount++;

            // 内置模板（同版本）重复加载时，同一批"引用缺失"警告已输出过，此处抑制重复刷屏
            bool prevSuppress = SuppressTemplateWarnings;
            SuppressTemplateWarnings = !builtInDetail;
            try
            {
                foreach (var tpln in templates)
                    tpln.Value.FillIncludes(templates);
            }
            finally
            {
                SuppressTemplateWarnings = prevSuppress;
            }

            // 键 → 来源全名（供"非标准模板路径"提示里的"已按「内置模板 1.3」匹配"使用）
            var keySource = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in keySourceIndex)
                if (kv.Value >= 0 && kv.Value < sources.Count) keySource[kv.Key] = sources[kv.Value].Label;
            LastTemplateKeySource = keySource;

            return (templates, builtInCount, customCount, sources, keySource);
        }

        /// <summary>
        /// 最近一次 <see cref="LoadTemplates"/> 产出的"键 → 来源全名"表。
        /// <para>供**只拿到模板表、拿不到来源**的地方使用（例如调用方已把 templates 传进检测方法）。
        /// **仅用于提示文案**（"已按「内置模板 1.3」匹配"），不参与任何判定。</para>
        /// </summary>
        public static Dictionary<string, string> LastTemplateKeySource { get; private set; }

        /// <summary>
        /// 自定义模板来源的短名：有备注用备注，否则用路径的文件名部分。
        /// <para>⚠ 必须**去掉换行并 Trim**：备注是表格里手填的，实测出现过 `"官方\r\n"`
        /// （结尾带 CRLF）。它会被拼进"模板来源：…"汇总行，而日志面板是"一条日志 = 一个显示行"，
        /// 一旦文本里带换行，该条日志就会渲染成两行 → 日志行号与内部条目错位
        /// （表现为：双击日志跳转打开的是**下一行**的文件、链接着色串行）。</para>
        /// </summary>
        private static string CustomTemplateShortName(TemplateEntry entry)
        {
            string name = !string.IsNullOrWhiteSpace(entry.Note) ? entry.Note
                : (!string.IsNullOrWhiteSpace(entry.Path) ? Path.GetFileNameWithoutExtension(entry.Path) : "");
            return SanitizeSingleLine(name);
        }

        /// <summary>
        /// 压成**单行**显示文本：去掉 CR/LF（换成空格）、Trim。
        /// 用于所有会被拼进"一行日志/一行提示"的用户可填文本（备注等），防止一条日志被渲染成多行。
        /// </summary>
        public static string SanitizeSingleLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.IndexOf('\r') >= 0) s = s.Replace("\r", " ");
            if (s.IndexOf('\n') >= 0) s = s.Replace("\n", " ");
            return s.Trim();
        }

        /// <summary>自定义模板来源的全名（提示消息用）</summary>
        private static string CustomTemplateSourceLabel(TemplateEntry entry)
            => string.Format(T("Template.LoadedCustom", "自定义模板（{0}）"), CustomTemplateShortName(entry));

        /// <summary>
        /// 生成"模板来源"汇总文本，形如：
        /// <c>模板来源：02-1.3.1.1-official 有效0/818、DT模板修复 有效2/819、03-1.3.1.1-tt300 有效867/867 ｜ 最终有效 869 个</c>
        /// <para>来源**严格按设置里的勾选顺序**逐个列出（不按内置/自定义分组——分组会打乱实际加载顺序，
        /// 造成"谁覆盖谁"的误解）。每个来源显示"有效X/加载Y"：X = 该来源在合并后最终字典里**最后覆盖生效**的模板数
        /// （各来源有效数之和 = 末尾"最终有效"），Y = 该来源**加载**了几个模板；
        /// 同名模板按此顺序被后面的来源覆盖。</para>
        /// </summary>
        public static string FormatTemplateSources(List<TemplateSourceCount> sources, int finalCount)
        {
            var parts = new List<string>();
            if (sources != null)
            {
                foreach (var s in sources)
                {
                    if (s == null) continue;
                    parts.Add($"{s.ShortName} 有效{s.EffectiveCount}/{s.Count}");
                }
            }

            string prefix = parts.Count > 0 ? string.Join("、", parts) + " ｜ " : "";

            return string.Format(T("LOG.TemplateCounts", "模板来源：{0}最终有效 {1} 个"), prefix, finalCount);
        }

        #region CLI（2026-09 重做，命令语法与语义详见 plan_20260907_CLI修复.md）

        // WinExe 默认无控制台句柄：交互式黑窗口下需附加父控制台，输出才可见；重定向场景原生可用
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();
        private const int ATTACH_PARENT_PROCESS = -1;

        private static string T(string key, string zhDefault)
        {
            return LanguageManager.Instance.GetText(key, zhDefault);
        }

        private static void CliOut(string message) { Console.WriteLine(message); }
        private static void CliErr(string message) { Console.Error.WriteLine(message); }

        /// <summary>CLI 版本横幅：软件名_v4.0(API_lz0.3)。GUI 版本放前、底层 API 版本放括号；
        /// name 为工具名（--version 取进程名、--help 传固定名）。两处共用同一格式，避免不一致。</summary>
        private static string CliVersionBanner(string name) => $"{name}_{GUI_VERSION}(API_{VERSION})";

        private enum CliPkgType { Arz, Arc }

        /// <summary>--language 这次做了什么（None = 参数没出现）</summary>
        private enum CliLanguageAction { None, SetLanguage, ExportPacks }

        private sealed class CliArgs
        {
            public readonly List<string> Positional = new List<string>();
            public bool Show;
            public bool Update;
            public string Tpl;
        }

        private sealed class CliPkgStats
        {
            public long Size;
            public int Total, Valid, Tombstones;
            public List<string> Names;
        }

        // CLI 统一入口：附加控制台 → Ctrl+C 取消令牌 → 分发命令
        public static int CliMain(string[] args)
        {
            try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
            try { if (!Console.IsOutputRedirected) Console.OutputEncoding = Encoding.UTF8; } catch { }

            // 预处理 --language：设置语言 / 应用自定义语言包 / 导出内置语言包（与 GUI 语言设置同步）；
            // 只带 --language 而没有其它参数时，执行完打印结果并退出
            string langErr = CliPresetLanguage(args, out string[] restArgs, out CliLanguageAction langAction,
                out string langMessage, out int langExit);
            if (langErr != null)
            {
                // 参数用法错误 → 1（附帮助）；执行失败（语言包读不进去/导出失败）→ 2（不附帮助）
                if (langExit == 2)
                {
                    CliErr(langErr);
                    return 2;
                }
                return CliParamError(langErr);
            }
            if (langAction != CliLanguageAction.None && restArgs.Length == 0)
            {
                CliOut(langMessage);
                return 0;
            }
            args = restArgs;

            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    try { cts.Cancel(); } catch { }
                };
                try
                {
                    return CliDispatch(args, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    CliErr(T("CLI.Canceled", "操作已取消"));
                    return 2;
                }
                catch (Exception ex)
                {
                    CliErr(T("CLI.UnexpectedError", "发生未处理的错误") + ": " + ex.Message);
                    return 2;
                }
            }
        }

        // 参数错误：输出一行错误原因 + 完整帮助（stderr），退出码 1
        private static int CliParamError(string reason)
        {
            CliErr(T("CLI.ParamError", "参数错误") + ": " + reason);
            CliPrintHelp(Console.Error);
            return 1;
        }

        // --language 的三种用法（模式词大小写不敏感；`--language=x` 与 `--language x` 两种写法都支持）：
        //   --language <cn|en>            设置内置语言并持久化（兼容 cn/zh/zh-CN/en-US 等写法）
        //   --language other <json路径>   应用自定义语言包并持久化
        //   --language export <文件夹>    导出两份内置语言包到该文件夹（目录不存在则创建，不改当前语言）
        // 返回错误消息（null=正常）；rest 为剥离 --language 及其值后的参数；
        // action = 这次做了什么；message = "只带 --language 时"要打印的结果文案。
        private static string CliPresetLanguage(string[] args, out string[] rest,
            out CliLanguageAction action, out string message, out int exitCode)
        {
            action = CliLanguageAction.None;
            message = null;
            rest = null;
            exitCode = 1;   // 默认按"参数错误"；只有 other/export 的**执行**失败才改成 2
            string lang = null;
            var restList = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string tok = CliNormalizeToken(args[i]);
                if (CliIsLanguageOption(tok))
                {
                    if (lang != null)
                    {
                        rest = null;
                        return T("CLI.DuplicateOption", "参数重复") + ": --language";
                    }
                    int eq = tok.IndexOf('=');
                    if (eq >= 0)
                        lang = tok.Substring(eq + 1);
                    else if (i + 1 < args.Length)
                        lang = args[++i];
                    else
                    {
                        rest = null;
                        return T("CLI.MissingValue", "参数缺少值") + ": --language";
                    }
                    continue;
                }
                restList.Add(args[i]);
            }

            rest = restList.ToArray();
            if (lang == null) return null;

            string mode = (lang ?? string.Empty).Trim().ToLowerInvariant();

            // ── --language other <json路径> ──
            if (mode == "other")
            {
                if (!CliTakeExtraValue(rest, out string path, out string[] remain))
                {
                    rest = null;
                    return T("CLI.LanguageOtherMissingPath", "参数缺少值：--language other 需要指定语言 json 文件路径");
                }
                if (!LanguageManager.Instance.TryLoadCustom(path, out string loadError))
                {
                    rest = null;
                    // 语言包存在但读不进去/解析失败 → 属"处理失败"而非参数错误
                    exitCode = 2;
                    return string.Format(T("CLI.LanguageCustomLoadFail", "自定义语言包无法加载：{0}"), loadError);
                }
                rest = remain;
                action = CliLanguageAction.SetLanguage;
                message = string.Format(T("CLI.LanguageSet", "语言已设置并保存：{0}"), CliLanguageDisplayName());
                return null;
            }

            // ── --language export <文件夹> ──
            if (mode == "export")
            {
                if (!CliTakeExtraValue(rest, out string folder, out string[] remain))
                {
                    rest = null;
                    return T("CLI.LanguageExportMissingFolder", "参数缺少值：--language export 需要指定导出目标文件夹");
                }
                if (!LanguageManager.TryExportBuiltInPacks(folder, out string exportError, out var written))
                {
                    rest = null;
                    // 目录/权限问题属"处理失败"而非参数错误
                    exitCode = 2;
                    return string.Format(T("CLI.LanguageExportFail", "导出内置语言包失败：{0}"), exportError);
                }
                rest = remain;
                action = CliLanguageAction.ExportPacks;
                message = string.Format(T("CLI.LanguageExported", "已导出内置语言包：{0}"), string.Join("、", written));
                return null;
            }

            // ── --language <cn|en> ──
            string code = CliMapLanguage(mode);
            if (code == null)
            {
                rest = null;
                return T("CLI.LanguageInvalid", "语言参数无效（仅支持 cn、en、other、export）") + ": " + lang;
            }
            LanguageManager.Instance.LoadBuiltIn(code);
            action = CliLanguageAction.SetLanguage;
            message = string.Format(T("CLI.LanguageSet", "语言已设置并保存：{0}"), CliLanguageDisplayName());
            return null;
        }

        /// <summary>
        /// 是否是 --language 选项：<c>--language</c> 与 <c>--language=xxx</c> 两种写法都要认。
        /// 注意：不能直接拿 <c>tok.Substring(2)</c> 与 "language" 比较 —— 等号写法下会得到
        /// "language=xxx"，匹配不上，于是被当成未知命令（原实现的 <c>=</c> 分支实际上从未生效）。
        /// </summary>
        private static bool CliIsLanguageOption(string tok)
        {
            if (!tok.StartsWith("--", StringComparison.Ordinal)) return false;
            string body = tok.Substring(2);
            int eq = body.IndexOf('=');
            if (eq >= 0) body = body.Substring(0, eq);
            return body.Equals("language", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 取 --language 的附加值（other 的 json 路径 / export 的目标文件夹）：取剩余参数里的第一个。
        /// 不存在、或以 '-' 开头（说明是另一个选项）都算缺值 —— 否则
        /// <c>--language other -u x.arc</c> 会把 <c>-u</c> 当成路径，报出莫名其妙的错误。
        /// </summary>
        private static bool CliTakeExtraValue(string[] rest, out string value, out string[] remain)
        {
            value = null;
            remain = rest;
            if (rest == null || rest.Length == 0) return false;

            string candidate = rest[0];
            if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith("-", StringComparison.Ordinal))
                return false;

            value = candidate;
            var rest2 = new string[rest.Length - 1];
            Array.Copy(rest, 1, rest2, 0, rest2.Length);
            remain = rest2;
            return true;
        }

        /// <summary>
        /// 语言标识 → CLI 显示名：zh=简体中文；en=English；自定义=「自定义（完整 json 路径）」。
        /// </summary>
        private static string CliLanguageDisplayName()
        {
            var lm = LanguageManager.Instance;
            if (lm.IsCustomLanguage)
                return string.Format(T("CLI.LanguageCustomName", "自定义（{0}）"), lm.CustomLanguagePath);
            return string.Equals(lm.CurrentLangCode, LanguageManager.BuiltInZh, StringComparison.OrdinalIgnoreCase)
                ? "简体中文"
                : "English";
        }

        private static string CliMapLanguage(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "cn": case "zh": case "zh-cn": return LanguageManager.BuiltInZh;
                case "en": case "en-us": return LanguageManager.BuiltInEn;
                default: return null;
            }
        }

        private static int CliDispatch(string[] args, CancellationToken ct)
        {
            if (args.Length == 0)
                return CliParamError(T("CLI.MissingInput", "缺少必要的输入参数"));

            // 兼容全角横线/破折号开头（中文输入法常见），统一按半角 - 处理
            string head = CliNormalizeToken(args[0]);
            if (!head.StartsWith("--", StringComparison.Ordinal))
            {
                string msg = T("CLI.InvalidToken", "参数必须以 -- 开头") + ": " + head;
                if (head.ToLowerInvariant().Contains("help"))
                    msg += "；" + T("CLI.HelpHint", "如需帮助请输入 --help（两个半角短横线）");
                return CliParamError(msg);
            }

            switch (head.Substring(2).ToLowerInvariant())
            {
                case "help":
                    CliPrintHelp(Console.Out);
                    return 0;
                case "version":
                    CliOut(CliVersionBanner(Path.GetFileName(AppDomain.CurrentDomain.FriendlyName)));
                    return 0;
                case "pack":
                case "unpack":
                    return CliDispatchPair(head.Substring(2).ToLowerInvariant(), args, ct);
                case "compact":
                    return CliDispatchSimple("compact", args, ct);
                case "info":
                    return CliDispatchSimple("info", args, ct);
                case "check":
                    return CliDispatchSimple("check", args, ct);
                default:
                    return CliParamError(T("CLI.UnknownCommand", "未知命令") + ": " + head);
            }
        }

        // --pack / --unpack：二级子参数 --arz/--arc 必须紧跟主命令
        private static int CliDispatchPair(string verb, string[] args, CancellationToken ct)
        {
            if (args.Length < 2 || !args[1].StartsWith("--", StringComparison.Ordinal))
                return CliParamError(T("CLI.MissingSubCommand", "缺少二级子参数 --arz 或 --arc"));

            string sub = args[1].Substring(2).ToLowerInvariant();
            if (sub != "arz" && sub != "arc")
                return CliParamError(T("CLI.MissingSubCommand", "缺少二级子参数 --arz 或 --arc") + ": " + args[1]);

            bool isArz = sub == "arz";
            var parsed = new CliArgs();
            var allowed = new HashSet<string> { "show" };
            if (verb == "pack")
            {
                allowed.Add("update");
                if (isArz) allowed.Add("tpl");
            }

            string err = CliParseTail(args.Skip(2).ToArray(), 2, allowed, parsed);
            if (err != null) return CliParamError(err);
            if (parsed.Positional.Count == 0)
                return CliParamError(T("CLI.MissingInput", "缺少必要的输入参数"));

            return verb == "pack" ? CmdPack(isArz, parsed, ct) : CmdUnpack(isArz, parsed, ct);
        }

        // --compact / --info / --check：单文件命令
        private static int CliDispatchSimple(string verb, string[] args, CancellationToken ct)
        {
            var parsed = new CliArgs();
            var allowed = new HashSet<string>();
            if (verb == "info") allowed.Add("show");

            int maxPositional = verb == "info" || verb == "check" ? 1 : 2;
            string err = CliParseTail(args.Skip(1).ToArray(), maxPositional, allowed, parsed);
            if (err != null) return CliParamError(err);
            if (parsed.Positional.Count == 0)
                return CliParamError(T("CLI.MissingInput", "缺少必要的输入参数"));

            switch (verb)
            {
                case "compact": return CmdCompact(parsed, ct);
                case "info": return CmdInfo(parsed);
                default: return CmdCheck(parsed);
            }
        }

        // 兼容全角横线（－）与破折号（—–）：参数开头的横线一律归一为半角 -
        private static string CliNormalizeToken(string tok)
        {
            int i = 0;
            while (i < tok.Length)
            {
                char c = tok[i];
                if (c == '-' || c == '－' || c == '—' || c == '–') { i++; continue; }
                break;
            }
            return i == 0 ? tok : new string('-', i) + tok.Substring(i);
        }

        // 通用尾部解析：选项（大小写不敏感、--name value 与 --name=value 皆可、只接受一次）+ 位置参数（按命令上限）
        private static string CliParseTail(string[] tail, int maxPositional, HashSet<string> allowed, CliArgs into)
        {
            for (int i = 0; i < tail.Length; i++)
            {
                string tok = CliNormalizeToken(tail[i]);
                if (tok.StartsWith("--", StringComparison.Ordinal))
                {
                    string name = tok.Substring(2);
                    string value = null;
                    int eq = name.IndexOf('=');
                    if (eq >= 0)
                    {
                        value = name.Substring(eq + 1);
                        name = name.Substring(0, eq);
                    }
                    name = name.ToLowerInvariant();

                    if (!allowed.Contains(name))
                        return T("CLI.UnknownOption", "未知或当前命令不支持的参数") + ": " + tok;

                    switch (name)
                    {
                        case "show":
                            if (into.Show) return T("CLI.DuplicateOption", "参数重复") + ": --show";
                            into.Show = true;
                            break;
                        case "update":
                            if (into.Update) return T("CLI.DuplicateOption", "参数重复") + ": --update";
                            into.Update = true;
                            break;
                        case "tpl":
                            if (into.Tpl != null) return T("CLI.DuplicateOption", "参数重复") + ": --tpl";
                            if (value != null) into.Tpl = value;
                            else if (i + 1 < tail.Length && !tail[i + 1].StartsWith("--", StringComparison.Ordinal))
                                into.Tpl = tail[++i];
                            else
                                return T("CLI.MissingValue", "参数缺少值") + ": --tpl";
                            break;
                    }
                }
                else
                {
                    if (into.Positional.Count >= maxPositional)
                        return T("CLI.ExtraPositional", "多余的位置参数") + ": " + tok;
                    into.Positional.Add(tok);
                }
            }
            return null;
        }

        // ==================== --pack ====================

        private static int CmdPack(bool isArz, CliArgs a, CancellationToken ct)
        {
            var cb = new CliProgressCallback(a.Show);
            DateTime start = DateTime.Now;
            string input = a.Positional[0];
            string outArg = a.Positional.Count > 1 ? a.Positional[1] : null;
            string outfile;
            int fileCount = 0;
            bool ok;

            if (isArz)
            {
                if (!Directory.Exists(input))
                {
                    CliErr(File.Exists(input)
                        ? T("CLI.NotADirectory", "输入不是目录") + ": " + input
                        : T("CLI.DirNotFound", "目录不存在") + ": " + input);
                    return 2;
                }

                // 结构自动识别（形态1/2/3，详见方案文档「ARZ 打包目录结构自动识别与输出」）
                string dbDir = CliLocateDatabase(input);
                if (dbDir == null)
                {
                    CliErr(T("CLI.DbNotFound", "无法定位 database 目录（输入须为 database 目录、其上级目录或其内部子目录）") + ": " + input);
                    return 2;
                }

                string modDir = Path.GetDirectoryName(dbDir);
                string modName = string.IsNullOrEmpty(modDir) ? "" : Path.GetFileName(modDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                // 打包范围：database 下全部一级子目录（排除 output），统计可打包的 .dbr 数量
                var subDirs = new List<string>();
                try
                {
                    foreach (string d in Directory.GetDirectories(dbDir))
                    {
                        string n = Path.GetFileName(d);
                        if (n.Equals("output", StringComparison.OrdinalIgnoreCase)) continue;
                        subDirs.Add(n);
                        fileCount += Directory.GetFiles(d, "*.dbr", SearchOption.AllDirectories).Length;
                    }
                }
                catch (Exception ex)
                {
                    CliErr(T("CLI.IoError", "读取失败") + ": " + ex.Message);
                    return 2;
                }

                if (fileCount == 0)
                {
                    CliErr(T("CLI.EmptyInputArz", "没有可打包的内容（database 下未找到 .dbr 文件）"));
                    return 2;
                }

                if (outArg != null)
                {
                    outfile = outArg;   // 显式指定：已存在直接覆盖
                }
                else
                {
                    if (string.IsNullOrEmpty(modName))
                    {
                        CliErr(T("CLI.RootDefaultName", "database 位于盘根，无法自动命名输出文件，请显式指定输出文件"));
                        return 2;
                    }
                    // 缺省：database 目录内，database 上级目录名命名，重名自动改名
                    outfile = CliUniqueName(Path.Combine(dbDir, modName + ".arz"));
                }

                // 模板：--tpl 优先（两种形态皆可）；路径不存在 → 忽略并提示，继续用设置中的模板
                List<TemplateEntry> entries = SettingsManager.TemplateEntries;
                if (!string.IsNullOrEmpty(a.Tpl))
                {
                    if (Directory.Exists(a.Tpl))
                        entries = new List<TemplateEntry> { new TemplateEntry { Id = "cli-tpl", Type = "Folder", Path = a.Tpl, IsChecked = true } };
                    else if (File.Exists(a.Tpl))
                        entries = new List<TemplateEntry> { new TemplateEntry { Id = "cli-tpl", Type = "Arc", Path = a.Tpl, IsChecked = true } };
                    else
                        cb.ReportWarning(T("CLI.TplNotFound", "指定的模板不存在，已忽略") + ": " + a.Tpl);
                }

                Dictionary<string, TemplateNode> templates;
                int builtInCount, customCount;
                try
                {
                    var loaded = LoadTemplates(entries);
                    templates = loaded.templates;
                    builtInCount = loaded.builtInCount;
                    customCount = loaded.customCount;
                }
                catch (Exception ex)
                {
                    CliErr(T("CLI.NoTemplates", "模板加载为 0，已终止打包") + ": " + ex.Message);
                    return 2;
                }
                if (templates.Count == 0)
                {
                    CliErr(T("CLI.NoTemplates", "模板加载为 0，已终止打包"));
                    return 2;
                }

                ok = GDPackEditorAPI.BuildArzWithSelectedDirs(modDir, null, null, subDirs, cb, outfile, ct, entries);
            }
            else
            {
                if (!Directory.Exists(input))
                {
                    CliErr(File.Exists(input)
                        ? T("CLI.NotADirectory", "输入不是目录") + ": " + input
                        : T("CLI.DirNotFound", "目录不存在") + ": " + input);
                    return 2;
                }

                try { fileCount = Directory.GetFiles(input, "*", SearchOption.AllDirectories).Length; }
                catch (Exception ex)
                {
                    CliErr(T("CLI.IoError", "读取失败") + ": " + ex.Message);
                    return 2;
                }
                if (fileCount == 0)
                {
                    CliErr(T("CLI.EmptyInputArc", "没有可打包的内容（输入目录为空）"));
                    return 2;
                }

                if (outArg != null)
                {
                    outfile = outArg;
                }
                else
                {
                    // 缺省：输入目录同级，目录名命名，重名自动改名
                    outfile = CliUniqueName(Path.Combine(Path.GetDirectoryName(input), Path.GetFileName(input) + ".arc"));
                }

                if (a.Update && !File.Exists(outfile))
                {
                    CliErr(T("CLI.UpdateRequiresExisting", "--update 增量打包要求目标 ARC 已存在") + ": " + outfile);
                    return 2;
                }

                if (a.Update)
                {
                    // 增量打包：CLI 非交互，打印变更清单后自动确认
                    ok = GDPackEditorAPI.PackArcIncremental(input, outfile, outfile, cb, changes =>
                    {
                        foreach (string line in changes) CliOut(line);
                        return true;
                    }, true, ct);
                }
                else
                {
                    ok = GDPackEditorAPI.PackArc(input, outfile, cb, ct);
                }
            }

            cb.LogFinalize();
            if (!ok)
            {
                CliErr(T("CLI.Failed", "操作失败，请查看上方日志"));
                return 2;
            }

            double elapsed = (DateTime.Now - start).TotalSeconds;
            CliOut(string.Format(T("CLI.DonePack", "打包完成：{0} 个文件，耗时 {1:F1} 秒"), fileCount, elapsed));
            CliOut(T("CLI.OutputFile", "输出文件") + ": " + Path.GetFullPath(outfile));
            CliPrintInfo(outfile, false);   // 完成统计：复用 info 输出新包概况
            return 0;
        }

        // ==================== --unpack ====================

        private static int CmdUnpack(bool isArz, CliArgs a, CancellationToken ct)
        {
            var cb = new CliProgressCallback(a.Show);
            DateTime start = DateTime.Now;
            string file = a.Positional[0];

            if (!File.Exists(file))
            {
                CliErr(T("CLI.FileNotFound", "文件不存在") + ": " + file);
                return 2;
            }

            // 解包前校验并统计有效条目（损坏文件在此直接报错，不产生半解包输出）
            CliPkgStats st = CliGetStats(file, isArz ? CliPkgType.Arz : CliPkgType.Arc, false);
            if (st == null)
            {
                CliErr(T("CLI.TypeUnknown", "无法识别的包文件（损坏或不是有效的 ARZ/ARC）") + ": " + file);
                return 2;
            }

            // 输出目录缺省：ARZ=包文件所在目录（与GUI一致）；ARC=包文件所在目录下的同名子目录（与GUI一致）
            string full = Path.GetFullPath(file);
            string outDir;
            if (a.Positional.Count > 1)
                outDir = a.Positional[1];
            else if (isArz)
                outDir = Path.GetDirectoryName(full);
            else
                outDir = Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full));

            bool ok = isArz
                ? GDPackEditorAPI.UnpackArz(file, outDir, cb, ct)
                : GDPackEditorAPI.UnpackArc(file, outDir, cb, ct);

            cb.LogFinalize();
            if (!ok)
            {
                CliErr(T("CLI.Failed", "操作失败，请查看上方日志"));
                return 2;
            }

            double elapsed = (DateTime.Now - start).TotalSeconds;
            CliOut(string.Format(T("CLI.DoneUnpack", "解包完成：{0} 条，耗时 {1:F1} 秒"), st.Valid, elapsed));
            CliOut(T("CLI.OutputDir", "输出目录") + ": " + Path.GetFullPath(outDir));
            return 0;
        }

        // ==================== --compact ====================

        private static int CmdCompact(CliArgs a, CancellationToken ct)
        {
            var cb = new CliProgressCallback(a.Show);
            DateTime start = DateTime.Now;
            string file = a.Positional[0];

            if (!File.Exists(file))
            {
                CliErr(T("CLI.FileNotFound", "文件不存在") + ": " + file);
                return 2;
            }

            CliPkgType type;
            if (!CliDetectType(file, out type))
            {
                CliErr(T("CLI.TypeUnknown", "无法识别的包文件（损坏或不是有效的 ARZ/ARC）") + ": " + file);
                return 2;
            }

            string outfile;
            if (a.Positional.Count > 1)
                outfile = a.Positional[1];   // 显式指定：允许与输入相同（数据层先读后写，安全就地固实）
            else
            {
                // 缺省：新建 原名_compact.扩展名，重名自动改名，原文件不动
                string dir = Path.GetDirectoryName(Path.GetFullPath(file));
                string name = Path.GetFileNameWithoutExtension(file);
                string ext = Path.GetExtension(file);
                outfile = CliUniqueName(Path.Combine(dir, name + "_compact" + ext));
            }

            bool ok = type == CliPkgType.Arc
                ? GDPackEditorAPI.CompactArc(file, outfile, cb, ct)
                : GDPackEditorAPI.CompactArz(file, outfile, cb, ct);

            cb.LogFinalize();
            if (!ok)
            {
                CliErr(T("CLI.Failed", "操作失败，请查看上方日志"));
                return 2;
            }

            double elapsed = (DateTime.Now - start).TotalSeconds;
            CliOut(string.Format(T("CLI.DoneCompact", "固实完成，耗时 {0:F1} 秒"), elapsed));

            bool sameFile = string.Equals(Path.GetFullPath(outfile), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
            if (sameFile || File.Exists(outfile))
                CliOut(T("CLI.OutputFile", "输出文件") + ": " + Path.GetFullPath(outfile));
            else
                CliOut(T("CLI.CompactNoTombstone", "没有墓碑记录，未生成新文件"));
            return 0;
        }

        // ==================== --info ====================

        private static int CmdInfo(CliArgs a)
        {
            string file = a.Positional[0];
            if (!File.Exists(file))
            {
                CliErr(T("CLI.FileNotFound", "文件不存在") + ": " + file);
                return 2;
            }
            return CliPrintInfo(file, a.Show);
        }

        // 概况报告：类型 / 大小 / 总条目 / 有效 / 墓碑；listAll 时逐行列出有效条目名
        private static int CliPrintInfo(string file, bool listAll)
        {
            CliPkgType type;
            if (!CliDetectType(file, out type))
            {
                CliErr(T("CLI.TypeUnknown", "无法识别的包文件（损坏或不是有效的 ARZ/ARC）") + ": " + file);
                return 2;
            }

            CliPkgStats st = CliGetStats(file, type, listAll, CliOut);
            if (st == null)
            {
                CliErr(T("CLI.TypeUnknown", "无法识别的包文件（损坏或不是有效的 ARZ/ARC）") + ": " + file);
                return 2;
            }

            string typeName = type == CliPkgType.Arc ? "ARC" : "ARZ";
            CliOut(T("CLI.InfoType", "类型") + ": " + typeName);
            CliOut(T("CLI.InfoSize", "文件大小") + ": " + st.Size.ToString("N0") + " " + T("CLI.Bytes", "字节")
                + " (" + (st.Size / 1024.0 / 1024.0).ToString("F2") + " MB)");
            CliOut(T("CLI.InfoTotal", "总条目") + ": " + st.Total.ToString("N0"));
            CliOut(T("CLI.InfoValid", "有效条目") + ": " + st.Valid.ToString("N0"));
            CliOut(T("CLI.InfoTombstone", "墓碑（僵尸）记录") + ": " + st.Tombstones.ToString("N0"));

            if (listAll && st.Names != null)
            {
                foreach (string n in st.Names)
                    CliOut("  " + n);
            }
            return 0;
        }

        // ==================== --check ====================

        /// <summary>目录 dbr 文本检测（--check 传入目录时，批D）：遍历 *.dbr 按【内容检测】同一套规则检查。</summary>
        private static int CmdCheckDir(string dir)
        {
            CliOut(T("CLI.CheckDirHeader", "目录检测（*.dbr）") + ": " + dir);
            var cb = new CliProgressCallback(false);
            try
            {
                // 进度交给 CLI 底部单行显示（原地刷新）；进度不进日志
                var (issues, files) = GDPackEditorAPI.CheckDbrDirectory(dir, cb, CancellationToken.None,
                    (pct, msg) => cb.LogOverwrite(msg));
                cb.LogFinalize();
                CliOut(string.Format(T("CLI.CheckDirResult", "目录检测完成：检查 {0} 个文件，发现 {1} 处可疑"), files, issues));
                return 0;
            }
            catch (Exception ex)
            {
                cb.LogFinalize();
                CliErr(T("Common.CheckFailed", "检测未完成") + ": " + ex.Message);
                return 2;
            }
        }

        private static int CmdCheck(CliArgs a)
        {
            string file = a.Positional[0];
            // 批D：--check 自动判断路径 —— 目录 → 目录 dbr 文本检测；文件 → 包检测（原有流程不变）
            if (Directory.Exists(file))
                return CmdCheckDir(file);
            if (!File.Exists(file))
            {
                CliErr(T("CLI.FileNotFound", "文件不存在") + ": " + file);
                return 2;
            }

            CliPkgType type;
            if (!CliDetectType(file, out type))
            {
                CliErr(T("CLI.TypeUnknown", "无法识别的包文件（损坏或不是有效的 ARZ/ARC）") + ": " + file);
                return 2;
            }

            string typeName = type == CliPkgType.Arc ? "ARC" : "ARZ";
            int total, valid;

            try
            {
                if (type == CliPkgType.Arz)
                {
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var reader = new ARZReader(fs);
                        total = reader.Count;
                        valid = 0;
                        for (int i = 0; i < total; i++)
                        {
                            var info = reader.GetRecordInfo(i);
                            string name = reader.GetRecordName(i);
                            if (info != null && !string.IsNullOrEmpty(name)) valid++;
                            // 墓碑记录不做明细输出：它们没有路径，无法据此定位，只报数量即可
                        }
                    }
                }
                else
                {
                    var afile = new ARCFile();
                    afile.NameReporter = CliOut;   // CLI 仅日志：读侧判定结论直接打印
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                        afile.ReadStream(fs);
                    total = afile.toc.Count;
                    valid = 0;
                    for (int i = 0; i < total; i++)
                    {
                        var e = afile.toc[i];
                        string name = e.GetEntryString(afile.strs);
                        if (!string.IsNullOrEmpty(name) && e.EntryType == 3) valid++;
                        // 墓碑条目同理：只计数、不列明细
                    }
                }
            }
            catch (Exception ex)
            {
                CliErr(string.Format(T("CLI.Damaged", "文件结构损坏或不是有效的 {0} 文件"), typeName) + " (" + ex.Message + ")");
                return 2;
            }

            int tombstones = total - valid;
            CliOut(T("CLI.CheckHeader", "结构校验") + ": " + T("CLI.CheckOk", "通过") + " (" + typeName + ")");
            CliOut(T("CLI.InfoTotal", "总条目") + ": " + total.ToString("N0")
                + " / " + T("CLI.InfoValid", "有效") + ": " + valid.ToString("N0")
                + " / " + T("CLI.InfoTombstone", "墓碑") + ": " + tombstones.ToString("N0"));

            if (tombstones > 0)
            {
                // 只报数量：墓碑没有路径，列索引/类型/大小对使用者没有可操作性，反而看不懂
                CliOut(string.Format(T("CLI.CheckTombstoneCount",
                    "发现 {0} 个墓碑记录（已删除记录留下的空位，无路径可定位）"), tombstones));
                CliOut(T("CLI.CheckHint", "提示：可使用 --compact 清理墓碑记录"));
            }

            // ARZ 内容检查：值层面的可疑内容（换行 / 逗号 / 控制字符）。
            // 仅报警告、不改动包、不影响退出码（与"有墓碑仅提示"的既有风格一致）。
            // 进度：底部单行原地刷新「检测中（当前/总数）XX%」，有新日志时进度行挪到新日志下方，结束后清除。
            if (type == CliPkgType.Arz)
            {
                CliOut(T("CLI.CheckContentHeader", "内容检查"));
                var cb = new CliProgressCallback(false);
                try
                {
                    // 进度交给 CLI 底部单行显示（原地刷新）；进度不进日志
                    var (issues, checkedRecords, newlineIssues) = GDPackEditorAPI.CheckArzContent(file, cb, CancellationToken.None,
                        (pct, msg) => cb.LogOverwrite(msg));
                    // 先清进度行再输出完成行：否则完成行会接在「检测中…100%」同一行后
                    cb.LogFinalize();
                    CliOut(string.Format(T("CLI.CheckContentResult", "内容检查完成：检查 {0} 条记录，发现 {1} 处可疑"),
                        checkedRecords, issues));
                    // 换行说明放"完成"之后：此处用户注意力最高（解释"为何导出/源码里看不到换行"）
                    GDPackEditorAPI.EmitCheckNewlineNote(cb, newlineIssues);
                }
                catch (Exception ex)
                {
                    CliOut(T("CLI.CheckContentFailed", "内容检查未完成") + ": " + ex.Message);
                }
                finally
                {
                    cb.LogFinalize();   // 清除底部进度行，保持收尾干净
                }
            }
            return 0;
        }

        // ==================== 辅助 ====================

        // 类型识别只看文件头：ARC 魔数 "ARC\0"；ARZ 魔数 02 00 03 00（+ 头布局校验）
        private static bool CliDetectType(string file, out CliPkgType type)
        {
            type = CliPkgType.Arz;
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] head = new byte[4];
                    int read = 0;
                    while (read < 4)
                    {
                        int n = fs.Read(head, read, 4 - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read == 4 && head[0] == (byte)'A' && head[1] == (byte)'R' && head[2] == (byte)'C' && head[3] == 0)
                    {
                        type = CliPkgType.Arc;
                        return true;
                    }
                    // ARZ：魔数(short 0x02) + 版本(short 0x03)，再经头布局校验（偏移非法抛异常）
                    if (read == 4 && head[0] == 0x02 && head[1] == 0x00 && head[2] == 0x03 && head[3] == 0x00)
                    {
                        fs.Seek(0, SeekOrigin.Begin);
                        new ARZReader(fs);
                        type = CliPkgType.Arz;
                        return true;
                    }
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        // 结构自动识别（形态1：自身即 database；形态2：含 database 子目录；形态3：位于 database 祖先目录内）
        private static string CliLocateDatabase(string inputDir)
        {
            string full = Path.GetFullPath(inputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(Path.GetFileName(full), "database", StringComparison.OrdinalIgnoreCase))
                return full;

            string sub = Path.Combine(full, "database");
            if (Directory.Exists(sub))
                return sub;

            for (var dir = Directory.GetParent(full); dir != null; dir = dir.Parent)
            {
                if (string.Equals(dir.Name, "database", StringComparison.OrdinalIgnoreCase))
                    return dir.FullName;
            }
            return null;
        }

        // 条目统计（总/有效/墓碑；collectNames 时收集有效条目名）。文件无法读取返回 null。
        // nameReporter 非空时输出读侧名字编码判定结论 —— 只由 --info / --check 这类"专门看包"的命令传入；
        // 解包/打包前的预检同样调用本方法，传空以免同一结论被打印两次。
        private static CliPkgStats CliGetStats(string file, CliPkgType type, bool collectNames, Action<string> nameReporter = null)
        {
            try
            {
                var st = new CliPkgStats { Names = collectNames ? new List<string>() : null };
                st.Size = new FileInfo(file).Length;
                if (type == CliPkgType.Arz)
                {
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var reader = new ARZReader(fs);
                        st.Total = reader.Count;
                        for (int i = 0; i < st.Total; i++)
                        {
                            var info = reader.GetRecordInfo(i);
                            string name = reader.GetRecordName(i);
                            if (info == null || string.IsNullOrEmpty(name)) continue;
                            st.Valid++;
                            if (collectNames) st.Names.Add(name);
                        }
                    }
                }
                else
                {
                    var afile = new ARCFile();
                    afile.NameReporter = nameReporter;   // 仅 --info（传入 CliOut）时输出判定结论
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                        afile.ReadStream(fs);
                    st.Total = afile.toc.Count;
                    foreach (var e in afile.toc)
                    {
                        string name = e.GetEntryString(afile.strs);
                        if (string.IsNullOrEmpty(name) || e.EntryType != 3) continue;
                        st.Valid++;
                        if (collectNames) st.Names.Add(name);
                    }
                }
                st.Tombstones = st.Total - st.Valid;
                return st;
            }
            catch
            {
                return null;
            }
        }

        // 自动生成名冲突时自动改名「名称 (2).扩展名」，效果同资源管理器
        private static string CliUniqueName(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        private static void CliPrintHelp(TextWriter writer)
        {
            writer.WriteLine(CliVersionBanner("GDPackEditor"));
            writer.WriteLine(T("CLI.HelpGuiHint", "不带参数或 --gui 启动图形界面"));
            writer.WriteLine();
            writer.WriteLine(T("CLI.HelpUsage", "用法") + ":");
            writer.WriteLine("  " + T("CLI.UsagePackArz", "--pack   --arz <目录> [输出文件] [--tpl <tpl目录|templates.arc>] [--show]"));
            writer.WriteLine("      " + T("CLI.DescPackArz", "打包ARZ：自动识别 database 目录结构，database 下全部子目录参与打包；--tpl 可选，不输入则使用内置模板"));
            writer.WriteLine("  " + T("CLI.UsagePackArc", "--pack   --arc <目录> [输出文件] [--update] [--show]"));
            writer.WriteLine("      " + T("CLI.DescPackArc", "打包ARC；--update 增量打包（要求目标 ARC 已存在）"));
            writer.WriteLine("  " + T("CLI.UsageUnpackArz", "--unpack --arz <包文件> [输出目录] [--show]"));
            writer.WriteLine("      " + T("CLI.DescUnpackArz", "解包ARZ；输出目录缺省为包文件所在目录"));
            writer.WriteLine("  " + T("CLI.UsageUnpackArc", "--unpack --arc <包文件> [输出目录] [--show]"));
            writer.WriteLine("      " + T("CLI.DescUnpackArc", "解包ARC；输出目录缺省为与包同名的子目录"));
            writer.WriteLine("  " + T("CLI.UsageCompact", "--compact <包文件> [输出文件]"));
            writer.WriteLine("      " + T("CLI.DescCompact", "固实：清理墓碑/无效条目；输出缺省为 原名_compact.扩展名（重名自动改名）"));
            writer.WriteLine("  " + T("CLI.UsageInfo", "--info <包文件> [--show]"));
            writer.WriteLine("      " + T("CLI.DescInfo", "概况报告（类型/条目/大小）；--show 追加列出包内全部条目"));
            writer.WriteLine("  " + T("CLI.UsageCheck", "--check <包文件>"));
            writer.WriteLine("      " + T("CLI.DescCheck", "体检：结构校验 + 墓碑记录检测（只读）"));
            writer.WriteLine("  " + T("CLI.UsageLanguage", "--language <cn|en>"));
            writer.WriteLine("      " + T("CLI.DescLanguage", "设置语言：cn=简体中文，en=English；与 GUI 语言设置同步并持久化"));
            writer.WriteLine("  " + T("CLI.UsageHelpVersion", "--help | --version   (帮助 / 版本信息)"));
            writer.WriteLine();
            writer.WriteLine(T("CLI.HelpExitCodes", "退出码：0=成功，1=参数错误，2=处理失败"));
        }

        #endregion
    }

    // CLI 进度回调：默认单行覆写显示进度；--show 逐行明细；输出重定向时进度静默（仅保留开始/结束信息）
    class CliProgressCallback : IProgressCallback
    {
        private readonly bool _showDetail;
        private readonly bool _console;
        private readonly object _lock = new object();
        private int _lastLen;   // 上次进度行显示宽度，用于擦除残留
        private string _progressText;   // 当前底部进度行文本：打印日志后据此在新日志下方重绘
        /// <summary>进度刷新节流（与 GUI 共用同一实现，保证两端口径一致）</summary>
        private readonly ProgressThrottle _throttle = new ProgressThrottle();

        public CliProgressCallback(bool showDetail)
        {
            _showDetail = showDetail;
            try { _console = !Console.IsOutputRedirected; } catch { _console = false; }
        }

        // 按 CJK 双列估算消息显示宽度，避免长度误判导致截断不足而折行
        private static int WidthOf(string s)
        {
            int w = 0;
            foreach (char c in s) w += c > 0x7F ? 2 : 1;
            return w;
        }

        /// <summary>擦掉底部进度行（保留 _progressText，便于之后重绘）。无光标控制（重定向/明细模式）时什么都不做。</summary>
        private void EraseProgressLine()
        {
            if (!_console || _lastLen == 0) return;
            try
            {
                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write(new string(' ', _lastLen));
                Console.SetCursorPosition(0, Console.CursorTop);
            }
            catch { }
            _lastLen = 0;
        }

        /// <summary>擦掉底部进度行并放弃它（结束/清除用）</summary>
        private void ClearProgressLine()
        {
            EraseProgressLine();
            _progressText = null;
        }

        /// <summary>按显示宽度（CJK 双列）截断：一次扫描找到切点，避免逐字符重算整串宽度（O(len²)）</summary>
        private static string TruncateToDisplayWidth(string s, int maxWidth)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int w = 0, i = 0;
            for (; i < s.Length; i++)
            {
                int cw = s[i] > 0x7F ? 2 : 1;
                if (w + cw > maxWidth) break;
                w += cw;
            }
            return i >= s.Length ? s : s.Substring(0, i);
        }

        private void Overwrite(string message)
        {
            if (_showDetail) { Console.WriteLine(message); _progressText = null; return; }
            if (!_console) return;
            try
            {
                // 按窗口宽度截断（CJK 按双列），保证进度消息永不折行
                int width = Math.Max(20, Console.WindowWidth - 1);
                message = TruncateToDisplayWidth(message, width);
                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write(message);
                // 短消息按上次宽度补空格，擦除上一轮更长的残留
                int w = WidthOf(message);
                if (_lastLen > w) Console.Write(new string(' ', _lastLen - w));
                _lastLen = w;
                _progressText = message;   // 记下来：有日志输出时要在新日志下方重绘
            }
            catch { _lastLen = 0; }
        }

        /// <summary>
        /// 打印一行日志，并把底部进度行挪到该日志的**下方**（进度行始终贴着最新输出）。
        /// 无光标控制时退化为普通逐行输出。不换行、不闪烁，无旋转动画。
        /// </summary>
        private void WriteLineAbove(string message, bool toError = false)
        {
            if (_showDetail || !_console)
            {
                if (toError) Console.Error.WriteLine(message);
                else Console.WriteLine(message);
                return;
            }
            EraseProgressLine();                       // 先擦掉进度行，日志写回它的位置
            if (toError) Console.Error.WriteLine(message);
            else Console.WriteLine(message);
            if (!string.IsNullOrEmpty(_progressText))
                Overwrite(_progressText);              // 再在最新行下方重绘进度行
        }

        /// <summary>
        /// 文件通道：CLI 已把日志打到控制台，故只落盘、不投递 UI（避免与主窗体日志框重复）。
        /// 过程性进度行是否落盘由设置项「详情记录在日志文件中」决定（默认关，避免日志被 (N/M) 行淹没）；
        /// `--show` 只控制**控制台**是否逐行显示明细，与文件记录相互独立。
        /// </summary>
        private void PersistLineToFile(string message, LogLevel level)
        {
            if (!SettingsManager.LogDetailToFile && LogService.IsProgressLine(message)) return;
            LogService.WriteFileOnly(level, message);
        }

        public void Report(int percentage, string message)
        {
            PersistLineToFile(message, LogLevel.Info);
            lock (_lock)
            {
                if (_showDetail) { Console.WriteLine(message); return; }
                if (!_console)
                {
                    // 重定向时进度静默，仅保留开始/结束等里程碑信息
                    if (percentage == 0 || percentage == 100) Console.WriteLine(message);
                    return;
                }
                // 终端底部进度行：按百分比节流（首条 / 末条 / 回退 / Δ≥1% 才重绘，与 GUI 同一节流器）。
                // 原实现每条记录都擦除+重绘（约 6 次控制台 API/条，6 万条 ≈ 36 万次内核调用，远超解包本身）。
                if (!_throttle.ShouldReport(percentage)) return;
                EraseProgressLine();
                Overwrite($"[{Math.Max(0, Math.Min(100, percentage))}%] {message}");
            }
        }

        public void ReportLog(string message)
        {
            PersistLineToFile(message, LogLevel.Info);   // 过程行在「详细过程」关闭时不落盘（与 GUI 同一规则）
            lock (_lock)
            {
                WriteLineAbove(message);
            }
        }

        public void ReportError(string error)
        {
            LogService.WriteFileOnly(LogLevel.Error, error);
            lock (_lock)
            {
                WriteLineAbove(error, toError: true);
            }
        }

        public void ReportWarning(string warning, string groupKey = null)
        {
            // CLI 控制台无折叠分组（groupKey 仅 GUI 日志区使用），逐条原样输出
            LogService.WriteFileOnly(LogLevel.Warn, warning);
            lock (_lock)
            {
                WriteLineAbove(warning, toError: true);
            }
        }

        /// <summary>
        /// 覆盖行（控制台替换上一行显示的瞬态进度）。【约定】只作用于界面、不落盘：
        /// 否则每个进度点都会写一行，日志文件会被淹没。
        /// </summary>
        public void LogOverwrite(string message)
        {
            lock (_lock)
            {
                Overwrite(message);
            }
        }

        public void LogFinalize()
        {
            lock (_lock)
            {
                ClearProgressLine();
            }
        }

        public void UpdateProgress(int percentage, string message)
        {
            lock (_lock)
            {
                if (_showDetail) { Console.WriteLine(message); return; }
                if (!_console) return;   // 重定向：静默进度不输出（与 Report 同口径）
                // 与 Report 共用同一节流器：静默进度（如解包前扫描）也是每条一次调用
                if (!_throttle.ShouldReport(percentage)) return;
                Overwrite($"[{Math.Max(0, Math.Min(100, percentage))}%] {message}");
            }
        }
    }
}