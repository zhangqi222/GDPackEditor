using System;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using ScintillaNET;

namespace GDPackEditor
{
    /// <summary>
    /// Scintilla 语法着色统一入口（txt=props/ini、tpl=json、lua=lua、dbr=手动按首逗号染色）。
    /// DiffTextDialog 与 ArzArcEditorForm 源码模式共用此方案，保证两种编辑器着色一致。
    /// </summary>
    internal static class ScintillaSyntax
    {
        private class DbrState
        {
            public bool Active;          // 当前是否处于 dbr 手动染色模式
            public bool Hooked;          // StyleNeeded 是否已挂载
        }

        private static readonly ConditionalWeakTable<Scintilla, DbrState> _states = new ConditionalWeakTable<Scintilla, DbrState>();

        private static DbrState GetState(Scintilla ed)
        {
            return _states.GetValue(ed, _ => new DbrState());
        }

        /// <summary>
        /// 按扩展名切换 lexer 并应用主题着色。filePathOrName 可为相对路径/文件名。
        /// 重复调用会切换 lexer 并重新着色（幂等）。
        /// </summary>
        public static void Apply(Scintilla ed, string filePathOrName, string monoFontName, int fontSize)
        {
            if (ed == null || ed.IsDisposed) return;

            string ext = string.IsNullOrEmpty(filePathOrName)
                ? ""
                : (Path.GetExtension(filePathOrName) ?? "").ToLowerInvariant();

            bool dbr = ext == ".dbr";
            string lexer = ext switch
            {
                ".lua" => "lua",
                ".txt" => "props",
                ".tpl" => "json",
                _ => null   // 其他（含 .dbr）不启用内置 lexer：dbr 用手动染色，其余仅默认样式
            };

            var state = GetState(ed);

            if (dbr)
            {
                // 切到空 lexer，用手动染色
                if (!string.IsNullOrEmpty(ed.LexerName)) { ed.LexerName = ""; ed.StyleClearAll(); }
                if (!state.Hooked)
                {
                    ed.StyleNeeded += OnDbrStyleNeeded;
                    state.Hooked = true;
                }
                state.Active = true;
                SetDefaultAndClear(ed, monoFontName, fontSize);
                ApplyDbrTheme(ed);
            }
            else
            {
                if (state.Active)
                {
                    ed.StyleNeeded -= OnDbrStyleNeeded;
                    state.Hooked = false;
                }
                state.Active = false;

                if (lexer != null && !string.Equals(ed.LexerName, lexer, StringComparison.OrdinalIgnoreCase))
                {
                    ed.LexerName = lexer;
                    ed.StyleClearAll();
                }
                else if (lexer == null && !string.IsNullOrEmpty(ed.LexerName))
                {
                    ed.LexerName = "";
                    ed.StyleClearAll();
                }

                SetDefaultAndClear(ed, monoFontName, fontSize);
                SetLineNumber(ed);   // 同上：行号边距换肤与是否使用 lexer 无关
                if (lexer != null) ApplyLexerTheme(ed, lexer);
            }

            ForceRecolour(ed);
        }

        /// <summary>
        /// 仅重新应用主题配色（不切换 lexer），供主题切换时调用。
        /// </summary>
        public static void ApplyTheme(Scintilla ed, string monoFontName, int fontSize)
        {
            if (ed == null || ed.IsDisposed) return;
            var state = GetState(ed);
            SetDefaultAndClear(ed, monoFontName, fontSize);
            SetLineNumber(ed);   // 行号边距必须无条件换肤：StyleClearAll 会重置它，且 dbr 手动染色模式不走 ApplyLexerTheme
            if (state.Active) ApplyDbrTheme(ed);
            else if (!string.IsNullOrEmpty(ed.LexerName)) ApplyLexerTheme(ed, ed.LexerName);
            ForceRecolour(ed);
        }

        private static void SetDefaultAndClear(Scintilla ed, string monoFontName, int fontSize)
        {
            var def = ed.Styles[Style.Default];
            if (!string.IsNullOrEmpty(monoFontName)) def.Font = monoFontName;
            if (fontSize > 0) def.Size = fontSize;
            def.BackColor = ThemeManager.EditorBack;
            def.ForeColor = ThemeManager.EditorFore;
            ed.StyleClearAll();
        }

