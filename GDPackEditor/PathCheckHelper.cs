using System.Drawing;
using System.IO;

namespace GDPackEditor
{
    /// <summary>
    /// 路径存在性检测公共工具：供历史记录、模板、本地化名称、字段名本地化等表格统一调用。
    /// 缺失路径统一显示灰色 + 删除线。
    /// </summary>
    public static class PathCheckHelper
    {
        /// <summary>
        /// 通用存在性检测（文件或目录均可），空路径视为不存在。
        /// </summary>
        public static bool Exists(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return File.Exists(path) || Directory.Exists(path);
        }

        /// <summary>
        /// 模板条目存在性检测：Folder 类型检测目录，其余（Arc）检测文件。
        /// </summary>
        public static bool TemplateExists(string type, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (type == "Folder") return Directory.Exists(path);
            return File.Exists(path);
        }

        /// <summary>
        /// 单元格渲染前应用缺失样式：路径列且文件缺失时，将该单元格文本置为灰色 + 删除线（只影响该单元格）。
        /// 由表格的 CellPaintBegin 事件回调调用；渲染结束后 AntdUI 会自动释放本回调创建的画笔与字体。
        /// </summary>
        public static void ApplyMissingCellStyle(AntdUI.TablePaintBeginEventArgs e, string pathColumnKey, bool missing, Font baseFont)
        {
            if (!missing) return;
            if (e.Column == null || e.Column.Key != pathColumnKey) return;
            e.SetFore(ThemeManager.TextTertiary);
            e.SetFont(new Font(baseFont, FontStyle.Strikeout));
        }
    }
}
