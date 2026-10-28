using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GDPackEditor
{
    public class LanguageItem
    {
        public string Code { get; set; }
        public string Name { get; set; }

        public LanguageItem(string code, string name)
        {
            Code = code;
            Name = name;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// UI 语言管理。两个来源：
    /// ① 内置语言包 —— 编译进 EXE 的嵌入资源，文件名贴游戏命名习惯（ZH.json / EN.json）；
    /// ② 自定义语言包 —— 磁盘上的任意 json 文件，由用户在设置里导入。
    ///
    /// 口径约定：语言代码一律小写 <c>zh</c> / <c>en</c> / <c>other</c>，读写都比较忽略大小写；
    /// 语言标识 → 资源文件名的映射走 <see cref="BuiltInFiles"/> **查表**，不做字符串拼接 ——
    /// <c>GetManifestResourceStream</c> 的资源名匹配是大小写敏感的，而配置里存的是小写代码。
    /// </summary>
    public class LanguageManager
    {
        private static readonly Lazy<LanguageManager> _instance = new Lazy<LanguageManager>(() => new LanguageManager());
        public static LanguageManager Instance => _instance.Value;

        /// <summary>内置：简体中文</summary>
        public const string BuiltInZh = "zh";
        /// <summary>内置：英文</summary>
        public const string BuiltInEn = "en";
        /// <summary>自定义语言包的代码（配置里 name 固定为它）</summary>
        public const string CustomCode = "other";

        /// <summary>内置代码 → 语言文件名（大写，贴游戏命名习惯）。必须查表：资源名匹配区分大小写。</summary>
        private static readonly Dictionary<string, string> BuiltInFiles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BuiltInZh] = "ZH.json",
                [BuiltInEn] = "EN.json",
            };

        /// <summary>自定义语言包不可用时，运行时回落到内置英文（与 MOD 合并使用说明等处"除中文外一律英文"的取向一致）</summary>
        private const string FallbackCode = BuiltInEn;

        private Dictionary<string, string> _currentLanguage = new Dictionary<string, string>();
        private string _currentLangCode = BuiltInZh;
        private bool _isCustom;
        private string _customPath;

        public event Action LanguageChanged;

        private LanguageManager()
        {
            ApplyFromSettings();
        }

        // ───────────────────────── 静态工具 ─────────────────────────

        /// <summary>内置语言代码（顺序即设置窗里的排列顺序）</summary>
        public static IReadOnlyList<string> BuiltInCodes { get; } = new[] { BuiltInZh, BuiltInEn };

        /// <summary>
        /// 把用户输入/旧配置里的语言标识归一化为内置代码；非内置返回 null。忽略大小写。
        /// 兼容写法：cn / zh / zh-CN / zh_cn → zh；en / en-US → en。
        /// </summary>
        public static string NormalizeBuiltInCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string v = raw.Trim().ToLowerInvariant();
            if (v == "cn" || v == "zh" || v == "zh-cn" || v == "zh_cn" || v == "zhcn") return BuiltInZh;
            if (v == "en" || v == "en-us" || v == "en_us" || v == "enus") return BuiltInEn;
            return null;
        }

        public static bool IsBuiltInCode(string raw) => NormalizeBuiltInCode(raw) != null;

        /// <summary>内置语言包的文件名（ZH.json / EN.json）；导出时也用它，非内置返回 null</summary>
        public static string GetBuiltInFileName(string code)
            => BuiltInFiles.TryGetValue(code ?? string.Empty, out var file) ? file : null;

        private static string BuiltInResourceName(string code)
        {
            string file = GetBuiltInFileName(code);
            return file == null ? null : $"GDPackEditor.languages.{file}";
        }

        // ───────────────────────── 运行态查询 ─────────────────────────

        /// <summary>当前运行的语言标识：zh / en / other</summary>
        public string CurrentLangCode => _currentLangCode;

        /// <summary>当前是否在用自定义语言包</summary>
        public bool IsCustomLanguage => _isCustom;

        /// <summary>当前自定义语言包路径（非自定义时为空）</summary>
        public string CustomLanguagePath => _customPath;

        /// <summary>当前自定义语言包的文件名（仅用于显示）</summary>
        public string CustomLanguageFileName
            => string.IsNullOrEmpty(_customPath) ? string.Empty : Path.GetFileName(_customPath);

        /// <summary>
        /// 启动时若自定义语言包不可用，这里留下原因（null=无问题），供主界面弹窗提示。
        /// 注意：此时只回落到内置英文，**配置里仍保留用户的自定义项**，好让他到设置里改。
        /// </summary>
        public string LastLoadError { get; private set; }

        /// <summary>
        /// 设置窗预选用：返回**持久化配置**里的语言标识（zh / en / other）。
        /// 与运行时 <see cref="CurrentLangCode"/> 的差别：自定义包加载失败时，配置是 other、运行时是 en。
        /// </summary>
        public string GetInitialLanguageCode()
        {
            var s = SettingsManager.Language;
            if (s == null) return BuiltInZh;
            return s.Builtin ? (NormalizeBuiltInCode(s.Name) ?? BuiltInZh) : CustomCode;
        }

        // ───────────────────────── 启动加载 ─────────────────────────

        /// <summary>
        /// 按配置加载语言。**不写配置、不弹窗、不抛异常**：
        /// 自定义包读不出来时回落内置英文并记录 <see cref="LastLoadError"/>，配置原样保留。
        /// </summary>
        private void ApplyFromSettings()
        {
            LastLoadError = null;
            var s = SettingsManager.Language;

            if (s == null || s.Builtin)
            {
                string code = NormalizeBuiltInCode(s?.Name) ?? BuiltInZh;
                LoadBuiltInCore(code);
                NotifyAntdUiLanguage(code);   // 启动时也让 AntdUI 组件的排版与语言一致
                return;
            }

            if (TryLoadCustomCore(s.Path, out string error))
            {
                _currentLangCode = CustomCode;
                _isCustom = true;
                _customPath = s.Path;
                NotifyAntdUiLanguage(BuiltInZh);   // 自定义包按中文语系（AntdUI 内置文案随中文）
                return;
            }

            LoadBuiltInCore(FallbackCode);
            NotifyAntdUiLanguage(FallbackCode);
            LastLoadError = error;
        }

        /// <summary>只换字典与运行态字段，不写配置、不触发事件</summary>
        private bool LoadBuiltInCore(string code)
        {
            string resource = BuiltInResourceName(code);
            if (resource == null) return false;

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null)
                {
                    // 资源名（含大小写）对不上是最容易踩的坑：这里必须留痕，别静默变成"界面全空"
                    LogService.Error($"[language] 内置语言资源未找到: {resource}");
                    _currentLanguage = new Dictionary<string, string>();
                    _currentLangCode = BuiltInZh;
                    _isCustom = false;
                    _customPath = null;
                    return false;
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var dict = ParseLanguageJson(reader.ReadToEnd(), out string error);
                    if (dict == null)
                    {
                        LogService.Error($"[language] 内置语言解析失败: {resource} - {error}");
                        dict = new Dictionary<string, string>();
                    }
                    _currentLanguage = dict;
                    _currentLangCode = NormalizeBuiltInCode(code) ?? BuiltInZh;
                    _isCustom = false;
                    _customPath = null;
                    return true;
                }
            }
        }

        /// <summary>只读盘解析自定义语言包并换字典，写配置的活交给调用方</summary>
        private bool TryLoadCustomCore(string path, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = GetText("Language.Error.EmptyPath", "未指定语言文件路径");
                return false;
            }

            try
            {
                if (!File.Exists(path))
                {
                    error = string.Format(GetText("Language.Error.FileNotFound", "语言文件不存在：{0}"), path);
                    return false;
                }

                // 走本工具的自动检测编码工作流（BOM → 严格 UTF-8 → 本机 ACP）：
                // GBK/ANSI 存的语言包也能读；纯 ASCII 内容各编码字节相同，自动短路
                string json = TextFileHelper.ReadText(path);
                var dict = ParseLanguageJson(json, out string parseError);
                if (dict == null)
                {
                    error = string.Format(GetText("Language.Error.ParseFailed", "语言文件格式错误：{0}"), parseError);
                    return false;
                }
                if (dict.Count == 0)
                {
                    error = GetText("Language.Error.EmptyFile", "语言文件内容为空");
                    return false;
                }

                _currentLanguage = dict;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>解析"键→文案"的语言 json。失败返回 null 并给出原因。</summary>
        private static Dictionary<string, string> ParseLanguageJson(string json, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "内容为空";
                return null;
            }

            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, LangJsonOptions);
                if (dict == null)
                {
                    error = "解析结果为空";
                    return null;
                }
                return dict;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 读语言 json 的选项：容忍 <c>//</c> 注释与尾逗号（比内置包更宽容），属性名不区分大小写。
        /// </summary>
        private static readonly JsonSerializerOptions LangJsonOptions = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
        };

        // ───────────────────────── 对外切换 ─────────────────────────

        /// <summary>切换到内置语言（改运行态 + 写配置 + 触发刷新）。返回是否成功。</summary>
        public bool LoadBuiltIn(string code)
        {
            string normalized = NormalizeBuiltInCode(code) ?? BuiltInZh;
            if (!LoadBuiltInCore(normalized)) return false;

            SettingsManager.Language = new LanguageSetting { Name = normalized, Builtin = true };
            SettingsManager.Save();

            LanguageChanged?.Invoke();
            NotifyAntdUiLanguage(normalized);
            return true;
        }

        /// <summary>
        /// 应用自定义语言包（改运行态 + 写配置 + 触发刷新）。
        /// **失败时不改任何状态、不落盘**，把原因交给调用方去提示。
        /// </summary>
        public bool TryLoadCustom(string path, out string error)
        {
            if (!TryLoadCustomCore(path, out error)) return false;

            _currentLangCode = CustomCode;
            _isCustom = true;
            _customPath = path;

            SettingsManager.Language = new LanguageSetting { Name = CustomCode, Builtin = false, Path = path };
            SettingsManager.Save();

            LanguageChanged?.Invoke();
            NotifyAntdUiLanguage(BuiltInZh);
            return true;
        }

        /// <summary>
        /// 通知 AntdUI 组件库"语言已切换"（<c>AntdUI.Localization.SetLanguage</c> → 派发 EventType.LANG）。
        ///
        /// <para>为什么必须调用：AntdUI 的 Button/Label 等控件靠 <c>EventType.LANG</c> 触发
        /// <c>BeforeAutoSize()</c> 重排；只改控件的 <c>Text</c> 属性时，其排版/渲染缓存不会更新，
        /// 表现就是**界面上仍显示旧语言文字，直到切换选项卡等操作强制重排**（用户实测反馈）。
        /// 同时 AntdUI 内置文案（如空数据「暂无数据」）也依赖该机制。</para>
        ///
        /// <para>调用时机：必须在 <c>LanguageChanged?.Invoke()</c> **之后**——本工具的刷新器
        /// 先把各处 Text 设成新语言，AntdUI 再按新文本重排，顺序反了会按旧文本测量。</para>
        /// </summary>
        private static void NotifyAntdUiLanguage(string code)
        {
            try
            {
                string culture = string.Equals(code, BuiltInEn, StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
                AntdUI.Localization.SetLanguage(culture);   // 派发 EventType.LANG → 各控件 BeforeAutoSize 重排
            }
            catch { /* 非 GUI 环境（CLI）无控件监听，忽略 */ }
        }

        /// <summary>只校验一个语言文件能不能用（设置窗「导入」按钮用），不改运行态、不落盘。</summary>
        public static bool ValidateLanguageFile(string path, out string error)
        {
            error = null;
            var probe = _instance.Value;   // 确保单例已初始化（借用它的多语言文案）

            if (string.IsNullOrWhiteSpace(path))
            {
                error = probe.GetText("Language.Error.EmptyPath", "未指定语言文件路径");
                return false;
            }
            try
            {
                if (!File.Exists(path))
                {
                    error = string.Format(probe.GetText("Language.Error.FileNotFound", "语言文件不存在：{0}"), path);
                    return false;
                }

                // 走本工具的自动检测编码工作流（BOM → 严格 UTF-8 → 本机 ACP），与 TryLoadCustomCore 同口径
                string json = TextFileHelper.ReadText(path);
                var dict = ParseLanguageJson(json, out string parseError);
                if (dict == null)
                {
                    error = string.Format(probe.GetText("Language.Error.ParseFailed", "语言文件格式错误：{0}"), parseError);
                    return false;
                }
                if (dict.Count == 0)
                {
                    error = probe.GetText("Language.Error.EmptyFile", "语言文件内容为空");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ───────────────────────── 导出内置语言包 ─────────────────────────

        /// <summary>读取内置语言包的 json 原文（导出用；顺带去掉可能的 BOM）</summary>
        public static bool TryGetBuiltInJson(string code, out string json)
        {
            json = null;
            string resource = BuiltInResourceName(code);
            if (resource == null) return false;

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null) return false;
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }
            }
            if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
            return true;
        }

        /// <summary>
        /// 把两份内置语言包导出到指定目录（目录不存在则创建，与项目里输出目录一律自动创建一致）。
        /// 返回是否全部成功；written 为已写出的文件名列表。
        /// </summary>
        public static bool TryExportBuiltInPacks(string folder, out string error, out List<string> written)
        {
            error = null;
            written = new List<string>();

            if (string.IsNullOrWhiteSpace(folder))
            {
                error = "未指定导出目录";
                return false;
            }

            try
            {
                Directory.CreateDirectory(folder);
                foreach (string code in BuiltInCodes)
                {
                    string fileName = GetBuiltInFileName(code);
                    if (!TryGetBuiltInJson(code, out string json))
                    {
                        error = $"内置语言包资源缺失: {fileName}";
                        return false;
                    }

                    string target = Path.Combine(folder, fileName);
                    File.WriteAllText(target, json, new UTF8Encoding(false));
                    written.Add(fileName);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ───────────────────────── 取文案 ─────────────────────────

        public string GetText(string key, string defaultValue = "")
        {
            return _currentLanguage.TryGetValue(key, out var value) ? value : defaultValue;
        }

        /// <summary>
        /// 取当前语言下 key 的值，并区分「key 不存在」与「key 存在但值为空」。
        /// <para>为什么需要：个别 key 在当前语言下**故意为空**（如英文无「个」这样的量词单位），
        /// 调用方（UiTextRefresher）需要把控件刷成空字符串，而不是当成"没有这个 key"而跳过。</para>
        /// </summary>
        public bool TryGetText(string key, out string value)
        {
            return _currentLanguage.TryGetValue(key, out value);
        }
    }
}