        /// <summary>
        /// 只读文本/日志面板的**统一样式**（多处复用，避免每处各写一遍配色）：
        /// 等宽字体、明暗主题底色与前景、选区配色、超宽自动换行、滚动条策略（只保留纵向）。
        /// 与 <see cref="Apply"/> / <see cref="ApplyTheme"/> 的区别：只读面板不需要 lexer，故只处理默认样式。
        /// </summary>
        /// <param name="resetStyles">
        /// true：执行 <see cref="SetDefaultAndClear"/>（内部调用 StyleClearAll，**会重置 Marker 颜色**，调用方需随后重新应用 Marker）；
        /// false：仅刷新配色（供主题跟随的轻量刷新使用），不会清掉已有行标记。
        /// </param>
        public static void ApplyReadOnlyLog(Scintilla ed, string monoFontName, int fontSize, bool resetStyles = true)
        {
            if (ed == null || ed.IsDisposed) return;

            // 超宽自动换行（长路径/长数值不再被截断）；既然换行，横向滚动条即无意义，一并禁用
            ed.WrapMode = ScintillaNET.WrapMode.Word;
            ed.ScrollWidth = 1;
            ed.ScrollWidthTracking = true;
            ed.HScrollBar = false;
            ed.VScrollBar = true;

            if (resetStyles) SetDefaultAndClear(ed, monoFontName, fontSize);
            SetLineNumber(ed);   // 行号边距换肤（本方法供多处只读面板复用，是否显示边距由调用方决定）

            // 控件级与默认样式级都要设：只设样式时控件底色仍可能保持浅色（暗色下文字发暗的主因）
            ed.BackColor = ThemeManager.EditorBack;
            ed.ForeColor = ThemeManager.EditorFore;
            ed.Styles[Style.Default].BackColor = ThemeManager.EditorBack;
            ed.Styles[Style.Default].ForeColor = ThemeManager.EditorFore;
            ed.CaretForeColor = ThemeManager.EditorFore;

            // 选区配色：暗色下默认白字灰底看不清，统一为编辑器同款
            ed.SelectionBackColor = ThemeManager.EditorSelectionBack;
            ed.SelectionTextColor = ThemeManager.EditorSelectionFore;
            ed.SelectionAdditionalBackColor = ThemeManager.EditorSelectionBack;
            ed.SelectionAdditionalTextColor = ThemeManager.EditorSelectionFore;
            ed.SelectionInactiveBackColor = ThemeManager.EditorSelectionBack;
            ed.SelectionInactiveTextColor = ThemeManager.EditorSelectionFore;
        }

        private static void ForceRecolour(Scintilla ed)
        {
            try
            {
                if (ed.TextLength > 0) ed.Colorize(0, -1);
            }
            catch { }
        }

        /// <summary>
        /// 应用行号边距与行号样式（<c>STYLE_LINENUMBER</c>）的颜色。
        /// <para>
        /// 公开供调用方**在最后一步补救**：实测发现部分路径（主题/控件初始化、较早的 StyleClearAll）
        /// 会在 ScintillaSyntax 应用之后再重置 <c>STYLE_LINENUMBER</c>，导致暗色下行号栏仍是浅灰，
        /// 故编辑器侧在关键节点（着色后、窗体显示后）会再调一次本方法。
        /// </para>
        /// </summary>
        public static void SetLineNumber(Scintilla ed)
        {
            // ① 行号文字样式（STYLE_LINENUMBER）
            var ln = ed.Styles[Style.LineNumber];
            ln.BackColor = ThemeManager.EditorLineNumberBack;
            ln.ForeColor = ThemeManager.EditorLineNumberFore;

            if (ed.Margins == null || ed.Margins.Count == 0) return;
            var margin = ed.Margins[0];

            // ② 边距类型：**只为本来就会显示行号的边距**补设置，收敛影响面
            //    —— 宽度为 0 的隐藏边距（如日志面板）与符号/折叠边距一律不动。
            if (margin.Type != MarginType.Number)
            {
                if (margin.Width <= 0) return;
                margin.Type = MarginType.Number;
            }

            // ③ 边距背景色：**必须放在最后**。
            //    Scintilla 的 MarginType 赋值会重置边距属性（含背景色），此前把背景色写在类型之前，
            //    随即被清掉 —— 这正是"暗色下行号区仍是浅灰"的原因。
            margin.BackColor = ThemeManager.EditorLineNumberBack;
        }

        private static void SetSyntax(Scintilla ed, int styleIndex, Color fore)
        {
            var s = ed.Styles[styleIndex];
            s.ForeColor = fore;
            s.BackColor = ThemeManager.EditorBack;
        }

        // ===================== 内置 lexer 配色 =====================

