using System;
using System.Drawing;
using Microsoft.Win32;
using AntdUI;

namespace GDPackEditor
{
    /// <summary>
    /// 统一主题管理器（亮/暗模式 + 主色方案 唯一入口）。
    /// 规则：ThemeMode=System 时跟随 Windows 深浅色（读注册表 AppsUseLightTheme），
    ///       否则按显式选择；ThemeAccent 决定品牌主色。均重启生效（同 DpiManager 模式）。
    /// 语义状态色（修改/新增/删除/差异背景等）提供深浅两套色板，
    /// 主题色（背景/文字/边框）直接取 AntdUI 当前主题 token。
    /// </summary>
    public static class ThemeManager
    {
        private static bool _isDark;
        private static string _accentName = "default";

        /// <summary>当前是否为暗色模式（启动时决策，重启生效）。</summary>
        public static bool IsDark => _isDark;

        /// <summary>当前主色方案标识（default/emerald/violet/amber）。</summary>
        public static string AccentName => _accentName;

        /// <summary>
        /// 程序启动时调用：按设置决策明暗模式 + 主色，应用到 AntdUI 全局。
        /// 必须保证在 SettingsManager 已初始化之后调用（替代原写死 TMode.Light 的代码）。
        /// </summary>
        public static void ApplyTheme()
        {
            string mode = SettingsManager.ThemeMode;
            _isDark = mode == "Dark" || (mode == "System" && QuerySystemDark());

            AntdUI.Config.Mode = _isDark ? AntdUI.TMode.Dark : AntdUI.TMode.Light;
            // 主题默认背景/文字（沿用原值：浅色灰底黑字，深色深底白字）
            AntdUI.Config.Theme()
                .Light("#f0f0f0", "#000000")
                .Dark("#333333", "#ffffff");

            // 主色方案（SetPrimary 会自动重算整套 8 阶色板；Config.Mode 切换时也会自动重算）
            // 注意：主色区分明暗两套色值——暗色模式下使用更亮的标准色（One Dark 系列），
            // 避免 AntdUI 明暗共用同一主色导致暗色下文字/选中项偏暗看不清。
            _accentName = SettingsManager.ThemeAccent;
            AntdUI.Style.SetPrimary(GetAccentColor(_accentName, _isDark));
        }

        /// <summary>跟随系统时查询 Windows 深浅色（AppsUseLightTheme=0 为暗色）。读取失败回退亮色。</summary>
        private static bool QuerySystemDark()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v != null) return Convert.ToInt32(v) == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 取指定主色方案在指定明暗模式下的实际颜色（供设置窗「色系」单选按钮显示对应颜色用）。
        /// </summary>
        public static Color GetAccentColor(string name, bool dark)
        {
            if (dark)
            {
                // 暗色模式：One Dark（VS Code 默认深色主题）标准亮色，暗底上可读性高
                switch (name)
                {
                    case "lintan": return Color.FromArgb(255, 222, 89);    // #FFDE59 林塘-金（暗色保持原值）
                    case "emerald": return Color.FromArgb(152, 195, 121);   // #98C379 亮绿
                    case "violet": return Color.FromArgb(198, 120, 221);    // #C678DD 亮紫
                    case "amber": return Color.FromArgb(209, 154, 102);     // #D19A66 亮橙
                    case "red": return Color.FromArgb(224, 108, 117);       // #E06C75 亮红
                    case "cyan": return Color.FromArgb(86, 182, 194);       // #56B6C2 亮青
                    default: return Color.FromArgb(97, 175, 239);           // #61AFEF 亮蓝
                }
            }
            switch (name)
            {
                case "lintan": return Color.FromArgb(218, 165, 32);   // #DAA520 林塘-金（亮色模式用较暗的金色）
                case "emerald": return Color.FromArgb(38, 166, 91);   // 翡翠绿
                case "violet": return Color.FromArgb(114, 46, 209);   // 紫罗兰
                case "amber": return Color.FromArgb(250, 140, 22);    // 琥珀橙
                case "red": return Color.FromArgb(245, 34, 45);       // 红
                case "cyan": return Color.FromArgb(19, 194, 194);     // 青
                default: return Color.FromArgb(22, 119, 255);         // 默认蓝
            }
        }

        #region 主题色（AntdUI 当前主题 token）

