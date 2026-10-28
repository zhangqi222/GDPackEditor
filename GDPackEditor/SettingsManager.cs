using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace GDPackEditor
{
    public class LanguagePackEntry
    {
        public string Name { get; set; }
        public string Note { get; set; }
        public string FilePath { get; set; }
        [JsonIgnore]
        public bool IsChecked { get; set; }
        [JsonIgnore]
        public string DisplayNote => !string.IsNullOrEmpty(Note) ? Note : Name;
    }

    /// <summary>
    /// DBR 字段名本地化字典条目（用于"字段名本地化"管理弹窗）
    /// </summary>
    public class DbrKeyMappingEntry
    {
        public string Id { get; set; }              // 唯一标识（GUID）
        public string Note { get; set; }            // 备注
        public string FilePath { get; set; }        // 路径（内置条目用内置标识，外部用实际路径）
        public string Type { get; set; }            // "BuiltIn" / "External"
        public bool IsChecked { get; set; }         // 是否启用

        [JsonIgnore]
        public string DisplayNote => !string.IsNullOrEmpty(Note) ? Note : FilePath;

        [JsonIgnore]
        public string DisplayType
        {
            get
            {
                if (Type == "BuiltIn")
                    return LanguageManager.Instance.GetText("Editor.DbrKeyMappingTypeBuiltIn", "内置");
                return LanguageManager.Instance.GetText("Editor.DbrKeyMappingTypeExternal", "外部");
            }
        }

        [JsonIgnore]
        public string DisplayOperation
        {
            get
            {
                if (Type == "BuiltIn")
                    return "";
                return LanguageManager.Instance.GetText("TemplateManager.ChangeButton", "更改")
                    + " | " + LanguageManager.Instance.GetText("TemplateManager.ClearButton", "清除");
            }
        }
    }

    /// <summary>
    /// 文件夹显示本地化字典条目（用于"本地化文件夹显示"管理弹窗）
    /// </summary>
    public class FolderMappingEntry
    {
        public string Id { get; set; }              // 唯一标识（GUID）
        public string Note { get; set; }            // 备注
        public string FilePath { get; set; }        // 路径（内置条目用内置标识，外部用实际路径）
        public string Type { get; set; }            // "BuiltIn" / "External"
        public bool IsChecked { get; set; }         // 是否启用

        [JsonIgnore]
        public string DisplayNote => !string.IsNullOrEmpty(Note) ? Note : FilePath;

        [JsonIgnore]
        public string DisplayType
        {
            get
            {
                if (Type == "BuiltIn")
                    return LanguageManager.Instance.GetText("Editor.DbrKeyMappingTypeBuiltIn", "内置");
                return LanguageManager.Instance.GetText("Editor.DbrKeyMappingTypeExternal", "外部");
            }
        }

        [JsonIgnore]
        public string DisplayOperation
        {
            get
            {
                if (Type == "BuiltIn")
                    return "";
                return LanguageManager.Instance.GetText("TemplateManager.ChangeButton", "更改")
                    + " | " + LanguageManager.Instance.GetText("TemplateManager.ClearButton", "清除");
            }
        }
    }

    public class FavoriteEntry
    {
        public string Path { get; set; }
        public string Note { get; set; }
    }

    public class RecentFileEntry
    {
        public string FilePath { get; set; }
        public DateTime OpenTime { get; set; }
    }

    /// <summary>
    /// 包内搜索页签的「搜索目录 / 搜索关键词」历史条目。
    /// <para><b>置顶</b>语义：置顶项排在列表最上方、以主色显示、**不计入「清空全部」**，也**不占普通项名额**。</para>
    /// <para>列表不变量：**置顶项在前（顺序稳定）、普通项在后（最新在前）** —— 显示时直接按列表顺序渲染即可。</para>
    /// </summary>
    /// <summary>包内搜索的三套历史（目录 / 文件名条件 / 内容条件，各自独立、互不组合）</summary>
    public enum SearchHistoryKind
    {
        Dir = 0,
        Name,
        Content
    }

    public class SearchHistoryEntry
    {
        public string Value { get; set; }
        public bool Pinned { get; set; }
    }

    public class MergeHistoryEntry
    {
        public string Id { get; set; }
        public string BasePath { get; set; }
        public string ModPath { get; set; }
        public DateTime OpenTime { get; set; }
    }

    /// <summary>
    /// 批量编辑参数预设（弹窗左侧参数区的可保存/载入组合；不包含三态勾选与手改值）。
    /// Mode: 0=全部相同 1=单数步进 2=数组步进 3=固定分割
    /// </summary>
    public class BatchEditPreset
    {
        public string Name { get; set; }
        public int Mode { get; set; }
        public string Formula { get; set; }
        public string Prefix { get; set; }
        public string Suffix { get; set; }
        public int Pad { get; set; }
        public string FragmentText { get; set; }
        public bool FragmentEnabled { get; set; }
        /// <summary>循环次数：留空=无限</summary>
        public string FragmentRounds { get; set; }
        public string SplitMethod { get; set; }
        public string SplitSymbol { get; set; }
        public string SplitContent { get; set; }
        public int SplitLength { get; set; }
        public bool SplitLoop { get; set; }
    }

    /// <summary>
    /// 模板条目（用于模板管理器）
    /// </summary>
    public class TemplateEntry
    {
        public string Id { get; set; }              // 唯一标识（GUID或自增）
        public string Note { get; set; }            // 描述/备注
        public string Path { get; set; }            // 路径（内置用版本号，自定义用实际路径）
        public string Type { get; set; }            // "BuiltIn" / "Arc" / "Folder"
        public bool IsChecked { get; set; }         // 是否启用
        public string BuiltInVersion { get; set; }  // 内置模板版本号（如"1.2"/"1.3"），非内置时为空

        // 以下为表格显示用的只读属性（不参与序列化）
        [JsonIgnore]
        public string DisplayType
        {
            get
            {
                if (Type == "BuiltIn")
                    return LanguageManager.Instance.GetText("TemplateManager.BuiltInType", "内置");
                if (Type == "Arc")
                    return LanguageManager.Instance.GetText("TemplateManager.ArcType", "ARC");
                if (Type == "Folder")
                    return LanguageManager.Instance.GetText("TemplateManager.FolderType", "文件夹");
                return Type;
            }
        }
        [JsonIgnore]
        public string DisplayOperation
        {
            get
            {
                if (Type == "BuiltIn")
                    return "";
                return LanguageManager.Instance.GetText("TemplateManager.ChangeButton", "更改")
                    + " | " + LanguageManager.Instance.GetText("TemplateManager.ClearButton", "清除");
            }
        }
    }

    public static class SettingsManager
    {
        private static readonly string SettingsDir;
        internal static readonly string SettingsFile;

        /// <summary>
        /// 「清除配置」抑制保存标志：ClearSettings 物理删除 settings.jsonc 后置位，此后所有
        /// <see cref="Save"/> 调用直接跳过 —— 否则关窗流程（窗口状态/列宽记忆/历史记录等）
        /// 会立即把内存配置原样写回磁盘，导致清除无效。标志随进程结束自然复位，重启后恢复默认。
        /// </summary>
        internal static bool SuppressSave;

        /// <summary>设置目录（%APPDATA%\GDPackEditor）；日志文件等与本目录同级存放，不另建子目录</summary>
        public static string SettingsDirectory => SettingsDir;

        /// <summary>跨进程/跨线程保护 settings.jsonc 读写的命名互斥锁（防止多开或后台线程并发写导致文件损坏/丢失）</summary>
        private static readonly Mutex _fileMutex;

        /// <summary>
        /// UI 语言（二级结构：name / builtin / path）。见 <see cref="LanguageSetting"/>。
        /// 旧版本这里是字符串（"zh-CN"/"en-US"），由 <see cref="LanguageSettingConverter"/> 自动迁移。
        /// </summary>
        public static LanguageSetting Language { get; set; } = new LanguageSetting();
        public static List<TemplateEntry> TemplateEntries { get; set; } = new List<TemplateEntry>();
        public static List<LanguagePackEntry> ImportedPacks { get; set; } = new List<LanguagePackEntry>();
        public static List<DbrKeyMappingEntry> DbrKeyMappingPacks { get; set; } = new List<DbrKeyMappingEntry>();
        public static List<FolderMappingEntry> FolderMappingPacks { get; set; } = new List<FolderMappingEntry>();
        public static bool ShowFileNotes { get; set; } = false;
        public static List<int> ActivePackKeys { get; set; } = new List<int>();
        public static bool BackupOriginalFile { get; set; } = true;
        /// <summary>是否显示详细过程日志（解包/打包每文件的 (N/M) 行），默认关闭以提升性能</summary>
        public static bool ShowDetailLog { get; set; } = false;
        /// <summary>
        /// 「详情记录在日志文件中」：过程性进度行（(N/M) 等）是否写入日志文件，**默认关闭**（这类行极易污染日志）。
        /// 仅在「日志显示详情」开启时才可在界面勾选（保存时按两者同时成立落库）。
        /// </summary>
        public static bool LogDetailToFile { get; set; } = false;
        /// <summary>「保存日志」开关：主日志 GDPackEditor.log 是否写入（界面日志显示不受影响），默认开</summary>
        public static bool LogFileEnabled { get; set; } = true;
        /// <summary>「单独保存操作日志」开关：以包命名的日志文件是否写入（保存/固实成功时生成），默认开</summary>
        public static bool PackageLogEnabled { get; set; } = true;
        /// <summary>日志单文件行数上限（超出从文件头部裁剪），默认 2000，范围 200~20000。
        /// 各包日志文件同样按此行数裁剪（用户决定：不再按保留天数删除）</summary>
        public static int LogMaxLines { get; set; } = 100000;
        /// <summary>是否启用界面自动缩放（跟随Windows系统缩放，最大锁定150%）。关闭时按100%设计尺寸清晰显示。重启生效。</summary>
        public static bool UiScaleEnabled { get; set; } = true;
        // ─────────────────────────── 设置 → 记住表格列宽 ───────────────────────────
        // 快照式列宽记忆（TableWidthStore）：开窗应用 / 关窗整表快照，按「列顺序」数组存储、不绑列 Key。
        // 数值为逻辑像素（与 DPI 缩放无关）；null/空 = 无记录 → 各表保持列定义默认宽度。
        /// <summary>记住表格列宽开关：文件列表（ARZ/ARC编辑器）</summary>
        public static bool TwEnabledFileList { get; set; } = true;
        /// <summary>记住表格列宽开关：DBR编辑器表格</summary>
        public static bool TwEnabledDbrGrid { get; set; } = true;
        /// <summary>记住表格列宽开关：MOD合并差异表</summary>
        public static bool TwEnabledMergeTable { get; set; } = true;
        /// <summary>记住表格列宽开关：包内搜索结果表</summary>
        public static bool TwEnabledSearchResults { get; set; } = true;
        /// <summary>列宽记录（逻辑像素，按列顺序）：文件列表</summary>
        public static List<int> TwWidthsFileList { get; set; }
        /// <summary>列宽记录（逻辑像素，按列顺序）：DBR编辑器表格</summary>
        public static List<int> TwWidthsDbrGrid { get; set; }
        /// <summary>列宽记录（逻辑像素，按列顺序）：MOD合并差异表</summary>
        public static List<int> TwWidthsMergeTable { get; set; }
        /// <summary>列宽记录（逻辑像素，按列顺序）：包内搜索结果表</summary>
        public static List<int> TwWidthsSearchResults { get; set; }
        // ─────────────────────────── 设置 → 检测 ───────────────────────────
        // 内容检测的逐项开关（plan_20260923 §批B）。
        // 除「值含换行」「值首尾空白」默认关外，其余默认开。
        // 「记录解压失败 / 记录无字段」属结构性异常，**不可关闭**（不在此列，始终报告）。

        /// <summary>检测项：值含换行（默认关）</summary>
        public static bool CheckNewline { get; set; } = false;
        /// <summary>检测项：值含逗号（默认开）</summary>
        public static bool CheckComma { get; set; } = true;
        /// <summary>检测项：值含控制字符（默认开）</summary>
        public static bool CheckControlChar { get; set; } = true;
        /// <summary>检测项：值首尾空白（读取时会被去掉，默认关）</summary>
        public static bool CheckTrimWhitespace { get; set; } = false;
        /// <summary>检测项：字段名首尾空白（读取时会被去掉并导致模板匹配失败，默认开）</summary>
        public static bool CheckKeyTrimWhitespace { get; set; } = true;
        /// <summary>检测项：字段不在模板中（需模板，默认开；模板未启用时自动跳过）</summary>
        public static bool CheckTplFieldMissing { get; set; } = true;
        /// <summary>检测项：字段值格式错误（以模板 type 为准，需模板，默认开）</summary>
        public static bool CheckTplValueFormat { get; set; } = true;
        /// <summary>检测项：非数组字段值含分号（默认开）</summary>
        public static bool CheckSemicolon { get; set; } = true;
        /// <summary>检测项：值过长，阈值 <see cref="LongValueThreshold"/>（默认开）</summary>
        public static bool CheckLongValue { get; set; } = true;
        /// <summary>检测项：同一记录内字段名重复出现（默认开）</summary>
        public static bool CheckDuplicateField { get; set; } = true;
        /// <summary>检测项：记录缺 templateName 行（默认开）</summary>
        public static bool CheckNoTemplateName { get; set; } = true;
        /// <summary>「值过长」检测阈值：实测样本 3_transmogsmith.dbr 最长字段值 91,073 字符 ×2 ≈ 18.2 万，取整 20 万
        /// （注意：与批量编辑门禁 DbrValueGuard.MaxValueLength=4096 是两回事——门禁管新输入值防误粘贴，检测管包内存量数据）</summary>
        public const int LongValueThreshold = 200000;

        // ─────────────────────────── 设置 → 编码 ───────────────────────────
        // 模型：**文件名 1 项 + 内容 2 项（打包 / 解包各一）**，三项都同时管 ARC 与 ARZ。
        // 名字与内容是**两套独立机制**，禁止互相套用（skill §1.2 R2.4）：
        //   · 名字 → 跟本机走（本机 ANSI 码页），规则来自容器格式约定；
        //   · 内容 → 零转码为底，统一档才按目标编码转。
        // 详见 plan_20260918_恐怖黎明包文件编码完善方案.md §3。

        /// <summary>文件名策略：按本机 ANSI 码页写入（官方行为，默认）</summary>
        public const string NameModeAcp = "Acp";
        /// <summary>文件名策略：过滤非 ASCII 名字（名字含非 ASCII 的文件不打包）</summary>
        public const string NameModeFilterAscii = "FilterAscii";

        /// <summary>内容策略：原样（零转码，官方行为，默认）</summary>
        public const string ContentModeOriginal = "Original";
        /// <summary>内容策略：统一转本机 ANSI 码页</summary>
        public const string ContentModeAnsi = "Ansi";
        /// <summary>内容策略：统一转 UTF-8 无 BOM</summary>
        public const string ContentModeUtf8NoBom = "Utf8NoBom";

        /// <summary>
        /// 文件名编码策略。**ARC 名字表 与 ARZ 记录名 共用同一个设置**（两者规则相同：都跟本机 ACP）。
        /// 默认 <see cref="NameModeAcp"/> = 官方行为。
        /// </summary>
        public static string NameMode { get; set; } = NameModeAcp;

        /// <summary>内容【打包】策略：ARC 的 .txt/.tpl/.lua ＋ ARZ 的 .dbr 值。默认原样 = 官方行为。</summary>
        public static string ContentPackMode { get; set; } = ContentModeOriginal;

        /// <summary>内容【解包】策略：解出的文件按什么编码落盘。默认原样 = 官方行为。</summary>
        public static string ContentUnpackMode { get; set; } = ContentModeOriginal;

        /// <summary>把内容策略解析为**目标编码**；返回 null = 原样档（零转码）</summary>
        public static Encoding ResolveContentTarget(string mode)
        {
            if (string.Equals(mode, ContentModeAnsi, StringComparison.OrdinalIgnoreCase)) return NameCodec.Acp;
            if (string.Equals(mode, ContentModeUtf8NoBom, StringComparison.OrdinalIgnoreCase)) return TextFileHelper.Utf8NoBomTarget;
            return null;
        }

        /// <summary>打包时文本内容的目标编码；null = 原样档</summary>
        public static Encoding ContentPackTarget => ResolveContentTarget(ContentPackMode);

        /// <summary>解包时文本内容的目标编码；null = 原样档</summary>
        public static Encoding ContentUnpackTarget => ResolveContentTarget(ContentUnpackMode);

        // ── 旧字段兼容桥：阶段③ 起 NameMode / ContentPackMode 才是唯一真源 ──

        /// <summary>
        /// 旧设置项「打包时跳过非 ASCII 文件名」= 文件名策略为 <see cref="NameModeFilterAscii"/>。
        /// **作用范围：ARC 名字表 + ARZ 记录名**（两者规则相同，都由该项决定）。
        /// 开启时含非 ASCII 的名字一律不写进包，并给汇总告警；只有"本机码页表示不了"的名字才强制跳过。
        /// </summary>
        public static bool IgnoreNonAsciiArcNames
        {
            get => string.Equals(NameMode, NameModeFilterAscii, StringComparison.OrdinalIgnoreCase);
            set => NameMode = value ? NameModeFilterAscii : NameModeAcp;
        }

        /// <summary>旧设置项「ARC 打包文本编码」= <see cref="ContentPackMode"/>（两套取值常量同域，可直接互转）</summary>
        public static string ArcTextEncoding
        {
            get => ContentPackMode;
            set => ContentPackMode = value;
        }

        /// <summary>原样打包：不做任何编码转换</summary>
        public const string ArcTextEncodingOriginal = ContentModeOriginal;
        /// <summary>转为 UTF-8 无 BOM</summary>
        public const string ArcTextEncodingUtf8NoBom = ContentModeUtf8NoBom;
        /// <summary>打包 ARC 时是否需要对文本内容做转码（原样档 = 不需要）</summary>
        public static bool ShouldNormalizeArcText => ContentPackTarget != null;
        /// <summary>主题模式：System=跟随系统 / Light=亮色 / Dark=暗色。重启生效。</summary>
        public static string ThemeMode { get; set; } = "System";
        /// <summary>主色方案：default=默认蓝 / emerald=翡翠绿 / violet=紫罗兰 / amber=琥珀橙。重启生效。</summary>
        public static string ThemeAccent { get; set; } = "default";
        /// <summary>解包ARZ是否单线程写盘。默认单线程：机械硬盘顺序写更快；SSD可切换多线程以充分利用并发</summary>
        public static bool UnpackSingleThread { get; set; } = true;
        /// <summary>打包ARC是否启用增量模式</summary>
        public static bool PackArcIncremental { get; set; } = false;
        /// <summary>打包ARC增量模式是否包含删除（增/改/删）；false=增/改</summary>
        public static bool PackArcIncrementalFull { get; set; } = false;
        /// <summary>打包ARZ读取模式是否单线程</summary>
        public static bool PackArzSingleThread { get; set; } = true;
        public static bool EditorRightPanelCollapsed { get; set; } = false;
        public static bool EditorLogPanelCollapsed { get; set; } = false;
        public static bool EditorLeftPanelCollapsed { get; set; } = false;
        public static bool EditorOutlinePanelCollapsed { get; set; } = false;
        public static bool MergeHistoryPanelCollapsed { get; set; } = false;
        public static bool MergeRightPanelCollapsed { get; set; } = false;
        /// <summary>MOD合并日志区折叠态（默认折叠：首次使用不占空间，合并时自动展开）</summary>
        public static bool MergeLogPanelCollapsed { get; set; } = true;
        public static bool EditorFlatViewMode { get; set; } = false;
        public static bool MergeFlatViewMode { get; set; } = false;

        // ── 设置 → 包内搜索页签（改动即存；页面构建时套用）──
        /// <summary>是否搜索 ARZ 包（默认 false = 不勾，由用户按需勾选）</summary>
        public static bool SearchIncludeArz { get; set; } = false;
        /// <summary>是否搜索 ARC 包</summary>
        public static bool SearchIncludeArc { get; set; } = true;
        /// <summary>是否包含子目录</summary>
        public static bool SearchIncludeSubDir { get; set; } = true;
        /// <summary>是否区分大小写（批21 起**仅作用于内容条件**，文件名条件固定不敏感）</summary>
        public static bool SearchCaseSensitive { get; set; } = false;
        /// <summary>包内搜索：**完全匹配（全）**开关 —— 内容条件按字面字符串完全相等匹配
        /// （带逗号=整行、dbr 不带逗号=k/v 分别比、通配符按字面量；方案 plan_20261008 §3）</summary>
        public static bool SearchExactMatch { get; set; } = false;

        /// <summary>包内搜索：**本地化反查**开关（语言包译名反查 tag；方案 plan_20260930）。
        /// 仅放在包内搜索页签内（不进设置窗口），但状态**持久化**（用户定稿：重启保持）</summary>
        public static bool SearchLangReverse { get; set; } = false;

        // ── 设置 → 包内搜索「历史记录」（目录 / 关键词各自独立，互不组合；改动即存）──
        /// <summary>搜索历史「普通项」上限（置顶项不占名额；超出时从最旧的普通项开始剔除）</summary>
        public const int SearchHistoryMaxItems = 30;

        // 三套历史各自独立（目录 / 文件名条件 / 内容条件），互不组合
        /// <summary>最近使用的搜索目录（置顶在前、普通项最新在前）</summary>
        public static List<SearchHistoryEntry> SearchRecentDirs { get; set; } = new List<SearchHistoryEntry>();
        /// <summary>最近使用的**文件名条件**（同上；含内置扩展名项，见 SearchReservedNameValues）</summary>
        public static List<SearchHistoryEntry> SearchRecentNames { get; set; } = new List<SearchHistoryEntry>();
        /// <summary>最近使用的**内容条件**（同上）</summary>
        public static List<SearchHistoryEntry> SearchRecentContents { get; set; } = new List<SearchHistoryEntry>();

        /// <summary>
        /// 文件名历史里的**内置项**（浮层固定分组：一键填入 .cnv / .qst 的扩展名筛选 —— 它们本质是扩展名筛选，属文件名框）。
        /// <para><b>精确屏蔽</b>：这些值**永不写入文件名历史**（否则会与内置分组重复，且可被误删/误置顶）；
        /// 同时覆盖 .cnv 与 *.cnv 两种写法 —— 用户怎么打都算"就是那个内置项"。</para>
        /// </summary>
        public static readonly string[] SearchReservedNameValues = { ".cnv", ".qst", "*.cnv", "*.qst" };

        /// <summary>文件名历史浮层里**内置分组显示**的两项（一键填入扩展名筛选；显示用规范写法，屏蔽覆盖两种写法）</summary>
        public static readonly string[] SearchBuiltinNameValues = { "*.cnv", "*.qst" };

        /// <summary>是否为内置扩展名筛选值（去首尾空白、忽略大小写，**精确匹配**）</summary>
        public static bool IsReservedNameValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            foreach (string r in SearchReservedNameValues)
                if (string.Equals(r, v, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ── 设置 → 主窗口状态（关闭/移动/缩放时自动保存）──
        /// <summary>
        /// 是否**保存过**主窗口状态（替代"X==0 &amp;&amp; Y==0 视为没保存过"的旧哨兵）。
        /// <para>旧写法在主屏左上角或贴边时会被误判成"首次运行"，被强行居中；本字段消除该歧义。</para>
        /// <para>旧配置无此字段（`bool?` 为 null）→ Load 时按"X/Y 非 0 或尺寸非默认"推断，老用户平滑过渡。</para>
        /// </summary>
        public static bool HasMainWindowState { get; set; } = false;
        /// <summary>主窗口是否处于最大化状态（保存时记；恢复时最后应用）</summary>
        public static bool IsMainWindowMaximized { get; set; } = false;
        /// <summary>主窗口**普通（非最大化）**状态的屏幕 X（绝对坐标，支持副屏）</summary>
        public static int MainWindowX { get; set; } = 0;
        /// <summary>主窗口**普通**状态的屏幕 Y</summary>
        public static int MainWindowY { get; set; } = 0;
        /// <summary>主窗口**普通**状态的宽（与 GuiMainForm.DefaultWindowSize 默认一致）</summary>
        public static int MainWindowWidth { get; set; } = 900;
        /// <summary>主窗口**普通**状态的高</summary>
        public static int MainWindowHeight { get; set; } = 700;

        /// <summary>批量编辑参数预设（弹窗内保存/载入/删除，持久化到 settings.jsonc）</summary>
        public static List<BatchEditPreset> BatchEditPresets { get; set; } = new List<BatchEditPreset>();

        private static Dictionary<string, List<FavoriteEntry>> _favorites = new Dictionary<string, List<FavoriteEntry>>
        {
            ["arz"] = new List<FavoriteEntry>(),
            ["arc"] = new List<FavoriteEntry>()
        };

        private static List<RecentFileEntry> _recentFiles = new List<RecentFileEntry>();

        private static List<MergeHistoryEntry> _mergeHistory = new List<MergeHistoryEntry>();

        static SettingsManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            SettingsDir = Path.Combine(appData, "GDPackEditor");
            SettingsFile = Path.Combine(SettingsDir, "settings.jsonc");
            try { _fileMutex = new Mutex(false, @"Local\GDPackEditor-settings-file"); }
            catch { _fileMutex = new Mutex(); }
            Load();
        }

        private static bool AcquireFileLock()
        {
            try { return _fileMutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { return true; }
            catch { return false; }
        }

        private static void ReleaseFileLock()
        {
            try { _fileMutex.ReleaseMutex(); } catch { }
        }

        public static void Load()
        {
            if (!AcquireFileLock()) { LogService.Warn("[settings] load skipped: file locked by another process"); return; }
            try
            {
                // no-file 探测：上锁后短时重试，避免与其它进程写入/替换的瞬时态误判为"文件不存在"
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (File.Exists(SettingsFile)) break;
                    if (attempt < 2) Thread.Sleep(300);
                }
                if (!File.Exists(SettingsFile)) { EnsureBuiltInTemplateEntries(); Save(); return; }
                string text = File.ReadAllText(SettingsFile, Encoding.UTF8);
                text = StripJsonComments(text);
                var data = JsonSerializer.Deserialize<SettingsData>(text);
                if (data == null) { LogService.Error("[settings] deserialize returned null, load skipped"); return; }
                // 语言：转换器已把旧的字符串格式（"zh-CN"/"en-US"）迁移为二级对象；
                // 字段缺失/为 null 时用默认（内置中文），不沿用内存里的旧值
                Language = data.Language ?? new LanguageSetting();
                TemplateEntries = data.TemplateEntries ?? new List<TemplateEntry>();
                ImportedPacks = data.ImportedPacks ?? new List<LanguagePackEntry>();
                DbrKeyMappingPacks = data.DbrKeyMappingPacks ?? new List<DbrKeyMappingEntry>();
                FolderMappingPacks = data.FolderMappingPacks ?? new List<FolderMappingEntry>();

                // 内置模板包确保式同步：旧标识符迁移 → 清理失效条目 → 补齐缺失内置包（用户排序不受影响）
                EnsureBuiltInTemplateEntries();
                NormalizeBuiltInTemplateNotes();   // 内置模板备注统一为文件名短名（含老配置迁移）
                ShowFileNotes = data.ShowFileNotes;
                ActivePackKeys = data.ActivePackKeys ?? new List<int>();
                BackupOriginalFile = data.BackupOriginalFile;
                ShowDetailLog = data.ShowDetailLog;
                LogDetailToFile = data.LogDetailToFile;
                LogFileEnabled = data.LogFileEnabled;
                PackageLogEnabled = data.PackageLogEnabled;
                // 同步到 LogService 的写入开关（启动即生效）
                LogService.FileEnabled = LogFileEnabled;
                LogService.PackageLogEnabled = PackageLogEnabled;
                // ── 设置 → 检测（逐项开关）──
                CheckNewline = data.CheckNewline;
                CheckComma = data.CheckComma;
                CheckControlChar = data.CheckControlChar;
                CheckTrimWhitespace = data.CheckTrimWhitespace;
                CheckKeyTrimWhitespace = data.CheckKeyTrimWhitespace;
                CheckTplFieldMissing = data.CheckTplFieldMissing;
                CheckTplValueFormat = data.CheckTplValueFormat;
                CheckSemicolon = data.CheckSemicolon;
                CheckLongValue = data.CheckLongValue;
                CheckDuplicateField = data.CheckDuplicateField;
                CheckNoTemplateName = data.CheckNoTemplateName;
                if (data.LogMaxLines > 0) LogMaxLines = data.LogMaxLines;
                // ── 设置 → 编码（阶段③ 模型：文件名 1 项 + 内容 2 项）──
                // 新键优先；旧键（ArcTextEncoding / IgnoreNonAsciiArcNames）仅作一次性兜底迁移，
                // 保证升级后行为与升级前一致（不静默改用户已保存的选择）。
                NameMode = string.IsNullOrEmpty(data.NameMode)
                    ? (data.IgnoreNonAsciiArcNames == true ? NameModeFilterAscii : NameModeAcp)
                    : data.NameMode;
                ContentPackMode = string.IsNullOrEmpty(data.ContentPackMode)
                    ? (string.IsNullOrEmpty(data.ArcTextEncoding) ? ContentModeOriginal : data.ArcTextEncoding)
                    : data.ContentPackMode;
                ContentUnpackMode = string.IsNullOrEmpty(data.ContentUnpackMode)
                    ? ContentModeOriginal
                    : data.ContentUnpackMode;
                UiScaleEnabled = data.UiScaleEnabled;
                // ── 设置 → 记住表格列宽（TableWidthStore 快照；null = 无记录走默认）──
                TwEnabledFileList = data.TwEnabledFileList;
                TwEnabledDbrGrid = data.TwEnabledDbrGrid;
                TwEnabledMergeTable = data.TwEnabledMergeTable;
                TwEnabledSearchResults = data.TwEnabledSearchResults;
                TwWidthsFileList = data.TwWidthsFileList;
                TwWidthsDbrGrid = data.TwWidthsDbrGrid;
                TwWidthsMergeTable = data.TwWidthsMergeTable;
                TwWidthsSearchResults = data.TwWidthsSearchResults;
                if (!string.IsNullOrEmpty(data.ThemeMode))
                    ThemeMode = data.ThemeMode;
                if (!string.IsNullOrEmpty(data.ThemeAccent))
                    ThemeAccent = data.ThemeAccent;
                UnpackSingleThread = data.UnpackSingleThread;
                PackArcIncremental = data.PackArcIncremental;
                PackArcIncrementalFull = data.PackArcIncrementalFull;
                PackArzSingleThread = data.PackArzSingleThread;
                EditorRightPanelCollapsed = data.EditorRightPanelCollapsed;
                EditorLogPanelCollapsed = data.EditorLogPanelCollapsed;
                EditorLeftPanelCollapsed = data.EditorLeftPanelCollapsed;
                EditorOutlinePanelCollapsed = data.EditorOutlinePanelCollapsed;
                MergeHistoryPanelCollapsed = data.MergeHistoryPanelCollapsed;
                MergeRightPanelCollapsed = data.MergeRightPanelCollapsed;
                MergeLogPanelCollapsed = data.MergeLogPanelCollapsed;
                EditorFlatViewMode = data.EditorFlatViewMode;
                MergeFlatViewMode = data.MergeFlatViewMode;
                SearchIncludeArz = data.SearchIncludeArz;
                SearchIncludeArc = data.SearchIncludeArc;
                SearchIncludeSubDir = data.SearchIncludeSubDir;
                SearchCaseSensitive = data.SearchCaseSensitive;
                SearchExactMatch = data.SearchExactMatch;
                SearchLangReverse = data.SearchLangReverse;
                // 搜索历史（目录/关键词各自独立）：旧配置无这两字段 → 保持空列表；有则规范化（置顶在前）
                SearchRecentDirs = NormalizeSearchHistory(data.SearchRecentDirs);
                SearchRecentNames = NormalizeSearchHistory(data.SearchRecentNames);
                SearchRecentContents = NormalizeSearchHistory(data.SearchRecentContents);
                // 自愈：清掉已混入的内置扩展名项（含历史版本/手工改配置留下的）
                SearchRecentNames.RemoveAll(e => IsReservedNameValue(e.Value));
                // 主窗口状态：宽高为 0（旧配置缺字段）时保持默认，避免把窗口设成 0x0
                IsMainWindowMaximized = data.IsMainWindowMaximized;
                MainWindowX = data.MainWindowX;
                MainWindowY = data.MainWindowY;
                if (data.MainWindowWidth > 0) MainWindowWidth = data.MainWindowWidth;
                if (data.MainWindowHeight > 0) MainWindowHeight = data.MainWindowHeight;
                // "是否保存过"：新配置直接读；旧配置无该字段 → 按旧哨兵口径回推（保证老用户不被强行居中）
                HasMainWindowState = data.HasMainWindowState
                    ?? (data.MainWindowX != 0 || data.MainWindowY != 0
                        || data.MainWindowWidth != 900 || data.MainWindowHeight != 700);
                BatchEditPresets = data.BatchEditPresets ?? new List<BatchEditPreset>();
                if (data.Favorites != null)
                    _favorites = data.Favorites;
                if (data.RecentFiles != null)
                    _recentFiles = data.RecentFiles;
                if (data.MergeHistory != null)
                    _mergeHistory = data.MergeHistory;
            }
            catch (Exception ex)
            {
                LogService.Error($"[settings] load failed: {ex.Message}");
            }
            finally
            {
                ReleaseFileLock();
            }
        }

        /// <summary>旧版内置模板备注的默认前缀（tt300 = 内置模板来源 GD小白丁 / TT300），仅用于识别旧默认文案</summary>
        public const string BuiltInNotePrefix = "tt300_";

        /// <summary>内置模板的默认备注：标识符去掉 templates- 前缀的短名（如 01-1.2.1.6-tt300）</summary>
        public static string BuiltInTemplateDefaultNote(string id) => Program.BuiltInShortName(id);

        /// <summary>
        /// 内置模板备注归一：仅当备注为空、旧默认文案（tt300_旧版本 / 内置模板 x.y / Built-in x.y）时
        /// 改写为新默认短名；用户自己改过的备注保持不动。
        /// </summary>
        private static void NormalizeBuiltInTemplateNotes()
        {
            foreach (var e in TemplateEntries)
            {
                if (e == null || !string.Equals(e.Type, "BuiltIn", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(e.BuiltInVersion)) continue;
                string want = BuiltInTemplateDefaultNote(e.BuiltInVersion);
                string note = e.Note?.Trim();
                bool isOldDefault = string.IsNullOrEmpty(note)
                    || note.StartsWith(BuiltInNotePrefix, StringComparison.OrdinalIgnoreCase)               // 旧默认 tt300_1.x
                    || string.Equals(note, "内置模板 " + e.BuiltInVersion, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(note, "Built-in " + e.BuiltInVersion, StringComparison.OrdinalIgnoreCase);
                if (isOldDefault) e.Note = want;
            }
        }

        /// <summary>
        /// 内置模板包"确保式"同步（每次加载配置后调用）：
        /// ① 旧版本号标识符（"1.2"/"1.3"）按「版本段前缀匹配」迁移到新文件名标识符（如 templates-01-1.2.1.6-tt300）；
        /// ② 移除已不存在于内嵌资源的内置条目；
        /// ③ 新发现的内嵌包按文件名自然序追加到列表末尾（已有条目顺序不动，用户拖拽排序永远优先）。
        /// </summary>
        public static void EnsureBuiltInTemplateEntries()
        {
            var packs = Program.GetBuiltInTemplateVersions();
            if (packs.Count == 0) return;

            // ① 旧版本号 → 新标识符迁移（旧值只有历史默认的 "1.2"/"1.3"，凡已带 templates- 前缀的视为已迁移）
            for (int i = TemplateEntries.Count - 1; i >= 0; i--)
            {
                var e = TemplateEntries[i];
                if (e == null || !string.Equals(e.Type, "BuiltIn", StringComparison.OrdinalIgnoreCase)) continue;
                string ver = e.BuiltInVersion;
                if (string.IsNullOrEmpty(ver) || ver.StartsWith("templates-", StringComparison.OrdinalIgnoreCase)) continue;

                string matched = packs.FirstOrDefault(p => MatchesLegacyVersion(p, ver));
                if (matched == null)
                {
                    TemplateEntries.RemoveAt(i);
                    LogService.Warn($"[settings] 内置模板条目已移除（找不到对应内嵌包）: {ver}");
                    continue;
                }

                // 备注仍是旧默认文案 → 直接改为新默认短名（用户自改的备注不动）
                string note = e.Note?.Trim();
                if (string.IsNullOrEmpty(note)
                    || string.Equals(note, "内置模板 " + ver, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(note, "Built-in " + ver, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(note, BuiltInNotePrefix + ver, StringComparison.OrdinalIgnoreCase))
                {
                    e.Note = BuiltInTemplateDefaultNote(matched);
                }
                e.BuiltInVersion = matched;
                e.Path = matched;
                LogService.Info($"[settings] 内置模板条目已迁移: {ver} -> {matched}");
            }

            // ② 清理标识符已失效的内置条目（如发行包调整后残留）
            for (int i = TemplateEntries.Count - 1; i >= 0; i--)
            {
                var e = TemplateEntries[i];
                if (e == null || !string.Equals(e.Type, "BuiltIn", StringComparison.OrdinalIgnoreCase)) continue;
                if (!packs.Contains(e.BuiltInVersion))
                {
                    TemplateEntries.RemoveAt(i);
                    LogService.Warn($"[settings] 内置模板条目已移除（内嵌包不存在）: {e.BuiltInVersion}");
                }
            }

            // ③ 补齐缺失的内置包条目：追加到末尾，彼此保持文件名自然序
            //   （默认勾选规则：文件名含 official 才默认勾选；无任何 official 包时退回全部勾选）
            foreach (var id in packs)
            {
                bool exists = TemplateEntries.Any(p =>
                    p != null &&
                    string.Equals(p.Type, "BuiltIn", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.BuiltInVersion, id, StringComparison.OrdinalIgnoreCase));
                if (exists) continue;
                TemplateEntries.Add(new TemplateEntry
                {
                    Id = "builtin-" + id,
                    Note = BuiltInTemplateDefaultNote(id),
                    Path = id,
                    Type = "BuiltIn",
                    IsChecked = Program.BuiltInDefaultChecked(packs, id),
                    BuiltInVersion = id
                });
            }
        }

        /// <summary>
        /// 内置包标识符是否匹配旧版本号：去 templates- 前缀后按 - 分段，
        /// 任一段与旧版本号相等，或以「旧版本号 + .」开头（1.2 匹配 1.2.1.6，不会误匹配 1.35）。
        /// </summary>
        private static bool MatchesLegacyVersion(string id, string legacyVersion)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(legacyVersion)) return false;
            string name = id.StartsWith("templates-", StringComparison.OrdinalIgnoreCase)
                ? id.Substring("templates-".Length) : id;
            foreach (var seg in name.Split('-'))
            {
                if (string.Equals(seg, legacyVersion, StringComparison.OrdinalIgnoreCase)) return true;
                if (seg.StartsWith(legacyVersion + ".", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 确保内置 DBR 字段名字典条目存在（必须在 SettingsManager 静态初始化完成、LanguageManager 就绪后调用，
        /// 否则会与 LanguageManager 构造函数形成循环初始化依赖）
        /// </summary>
        public static void EnsureDefaultDbrKeyMappingEntries()
        {
            // 迁移：移除旧版单条内置条目（Id="builtin-dbrkey-cn"），改为每条内嵌文件独立一条
            var oldBuiltIn = DbrKeyMappingPacks.FirstOrDefault(p => p.Type == "BuiltIn" && p.Id == "builtin-dbrkey-cn");
            if (oldBuiltIn != null)
            {
                DbrKeyMappingPacks.Remove(oldBuiltIn);
            }

            // 逐个感知内嵌资源，确保每个都有自己的勾选条目
            var builtInFileNames = DbrKeyMapping.GetBuiltInFileNames();
            foreach (var fname in builtInFileNames)
            {
                bool exists = DbrKeyMappingPacks.Any(p =>
                    p.Type == "BuiltIn" &&
                    string.Equals(p.FilePath, fname, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    DbrKeyMappingPacks.Add(new DbrKeyMappingEntry
                    {
                        Id = "builtin-" + fname,
                        Note = LanguageManager.Instance.GetText("Editor.DbrKeyMappingBuiltInNote", "内置字段名字典"),
                        FilePath = fname,
                        Type = "BuiltIn",
                        // 默认不启用（与文件夹注释字典一致）：默认保持干净，由用户自行勾选开启
                        IsChecked = false
                    });
                }
            }
        }

        /// <summary>
        /// 确保内置文件夹注释字典条目存在（默认不启用，保持原"显示文件夹备注"关闭的默认行为）。
        /// 必须在 SettingsManager 静态初始化完成、LanguageManager 就绪后调用。
        /// </summary>
        public static void EnsureDefaultFolderMappingEntries()
        {
            var builtInFileNames = FolderMapping.GetBuiltInFileNames();
            foreach (var fname in builtInFileNames)
            {
                bool exists = FolderMappingPacks.Any(p =>
                    p.Type == "BuiltIn" &&
                    string.Equals(p.FilePath, fname, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    FolderMappingPacks.Add(new FolderMappingEntry
                    {
                        Id = "builtin-" + fname,
                        Note = LanguageManager.Instance.GetText("Editor.FolderMappingBuiltInNote", "内置文件夹注释字典"),
                        FilePath = fname,
                        Type = "BuiltIn",
                        IsChecked = false
                    });
                }
            }
        }

        public static void Save()
        {
            // 「清除配置」后抑制回写（见 SuppressSave 注释）
            if (SuppressSave) return;
            if (!AcquireFileLock()) { LogService.Warn("[settings] save skipped: file locked by another process"); return; }
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var data = new SettingsData
                {
                    Language = Language,
                    TemplateEntries = TemplateEntries,
                    ImportedPacks = ImportedPacks,
                    DbrKeyMappingPacks = DbrKeyMappingPacks,
                    FolderMappingPacks = FolderMappingPacks,
                    ShowFileNotes = ShowFileNotes,
                    ActivePackKeys = ActivePackKeys,
                    BackupOriginalFile = BackupOriginalFile,
                    ShowDetailLog = ShowDetailLog,
                    LogDetailToFile = LogDetailToFile,
                    LogFileEnabled = LogFileEnabled,
                    PackageLogEnabled = PackageLogEnabled,
                    LogMaxLines = LogMaxLines,
                    CheckNewline = CheckNewline,
                    CheckComma = CheckComma,
                    CheckControlChar = CheckControlChar,
                    CheckTrimWhitespace = CheckTrimWhitespace,
                    CheckKeyTrimWhitespace = CheckKeyTrimWhitespace,
                    CheckTplFieldMissing = CheckTplFieldMissing,
                    CheckTplValueFormat = CheckTplValueFormat,
                    CheckSemicolon = CheckSemicolon,
                    CheckLongValue = CheckLongValue,
                    CheckDuplicateField = CheckDuplicateField,
                    CheckNoTemplateName = CheckNoTemplateName,
                    NameMode = NameMode,
                    ContentPackMode = ContentPackMode,
                    ContentUnpackMode = ContentUnpackMode,
                    UiScaleEnabled = UiScaleEnabled,
                    TwEnabledFileList = TwEnabledFileList,
                    TwEnabledDbrGrid = TwEnabledDbrGrid,
                    TwEnabledMergeTable = TwEnabledMergeTable,
                    TwEnabledSearchResults = TwEnabledSearchResults,
                    TwWidthsFileList = TwWidthsFileList,
                    TwWidthsDbrGrid = TwWidthsDbrGrid,
                    TwWidthsMergeTable = TwWidthsMergeTable,
                    TwWidthsSearchResults = TwWidthsSearchResults,
                    ThemeMode = ThemeMode,
                    ThemeAccent = ThemeAccent,
                    UnpackSingleThread = UnpackSingleThread,
                    PackArcIncremental = PackArcIncremental,
                    PackArcIncrementalFull = PackArcIncrementalFull,
                    PackArzSingleThread = PackArzSingleThread,
                    EditorRightPanelCollapsed = EditorRightPanelCollapsed,
                    EditorLogPanelCollapsed = EditorLogPanelCollapsed,
                    EditorLeftPanelCollapsed = EditorLeftPanelCollapsed,
                    EditorOutlinePanelCollapsed = EditorOutlinePanelCollapsed,
                    MergeHistoryPanelCollapsed = MergeHistoryPanelCollapsed,
                    MergeRightPanelCollapsed = MergeRightPanelCollapsed,
                    MergeLogPanelCollapsed = MergeLogPanelCollapsed,
                    EditorFlatViewMode = EditorFlatViewMode,
                    MergeFlatViewMode = MergeFlatViewMode,
                    SearchIncludeArz = SearchIncludeArz,
                    SearchIncludeArc = SearchIncludeArc,
                    SearchIncludeSubDir = SearchIncludeSubDir,
                    SearchCaseSensitive = SearchCaseSensitive,
                    SearchExactMatch = SearchExactMatch,
                    SearchLangReverse = SearchLangReverse,
                    SearchRecentDirs = SearchRecentDirs,
                    SearchRecentNames = SearchRecentNames,
                    SearchRecentContents = SearchRecentContents,
                    HasMainWindowState = HasMainWindowState,
                    IsMainWindowMaximized = IsMainWindowMaximized,
                    MainWindowX = MainWindowX,
                    MainWindowY = MainWindowY,
                    MainWindowWidth = MainWindowWidth,
                    MainWindowHeight = MainWindowHeight,
                    BatchEditPresets = BatchEditPresets,
                    Favorites = _favorites,
                    RecentFiles = _recentFiles,
                    MergeHistory = _mergeHistory
                };
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string json = JsonSerializer.Serialize(data, options);
                string tmpFile = SettingsFile + ".tmp";
                File.WriteAllText(tmpFile, json, new UTF8Encoding(false));
                File.Move(tmpFile, SettingsFile, true);
            }
            catch (Exception ex)
            {
                LogService.Error($"[settings] save failed: {ex.Message}");
            }
            finally
            {
                ReleaseFileLock();
            }
        }

        public static List<FavoriteEntry> GetFavorites(string key)
        {
            if (!_favorites.ContainsKey(key))
                _favorites[key] = new List<FavoriteEntry>();
            return _favorites[key];
        }

        public static void SetFavorites(string key, List<FavoriteEntry> list)
        {
            _favorites[key] = list;
            Save();
        }

        private static string StripJsonComments(string json)
        {
            var sb = new StringBuilder();
            using var reader = new StringReader(json);
            string line;
            while ((line = reader.ReadLine()) != null)
                sb.AppendLine(StripLineComment(line));
            return sb.ToString();
        }

        /// <summary>仅删除引号外的 // 注释，字符串值内的 //（如网络路径）不被误删</summary>
        private static string StripLineComment(string line)
        {
            bool inString = false;
            for (int i = 0; i < line.Length - 1; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    int backslashes = 0;
                    for (int j = i - 1; j >= 0 && line[j] == '\\'; j--)
                        backslashes++;
                    if (backslashes % 2 == 0)
                        inString = !inString;
                    continue;
                }
                if (!inString && c == '/' && line[i + 1] == '/')
                    return line[..i];
            }
            return line;
        }

        private class SettingsData
        {
            /// <summary>UI 语言。用转换器同时吃住旧格式（字符串 "zh-CN"）与新格式（{name,builtin,path}）</summary>
            [JsonConverter(typeof(LanguageSettingConverter))]
            public LanguageSetting Language { get; set; }
            public List<TemplateEntry> TemplateEntries { get; set; }
            public List<LanguagePackEntry> ImportedPacks { get; set; }
            public List<DbrKeyMappingEntry> DbrKeyMappingPacks { get; set; }
            public List<FolderMappingEntry> FolderMappingPacks { get; set; }
            public bool ShowFileNotes { get; set; }
            public List<int> ActivePackKeys { get; set; }
            public bool BackupOriginalFile { get; set; } = true;
            public bool ShowDetailLog { get; set; } = false;
            public bool LogDetailToFile { get; set; } = false;
            public bool LogFileEnabled { get; set; } = true;
            public bool PackageLogEnabled { get; set; } = true;
            public int LogMaxLines { get; set; } = 100000;
            public int LogReportKeepDays { get; set; } = 30;
            // ── 设置 → 检测（逐项开关；默认值与静态属性一致）──
            public bool CheckNewline { get; set; } = false;
            public bool CheckComma { get; set; } = true;
            public bool CheckControlChar { get; set; } = true;
            public bool CheckTrimWhitespace { get; set; } = false;
            public bool CheckKeyTrimWhitespace { get; set; } = true;
            public bool CheckTplFieldMissing { get; set; } = true;
            public bool CheckTplValueFormat { get; set; } = true;
            public bool CheckSemicolon { get; set; } = true;
            public bool CheckLongValue { get; set; } = true;
            public bool CheckDuplicateField { get; set; } = true;
            public bool CheckNoTemplateName { get; set; } = true;
            // ── 设置 → 编码（阶段③ 模型）──
            /// <summary>文件名策略：Acp（官方行为）| FilterAscii</summary>
            public string NameMode { get; set; }
            /// <summary>内容打包策略：Original（官方行为）| Ansi | Utf8NoBom</summary>
            public string ContentPackMode { get; set; }
            /// <summary>内容解包策略：Original（官方行为）| Ansi | Utf8NoBom</summary>
            public string ContentUnpackMode { get; set; }
            // ⚠ 废弃键（**只读**，Save 时不再写出）：
            //  · 仅用于"新键缺失时"的一次性静默迁移（旧版本写的文件里只有这两个键）。
            //  · **故意不给初值** —— 必须能区分"键不存在"（新装 / 清除配置 / 老版本没这键）
            //    与"用户确实设过"。若给初值（原为 "Utf8NoBom"/true），缺失时会被当成用户旧设置，
            //    导致新装用户的内容打包被静默改成"统一UTF8"、而不是官方原样。
            //  · 定案规则：**本版本写出的配置只含本版本认识的键**（旧升新、新降旧一致），
            //    故 Save 不再回写这两个键；再用 WhenWritingNull 让它们**彻底不出现在新文件里**
            //    （只加在属性上即可：仍能读旧文件，但值为 null 时不写出）。
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string ArcTextEncoding { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public bool? IgnoreNonAsciiArcNames { get; set; }
            public bool UiScaleEnabled { get; set; } = true;
            // ── 记住表格列宽（TableWidthStore 快照；List 为 null = 无记录，用各表列定义默认值）──
            public bool TwEnabledFileList { get; set; } = true;
            public bool TwEnabledDbrGrid { get; set; } = true;
            public bool TwEnabledMergeTable { get; set; } = true;
            public bool TwEnabledSearchResults { get; set; } = true;
            public List<int> TwWidthsFileList { get; set; }
            public List<int> TwWidthsDbrGrid { get; set; }
            public List<int> TwWidthsMergeTable { get; set; }
            public List<int> TwWidthsSearchResults { get; set; }
            public string ThemeMode { get; set; } = "System";
            public string ThemeAccent { get; set; } = "default";
            public bool UnpackSingleThread { get; set; } = true;
            public bool PackArcIncremental { get; set; } = false;
            public bool PackArcIncrementalFull { get; set; } = false;
            public bool PackArzSingleThread { get; set; } = true;
            public bool EditorRightPanelCollapsed { get; set; }
            public bool EditorLogPanelCollapsed { get; set; }
            public bool EditorLeftPanelCollapsed { get; set; }
            public bool EditorOutlinePanelCollapsed { get; set; }
            public bool MergeHistoryPanelCollapsed { get; set; }
            public bool MergeRightPanelCollapsed { get; set; }
            /// <summary>默认折叠（旧配置无此字段时反序列化取此默认值）</summary>
            public bool MergeLogPanelCollapsed { get; set; } = true;
            public bool EditorFlatViewMode { get; set; }
            public bool MergeFlatViewMode { get; set; }

            // ── 包内搜索页签 ──
            public bool SearchIncludeArz { get; set; } = false;
            public bool SearchIncludeArc { get; set; } = true;
            public bool SearchIncludeSubDir { get; set; } = true;
            public bool SearchCaseSensitive { get; set; } = false;
            public bool SearchExactMatch { get; set; } = false;
            public bool SearchLangReverse { get; set; } = false;
            /// <summary>搜索目录历史（置顶在前、普通项最新在前）</summary>
            public List<SearchHistoryEntry> SearchRecentDirs { get; set; }
            /// <summary>文件名条件历史（同上）</summary>
            public List<SearchHistoryEntry> SearchRecentNames { get; set; }
            /// <summary>内容条件历史（同上）</summary>
            public List<SearchHistoryEntry> SearchRecentContents { get; set; }
            /// <summary>主窗口状态（默认值必须与静态属性一致）</summary>
            /// <summary>是否保存过主窗口状态。用 `bool?` 以便区分"旧配置缺字段(null)"与"明确为 false"。</summary>
            public bool? HasMainWindowState { get; set; }
            public bool IsMainWindowMaximized { get; set; } = false;
            public int MainWindowX { get; set; } = 0;
            public int MainWindowY { get; set; } = 0;
            public int MainWindowWidth { get; set; } = 900;
            public int MainWindowHeight { get; set; } = 700;
            public List<BatchEditPreset> BatchEditPresets { get; set; }
            public Dictionary<string, List<FavoriteEntry>> Favorites { get; set; }
            public List<RecentFileEntry> RecentFiles { get; set; }
            public List<MergeHistoryEntry> MergeHistory { get; set; }
        }

        public static List<RecentFileEntry> GetRecentFiles()
        {
            return _recentFiles.OrderByDescending(f => f.OpenTime).ToList();
        }

        public static void AddRecentFile(string filePath)
        {
            _recentFiles.RemoveAll(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            _recentFiles.Insert(0, new RecentFileEntry
            {
                FilePath = filePath,
                OpenTime = DateTime.Now
            });
            if (_recentFiles.Count > 20)
                _recentFiles.RemoveRange(20, _recentFiles.Count - 20);
            Save();
        }

        public static void RemoveRecentFile(string filePath)
        {
            _recentFiles.RemoveAll(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        public static void ClearRecentFiles()
        {
            _recentFiles.Clear();
            Save();
        }

        // ═══════════ 包内搜索：历史记录（目录 / 关键词，各自独立）═══════════
        //
        // 与「最近文件」那套（RecentFileEntry + 历史弹窗）**完全独立**，互不影响。
        // 列表不变量：置顶项在前（顺序稳定）、普通项在后（最新在前）。

        /// <summary>取某套历史的列表（三套各自独立）</summary>
        public static List<SearchHistoryEntry> GetSearchHistory(SearchHistoryKind kind)
        {
            switch (kind)
            {
                case SearchHistoryKind.Name: return SearchRecentNames;
                case SearchHistoryKind.Content: return SearchRecentContents;
                default: return SearchRecentDirs;
            }
        }

        /// <summary>
        /// 记录一条历史（去重忽略大小写；已存在→普通项提到最前、置顶项位置不变；普通项超上限剔最旧）。
        /// <para>**文件名条件**里的内置扩展名项（.cnv/.qst/*.cnv/*.qst）精确屏蔽、不入历史。</para>
        /// </summary>
        public static void AddSearchHistoryValue(SearchHistoryKind kind, string value)
        {
            if (kind == SearchHistoryKind.Name && IsReservedNameValue(value)) return;
            SaveAfter(() => AddSearchHistory(GetSearchHistory(kind), value));
        }

        /// <summary>切换某条历史的置顶状态</summary>
        public static void ToggleSearchHistoryPin(SearchHistoryKind kind, string value)
            => SaveAfter(() => TogglePinCore(GetSearchHistory(kind), value));

        /// <summary>删除某条历史（置顶项也能单独删）</summary>
        public static void RemoveSearchHistoryValue(SearchHistoryKind kind, string value)
            => SaveAfter(() => RemoveSearchHistoryCore(GetSearchHistory(kind), value));

        /// <summary>清空某套历史（**保留置顶项**）</summary>
        public static void ClearUnpinnedSearchHistory(SearchHistoryKind kind)
            => SaveAfter(() => ClearUnpinnedSearchHistoryCore(GetSearchHistory(kind)));

        /// <summary>执行一次纯列表变更后再落盘（纯逻辑与持久化解耦，便于独立验证）</summary>
        private static void SaveAfter(Action mutate)
        {
            mutate();
            Save();
        }

        /// <summary>普通项区块的起始下标（= 置顶项个数）</summary>
        private static int FirstUnpinnedIndex(List<SearchHistoryEntry> list)
        {
            for (int i = 0; i < list.Count; i++)
                if (!list[i].Pinned) return i;
            return list.Count;
        }

        private static void AddSearchHistory(List<SearchHistoryEntry> list, string value)
        {
            if (list == null || string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim();

            var exist = list.FirstOrDefault(e => string.Equals(e.Value, value, StringComparison.OrdinalIgnoreCase));
            if (exist != null)
            {
                // 置顶项保持原位（不打乱用户自己置顶的顺序）；普通项提到普通区块最前
                if (!exist.Pinned)
                {
                    list.Remove(exist);
                    list.Insert(FirstUnpinnedIndex(list), exist);
                }
            }
            else
            {
                list.Insert(FirstUnpinnedIndex(list), new SearchHistoryEntry { Value = value, Pinned = false });
            }

            // 普通项超上限：从尾部（最旧）剔除，置顶项不动
            int unpinned = list.Count(e => !e.Pinned);
            for (int i = list.Count - 1; i >= 0 && unpinned > SearchHistoryMaxItems; i--)
            {
                if (list[i].Pinned) continue;
                list.RemoveAt(i);
                unpinned--;
            }
        }

        private static void TogglePinCore(List<SearchHistoryEntry> list, string value)
        {
            if (list == null || string.IsNullOrEmpty(value)) return;
            var entry = list.FirstOrDefault(e => string.Equals(e.Value, value, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            list.Remove(entry);
            if (entry.Pinned)
            {
                entry.Pinned = false;
                list.Insert(FirstUnpinnedIndex(list), entry);   // 取消置顶 → 回到普通区块最前
            }
            else
            {
                entry.Pinned = true;
                list.Insert(FirstUnpinnedIndex(list), entry);   // 置顶 → 接到置顶区块末尾（普通区块起始处）
            }
        }

        private static void RemoveSearchHistoryCore(List<SearchHistoryEntry> list, string value)
        {
            if (list == null || string.IsNullOrEmpty(value)) return;
            list.RemoveAll(e => string.Equals(e.Value, value, StringComparison.OrdinalIgnoreCase));
        }

        private static void ClearUnpinnedSearchHistoryCore(List<SearchHistoryEntry> list)
        {
            if (list == null) return;
            list.RemoveAll(e => !e.Pinned);   // 只清普通项，置顶项保留
        }

        /// <summary>规范化：保证"置顶在前、普通在后"的不变量（外部手工改过配置文件时的兜底；实时稳定排序）</summary>
        private static List<SearchHistoryEntry> NormalizeSearchHistory(List<SearchHistoryEntry> list)
        {
            if (list == null) return new List<SearchHistoryEntry>();
            list.RemoveAll(e => e == null || string.IsNullOrWhiteSpace(e.Value));
            return list.OrderByDescending(e => e.Pinned).ToList();
        }

        // 注：老版文件名历史里的"词首 -/! 排除"写法**不做迁移**（用户定稿：`-`/`!` 一律视为字符串，
        // 不删减历史内容）。老写法条目在新语义下会按字面量参与匹配，可能搜不到东西，由用户自行修改条件。

        public static List<MergeHistoryEntry> GetMergeHistory()
        {
            return _mergeHistory.OrderByDescending(e => e.OpenTime).ToList();
        }

        public static void AddMergeHistory(string basePath, string modPath)
        {
            if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(modPath)) return;
            _mergeHistory.RemoveAll(e => string.Equals(e.BasePath, basePath, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(e.ModPath, modPath, StringComparison.OrdinalIgnoreCase));
            _mergeHistory.Insert(0, new MergeHistoryEntry
            {
                Id = Guid.NewGuid().ToString(),
                BasePath = basePath,
                ModPath = modPath,
                OpenTime = DateTime.Now
            });
            if (_mergeHistory.Count > 20)
                _mergeHistory.RemoveRange(20, _mergeHistory.Count - 20);
            Save();
        }

        public static void RemoveMergeHistory(string id)
        {
            _mergeHistory.RemoveAll(e => e.Id == id);
            Save();
        }

        public static void ClearMergeHistory()
        {
            _mergeHistory.Clear();
            Save();
        }
    }
}
