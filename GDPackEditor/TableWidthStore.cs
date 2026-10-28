using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using AntdUI;

namespace GDPackEditor
{
    /// <summary>
    /// 主要表格列宽记忆（快照式，方案见 plan_20261010_记忆主要表格列宽.md）：
    /// <para>· <b>Apply</b>（开窗 / 列定义构建完成后调用）：应用记忆列宽；首次调用时捕获各列原始 Width，供「重置」恢复默认。</para>
    /// <para>· <b>AttachAutoSave</b>（Apply 后调用）：MouseUp 防抖 500ms 自动保存——AntdUI 无列宽变化公开事件，
    /// 用「松手后比对快照」兜住常规交互，拖完列宽即存，不依赖关窗。</para>
    /// <para>· <b>Save</b>（关窗 / 主窗体退出代理时调用）：按「列顺序」整表快照——读 WidthPixel（库布局回写的实际像素）
    /// ÷ 当前缩放比 = 逻辑像素，写入 settings；与现有记录一致时跳过写盘（自动保存的普通点击不产生 IO）。</para>
    /// <para>· <b>Reset</b>：清除记录。右键菜单入口带表格实例，立即恢复原始列宽；设置页入口仅清记录，下次开窗生效。</para>
    /// </summary>
    /// <remarks>
    /// 记录按列顺序数组存储（不绑列 Key），列数不符时整条弃用走默认（兼容版本加 / 减列）；
    /// 数值为逻辑像素（与 DPI 缩放无关），应用时直接写 Width 字符串（AntdUI 布局内部乘 Dpi）。
    /// AntdUI 无列宽变化公开事件，保存靠三层兜底：MouseUp 防抖自动保存 → 关窗 Save → 主窗体退出代理保存
    /// （非模态子窗体在主窗体先关时收不到自身 OnFormClosing，由主窗体遍历 OpenForms 代存，见 GuiMainForm）。
    /// </remarks>
    internal static class TableWidthStore
    {
        /// <summary>接入列宽记忆的表格标识（与 SettingsManager 的 Tw* 配置键一一对应）</summary>
        internal enum TableId { FileList, DbrGrid, MergeTable, SearchResults }

        /// <summary>
        /// 原始列宽缓存（首次 Apply 时捕获的各列 Width 字符串）：重置时据此恢复列定义默认值。
        /// ConditionalWeakTable 随表格实例 GC 自动清理，天然支持编辑器多开实例（每实例独立快照）。
        /// </summary>
        private static readonly ConditionalWeakTable<Table, List<string>> _originalWidths
            = new ConditionalWeakTable<Table, List<string>>();

        /// <summary>AutoSave 防重挂载标记（Apply 可幂等重复调用，MouseUp 只挂一次）</summary>
        private static readonly ConditionalWeakTable<Table, object> _autoSaveAttached
            = new ConditionalWeakTable<Table, object>();

        /// <summary>AutoSave 防抖计时器（每表一个，随表 GC 清理；回调内自行检查 IsDisposed）</summary>
        private static readonly ConditionalWeakTable<Table, System.Windows.Forms.Timer> _autoSaveTimers
            = new ConditionalWeakTable<Table, System.Windows.Forms.Timer>();

        #region 开关 / 记录读写（对 SettingsManager 各键的分发）

        private static bool IsEnabled(TableId id)
        {
            switch (id)
            {
                case TableId.FileList: return SettingsManager.TwEnabledFileList;
                case TableId.DbrGrid: return SettingsManager.TwEnabledDbrGrid;
                case TableId.MergeTable: return SettingsManager.TwEnabledMergeTable;
                case TableId.SearchResults: return SettingsManager.TwEnabledSearchResults;
                default: return false;
            }
        }

        private static List<int> GetWidths(TableId id)
        {
            switch (id)
            {
                case TableId.FileList: return SettingsManager.TwWidthsFileList;
                case TableId.DbrGrid: return SettingsManager.TwWidthsDbrGrid;
                case TableId.MergeTable: return SettingsManager.TwWidthsMergeTable;
                case TableId.SearchResults: return SettingsManager.TwWidthsSearchResults;
                default: return null;
            }
        }