        /// <summary>页面整体布局背景色（最外层容器）。与 Config.Theme().Light/Dark 背景一致，保证窗体 BackColor 与内容面板同色。</summary>
        public static Color BgLayout => _isDark ? Color.FromArgb(0x33, 0x33, 0x33) : Color.FromArgb(0xF0, 0xF0, 0xF0);
        /// <summary>组件容器背景色（面板/输入框等）。</summary>
        public static Color BgContainer => AntdUI.Style.Db.BgContainer;
        /// <summary>主文字色。</summary>
        public static Color Text => AntdUI.Style.Db.Text;
        /// <summary>次要文字色。</summary>
        public static Color TextTertiary => AntdUI.Style.Db.TextTertiary;
        /// <summary>边框色。</summary>
        public static Color BorderColor => AntdUI.Style.Db.BorderColor;

        /// <summary>
        /// 表格网格线专用色（四个主要表格显式设置，覆盖 AntdUI 内置 BorderColor 同源色）：
        /// 内置色暗模式 #424242 与表头背景 #1D1D1D 对比弱 + 1px 细线在高 DPI 下发虚，
        /// 列头分割线（拖动列宽的抓手）看不清（2026-10-13 用户反馈）——此色加亮/加深一档。
        /// 注意仅在启动时取值（项目主题切换=重启生效），与 ApplyTheme 机制一致。
        /// </summary>
        public static Color TableGridColor => _isDark ? Color.FromArgb(0x5F, 0x5F, 0x5F) : Color.FromArgb(0xB9, 0xB9, 0xB9);
        /// <summary>品牌主色上文字色（配合 Primary 底色）。</summary>
        public static Color PrimaryColor => AntdUI.Style.Db.PrimaryColor;
        public static Color Primary => AntdUI.Style.Db.Primary;

        #endregion

        #region 语义状态色（深浅两套色板）

        /// <summary>修改/已修改（红）。</summary>
        public static Color Modified => _isDark ? Color.FromArgb(224, 108, 117) : Color.Red;
        /// <summary>新增（绿）。</summary>
        public static Color Added => _isDark ? Color.FromArgb(152, 195, 121) : Color.Green;
        /// <summary>新增文件（蓝，文件树用）。</summary>
        public static Color AddedBlue => _isDark ? Color.FromArgb(97, 175, 239) : Color.Blue;
        /// <summary>删除/未修改/灰显。</summary>
        public static Color Deleted => _isDark ? Color.FromArgb(154, 154, 154) : Color.Gray;
        /// <summary>多文件对比：非基准列与基准值不同的浅红背景。</summary>
        public static Color DiffBack => _isDark ? Color.FromArgb(90, 50, 50) : Color.FromArgb(255, 255, 205, 205);
        /// <summary>文件操作行背景。</summary>
        public static Color FileRowBack => _isDark ? Color.FromArgb(58, 58, 58) : Color.LightGray;
        /// <summary>表格奇数行背景。</summary>
        public static Color OddRowBack => _isDark ? Color.FromArgb(31, 31, 31) : Color.FromArgb(245, 245, 248);
        /// <summary>独有文件行背景（仅1有/仅2有）。</summary>
        public static Color ExclusiveRowBack => _isDark ? Color.FromArgb(61, 46, 80) : Color.FromArgb(232, 222, 248);
        /// <summary>冲突不同文件行背景（两边都有但内容不同）。</summary>
        public static Color ConflictDiffRowBack => _isDark ? Color.FromArgb(80, 48, 48) : Color.FromArgb(255, 235, 238);

        #endregion

        #region Scintilla 编辑器配色（第三方控件，不随 AntdUI 主题，需单独切换）

        public static Color EditorBack => _isDark ? Color.FromArgb(30, 30, 30) : Color.White;
        public static Color EditorFore => _isDark ? Color.FromArgb(212, 212, 212) : Color.Black;
        /// <summary>行内代码/代码块/引用的底色（比编辑器底色亮/浅一档的灰，MD 阅读器包裹块用）</summary>
        public static Color EditorCodeBack => _isDark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(242, 242, 242);
        /// <summary>分隔线/边框色（弱化的边框灰，MD 阅读器 hr/代码块边框/表格线用）</summary>
        public static Color EditorLine => _isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(208, 208, 208);
        public static Color EditorCurrentLine => _isDark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(255, 255, 255, 200);
        public static Color EditorMarkerBack => _isDark ? Color.FromArgb(45, 91, 45) : Color.FromArgb(255, 255, 200);
        public static Color EditorMarkerFore => _isDark ? Color.FromArgb(45, 91, 45) : Color.FromArgb(255, 255, 200);

