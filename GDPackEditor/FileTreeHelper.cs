using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace GDPackEditor
{
    /// <summary>
    /// 双树视图辅助（Phase 1 抽离自 ArzArcEditorForm，兼顾 Table 树模式）
    /// </summary>
    public static class FileTreeHelper
    {
        /// <summary>
        /// 从 CompareItem 列表构建 NodeData 层级（左/右各一套，复用 GuiMainForm 已有实现）
        /// </summary>
        public static List<NodeData> BuildFromCompareItems(IEnumerable<ModMergeToolForm.CompareItem> items, Func<ModMergeToolForm.CompareItem, bool> predicate = null)
        {
            var entries = new List<(string name, DateTime fileTime, int index, string recordType)>();
            int idx = 0;
            foreach (var it in items)
            {
                if (predicate != null && !predicate(it)) continue;
                if (string.IsNullOrEmpty(it.FileName)) continue;
                entries.Add((it.FileName, DateTime.Now, idx++, Path.GetExtension(it.FileName)));
            }
            return GuiMainForm.BuildHierarchicalStructureStatic(entries);
        }

        /// <summary>
        /// 计算文件夹树最大文本宽度（用于 AutoScrollMinSize，与 ArzArcEditorForm 一致）
        /// </summary>
        public static int CalculateMaxWidth(IEnumerable<NodeData> roots, Font font, float dpiScale)
        {
            int maxW = 0;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            {
                void Measure(IList<NodeData> nodes, int depth)
                {
                    foreach (var nd in nodes)
                    {
                        int indent = depth * (int)(16 * dpiScale);
                        string text = nd.IsDirectory ? $"{nd.Name} [{nd.TotalItemCount}]" : nd.Name;
                        int w = (int)g.MeasureString(text, font).Width + indent + 40;
                        if (w > maxW) maxW = w;
                        if (nd.Children != null && nd.Children.Count > 0)
                            Measure(nd.Children, depth + 1);
                    }
                }
                Measure(roots.ToList(), 0);
            }
            return maxW;
        }

        /// <summary>
        /// 按 NodeData 路径查找节点（用于双树同步选中）
        /// </summary>
        public static NodeData FindNodeByPath(List<NodeData> roots, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            List<NodeData> cur = roots;
            NodeData found = null;
            foreach (var p in parts)
            {
                found = cur.FirstOrDefault(n => string.Equals(n.Name, p, StringComparison.OrdinalIgnoreCase));
                if (found == null) return null;
                cur = found.Children;
            }
            return found;
        }
    }
}
