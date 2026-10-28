using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using TheArtOfDev.HtmlRenderer.WinForms;
using Markdig;

namespace GDPackEditor
{
    /// <summary>
    /// 内置 Markdown 文档查看器（供主界面关于/更新日志/使用说明、MOD合并使用说明、生成数组弹窗说明等复用）。
    /// 从内嵌资源按后缀名加载 md：**Markdig 解析输出语义 HTML**（列表嵌套、松散列表、强调、行内代码、
    /// 引用等语义由成熟库保证），套主题 CSS（颜色实时取 ThemeManager，跟随明暗设置）后由 HtmlPanel 呈现——
    /// 只读、可选中复制、右键复制；行内代码/代码块圆角底色块、引用左竖线由 HTML 排版引擎真渲染。
    /// </summary>
    public static class MarkdownViewer
    {
        /// <summary>
        /// 弹出 Markdown 文档窗口。
        /// </summary>
        /// <param name="owner">宿主窗体</param>
        /// <param name="resourceSuffix">资源名后缀，如 "!ChangeLog.md"</param>
        /// <param name="titleKey">窗口标题的多语言 key</param>
        public static void Show(IWin32Window owner, string resourceSuffix, string titleKey)
        {
            var form = new AntdUI.BaseForm();
            form.Text = LanguageManager.Instance.GetText(titleKey, "说明");
            form.StartPosition = FormStartPosition.Manual;
            form.MinimumSize = new Size(400, 300);
            var wa0 = owner != null ? Screen.FromHandle(owner.Handle).WorkingArea : Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
            int dlgW = (int)(wa0.Width * 0.5);
            int dlgH = (int)(wa0.Height * 0.7);
            form.Size = new Size(dlgW, dlgH);
            form.Left = wa0.Left + (wa0.Width - dlgW) / 2;
            form.Top = wa0.Top + (wa0.Height - dlgH) / 2;

            var host = CreateMarkdownControl(LoadMarkdown(resourceSuffix)
                ?? LanguageManager.Instance.GetText("MainForm.AboutNotFound", "文件未找到。"), out _);
            form.Controls.Add(host);

            var closeButton = new AntdUI.Button();
            closeButton.Text = LanguageManager.Instance.GetText("Common.Close", "关闭");
            closeButton.Dock = DockStyle.Bottom;
            closeButton.Height = 30;
            closeButton.Click += (s, e) => form.Close();

            form.Controls.Add(closeButton);

            if (owner is Form f) form.ShowDialog(f);
            else form.ShowDialog();
        }

        // ===================== 供扁平内嵌场景复用 =====================

        /// <summary>按当前语言返回内嵌 md 资源后缀（仅简中用 CN，其余一律 EN——与自定义语言包回落取向一致）</summary>
        internal static string HelpResourceSuffix(string cnSuffix, string enSuffix)
            => string.Equals(LanguageManager.Instance.CurrentLangCode, LanguageManager.BuiltInZh, StringComparison.OrdinalIgnoreCase)
                ? cnSuffix : enSuffix;

        /// <summary>读取内嵌 md 资源文本；找不到或读失败返回 null</summary>
        internal static string LoadMarkdown(string resourceSuffix)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                string resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(resourceName)) return null;
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 创建只读 Markdown 渲染控件（**外层带 10px 内边距**，内容不紧贴四边；自带滚动/主题配色/可选中复制）。
        /// 供生成数组弹窗说明侧栏、帮助 / 更新日志 / 关于弹窗等**扁平内嵌**场景复用。
        /// </summary>
        internal static Control CreateMarkdownControl(string markdownText, out bool found)
        {
            var panel = new PlainTextHtmlPanel
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeManager.EditorBack,
                // 可选中复制；内置右键菜单禁用（其"复制"写入的是带样式 HTML），换自建"复制"仅写纯文本
                IsSelectionEnabled = true,
                IsContextMenuEnabled = false,
                Text = BuildDocumentHtml(markdownText
                    ?? LanguageManager.Instance.GetText("MainForm.AboutNotFound", "文件未找到。"))
            };
            found = markdownText != null;

