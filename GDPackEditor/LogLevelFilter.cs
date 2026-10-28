using System;
using System.Windows.Forms;

namespace GDPackEditor
{
    /// <summary>
    /// 日志**四级筛选**的公共判定：主界面 <see cref="LogView"/> 与编辑器日志框共用一份，避免两处走样。
    ///
    /// <para>级别语义：0=全部、1=信息、2=警告、3=错误；行格式约定 `[HH:mm:ss] [级别] 正文`。</para>
    ///
    /// <para>本类只做**纯判定**（不含控件状态、不碰 UI），因此抽出后主界面行为逐字节不变
    /// （原来 LogView 内部实现即此逻辑，现改为委托调用）。</para>
    /// </summary>
    internal static class LogLevelFilter
    {
        /// <summary>级别筛选项："全部"（不过滤）</summary>
        public const int All = 0;
        /// <summary>级别筛选项："信息"</summary>
        public const int Info = 1;
        /// <summary>级别筛选项："警告"</summary>
        public const int Warn = 2;
        /// <summary>级别筛选项："错误"</summary>
        public const int Error = 3;

        /// <summary>级别筛选项文本（次序与 <see cref="All"/>/<see cref="Info"/>/<see cref="Warn"/>/<see cref="Error"/> 一致）</summary>
        public static string[] LevelTexts() => new[]
        {
            LanguageManager.Instance.GetText("LogView.LevelAll", "全部"),
            LanguageManager.Instance.GetText("LogView.LevelInfo", "信息"),
            LanguageManager.Instance.GetText("LogView.LevelWarn", "警告"),
            LanguageManager.Instance.GetText("LogView.LevelError", "错误"),
        };

        /// <summary>
        /// 行级别：3=错误、2=警告、1=信息（与四级筛选的下标对应）。
        /// 没有级别标记的行（如瞬态进度行）按信息算，避免切到"信息"时整行凭空消失。
        /// </summary>
        public static int LineLevel(string line)
        {
            if (string.IsNullOrEmpty(line)) return Info;
            if (line.IndexOf("[ERROR]", StringComparison.Ordinal) >= 0
                || line.IndexOf("[FATAL]", StringComparison.Ordinal) >= 0) return Error;
            if (line.IndexOf("[WARN]", StringComparison.Ordinal) >= 0) return Warn;
            return Info;
        }

        /// <summary>
        /// 可见性判定：级别筛选与文本过滤**叠加**生效（两者都只影响显示，内部行集合始终完整保留）。
        /// </summary>
        public static bool Match(string line, int levelIndex, string textFilter)
        {
            if (levelIndex > 0 && LineLevel(line) != levelIndex) return false;
            if (!string.IsNullOrEmpty(textFilter)
                && (line == null || line.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0)) return false;
            return true;
        }

        /// <summary>垂直居中：控件在整行高度里的 Y 坐标</summary>
        public static int CenterY(Control c, int rowHeight) => Math.Max(0, (rowHeight - c.Height) / 2);
    }
}