        private static void SetWidths(TableId id, List<int> widths)
        {
            switch (id)
            {
                case TableId.FileList: SettingsManager.TwWidthsFileList = widths; break;
                case TableId.DbrGrid: SettingsManager.TwWidthsDbrGrid = widths; break;
                case TableId.MergeTable: SettingsManager.TwWidthsMergeTable = widths; break;
                case TableId.SearchResults: SettingsManager.TwWidthsSearchResults = widths; break;
            }
        }

        /// <summary>
        /// 该表是否已有生效的列宽记忆（开关开 且 记录非空）。
        /// 包内搜索结果页据此短路「逐条测宽」逻辑（有记忆时列宽固定，无需动态测宽）。
        /// </summary>
        internal static bool HasRecord(TableId id)
        {
            if (!IsEnabled(id)) return false;
            var w = GetWidths(id);
            return w != null && w.Count > 0;
        }

        #endregion

        #region 应用 / 快照 / 重置

        /// <summary>
        /// 应用记忆列宽（各表列定义构建完成后调用；可幂等重复调用）。
        /// 无记录 → 不动（保持各表列定义默认宽度）；记录列数与当前列数不符 → 整条弃用。
        /// </summary>
        internal static void Apply(TableId id, Table table)
        {
            if (table == null || table.Columns == null) return;
            // 首次捕获原始列宽（此后重复调用不覆盖首次快照，「重置」以本次为准）
            if (!_originalWidths.TryGetValue(table, out var origin))
            {
                origin = table.Columns.Select(c => c == null ? null : c.Width).ToList();
                _originalWidths.Add(table, origin);
            }
            if (!IsEnabled(id)) return;
            var widths = GetWidths(id);
            if (widths == null || widths.Count == 0) return;   // 无记录 → 保持默认
            var cols = table.Columns.ToList();
            if (widths.Count != cols.Count) return;            // 列数不符 → 整条弃用
            for (int i = 0; i < cols.Count; i++)
            {
                if (cols[i] == null || widths[i] <= 0) continue;
                // Width 字符串数字为"设计像素"，AntdUI 布局时内部会乘 Dpi（Table.Layout.ColumnWidth），
                // 这里直接写逻辑像素即可；再乘 DpiManager.S 会导致双重缩放（高 DPI 下列宽每轮保存滚动膨胀）。
                cols[i].Width = widths[i].ToString();
            }
            RefreshLayout(table);
        }

        /// <summary>
        /// 保存列宽快照（关窗时调用）：整表按列顺序覆盖写入 settings 并落盘。
        /// 可见列未布局（WidthPixel ≤ 0）→ 本次整条不保存，保留旧记录；
        /// 隐藏列（Visible=false，如搜索表树形下的「包名」列）→ 记 0，应用时跳过（保持列定义默认）。
        /// </summary>
        internal static void Save(TableId id, Table table)
        {
            if (table == null || table.Columns == null) return;
            if (!IsEnabled(id)) return;
            try
            {
                float scale = DpiManager.Scale;
                if (scale <= 0) scale = 1f;
                var widths = new List<int>();
                foreach (var c in table.Columns)
                {
                    if (c == null) return;
                    if (!c.Visible) { widths.Add(0); continue; }   // 隐藏列：记 0，应用时跳过
                    // WidthPixel 为库布局回写的实际像素，换算回逻辑像素存储（与缩放无关）
                    int logical = (int)Math.Round(c.WidthPixel / scale, MidpointRounding.AwayFromZero);
                    if (logical <= 0) return;   // 可见列未布局 → 整条弃用
                    widths.Add(logical);
                }
                if (widths.Count == 0) return;
                // 去重：与现有记录一致 → 跳过写盘（MouseUp 防抖自动保存的普通点击不产生 IO）
                var old = GetWidths(id);
                if (old != null && old.Count == widths.Count && old.SequenceEqual(widths)) return;
                SetWidths(id, widths);
                SettingsManager.Save();
            }
            catch (Exception ex)
            {
                LogService.Warn($"[tablewidth] save failed: {ex.Message}");
            }
        }