            // 自建右键菜单：仅纯文本复制（文案走多语言）
            var copyMenu = new ContextMenuStrip();
            var copyItem = new ToolStripMenuItem(LanguageManager.Instance.GetText("Common.Copy", "复制"));
            copyItem.Click += (s, e) => panel.CopySelectedText();
            copyMenu.Items.Add(copyItem);
            panel.ContextMenuStrip = copyMenu;

            // 内边距用外层 Panel 提供：HTML body 的 margin/padding 保持 0，留白与缩放由 WinForms 层统一
            var wrap = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(DpiManager.S(10)),   // 上下左右各 10px（随 DPI 缩放）
                BackColor = ThemeManager.EditorBack        // 与渲染控件同底色，避免露边
            };
            wrap.Controls.Add(panel);
            return wrap;
        }

        // ===================== HTML 生成 =====================

        /// <summary>
        /// md → 语义 HTML → 套主题 CSS。
        /// 内嵌文档核心语法（标题/列表/引用/代码/强调/链接/分隔线）+ 管道表格（LICENSE 第三方组件表），
        /// pipeline 需 UsePipeTables()（默认管道不含表格）；任务列表复选框 HtmlPanel 不支持，需先转字符渲染。
        /// </summary>
        private static string BuildDocumentHtml(string md)
        {
            // Markdig 1.4.0：UsePipeTables 在 MarkdownExtensions（命名空间 Markdig，需 using）且必须传 PipeTableOptions（默认构造即默认表格行为）
            var pipeline = new MarkdownPipelineBuilder().UsePipeTables(new Markdig.Extensions.Tables.PipeTableOptions()).Build();
            string body = Markdig.Markdown.ToHtml(md, pipeline);
            return "<html><head><style>" + BuildCss() + "</style></head><body>" + body + "</body></html>";
        }

        /// <summary>
        /// 主题 CSS：颜色全部实时取 ThemeManager（明/暗/跟随系统自动生效，不写死色值）；
        /// 尺寸统一走 DpiManager 随系统缩放（旧 Scintilla 方案的字号为磅，CSS 用 px，1磅 = 4/3 px）。
        /// corner-radius 为 HtmlRenderer 的圆角属性名（非标准 border-radius）。
        /// </summary>
        private static string BuildCss()
        {
            float Pt(float pt) => DpiManager.S(pt * 4f / 3f);   // 磅 → 缩放后像素
            string cjk = FontHelper.CjkFontName;
            string mono = FontHelper.MonoFontName;
            string fore = Css(ThemeManager.EditorFore);
            string back = Css(ThemeManager.EditorBack);
            string codeBack = Css(ThemeManager.EditorCodeBack);
            string codeFore = Css(ThemeManager.EditorString);
            string quoteFore = Css(ThemeManager.EditorComment);
            string link = Css(ThemeManager.EditorKeyword);
            string line = Css(ThemeManager.EditorLine);

            var sb = new StringBuilder();
            // 正文：中文字体链（英文系统命中英文名），代码用等宽西文字体
            sb.AppendLine($"body {{ font-family:'{cjk}','Microsoft YaHei',sans-serif; font-size:{Pt(10.5f)}px; line-height:1.5; color:{fore}; background-color:{back}; margin:0; padding:0; }}");
            sb.AppendLine($"p {{ margin:{DpiManager.S(6)}px 0; }}");
            // 标题：分级字号 + 加粗，正文默认色（延续定稿效果）
            sb.AppendLine($"h1 {{ font-size:{Pt(18)}px; }}");
            sb.AppendLine($"h2 {{ font-size:{Pt(16)}px; }}");
            sb.AppendLine($"h3 {{ font-size:{Pt(14)}px; }}");
            sb.AppendLine($"h4 {{ font-size:{Pt(12)}px; }}");
            sb.AppendLine($"h5 {{ font-size:{Pt(10)}px; }}");
            sb.AppendLine($"h6 {{ font-size:{Pt(9)}px; }}");
            sb.AppendLine($"h1,h2,h3,h4,h5,h6 {{ font-weight:bold; color:{fore}; margin:{DpiManager.S(8)}px 0; }}");
            // 行内代码：圆角底色块（真排版包裹效果，含内边距）
            sb.AppendLine($"code {{ font-family:'{mono}','Courier New',monospace; font-size:{Pt(9.5f)}px; color:{codeFore}; background-color:{codeBack}; padding:1px {DpiManager.S(4)}px; corner-radius:{DpiManager.S(3)}px; }}");
            // 代码块：整块圆角底色 + 细边框；内部 code 还原避免双重底色
            sb.AppendLine($"pre {{ background-color:{codeBack}; border:1px solid {line}; corner-radius:{DpiManager.S(4)}px; padding:{DpiManager.S(6)}px {DpiManager.S(8)}px; margin:{DpiManager.S(4)}px 0; }}");
            sb.AppendLine($"pre code {{ background-color:transparent; padding:0; corner-radius:0; }}");
            // 引用：左竖线 + 底色块 + 斜体注释色（经典 MD 引用样式）
            sb.AppendLine($"blockquote {{ border-left:{Math.Max(2, DpiManager.S(3))}px solid {quoteFore}; background-color:{codeBack}; margin:{DpiManager.S(4)}px 0; padding:{DpiManager.S(2)}px {DpiManager.S(8)}px; color:{quoteFore}; font-style:italic; }}");
            sb.AppendLine($"blockquote p {{ margin:{DpiManager.S(1)}px 0; }}");
            sb.AppendLine($"a {{ color:{link}; text-decoration:underline; }}");
            sb.AppendLine($"hr {{ border:none; border-top:1px solid {line}; margin:{DpiManager.S(8)}px 0; }}");
            sb.AppendLine($"ul,ol {{ margin:{DpiManager.S(2)}px 0; padding-left:{DpiManager.S(24)}px; }}");
            sb.AppendLine($"li {{ margin:{DpiManager.S(1)}px 0; }}");
            // 列表符号：1级实心圆点，2级起一律空心。库对 square 用"♠"字符渲染（黑桃来源），
            // 默认 CSS 会把 ul ol ul 嵌套设为 square，这里全量覆盖压掉；库的 circle 即 2 级现用的空心样式
            sb.AppendLine($"ul {{ list-style-type:disc; }}");
            sb.AppendLine($"ul ul,ol ul {{ list-style-type:circle; }}");
            sb.AppendLine($"ul ul ul,ol ul ul,ul ol ul {{ list-style-type:circle; }}");
            // 表格（当前内嵌文档未用，防御性保留）
            sb.AppendLine($"table {{ border-collapse:collapse; margin:{DpiManager.S(4)}px 0; }}");
            sb.AppendLine($"th,td {{ border:1px solid {line}; padding:{DpiManager.S(2)}px {DpiManager.S(6)}px; }}");
            return sb.ToString();
        }

        /// <summary>Color → CSS rgb()（用 rgb 表达式，兼容性最稳）</summary>
        private static string Css(Color c) => $"rgb({c.R},{c.G},{c.B})";

        /// <summary>
        /// 纯文本复制的 HtmlPanel：拦截 Ctrl+C、右键"复制"均只写纯文本。
        /// 库默认复制走 CopySelectedHtml，会把带样式 HTML 写入剪贴板（粘到富文本处会带底色块等格式），
        /// 阅读场景只需要文字；ProcessCmdKey 在消息预处理阶段拦截，先于库自身的按键处理。
        /// </summary>
        private sealed class PlainTextHtmlPanel : HtmlPanel
        {
            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (keyData == (Keys.Control | Keys.C))
                {
                    CopySelectedText();
                    return true;   // 已消费，避免库继续按 HTML 格式复制
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            /// <summary>把当前选中内容以纯文本写入剪贴板（无选中时不动剪贴板）</summary>
            public void CopySelectedText()
            {
                string text = SelectedText;
                if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
            }
        }
    }
}
