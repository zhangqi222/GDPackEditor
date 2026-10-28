using System.Drawing;

namespace GDPackEditor
{
    /// <summary>
    /// AntdUI 按钮的**统一规格工厂**（按钮 UI 统一，plan_20260923_UI相关调整 第 10 项）：
    ///
    /// <para>规格（与主界面"打包/解包"等主按钮一致）：</para>
    /// <para>· **AutoSize 由内容撑起宽度**——不设固定宽高；强调/主操作按钮由调用方按需加左右 Padding 显宽；</para>
    /// <para>· 字体统一 9 号（<paramref name="bold"/>=true 用于主操作/强调按钮）；</para>
    /// <para>· 语义配色由 <paramref name="type"/> 决定（Primary=主操作，Default=常规，Error/Info/…按语义）。</para>
    ///
    /// <para>全工程按钮规格以此为准（wiki 开发侧 §6.3）；存量按钮已按此归一。</para>
    /// </summary>
    internal static class ButtonFactory
    {
        public static AntdUI.Button Create(string text, AntdUI.TTypeMini type = AntdUI.TTypeMini.Default,
            bool bold = false, string name = null)
            => new AntdUI.Button
            {
                Name = name,
                Text = text,
                AutoSize = true,
                Font = FontHelper.UiFont(9, bold ? FontStyle.Bold : FontStyle.Regular),
                Type = type
            };
    }
}