        private static void ApplyLexerTheme(Scintilla ed, string lexer)
        {
            SetLineNumber(ed);
            switch (lexer)
            {
                case "lua":
                    SetSyntax(ed, Style.Lua.Comment, ThemeManager.EditorComment);
                    SetSyntax(ed, Style.Lua.CommentLine, ThemeManager.EditorComment);
                    SetSyntax(ed, Style.Lua.CommentDoc, ThemeManager.EditorComment);
                    SetSyntax(ed, Style.Lua.Number, ThemeManager.EditorNumber);
                    SetSyntax(ed, Style.Lua.String, ThemeManager.EditorString);
                    SetSyntax(ed, Style.Lua.Character, ThemeManager.EditorString);
                    SetSyntax(ed, Style.Lua.LiteralString, ThemeManager.EditorString);
                    SetSyntax(ed, Style.Lua.StringEol, ThemeManager.EditorString);
                    SetSyntax(ed, Style.Lua.Preprocessor, ThemeManager.EditorSection);
                    SetSyntax(ed, Style.Lua.Operator, ThemeManager.EditorOperator);
                    SetSyntax(ed, Style.Lua.Identifier, ThemeManager.EditorIdentifier);
                    SetSyntax(ed, Style.Lua.Label, ThemeManager.EditorSection);
                    SetSyntax(ed, Style.Lua.Word, ThemeManager.EditorKeyword);
                    SetSyntax(ed, Style.Lua.Word2, ThemeManager.EditorIdentifier);
                    SetSyntax(ed, Style.Lua.Word3, ThemeManager.EditorString);
                    SetSyntax(ed, Style.Lua.Word4, ThemeManager.EditorNumber);
                    SetSyntax(ed, Style.Lua.Word5, ThemeManager.EditorSection);
                    SetSyntax(ed, Style.Lua.Word6, ThemeManager.EditorKeyword);
                    SetSyntax(ed, Style.Lua.Word7, ThemeManager.EditorIdentifier);
                    SetSyntax(ed, Style.Lua.Word8, ThemeManager.EditorKeyword);
                    break;

                case "props":
                    SetSyntax(ed, Style.Properties.Comment, ThemeManager.EditorComment);
                    SetSyntax(ed, Style.Properties.Section, ThemeManager.EditorSection);
                    SetSyntax(ed, Style.Properties.Assignment, ThemeManager.EditorOperator);
                    SetSyntax(ed, Style.Properties.DefVal, ThemeManager.EditorString);
                    SetSyntax(ed, Style.Properties.Key, ThemeManager.EditorKeyword);
                    break;

                case "json":
                    SetSyntax(ed, Style.Json.Number, ThemeManager.EditorNumber);
                    SetSyntax(ed, Style.Json.String, ThemeManager.EditorString);
                    SetSyntax(ed, Style.Json.StringEol, ThemeManager.EditorString);
                    SetSyntax(ed, Style.Json.PropertyName, ThemeManager.EditorKeyword);
                    SetSyntax(ed, Style.Json.EscapeSequence, ThemeManager.EditorString);
                    SetSyntax(ed, Style.Json.LineComment, ThemeManager.EditorComment);
                    SetSyntax(ed, Style.Json.BlockComment, ThemeManager.EditorComment);
                    SetSyntax(ed, Style.Json.Operator, ThemeManager.EditorOperator);
                    SetSyntax(ed, Style.Json.Keyword, ThemeManager.EditorKeyword);
                    SetSyntax(ed, Style.Json.Error, ThemeManager.EditorString);
                    break;
            }
        }

        // ===================== dbr 手动染色（每行 key,value,） =====================

        private static void ApplyDbrTheme(Scintilla ed)
        {
            // 借用 Style.Json 的 Number/String 槽位分别作 key / value
            SetSyntax(ed, Style.Json.Number, ThemeManager.EditorKeyword);  // key
            SetSyntax(ed, Style.Json.String, ThemeManager.EditorString);   // value
        }

        private static void OnDbrStyleNeeded(object sender, StyleNeededEventArgs e)
        {
            var ed = (Scintilla)sender;
            if (ed == null) return;
            var state = GetState(ed);
            if (!state.Active) return;

            var text = ed.Text;
            if (string.IsNullOrEmpty(text)) return;

            var start = ed.GetEndStyled();
            // 文本被替换/变短后 GetEndStyled() 可能残留旧值（超出新文本长度），越界则回退到 0 确保重新染色
            if (start < 0 || start > text.Length) start = 0;
            var target = e.Position;
            if (target > text.Length) target = text.Length;
            if (target <= start) return;

            ed.StartStyling(start);
            var i = start;
            while (i < target)
            {
                int nl = text.IndexOf('\n', i);
                int lineEnd = nl < 0 ? target : nl;
                if (lineEnd > target) lineEnd = target;

                int len = lineEnd - i;
                if (len > 0)
                {
                    int comma = text.IndexOf(',', i) - i;
                    if (comma > 0)
                    {
                        if (comma > len) comma = len;
                        ed.SetStyling(comma, Style.Json.Number);       // key
                        ed.SetStyling(len - comma, Style.Json.String); // value
                    }
                    else
                    {
                        ed.SetStyling(len, Style.Default);
                    }
                }

                i = lineEnd;
                if (nl >= 0 && i < text.Length && text[i] == '\n')
                {
                    ed.SetStyling(1, Style.Default);
                    i++;
                }
            }
        }
    }
}
