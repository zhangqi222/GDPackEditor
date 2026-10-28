using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal static class FontHelper
{
    private static readonly string[] _uiFontCandidates =
        { "微软雅黑", "Microsoft YaHei UI", "SimSun", "宋体", "Segoe UI", "Tahoma" };

    private static readonly string[] _monoFontCandidates =
        { "Consolas", "JetBrains Mono", "Cascadia Code", "Courier New" };

    // 中文字体候选链（Markdown 阅读器等以中文为主的正文用）：中/英文注册名都列出——
    // FontFamily.Families 返回本地化名，中文系统命中中文名、英文系统命中英文名。
    // Win10/11 各语言版本均自带微软雅黑/SimSun，正常环境链头即命中；链尾兜底系统默认字体。
    private static readonly string[] _cjkFontCandidates =
        { "微软雅黑", "Microsoft YaHei UI", "Microsoft YaHei", "宋体", "SimSun" };

    private static readonly string _uiFontName;
    private static readonly string _monoFontName;
    private static readonly string _cjkFontName;

    static FontHelper()
    {
        var installed = new HashSet<string>(
            FontFamily.Families.Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);

        _uiFontName = _uiFontCandidates.FirstOrDefault(f => installed.Contains(f))
                      ?? SystemFonts.DefaultFont.FontFamily.Name;

        _monoFontName = _monoFontCandidates.FirstOrDefault(f => installed.Contains(f))
                        ?? "Courier New";

        _cjkFontName = _cjkFontCandidates.FirstOrDefault(f => installed.Contains(f))
                      ?? SystemFonts.DefaultFont.FontFamily.Name;
    }

    public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
        => new Font(_uiFontName, GDPackEditor.DpiManager.SF(size), style);

    public static Font MonoFont(float size, FontStyle style = FontStyle.Regular)
        => new Font(_monoFontName, GDPackEditor.DpiManager.SF(size), style);

    public static string UiFontName => _uiFontName;
    public static string MonoFontName => _monoFontName;

    /// <summary>中文字体名（候选链见 _cjkFontCandidates；保证简体字形，避免 DirectWrite 对汉字的日文回退）</summary>
    public static string CjkFontName => _cjkFontName;
}