        // —— Scintilla 语法着色（VS Code Dark+/Light 配色，仅前景色，背景统一为 EditorBack） ——
        /// <summary>行号背景（比正文底色略亮以作区分）。</summary>
        public static Color EditorLineNumberBack => _isDark ? Color.FromArgb(35, 35, 35) : Color.FromArgb(248, 248, 248);
        /// <summary>行号前景。</summary>
        public static Color EditorLineNumberFore => _isDark ? Color.FromArgb(133, 133, 133) : Color.FromArgb(108, 108, 108);
        /// <summary>注释。</summary>
        public static Color EditorComment => _isDark ? Color.FromArgb(106, 153, 85) : Color.FromArgb(0, 128, 0);
        /// <summary>数字。</summary>
        public static Color EditorNumber => _isDark ? Color.FromArgb(181, 206, 168) : Color.FromArgb(9, 134, 88);
        /// <summary>字符串/字符/属性值。</summary>
        public static Color EditorString => _isDark ? Color.FromArgb(206, 145, 120) : Color.FromArgb(163, 21, 21);
        /// <summary>关键字。</summary>
        public static Color EditorKeyword => _isDark ? Color.FromArgb(86, 156, 214) : Color.FromArgb(0, 0, 255);
        /// <summary>标识符。</summary>
        public static Color EditorIdentifier => _isDark ? Color.FromArgb(156, 220, 254) : Color.FromArgb(0, 16, 128);
        /// <summary>操作符/其他文本。</summary>
        public static Color EditorOperator => _isDark ? Color.FromArgb(212, 212, 212) : Color.Black;
        /// <summary>段名/标签/预处理。</summary>
        public static Color EditorSection => _isDark ? Color.FromArgb(197, 134, 192) : Color.FromArgb(128, 0, 0);
        /// <summary>选中文本背景（暗色用 VS Code 选中蓝，与正文底色拉开差异）。</summary>
        public static Color EditorSelectionBack => _isDark ? Color.FromArgb(38, 79, 120) : Color.FromArgb(51, 153, 255);
        /// <summary>选中文本前景。</summary>
        public static Color EditorSelectionFore => _isDark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(255, 255, 255);

        #endregion

        #region 日志行背景色（Scintilla marker，LogView 与 ARZ/ARC 编辑器共用一套，避免两处色值分叉）

        /// <summary>日志行背景：警告（黄）。</summary>
        public static Color LogWarnBack => _isDark ? Color.FromArgb(72, 62, 28) : Color.FromArgb(255, 246, 210);
        /// <summary>日志行背景：错误（红）。</summary>
        public static Color LogErrorBack => _isDark ? Color.FromArgb(72, 34, 34) : Color.FromArgb(255, 224, 224);
        /// <summary>日志行背景：变更-新增（绿）。</summary>
        public static Color LogAddBack => _isDark ? Color.FromArgb(28, 62, 40) : Color.FromArgb(214, 246, 220);
        /// <summary>日志行背景：变更-删除（红）。</summary>
        public static Color LogDelBack => _isDark ? Color.FromArgb(72, 34, 34) : Color.FromArgb(255, 224, 224);
        /// <summary>日志行背景：变更-修改（黄）。</summary>
        public static Color LogModBack => _isDark ? Color.FromArgb(72, 62, 28) : Color.FromArgb(255, 246, 210);

        #endregion

        #region ArrowSplitter 自绘配色

        /// <summary>折叠分栏条背景。</summary>
        public static Color SplitterBar => _isDark ? Color.FromArgb(56, 56, 56) : Color.FromArgb(220, 220, 220);
        /// <summary>分隔细条。</summary>
        public static Color SplitterBack => _isDark ? Color.FromArgb(58, 58, 58) : Color.Silver;
        /// <summary>折叠按钮文字与半透明底：跟随当前主题主色（暗色下为提亮的 One Dark 亮色，自动可读）。</summary>
        public static Color SplitterButton => AntdUI.Style.Db.Primary;

        #endregion
    }
}
