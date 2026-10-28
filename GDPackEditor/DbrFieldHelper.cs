using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace GDPackEditor
{
    /// <summary>
    /// DBR 字段编辑共享工具类（供 MergeCompareDialog / ArzArcEditorForm 等复用）。
    /// 职责：字段名单元格、file_dbr 浏览按钮、模板匹配、bool/picklist 下拉弹出。
    /// 组件只做"读入 currentValue、通过回调输出结果"，不感知具体业务。
    /// </summary>
    public static class DbrFieldHelper
    {
        /// <summary>
        /// 构造字段名单元格：显示本地化名（无映射显示英文原值），悬停 Tooltip 显示英文原值便于对照。
        /// </summary>
        public static AntdUI.CellLink CreateFieldNameCell(string name)
        {
            string display = DbrKeyMapping.GetLocalizedName(name);
            return new AntdUI.CellLink(Guid.NewGuid().ToString(), display)
            {
                Tooltip = name,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        /// <summary>
        /// file_dbr 字段的"浏览"按钮数组。
        /// </summary>
        public static AntdUI.CellLink[] GetDbrBrowseLinks()
        {
            return new AntdUI.CellLink[] {
                new AntdUI.CellButton("browse", LanguageManager.Instance.GetText("Editor.Browse", "浏览"), AntdUI.TTypeMini.Default)
            };
        }

        /// <summary>
        /// 模板匹配：先精确匹配（含 database/templates/ 前缀），失败按文件名后缀回退。
        /// </summary>
        /// <summary>
        /// 模板匹配（口径见 <see cref="TemplateNameResolver"/> —— **全项目唯一**，不要在这里另写一套）。
        /// <para>旧实现最后一步是"按文件名取**第一个**同名模板"，同名不同目录会**静默串模板**（比找不到更危险）；
        /// 现统一走解析器：靠前的标准候选不中时才按文件名兜底，且**必须唯一**。</para>
        /// </summary>
        public static TemplateNode MatchTemplate(Dictionary<string, TemplateNode> templateDict, string tplName)
            => TemplateNameResolver.Resolve(templateDict, tplName).Template;

        /// <summary>
        /// 构建模板字典：key 统一规范化（小写 + / 分隔 + database/templates/ 前缀）。
        /// </summary>
        public static Dictionary<string, TemplateNode> BuildTemplateDict(IEnumerable<TemplateNode> templates)
        {
            var dict = new Dictionary<string, TemplateNode>(StringComparer.OrdinalIgnoreCase);
            if (templates == null) return dict;
            foreach (var tpl in templates)
            {
                if (tpl == null) continue;
                string key = tpl.TemplateFile?.ToLower().Replace('\\', '/');
                if (string.IsNullOrEmpty(key)) continue;
                if (!key.StartsWith("database/templates/"))
                    key = "database/templates/" + key;
                if (!dict.ContainsKey(key))
                    dict[key] = tpl;
            }
            return dict;
        }

        /// <summary>
        /// 判断是否为 bool / picklist 字段（支持下拉选择）。
        /// </summary>
        public static bool IsPicklistField(TemplateVariableInfo v)
        {
            return v != null && (v.Type == "bool" || v.PicklistOptions.Length > 0);
        }

        /// <summary>
        /// 弹出 bool/picklist 下拉选择。
        /// </summary>
        /// <param name="host">宿主窗体（负责承载下拉控件）</param>
        /// <param name="anchorScreenPt">锚定屏幕坐标点（按钮单元格 PointToScreen 后取 Rect 左下角）</param>
        /// <param name="v">变量信息</param>
        /// <param name="currentValue">当前值（; 分隔）</param>
        /// <param name="onResult">选择结果回调（; 分隔字符串，含空串表示清空）</param>
        public static void ShowPicklistDropdown(
            Control host,
            Point anchorScreenPt,
            TemplateVariableInfo v,
            string currentValue,
            Action<string> onResult)
        {
            if (host == null || v == null || onResult == null) return;

            string[] options = v.Type == "bool"
                ? new[] { "", "0", "1" }
                : v.PicklistOptions;

            var select = new AntdUI.SelectMultiple
            {
                CheckMode = true,
                Width = 1,
                Height = 1,
                Font = FontHelper.UiFont(10),
                Placement = AntdUI.TAlignFrom.TR,
                ListAutoWidth = true,
                MaxCount = 20
            };
            foreach (var opt in options)
                select.Items.Add(opt);

            string[] currentSelected = string.IsNullOrEmpty(currentValue)
                ? Array.Empty<string>()
                : currentValue.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            select.SelectedValue = currentSelected;

            select.Location = host.PointToClient(anchorScreenPt);
            host.Controls.Add(select);
            select.BringToFront();
            select.CreateControl();

            bool handled = false;
            select.ExpandDropChanged += (s, expanded) =>
            {
                if (!expanded.Value && !handled)
                {
                    handled = true;
                    var selected = select.SelectedValue;
                    string result = selected.Length > 0 ? string.Join(";", selected) : "";
                    host.Controls.Remove(select);
                    select.Dispose();
                    onResult(result);
                }
            };
            select.ExpandDrop = true;
        }
    }
}