        /// <summary>重置（右键菜单入口）：清记录 + 立即恢复该表原始列宽（首次 Apply 时捕获的默认值）</summary>
        internal static void Reset(TableId id, Table table)
        {
            SetWidths(id, null);
            if (table != null && _originalWidths.TryGetValue(table, out var origin))
            {
                var cols = table.Columns == null ? null : table.Columns.ToList();
                if (cols != null && origin != null && origin.Count == cols.Count)
                {
                    for (int i = 0; i < cols.Count; i++)
                    {
                        if (cols[i] != null) cols[i].Width = origin[i];   // 原始为 null 时恢复"未设置"状态
                    }
                    RefreshLayout(table);
                }
            }
            SettingsManager.Save();
        }

        /// <summary>重置（设置页入口）：仅清记录，下次开窗生效（设置页无表格实例）</summary>
        internal static void Reset(TableId id)
        {
            SetWidths(id, null);
            SettingsManager.Save();
        }

        /// <summary>
        /// 为表格挂上 MouseUp 防抖自动保存（Apply 后调用）：任何松手（含拖列宽结束）停 500ms 后
        /// 比对快照保存一次，有变化才写盘。AntdUI 无列宽变化公开事件，用 MouseUp 兜住常规交互，
        /// 避免强杀进程 / 主窗体先关时丢失本次调整。与右键重置菜单（MouseClick）互不干扰。
        /// </summary>
        internal static void AttachAutoSave(TableId id, Table table)
        {
            if (table == null) return;
            if (_autoSaveAttached.TryGetValue(table, out _)) return;   // 防重挂载
            _autoSaveAttached.Add(table, true);
            table.MouseUp += (s, e) =>
            {
                if (!IsEnabled(id)) return;
                if (table.IsDisposed || table.Disposing || !table.IsHandleCreated) return;
                // 防抖：连续点击 / 拖动只在停手 500ms 后保存一次
                if (!_autoSaveTimers.TryGetValue(table, out var timer))
                {
                    timer = new System.Windows.Forms.Timer { Interval = 500 };
                    _autoSaveTimers.Add(table, timer);
                    timer.Tick += (s2, e2) =>
                    {
                        timer.Stop();
                        if (!table.IsDisposed && !table.Disposing) Save(id, table);
                    };
                }
                timer.Stop();
                timer.Start();
            };
        }

        /// <summary>
        /// 为表格挂上右键「重置本表列宽记忆」菜单（供没有现有右键菜单的表格使用；
        /// 已有菜单的表格自行在分发分支里调 <see cref="Reset(TableId, Table)"/>，避免两套菜单）。
        /// </summary>
        internal static void AttachResetMenu(TableId id, Table table)
        {
            if (table == null) return;
            table.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var item = new AntdUI.ContextMenuStripItem(
                    LanguageManager.Instance.GetText("Editor.ResetTableWidth", "重置本表列宽记忆")) { Tag = "resetWidths" };
                AntdUI.ContextMenuStrip.open(table, it =>
                {
                    if ((it.Tag as string) == "resetWidths") Reset(id, table);
                }, new AntdUI.IContextMenuStripItem[] { item });
            };
        }

        /// <summary>
        /// 触发表格重排重绘（宽度字符串写入后界面立即生效）。
        /// AntdUI 的 Column.Width 是纯自动属性（写值不触发重排），需 LoadLayout + Invalidate；
        /// 句柄未创建（开窗列定义阶段）时跳过 —— 首次布局自然按新宽度排列。
        /// </summary>
        private static void RefreshLayout(Table table)
        {
            try
            {
                if (table == null || table.IsDisposed || !table.IsHandleCreated) return;
                table.LoadLayout();
                table.Invalidate();
            }
            catch { }
        }

        #endregion
    }
}
