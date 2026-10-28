using System;
using System.Windows.Forms;

namespace GDPackEditor
{
    /// <summary>
    /// UI 文本刷新器：递归遍历控件树，按 **控件 Name 即语言 key** 的既有约定
    /// （见 GuiMainForm.RefreshControlText 注释）重设可见文本。
    ///
    /// <para>为什么需要它：主窗体靠 Name=key 递归刷新；而编辑器 / 合并工具等
    /// **独立顶层窗体**不在主窗体控件树内，切语言时无人刷新（实测编辑器切英文后
    /// 仍有 44 处中文残留）。本类把该机制抽成公共实现供这些窗体在 LanguageChanged 时调用。</para>
    ///
    /// <para>约定：仅当 Name 形如 <c>模块.语义</c>（含 '.'）且该 key 在当前语言表中**存在**时才改写；
    /// 其余一律跳过——避免把普通 Name 误当 key 改写文本。</para>
    /// </summary>
    internal static class UiTextRefresher
    {
        /// <summary>递归刷新整棵控件树（含根控件本身，便于窗体标题 Name=key 的场景）</summary>
        public static void RefreshTree(Control root)
        {
            if (root == null) return;
            Apply(root);
            foreach (Control c in root.Controls)
            {
                Apply(c);
                RefreshTree(c);
            }
        }

        private static void Apply(Control c)
        {
            if (c == null) return;
            string key = c.Name;
            if (string.IsNullOrEmpty(key) || key.IndexOf('.') < 0) return;

            // 仅当 key 在当前语言表中**存在**时才改写文本（值为空也照样改写：
            // 个别 key 在英文下故意为空，如量词单位「个」；见 LanguageManager.TryGetText）
            if (!LanguageManager.Instance.TryGetText(key, out string text)) return;

            switch (c)
            {
                // ⚠ AntdUI 继承关系决定顺序：派生类必须排在基类之前
                //   Select : Input、Dropdown : Button → Select/Dropdown 先于 Input/Button
                case AntdUI.Select select:
                    select.PlaceholderText = text;
                    break;
                case AntdUI.Input input:
                    // 输入框：原本有前缀的刷新前缀（带 ':'），否则刷新占位符
                    if (!string.IsNullOrEmpty(input.PrefixText)) input.PrefixText = text + ":";
                    else input.PlaceholderText = text;
                    break;
                case AntdUI.Dropdown dropdown:
                    // 仅刷新当前显示文本；下拉项（Items）文本由各窗体自行维护
                    dropdown.Text = text;
                    break;
                case AntdUI.Button button:
                    button.Text = text;
                    break;
                case AntdUI.TabPage page:
                    page.Text = text;
                    break;
                case AntdUI.Panel panel:
                    panel.Text = text;
                    break;
                case AntdUI.Label label:
                    label.Text = text;
                    break;
                case AntdUI.Checkbox checkbox:
                    checkbox.Text = text;
                    break;
                case AntdUI.Radio radio:
                    radio.Text = text;
                    break;
                case AntdUI.Switch _:
                    break;   // AntdUI.Switch 不承载文本（本项目惯例：文字用独立 Label）
                case System.Windows.Forms.TextBox textBox:
                    // WinForms 输入框：Name=key 语义为**占位符**（绝不能改 Text，那是用户输入内容！）
                    textBox.PlaceholderText = text;
                    break;
                case ScintillaNET.Scintilla _:
                    break;   // 源码/日志区是内容不是文案
                default:
                    if (c.Text != text) c.Text = text;
                    break;
            }
        }
    }
}
