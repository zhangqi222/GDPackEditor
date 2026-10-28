using System;
using System.IO;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using Label = AntdUI.Label;
using TextBox = AntdUI.Input;
using CheckBox = AntdUI.Checkbox;
using RadioButton = AntdUI.Radio;
using Panel = AntdUI.Panel;
using ComboBox = AntdUI.Dropdown;

namespace GDPackEditor
{
    public partial class GuiMainForm : AntdUI.BaseForm
    {
        // 用于保存database子目录选择列表的控件引用
        private AntdUI.StackPanel databaseSubDirsCheckedListBox;
        
        // 顶部右侧「设置」按钮（打开设置集中管理窗）
        private AntdUI.Button settingsButton;

        // 主选项卡控件（用于切页时刷新各历史表）
        private AntdUI.Tabs _mainTabs;

        // ═══ 包内搜索选项卡控件引用 ═══
        private AntdUI.Input _searchDirBox;             // 搜索目录（必填）
        private AntdUI.Input _searchNameBox;            // 文件名条件（可空；支持通配与/或）
        private AntdUI.Input _searchContentBox;         // 内容条件（可空；支持与/或/短语/通配）
        // 排除框（方案 §30）：逻辑非整体收敛到这里（包含框不再有词首 -/!）；**文件级**语义；不记历史
        private AntdUI.Input _searchDirExcludeBox;      // 目录排除（包文件完整磁盘路径，固定不敏感）
        private AntdUI.Input _searchNameExcludeBox;     // 文件名排除（包内相对路径）
        private AntdUI.Input _searchContentExcludeBox;  // 内容排除（全文任一行命中 → 整个文件丢弃）
        /// <summary>本次搜索"内容条件里第一个正向词"（供打开文件时在编辑器里定位；每次搜索开始时算一次）</summary>
        private string _searchContentLocateTerm;
        // ═══ 搜索历史（自绘浮层；不用弹窗/表格）═══
        private AntdUI.Button _searchDirHistoryBtn;     // 目录行的「历史」按钮
        private AntdUI.Button _searchNameHistoryBtn;    // 文件名行的「历史」按钮
        private AntdUI.Button _searchContentHistoryBtn; // 内容行的「历史」按钮
        /// <summary>当前打开的搜索历史浮层（用 IClose 收起）</summary>
        private Form _searchHistoryPopover;
        /// <summary>当前浮层属于哪套历史（目录/文件名/内容）；用于"点同一个按钮则收起"</summary>
        private SearchHistoryKind _searchHistoryPopoverKind;
        /// <summary>浮层内的列表容器（置顶/清理后就地重建）</summary>
        private AntdUI.Panel _searchHistoryListPanel;
        /// <summary>浮层内的"共 N 条"标签与「清空全部」按钮（就地刷新用）</summary>
        private AntdUI.Label _searchHistoryCountLabel;
        private AntdUI.Button _searchHistoryClearBtn;
        /// <summary>浮层内图标按钮的悬停提示（AntdUI 扩展器；只建一次）</summary>
        private AntdUI.TooltipComponent _searchHistoryTip;
        private AntdUI.Checkbox _searchIncludeArz;      // 搜索 ARZ 包（目录行行内开关，短标签 arz）
        private AntdUI.Checkbox _searchIncludeArc;      // 搜索 ARC 包（目录行行内开关，短标签 arc）
        private AntdUI.Checkbox _searchIncludeSubDir;   // 包含子目录（目录行行内开关，短标签 子）
        private AntdUI.Checkbox _searchCaseSensitive;   // 区分大小写（内容行行内开关，短标签 Aa；批21 起仅作用于内容）
        private AntdUI.Checkbox _searchExactMatch;      // 完全匹配（内容行行内开关，短标签 全；批21 新增）
        private AntdUI.Checkbox _searchLangReverse;     // 本地化反查（内容行行内开关，短标签 反；批20 起状态持久化）
        private Label _searchLangPreviewLink;           // 「预览」链接（原 >>；随「反」开关状态变灰/变蓝，见 UpdateLangReverseUiState）
        /// <summary>行内开关的悬停提示（AntdUI 扩展器；与历史浮层的 tip 分开建，只建一次）</summary>
        private AntdUI.TooltipComponent _searchSwitchTip;
        /// <summary>行内开关 tooltip 的 (控件, 文案key, 回退文案) 登记（切语言时按 key 重刷，见 RefreshSearchSwitchTips）</summary>
        private readonly List<(Control ctl, string key, string fallback)> _searchSwitchTips = new();
        private AntdUI.Button _searchButton;            // 开始搜索
        private AntdUI.Button _searchCancelButton;      // 取消（搜索中显示）
        private AntdUI.Table _searchResultTable;        // 结果表格（虚拟模式）
        private AntdUI.Column _searchColArchive;        // 包名列（树形下隐藏：包名已是树节点）
        private AntdUI.Column _searchColFile;           // 名称列（SetTree + Render，两种形态共用）
        private AntdUI.Column _searchColContent;        // 内容列（绑 Content=单行显示值；多行文本会被行高裁空，见 ContentRaw）
        private AntdUI.Column _searchColDescription;    // 说明列（跟随开关显隐）
        private AntdUI.Column _searchColLocalized;      // 本地化名称列
        private AntdUI.Column _searchColTemplate;       // 模板列
        private AntdUI.AntList<SearchResultRow> _searchResultRows;   // 表格数据源（当前显示集）
        private AntdUI.Input _searchFilterBox;          // 结果筛选
        private AntdUI.Panel _searchFilterRow;          // 筛选行容器（#1：默认隐藏，有结果时才显示）
        private AntdUI.Label _searchStatsLabel;         // 统计信息
        private Label _searchStatusLabel;               // 状态/进度文字（在 statusRow 内）
        private System.Windows.Forms.Timer _searchFilterDebounce;   // 筛选防抖（300ms）

        /// <summary>当前搜索的全部命中行（筛选时从这里重建表格数据源）</summary>
        private readonly List<SearchResultRow> _searchAllRows = new List<SearchResultRow>();

        /// <summary>本次搜索使用的语言包字典（tag → 本地化名）；切语言时按行内 RawTag 重算本地化名称</summary>
        private Dictionary<string, string> _searchLangTags;

        /// <summary>
        /// 搜索结果行。显示字段与表格列 Key 一一对应；后半段是非显示字段（打开、筛选、切语言用）。
        /// </summary>
        private sealed class SearchResultRow
        {
            // ── 显示（列 Key 同名）──
            public string Archive { get; set; }       // 包名
            public string File { get; set; }          // 包内文件名（相对路径）
            public string Size { get; set; }          // 大小（已格式化 KB/MB）
            public long SizeBytes { get; set; }       // 大小原始字节数（按列排序用；显示列是格式化文本）
            public string Time { get; set; }          // 时间戳
            public string Description { get; set; }   // 说明（仅 .dbr）
            public string Localized { get; set; }     // 本地化名称（仅 .dbr）
            public string Template { get; set; }      // 模板（仅 .dbr）
            public string Hits { get; set; }          // 命中数（内容模式）
            public string Content { get; set; }       // 命中行上下文：**单元格显示值（单行）**，多处命中以 " ／ " 分隔
            /// <summary>
            /// 命中行上下文的**原值**：多处命中以 `\n` 分隔（**不直接进单元格**）。
            /// <para>为什么拆两个字段：单元格里的文本必须是**单行** —— AntdUI 的单元格是"整块文本在行高内竖直居中"，
            /// 多行整块（实测 79.2px）远高于单行行高（26px），居中后矩形内**没有一行是完整的**，
            /// 而 `StringFormat.GenericTypographic` 自带 `LineLimit`（只画完整行）→ **整格空白**
            /// （现象：命中数=1 正常、多行空白）。</para>
            /// <para>所以：<see cref="Content"/> = 单行显示值（单元格绑定，列 key 仍是 `Content`）；
            /// 本字段 = 原值，供**右键「复制」（保留换行）**与**结果表筛选**使用。</para>
            /// </summary>
            public string ContentRaw { get; set; }
            public AntdUI.CellLink[] Action { get; set; }   // 操作列：打开按钮

            // ── 非显示（打开/筛选/切语言/复制）──
            public string ArchivePath { get; set; }
            public string RelPath { get; set; }
            public string Keyword { get; set; }
            public string RawTag { get; set; }        // 原始 tag：切语言时据此重算 Localized，无需重新解包
            public int RecordIndex { get; set; }
            public int MatchCount { get; set; }
            /// <summary>该行来自内容搜索（true）还是文件名搜索（false）——决定「打开」是定位文件列表还是打开文件内容</summary>
            public bool ContentMode { get; set; }
            /// <summary>
            /// 原始命中对象（**未截断**的上下文 / 说明 / 本地化名都在这）。
            /// <para>显示字段已过 <c>SafeCellText</c> 去换行截断，右键「复制」要拿原值，故保留引用。</para>
            /// </summary>
            public ArchiveSearchHit Hit { get; set; }

            // ── 文件树形态（#5）：平铺时 Children 为 null，AntdUI 自动不显示缩进与展开箭头 ──
            /// <summary>子节点（仅文件树形态下的目录节点有值）</summary>
            public List<SearchResultRow> Children { get; set; }
            /// <summary>是否目录/包节点（true = 文件树里的非叶节点）</summary>
            public bool IsDirectory { get; set; }
            /// <summary>树形下的节点显示名（带 📁/📄 前缀）；平铺形态不使用</summary>
            public string DisplayName { get; set; }
            /// <summary>树节点的唯一路径（建树去重 + 排序用）</summary>
            public string NodePath { get; set; }
            /// <summary>树节点类别：决定树形下名称列的显示色（见 <see cref="SearchNodeKind"/>）</summary>
            public SearchNodeKind NodeKind { get; set; }
        }
        
        // 版本号标签
        private Label versionLabel;
        
        // 需要动态多语言更新的输入框列表
        private List<(TextBox Control, string Key, string Fallback)> _localizedPrefixes = new List<(TextBox, string, string)>();
        // 需要动态多语言更新的占位文字列表
        private List<(TextBox Control, string Key, string Fallback)> _localizedPlaceholders = new List<(TextBox, string, string)>();
        
        // 当前正在执行的操作进度回调（用于取消）
        private GuiProgressCallback _activeProgress;
        
        // 请求取消当前操作
        private void CancelActiveOperation()
        {
            _activeProgress?.Cancel();
        }
        
        /// <summary>
        /// 生成搜索页签的「就绪」状态行容器：只放左侧「就绪」文字（表单校验提示与结果汇总的常驻出口）。
        /// 注意：状态标签必须保留 Name="Common.StatusLabel"，搜索页签靠 Controls.Find(..., true) 取它刷新状态。
        /// </summary>
        private AntdUI.Panel BuildStatusRow(DockStyle dock)
        {
            var statusLabel = new Label
            {
                Text = LanguageManager.Instance.GetText("Common.StatusLabel", "就绪"),
                Name = "Common.StatusLabel",
                Font = FontHelper.UiFont(9),
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            };

            var statusRow = new AntdUI.Panel { Dock = dock, Back = Color.Transparent };
            if (dock == DockStyle.Top) statusRow.Height = 30;
            statusRow.Controls.Add(statusLabel);   // Fill：先加，最后处理 → 占剩余空间
            return statusRow;
        }

        /// <summary>
        /// 让 Dock=Top 的内容容器高度始终贴合内部子行（含 Margin）。
        /// 构建与 DPI 缩放后布局事件都会触发，按子行实际底边校正，避免 125%/150% 缩放下行被截断。
        /// （内容容器是 Spin 遮罩目标，高度精确 = 遮罩范围精确）
        /// </summary>
        private void AttachContentAutoHeight(AntdUI.Panel contentPanel)
        {
            contentPanel.Layout += (s, e) =>
            {
                int maxBottom = 0;
                foreach (Control c in contentPanel.Controls)
                {
                    if (!c.Visible) continue;
                    int b = c.Bottom + c.Margin.Bottom;
                    if (b > maxBottom) maxBottom = b;
                }
                if (maxBottom > 0 && contentPanel.Height != maxBottom)
                    contentPanel.Height = maxBottom;
            };
        }

        /// <summary>
        /// 操作结果弹窗（统一调度到 UI 线程）：Spin 后台动作内调用时先收遮罩再弹窗，
        /// 避免弹窗与遮罩/取消按钮同屏堆叠；UI 线程上调用则立即显示。
        /// </summary>
        private void ShowResultBox(string textKey, string textFallback, MessageBoxIcon icon)
        {
            string titleKey = icon == MessageBoxIcon.Error ? "Common.MessageBoxTitle.Error"
                            : icon == MessageBoxIcon.Warning ? "Common.MessageBoxTitle.Warning"
                            : "Common.MessageBoxTitle.Information";
            string titleFallback = icon == MessageBoxIcon.Error ? "错误"
                                 : icon == MessageBoxIcon.Warning ? "提示" : "成功";
            void show()
            {
                MessageBox.Show(
                    LanguageManager.Instance.GetText(textKey, textFallback),
                    LanguageManager.Instance.GetText(titleKey, titleFallback),
                    MessageBoxButtons.OK, icon);
            }
            if (InvokeRequired) { try { BeginInvoke((Action)show); } catch { } }
            else show();
        }

        /// <summary>
        /// 4 个操作页签统一的执行骨架：AntdUI.Spin 遮罩罩住上半部内容容器（日志区保持可见可滚动），
        /// 遮罩自带「转圈 + 进度文字 + 取消按钮」，点取消自动触发取消令牌。各流程只负责
        /// 执行前校验（调用方自行完成，走 MessageBox）与 work 主体（Spin 后台线程执行）。
        /// </summary>
        /// <param name="contentPanel">上半部内容容器（遮罩目标）</param>
        /// <param name="logView">本页日志面板</param>
        /// <param name="lockButtons">执行期间禁用的按钮（双保险：遮罩挡鼠标，禁用挡键盘焦点）</param>
        /// <param name="busyPrefix">初始遮罩文字（已本地化的动作名，随后被实时进度文字覆盖）</param>
        /// <param name="work">执行主体：(进度回调, 取消令牌, Spin配置)；进度文字实时更新遮罩；
        /// 结果弹窗请走 <see cref="ShowResultBox"/>（后台动作内自动调度到 UI 线程）</param>
        private async Task RunTabOperationAsync(
            Control contentPanel, LogView logView, AntdUI.Button[] lockButtons,
            string busyPrefix, Func<GuiProgressCallback, System.Threading.CancellationToken, AntdUI.Spin.Config, Task> work)
        {
            if (contentPanel == null || lockButtons == null || lockButtons.Length == 0) return;

            // 防重入双保险：任一按钮已禁用说明本组操作正在执行（正常不会发生）
            foreach (var b in lockButtons)
                if (b != null && !b.Enabled) return;
            foreach (var b in lockButtons)
                if (b != null) b.Enabled = false;

            var spinConfig = new AntdUI.Spin.Config
            {
                Text = busyPrefix,
                // 显式收小遮罩文字：默认跟随窗体字体观感偏大，按主界面字号体系减小 2 号
                Font = FontHelper.UiFont(10),
                CancelText = LanguageManager.Instance.GetText("Common.CancelButton", "取消")
            };

            // 进度文字出口：后台线程 → UI 线程，实时更新 Spin 遮罩文字（如「解包 45% · 123/456」）
            var progress = new GuiProgressCallback(logView, (pct, msg) =>
            {
                try
                {
                    if (IsDisposed) return;
                    BeginInvoke((Action)(() => spinConfig.Text = msg));
                }
                catch { }
            });
            spinConfig.CancelTokenSource = progress.TokenSource;   // Spin「取消」→ 令牌 → 后台检查点
            progress.ShowDetailLog = SettingsManager.ShowDetailLog;

            try
            {
                await AntdUI.Spin.open(contentPanel, spinConfig, async cfg =>
                {
                    try
                    {
                        await work(progress, progress.Token, cfg);
                    }
                    catch (OperationCanceledException)
                    {
                        logView?.Enqueue(LanguageManager.Instance.GetText("LOG.OperationTerminated", "操作已终止"));
                    }
                    catch (Exception ex)
                    {
                        if (progress.IsCancellationRequested)
                        {
                            logView?.Enqueue(LanguageManager.Instance.GetText("LOG.OperationTerminated", "操作已终止"));
                            return;
                        }
                        progress.ReportError(ex.Message);
                        ShowResultBox("Common.MessageBoxProcessError", "操作过程中发生错误：" + ex.Message, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        progress.LogFinalize();
                    }
                });
            }
            finally
            {
                foreach (var b in lockButtons)
                    if (b != null) b.Enabled = true;
                try { progress.Dispose(); } catch { }
            }
        }

        public GuiMainForm()
        {
            // 窗口级主题配置
            Theme()
                .Light("#f0f0f0", "#000000")
                .Dark("#333333", "#ffffff");
            
            // 设置窗口背景色为浅灰色
            this.BackColor = ThemeManager.BgLayout;
            
            SetupUI();
            InitLanguageSupport();
            
        }

        /// <summary>自定义语言包不可用的启动提示只弹一次（OnLoad 可能被重入）</summary>
        private bool _langWarningShown;

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            RestoreMainWindowState();

            // 自定义语言包不存在/读不出来时，LanguageManager 已回落到内置英文并在 LastLoadError 留了原因；
            // 这里给用户一次明确提示。配置里仍保留他的自定义项，所以他"到设置中更改"才有东西可改。
            // 放在 OnLoad 而不是 Program.cs：CLI 分支不会构造本窗体，天然不会误弹 GUI 弹窗。
            if (!_langWarningShown && !string.IsNullOrEmpty(LanguageManager.Instance.LastLoadError))
            {
                _langWarningShown = true;
                try { LogService.Warn($"[language] {LanguageManager.Instance.LastLoadError}"); } catch { }
                MessageBox.Show(this,
                    LanguageManager.Instance.GetText("Startup.CustomLanguageMissing",
                        "自定义语言包不存在或无法读取，将使用默认英文，请到设置中更改。"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 恢复主窗口状态：尺寸 → 位置 → 最大化（最大化最后应用，避免普通尺寸限制最大化）。
        /// <para>位置用「与某屏幕工作区**重叠 ≥ 100×100**」校验，防止拔掉外接屏后窗口跑到屏幕外找不回来
        /// （旧写法用 `IntersectsWith`，只要沾一点边就算"可见"，会出现窗口 99% 在屏外）。</para>
        /// <para>尺寸按目标屏幕工作区收敛上限，避免高 DPI 屏切到低 DPI 屏后窗口超出屏幕。</para>
        /// <para>"是否保存过"看 <see cref="SettingsManager.HasMainWindowState"/>，不再用 <c>X==0 &amp;&amp; Y==0</c>
        /// 当哨兵（那会把主屏左上角/贴边误判成首次运行而强行居中）。</para>
        /// </summary>
        private void RestoreMainWindowState()
        {
            try
            {
                if (!SettingsManager.HasMainWindowState) return;   // 首次运行：保持 CenterScreen 默认尺寸

                // 1) 尺寸：不小于 MinimumSize（小于则保持默认尺寸）
                int w = SettingsManager.MainWindowWidth;
                int h = SettingsManager.MainWindowHeight;

                // 2) 位置：与某屏幕工作区重叠 ≥ 100×100 才算"可见"
                var rect = new Rectangle(SettingsManager.MainWindowX, SettingsManager.MainWindowY, w, h);
                var target = Screen.AllScreens
                    .OrderByDescending(s => IntersectArea(s.WorkingArea, rect))
                    .FirstOrDefault() ?? Screen.PrimaryScreen;

                if (OverlapAtLeast(target.WorkingArea, rect, MinVisiblePx, MinVisiblePx))
                {
                    // 尺寸收敛到目标屏幕工作区上限（高 DPI → 低 DPI 切换时防止超出屏幕）
                    w = Math.Min(w, target.WorkingArea.Width);
                    h = Math.Min(h, target.WorkingArea.Height);
                    if (w >= MinimumSize.Width && h >= MinimumSize.Height)
                        Size = new Size(w, h);
                    StartPosition = FormStartPosition.Manual;
                    Location = new Point(SettingsManager.MainWindowX, SettingsManager.MainWindowY);
                }
                // 不可见则保持 StartPosition=CenterScreen（回退居中）

                // 3) 最大化最后应用
                if (SettingsManager.IsMainWindowMaximized)
                    WindowState = FormWindowState.Maximized;
            }
            catch (Exception ex)
            {
                try { LogService.Warn($"[window] restore state failed: {ex.Message}"); } catch { }
            }
        }

        /// <summary>判定"窗口可见"所需的最小重叠边长（像素）</summary>
        private const int MinVisiblePx = 100;

        private static int IntersectArea(Rectangle a, Rectangle b)
        {
            var r = Rectangle.Intersect(a, b);
            return r.Width <= 0 || r.Height <= 0 ? 0 : r.Width * r.Height;
        }

        /// <summary>两个矩形是否至少重叠 <paramref name="minW"/> × <paramref name="minH"/></summary>
        private static bool OverlapAtLeast(Rectangle a, Rectangle b, int minW, int minH)
        {
            var r = Rectangle.Intersect(a, b);
            return r.Width >= minW && r.Height >= minH;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _windowStateTimer?.Stop();
            SaveMainWindowState();
            // 关窗快照列宽记忆（TableWidthStore：包内搜索结果表；设置开关关闭时内部直接跳过）
            TableWidthStore.Save(TableWidthStore.TableId.SearchResults, _searchResultTable);
            // 主窗体先关时，非模态子窗体（编辑器 / 合并工具）收不到自身 OnFormClosing（消息循环随主窗体
            // 结束，进程直接终止），这里遍历 OpenForms 代存其列宽记忆——否则这三个表的列宽永远丢失。
            // Save 内部去重，与子窗体随后自行关窗的 Save 重复调用无 IO。
            foreach (Form f in Application.OpenForms)
            {
                if (f == this || f.IsDisposed || f.Disposing) continue;
                if (f is ArzArcEditorForm editor) editor.SaveColumnWidths();
                else if (f is ModMergeToolForm merge) merge.SaveColumnWidths();
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
        }

        /// <summary>普通状态下移动/缩放后的持久化防抖定时器（见 <see cref="ScheduleWindowStateSave"/>）</summary>
        private System.Windows.Forms.Timer _windowStateTimer;

        /// <summary>
        /// 移动/缩放结束后 **防抖** 保存窗口状态（500ms）。
        /// <para>为什么不能只在关闭时保存：崩溃 / 强杀 / 断电时那次尺寸位置会全部丢失。</para>
        /// <para>任何窗口状态都可安全保存：<see cref="SaveMainWindowState"/> 在非普通状态一律取 `RestoreBounds`，
        /// 不会把最大化尺寸或最小化的 -32000 占位坐标写进配置。</para>
        /// </summary>
        private void ScheduleWindowStateSave()
        {
            if (_windowStateTimer == null)
            {
                _windowStateTimer = new System.Windows.Forms.Timer { Interval = 500 };
                _windowStateTimer.Tick += (s, e) =>
                {
                    _windowStateTimer.Stop();
                    if (IsDisposed || Disposing) return;
                    SaveMainWindowState();
                };
            }
            _windowStateTimer.Stop();
            _windowStateTimer.Start();
        }

        /// <summary>
        /// 保存主窗口状态。
        /// <para>**普通边界恒用 `(Normal ? Bounds : RestoreBounds)`** —— 最大化/最小化关闭时
        /// `RestoreBounds` 仍是"还原后的普通窗口边界"，所以任何状态下关闭都能记住用户的普通尺寸/位置。
        /// （旧写法在最大化时只记标志、完全不写尺寸，导致"调过的普通大小永远存不下来、
        /// 启动即最大化、点还原只有默认 900×700"—— 用户表现为"只能最大化、回不来了"。）</para>
        /// </summary>
        private void SaveMainWindowState()
        {
            try
            {
                SettingsManager.IsMainWindowMaximized = WindowState == FormWindowState.Maximized;

                Rectangle bounds = Bounds;
                if (WindowState != FormWindowState.Normal)
                {
                    var rb = RestoreBounds;                 // 最小化时 Location 是 -32000 占位坐标，必须走这里
                    if (rb.Width <= 0 || rb.Height <= 0)
                    {
                        // 取不到有效的"普通边界"（极少数时序）：只记标志，绝不写坏坐标
                        SettingsManager.Save();
                        return;
                    }
                    bounds = rb;
                }
                SettingsManager.MainWindowX = bounds.X;
                SettingsManager.MainWindowY = bounds.Y;
                SettingsManager.MainWindowWidth = bounds.Width;
                SettingsManager.MainWindowHeight = bounds.Height;
                SettingsManager.HasMainWindowState = true;
                SettingsManager.Save();
            }
            catch (Exception ex)
            {
                try { LogService.Warn($"[window] save state failed: {ex.Message}"); } catch { }
            }
        }

        /// <summary>
        /// 主窗口默认尺寸。设置窗的默认宽度取这里的 Width，避免两处各写一个数字、日后只改一处而走偏。
        /// </summary>
        public static readonly Size DefaultWindowSize = new Size(900, 700);

        private void SetupUI()
        {
            // 设置主窗口属性
            this.Text = BuildWindowTitle();
            this.Size = DefaultWindowSize;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new System.Drawing.Size(600, 560);
            
            this.AllowDrop = true;

            // 移动/缩放后防抖保存窗口状态（仅普通状态生效）；关闭时另有兜底保存
            this.ResizeEnd += (s, e) => ScheduleWindowStateSave();
            this.Move += (s, e) => ScheduleWindowStateSave();

            // 设置图标
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream("GDPackEditor.app.ico"))
                {
                    if (stream != null) this.Icon = new System.Drawing.Icon(stream);
                }
            }
            catch (Exception ex) { Console.WriteLine("Failed to load icon: " + ex.Message); }

            // ═══ Row 0: 顶部标题栏 (高度: 60px) ═══
            AntdUI.Panel headerPanel = new AntdUI.Panel { Dock = DockStyle.Top, Height = 70, Padding = new Padding(10, 10, 0, 10), Back = Color.Transparent };

            // ─── 标题：品牌名（主色、18px 粗体）+ 本地化软件名（10px、默认色），二者间留间距 ───
            Label titleLabel = new Label { Text = LanguageManager.Instance.GetText("MainForm.Title", "GDPackEditor"), Name = "MainForm.Title", Font = FontHelper.UiFont(18, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft, Dock = DockStyle.Left, AutoSize = true, ForeColor = ThemeManager.Primary };
            Label titleSubLabel = new Label { Text = LanguageManager.Instance.GetText("MainForm.TitleSub", "恐怖黎明包编辑器"), Name = "MainForm.TitleSub", Font = FontHelper.UiFont(10), TextAlign = ContentAlignment.BottomLeft, Dock = DockStyle.Left, AutoSize = true, ForeColor = ThemeManager.Text };
            // AntdUI.Label 会按文字自适应高度，两个不同字号的 Label 顶部对齐、底部自然不齐（TextAlign 没有多余高度可用）。
            // 把副标题的盒子上方补足「主标题盒高 − 副标题盒高」，再扣掉两字体「盒底到墨迹底边」的留白差，才能让两者文字底边真正齐平。
            const int subBottomBias = 6;   // 实测：大字号盒底到字底的留白比小字号多约 6px（随 DPI 缩放）
            titleSubLabel.Padding = new Padding(0, Math.Max(0, titleLabel.Height - titleSubLabel.Height - DpiManager.S(subBottomBias)), 0, 0);
            titleSubLabel.AutoSize = false;   // 触发按含 Padding 的尺寸重新测算
            titleSubLabel.AutoSize = true;
            Label titleGap = new Label { Dock = DockStyle.Left, Width = 8, AutoSize = false };

            // ─── 标题栏右侧：仅「设置」按钮（集中管理入口） ───
            settingsButton = new AntdUI.Button { Text = LanguageManager.Instance.GetText("MainForm.SettingsButton", "设置"), Name = "MainForm.SettingsButton", Font = FontHelper.UiFont(9), IconSvg = "SettingOutlined", Dock = DockStyle.Right, AutoSize = true, Margin = new Padding(0), Type = AntdUI.TTypeMini.Primary };
            settingsButton.Click += (s, e) => ShowSettingsDialog();

            // ─── 反向 Dock：最后 Add 的在最左/最右 ───
            headerPanel.Controls.Add(titleSubLabel);        // Left（在品牌名右侧）
            headerPanel.Controls.Add(titleGap);             // Left（品牌名与本地化名之间的间距）
            headerPanel.Controls.Add(titleLabel);           // Left → 最左
            headerPanel.Controls.Add(settingsButton);       // Right → 最右

            // ═══ Row 1: 选项卡控件 ═══
            _mainTabs = new AntdUI.Tabs
            {
                Dock = DockStyle.Fill,
                TabMenuVisible = true,
                Gap = 12,
                Type = AntdUI.TabType.Card,
                Font = FontHelper.UiFont(12, FontStyle.Bold),
                Style = new AntdUI.Tabs.StyleCard
                {

                    Radius = 0,
                    Gap = -1,
                    BorderColor = ThemeManager.BorderColor,

                }
            };
            _mainTabs.Pages.Add(CreateArzArcEditorTab());
            _mainTabs.Pages.Add(CreateModMergeTab());
            _mainTabs.Pages.Add(CreateUnpackArcTab());
            _mainTabs.Pages.Add(CreatePackArcTab());
            _mainTabs.Pages.Add(CreateUnpackArzTab());
            _mainTabs.Pages.Add(CreatePackArzTab());
            _mainTabs.Pages.Add(CreateArchiveSearchTab());

            // ═══ Row 2: 底部状态栏 (高度: 28px) ═══
            AntdUI.Panel statusBarPanel = new AntdUI.Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(10, 0, 10, 0), Back = Color.Transparent };
            Label authorLabel = new Label { Text = LanguageManager.Instance.GetText("MainForm.AuthorLabel", "作者: laozhangggg"), Name = "MainForm.AuthorLabel", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Left, ForeColor = ThemeManager.TextTertiary, Font = FontHelper.UiFont(9) };

            // 显示详细过程开关（作者标签右侧）：默认关闭时过滤解包/打包每文件的 (N/M) 过程行日志以提升性能 —— 已移入设置窗「主设置」页签
            Label statusSpacer = new Label { Dock = DockStyle.Fill };
            versionLabel = new Label { Text = $"{Program.GUI_VERSION}", Name = "MainForm.VersionLabel", AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Right, Cursor = Cursors.Hand, Font = FontHelper.UiFont(9, FontStyle.Underline), ForeColor = ThemeManager.TextTertiary };
            versionLabel.Click += (s, e) => MarkdownViewer.Show(this, "!ChangeLog.md", "MainForm.ChangeLogTitle");
            Label aboutLabel = new Label { Text = LanguageManager.Instance.GetText("MainForm.AboutLabel", "关于"), Name = "MainForm.AboutLabel", AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Right, Cursor = Cursors.Hand, Font = FontHelper.UiFont(9, FontStyle.Underline), ForeColor = ThemeManager.TextTertiary };
            aboutLabel.Click += (s, e) => MarkdownViewer.Show(this, "!aboutTool.md", "MainForm.AboutTitle");
            Label donateLabel = new Label { Text = LanguageManager.Instance.GetText("MainForm.DonateLabel", "打赏"), Name = "MainForm.DonateLabel", AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Right, Cursor = Cursors.Hand, Font = FontHelper.UiFont(9, FontStyle.Underline), ForeColor = ThemeManager.TextTertiary };
            donateLabel.Click += (s, e) => ShowDonateDialog();
            Label gap3 = new Label { Dock = DockStyle.Right, Width = 10, AutoSize = false };
            // 「打赏」与「关于」之间的间隔：与「关于 | 版本号」的间隔保持一致（反向 Dock：此位置 Add → 落在两者之间）
            Label gapDonateAbout = new Label { Dock = DockStyle.Right, Width = 10, AutoSize = false };
            // 「使用说明」入口：与「关于」同排，点击用内置 Markdown 阅读器显示用户侧 wiki（内嵌资源）
            Label helpLabel = new Label { Text = LanguageManager.Instance.GetText("MainForm.HelpLabel", "使用说明"), Name = "MainForm.HelpLabel", AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Right, Cursor = Cursors.Hand, Font = FontHelper.UiFont(9, FontStyle.Underline), ForeColor = ThemeManager.TextTertiary };
            helpLabel.Click += (s, e) => MarkdownViewer.Show(this, "GDPackEditor_wiki_user.md", "MainForm.HelpTitle");
            Label gapHelpAbout = new Label { Dock = DockStyle.Right, Width = 10, AutoSize = false };
            Label gapAuthorDetail = new Label { Dock = DockStyle.Left, Width = 10, AutoSize = false };
            statusBarPanel.Controls.Add(gapAuthorDetail);
            statusBarPanel.Controls.Add(authorLabel);
            statusBarPanel.Controls.Add(statusSpacer);
            statusBarPanel.Controls.Add(donateLabel);
            statusBarPanel.Controls.Add(gapDonateAbout);
            statusBarPanel.Controls.Add(helpLabel);
            statusBarPanel.Controls.Add(gapHelpAbout);
            statusBarPanel.Controls.Add(aboutLabel);
            statusBarPanel.Controls.Add(gap3);
            statusBarPanel.Controls.Add(versionLabel);
            // 「LICENSE」入口：状态栏最右侧（反向 Dock：最后 Add 在最右），点击用内置 Markdown 阅读器显示根目录 LICENSE（内嵌资源）。
            // 文字固定为 "LICENSE" 不做多语言（许可标识为通用写法），窗口标题仍走多语言 key
            Label gapLicense = new Label { Dock = DockStyle.Right, Width = 10, AutoSize = false };
            Label licenseLabel = new Label { Text = "LICENSE", Name = "MainForm.LicenseLabel", AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Right, Cursor = Cursors.Hand, Font = FontHelper.UiFont(9, FontStyle.Underline), ForeColor = ThemeManager.TextTertiary };
            licenseLabel.Click += (s, e) => MarkdownViewer.Show(this, "LICENSE", "MainForm.LicenseTitle");
            statusBarPanel.Controls.Add(gapLicense);
            statusBarPanel.Controls.Add(licenseLabel);

            // ═══ Form 布局 (Dock: Bottom → Top → Fill) ═══
            this.Controls.Add(_mainTabs);
            this.Controls.Add(headerPanel);
            this.Controls.Add(statusBarPanel);

            // 注册语言切换事件，动态更新所有输入框前缀
            LanguageManager.Instance.LanguageChanged += UpdateLocalizedPrefixes;
            // 注册语言切换事件，动态更新所有输入框占位文字
            LanguageManager.Instance.LanguageChanged += UpdateLocalizedPlaceholders;
            // 注册语言切换事件，动态更新窗口标题（品牌名 + 本地化软件名）
            LanguageManager.Instance.LanguageChanged += () => this.Text = BuildWindowTitle();
        }

        /// <summary>
        /// 窗口标题：品牌名 + 本地化软件名（如「GDPackEditor 恐怖黎明包编辑器」），随语言切换变化。
        /// </summary>
        private static string BuildWindowTitle()
            => LanguageManager.Instance.GetText("MainForm.Title", "GDPackEditor")
               + " " + LanguageManager.Instance.GetText("MainForm.TitleSub", "恐怖黎明包编辑器");

        /// <summary>
        /// 语言切换时更新所有输入框的前缀文本
        /// </summary>
        private void UpdateLocalizedPrefixes()
        {
            foreach (var item in _localizedPrefixes)
                item.Control.PrefixText = LanguageManager.Instance.GetText(item.Key, item.Fallback);
        }

        /// <summary>
        /// 语言切换时更新所有输入框的占位文字
        /// </summary>
        private void UpdateLocalizedPlaceholders()
        {
            foreach (var item in _localizedPlaceholders)
                item.Control.PlaceholderText = LanguageManager.Instance.GetText(item.Key, item.Fallback);
        }

        /// <summary>
        /// 为 Panel 添加分组标题 Label（替代 Panel.Text 的 GroupBox 效果）
        /// </summary>
        private void AddGroupTitle(AntdUI.Panel panel, string text, string name)
        {
            Label label = new Label();
            label.Text = text;
            label.Name = name;
            label.Font = FontHelper.UiFont(9, FontStyle.Bold);
            label.Dock = DockStyle.Top;
            label.Height = 22;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Padding = new Padding(3, 0, 0, 0);
            panel.Controls.Add(label);
        }

        private void EnableDragDrop(TextBox textBox)
        {
            textBox.AllowDrop = true;
            textBox.HandDragFolder = false;
            textBox.DragEnter += (s, e) => {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
                else
                    e.Effect = DragDropEffects.None;
            };
            textBox.DragDrop += (s, e) => {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files.Length > 0)
                    {
                        textBox.Text = files[0];
                    }
                }
            };
        }




        #region ARZ/ARC 编辑器选项卡

        private AntdUI.TabPage CreateArzArcEditorTab()
        {
            AntdUI.TabPage tabPage = new AntdUI.TabPage() { Text = "ARZ/ARC 编辑器" };
            tabPage.Name = "EditorTab.Title";


            AntdUI.Panel mainPanel = new AntdUI.Panel() { Back = Color.Transparent };
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.Padding = new Padding(20);

            // 拖入区域（顶部提示条 + 历史记录表格 + 底部清空，整体仍支持拖拽）
            var dropPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderWidth = 1F,
                AllowDrop = true,
                HandDragFolder = false,
                Back = ThemeManager.BgContainer,
                BackColor = ThemeManager.BgContainer
            };

            var historyTable = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = true,
                Bordered = true,
                MultipleRows = false,
                EditMode = AntdUI.TEditMode.None,
                LostFocusClearSelection = false,
                Font = FontHelper.UiFont(9),
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill
            };

            var bottomBar = new AntdUI.Panel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                Back = Color.Transparent,
                BackColor = Color.Transparent
            };
            var clearAllBtn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Editor.ClearAll", "清空"),
                Type = AntdUI.TTypeMini.Error,
                AutoSize = true,
                
                Font = FontHelper.UiFont(9),
                Dock = DockStyle.Right,
                Margin = new Padding(0, 2, 4, 0)
            };
            bottomBar.Controls.Add(clearAllBtn);

            var dropLabel = new Label
            {
                Name = "Editor.DropFileHint",
                Text = LanguageManager.Instance.GetText("Editor.DropFileHint", "将 ARZ/ARC 文件拖入此处"),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top,
                Height = 56,
                Font = FontHelper.UiFont(12),
                ForeColor = ThemeManager.TextTertiary,
                BackColor = ThemeManager.BgContainer
            };

            void RefreshHistoryTable()
            {
                historyTable.Columns.Clear();
                historyTable.Columns.Add(new AntdUI.Column("FileName",
                    LanguageManager.Instance.GetText("Common.File", "文件")) { Width = "50%", LineBreak = true });
                historyTable.Columns.Add(new AntdUI.Column("OpenTime",
                    LanguageManager.Instance.GetText("Editor.OpenTime", "打开时间")) { Width = "30%", Align = AntdUI.ColumnAlign.Right });
                historyTable.Columns.Add(new AntdUI.Column("CellLinks", "") { Width = "130" });
                historyTable.EmptyText = LanguageManager.Instance.GetText("Editor.NoRecentFiles", "暂无历史记录");

                var files = SettingsManager.GetRecentFiles();
                var dataSource = new List<object>();
                foreach (var file in files)
                {
                    bool exists = PathCheckHelper.Exists(file.FilePath);
                    dataSource.Add(new
                    {
                        FileName = file.FilePath,
                        FilePath = file.FilePath,
                        Exists = exists,
                        OpenTime = file.OpenTime.ToString("yyyy-MM-dd HH:mm"),
                        CellLinks = new AntdUI.CellLink[]
                        {
                            new AntdUI.CellButton("open", LanguageManager.Instance.GetText("Common.Open", "打开"), AntdUI.TTypeMini.Primary),
                            new AntdUI.CellButton("clear", LanguageManager.Instance.GetText("Common.Clear", "清除"))
                        }
                    });
                }
                historyTable.DataSource = dataSource;
            }

            historyTable.CellButtonClick += (s, e) =>
            {
                var btn = e.Btn;
                var data = e.Record as dynamic;
                string filePath = data.FilePath;
                if (btn.Id == "open")
                {
                    if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    {
                        MessageBox.Show(
                            LanguageManager.Instance.GetText("Common.MessageBoxFileNotFound", "文件不存在，无法打开编辑器"),
                            LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "提示"),
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    var editor = new ArzArcEditorForm(filePath);
                    editor.FormClosed += (s2, args) => editor.Dispose();
                    editor.Show();
                    RefreshHistoryTable();
                }
                else if (btn.Id == "clear")
                {
                    SettingsManager.RemoveRecentFile(filePath);
                    RefreshHistoryTable();
                }
            };

            historyTable.CellPaintBegin += (s, e) =>
            {
                if (e.RowType == AntdUI.RowType.Column || e.RowType == AntdUI.RowType.Summary || e.Record == null) return;
                if (e.Column == null || e.Column.Key != "FileName") return;
                bool missing = !(bool)((dynamic)e.Record).Exists;
                PathCheckHelper.ApplyMissingCellStyle(e, "FileName", missing, historyTable.Font);
            };

            clearAllBtn.Click += (s, ev) =>
            {
                SettingsManager.ClearRecentFiles();
                RefreshHistoryTable();
            };

            dropPanel.Controls.Add(historyTable);
            dropPanel.Controls.Add(bottomBar);
            dropPanel.Controls.Add(dropLabel);

            LanguageManager.Instance.LanguageChanged += () =>
            {
                clearAllBtn.Text = LanguageManager.Instance.GetText("Editor.ClearAll", "清空");
                RefreshHistoryTable();
            };

            tabPage.VisibleChanged += (s, e) =>
            {
                if (tabPage.Visible) RefreshHistoryTable();
            };

            dropPanel.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
                else
                    e.Effect = DragDropEffects.None;
            };
            dropPanel.DragDrop += (s, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length == 0) return;
                string file = files[0];
                string ext = Path.GetExtension(file)?.ToLower();
                if (ext == ".arz" || ext == ".arc")
                {
                    var editor = new ArzArcEditorForm(file);
                    editor.FormClosed += (s, args) => editor.Dispose();
                    editor.Show();
                    RefreshHistoryTable();
                }
                else
                {
                    MessageBox.Show(
                        LanguageManager.Instance.GetText("Common.MessageBoxUnsupportedFormat", "不支持的文件格式，请选择 ARZ 或 ARC 文件"),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    var editor = new ArzArcEditorForm(null);
                    editor.FormClosed += (s, args) => editor.Dispose();
                    editor.Show();
                }
            };

            mainPanel.Controls.Add(dropPanel);

            // 打开编辑器按钮
            var openEditorButton = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("Editor.OpenEditorButton", "打开 ARZ/ARC 编辑器"),
                Name = "Editor.OpenEditorButton",
                AutoSize = true,
                Font = FontHelper.UiFont(9, FontStyle.Bold),
                Type = AntdUI.TTypeMini.Primary
            };
            openEditorButton.Click += (s, e) =>
            {
                var editor = new ArzArcEditorForm(null);
                editor.FormClosed += (s, args) => editor.Dispose();
                editor.Show();
            };

            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 70,
                Back = Color.Transparent
            };
            buttonPanel.Controls.Add(openEditorButton);
            buttonPanel.Layout += (s, e) => {
                if (buttonPanel.Controls.Count == 0) return;
                int totalWidth = 0;
                int gap = 10;
                foreach (Control c in buttonPanel.Controls) totalWidth += c.Width;
                totalWidth += gap * (buttonPanel.Controls.Count - 1);
                int x = (buttonPanel.Width - totalWidth) / 2;
                int y = (buttonPanel.Height - buttonPanel.Controls[0].Height) / 2;
                foreach (Control c in buttonPanel.Controls)
                {
                    c.Location = new Point(x, y);
                    x += c.Width + gap;
                }
            };
            mainPanel.Controls.Add(buttonPanel);

            tabPage.Controls.Add(mainPanel);

            // AntdUI.Tabs 切页时只改 Page.Showed、不变化 Visible，故用 SelectedIndexChanged 保证历史实时刷新
            _mainTabs.SelectedIndexChanged += (s, e) =>
            {
                if (e.Value >= 0 && e.Value < _mainTabs.Pages.Count && _mainTabs.Pages[e.Value] == tabPage)
                    RefreshHistoryTable();
            };

            return tabPage;
        }

        #endregion

        #region 解包ARC选项卡
        
        private AntdUI.TabPage CreateUnpackArcTab()
        {
            AntdUI.TabPage tabPage = new AntdUI.TabPage() { Text = "解包ARC" };
            tabPage.Name = "UnpackArcTab.Title";

            AntdUI.Panel mainPanel = new AntdUI.Panel() { Back = Color.Transparent };
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.Padding = new Padding(10);

            // ═══ 上半部内容容器：执行时作为 Spin 遮罩目标（罩住输入/按钮行，日志区保持可见可滚动）═══
            AntdUI.Panel contentPanel = new AntdUI.Panel() { Back = Color.Transparent, Dock = DockStyle.Top };

            // 日志输出（公共组件 LogView：Scintilla + 过滤/清空/导出工具条 + 警告黄/错误红行级着色）
            LogView logView = new LogView { Name = "logView", Dock = DockStyle.Fill };
            
            // ═══ 输入文件选择（Fill先加→Right后加）═══
            AntdUI.Panel inputRow = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Top, Height = 42 };

            TextBox arcFilePathTextBox = new TextBox { Font = FontHelper.UiFont(9), Name = "arcFilePath", Dock = DockStyle.Fill, Margin = new Padding(0), PrefixText = LanguageManager.Instance.GetText("UnpackArcTab.InputLabel", "输入文件："), AllowClear = true };
            _localizedPrefixes.Add((arcFilePathTextBox, "UnpackArcTab.InputLabel", "输入文件："));
            arcFilePathTextBox.PlaceholderText = LanguageManager.Instance.GetText("UnpackArcTab.InputArcPlaceholder", "支持拖入，请选择要解包的ARC文件");
            _localizedPlaceholders.Add((arcFilePathTextBox, "UnpackArcTab.InputArcPlaceholder", "支持拖入，请选择要解包的ARC文件"));
            EnableDragDrop(arcFilePathTextBox);
            AntdUI.Button browseArcButton = new AntdUI.Button { AutoSize = true, Text = "浏览", Name = "Common.BrowseButton", Font = FontHelper.UiFont(9), Dock = DockStyle.Right, Margin = new Padding(0), Type = AntdUI.TTypeMini.Primary };
            browseArcButton.Click += (s, e) => BrowseFile(arcFilePathTextBox, "选择ARC文件", "ARC文件|*.arc");
            AntdUI.Button viewArcButton = new AntdUI.Button { AutoSize = true, Text = "编辑", Name = "Common.ViewButton", Font = FontHelper.UiFont(9), Dock = DockStyle.Right, Margin = new Padding(0) };
            viewArcButton.Click += (s, e) => { var editor = new ArzArcEditorForm(string.IsNullOrEmpty(arcFilePathTextBox.Text?.Trim()) ? null : arcFilePathTextBox.Text.Trim()); editor.FormClosed += (s2, a2) => editor.Dispose(); editor.Show(); };
            inputRow.Controls.Add(arcFilePathTextBox);
            inputRow.Controls.Add(browseArcButton);
            inputRow.Controls.Add(viewArcButton);

            // ═══ 输出目录显示（Fill先加→Right后加）═══
            AntdUI.Panel outputRow = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Top, Height = 42 };

            TextBox outputDirTextBox = new TextBox { Font = FontHelper.UiFont(9), Name = "outputDir", Dock = DockStyle.Fill, Margin = new Padding(0), PrefixText = LanguageManager.Instance.GetText("UnpackArcTab.OutputLabel", "输出目录："), AllowClear = true };
            _localizedPrefixes.Add((outputDirTextBox, "UnpackArcTab.OutputLabel", "输出目录："));
            outputDirTextBox.PlaceholderText = LanguageManager.Instance.GetText("UnpackArcTab.OutputPlaceholder", "默认自动生成，可自行修改");
            _localizedPlaceholders.Add((outputDirTextBox, "UnpackArcTab.OutputPlaceholder", "默认自动生成，可自行修改"));
            outputRow.Controls.Add(outputDirTextBox);
            
            // 自动更新输出目录
            arcFilePathTextBox.TextChanged += (s, e) => {
                if (!string.IsNullOrEmpty(arcFilePathTextBox.Text))
                {
                    string dir = Path.Combine(
                        Path.GetDirectoryName(arcFilePathTextBox.Text),
                        Path.GetFileNameWithoutExtension(arcFilePathTextBox.Text)
                    );
                    outputDirTextBox.Text = dir;
                }
                else
                {
                    outputDirTextBox.Text = "";
                }
            };
            
            // 执行按钮区域
            AntdUI.Panel buttonPanel = new AntdUI.Panel() { Back = Color.Transparent };
            buttonPanel.Dock = DockStyle.Top;
            buttonPanel.Height = 50;

            AntdUI.Button unpackArcButton = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("UnpackArcTab.ExecuteButton", "解包ARC"),
                Name = "UnpackArcTab.ExecuteButton",
                AutoSize = true,
                Font = FontHelper.UiFont(9, FontStyle.Bold),
                Type = AntdUI.TTypeMini.Primary
            };
            unpackArcButton.Click += async (s, e) => await ExecuteUnpackArc(arcFilePathTextBox.Text, outputDirTextBox.Text, contentPanel, logView, unpackArcButton);

            buttonPanel.Controls.Add(unpackArcButton);
            buttonPanel.Layout += (s, e) => {
                int visibleCount = 0;
                int totalWidth = 0;
                int gap = 10;
                foreach (Control c in buttonPanel.Controls)
                {
                    if (!c.Visible) continue;
                    visibleCount++;
                    totalWidth += c.Width;
                }
                if (visibleCount == 0) return;
                totalWidth += gap * (visibleCount - 1);
                int x = (buttonPanel.Width - totalWidth) / 2;
                int y = (buttonPanel.Height - buttonPanel.Controls[0].Height) / 2;
                foreach (Control c in buttonPanel.Controls)
                {
                    if (!c.Visible) continue;
                    c.Location = new Point(x, y);
                    x += c.Width + gap;
                }
            };

            // 上半部组装（WinForms 反向处理 Dock：最后加入的最先处理，占据最上）
            contentPanel.Controls.Add(buttonPanel);   // Top - processed 3rd（最下）
            contentPanel.Controls.Add(outputRow);     // Top - processed 2nd
            contentPanel.Controls.Add(inputRow);      // Top - processed 1st（最上）

            // 高度贴合内部子行（构建与 DPI 缩放后自动校正，行不被截断）
            AttachContentAutoHeight(contentPanel);

            mainPanel.Controls.Add(logView);         // Fill - added first, processed last
            mainPanel.Controls.Add(contentPanel);    // Top - 上半部（含输入/输出/按钮行）

            tabPage.Controls.Add(mainPanel);

            return tabPage;
        }
        
        #endregion

        #region 打包ARC选项卡
        
        private AntdUI.TabPage CreatePackArcTab()
        {
            AntdUI.TabPage tabPage = new AntdUI.TabPage() { Text = LanguageManager.Instance.GetText("PackArcTab.Title", "打包ARC") };
            tabPage.Name = "PackArcTab.Title";

            AntdUI.Panel mainPanel = new AntdUI.Panel() { Back = Color.Transparent };
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.Padding = new Padding(10);

            // ═══ 上半部内容容器：执行时作为 Spin 遮罩目标（罩住输入/选项/按钮行，日志区保持可见可滚动）═══
            AntdUI.Panel contentPanel = new AntdUI.Panel() { Back = Color.Transparent, Dock = DockStyle.Top };

            // 日志输出（公共组件 LogView：Scintilla + 过滤/清空/导出工具条 + 警告黄/错误红行级着色）
            LogView logView = new LogView { Name = "logView", Dock = DockStyle.Fill };

            // ═══ 输入目录行（Fill先加→Right后加）═══
            AntdUI.Panel inputRow = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Top, Height = 42 };

            TextBox inputDirTextBox = new TextBox { Font = FontHelper.UiFont(9), Name = "inputDir", Dock = DockStyle.Fill, Margin = new Padding(0), PrefixText = LanguageManager.Instance.GetText("PackArcTab.InputDirLabel", "输入目录："), AllowClear = true };
            _localizedPrefixes.Add((inputDirTextBox, "PackArcTab.InputDirLabel", "输入目录："));
            inputDirTextBox.PlaceholderText = LanguageManager.Instance.GetText("PackArcTab.InputDirPlaceholder", "支持拖入，请选择要打包为ARC的文件夹");
            _localizedPlaceholders.Add((inputDirTextBox, "PackArcTab.InputDirPlaceholder", "支持拖入，请选择要打包为ARC的文件夹"));
            EnableDragDrop(inputDirTextBox);
            AntdUI.Button browseDirButton = new AntdUI.Button { AutoSize = true, Text = "浏览", Name = "Common.BrowseButton", Font = FontHelper.UiFont(9), Dock = DockStyle.Right, Margin = new Padding(0), Type = AntdUI.TTypeMini.Primary };
            browseDirButton.Click += (s, e) => BrowseFolder(inputDirTextBox, LanguageManager.Instance.GetText("Common.BrowseFolderDescription", "选择文件夹"));
            inputRow.Controls.Add(inputDirTextBox);
            inputRow.Controls.Add(browseDirButton);

            // ═══ 输出文件行（Fill先加→Right后加）═══
            AntdUI.Panel outputRow = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Top, Height = 42 };

            TextBox outputFileTextBox = new TextBox { Font = FontHelper.UiFont(9), Name = "outputFile", Dock = DockStyle.Fill, Margin = new Padding(0), PrefixText = LanguageManager.Instance.GetText("PackArcTab.OutputFileLabel", "输出文件："), AllowClear = true };
            _localizedPrefixes.Add((outputFileTextBox, "PackArcTab.OutputFileLabel", "输出文件："));
            outputFileTextBox.PlaceholderText = LanguageManager.Instance.GetText("PackArcTab.OutputPlaceholder", "默认自动生成，可自行修改");
            _localizedPlaceholders.Add((outputFileTextBox, "PackArcTab.OutputPlaceholder", "默认自动生成，可自行修改"));
            AntdUI.Button viewArcButton = new AntdUI.Button { AutoSize = true, Text = "编辑", Name = "Common.ViewButton", Font = FontHelper.UiFont(9), Dock = DockStyle.Right, Margin = new Padding(0) };
            viewArcButton.Click += (s, e) => {
                string path = outputFileTextBox.Text?.Trim();
                var editor = new ArzArcEditorForm(string.IsNullOrEmpty(path) ? null : path);
                editor.FormClosed += (s2, args) => editor.Dispose();
                editor.Show();
            };
            outputRow.Controls.Add(outputFileTextBox);
            outputRow.Controls.Add(viewArcButton);
            
            inputDirTextBox.TextChanged += (s, e) => {
                string input = inputDirTextBox.Text;
                if (!string.IsNullOrEmpty(input))
                {
                    string dir = input;
                    try { dir = Path.GetFullPath(input).TrimEnd('\\', '/'); }
                    catch { dir = input.TrimEnd('\\', '/'); }
                    string name = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name) && !dir.EndsWith(":"))
                    {
                        string parent = Path.GetDirectoryName(dir) ?? dir;
                        outputFileTextBox.Text = Path.Combine(parent, name + ".arc");
                    }
                    else
                    {
                        outputFileTextBox.Text = "";
                    }
                }
                else
                {
                    outputFileTextBox.Text = "";
                }
            };
            
            // 增量模式选择
            CheckBox concurrentCheck = new CheckBox();
            concurrentCheck.Text = LanguageManager.Instance.GetText("PackArcTab.ConcurrentCheck", "增量模式") + "：";
            concurrentCheck.Name = "PackArcTab.ConcurrentCheck";
            concurrentCheck.AutoSize = true;
            concurrentCheck.Margin = new Padding(0);
            concurrentCheck.Checked = SettingsManager.PackArcIncremental;
            concurrentCheck.Font = FontHelper.UiFont(9);

            RadioButton incrementalUpdateOnly = new RadioButton();
            incrementalUpdateOnly.Text = LanguageManager.Instance.GetText("PackArcTab.IncrementalUpdate", "增/改");
            incrementalUpdateOnly.Name = "PackArcTab.IncrementalUpdate";
            incrementalUpdateOnly.AutoSize = true;
            incrementalUpdateOnly.Margin = new Padding(15, 0, 0, 0);
            incrementalUpdateOnly.Checked = !SettingsManager.PackArcIncrementalFull;
            incrementalUpdateOnly.Font = FontHelper.UiFont(9);

            RadioButton incrementalFull = new RadioButton();
            incrementalFull.Text = LanguageManager.Instance.GetText("PackArcTab.IncrementalFull", "增/改/删");
            incrementalFull.Name = "PackArcTab.IncrementalFull";
            incrementalFull.AutoSize = true;
            incrementalFull.Margin = new Padding(15, 0, 0, 0);
            incrementalFull.Checked = SettingsManager.PackArcIncrementalFull;
            incrementalFull.Font = FontHelper.UiFont(9);

            concurrentCheck.CheckedChanged += (s, e) =>
            {
                incrementalUpdateOnly.Enabled = concurrentCheck.Checked;
                incrementalFull.Enabled = concurrentCheck.Checked;
                SettingsManager.PackArcIncremental = concurrentCheck.Checked;
                SettingsManager.Save();
            };
            incrementalUpdateOnly.CheckedChanged += (s, e) =>
            {
                if (incrementalUpdateOnly.Checked)
                {
                    SettingsManager.PackArcIncrementalFull = false;
                    SettingsManager.Save();
                }
            };
            incrementalFull.CheckedChanged += (s, e) =>
            {
                if (incrementalFull.Checked)
                {
                    SettingsManager.PackArcIncrementalFull = true;
                    SettingsManager.Save();
                }
            };
            incrementalUpdateOnly.Enabled = concurrentCheck.Checked;
            incrementalFull.Enabled = concurrentCheck.Checked;

            // 增量模式区域（单行：checkbox + ? + radio1 + radio2）
            AntdUI.Panel incrementalGroup = new AntdUI.Panel() { Back = Color.Transparent };
            incrementalGroup.Dock = DockStyle.Top;
            incrementalGroup.Height = 42;

            AntdUI.FlowPanel incrementalPanel = new AntdUI.FlowPanel() { Back = Color.Transparent };
            incrementalPanel.Dock = DockStyle.Fill;
            incrementalPanel.Controls.Add(incrementalFull);
            incrementalPanel.Controls.Add(incrementalUpdateOnly);
            incrementalPanel.Controls.Add(concurrentCheck);
            incrementalGroup.Controls.Add(incrementalPanel);

            // 执行按钮区域
            AntdUI.Panel buttonPanel = new AntdUI.Panel() { Back = Color.Transparent };
            buttonPanel.Dock = DockStyle.Top;
            buttonPanel.Height = 50;

            AntdUI.Button packArcButton = new AntdUI.Button();
            packArcButton.Text = LanguageManager.Instance.GetText("PackArcTab.ExecuteButton", "打包ARC");
            packArcButton.Name = "PackArcTab.ExecuteButton";
            packArcButton.AutoSize = true;
            packArcButton.Font = FontHelper.UiFont(9, FontStyle.Bold);
            packArcButton.Type = AntdUI.TTypeMini.Primary;

            AntdUI.Button compactArcButton = new AntdUI.Button();
            compactArcButton.Text = LanguageManager.Instance.GetText("PackArcTab.CompactButton", "固实");
            compactArcButton.Name = "PackArcTab.CompactButton";
            compactArcButton.AutoSize = true;
            compactArcButton.Font = FontHelper.UiFont(9, FontStyle.Bold);
            //compactArcButton.Type = AntdUI.TTypeMini.Primary;

            packArcButton.Click += async (s, e) => await ExecutePackArc(inputDirTextBox.Text, outputFileTextBox.Text, contentPanel, logView, packArcButton, compactArcButton, concurrentCheck.Checked, incrementalFull.Checked);
            compactArcButton.Click += async (s, e) => await ExecuteCompactArc(outputFileTextBox.Text, contentPanel, logView, compactArcButton);

            buttonPanel.Controls.Add(packArcButton);
            buttonPanel.Controls.Add(compactArcButton);
            buttonPanel.Layout += (s, e) => {
                int visibleCount = 0;
                int totalWidth = 0;
                int gap = 10;
                foreach (Control c in buttonPanel.Controls)
                {
                    if (!c.Visible) continue;
                    visibleCount++;
                    totalWidth += c.Width;
                }
                if (visibleCount == 0) return;
                totalWidth += gap * (visibleCount - 1);
                int x = (buttonPanel.Width - totalWidth) / 2;
                int y = (buttonPanel.Height - buttonPanel.Controls[0].Height) / 2;
                foreach (Control c in buttonPanel.Controls)
                {
                    if (!c.Visible) continue;
                    c.Location = new Point(x, y);
                    x += c.Width + gap;
                }
            };
            
            // 上半部组装（WinForms 反向处理 Dock：最后加入的最先处理，占据最上）
            contentPanel.Controls.Add(buttonPanel);      // Top - processed 4th（最下）
            contentPanel.Controls.Add(incrementalGroup); // Top - processed 3rd
            contentPanel.Controls.Add(outputRow);        // Top - processed 2nd
            contentPanel.Controls.Add(inputRow);         // Top - processed 1st（最上）

            // 高度贴合内部子行（构建与 DPI 缩放后自动校正，行不被截断）
            AttachContentAutoHeight(contentPanel);

            mainPanel.Controls.Add(logView);         // Fill - added first, processed last
            mainPanel.Controls.Add(contentPanel);    // Top - 上半部（含输入/输出/选项/按钮行）

            tabPage.Controls.Add(mainPanel);

            return tabPage;
        }
        
        #endregion

        #region 解包ARZ选项卡
        
        private AntdUI.TabPage CreateUnpackArzTab()
        {
            AntdUI.TabPage tabPage = new AntdUI.TabPage() { Text = "解包ARZ" };
            tabPage.Name = "UnpackArzTab.Title";

            AntdUI.Panel mainPanel = new AntdUI.Panel() { Back = Color.Transparent };
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.Padding = new Padding(10);

            // ═══ 上半部内容容器：执行时作为 Spin 遮罩目标（罩住输入/选项/按钮行，日志区保持可见可滚动）═══
            AntdUI.Panel contentPanel = new AntdUI.Panel() { Back = Color.Transparent, Dock = DockStyle.Top };

            // 日志输出（公共组件 LogView：Scintilla + 过滤/清空/导出工具条 + 警告黄/错误红行级着色）
            LogView logView = new LogView { Name = "logView", Dock = DockStyle.Fill };

            // ═══ 输入文件行（Fill先加→Right后加）═══
            AntdUI.Panel inputRow = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Top, Height = 42 };

            TextBox arzFilePathTextBox = new TextBox { Font = FontHelper.UiFont(9), Name = "arzFilePath", Dock = DockStyle.Fill, Margin = new Padding(0), PrefixText = LanguageManager.Instance.GetText("UnpackArzTab.InputLabel", "输入文件："), AllowClear = true };
            _localizedPrefixes.Add((arzFilePathTextBox, "UnpackArzTab.InputLabel", "输入文件："));
            arzFilePathTextBox.PlaceholderText = LanguageManager.Instance.GetText("UnpackArzTab.InputArzPlaceholder", "支持拖入，请选择要解包的ARZ文件");
            _localizedPlaceholders.Add((arzFilePathTextBox, "UnpackArzTab.InputArzPlaceholder", "支持拖入，请选择要解包的ARZ文件"));
            EnableDragDrop(arzFilePathTextBox);
            AntdUI.Button browseArzButton = new AntdUI.Button { AutoSize = true, Text = "浏览", Name = "Common.BrowseButton", Font = FontHelper.UiFont(9), Dock = DockStyle.Right, Margin = new Padding(0), Type = AntdUI.TTypeMini.Primary };
            browseArzButton.Click += (s, e) => BrowseFile(arzFilePathTextBox, "选择ARZ文件", "ARZ文件|*.arz");
            AntdUI.Button viewArzButton = new AntdUI.Button { AutoSize = true, Text = "编辑", Name = "Common.ViewButton", Font = FontHelper.UiFont(9), Dock = DockStyle.Right, Margin = new Padding(0) };
            viewArzButton.Click += (s, e) => {
                string path = arzFilePathTextBox.Text?.Trim();
                var editor = new ArzArcEditorForm(string.IsNullOrEmpty(path) ? null : path);
                editor.FormClosed += (s2, args) => editor.Dispose();
                editor.Show();
            };
            // 检测：对所选 ARZ 做内容检查（等价 CLI --check 的内容检查），结果输出到本页日志区。
            // ⚠️ AntdUI/反向 Dock：**后加入的越靠左**。加入序 [输入框→浏览→检测→编辑] 的视觉结果是
            //    [输入框][浏览][检测][编辑]（检测落在「浏览」右边）
            AntdUI.Button checkArzButton = new AntdUI.Button { AutoSize = true, Text = LanguageManager.Instance.GetText("Common.CheckButton", "检测"), Name = "Common.CheckButton", Font = FontHelper.UiFont(9), Dock = DockStyle.Right, Margin = new Padding(0) };
            checkArzButton.Click += async (s, e) => await ExecuteCheckArz(arzFilePathTextBox.Text, contentPanel, logView, checkArzButton);
            inputRow.Controls.Add(arzFilePathTextBox);
            inputRow.Controls.Add(browseArzButton);
            inputRow.Controls.Add(checkArzButton);
            inputRow.Controls.Add(viewArzButton);

            // ═══ 输出目录行（Fill先加→Right后加）═══
            AntdUI.Panel outputRow = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Top, Height = 42 };

            TextBox outputDirTextBox = new TextBox { Font = FontHelper.UiFont(9), Name = "outputDir", Dock = DockStyle.Fill, Margin = new Padding(0), PrefixText = LanguageManager.Instance.GetText("UnpackArzTab.OutputDirLabel", "输出目录："), AllowClear = true };
            _localizedPrefixes.Add((outputDirTextBox, "UnpackArzTab.OutputDirLabel", "输出目录："));
            outputDirTextBox.PlaceholderText = LanguageManager.Instance.GetText("UnpackArzTab.OutputPlaceholder", "默认自动生成，可自行修改");
            _localizedPlaceholders.Add((outputDirTextBox, "UnpackArzTab.OutputPlaceholder", "默认自动生成，可自行修改"));
            outputRow.Controls.Add(outputDirTextBox);
            
            arzFilePathTextBox.TextChanged += (s, e) => {
                if (!string.IsNullOrEmpty(arzFilePathTextBox.Text))
                {
                    string dir = Path.GetDirectoryName(arzFilePathTextBox.Text);
                    outputDirTextBox.Text = dir;
                }
                else
                {
                    outputDirTextBox.Text = "";
                }
            };

            // ═══ 解包模式行（单线程/多线程单选）：机械硬盘顺序写更快，SSD可开多线程充分利用并发 ═══
            AntdUI.Panel threadRow = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Top, Height = 34 };

            Label threadModeLabel = new Label();
            threadModeLabel.Text = LanguageManager.Instance.GetText("UnpackArzTab.ThreadModeLabel", "解包模式：");
            threadModeLabel.Name = "UnpackArzTab.ThreadModeLabel";
            threadModeLabel.AutoSize = true;
            threadModeLabel.Dock = DockStyle.Left;
            threadModeLabel.Padding = new Padding(0, 8, 0, 0);
            threadModeLabel.Font = FontHelper.UiFont(9);

            AntdUI.Radio radioSingle = new AntdUI.Radio { Text = LanguageManager.Instance.GetText("UnpackArzTab.ThreadModeSingle", "单线程（HDD推荐）"), Name = "UnpackArzTab.ThreadModeSingle", Checked = SettingsManager.UnpackSingleThread, AutoSize = true, Dock = DockStyle.Left, Margin = new Padding(4, 4, 0, 0), Font = FontHelper.UiFont(9) };

            AntdUI.Radio radioMulti = new AntdUI.Radio { Text = LanguageManager.Instance.GetText("UnpackArzTab.ThreadModeMulti", "多线程（SSD推荐）"), Name = "UnpackArzTab.ThreadModeMulti", Checked = !SettingsManager.UnpackSingleThread, AutoSize = true, Dock = DockStyle.Left, Margin = new Padding(12, 4, 0, 0), Font = FontHelper.UiFont(9) };

            radioSingle.CheckedChanged += (s, e) =>
            {
                if (radioSingle.Checked)
                {
                    SettingsManager.UnpackSingleThread = true;
                    SettingsManager.Save();
                }
            };

            radioMulti.CheckedChanged += (s, e) =>
            {
                if (radioMulti.Checked)
                {
                    SettingsManager.UnpackSingleThread = false;
                    SettingsManager.Save();
                }
            };

            LanguageManager.Instance.LanguageChanged += () =>
            {
                threadModeLabel.Text = LanguageManager.Instance.GetText("UnpackArzTab.ThreadModeLabel", "解包模式：");
                radioSingle.Text = LanguageManager.Instance.GetText("UnpackArzTab.ThreadModeSingle", "单线程（HDD推荐）");
                radioMulti.Text = LanguageManager.Instance.GetText("UnpackArzTab.ThreadModeMulti", "多线程（SSD推荐）");
            };

            // Add顺序（Dock=Left 逆序布局）：label最左 → 多线程 → 单线程（与打包ARZ一致，单线程在后）
            threadRow.Controls.Add(radioSingle);
            threadRow.Controls.Add(radioMulti);
            threadRow.Controls.Add(threadModeLabel);
            
            // 按钮居中区域
            AntdUI.Panel buttonPanel = new AntdUI.Panel() { Back = Color.Transparent };
            buttonPanel.Dock = DockStyle.Top;
            buttonPanel.Height = 50;

            AntdUI.Button unpackArzButton = new AntdUI.Button();
            unpackArzButton.Text = LanguageManager.Instance.GetText("UnpackArzTab.ExecuteButton", "解包ARZ");
            unpackArzButton.Name = "UnpackArzTab.ExecuteButton";
            unpackArzButton.AutoSize = true;
            unpackArzButton.Font = FontHelper.UiFont(9, FontStyle.Bold);
            unpackArzButton.Type = AntdUI.TTypeMini.Primary;
            unpackArzButton.Click += async (s, e) => await ExecuteUnpackArz(arzFilePathTextBox.Text, outputDirTextBox.Text, contentPanel, logView, unpackArzButton);

            buttonPanel.Controls.Add(unpackArzButton);
            buttonPanel.Layout += (s, e) => {
                int visibleCount = 0;
                int totalWidth = 0;
                int gap = 10;
                foreach (Control c in buttonPanel.Controls)
                {
                    if (!c.Visible) continue;
                    visibleCount++;
                    totalWidth += c.Width;
                }
                if (visibleCount == 0) return;
                totalWidth += gap * (visibleCount - 1);
                int x = (buttonPanel.Width - totalWidth) / 2;
                int y = (buttonPanel.Height - buttonPanel.Controls[0].Height) / 2;
                foreach (Control c in buttonPanel.Controls)
                {
                    if (!c.Visible) continue;
                    c.Location = new Point(x, y);
                    x += c.Width + gap;
                }
            };
            
            // 上半部组装（WinForms 反向处理 Dock：最后加入的最先处理，占据最上）
            contentPanel.Controls.Add(buttonPanel);   // Top - processed 4th（最下）
            contentPanel.Controls.Add(threadRow);     // Top - processed 3rd
            contentPanel.Controls.Add(outputRow);     // Top - processed 2nd
            contentPanel.Controls.Add(inputRow);      // Top - processed 1st（最上）

            // 高度贴合内部子行（构建与 DPI 缩放后自动校正，行不被截断）
            AttachContentAutoHeight(contentPanel);

            mainPanel.Controls.Add(logView);         // Fill - added first, processed last
            mainPanel.Controls.Add(contentPanel);    // Top - 上半部（含输入/输出/模式/按钮行）

            tabPage.Controls.Add(mainPanel);

            return tabPage;
        }
        
        #endregion

        #region 打包ARZ选项卡
        
        private AntdUI.TabPage CreatePackArzTab()
        {
            AntdUI.TabPage tabPage = new AntdUI.TabPage() { Text = LanguageManager.Instance.GetText("PackArzTab.Title", "打包ARZ") };
            tabPage.Name = "PackArzTab.Title";

            // 创建主面板
            AntdUI.Panel outerPanel = new AntdUI.Panel() { Back = Color.Transparent };
            outerPanel.Dock = DockStyle.Fill;
            outerPanel.Padding = new Padding(10);

            // 日志输出（公共组件 LogView：Scintilla + 过滤/清空/导出工具条 + 警告黄/错误红行级着色）
            LogView logView = new LogView { Name = "logView", Dock = DockStyle.Fill };

            // 中间内容面板（左右分栏）
            AntdUI.Panel middlePanel = new AntdUI.Panel() { Back = Color.Transparent };
            middlePanel.Dock = DockStyle.Top;
            middlePanel.Height = 270;
            middlePanel.Padding = new Padding(0, 5, 0, 0);

            // ═══ MOD目录输入框下方：选择要打包的子目录（单行横排，超宽横向滚动）═══
            AntdUI.Panel subDirArea = new AntdUI.Panel() { Back = Color.Transparent, Dock = DockStyle.Top, Height = 74 };
            subDirArea.BackColor = Color.Transparent;

            // 标题行：标题(左) + 全选(右)
            AntdUI.Panel subDirHeaderRow = new AntdUI.Panel() { Back = Color.Transparent, Dock = DockStyle.Top, Height = 26 };
            subDirHeaderRow.BackColor = Color.Transparent;

            Label databaseSubDirsLabel = new Label();
            databaseSubDirsLabel.Text = LanguageManager.Instance.GetText("PackArzTab.DatabaseSubDirsLabel", "选择要打包的子目录:");
            databaseSubDirsLabel.Name = "PackArzTab.DatabaseSubDirsLabel";
            databaseSubDirsLabel.Dock = DockStyle.Left;
            databaseSubDirsLabel.AutoSize = true;
            databaseSubDirsLabel.Padding = new Padding(0, 5, 0, 0);
            databaseSubDirsLabel.Font = FontHelper.UiFont(9);

            CheckBox databaseSubDirsSelectAll = new CheckBox();
            databaseSubDirsSelectAll.Text = LanguageManager.Instance.GetText("Common.SelectAll", "全选");
            databaseSubDirsSelectAll.Name = "Common.SelectAll";
            databaseSubDirsSelectAll.Checked = false;
            databaseSubDirsSelectAll.Dock = DockStyle.Right;
            databaseSubDirsSelectAll.AutoSize = true;
            databaseSubDirsSelectAll.Margin = new Padding(20, 0, 0, 0);
            databaseSubDirsSelectAll.Font = FontHelper.UiFont(9);
            databaseSubDirsSelectAll.CheckedChanged += (s, e) => {
                foreach (Control ctl in databaseSubDirsCheckedListBox.Controls)
                    if (ctl is CheckBox cb) cb.Checked = databaseSubDirsSelectAll.Checked;
            };

            subDirHeaderRow.Controls.Add(databaseSubDirsSelectAll);
            subDirHeaderRow.Controls.Add(databaseSubDirsLabel);

            // 横向单行复选框列表（文件夹多时横向拓展，显示横向滚动条）
            databaseSubDirsCheckedListBox = new AntdUI.StackPanel();
            databaseSubDirsCheckedListBox.Name = "databaseSubDirsList";
            databaseSubDirsCheckedListBox.Dock = DockStyle.Fill;
            databaseSubDirsCheckedListBox.AutoScroll = true;
            databaseSubDirsCheckedListBox.Vertical = false;
            databaseSubDirsCheckedListBox.Gap = 8;
            databaseSubDirsCheckedListBox.Back = Color.Transparent;
            databaseSubDirsCheckedListBox.BackColor = Color.Transparent;
            databaseSubDirsCheckedListBox.AutoContainerBgTransparent = true;
            databaseSubDirsCheckedListBox.Padding = new Padding(4, 0, 4, 0);

            subDirArea.Controls.Add(databaseSubDirsCheckedListBox);
            subDirArea.Controls.Add(subDirHeaderRow);

            // ---------- 左侧面板：主要功能区域 ----------
            AntdUI.Panel leftPanel = new AntdUI.Panel() { Back = Color.Transparent };
            leftPanel.Dock = DockStyle.Fill;
            leftPanel.Padding = new Padding(0, 0, 0, 0);

            // ═══ 输入目录行（Fill先加→Right后加）═══
            AntdUI.Panel inputRow = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Top, Height = 42 };
            inputRow.BackColor = Color.Transparent;

            TextBox inputDirTextBox = new TextBox { Font = FontHelper.UiFont(9), Name = "inputDir", Dock = DockStyle.Fill, Margin = new Padding(0), PrefixText = LanguageManager.Instance.GetText("PackArzTab.InputDirLabel", "输入mod目录："), AllowClear = true };
            _localizedPrefixes.Add((inputDirTextBox, "PackArzTab.InputDirLabel", "输入mod目录："));
            inputDirTextBox.PlaceholderText = LanguageManager.Instance.GetText("PackArzTab.InputDirPlaceholder", "支持拖入，请选择要打包为ARZ的文件夹");
            _localizedPlaceholders.Add((inputDirTextBox, "PackArzTab.InputDirPlaceholder", "支持拖入，请选择要打包为ARZ的文件夹"));
            EnableDragDrop(inputDirTextBox);
            AntdUI.Button browseDirButton = new AntdUI.Button { AutoSize = true, Text = "浏览", Name = "Common.BrowseButton", Font = FontHelper.UiFont(9), Dock = DockStyle.Right, Margin = new Padding(0), Type = AntdUI.TTypeMini.Primary };
            browseDirButton.Click += (s, e) => BrowseFolder(inputDirTextBox, LanguageManager.Instance.GetText("Common.BrowseFolderDescription", "选择文件夹"));
            inputRow.Controls.Add(inputDirTextBox);
            inputRow.Controls.Add(browseDirButton);

            // ═══ 分组框：输入mod目录 + 子目录区（浅蓝框，视觉层级，与按钮主题色一致）═══
            AntdUI.Panel groupPanel = new AntdUI.Panel() { Back = AntdUI.Style.Get(AntdUI.Colour.InfoBg) };
            groupPanel.Dock = DockStyle.Top;
            groupPanel.Height = 156;
            groupPanel.BorderWidth = 1;
            groupPanel.BorderColor = AntdUI.Style.Get(AntdUI.Colour.InfoBorder);
            groupPanel.Radius = 8;
            groupPanel.Padding = new Padding(12, 8, 12, 10);

            // ═══ MOD目录输入框上方说明（灰色小字，多语言键经RefreshUI自动刷新）═══
            Label modDirHintLabel = new Label
            {
                Name = "PackArzTab.InputDirHint",
                Text = LanguageManager.Instance.GetText("PackArzTab.InputDirHint", "拖入MOD根目录，自动读取其database子目录，输出名取文件夹名"),
                Font = FontHelper.UiFont(9),
                ForeColor = AntdUI.Style.Db.Error,
                BackColor = Color.Transparent,
                Dock = DockStyle.Top,
                Height = 20,
                TextAlign = ContentAlignment.MiddleLeft
            };
            groupPanel.Controls.Add(subDirArea);
            groupPanel.Controls.Add(inputRow);
            groupPanel.Controls.Add(modDirHintLabel);

            // ═══ 输出文件行（Fill先加→Right后加）═══
            AntdUI.Panel outputRow = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Top, Height = 42 };

            TextBox outputFileTextBox = new TextBox { Font = FontHelper.UiFont(9), Name = "outputFile", Dock = DockStyle.Fill, Margin = new Padding(0), PrefixText = LanguageManager.Instance.GetText("PackArzTab.OutputFileLabel", "输出文件："), AllowClear = true };
            _localizedPrefixes.Add((outputFileTextBox, "PackArzTab.OutputFileLabel", "输出文件："));
            outputFileTextBox.PlaceholderText = LanguageManager.Instance.GetText("PackArzTab.OutputPlaceholder", "默认自动生成，可自行修改");
            _localizedPlaceholders.Add((outputFileTextBox, "PackArzTab.OutputPlaceholder", "默认自动生成，可自行修改"));
            AntdUI.Button viewArzButton = new AntdUI.Button { AutoSize = true, Text = "编辑", Name = "Common.ViewButton", Font = FontHelper.UiFont(9), Dock = DockStyle.Right, Margin = new Padding(0) };
            viewArzButton.Click += (s, e) => {
                string path = outputFileTextBox.Text?.Trim();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    MessageBox.Show(
                        LanguageManager.Instance.GetText("Common.MessageBoxFileNotFound", "文件不存在，无法打开编辑器"),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "提示"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var editor = new ArzArcEditorForm(path);
                editor.FormClosed += (s2, args) => editor.Dispose();
                editor.Show();
            };
            // 检测：对输出（已生成/待生成）的 ARZ 做内容检查，结果输出到本页日志区。
            // 反向 Dock：在"编辑"之前 Add → 紧贴输出框（视觉为 [输出框][检测][编辑]）
            AntdUI.Button checkArzButton = new AntdUI.Button { AutoSize = true, Text = LanguageManager.Instance.GetText("Common.CheckButton", "检测"), Name = "Common.CheckButton", Font = FontHelper.UiFont(9), Dock = DockStyle.Right, Margin = new Padding(0) };
            checkArzButton.Click += async (s, e) => await ExecuteCheckArz(outputFileTextBox.Text, middlePanel, logView, checkArzButton);
            outputRow.Controls.Add(outputFileTextBox);
            outputRow.Controls.Add(checkArzButton);
            outputRow.Controls.Add(viewArzButton);

            // 自动更新输出文件路径
            inputDirTextBox.TextChanged += (s, e) => {
                string input = inputDirTextBox.Text;
                if (!string.IsNullOrEmpty(input))
                {
                    string dir = input;
                    try { dir = Path.GetFullPath(input).TrimEnd('\\', '/'); }
                    catch { dir = input.TrimEnd('\\', '/'); }
                    string modName = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(modName) && !dir.EndsWith(":"))
                    {
                        string outputFile = Path.Combine(dir, "database", modName + ".arz");
                        outputFileTextBox.Text = outputFile;
                    }
                    else
                    {
                        outputFileTextBox.Text = "";
                    }
                    LoadDatabaseSubDirectories(dir);
                }
                else
                {
                    outputFileTextBox.Text = "";
                    databaseSubDirsCheckedListBox.Controls.Clear();
                }
            };

            void LoadDatabaseSubDirectories(string modDir)
            {
                databaseSubDirsCheckedListBox.Controls.Clear();

                if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir))
                    return;

                string databaseDir = Path.Combine(modDir, "database");
                if (!Directory.Exists(databaseDir))
                    return;

                try
                {
                    string[] subDirs = Directory.GetDirectories(databaseDir, "*", SearchOption.TopDirectoryOnly);

                    foreach (string subDir in subDirs)
                    {
                        string subDirName = Path.GetFileName(subDir);
                        if (subDirName != "output")
                        {
                            bool isChecked = subDirName.Equals("records", StringComparison.OrdinalIgnoreCase);
                            var cb = new CheckBox();
                            cb.Text = subDirName;
                            cb.Checked = isChecked;
                            cb.AutoSize = true;
                            cb.Font = FontHelper.UiFont(9);
                            cb.Margin = new Padding(0, 4, 0, 4);
                            databaseSubDirsCheckedListBox.Controls.Add(cb);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error loading database subdirectories: " + ex.Message);
                }
            };

            // 读取模式选择
            RadioButton singleThreadRadio = new RadioButton();
            singleThreadRadio.Text = LanguageManager.Instance.GetText("PackArzTab.SingleThreadRead", "单线程[HDD推荐]");
            singleThreadRadio.Name = "PackArzTab.SingleThreadRead";
            singleThreadRadio.AutoSize = true;
            singleThreadRadio.Checked = SettingsManager.PackArzSingleThread;
            singleThreadRadio.Font = FontHelper.UiFont(9);

            RadioButton multiThreadRadio = new RadioButton();
            multiThreadRadio.Text = LanguageManager.Instance.GetText("PackArzTab.MultiThreadRead", "多线程[SSD推荐]");
            multiThreadRadio.Name = "PackArzTab.MultiThreadRead";
            multiThreadRadio.AutoSize = true;
            multiThreadRadio.Checked = !SettingsManager.PackArzSingleThread;
            multiThreadRadio.Font = FontHelper.UiFont(9);

            singleThreadRadio.CheckedChanged += (s, e) =>
            {
                if (singleThreadRadio.Checked)
                {
                    SettingsManager.PackArzSingleThread = true;
                    SettingsManager.Save();
                }
            };
            multiThreadRadio.CheckedChanged += (s, e) =>
            {
                if (multiThreadRadio.Checked)
                {
                    SettingsManager.PackArzSingleThread = false;
                    SettingsManager.Save();
                }
            };

            // 模式选择区域：读取模式
            AntdUI.Panel modeSection = new AntdUI.Panel() { Back = Color.Transparent };
            modeSection.Dock = DockStyle.Top;
            modeSection.Height = 42;

            // 第一行：读取模式
            AntdUI.Panel readModeRow = new AntdUI.Panel() { Back = Color.Transparent };
            readModeRow.Height = 42;
            readModeRow.Dock = DockStyle.Top;

            Label readModeLabel = new Label();
            readModeLabel.Text = LanguageManager.Instance.GetText("PackArzTab.ReadModeGroup", "读取模式") + "：";
            readModeLabel.Name = "PackArzTab.ReadModeGroup";
            readModeLabel.Dock = DockStyle.Left;
            readModeLabel.AutoSize = true;
            readModeLabel.Padding = new Padding(0, 12, 0, 0);
            readModeLabel.Font = FontHelper.UiFont(9);
            readModeLabel.Width = 85;

            AntdUI.FlowPanel readModeFlow = new AntdUI.FlowPanel() { Back = Color.Transparent };
            readModeFlow.Dock = DockStyle.Fill;
            readModeFlow.Controls.Add(singleThreadRadio);
            readModeFlow.Controls.Add(multiThreadRadio);

            readModeRow.Controls.Add(readModeFlow);
            readModeRow.Controls.Add(readModeLabel);

            modeSection.Controls.Add(readModeRow);

            // 执行按钮区域
            AntdUI.Panel buttonRowPanel = new AntdUI.Panel() { Back = Color.Transparent };
            buttonRowPanel.Dock = DockStyle.Top;
            buttonRowPanel.Height = 50;

            AntdUI.Button packArzButton = new AntdUI.Button();
            packArzButton.Text = LanguageManager.Instance.GetText("PackArzTab.ExecuteButton", "打包ARZ");
            packArzButton.Name = "PackArzTab.ExecuteButton";
            packArzButton.AutoSize = true;
            packArzButton.Font = FontHelper.UiFont(9, FontStyle.Bold);
            packArzButton.Type = AntdUI.TTypeMini.Primary;

            AntdUI.Button compactArzButton = new AntdUI.Button();
            compactArzButton.Text = LanguageManager.Instance.GetText("PackArzTab.CompactButton", "固实");
            compactArzButton.Name = "PackArzTab.CompactButton";
            compactArzButton.AutoSize = true;
            compactArzButton.Font = FontHelper.UiFont(9, FontStyle.Bold);
            //compactArzButton.Type = AntdUI.TTypeMini.Primary;

            packArzButton.Click += async (s, e) => await ExecutePackArz(
                inputDirTextBox.Text, outputFileTextBox.Text,
                middlePanel, logView, packArzButton, compactArzButton, multiThreadRead: multiThreadRadio.Checked);
            compactArzButton.Click += async (s, e) => await ExecuteCompactArz(outputFileTextBox.Text, middlePanel, logView, compactArzButton);

            buttonRowPanel.Controls.Add(compactArzButton);
            buttonRowPanel.Controls.Add(packArzButton);
            buttonRowPanel.Layout += (s, e) => {
                int visibleCount = 0;
                int totalWidth = 0;
                int gap = 10;
                foreach (Control c in buttonRowPanel.Controls)
                {
                    if (!c.Visible) continue;
                    visibleCount++;
                    totalWidth += c.Width;
                }
                if (visibleCount == 0) return;
                totalWidth += gap * (visibleCount - 1);
                int x = (buttonRowPanel.Width - totalWidth) / 2;
                int y = (buttonRowPanel.Height - buttonRowPanel.Controls[0].Height) / 2;
                foreach (Control c in buttonRowPanel.Controls)
                {
                    if (!c.Visible) continue;
                    c.Location = new Point(x, y);
                    x += c.Width + gap;
                }
            };

            // 组装左侧面板（从上到下：分组框[输入mod目录+子目录] → 输出文件 → 模式 → 按钮）
            leftPanel.Controls.Add(buttonRowPanel);       // Top - processed 4th
            leftPanel.Controls.Add(modeSection);          // Top - processed 3rd
            leftPanel.Controls.Add(outputRow);            // Top - processed 2nd
            leftPanel.Controls.Add(groupPanel);           // Top - 输入MOD目录+子目录分组（浅框）

            // 组装中间面板（表单区容器，执行时作为 Spin 遮罩目标）
            middlePanel.Controls.Add(leftPanel);

            // 组装外层面板（WinForms 反向处理 Dock：最后加入的最先处理，占据最上/最下）
            // 视觉自上而下 = 表单区（middlePanel，执行时作为 Spin 遮罩目标）→ 日志区
            outerPanel.Controls.Add(logView);             // Fill - 最先加入最后处理，自适应剩余空间
            outerPanel.Controls.Add(middlePanel);         // Top - 最上：表单区

            // 布局高度自适应：表单区高度随 DPI 与内容自动适配（各行高度之和），
            // 窗口拉高时日志区域自动填充剩余空间；日志区保留最小高度。
            void UpdateContentHeight()
            {
                if (outerPanel.Height <= 0) return;
                // 日志面板自带约 40px 高的工具条（筛选/过滤/清空/导出），最小高度要把工具条算进去，
                // 否则缩到最小时日志正文只剩十几像素
                int minLogH = (int)(100 * DpiManager.Scale);
                int contentH = groupPanel.Height + outputRow.Height + modeSection.Height +
                               buttonRowPanel.Height + middlePanel.Padding.Vertical;
                int availH = outerPanel.Height - outerPanel.Padding.Vertical;
                int maxMiddleH = Math.Max(200, availH - minLogH);
                middlePanel.Height = Math.Min(contentH, maxMiddleH);
            }
            outerPanel.Layout += (s, e) => UpdateContentHeight();

            tabPage.Controls.Add(outerPanel);

            return tabPage;
        }

        #endregion

        #region 搜索选项卡（跨包搜索）

        /// <summary>
        /// 搜索选项卡：不打开包，直接搜索某目录下所有 ARZ/ARC 的**包内文件名**或**文件内容**。
        /// <para>布局（Dock 反向：Fill 先加、Top 后加，最后加的在最上）：</para>
        /// <para>目录行 → 搜索词行 → 提示文本 → 选项行 → 按钮行 → 进度条 → 状态行 → [结果区] → 日志(底部)</para>
        /// </summary>
        private AntdUI.TabPage CreateArchiveSearchTab()
        {
            AntdUI.TabPage tabPage = new AntdUI.TabPage() { Text = LanguageManager.Instance.GetText("SearchTab.Title", "包内搜索") };
            tabPage.Name = "SearchTab.Title";

            AntdUI.Panel mainPanel = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Fill, Padding = new Padding(10) };

            // ═══ 顶部说明（红字，最上方）：一句话讲清"不解压也能搜"═══
            Label searchNoticeLabel = new Label
            {
                Name = "SearchTab.Notice",
                Text = LanguageManager.Instance.GetText("SearchTab.Notice",
                    "将搜索ARZ/ARC包内文件名及内容，【不解包】！"),
                Dock = DockStyle.Top,
                Height = 22,
                Font = FontHelper.UiFont(9, FontStyle.Bold),
                ForeColor = ThemeManager.Modified,   // 红字（亮色=Red / 暗色=柔和红），与"重要提示"语义一致
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 0, 0, 0)
            };

            // ═══ 三行共用容器：[左 75%（嵌套行组件容器）｜ 右 25% 排除框] ═══
            // 批21 实测修复（用户定稿的嵌套结构）：右侧只有排除框、没有其它组件 → 比例可固定：
            // 外层两列 75/25、**没有 AutoSize 列** → 排除框恒为整行的 25%，三行严格对齐；
            // 左 75% 单元格里再**嵌套**该行的组件容器（NewSearchLeftPanel）：[输入框 | 开关组 | 历史/浏览]，
            // 各行组件多少不同 → 由**输入框自适应**吸收差值（输入框长短不一，但左容器与排除框同宽对齐）。
            // ⚠ 不能把开关/按钮列提到外层当 AutoSize 列：75/25 会先扣掉 AutoSize 再分配 →
            //   比例随行内容漂移、各行排除框错位（第一版实测就是这么歪的）。
            var searchRows = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,                // 高度=3×42=126：不设时 Dock=Top 保持默认 100 → 第 3 行被裁一半（批21 实测）
                Margin = new Padding(0),
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 3
            };
            searchRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75f));  // 左容器（三行同宽）
            searchRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f)); // 排除框（固定 25%，无 AutoSize 扣减）
            for (int i = 0; i < 3; i++)
                searchRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));   // 三行等高 42

            // ═══ 目录行（左 75% 嵌套 ｜ 排除 25%，行 0）═══

            AntdUI.Button browseSearchDirButton = ButtonFactory.Create(
                LanguageManager.Instance.GetText("Common.BrowseButton", "浏览"), AntdUI.TTypeMini.Primary, false, "Common.BrowseButton");
            browseSearchDirButton.Margin = new Padding(0);
            browseSearchDirButton.Click += (s, e) => BrowseFolder(_searchDirBox,
                LanguageManager.Instance.GetText("SearchTab.BrowseDirDesc", "选择要搜索的文件夹"));

            // 历史记录（自绘浮层）：近期使用目录；可逐条清理 / 置顶，也可一键清理（保留置顶）
            _searchDirHistoryBtn = CreateSearchHistoryButton("SearchTab.History", "历史");
            _searchDirHistoryBtn.Click += (s, e) => ToggleSearchHistoryPopover(_searchDirHistoryBtn, SearchHistoryKind.Dir);

            // ═══ 目录行左容器：[搜索目录(自适应) ｜ ☐arz ☐arc ☐子 ｜ 历史/浏览] ═══
            // 批21：原独立「选项行」拆解 —— 扫描范围类开关（arz/arc/子）跟目录行走（语义 = 搜哪里）。
            // 短标签 + TooltipComponent 悬停说明（空间换取，见方案 plan_20261008 §2.3）。
            TableLayoutPanel dirLeft = NewSearchLeftPanel(withSwitches: true);
            FlowLayoutPanel dirSwitches = NewSearchSwitchFlow();
            _searchIncludeArz = CreateSearchSwitch("SearchTab.SearchArz", "arz", "SearchTab.TipArz",
                "搜索 .arz 包（默认不勾选）", false);
            _searchIncludeArc = CreateSearchSwitch("SearchTab.SearchArc", "arc", "SearchTab.TipArc",
                "搜索 .arc 包（默认勾选）", true);
            _searchIncludeSubDir = CreateSearchSwitch("SearchTab.IncludeSubDir", "子", "SearchTab.TipSubDir",
                "递归搜索搜索目录下的所有子文件夹", true);
            dirSwitches.Controls.Add(_searchIncludeArz);
            dirSwitches.Controls.Add(_searchIncludeArc);
            dirSwitches.Controls.Add(_searchIncludeSubDir);
            dirLeft.Controls.Add(dirSwitches, 1, 0);
            dirLeft.Controls.Add(NewSearchMiddleRow(_searchDirHistoryBtn, browseSearchDirButton), 2, 0);

            _searchDirBox = new TextBox
            {
                Font = FontHelper.UiFont(9),
                Name = "SearchTab.DirInput",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                PrefixText = LanguageManager.Instance.GetText("SearchTab.DirLabel", "搜索目录："),
                AllowClear = true
            };
            _localizedPrefixes.Add((_searchDirBox, "SearchTab.DirLabel", "搜索目录："));
            _searchDirBox.PlaceholderText = LanguageManager.Instance.GetText("SearchTab.DirPlaceholder", "支持拖入，选择包含 ARZ/ARC 的目录");
            _localizedPlaceholders.Add((_searchDirBox, "SearchTab.DirPlaceholder", "支持拖入，选择包含 ARZ/ARC 的目录"));
            EnableDragDrop(_searchDirBox);

            // 目录排除：包文件**完整磁盘路径**命中即跳过（子目录命中 = 整棵子树剪枝）。
            // 大小写固定不敏感（Windows 路径习惯）；`\` 与 `/` 内部归一等价（见 CollectArchiveFiles）。
            _searchDirExcludeBox = new TextBox
            {
                Font = FontHelper.UiFont(9),
                Name = "SearchTab.DirExcludeInput",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                PrefixText = LanguageManager.Instance.GetText("SearchTab.ExcludeLabel", "排除："),
                AllowClear = true
            };
            _localizedPrefixes.Add((_searchDirExcludeBox, "SearchTab.ExcludeLabel", "排除："));
            _searchDirExcludeBox.PlaceholderText = LanguageManager.Instance.GetText("SearchTab.DirExcludePlaceholder",
                "不扫这些路径，支持 * ? 、| 或（都排除）；如 \\backup\\ | _bak");
            _localizedPlaceholders.Add((_searchDirExcludeBox, "SearchTab.DirExcludePlaceholder",
                "不扫这些路径，支持 * ? 、| 或（都排除）；如 \\backup\\ | _bak"));

            dirLeft.Controls.Add(_searchDirBox, 0, 0);             // 输入框：Percent 100 → 吃掉左容器的剩余宽度（自适应）
            searchRows.Controls.Add(dirLeft, 0, 0);                // 左 75%（三行同宽）
            searchRows.Controls.Add(_searchDirExcludeBox, 1, 0);   // 排除 25%（固定比例，三行对齐）

            // ═══ 文件名条件行（行 1：左 75% 嵌套 [包内文件名 ｜ 历史] ｜ 排除 25%）═══
            // 漏斗第 1 层：只填它 = 只筛文件名（**不读数据**，快路径）

            // 历史记录（自绘浮层）：近期文件名条件 + 内置扩展名项（.cnv/.qst 的 *.* 写法）
            _searchNameHistoryBtn = CreateSearchHistoryButton("SearchTab.History", "历史");
            _searchNameHistoryBtn.Click += (s, e) => ToggleSearchHistoryPopover(_searchNameHistoryBtn, SearchHistoryKind.Name);

            TableLayoutPanel nameLeft = NewSearchLeftPanel(withSwitches: false);   // 文件名行没有开关
            nameLeft.Controls.Add(NewSearchMiddleRow(_searchNameHistoryBtn), 1, 0);

            _searchNameBox = new TextBox
            {
                Font = FontHelper.UiFont(9),
                Name = "SearchTab.NameInput",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                PrefixText = LanguageManager.Instance.GetText("SearchTab.NameLabel", "文件名："),
                AllowClear = true
            };
            _localizedPrefixes.Add((_searchNameBox, "SearchTab.NameLabel", "文件名："));
            _searchNameBox.PlaceholderText = LanguageManager.Instance.GetText("SearchTab.NamePlaceholder",
                "包内相对路径，支持 * ? 通配、空格=与、竖线=或，如 *.dbr 或 */items/*");
            _localizedPlaceholders.Add((_searchNameBox, "SearchTab.NamePlaceholder",
                "包内相对路径，支持 * ? 通配、空格=与、竖线=或，如 *.dbr 或 */items/*"));
            _searchNameBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) ExecuteArchiveSearch(); };

            // 文件名排除：包内相对路径命中 → 整条丢弃（**不读数据**）。语法与包含一致，**没有** -/!（逻辑非已收敛到此）
            _searchNameExcludeBox = new TextBox
            {
                Font = FontHelper.UiFont(9),
                Name = "SearchTab.NameExcludeInput",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                PrefixText = LanguageManager.Instance.GetText("SearchTab.ExcludeLabel", "排除："),
                AllowClear = true
            };
            _localizedPrefixes.Add((_searchNameExcludeBox, "SearchTab.ExcludeLabel", "排除："));
            _searchNameExcludeBox.PlaceholderText = LanguageManager.Instance.GetText("SearchTab.NameExcludePlaceholder",
                "不搜这些名字，支持 * ? 、空格=与、| 或（都排除）；如 */test/* | *_old");
            _localizedPlaceholders.Add((_searchNameExcludeBox, "SearchTab.NameExcludePlaceholder",
                "不搜这些名字，支持 * ? 、空格=与、| 或（都排除）；如 */test/* | *_old"));
            _searchNameExcludeBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) ExecuteArchiveSearch(); };

            nameLeft.Controls.Add(_searchNameBox, 0, 0);            // 输入框自适应（本行只有历史按钮 → 输入框最宽）
            searchRows.Controls.Add(nameLeft, 0, 1);               // 左 75%（三行同宽）
            searchRows.Controls.Add(_searchNameExcludeBox, 1, 1);  // 排除 25%

            // ═══ 内容条件行（行 2：左 75% 嵌套 ｜ 排除 25%）═══
            // 漏斗第 2 层：与文件名条件同时填写时，只对通过第 1 层的文件读内容
            TableLayoutPanel contentLeft = NewSearchLeftPanel(withSwitches: true);
            FlowLayoutPanel contentSwitches = NewSearchSwitchFlow();

            _searchContentHistoryBtn = CreateSearchHistoryButton("SearchTab.History", "历史");
            _searchContentHistoryBtn.Click += (s, e) => ToggleSearchHistoryPopover(_searchContentHistoryBtn, SearchHistoryKind.Content);

            // ═══ 内容行左容器：[内容(自适应) ｜ ☐Aa ☐全 ☐反 >> ｜ 历史] ═══
            // 批21：匹配方式类开关跟内容行走（语义 = 怎么匹配内容）。
            // 「反」主色蓝 = 切换开关；紧贴的可点「>>」= 打开「查看匹配结果」弹窗（原独立链接并入）。
            _searchCaseSensitive = CreateSearchSwitch("SearchTab.CaseSensitive", "Aa", "SearchTab.TipCase",
                "内容匹配区分大小写（仅作用于内容条件；文件名与目录固定不区分）", false);
            _searchExactMatch = CreateSearchSwitch("SearchTab.ExactMatch", "全", "SearchTab.TipExact",
                "完全匹配：内容词元按字面字符串完全相等才命中（dbr=剥掉行尾逗号后按逗号分段，字段名或任一字段值相等；"
                + "带逗号=整行不区分k和v；其它文件=整行；勾「反」时反查只完整匹配译名v；通配符 * ? 按字面量；与/或/引号仍有效；不影响排除框）", false);
            _searchLangReverse = CreateSearchSwitch("SearchTab.LangSearch", "反", "SearchTab.TipLangReverse",
                "本地化反查：内容输入物品译名（如 刀、剑）时自动反查成对应 tag 一并搜索（仅文本类有效）", false);
            // 反字用默认色（不再固定蓝），勾选状态与右侧「预览」联动同色（见 UpdateLangReverseUiState）

            // 「预览」（原 >>）：打开「查看匹配结果」弹窗。**随「反」开关联动**：
            // 未勾选「反」= 灰色不可点；勾选后 = 主色蓝可点（用户定稿，防止没开反查时点出空结果）
            _searchLangPreviewLink = new Label
            {
                Name = "SearchTab.LangSearchViewBtn",
                Text = LanguageManager.Instance.GetText("SearchTab.LangSearchViewBtn", "预览"),
                AutoSize = true,
                Margin = new Padding(0, 0, 6, 0),                  // 上边距由下方首次布局时**动态计算**（右侧 6 = 与历史按钮的间距）
                Font = FontHelper.UiFont(9, FontStyle.Underline),  // 下划线提示可点
                ForeColor = ThemeManager.TextTertiary,             // 初始未勾选「反」→ 灰（UpdateLangReverseUiState 统一管色）
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            _searchLangPreviewLink.Click += (s, e) => { if (_searchLangReverse.Checked) ShowLangReverseMatches(); };
            SetSearchSwitchTip(_searchLangPreviewLink, "SearchTab.TipViewMatches", "打开「查看匹配结果」窗口，逐行显示反查命中的语言包条目（需先勾选「反」）");

            // 「预览」垂直对齐（批23 实测定稿）：复选框组件自带内边距、比纯文字 Label 高，且**文字在复选框
            // 框内偏下** —— 纯几何居中会偏高（实测：固定 2/4px 都靠上）。⚠ 不能在 Layout 事件里取高度：
            // 那时 AutoSize 还没量完、两控件高度相等，算出的值不对（实测"调了没动"的根因）。
            // 故挂**首次 Paint**（此刻高度已定）：取两控件实际高度差的一半（纯几何居中）。
            // 校正量实测轨迹：+4 偏低3px → +2 偏低2px → 0（用户截图逐像素校准收口）。
            PaintEventHandler alignPreview = null;
            alignPreview = (s, e) =>
            {
                _searchLangPreviewLink.Paint -= alignPreview;   // 一次性：高度差与文字无关（字体相同），算一次即可
                int top = Math.Max(0, (_searchLangReverse.Height - _searchLangPreviewLink.Height) / 2);
                _searchLangPreviewLink.Margin = new Padding(0, top, 6, 0);
            };
            _searchLangPreviewLink.Paint += alignPreview;

            contentSwitches.Controls.Add(_searchCaseSensitive);
            contentSwitches.Controls.Add(_searchExactMatch);
            contentSwitches.Controls.Add(_searchLangReverse);
            contentSwitches.Controls.Add(_searchLangPreviewLink);
            contentLeft.Controls.Add(contentSwitches, 1, 0);
            contentLeft.Controls.Add(NewSearchMiddleRow(_searchContentHistoryBtn), 2, 0);

            _searchContentBox = new TextBox
            {
                Font = FontHelper.UiFont(9),
                Name = "SearchTab.ContentInput",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                PrefixText = LanguageManager.Instance.GetText("SearchTab.ContentLabel", "内容："),
                AllowClear = true
            };
            _localizedPrefixes.Add((_searchContentBox, "SearchTab.ContentLabel", "内容："));
            _searchContentBox.PlaceholderText = LanguageManager.Instance.GetText("SearchTab.ContentPlaceholder",
                "文件内容，支持空格=与（同一行）、竖线=或、\"引号=整体；仅搜 dbr/tpl/txt/lua");
            _localizedPlaceholders.Add((_searchContentBox, "SearchTab.ContentPlaceholder",
                "文件内容，支持空格=与（同一行）、竖线=或、\"引号=整体；仅搜 dbr/tpl/txt/lua"));
            _searchContentBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) ExecuteArchiveSearch(); };

            // 内容排除（**文件级**）：全文任一行命中 → 整个文件丢弃（不出结果、不计命中数）
            _searchContentExcludeBox = new TextBox
            {
                Font = FontHelper.UiFont(9),
                Name = "SearchTab.ContentExcludeInput",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                PrefixText = LanguageManager.Instance.GetText("SearchTab.ExcludeLabel", "排除："),
                AllowClear = true
            };
            _localizedPrefixes.Add((_searchContentExcludeBox, "SearchTab.ExcludeLabel", "排除："));
            _searchContentExcludeBox.PlaceholderText = LanguageManager.Instance.GetText("SearchTab.ContentExcludePlaceholder",
                "整文件不要含这些内容，支持 * ? 、| 或；如 TODO | \"FIX ME\"");
            _localizedPlaceholders.Add((_searchContentExcludeBox, "SearchTab.ContentExcludePlaceholder",
                "整文件不要含这些内容，支持 * ? 、| 或；如 TODO | \"FIX ME\""));
            _searchContentExcludeBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) ExecuteArchiveSearch(); };

            contentLeft.Controls.Add(_searchContentBox, 0, 0);     // 输入框自适应
            searchRows.Controls.Add(contentLeft, 0, 2);            // 左 75%（三行同宽）
            searchRows.Controls.Add(_searchContentExcludeBox, 1, 2); // 排除 25%

            // ═══ 提示文本（9 号字、主色；说明内容搜索的范围白名单）═══
            Label searchHintLabel = new Label
            {
                Name = "SearchTab.Hint",
                Text = LanguageManager.Instance.GetText("SearchTab.Hint",
                    "语法：空格=与  竖线=或  \"引号\"=整体；文件名另支持 * ? 通配与词首 - 排除。内容搜索仅 dbr / tpl / txt / lua（其余格式只能按文件名筛）"),
                Dock = DockStyle.Top,
                Height = 18,
                Font = FontHelper.UiFont(9),
                ForeColor = ThemeManager.Primary,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 0, 0, 0)
            };

            // 批21：原「选项行」与「本地化反查行」两条独立开关行已删除 ——
            // 扫描范围（arz/arc/子）行内化到目录行中列、匹配方式（Aa/全/反>>）行内化到内容行中列
            // （方案 plan_20261008 §2），结果区净增 84px。三行由共享 searchRows TLP 承载（见上）。

            // ═══ 按钮行（居中：搜索 + 取消）═══
            AntdUI.Panel buttonPanel = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Top, Height = 50 };

            _searchButton = ButtonFactory.Create(
                LanguageManager.Instance.GetText("SearchTab.SearchButton", "开始搜索"), AntdUI.TTypeMini.Primary, true, "SearchTab.SearchButton");

            _searchCancelButton = ButtonFactory.Create(
                LanguageManager.Instance.GetText("Common.CancelButton", "取消"), AntdUI.TTypeMini.Default, false, "Common.CancelButton");
            _searchCancelButton.Visible = false;
            _searchCancelButton.Click += (s, e) => CancelActiveOperation();

            buttonPanel.Controls.Add(_searchCancelButton);
            buttonPanel.Controls.Add(_searchButton);
            buttonPanel.Layout += (s, e) =>
            {
                int visibleCount = 0, totalWidth = 0, gap = 10;
                foreach (Control c in buttonPanel.Controls)
                {
                    if (!c.Visible) continue;
                    visibleCount++;
                    totalWidth += c.Width;
                }
                if (visibleCount == 0) return;
                totalWidth += gap * (visibleCount - 1);
                int x = (buttonPanel.Width - totalWidth) / 2;
                int y = (buttonPanel.Height - buttonPanel.Controls[0].Height) / 2;
                foreach (Control c in buttonPanel.Controls)
                {
                    if (!c.Visible) continue;
                    c.Location = new Point(x, y);
                    x += c.Width + gap;
                }
            };

            // 步骤2 占位：点击只在状态行提示，步骤3 接入真实搜索
            _searchButton.Click += (s, e) => ExecuteArchiveSearch();

            // ═══ 状态行 ═══
            // 刻意**不要进度条**：跨包搜索无法算出准确的百分比（包内条目数不等、并行推进也不齐），
            // 与其显示一个不诚实的百分比，不如把空间让给结果表 —— 用搜索按钮自带的 SPIN + 状态文字即可。
            AntdUI.Panel statusRow = BuildStatusRow(DockStyle.Top);
            _searchStatusLabel = statusRow.Controls.Find("Common.StatusLabel", true).FirstOrDefault() as Label;
            // #3：状态文字**居中**——与上方居中排列的「搜索/取消」按钮同一条垂直线，
            // 点完搜索后眼睛不用左右找（BuildStatusRow 是各页签共用的，故只改本页这个实例）
            if (_searchStatusLabel != null) _searchStatusLabel.TextAlign = ContentAlignment.MiddleCenter;

            // ═══ 结果区：表格(Fill) + 统计(Top) + 筛选(Top) ═══
            AntdUI.Panel resultPanel = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Fill };

            _searchResultTable = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = true,
                Bordered = true,
                BorderColor = ThemeManager.TableGridColor,   // 网格线加深（列头分割线=拖宽抓手，内置色对比弱）
                MultipleRows = false,
                EditMode = AntdUI.TEditMode.None,
                LostFocusClearSelection = false,
                Font = FontHelper.UiFont(9),
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill,
                VirtualMode = true,
                RowHeight = 26,
                ShowCheckBg = false,
                EnableHeaderResizing = true
            };
            // 列宽默认策略（用户定稿）：大小/时间戳/命中数/操作=auto（内容短且可预估），其余=150px（防虚拟渲染滚动抖动）；
            // 用户拖动后关窗 → TableWidthStore 整表快照，此后全列固定像素（渠道收敛：默认定义 → 拖动记忆）
            // 包名：**树形形态下包名已是树节点**，故树形时隐藏（显隐统一在 ApplySearchResultColumnVisibility）
            _searchColArchive = new AntdUI.Column("Archive", LanguageManager.Instance.GetText("SearchTab.ColArchive", "包名")) { Width = "150", SortOrder = true };
            _searchResultTable.Columns.Add(_searchColArchive);

            // 包内文件名：**兼作文件树的名称列**（SetTree("Children")）。
            //   平铺行 Children == null 时，AntdUI 自动不显示缩进与展开箭头 ——
            //   所以一份列配置同时服务两种形态，不需要两套列或两个控件。
            _searchColFile = new AntdUI.Column("File", LanguageManager.Instance.GetText("SearchTab.ColFile", "文件名匹配结果")) { Width = "150", SortOrder = true };
            _searchColFile.SetTree("Children");
            // Render：平铺显示完整包内相对路径；树形显示 DisplayName（📁/📄 + 节点名）。两者都带**配色**：
            //   · 包内文件（含平铺行）→ **危险色**（AntdUI 语义色 Error，明暗主题各自适配）
            //   · 包（.arz/.arc）→ 主色；包内目录 → 强调色；本地目录 → 默认色（物理目录不抢眼）
            _searchColFile.Render = (v, r, i) =>
            {
                var row = r as SearchResultRow;
                if (row == null) return new AntdUI.CellText("");
                var danger = AntdUI.Style.Db.Error;
                if (!_searchTreeMode)
                    return new AntdUI.CellText(row.File ?? "", danger);              // 平铺：每行都是包内文件
                string text = row.DisplayName ?? row.File ?? "";
                switch (row.NodeKind)
                {
                    case SearchNodeKind.LocalDir:
                        return new AntdUI.CellText(text);                            // 物理目录：默认色
                    case SearchNodeKind.Package:
                        return new AntdUI.CellText(text, ThemeManager.Primary);       // 包（.arz/.arc）：主色
                    case SearchNodeKind.InArchiveDir:
                        return new AntdUI.CellText(text, AntdUI.Style.Db.Warning);    // 包内目录：强调色
                    default:
                        return new AntdUI.CellText(text, danger);                     // 包内文件：危险色
                }
            };
            _searchResultTable.Columns.Add(_searchColFile);
            _searchResultTable.Columns.Add(new AntdUI.Column("Size", LanguageManager.Instance.GetText("SearchTab.ColSize", "大小")) { Width = "auto", ColAlign = AntdUI.ColumnAlign.Right, SortOrder = true });
            _searchResultTable.Columns.Add(new AntdUI.Column("Time", LanguageManager.Instance.GetText("SearchTab.ColTime", "时间戳")) { Width = "auto", SortOrder = true });
            // 命中数 → 内容匹配结果：紧跟时间戳，一行结果的"是什么/多大/什么时候/命中几次/命中了什么"连着看
            _searchResultTable.Columns.Add(new AntdUI.Column("Hits", LanguageManager.Instance.GetText("SearchTab.ColHits", "命中数")) { Width = "auto", ColAlign = AntdUI.ColumnAlign.Right, SortOrder = true });
            // ⚠ 内容列**必须 `Ellipsis = false`（不能开省略号）**，原因是 AntdUI 的 flags 组合：
            //   `Ellipsis=true, LineBreak=false` → `Left|VerticalCenter|EllipsisCharacter`（**没有 NoWrap**）
            //     → GDI+ 会**按列宽把文本折行**；内容值很长（5 段命中拼成一行 ≈235 字符）→ 折成 3~6 行，
            //       整块高 47~95px ≫ 单行行高 26px → 竖直居中后矩形内没有完整行
            //       → `GenericTypographic` 自带的 `LineLimit`（只画完整行）把不完整行全丢 → **整格空白**（实测）。
            //   `Ellipsis=false, LineBreak=false` → `Left|VerticalCenter|NoWrap`
            //     → 不折行（块高恒为 15.8px ≤ 26）且 `Helper.TF` **只在 NoWrap 分支清掉 `LineLimit`** —— 双保险。
            //   代价：右端是**硬裁**（没有 "…"）。可接受：悬停有自带 tip 显示完整文本，右键「复制」是多行原值。
            // ⚠ 也**不能设 LineBreak**：`Ellipsis=true+LineBreak=true` 的 `NoWrapEllipsis` 组合虽能 NoWrap，
            //   但自带 tip 的触发条件含 `!col.LineBreak`（Table.Mouse.cs:816），一开 LineBreak 就不出 tip。
            // ⚠ 内容列**不能加 Render**：AntdUI 的 `AddCells` 对 `value is ICell` 会把单元格包成 `Template`
            //   （Table.Layout.cs:1006），而 `MouseHoverRow` 对 Template 单元格**只认 CellLink/CellImage**
            //   （Table.Mouse.cs:899）→ 普通 CellText 的 Template 单元格**不弹自带 tip**。
            _searchColContent = new AntdUI.Column("Content", LanguageManager.Instance.GetText("SearchTab.ColContent", "内容匹配结果前1-5项")) { Width = "150", SortOrder = true };
            _searchResultTable.Columns.Add(_searchColContent);
            // 内容列的**文字配色**：用「安全色/通过色」（AntdUI 语义色 Success，明暗主题各自适配）——
            // 搜到就是"找到了"，红色（危险色）会读成报错。
            // ⚠ 为什么不用 `Column.Render` 上色：Render 会把单元格包成 `Template` → **自带 tip 失效**（见上）。
            //   改用 AntdUI 的公开绘制扩展点 `CellPaintBegin`（`Table.Event.cs` 的 `OnCellPaintBegin`）：
            //   它只提供前景/背景/字体覆盖，**不改变单元格类型** → tip 照常。
            // ⚠ `CellFore` 的画笔由表格 `using` 释放（`Table.Render.cs:685`），所以必须**每次新建**，
            //   不能缓存复用（复用会被释放掉 → 下一次绘制用已释放画笔）。
            _searchResultTable.CellPaintBegin += (s, e) =>
            {
                if (e.RowType != AntdUI.RowType.None) return;    // 只给**数据行**上色（表头 Column / 汇总 Summary 不动）
                if (e.Column?.Key != "Content") return;
                e.CellFore = new SolidBrush(AntdUI.Style.Db.Success);
            };
            // 说明 / 本地化名称 / 模板：仅 .dbr 有值，跟随「显示说明/模板/本地化名称」开关显隐
            _searchColDescription = new AntdUI.Column("Description", LanguageManager.Instance.GetText("SearchTab.ColDescription", "说明")) { Width = "150", SortOrder = true };
            _searchColLocalized = new AntdUI.Column("Localized", LanguageManager.Instance.GetText("SearchTab.ColNotes", "本地化名称")) { Width = "150", SortOrder = true };
            _searchColTemplate = new AntdUI.Column("Template", LanguageManager.Instance.GetText("SearchTab.ColTemplate", "模板")) { Width = "150", SortOrder = true };
            _searchResultTable.Columns.Add(_searchColDescription);
            _searchResultTable.Columns.Add(_searchColLocalized);
            _searchResultTable.Columns.Add(_searchColTemplate);
            // 操作列**不参与排序**（不是数据列）；auto（内容固定为"打开"按钮，可预估）
            _searchResultTable.Columns.Add(new AntdUI.Column("Action", LanguageManager.Instance.GetText("SearchTab.ColAction", "操作")) { Width = "auto", ColAlign = AntdUI.ColumnAlign.Center });
            // 列定义就绪后应用记忆列宽（TableWidthStore；首次调用捕获原始列宽供重置恢复）+ MouseUp 防抖自动保存
            TableWidthStore.Apply(TableWidthStore.TableId.SearchResults, _searchResultTable);
            TableWidthStore.AttachAutoSave(TableWidthStore.TableId.SearchResults, _searchResultTable);
            // 搜索结果表无其它右键菜单，挂上「重置本表列宽记忆」入口
            TableWidthStore.AttachResetMenu(TableWidthStore.TableId.SearchResults, _searchResultTable);
            _searchResultTable.EmptyText = LanguageManager.Instance.GetText("Common.NoData", "暂无数据");
            // 操作列「打开」+ 行双击 + 行右键菜单，都走同一条打开逻辑
            _searchResultTable.CellButtonClick += OnSearchResultCellButtonClick;
            _searchResultTable.CellDoubleClick += OnSearchResultCellDoubleClick;
            _searchResultTable.CellClick += OnSearchResultCellClickRow;
            _searchResultTable.SortRows += OnSearchResultSortRows;   // 列头排序（数据由我们排）

            _searchResultRows = new AntdUI.AntList<SearchResultRow>();
            _searchResultTable.Binding(_searchResultRows);

            // ⚠ 统计标签是**动态内容**（不是静态文案）：不设 Name，避免被 RefreshControlText 按 key 覆盖；
            //   语言切换时由搜索页自己的刷新逻辑重建文案。
            _searchStatsLabel = new Label
            {
                Text = "",
                Dock = DockStyle.Top,
                Height = 22,
                Font = FontHelper.UiFont(9),
                TextAlign = ContentAlignment.MiddleLeft
            };

            AntdUI.Panel filterRow = new AntdUI.Panel { Back = Color.Transparent, Dock = DockStyle.Top, Height = 36, Visible = false };
            _searchFilterRow = filterRow;   // #1：默认隐藏，首次出结果时才显示（见 FlushSearchRows）
            _searchFilterBox = new TextBox
            {
                Font = FontHelper.UiFont(9),
                Name = "SearchTab.FilterInput",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                AllowClear = true
            };
            _searchFilterBox.PlaceholderText = LanguageManager.Instance.GetText("SearchTab.FilterPlaceholder", "筛选结果（包名 / 包内文件名 / 说明 / 模板 / 本地化名称 / 内容）...");
            _localizedPlaceholders.Add((_searchFilterBox, "SearchTab.FilterPlaceholder", "筛选结果（包名 / 包内文件名 / 说明 / 模板 / 本地化名称 / 内容）..."));

            // ═══ 形态切换 + 展开/收缩（#5；改用与「包内文件名/包内文件内容」**同一 Segmented 组件**）═══
            // 放"筛选行"最右侧：该行只在有结果时显示，所以它们天然只在有结果时可用。
            // ⚠ Segmented 不能放进 FlowPanel（实测被放大错位），必须"普通 Panel + 显式 Size + 手动居中"
            //   —— 与上面的模式切换同一做法。
            string treeSegText = LanguageManager.Instance.GetText("Editor.TreeViewMode", "树型显示");
            string flatSegText = LanguageManager.Instance.GetText("Editor.FlatViewMode", "平铺显示");
            var viewSegFont = FontHelper.UiFont(9);
            int viewSegWidth = TextRenderer.MeasureText(treeSegText, viewSegFont).Width
                + TextRenderer.MeasureText(flatSegText, viewSegFont).Width + DpiManager.S(36);
            _searchViewModeSeg = new AntdUI.Segmented
            {
                Name = "SearchTab.ViewModeSeg",
                Font = viewSegFont,
                BackColor = ThemeManager.BgContainer,
                BorderColor = ThemeManager.BorderColor,
                BackActive = ThemeManager.Primary,
                ForeActive = Color.White,
                Dock = DockStyle.None,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                AutoSize = false,
                Size = new Size(viewSegWidth, DpiManager.S(26)),
                SelectIndex = 1               // 1 = 平铺（默认形态）
            };
            _searchViewModeSeg.Items.Add(new AntdUI.SegmentedItem { ID = "tree", Text = treeSegText });
            _searchViewModeSeg.Items.Add(new AntdUI.SegmentedItem { ID = "flat", Text = flatSegText });
            _searchViewModeSeg.SelectIndexChanged += (s, e) => { if (!_syncingSearchViewSeg) ScheduleSearchViewModeSwitch(); };

            string collapseSegText = LanguageManager.Instance.GetText("ModMergeTool.CollapseAll", "收缩");
            string expandSegText = LanguageManager.Instance.GetText("ModMergeTool.ExpandAll", "展开");
            int expandSegWidth = TextRenderer.MeasureText(collapseSegText, viewSegFont).Width
                + TextRenderer.MeasureText(expandSegText, viewSegFont).Width + DpiManager.S(36);
            _searchExpandSeg = new AntdUI.Segmented
            {
                Name = "SearchTab.ExpandSeg",
                Font = viewSegFont,
                BackColor = ThemeManager.BgContainer,
                BorderColor = ThemeManager.BorderColor,
                BackActive = ThemeManager.Primary,
                ForeActive = Color.White,
                Dock = DockStyle.None,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                AutoSize = false,
                Size = new Size(expandSegWidth, DpiManager.S(26)),
                SelectIndex = 0               // 0 = 收缩（与"切到树形默认全收缩"一致）
            };
            _searchExpandSeg.Items.Add(new AntdUI.SegmentedItem { ID = "collapse", Text = collapseSegText });
            _searchExpandSeg.Items.Add(new AntdUI.SegmentedItem { ID = "expand", Text = expandSegText });
            _searchExpandSeg.SelectIndexChanged += (s, e) => { if (!_syncingSearchViewSeg) ScheduleSearchExpandSwitch(); };

            filterRow.Controls.Add(_searchFilterBox);   // Fill 先加

            // 两个 Segmented 各放一个普通 Panel（Dock=Right），并在 Panel 内垂直居中
            AntdUI.Panel expandSegWrap = new AntdUI.Panel
            {
                Back = Color.Transparent,
                Dock = DockStyle.Right,
                Width = expandSegWidth + DpiManager.S(16)
            };
            expandSegWrap.Controls.Add(_searchExpandSeg);
            void CenterExpandSeg() => _searchExpandSeg.Location = new Point(
                Math.Max(0, (expandSegWrap.Width - _searchExpandSeg.Width) / 2),
                Math.Max(0, (expandSegWrap.Height - _searchExpandSeg.Height) / 2));
            expandSegWrap.Resize += (s, e) => CenterExpandSeg();
            CenterExpandSeg();

            AntdUI.Panel viewSegWrap = new AntdUI.Panel
            {
                Back = Color.Transparent,
                Dock = DockStyle.Right,
                Width = viewSegWidth + DpiManager.S(16)
            };
            viewSegWrap.Controls.Add(_searchViewModeSeg);
            void CenterViewSeg() => _searchViewModeSeg.Location = new Point(
                Math.Max(0, (viewSegWrap.Width - _searchViewModeSeg.Width) / 2),
                Math.Max(0, (viewSegWrap.Height - _searchViewModeSeg.Height) / 2));
            viewSegWrap.Resize += (s, e) => CenterViewSeg();
            CenterViewSeg();

            filterRow.Controls.Add(expandSegWrap);   // 视觉：筛选框 | 收缩/展开 | 树型/平铺
            filterRow.Controls.Add(viewSegWrap);     // 最后加 → 最右

            // 形态切换防抖 300ms：切树形要重建整棵树（5 万条量级），连点不能每次都重建
            _searchViewModeDebounce = new System.Windows.Forms.Timer { Interval = 300 };
            _searchViewModeDebounce.Tick += (s, e) =>
            {
                _searchViewModeDebounce.Stop();
                bool wantTree = _searchViewModeSeg.SelectIndex == 0;
                if (wantTree != _searchTreeMode) SwitchSearchViewMode(wantTree);
            };
            // 展开/收缩防抖 250ms：ExpandAll 会触发整表重排，连点要合成一次
            _searchExpandDebounce = new System.Windows.Forms.Timer { Interval = 250 };
            _searchExpandDebounce.Tick += (s, e) =>
            {
                _searchExpandDebounce.Stop();
                if (!_searchTreeMode) return;
                _searchResultTable?.ExpandAll(_searchExpandSeg.SelectIndex == 1);
            };

            // 筛选防抖 300ms（与编辑器既有口径一致）
            _searchFilterDebounce = new System.Windows.Forms.Timer { Interval = 300 };
            _searchFilterDebounce.Tick += (s, e) =>
            {
                _searchFilterDebounce.Stop();
                ApplySearchFilter();
            };
            _searchFilterBox.TextChanged += (s, e) =>
            {
                _searchFilterDebounce.Stop();
                _searchFilterDebounce.Start();
            };

            resultPanel.Controls.Add(_searchResultTable);   // Fill 先加
            resultPanel.Controls.Add(_searchStatsLabel);    // 处理次序：先统计
            resultPanel.Controls.Add(filterRow);            // 再筛选（视觉在统计上方）

            // 本页**不放日志组件**（用户要求）：结果表格占满剩余空间；操作中的错误/失败包仍写日志文件，
            // 状态行给即时反馈，不占用展示面积。
            // 加入顺序 = 视觉顺序的反序（WinForms 从后往前处理 Dock）
            mainPanel.Controls.Add(resultPanel);      // Fill → 最后处理（占满剩余空间）
            mainPanel.Controls.Add(statusRow);
            mainPanel.Controls.Add(buttonPanel);
            mainPanel.Controls.Add(searchHintLabel);
            mainPanel.Controls.Add(searchRows);    // 三行共用 TLP（目录/文件名/内容），加入一次即可
            mainPanel.Controls.Add(searchNoticeLabel); // Top → 处理次序最靠前 → 最上

            tabPage.Controls.Add(mainPanel);

            ApplySearchSettingsToUi();           // #8：套用已持久化的搜索选项（并接线"改动即存"）
            ApplySearchResultColumnVisibility();   // 三列固定显示；「包名」列随形态显隐

            // 列头/EmptyText/Segmented 项/下拉文字/操作列按钮都不在 RefreshControlText 的控件树覆盖范围内，
            // 切语言需单独刷新（wiki 6.6）；本地化名称按行内 RawTag 重算，无需重新解包
            LanguageManager.Instance.LanguageChanged += RefreshSearchTabLocalizedTexts;

            return tabPage;
        }

        /// <summary>搜索页签的多语言刷新（列头 / 空态 / Segmented 项 / 快速选取 / 操作列按钮 / 本地化名称重算）</summary>
        private void RefreshSearchTabLocalizedTexts()
        {
            if (_searchResultTable == null || _searchResultTable.IsDisposed) return;
            // 历史浮层内容不在主窗体控件树内、且已按旧语言生成 → 直接收起，下次点开按新语言重建
            CloseSearchHistoryPopover();
            RefreshSearchSwitchTips();   // 行内开关的 tooltip 不在控件树内，需按新语言重刷（批21）
            try
            {
                void SetCol(string key, string titleKey, string fallback)
                {
                    foreach (AntdUI.Column c in _searchResultTable.Columns)
                    {
                        if (c.Key != key) continue;
                        c.Title = LanguageManager.Instance.GetText(titleKey, fallback);
                        break;
                    }
                }
                SetCol("Archive", "SearchTab.ColArchive", "包名");
                SetCol("File", "SearchTab.ColFile", "包内文件名");
                SetCol("Size", "SearchTab.ColSize", "大小");
                SetCol("Time", "SearchTab.ColTime", "时间戳");
                SetCol("Description", "SearchTab.ColDescription", "说明");
                SetCol("Localized", "SearchTab.ColNotes", "本地化名称");
                SetCol("Template", "SearchTab.ColTemplate", "模板");
                SetCol("Hits", "SearchTab.ColHits", "命中数");
                SetCol("Content", "SearchTab.ColContent", "内容");
                SetCol("Action", "SearchTab.ColAction", "操作");
                _searchResultTable.EmptyText = LanguageManager.Instance.GetText("Common.NoData", "暂无数据");

                // 形态 / 展开-收缩 Segmented（Items 不在控件树内，切语言需单独刷新；宽度随文字重算，否则被裁切）
                if (_searchViewModeSeg != null && !_searchViewModeSeg.IsDisposed && _searchViewModeSeg.Items.Count == 2)
                {
                    _searchViewModeSeg.Items[0].Text = LanguageManager.Instance.GetText("Editor.TreeViewMode", "树型显示");
                    _searchViewModeSeg.Items[1].Text = LanguageManager.Instance.GetText("Editor.FlatViewMode", "平铺显示");
                    ResizeSearchSegmented(_searchViewModeSeg, DpiManager.S(36));
                    _searchViewModeSeg.Invalidate();
                }
                if (_searchExpandSeg != null && !_searchExpandSeg.IsDisposed && _searchExpandSeg.Items.Count == 2)
                {
                    _searchExpandSeg.Items[0].Text = LanguageManager.Instance.GetText("ModMergeTool.CollapseAll", "收缩");
                    _searchExpandSeg.Items[1].Text = LanguageManager.Instance.GetText("ModMergeTool.ExpandAll", "展开");
                    ResizeSearchSegmented(_searchExpandSeg, DpiManager.S(36));
                    _searchExpandSeg.Invalidate();
                }

                // 「预览」链接文字不在 RefreshControlText 覆盖范围，切语言需单独刷新（颜色走 UpdateLangReverseUiState，不在此动）
                if (_searchLangPreviewLink != null && !_searchLangPreviewLink.IsDisposed)
                    _searchLangPreviewLink.Text = LanguageManager.Instance.GetText("SearchTab.LangSearchViewBtn", "预览");

                // 操作列按钮 + 本地化名称：对已有结果行就地重建（不重新解包）
                foreach (var row in _searchAllRows)
                {
                    row.Action = BuildSearchRowActionCell();
                    if (!string.IsNullOrEmpty(row.RawTag))
                        row.Localized = SafeCellText(LocalizationHelper.LookupLocalizedTag(row.RawTag, _searchLangTags));
                }
                _searchResultTable.Invalidate();
            }
            catch (Exception ex)
            {
                try { LogService.Warn($"[search] 语言刷新失败：{ex.Message}"); } catch { }
            }
        }

        /// <summary>
        /// 把持久化的搜索选项套用到界面，并接线"改动即存"（#8）。
        /// <para>6 项：ARZ 包 / ARC 包 / 包含子目录 / 区分大小写 / **完全匹配（批21 新增）** /
        /// **本地化反查开关**（状态持久化，重启保持；条件本身走各自的历史记录，不在此处持久化）。</para>
        /// <para>顺序：**先赋值、后接线**，避免初始化过程中触发一次无意义保存。</para>
        /// </summary>
        private void ApplySearchSettingsToUi()
        {
            try
            {
                if (_searchIncludeArz != null) _searchIncludeArz.Checked = SettingsManager.SearchIncludeArz;
                if (_searchIncludeArc != null) _searchIncludeArc.Checked = SettingsManager.SearchIncludeArc;
                if (_searchIncludeSubDir != null) _searchIncludeSubDir.Checked = SettingsManager.SearchIncludeSubDir;
                if (_searchCaseSensitive != null) _searchCaseSensitive.Checked = SettingsManager.SearchCaseSensitive;
                if (_searchExactMatch != null) _searchExactMatch.Checked = SettingsManager.SearchExactMatch;
                if (_searchLangReverse != null) _searchLangReverse.Checked = SettingsManager.SearchLangReverse;

                // ── 接线：改动即存（与项目其它选项一致）──
                void Save() { try { SettingsManager.Save(); } catch { } }
                if (_searchIncludeArz != null)
                    _searchIncludeArz.CheckedChanged += (s, e) => { SettingsManager.SearchIncludeArz = _searchIncludeArz.Checked; Save(); };
                if (_searchIncludeArc != null)
                    _searchIncludeArc.CheckedChanged += (s, e) => { SettingsManager.SearchIncludeArc = _searchIncludeArc.Checked; Save(); };
                if (_searchIncludeSubDir != null)
                    _searchIncludeSubDir.CheckedChanged += (s, e) => { SettingsManager.SearchIncludeSubDir = _searchIncludeSubDir.Checked; Save(); };
                if (_searchCaseSensitive != null)
                    _searchCaseSensitive.CheckedChanged += (s, e) => { SettingsManager.SearchCaseSensitive = _searchCaseSensitive.Checked; Save(); };
                if (_searchExactMatch != null)
                    _searchExactMatch.CheckedChanged += (s, e) => { SettingsManager.SearchExactMatch = _searchExactMatch.Checked; Save(); };
                if (_searchLangReverse != null)
                    _searchLangReverse.CheckedChanged += (s, e) =>
                    {
                        SettingsManager.SearchLangReverse = _searchLangReverse.Checked; Save();
                        UpdateLangReverseUiState();   // 「预览」按钮随「反」开关联动（灰/蓝、可点/不可点）
                    };

                UpdateLangReverseUiState();   // 初始状态同步（恢复的持久化「反」= true 时「预览」直接可点）


                UpdateSearchViewModeButtons();   // 初始：平铺为 Active，展开/收缩禁用（树形才有意义）
            }
            catch (Exception ex)
            {
                try { LogService.Warn($"[search] 套用搜索选项失败：{ex.Message}"); } catch { }
            }
        }

        /// <summary>
        /// 「反」开关 → 「预览」按钮的状态联动（用户定稿）：
        /// <para>未勾选「反」：预览 = 灰色（<see cref="ThemeManager.TextTertiary"/>）不可点；「反」字 = 默认文字色。</para>
        /// <para>勾选后：预览与「反」字同为**主色蓝**（<see cref="ThemeManager.Primary"/>），预览可点。</para>
        /// </summary>
        private void UpdateLangReverseUiState()
        {
            bool on = _searchLangReverse != null && _searchLangReverse.Checked;
            if (_searchLangPreviewLink != null && !_searchLangPreviewLink.IsDisposed)
            {
                _searchLangPreviewLink.ForeColor = on ? ThemeManager.Primary : ThemeManager.TextTertiary;
                _searchLangPreviewLink.Cursor = on ? Cursors.Hand : Cursors.Default;
            }
            if (_searchLangReverse != null && !_searchLangReverse.IsDisposed)
                _searchLangReverse.ForeColor = on ? ThemeManager.Primary : ThemeManager.Text;
        }

        /// <summary>「显示说明/模板/本地化名称」开关 → 控制三列显隐（关掉时表格更紧凑）</summary>
        /// <summary>
        /// 结果表列显隐：**说明 / 本地化名称 / 模板三列固定显示**（不再有开关 —— 这三项与"内容"
        /// 出自同一次读取 + 同一次解析，固定提取才能让用户一眼看到文件是什么东西）。
        /// 唯一随形态变化的是「包名」列：树形下包名已是树节点，列冗余 → 隐藏；回平铺再显示。
        /// </summary>
        private void ApplySearchResultColumnVisibility()
        {
            if (_searchColArchive != null) _searchColArchive.Visible = !_searchTreeMode;
        }

        /// <summary>本地化反查的单词 tag 收集上限（决策点 ③）：防止译名过泛时替代项过多拖慢整轮内容匹配</summary>
        private const int SearchLangReverseMaxTags = 200;


        // ═══════════ 结果表形态切换：平铺 ↔ 文件树（#5）═══════════
        //
        // 交互：搜索中**强制平铺**（流式追加、实时显示）+ 切换按钮禁用；搜索完成后可切换；
        //       切到树形后**默认全部收缩**，由用户自行展开（AntdUI 默认即折叠，见下）。
        // 树层级：搜索目录名 → [本地子目录…] → 包文件名 → 包内目录链… → 文件名
        //        （同一包内文件全部挂在"本包节点"之下 —— 这是用户的核心诉求）
        // 为什么流式不建树：命中是并行到达、顺序不确定，"按排序插入到指定行"每次 O(n) 搬移，
        //        5 万条 = 亿级搬移，比现在还慢；所以流式仍批量追加，树在收尾/切换时一次性建。

        private bool _searchTreeMode;
        private bool _searchRunning;
        /// <summary>形态切换 Segmented（0=树型显示 / 1=平铺显示；与「包内文件名/包内文件内容」同一组件）</summary>
        private AntdUI.Segmented _searchViewModeSeg;
        /// <summary>展开/收缩 Segmented（0=收缩 / 1=展开；仅树形可用）</summary>
        private AntdUI.Segmented _searchExpandSeg;
        /// <summary>形态切换防抖（切树形要重建整棵树，连点不能每次重建）</summary>
        private System.Windows.Forms.Timer _searchViewModeDebounce;
        /// <summary>展开/收缩防抖（ExpandAll 触发整表重排，连点合成一次）</summary>
        private System.Windows.Forms.Timer _searchExpandDebounce;
        /// <summary>程序改 Segmented.SelectIndex 时抑制其事件，避免自触发</summary>
        private bool _syncingSearchViewSeg;
        /// <summary>文件树最大深度（用于给名称列预留缩进余量）</summary>
        private int _searchTreeMaxDepth;

        /// <summary>
        /// 换数据源内容：**平铺**走 AntList 增量路径（快）。
        /// <para>⚠ 平铺行没有子节点，增量路径足够；树形必须用 <see cref="SetSearchRowsTree"/>。</para>
        /// </summary>
        private void SetSearchRowsFlat(IList<SearchResultRow> rows)
        {
            _searchResultRows.Clear();
            if (rows != null && rows.Count > 0) _searchResultRows.AddRange(rows);
        }

        /// <summary>
        /// 换数据源内容：**树形**必须全量重建。
        /// <para>⚠ 为什么：AntList 的增量通知（`AddRange` → `action("add", int[])`）在 AntdUI 里只是
        /// `new IRow(i, row, cells, 0, null)` —— **深度固定 0、不递归子节点**，所以树只会显示根节点；
        /// 只有全量 `IBinding`（内部走 `GetRowAuto`）才会按 `KeyTree` 递归扁平化整棵树。
        /// 同时全量重建会把展开状态重置为默认（即"默认全部收缩"）。</para>
        /// </summary>
        private void SetSearchRowsTree(List<SearchResultRow> roots)
        {
            SetSearchRowsFlat(roots);
            _searchResultTable?.Binding(_searchResultRows);   // 全量重建 → 递归扁平化树
        }

        /// <summary>
        /// 切换结果表形态。
        /// <para>形态**不做持久化**：按既定交互，每次搜索都从平铺开始（搜索中固定平铺以保持实时显示），
        /// 搜索完成后由用户点按钮切换 —— 持久化反而会造出一个"存了却不生效"的死配置项。</para>
        /// <para>搜索进行中直接忽略（按钮已禁用，这里再兜一层）。</para>
        /// </summary>
        private async void SwitchSearchViewMode(bool tree)
        {
            if (_searchRunning) return;

            _searchTreeMode = tree;
            UpdateSearchViewModeButtons();

            if (!tree)
            {
                // 平铺：直接换回"扁平行集合"（同一批行对象，无需重建）
                try
                {
                    SetSearchRowsFlat(_searchAllRows);
                    ApplySearchResultColumnVisibility();     // 平铺恢复「包名」列
                    ScrollSearchResultsToTop();
                }
                catch (Exception ex)
                {
                    try { LogService.Warn($"[search] 切回平铺失败：{ex.Message}"); } catch { }
                }
                return;
            }

            if (_searchAllRows.Count == 0)
            {
                // 没有结果就没有可建的树：把形态与 Segmented 都退回平铺，避免"选中了树型却什么都没有"
                _searchTreeMode = false;
                UpdateSearchViewModeButtons();
                return;
            }

            // 建树放后台线程（5 万条量级不卡 UI），完成后一次性换数据源
            string prevStatus = _searchStatusLabel?.Text;
            if (_searchStatusLabel != null)
                _searchStatusLabel.Text = LanguageManager.Instance.GetText("SearchTab.BuildingTree", "正在生成文件树…");
            try
            {
                var source = _searchAllRows.ToArray();
                List<SearchResultRow> roots = await Task.Run(() => BuildSearchTreeRows(source));
                if (!_searchTreeMode) return;            // 期间又被切回平铺
                SetSearchRowsTree(roots);
                ApplySearchResultColumnVisibility();     // 树形隐藏「包名」列
                // 默认**展开**（用户要求）：全量重建会重置展开状态，这里显式展开一次
                _searchResultTable.ExpandAll(true);
                SyncSearchExpandSeg(1);                  // 展开/收缩 Segmented 同步到"展开"
                ScrollSearchResultsToTop();
            }
            catch (Exception ex)
            {
                try { LogService.Warn($"[search] 生成文件树失败：{ex.Message}"); } catch { }
            }
            finally
            {
                if (_searchStatusLabel != null && !string.IsNullOrEmpty(prevStatus))
                    _searchStatusLabel.Text = prevStatus;
            }
        }

        /// <summary>滚动结果表回顶部（切形态后行数剧变，避免停在一片空白处）</summary>
        private void ScrollSearchResultsToTop()
        {
            try
            {
                if (_searchResultTable?.ScrollBar != null) _searchResultTable.ScrollBar.ValueY = 0;
            }
            catch { }
        }

        /// <summary>
        /// 搜索结果的树节点类别（#5 三级配色用；平铺形态不参与配色，一律默认色）：
        /// <para>· <see cref="File"/> 包内文件（叶；平铺行也是它）· <see cref="LocalDir"/> 本地目录（磁盘真实目录，含搜索目录根）
        /// · <see cref="Package"/> 包节点（.arz/.arc）· <see cref="InArchiveDir"/> 包内目录</para>
        /// </summary>
        private enum SearchNodeKind
        {
            /// <summary>包内文件（叶节点）</summary>
            File = 0,
            /// <summary>本地目录（磁盘上的物理目录）</summary>
            LocalDir,
            /// <summary>包节点（.arz / .arc）</summary>
            Package,
            /// <summary>包内目录</summary>
            InArchiveDir
        }

        /// <summary>形态按钮状态：Active 样式跟随当前形态；展开/收缩仅树形可用；搜索中全部禁用</summary>
        private void UpdateSearchViewModeButtons()
        {
            try
            {
                // 同步 Segmented 选中项（程序改值会触发 SelectIndexChanged，故用抑制标记）
                _syncingSearchViewSeg = true;
                try
                {
                    if (_searchViewModeSeg != null && !_searchViewModeSeg.IsDisposed)
                        _searchViewModeSeg.SelectIndex = _searchTreeMode ? 0 : 1;
                }
                finally { _syncingSearchViewSeg = false; }

                if (_searchViewModeSeg != null && !_searchViewModeSeg.IsDisposed)
                {
                    _searchViewModeSeg.Enabled = !_searchRunning;
                    _searchViewModeSeg.Invalidate();
                }
                if (_searchExpandSeg != null && !_searchExpandSeg.IsDisposed)
                {
                    _searchExpandSeg.Enabled = !_searchRunning && _searchTreeMode;   // 展开/收缩仅树形有意义
                    _searchExpandSeg.Invalidate();
                }
            }
            catch { }
        }

        /// <summary>同步展开/收缩 Segmented 的选中项（抑制事件）</summary>
        private void SyncSearchExpandSeg(int index)
        {
            if (_searchExpandSeg == null || _searchExpandSeg.IsDisposed) return;
            _syncingSearchViewSeg = true;
            try { _searchExpandSeg.SelectIndex = index; }
            finally { _syncingSearchViewSeg = false; }
        }

        /// <summary>
        /// 按两段文字重算 Segmented 宽度（含其 Dock=Right 包装 Panel），切语言后文字变化时调用，
        /// 否则新文字会被原宽度裁切。
        /// </summary>
        private static void ResizeSearchSegmented(AntdUI.Segmented seg, int extra)
        {
            try
            {
                if (seg == null || seg.IsDisposed || seg.Items.Count < 2) return;
                var font = seg.Font ?? FontHelper.UiFont(9);
                int w = TextRenderer.MeasureText(seg.Items[0].Text ?? "", font).Width
                    + TextRenderer.MeasureText(seg.Items[1].Text ?? "", font).Width + extra;
                seg.Size = new Size(w, seg.Height);
                if (seg.Parent != null && seg.Parent.Dock == DockStyle.Right)
                {
                    seg.Parent.Width = w + DpiManager.S(16);
                    seg.Location = new Point(
                        Math.Max(0, (seg.Parent.Width - seg.Width) / 2),
                        Math.Max(0, (seg.Parent.Height - seg.Height) / 2));
                }
            }
            catch { }
        }

        /// <summary>形态切换请求（防抖 300ms 后执行；连点只按最后一次生效）</summary>
        private void ScheduleSearchViewModeSwitch()
        {
            if (_searchRunning) { UpdateSearchViewModeButtons(); return; }   // 搜索中固定平铺
            if (_searchViewModeDebounce == null) return;
            _searchViewModeDebounce.Stop();
            _searchViewModeDebounce.Start();
        }

        /// <summary>展开/收缩请求（防抖 250ms）</summary>
        private void ScheduleSearchExpandSwitch()
        {
            if (_searchExpandDebounce == null) return;
            _searchExpandDebounce.Stop();
            _searchExpandDebounce.Start();
        }

        /// <summary>
        /// 把一批结果行组装成文件树（纯数据，可在后台线程跑）。
        /// <para>每个叶子的路径 = 本地相对目录链 + 包文件名 + 包内目录链 + 文件名，
        /// 这样"同一包内文件必然全部挂在同一个包节点下"。</para>
        /// <para>同层排序：**目录在前、文件在后**，各自按名称 OrdinalIgnoreCase 升序 ——
        /// 命中是并行到达的，不排序会导致每次切换顺序都不一样。</para>
        /// </summary>
        private List<SearchResultRow> BuildSearchTreeRows(IList<SearchResultRow> source)
        {
            string searchDir = _searchDirBox?.Text?.Trim() ?? "";
            string rootName = "";
            try { rootName = new DirectoryInfo(searchDir).Name; } catch { }
            if (string.IsNullOrEmpty(rootName)) rootName = searchDir;

            var root = new SearchResultRow
            {
                IsDirectory = true,
                DisplayName = "📁 " + rootName,
                NodePath = "",
                NodeKind = SearchNodeKind.LocalDir,
                Children = new List<SearchResultRow>()
            };
            var index = new Dictionary<string, SearchResultRow>(StringComparer.OrdinalIgnoreCase) { { "", root } };
            var parts = new List<(string name, bool isPkg)>();
            int maxDepth = 0;

            foreach (var row in source)
            {
                if (row == null || row.IsDirectory) continue;

                parts.Clear();
                // ① 本地相对目录（相对搜索目录；不勾"包含子目录"时为空）
                string localRel = GetLocalRelativeDir(searchDir, row.ArchivePath);
                if (!string.IsNullOrEmpty(localRel)) AddPathParts(parts, localRel, false);
                int localCount = parts.Count;      // 本地目录层数（= 包节点在 parts 中的下标）
                // ② 包文件名（包节点）
                string pkg = "";
                try { pkg = Path.GetFileName(row.ArchivePath); } catch { }
                if (!string.IsNullOrEmpty(pkg)) parts.Add((pkg, true));
                // ③ 包内目录链 + 文件名（严格按包内相对路径）
                AddPathParts(parts, row.RelPath, false);
                if (parts.Count == 0) continue;

                var cur = root;
                string path = "";
                for (int i = 0; i < parts.Count; i++)
                {
                    bool isLeaf = i == parts.Count - 1;
                    string name = parts[i].name;
                    path = path.Length == 0 ? name : path + "/" + name;
                    SearchResultRow node;
                    if (isLeaf)
                    {
                        // 叶子直接用原结果行（不复制、不重建显示字段）
                        node = row;
                        node.IsDirectory = false;
                        node.NodePath = path;
                        node.DisplayName = "📄 " + name;
                        node.NodeKind = SearchNodeKind.File;
                        cur.Children.Add(node);   // ⚠ 必须挂进父节点：漏了这一步树里就只有目录、看不到文件
                    }
                    else
                    {
                        if (!index.TryGetValue(path, out node))
                        {
                            node = new SearchResultRow
                            {
                                IsDirectory = true,
                                NodePath = path,
                                DisplayName = (parts[i].isPkg ? "📦 " : "📁 ") + name,
                                // 三级配色用：本地目录 = 磁盘上的物理目录；包 = .arz/.arc；其余 = 包内目录
                                NodeKind = i < localCount ? SearchNodeKind.LocalDir
                                    : (parts[i].isPkg ? SearchNodeKind.Package : SearchNodeKind.InArchiveDir),
                                Children = new List<SearchResultRow>()
                            };
                            index[path] = node;
                            cur.Children.Add(node);
                        }
                    }
                    cur = node;
                    if (i + 1 > maxDepth) maxDepth = i + 1;
                }
            }

            _searchTreeMaxDepth = maxDepth;
            SortSearchTree(root.Children);
            return new List<SearchResultRow> { root };
        }

        /// <summary>拆包内/本地路径为层级片段（同时兼容 / 与 \）</summary>
        private static void AddPathParts(List<(string name, bool isPkg)> parts, string path, bool isPkg)
        {
            if (string.IsNullOrEmpty(path)) return;
            foreach (string p in path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
                parts.Add((p, isPkg));
        }

        /// <summary>取包文件相对搜索目录的目录部分（用于树形在包节点之上还原本地目录层级）</summary>
        private static string GetLocalRelativeDir(string searchDir, string archivePath)
        {
            if (string.IsNullOrEmpty(searchDir) || string.IsNullOrEmpty(archivePath)) return "";
            try
            {
                string dir = Path.GetDirectoryName(archivePath) ?? "";
                if (dir.Length == 0) return "";
                string full = Path.GetFullPath(dir).TrimEnd('\\', '/');
                string rootFull = Path.GetFullPath(searchDir).TrimEnd('\\', '/');
                if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = full.Substring(rootFull.Length).Trim('\\', '/');
                    return rel == "." ? "" : rel;
                }
            }
            catch { }
            return "";
        }

        /// <summary>递归排序：同层"目录在前、文件在后"，各自按显示名升序（大小写不敏感）</summary>
        private static void SortSearchTree(List<SearchResultRow> nodes)
        {
            if (nodes == null || nodes.Count == 0) return;
            nodes.Sort((a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
                return string.Compare(a.DisplayName ?? a.File ?? "", b.DisplayName ?? b.File ?? "",
                    StringComparison.OrdinalIgnoreCase);
            });
            foreach (var n in nodes)
                if (n.Children != null && n.Children.Count > 0) SortSearchTree(n.Children);
        }

        // ═══════════ 搜索历史浮层（目录 / 关键词；自绘·非弹窗、非表格）═══════════
        //
        // 为什么不用 AntdUI.Dropdown 做「历史下拉」：`SelectItem` 没有"条目内动作按钮"这种成员，
        // 而 `Dropdown.ItemClick` 只回调 value（不带坐标）→ 整条记录只有一个热区，
        // 无法区分"点文字=使用"与"点图钉/×=置顶/清理"（逐条清理是用户明确要求）。
        // 改用 `AntdUI.Popover` 承载**自绘列表**：`LayeredFormPopover` 的 CloseMode=Click 语义是
        // "点击浮层**外**区域才关闭"，所以点浮层内的置顶/清理按钮不会把浮层关掉 —— 这正是逐条操作的前提。

        /// <summary>浮层里不滚动显示的最大行数（超过则出现滚动条；批23 实测 20 行在小屏会超出屏幕 → 收到 10）</summary>
        private const int SearchHistoryVisibleRows = 10;

        /// <summary>
        /// 内置「快速选取」项（**仅关键词浮层**显示：不可删/不可置顶/不计入清空）。
        /// <para>清单的单一真源在 <see cref="SettingsManager.SearchReservedKeywords"/> —— 同一份清单也被
        /// 数据层用来**精确屏蔽**（这两项永不写入关键词历史，否则会与内置分组重复）。</para>
        /// </summary>
        private static string[] SearchBuiltinNameItems => SettingsManager.SearchBuiltinNameValues;

        /// <summary>
        /// 取内容条件的**第一个正向词**（供"打开文件后在编辑器里定位"用）。
        /// <para>多关键词语法（与/或/短语）对编辑器的 `FindNext` 没有意义，所以只带一个词过去。</para>
        /// </summary>
        private static string FirstPositiveTerm(string condition)
        {
            var groups = SearchCondition.Parse(condition, allowWildcard: true);
            foreach (var g in groups)
                foreach (var t in g)
                {
                    if (string.IsNullOrEmpty(t.Text)) continue;
                    if (!t.Wildcard) return t.Text;
                    // 通配项：只把**词头的字面量段**交给编辑器（FindNext 是字面查找，带 * / ? 永远找不到）
                    int end = 0;
                    while (end < t.Text.Length && t.Text[end] != '*' && t.Text[end] != '?') end++;
                    if (end > 0) return t.Text.Substring(0, end);
                }
            return null;
        }

        /// <summary>
        /// **v 级反查**（批23，用户定稿）：把内容包含条件对语言包**每条「k=v」的 v 整体求值** ——
        /// 复用引擎 <see cref="SearchCondition.MatchLine"/>（与/或/引号/通配同一套语义），
        /// v 满足条件组 → 该条目 tag 收集。多词「与」要求**同一个 v** 同时含各词
        /// （原逐词反查是条目级并集，搜「强化 恶魔」会混入只含其一的条目 → 结果污染，已废）。
        /// <para>勾「全」= <c>MatchLine(exact:true)</c>：v 与词元**完全相等**才命中（规则 2/4 口径）。
        /// 排除条件同样对 v 用 MatchLine 求值（先包含后排除，与/或语义与排除框一致）。</para>
        /// </summary>
        /// <param name="langDict">语言包字典（tag → 译名 v）</param>
        /// <param name="contentCond">内容包含条件原文（与内容框同一套语法）</param>
        /// <param name="contentExCond">内容排除条件原文（可空）</param>
        /// <param name="exactMatch">勾「全」：v 与词元完全相等才命中</param>
        /// <param name="maxTags">收集上限（防译名过泛拖慢整轮搜索）</param>
        /// <param name="truncated">是否发生截断（状态行提示用）</param>
        /// <returns>命中的 tag 列表（按 key 排序，稳定顺序）；无命中返回空表</returns>
        private static List<string> BuildLangReverseTagList(
            Dictionary<string, string> langDict, string contentCond, string contentExCond,
            bool exactMatch, int maxTags, out bool truncated)
        {
            truncated = false;
            var tags = new List<string>();
            var groups = SearchCondition.Parse(contentCond, allowWildcard: true);
            if (langDict == null || langDict.Count == 0 || groups.Count == 0) return tags;
            var exGroups = SearchCondition.Parse(contentExCond, allowWildcard: true);
            int cap = maxTags + 1;
            foreach (var kv in langDict)
            {
                string v = kv.Value;
                if (string.IsNullOrEmpty(v)) continue;
                if (!SearchCondition.MatchLine(v, groups, caseSensitive: false, out _, exactMatch, isDbr: false)) continue;
                // 排除：v 满足排除条件组 → 该条目剔除（MatchLine 语义，与内容排除框同一套求值）
                if (exGroups.Count > 0 && SearchCondition.MatchLine(v, exGroups, caseSensitive: false, out _)) continue;
                tags.Add(kv.Key);
                if (tags.Count >= cap) break;
            }
            if (tags.Count > maxTags)
            {
                truncated = true;
                tags.RemoveRange(maxTags, tags.Count - maxTags);
            }
            tags.Sort(StringComparer.Ordinal);
            return tags;
        }

        /// <summary>
        /// 「查看匹配结果」：按当前「内容」条件做 **v 级反查**（与实际搜索同一函数
        /// <see cref="BuildLangReverseTagList"/> —— 预览所见即搜索所得），弹出**非阻塞**窗口逐行显示命中的
        /// 语言包条目（一行一条 `tag=译名`），关键词以危险色高亮。
        /// <para>**零结果也弹窗**（用户定稿）：结果为 0 时照常弹出、内容区空白、标题显示 0 条 ——
        /// 不再"只闪一下状态行"，避免误以为没点上。</para>
        /// <para>窗口规格（用户定稿）：可移动 / 可缩放，宽高为显示器的一半；只有「确定」按钮 + 关闭按钮；
        /// 内容区只保留竖向滚动条、禁止横向滚动、自动换行（Scintilla `WrapMode.Word` + `HScrollBar=false`）。
        /// 打开期间主界面可继续操作（`dlg.Show(owner)` 非阻塞）。</para>
        /// </summary>
        private async void ShowLangReverseMatches()
        {
            string contentCond = _searchContentBox.Text?.Trim();
            var words = CollectPositiveTerms(contentCond);   // 供「未输入词」守卫 + 弹窗内高亮（词没出现自然不高亮）
            string contentExCond = _searchContentExcludeBox.Text?.Trim();
            if (!string.IsNullOrEmpty(contentExCond)) contentExCond = contentExCond.Replace('\\', '/');
            if (words.Count == 0)
            {
                _searchStatusLabel.Text = LanguageManager.Instance.GetText("SearchTab.LangSearchNeedWords",
                    "请先在「内容」框输入关键词，再查看匹配结果");
                return;
            }

            // 后台：构建语言包字典 + v 级反查（与实际搜索同一函数，保证两处口径一致）
            List<(string tag, string value)> rows = null;
            bool noPack = false;
            var cfg = new AntdUI.Spin.Config { Text = LanguageManager.Instance.GetText("SearchTab.LangSearchLoading", "反查中...") };
            await AntdUI.Spin.open(this, cfg, async c =>
            {
                await Task.Run(() =>
                {
                    var dict = LocalizationHelper.MergeLanguagePacks(SettingsManager.ActivePackKeys);
                    if (dict == null || dict.Count == 0) { noPack = true; return; }
                    var tags = BuildLangReverseTagList(dict, contentCond, contentExCond,
                        _searchExactMatch != null && _searchExactMatch.Checked,
                        SearchLangReverseMaxTags, out _);
                    rows = tags.Select(t => (t, dict[t])).ToList();   // 一行一条 tag=译名
                });
            });
            if (noPack)
            {
                _searchStatusLabel.Text = LanguageManager.Instance.GetText("SearchTab.ErrNoLangPack",
                    "未启用语言包，无法本地化反查：请在「设置 → 语言包」中导入并勾选");
                return;
            }
            rows ??= new List<(string, string)>();
            if (rows.Count == 0)
                _searchStatusLabel.Text = string.Format(
                    LanguageManager.Instance.GetText("SearchTab.LangSearchNoHit",
                        "语言包中没有匹配「{0}」的译名，本次按原关键词搜索"),
                    string.Join("、", words));   // 零结果也弹窗（用户定稿），状态行仅作旁注

            // ── 弹窗（非阻塞）：显示器 50% 宽高、可移动缩放、只读内容区 + 底部「确定」──
            var dlg = new Form
            {
                Text = string.Format(LanguageManager.Instance.GetText("SearchTab.LangSearchDialogTitle",
                    "本地化反查 · 匹配结果（{0} 条）"), rows.Count),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,       // 可调整位置和尺寸
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = ThemeManager.BgContainer,
                KeyPreview = false
            };
            var screen = Screen.PrimaryScreen.Bounds;
            dlg.Size = new Size(screen.Width / 2, screen.Height / 2);   // 宽高 = 显示器一半

            var okBtn = ButtonFactory.Create(
                LanguageManager.Instance.GetText("SearchTab.LangSearchOk", "确定"), AntdUI.TTypeMini.Primary, false, "SearchTab.LangSearchOk");
            okBtn.Dock = DockStyle.Right;
            okBtn.Margin = new Padding(0, 6, 10, 6);
            okBtn.Click += (s, e) => dlg.Close();

            var bottomBar = new AntdUI.Panel { Dock = DockStyle.Bottom, Height = DpiManager.S(44), Back = Color.Transparent };
            bottomBar.Controls.Add(okBtn);

            // 内容区：只读 Scintilla（自动换行 + 仅竖向滚动），与编辑器日志同一套样式管线
            var sci = new ScintillaNET.Scintilla
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                TabWidth = 4,
                UseTabs = false,
                Technology = ScintillaNET.Technology.Default,  // GDI 渲染（定稿）：DirectWrite 模式下缺字形的字体回退常落日文字形，GDI 走字体链接回退宋体无此问题
                FontQuality = ScintillaNET.FontQuality.LcdOptimized   // ClearType 抗锯齿：GDI 下的最佳清晰度（仅 GDI 生效，DirectWrite 模式忽略此项）
            };
            ScintillaSyntax.ApplyReadOnlyLog(sci, FontHelper.MonoFontName, (int)(9f * DpiManager.Scale));
            sci.Margins[0].Width = 0;            // 隐藏行号边距

            dlg.Controls.Add(sci);               // Fill 先加
            dlg.Controls.Add(bottomBar);         // Bottom

            // 文本填充（临时关只读）；一行一条 tag=译名，并记录每条的字符区间（供"行下划线"分隔用）
            sci.ReadOnly = false;
            var entrySb = new System.Text.StringBuilder();
            var entryRanges = new List<(int start, int len)>();
            foreach (var r in rows)
            {
                int start = entrySb.Length;
                entrySb.Append($"{r.tag}={r.value}");
                entryRanges.Add((start, entrySb.Length - start));
                entrySb.Append("\r\n");
            }
            sci.Text = entrySb.ToString();
            sci.ReadOnly = true;

            // 每条匹配行下方画一条**下划线**分隔（StraightBox + Under = 文字下方的横线，灰色）
            const int IndSep = 1;
            sci.Indicators[IndSep].Style = ScintillaNET.IndicatorStyle.StraightBox;
            sci.Indicators[IndSep].Under = true;
            sci.Indicators[IndSep].ForeColor = ThemeManager.BorderColor;
            sci.Indicators[IndSep].Alpha = 255;
            sci.IndicatorCurrent = IndSep;
            foreach (var (start, len) in entryRanges)
                sci.IndicatorFillRange(start, len);

            // 关键词高亮：危险色（红）。Scintilla 搜索默认**不区分大小写**，与反查口径一致；
            // 位置以【字符】计（Scintilla5.NET 的文档位置是 UTF-16 code unit，见 wiki 6.3.0 ⑥）
            const int Ind = 0;
            sci.IndicatorCurrent = Ind;
            sci.Indicators[Ind].Style = ScintillaNET.IndicatorStyle.TextFore;
            sci.Indicators[Ind].ForeColor = ThemeManager.Modified;
            foreach (string word in words)
            {
                int from = 0;
                while (true)
                {
                    sci.TargetStart = from;
                    sci.TargetEnd = sci.TextLength;
                    sci.SearchFlags = ScintillaNET.SearchFlags.None;
                    int found = sci.SearchInTarget(word);
                    if (found < 0) break;
                    sci.IndicatorFillRange(sci.TargetStart, sci.TargetEnd - sci.TargetStart);
                    from = sci.TargetEnd;
                }
            }

            dlg.Show(this);   // 非阻塞：主界面可继续操作
        }

        /// <summary>
        /// 取内容条件的**非通配**正向词元（本地化反查用，方案 plan_20260930 §5.7）：
        /// 引号短语参与反查（它就是字面量，决策点 ⑨）；**通配词元跳过**（决策点 ⑦——Value 匹配没有通配语义）；
        /// 去重（同一词多组出现只反查一次）；空条件返回空表。
        /// <para>注意与 <see cref="FirstPositiveTerm"/> 的差异：后者会把通配项截成"词头字面量段"供编辑器
        /// 字面查找，反查**不要**这个行为——通配词元整跳。</para>
        /// </summary>
        private static List<string> CollectPositiveTerms(string condition)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(condition)) return result;
            foreach (var g in SearchCondition.Parse(condition, allowWildcard: true))
                foreach (var t in g)
                {
                    if (string.IsNullOrEmpty(t.Text) || t.Wildcard) continue;
                    if (!result.Contains(t.Text)) result.Add(t.Text);
                }
            return result;
        }

        /// <summary>
        /// 搜索前智能警告：内容条件非空、而**文件名条件限定的扩展名全部不在内容可读白名单**里
        /// （如只写了 `*.mp4`）→ 结果必然为空。提前拦下，避免白等一次全量扫描。
        /// </summary>
        /// <returns>true = 已提示并拦下本次搜索</returns>
        private bool WarnIfContentImpossible(string nameCondition, string contentCondition)
        {
            if (string.IsNullOrWhiteSpace(contentCondition) || string.IsNullOrWhiteSpace(nameCondition)) return false;
            var exts = SearchCondition.ExtractExtensions(
                SearchCondition.Parse(nameCondition, allowWildcard: true));
            if (exts.Count == 0) return false;                                     // 没限定扩展名 → 无从判断
            if (exts.Any(e => TextFileHelper.IsTextFileExtension(e))) return false; // 有可读格式 → 正常搜
            _searchStatusLabel.Text = string.Format(
                LanguageManager.Instance.GetText("SearchTab.WarnContentImpossible",
                    "文件名条件限定的格式（{0}）不支持内容搜索，结果必然为空"),
                string.Join(" / ", exts.Distinct()));
            return true;
        }

        /// <summary>造一个「历史」按钮（外观对齐原「快速选取」：文字 + 右侧下拉箭头）</summary>
        private AntdUI.Button CreateSearchHistoryButton(string nameKey, string fallback)
        {
            var btn = ButtonFactory.Create(LanguageManager.Instance.GetText(nameKey, fallback),
                AntdUI.TTypeMini.Default, false, nameKey);
            btn.IconSvg = AntdUI.SvgDb.IcoDown;
            btn.IconPosition = AntdUI.TAlignMini.Right;
            btn.Dock = DockStyle.Right;
            btn.Margin = new Padding(6, 0, 0, 0);
            return btn;
        }

        /// <summary>点「历史」按钮：同一个按钮再点一次 = 收起；换另一个按钮 = 收起后开新的</summary>
        private void ToggleSearchHistoryPopover(AntdUI.Button anchor, SearchHistoryKind kind)
        {
            bool sameOpen = _searchHistoryPopover != null && !_searchHistoryPopover.IsDisposed
                            && _searchHistoryPopover.Visible && _searchHistoryPopoverKind == kind;
            CloseSearchHistoryPopover();
            if (sameOpen) return;
            BuildSearchHistoryPopover(anchor, kind);
        }

        /// <summary>收起历史浮层（外部点击由 Popover 自己关闭，这里只是清引用 + 主动关）</summary>
        private void CloseSearchHistoryPopover()
        {
            try { (_searchHistoryPopover as AntdUI.ILayeredForm)?.IClose(); } catch { }
            _searchHistoryPopover = null;
            _searchHistoryListPanel = null;
            _searchHistoryCountLabel = null;
            _searchHistoryClearBtn = null;
        }

        private void BuildSearchHistoryPopover(AntdUI.Button anchor, SearchHistoryKind kind)
        {
            if (anchor == null || anchor.IsDisposed) return;
            var list = SettingsManager.GetSearchHistory(kind);

            // 行数 = 历史条数（+ 文件名浮层的内置扩展名分组标题与两项）；不滚动上限 10 行（批23，原 20 超屏）
            int rows = list.Count + (kind == SearchHistoryKind.Name ? SearchBuiltinNameItems.Length + 1 : 0);
            int visible = Math.Max(1, Math.Min(rows, SearchHistoryVisibleRows));
            int rowH = DpiManager.S(30);
            int headerH = DpiManager.S(40);
            var size = new Size(DpiManager.S(kind == SearchHistoryKind.Dir ? 560 : 460), headerH + visible * rowH);

            var root = new AntdUI.Panel { Back = Color.Transparent, Size = size };

            // ── 顶部一行：「清空全部」（左） + 「共 N 条」（右）──
            var header = new AntdUI.Panel { Dock = DockStyle.Top, Height = headerH, Back = Color.Transparent };
            _searchHistoryClearBtn = ButtonFactory.Create(
                LanguageManager.Instance.GetText("SearchTab.HistoryClearAll", "清空全部"),
                AntdUI.TTypeMini.Error, false, "SearchTab.HistoryClearAll");
            _searchHistoryClearBtn.Dock = DockStyle.Left;
            _searchHistoryClearBtn.Margin = new Padding(0);
            _searchHistoryClearBtn.Click += (s, e) =>
            {
                SettingsManager.ClearUnpinnedSearchHistory(kind);
                FillSearchHistoryRows(kind);   // 就地重建（置顶项保留）
            };
            _searchHistoryCountLabel = new AntdUI.Label
            {
                Dock = DockStyle.Right,
                AutoSize = false,
                Width = DpiManager.S(130),
                TextAlign = ContentAlignment.MiddleRight,
                Font = FontHelper.UiFont(9),
                ForeColor = ThemeManager.TextTertiary
            };
            header.Controls.Add(_searchHistoryClearBtn);
            header.Controls.Add(_searchHistoryCountLabel);

            // ── 列表容器（超过 20 行时出现滚动条）──
            _searchHistoryListPanel = new AntdUI.Panel { Dock = DockStyle.Fill, AutoScroll = true, Back = Color.Transparent };

            root.Controls.Add(_searchHistoryListPanel);   // Fill 先加
            root.Controls.Add(header);                    // Top 后加

            FillSearchHistoryRows(kind);

            _searchHistoryPopoverKind = kind;
            _searchHistoryPopover = AntdUI.Popover.open(anchor, root, AntdUI.TAlign.Bottom);
        }

        /// <summary>重建浮层内的列表（置顶/清理/清空后就地刷新；不改浮层尺寸 —— 内容只会变少或重排）</summary>
        private void FillSearchHistoryRows(SearchHistoryKind kind)
        {
            var listPanel = _searchHistoryListPanel;
            if (listPanel == null || listPanel.IsDisposed) return;
            var list = SettingsManager.GetSearchHistory(kind);

            listPanel.SuspendLayout();
            try
            {
                listPanel.Controls.Clear();

                var items = new List<Control>();   // 显示顺序（自上而下）
                // ① 置顶组（列表不变量保证置顶在前；显示顺序 = 列表顺序）
                foreach (var e in list.Where(x => x.Pinned))
                    items.Add(BuildSearchHistoryRow(e.Value, pinned: true, kind));
                // ② 文件名浮层：内置扩展名分组（原「快速选取」的 .cnv/.qst，一键填入 *.cnv / *.qst）
                if (kind == SearchHistoryKind.Name)
                {
                    items.Add(BuildSearchHistoryGroupLabel(LanguageManager.Instance.GetText("SearchTab.QuickPick", "快速选取")));
                    foreach (string ext in SearchBuiltinNameItems)
                        items.Add(BuildSearchHistoryRow(ext, pinned: false, kind, builtin: true));
                }
                // ③ 普通历史
                foreach (var e in list.Where(x => !x.Pinned))
                    items.Add(BuildSearchHistoryRow(e.Value, pinned: false, kind));
                // 空态（文件名浮层有内置项，故只在目录/内容浮层出现）
                if (list.Count == 0 && kind != SearchHistoryKind.Name)
                    items.Add(BuildSearchHistoryEmptyLabel());

                // Dock=Top 时"后加的在上方" → 反向加入才能得到自上而下的显示顺序
                for (int i = items.Count - 1; i >= 0; i--) listPanel.Controls.Add(items[i]);

                // 顶部计数 / 清空按钮可用性（置顶不计入清空，故普通项为 0 时置灰）
                int unpinned = list.Count(e => !e.Pinned);
                if (_searchHistoryCountLabel != null && !_searchHistoryCountLabel.IsDisposed)
                    _searchHistoryCountLabel.Text = string.Format(
                        LanguageManager.Instance.GetText("SearchTab.HistoryCount", "共 {0} 条"), list.Count);
                if (_searchHistoryClearBtn != null && !_searchHistoryClearBtn.IsDisposed)
                    _searchHistoryClearBtn.Enabled = unpinned > 0;
            }
            finally { listPanel.ResumeLayout(); }
        }

        /// <summary>一行历史：[文本(点击=填入)] [📌 置顶] [✕ 清理]（置顶在清理左侧；内置快捷项无按钮）</summary>
        private Control BuildSearchHistoryRow(string value, bool pinned, SearchHistoryKind kind, bool builtin = false)
        {
            var row = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(30), Back = Color.Transparent };

            var text = new AntdUI.Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                AutoEllipsis = true,                 // 长路径省略，不撑破浮层
                TextAlign = ContentAlignment.MiddleLeft,
                Font = FontHelper.UiFont(9),
                Text = value,
                Cursor = Cursors.Hand,
                // 置顶 = 主色；普通项（含内置快捷项）= 默认色
                ForeColor = pinned ? ThemeManager.Primary : (Color?)null
            };
            // 内置快捷项同样"点一下就填入"（它本就是原「快速选取」的功能）；两者差别只在有无置顶/清理按钮
            text.Click += (s, e) => UseSearchHistoryValue(kind, value);
            row.Controls.Add(text);   // Fill 先加

            if (!builtin)
            {
                // 置顶/取消置顶（**在清理左侧** → Dock=Right 时先加）
                var btnPin = CreateSearchHistoryIconButton(
                    pinned ? AntdUI.SvgDb.IcoPinFilled : AntdUI.SvgDb.IcoPinOutlined,
                    LanguageManager.Instance.GetText(pinned ? "SearchTab.HistoryUnpin" : "SearchTab.HistoryPin",
                        pinned ? "取消置顶" : "置顶"));
                if (pinned) btnPin.ForeColor = ThemeManager.Primary;   // 已置顶：图钉也上主色
                btnPin.Click += (s, e) =>
                {
                    SettingsManager.ToggleSearchHistoryPin(kind, value);
                    FillSearchHistoryRows(kind);   // 就地重建：置顶项会移到最上方
                };
                row.Controls.Add(btnPin);

                // 清理（**最右** → 后加）
                var btnClear = CreateSearchHistoryIconButton(AntdUI.SvgDb.IcoClose,
                    LanguageManager.Instance.GetText("Common.Clear", "清除"));
                btnClear.Click += (s, e) =>
                {
                    SettingsManager.RemoveSearchHistoryValue(kind, value);
                    FillSearchHistoryRows(kind);
                };
                row.Controls.Add(btnClear);
            }
            return row;
        }

        /// <summary>行内的小图标按钮（Ghost 弱化视觉；Dock=Right，后加的更靠右）</summary>
        private AntdUI.Button CreateSearchHistoryIconButton(string iconSvg, string tip)
        {
            var btn = new AntdUI.Button
            {
                IconSvg = iconSvg,
                Text = "",
                AutoSize = false,
                Ghost = true,
                Width = DpiManager.S(26),
                Height = DpiManager.S(24),
                Dock = DockStyle.Right,
                Margin = new Padding(0),
                Cursor = Cursors.Hand
            };
            // 悬停提示（AntdUI 扩展器组件；任何失败都不影响功能）
            try
            {
                if (_searchHistoryTip == null) _searchHistoryTip = new AntdUI.TooltipComponent();
                if (!string.IsNullOrEmpty(tip)) _searchHistoryTip.SetTip(btn, tip);
            }
            catch { }
            return btn;
        }

        /// <summary>分组标题（如「快速选取」）</summary>
        private Control BuildSearchHistoryGroupLabel(string text)
        {
            var p = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(24), Back = Color.Transparent };
            p.Controls.Add(new AntdUI.Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = FontHelper.UiFont(9),
                ForeColor = ThemeManager.TextTertiary,
                Text = text
            });
            return p;
        }

        /// <summary>空态提示</summary>
        private Control BuildSearchHistoryEmptyLabel()
        {
            var p = new AntdUI.Panel { Dock = DockStyle.Top, Height = DpiManager.S(30), Back = Color.Transparent };
            p.Controls.Add(new AntdUI.Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = FontHelper.UiFont(9),
                ForeColor = ThemeManager.TextTertiary,
                Text = LanguageManager.Instance.GetText("SearchTab.HistoryEmpty", "暂无历史记录")
            });
            return p;
        }

        /// <summary>点历史条目：**只填入对应输入框**（不直接搜索 —— 三套历史各记各的，组合起来未必是一次有效搜索），随后收起浮层</summary>
        private void UseSearchHistoryValue(SearchHistoryKind kind, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var box = kind == SearchHistoryKind.Name ? _searchNameBox
                    : kind == SearchHistoryKind.Content ? _searchContentBox
                    : _searchDirBox;
            if (box != null)
            {
                box.Text = value;
                box.Focus();
            }
            CloseSearchHistoryPopover();
        }

        /// <summary>跨包搜索的并行度（与编辑器同模板扫描同口径：min(CPU, 4)）</summary>
        private static readonly int SearchParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));

        /// <summary>
        /// 行**左容器**（外层 `searchRows` 第 1 列 75% 单元格里的嵌套 TLP，用户定稿的嵌套结构）：
        /// `[输入框(Percent 100) ｜ 开关组(AutoSize，可选) ｜ 历史/浏览(AutoSize)]`。
        /// <para>输入框 `Percent 100` = 吃掉左容器扣除两个 AutoSize 列后的**全部剩余宽度** →
        /// 各行组件多少不同，差值全由**输入框**吸收（输入框长短不一，但三行左容器同宽、排除框对齐）。</para>
        /// <para>为什么嵌套：排除框右侧没有其它组件 → 外层直接 75/25（无 AutoSize 列，比例不漂移）；
        /// 若把开关/按钮列提到外层，75/25 要先扣 AutoSize 再分配 → 各行排除框错位（第一版实测歪的根因）。</para>
        /// </summary>
        private static TableLayoutPanel NewSearchLeftPanel(bool withSwitches)
        {
            var tlp = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                BackColor = Color.Transparent,
                ColumnCount = withSwitches ? 3 : 2,
                RowCount = 1
            };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));                  // 输入框：自适应吃剩余
            if (withSwitches)
                tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));                   // 行内开关组
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));                       // 历史/浏览按钮
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            return tlp;
        }

        /// <summary>
        /// 行内开关组容器（横向流式；WinForms FlowLayoutPanel **添加顺序即视觉顺序**，无 AntdUI 倒序坑）。
        /// <para>`AutoSize=true + Dock=None + Anchor=None`（批21 实测修复三件套）：
        /// ① AutoSize 列按**内容实际宽度**定列宽 —— Dock=Fill 时首选宽度会退化成默认 200px，
        ///    内容左对齐、右侧出现 80px 大空隙（实测 77~125px）；</para>
        /// <para>② Anchor=None = 在 42px 行内**自动垂直居中** —— 原 Dock=Fill + Padding.Top=9 使按钮中心偏低 6px；</para>
        /// <para>③ Padding 只留左 8（与输入框的间距），上下右归零。</para>
        /// <para>⚠ 不用 AntdUI.FlowPanel：其显示顺序为添加顺序的**倒序**（原选项行踩过），
        /// 组内只有复选框与普通 Label（无 Segmented 等 0×0 / 被放大坑控件，见 wiki 6.3.0 ①③⑥）。</para>
        /// </summary>
        private static FlowLayoutPanel NewSearchSwitchFlow()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.None,           // 非填充：AutoSize 列直接量内容宽（Fill 会退化为默认 200px）
                Anchor = AnchorStyles.None,      // TLP 单元格内自动垂直居中
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,                 // 首选宽度 = 内容实际宽度（列宽测量依据）
                AutoScroll = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(8, 0, 0, 0)
            };
        }

        /// <summary>
        /// 行内开关复选框：**短标签** + 悬停 tooltip（完整说明），<see cref="Control.AutoSize"/> 必开
        /// （wiki 6.3.0 ⑥ AntdUI 默认 0×0 陷阱）。标签与 tooltip 的 key/回退文案登记到
        /// <see cref="_searchSwitchTips"/>，切语言时由 <see cref="RefreshSearchSwitchTips"/> 统一重刷。
        /// </summary>
        private AntdUI.Checkbox CreateSearchSwitch(string key, string fallback, string tipKey, string tipFallback, bool isChecked)
        {
            var cb = new AntdUI.Checkbox
            {
                Name = key,
                Text = LanguageManager.Instance.GetText(key, fallback),
                Checked = isChecked,
                AutoSize = true,
                Margin = new Padding(0, 0, 6, 0),   // 开关间距（批21 实测反馈：10 过宽 → 6）
                Font = FontHelper.UiFont(9),
                ForeColor = ThemeManager.Text,
                BackColor = Color.Transparent
            };
            SetSearchSwitchTip(cb, tipKey, tipFallback);
            return cb;
        }

        /// <summary>给控件挂行内开关 tooltip，并登记 key/回退文案（供切语言重刷）。</summary>
        private void SetSearchSwitchTip(Control ctl, string key, string fallback)
        {
            _searchSwitchTips.Add((ctl, key, fallback));
            if (_searchSwitchTip == null) _searchSwitchTip = new AntdUI.TooltipComponent();
            _searchSwitchTip.SetTip(ctl, LanguageManager.Instance.GetText(key, fallback));
        }

        /// <summary>
        /// 行内开关 tooltip 随语言切换重刷：tooltip 文本挂在 TooltipComponent 上、不在 RefreshControlText
        /// 的控件树覆盖范围内（wiki 6.6 同类问题）。
        /// </summary>
        private void RefreshSearchSwitchTips()
        {
            if (_searchSwitchTip == null) return;
            for (int i = 0; i < _searchSwitchTips.Count; i++)
            {
                (Control ctl, string key, string fallback) = _searchSwitchTips[i];
                if (ctl == null || ctl.IsDisposed) continue;
                _searchSwitchTip.SetTip(ctl, LanguageManager.Instance.GetText(key, fallback));
            }
        }

        /// <summary>历史/浏览按钮组（行左容器 AutoSize 列的直接子项）：横向流式、随按钮内容撑宽。
        /// <para>与开关组同一套三件套（批21 实测修复）：`AutoSize=true + Dock=None + Anchor=None` =
        /// 列宽量内容实际宽（不再退化成 200px 默认宽留大空隙）+ 42px 行内自动垂直居中（原 Padding.Top=9 偏低 6px）。</para>
        /// <para>右 Padding 14 = 与右侧排除框的间距（目标 10~20px，实测收敛到 14）；左右 6 = 与相邻组件的间距。</para></summary>
        private static FlowLayoutPanel NewSearchMiddleRow(params Control[] buttons)
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.None,           // 非填充：AutoSize 列直接量内容宽（Fill 会退化为默认 200px）
                Anchor = AnchorStyles.None,      // TLP 单元格内自动垂直居中
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,                 // 首选宽度 = 内容实际宽度（列宽测量依据）
                AutoScroll = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(6, 0, 14, 0)
            };
            foreach (Control b in buttons)
            {
                b.Dock = DockStyle.None;        // 流式顺序排布（原 Dock 是给旧行容器用的）
                flow.Controls.Add(b);
            }
            return flow;
        }

        /// <summary>
        /// 执行跨包搜索：校验 → 枚举包 → 包级并行搜索 → 命中增量出表 → 状态/统计收尾。
        /// <para>取消：<see cref="CancelActiveOperation"/>（取消按钮）→ GuiProgressCallback 的令牌。</para>
        /// </summary>
        private async void ExecuteArchiveSearch()
        {
            // ── 1) 输入校验（四项，逐项给明确提示）──
            string dir = _searchDirBox.Text?.Trim();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                _searchStatusLabel.Text = LanguageManager.Instance.GetText("SearchTab.ErrDir", "请选择要搜索的目录（目录不存在）");
                return;
            }
            // 文件名条件：把 Windows 习惯的 `\` 归一为包内路径分隔符 `/`
            // （包内记录名一律用正斜杠，如 records/items/a.dbr，用户输入 "records\items" 也应命中）
            string nameCond = _searchNameBox.Text?.Trim();
            if (!string.IsNullOrEmpty(nameCond)) nameCond = nameCond.Replace('\\', '/');
            string contentCond = _searchContentBox.Text?.Trim();
            // 校验：目录必填 + **文件名 / 内容至少一项**（两者都空 = 没有筛选条件，无意义）
            if (string.IsNullOrEmpty(nameCond) && string.IsNullOrEmpty(contentCond))
            {
                _searchStatusLabel.Text = LanguageManager.Instance.GetText("SearchTab.ErrNoCondition", "请至少填写「文件名」或「内容」其中一项");
                return;
            }
            bool includeArz = _searchIncludeArz.Checked;
            bool includeArc = _searchIncludeArc.Checked;
            if (!includeArz && !includeArc)
            {
                _searchStatusLabel.Text = LanguageManager.Instance.GetText("SearchTab.ErrNoType", "请至少勾选一种包类型（ARZ / ARC）");
                return;
            }
            // 有内容条件 = 走"读文件内容 + 命中行/命中数"这条链（也决定「打开」是打开文件内容）
            bool hasContent = !string.IsNullOrEmpty(contentCond);
            // ── 本地化搜索前置校验（决策点 ④）：开关开 + 有内容条件，但没有任何已启用的语言包 →
            //    明确报错并拒绝搜索，避免"静默空结果"这种最难排查的体验 ──
            if (_searchLangReverse.Checked && hasContent &&
                (SettingsManager.ActivePackKeys == null || SettingsManager.ActivePackKeys.Count == 0))
            {
                _searchStatusLabel.Text = LanguageManager.Instance.GetText("SearchTab.ErrNoLangPack",
                    "未启用语言包，无法本地化搜索：请在「设置 → 语言包」中导入并勾选");
                return;
            }
            // ── 排除条件（方案 §30）：逻辑非整体收敛到这里；**文件级**语义；不算"搜索条件"（见上方校验）──
            // 文件名排除与包含同口径：`\` → `/`（包内路径一律正斜杠）
            string nameExCond = _searchNameExcludeBox.Text?.Trim();
            if (!string.IsNullOrEmpty(nameExCond)) nameExCond = nameExCond.Replace('\\', '/');
            string contentExCond = _searchContentExcludeBox.Text?.Trim();
            // 目录排除：匹配对象是**完整磁盘路径**，`\` 与 `/` 内部归一等价（枚举阶段两侧都归一后再比）
            string dirExCond = _searchDirExcludeBox.Text?.Trim();
            if (!string.IsNullOrEmpty(dirExCond)) dirExCond = dirExCond.Replace('\\', '/');
            // 搜索前智能警告：文件名条件限定的格式都不支持内容搜索 → 结果必然为空，别白等一次全量扫描
            if (WarnIfContentImpossible(nameCond, contentCond)) return;

            // ── 2) 准备结果与进度（枚举也纳入进度：大目录递归枚举很慢，不能同步阻塞 UI）──
            // 记录搜索历史：**目录 / 文件名条件 / 内容条件三套各自独立记录**（不组合）；去重/置顶/上限见 SettingsManager
            SettingsManager.AddSearchHistoryValue(SearchHistoryKind.Dir, dir);
            if (!string.IsNullOrEmpty(nameCond)) SettingsManager.AddSearchHistoryValue(SearchHistoryKind.Name, nameCond);
            if (!string.IsNullOrEmpty(contentCond)) SettingsManager.AddSearchHistoryValue(SearchHistoryKind.Content, contentCond);
            // 打开文件时要在编辑器里定位到内容关键词 → 只把"第一个正向词"带过去（多关键词语法对 FindNext 无意义）
            _searchContentLocateTerm = FirstPositiveTerm(contentCond);
            CloseSearchHistoryPopover();   // 开始搜索时收起历史浮层，避免它盖在结果上

            // R1：搜索中必须平铺（否则流式追加的行会挂进上一次的树结构里）
            if (_searchTreeMode) SwitchSearchViewMode(false);
            ClearSearchResults();
            var progress = new GuiProgressCallback(null, null, _searchStatusLabel);   // 无进度条，只走状态文字
            _activeProgress = progress;
            progress.Reset();
            SetSearchRunning(true);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool cancelled = false;
            string error = null;
            List<string> files = null;

            try
            {
                // ── 3) 异步枚举包文件（只枚举 *.arz / *.arc，其它文件一律不碰）──
                // 枚举阶段没有"总数"可算（要先全遍历一遍才知道），故进度条进入**加载动画态**（不确定进度）；
                // 状态行按时间节流显示"正在遍历：<当前文件夹>（已遍历 N 个文件夹，找到 M 个包）"。
                progress.UpdateProgress(0, LanguageManager.Instance.GetText("SearchTab.ScanningDir", "正在扫描目录…"));
                files = await Task.Run(() => CollectArchiveFiles(dir, includeArz, includeArc,
                    _searchIncludeSubDir.Checked, dirExCond,
                    msg => { try { BeginInvoke((Action)(() => _searchStatusLabel.Text = msg)); } catch { } },
                    progress.Token), progress.Token);

                if (files.Count == 0)
                {
                    _searchStatusLabel.Text = LanguageManager.Instance.GetText("SearchTab.NoArchive", "该目录下未找到匹配的包文件（.arz / .arc）");
                    return;
                }

                // ── 4) 搜索（漏斗：先文件名 → 再内容）──
                // 语言包字典每次搜索构建一次（tag → 本地化名）；未启用语言包 → LookupLocalizedTag 回退原始 tag
                // 【本地化搜索】字典构建 + 译名反查一并放**后台线程**（决策点 ⑥：数万条语言包不在 UI 线程重解）
                Dictionary<string, string> langTags;
                string[] contentGroupTags = null;
                if (_searchLangReverse.Checked && hasContent)
                {
                    var built = await Task.Run(() =>
                    {
                        var dict = LocalizationHelper.MergeLanguagePacks(SettingsManager.ActivePackKeys);
                        // v 级反查（批23）：内容条件对每条译名 v 整体求值；「全」联动 = v 完全相等（规则 2/4）
                        var tags = BuildLangReverseTagList(dict, contentCond, contentExCond,
                            _searchExactMatch != null && _searchExactMatch.Checked,
                            SearchLangReverseMaxTags, out var truncated);
                        return new { Dict = dict, Tags = tags, Truncated = truncated };
                    });
                    _searchLangTags = built.Dict;
                    langTags = built.Dict;

                    // 状态行：反查统计（随后会被扫描进度覆盖，故同时落一行日志便于回看）
                    if (built.Tags.Count > 0)
                    {
                        contentGroupTags = built.Tags.ToArray();
                        string stat = string.Format(
                            LanguageManager.Instance.GetText("SearchTab.LangSearchStat",
                                "本地化搜索：命中 {0} 个 tag（与/或作用于同一译名）"),
                            built.Tags.Count);
                        if (built.Truncated)
                            stat += string.Format(LanguageManager.Instance.GetText("SearchTab.LangSearchTruncated",
                                "（命中 tag 过多，仅取前 {0} 个）"), SearchLangReverseMaxTags);
                        _searchStatusLabel.Text = stat;
                        try { LogService.Info($"[search] {stat}"); } catch { }
                    }
                    else
                    {
                        _searchStatusLabel.Text = string.Format(
                            LanguageManager.Instance.GetText("SearchTab.LangSearchNoHit",
                                "语言包中没有匹配「{0}」的译名，本次按原关键词搜索"),
                            string.Join("、", CollectPositiveTerms(contentCond)));
                    }

                    // 打开结果后在编辑器里定位：DBR 里只有 tag 没有译名 → 反查命中时改用**第一个 tag** 定位
                    // （否则编辑器拿中文词去定位，DBR 里根本不存在 → 静默定位失败，wiki 5.14）
                    if (contentGroupTags != null && !string.IsNullOrEmpty(_searchContentLocateTerm))
                        _searchContentLocateTerm = contentGroupTags[0];
                }
                else
                {
                    langTags = LocalizationHelper.MergeLanguagePacks(SettingsManager.ActivePackKeys);
                    _searchLangTags = langTags;
                }
                var options = new ArchiveSearchOptions
                {
                    NameCondition = nameCond,
                    ContentCondition = contentCond,
                    // 排除条件（文件级，方案 §30）：目录排除在**枚举阶段**生效（不进引擎），这里传引擎用的两项
                    NameExcludeCondition = nameExCond,
                    ContentExcludeCondition = contentExCond,
                    // 本地化搜索（方案 plan_20260930 / 批23 v 级反查）：开关关 = null → 引擎行为与引入前逐字节一致
                    ContentGroupTags = contentGroupTags,
                    CaseSensitive = _searchCaseSensitive.Checked,
                    // 完全匹配（批21）：内容包含条件按字面字符串完全相等判定；内容排除不受影响（保持包含匹配）
                    ContentExactMatch = _searchExactMatch != null && _searchExactMatch.Checked,
                    // 内容列：取**前 5 处命中行本身**，以 " / " 分隔（命中数仍统计全部；不是全量，全量在编辑器里看）
                    MaxContentHits = 5,
                    // 扩展列（说明/模板/本地化名称）：只对 .dbr 生效；内容模式本来就读全文，
                    // 文件名模式则要为每条命中额外读一次全文 —— 由「显示说明/模板/本地化名称」开关控制
                    WithDbrMeta = true,   // 固定提取说明/模板/本地化名称（选项已去掉）
                    LanguageTags = langTags
                };

                int foundCount = 0, doneCount = 0, failedCount = 0;
                var pending = new List<SearchResultRow>();
                bool flushAll = false;
                void FlushSearchRows()
                {
                    if (pending.Count == 0) return;
                    var arr = pending.ToArray();
                    pending.Clear();
                    _searchAllRows.AddRange(arr);
                    _searchResultRows.AddRange(arr);
                    ShowSearchFilterRow();                        // #1：有结果了才把筛选行露出来
                }
                // Progress<T>：Report 由后台线程调用，但回调统一在 UI 线程（FIFO），与编辑器 StartSearch 同法
                // （声明为 IProgress<T>：Progress<T> 的 Report 是显式接口实现）
                IProgress<ArchiveSearchHit> rowProgress = new Progress<ArchiveSearchHit>(hit =>
                {
                    pending.Add(BuildSearchResultRow(hit, hasContent));
                    foundCount++;
                    if (foundCount <= 100 || pending.Count >= 100 || flushAll) FlushSearchRows();
                });

                int totalPackages = files.Count;

                // ── 包内进度：只为让状态文字"动起来"（不再有进度条，故不再需要百分比）──
                // 单包搜索时外层只在包首尾各报一次；靠这里按记录/条目上报刷新"已扫描 X/Y · 命中 N 条"，
                // 命中数会持续增长 → 用户看得到进展。百分比仅用于节流判定（不显示）。
                int lastInnerPct = 0;
                IProgress<(int current, int total, string path)> innerProgress =
                    new Progress<(int current, int total, string path)>(p =>
                    {
                        double frac = p.total > 0 ? Math.Min(1.0, (double)p.current / p.total) : 1.0;
                        int pct = totalPackages > 0
                            ? (int)Math.Min(99, (doneCount + frac) * 100 / totalPackages)
                            : 100;
                        if (pct < 1) pct = 1;
                        if (pct < lastInnerPct) return;   // 多包并行时各包进度交错 → 只放行单调递增
                        lastInnerPct = pct;
                        progress.UpdateProgress(pct, string.Format(
                            LanguageManager.Instance.GetText("SearchTab.Searching",
                                "已扫描 {0}/{1} 个包 · 命中 {2} 条"),
                            doneCount, totalPackages, foundCount));
                    });

                await Task.Run(() =>
                {
                    try
                    {
                        Parallel.ForEach(files,
                            new ParallelOptions { MaxDegreeOfParallelism = SearchParallelism, CancellationToken = progress.Token },
                            file =>
                            {
                                try
                                {
                                    ArchiveSearcher.SearchArchive(file, options, progress.Token,
                                        progress: innerProgress,   // 包内进度（折算进整体，见上）
                                        onMatch: hit => rowProgress.Report(hit),
                                        onWarning: msg => LogService.Warn($"[search] {msg}"));
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    // 单个包读不了（占用 / 损坏 / 非包）只跳过并计数，不中断整体搜索
                                    System.Threading.Interlocked.Increment(ref failedCount);
                                    try { LogService.Warn($"[search] {file}: {ex.Message}"); } catch { }
                                }
                                finally
                                {
                                    int done = System.Threading.Interlocked.Increment(ref doneCount);
                                    if (!progress.IsCancellationRequested)
                                    {
                                        int pct = totalPackages > 0 ? done * 100 / totalPackages : 100;
                                        progress.UpdateProgress(pct, string.Format(
                                            LanguageManager.Instance.GetText("SearchTab.Searching",
                                                "已扫描 {0}/{1} 个包 · 命中 {2} 条"),
                                            done, totalPackages, foundCount));
                                    }
                                }
                            });
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    catch (Exception ex) { error = ex.Message; }
                });

                // 收尾：把所有剩余挂起行刷出（此时仍在 UI 线程，且队列已被 FIFO 处理完）
                flushAll = true;
                FlushSearchRows();
                sw.Stop();

                if (cancelled)
                {
                    _searchStatusLabel.Text = foundCount > 0
                        ? string.Format(LanguageManager.Instance.GetText("SearchTab.CancelledWithCount", "已取消，已找到 {0} 条"), foundCount)
                        : LanguageManager.Instance.GetText("SearchTab.Cancelled", "搜索已取消");
                }
                else if (error != null)
                {
                    _searchStatusLabel.Text = string.Format(LanguageManager.Instance.GetText("SearchTab.ErrSearch", "搜索异常：{0}"), error);
                    LogService.Error($"[search] {error}");
                }
                else if (foundCount == 0)
                {
                    _searchStatusLabel.Text = LanguageManager.Instance.GetText("SearchTab.NoResult", "未找到匹配结果");
                }
                else
                {
                    _searchStatusLabel.Text = string.Format(LanguageManager.Instance.GetText("SearchTab.FoundCount", "搜索完成，命中 {0} 条"), foundCount);
                }

                UpdateSearchStats(totalPackages, foundCount, failedCount, sw.Elapsed.TotalSeconds);
                if (failedCount > 0)
                    LogService.Warn(string.Format(LanguageManager.Instance.GetText("SearchTab.StatsFailed", "有 {0} 个包读取失败（已跳过）"), failedCount));
            }
            catch (Exception ex)
            {
                _searchStatusLabel.Text = string.Format(LanguageManager.Instance.GetText("SearchTab.ErrSearch", "搜索异常：{0}"), ex.Message);
                LogService.Error($"[search] {ex.Message}");
            }
            finally
            {
                sw.Stop();
                SetSearchRunning(false);
                if (ReferenceEquals(_activeProgress, progress)) _activeProgress = null;
                progress.Dispose();
            }
        }

        /// <summary>
        /// 枚举目录下的包文件（可选含子目录）。
        /// <para>**只枚举 `*.arz` / `*.arc` 两种模式**（不再逐个文件后过滤）—— 目录里有大量非包文件时它是主要耗时来源；
        /// 另外跳过 0 字节文件，返回结果按路径排序保证多次搜索结果顺序稳定。</para>
        /// <para>**自己逐目录遍历**（而不是一次 `EnumerateFiles(AllDirectories)`）：这样才能在遍历过程中
        /// 报"当前正在遍历哪个文件夹"，让进度**动起来**（用户反馈：内容多的目录遍历很久却毫无反馈）。
        /// 状态上报按时间节流（≥120ms 一次），避免上万目录时刷爆 UI 消息队列。
        /// 跳过无权限目录、不跟随符号链接/联接点（防环形）。</para>
        /// </summary>
        /// <param name="dirExcludeCondition">**目录排除**条件（方案 §30）：包文件/子目录的完整磁盘路径命中 → 跳过
        /// （子目录命中 = 整棵子树剪枝）。`\` 与 `/` 内部归一等价；大小写**固定不敏感**。null/空 = 不排除</param>
        /// <param name="onStatus">扫描过程中的进度文字（**在调用线程上回调**，已按时间节流）</param>
        private static List<string> CollectArchiveFiles(string dir, bool includeArz, bool includeArc, bool includeSubDir,
            string dirExcludeCondition = null,
            Action<string> onStatus = null, System.Threading.CancellationToken ct = default)
        {
            var files = new List<string>();
            var patterns = new List<string>(2);
            if (includeArz) patterns.Add("*.arz");
            if (includeArc) patterns.Add("*.arc");

            string rootFull;
            try { rootFull = Path.GetFullPath(dir).TrimEnd('\\', '/'); } catch { rootFull = dir; }

            // 目录排除（方案 §30）：完整磁盘路径（`\`→`/` 归一后）命中任一排除条件 → 跳过；
            // 大小写**固定不敏感**（Windows 路径习惯，不受"大小写敏感"开关影响）
            var dirExGroups = SearchCondition.Parse(dirExcludeCondition, allowWildcard: true);
            bool hasDirEx = dirExGroups.Count > 0;
            bool DirExcluded(string path)
            {
                if (!hasDirEx) return false;
                string full;
                try { full = Path.GetFullPath(path); } catch { full = path; }
                return SearchCondition.MatchName(full.Replace('\\', '/'), dirExGroups, caseSensitive: false);
            }

            var pending = new Stack<string>();
            pending.Push(dir);
            int visitedDirs = 0;
            var sinceReport = System.Diagnostics.Stopwatch.StartNew();
            string curFolder = null;

            void Report(bool force)
            {
                if (onStatus == null) return;
                if (!force && sinceReport.ElapsedMilliseconds < 120) return;
                sinceReport.Restart();
                onStatus(string.Format(LanguageManager.Instance.GetText("SearchTab.ScanningFolder",
                    "正在遍历：{0}（已遍历 {1} 个文件夹，找到 {2} 个包）"),
                    curFolder ?? "", visitedDirs, files.Count));
            }

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string cur = pending.Pop();
                visitedDirs++;
                // 状态里显示"相对搜索目录"的路径（更短更可读；根目录显示为 .）
                try
                {
                    string full = Path.GetFullPath(cur).TrimEnd('\\', '/');
                    curFolder = full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                        ? (full.Length == rootFull.Length ? "." : full.Substring(rootFull.Length).Trim('\\', '/'))
                        : cur;
                }
                catch { curFolder = cur; }
                Report(false);

                foreach (string pattern in patterns)
                {
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(cur, pattern, SearchOption.TopDirectoryOnly))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!ArchiveSearcher.TryGetKind(f, out _)) continue;   // 双保险：按扩展名再判一次
                            try { if (new FileInfo(f).Length == 0) continue; } catch { continue; }
                            if (DirExcluded(f)) continue;                          // 命中目录排除：不扫这个包
                            files.Add(f);
                            Report(false);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        try { LogService.Warn($"[search] 枚举目录失败（{cur} / {pattern}）：{ex.Message}"); } catch { }
                    }
                }

                if (!includeSubDir) continue;
                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(cur))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;   // 防环形
                        }
                        catch { continue; }
                        if (DirExcluded(sub)) continue;   // 命中目录排除：**整棵子树剪枝**（不入栈 → 其下全部跳过）
                        pending.Push(sub);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }   // 无权限等：跳过该目录
            }

            Report(true);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        /// <summary>清空结果区（重新搜索前调用）</summary>
        private void ClearSearchResults()
        {
            _searchAllRows.Clear();
            if (_searchResultRows != null) _searchResultRows.Clear();
            _searchStatsLabel.Text = "";
            HideSearchFilterRow();
        }

        /// <summary>
        /// 筛选行显隐（#1：默认隐藏，**有结果时才显示**，避免没结果时白占一行）。
        /// <para>显示时同步清掉上一次的筛选词，避免"新结果被旧筛选条件隐藏"的困惑。</para>
        /// </summary>
        private void HideSearchFilterRow()
        {
            if (_searchFilterRow == null || _searchFilterRow.IsDisposed) return;
            if (_searchFilterBox != null && !_searchFilterBox.IsDisposed && _searchFilterBox.Text.Length > 0)
                _searchFilterBox.Text = "";      // 触发防抖筛选（此时结果已空，无害）
            _searchFilterRow.Visible = false;
        }

        private void ShowSearchFilterRow()
        {
            if (_searchFilterRow == null || _searchFilterRow.IsDisposed) return;
            if (_searchFilterRow.Visible) return;
            _searchFilterRow.Visible = true;
        }

        /// <summary>搜索运行态切换：搜索按钮 Loading、取消按钮显示/隐藏</summary>
        private void SetSearchRunning(bool running)
        {
            try
            {
                _searchRunning = running;
                _searchButton.Loading = running;
                _searchButton.Enabled = !running;
                _searchCancelButton.Visible = running;
                _searchCancelButton.Parent?.PerformLayout();
                LayoutSearchButtonRow();
                UpdateSearchViewModeButtons();   // #5：搜索中禁用形态切换
            }
            catch { }
        }

        private void LayoutSearchButtonRow()
        {
            var panel = _searchButton?.Parent;
            if (panel == null) return;
            int visibleCount = 0, totalWidth = 0, gap = 10;
            foreach (Control c in panel.Controls)
            {
                if (!c.Visible) continue;
                visibleCount++;
                totalWidth += c.Width;
            }
            if (visibleCount == 0) return;
            totalWidth += gap * (visibleCount - 1);
            int x = (panel.Width - totalWidth) / 2;
            int y = (panel.Height - panel.Controls[0].Height) / 2;
            foreach (Control c in panel.Controls)
            {
                if (!c.Visible) continue;
                c.Location = new Point(x, y);
                x += c.Width + gap;
            }
        }

        /// <summary>把一条命中转成结果表行（显示字段 + 打开/筛选所需的隐藏字段）</summary>
        private SearchResultRow BuildSearchResultRow(ArchiveSearchHit hit, bool hasContent)
        {
            // 时间列口径与编辑器对齐：ARZ 的 LoadArchive 用 ToLocalTime（本地时间），而 ARC 条目名表沿用原值 ——
            // 这是编辑器既有的差异，这里保持"与编辑器逐字一致"，不擅自统一（要改应与编辑器一起改）
            DateTime? time = hit.FileTime;
            if (time.HasValue && hit.Kind == ArchiveKind.Arz) time = time.Value.ToLocalTime();

            return new SearchResultRow
            {
                // 所有进表格的字符串统一过 SafeCellText（去换行/控制字符 + 限长）——
                // 否则超长文本会让 AntdUI 的 GDI+ 测量抛 0x80004005 直接闪退（见 SafeCellText 说明）
                Archive = SafeCellText(hit.ArchivePath),        // 包名显示**完整路径**（便于区分不同目录下的同名包）
                File = SafeCellText(hit.RelPath),
                Size = FormatSize(hit.Size),
                SizeBytes = hit.Size,
                Time = time?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                Description = SafeCellText(hit.Description),
                Localized = SafeCellText(hit.LocalizedName),
                Template = SafeCellText(hit.Template),
                Hits = hasContent && hit.MatchCount > 0 ? hit.MatchCount.ToString() : "",
                // 内容列：**单元格显示单行**（多行整块会被行高居中裁空，见 ContentRaw 注释）；原值留给复制/筛选
                Content = BuildContentLineDisplay(hit.Context),
                ContentRaw = SafeCellText(hit.Context, max: 0, keepNewline: true),
                Action = BuildSearchRowActionCell(),
                ArchivePath = hit.ArchivePath,
                RelPath = hit.RelPath,
                Keyword = hasContent ? _searchContentLocateTerm : null,
                RawTag = hit.RawTag,
                RecordIndex = hit.RecordIndex,
                MatchCount = hit.MatchCount,
                ContentMode = hasContent,
                Hit = hit
            };
        }

        /// <summary>操作列单元格（按当前语言构建；切语言时对已有行重建）</summary>
        private static AntdUI.CellLink[] BuildSearchRowActionCell()
        {
            return new AntdUI.CellLink[]
            {
                new AntdUI.CellButton("open", LanguageManager.Instance.GetText("SearchTab.Open", "打开"), AntdUI.TTypeMini.Primary)
            };
        }

        /// <summary>
        /// 结果表「打开」：定位到结果所属的包并打开编辑器。
        /// <para>包内文件名搜索 → 只在编辑器的**文件列表**里选中并滚动到该行（不进编辑器/源码模式）；</para>
        /// <para>包内文件内容搜索 → 打开该文件内容（关键词定位在后续步骤接入）。</para>
        /// <para>同一个包已在编辑器中打开时只激活原窗口（EditorLauncher 负责），不会重复新建。</para>
        /// </summary>
        private void OnSearchResultCellButtonClick(object sender, AntdUI.TableButtonEventArgs e)
        {
            if (e.Btn == null || e.Btn.Id != "open") return;
            if (e.Record is not SearchResultRow row) return;
            OpenSearchResult(row);
        }

        /// <summary>结果行双击 = 同「打开」按钮</summary>
        private void OnSearchResultCellDoubleClick(object sender, AntdUI.TableClickEventArgs e)
        {
            if (e.Record is not SearchResultRow row) return;
            // 双击到操作列时交给按钮事件处理，避免一次点击走两遍
            string columnKey = e.Column?.Key;
            if (string.Equals(columnKey, "Action", StringComparison.Ordinal)) return;
            OpenSearchResult(row);
        }

        // ═══ 结果表右键菜单（#7：打开 / 复制右键单元格内容）═══
        private SearchResultRow _searchCtxRow;
        private AntdUI.Column _searchCtxColumn;

        /// <summary>
        /// 结果表右键：弹「打开 / 复制」菜单。
        /// <para>AntdUI 的 <c>CellClick</c> **左右键都会触发**（源码里紧随其后才是 `if (e.Button == Left)` 的分支），
        /// 因此右键只需在这里按 `e.Button` 分流，无需自绘或额外命中测试。</para>
        /// </summary>
        private void OnSearchResultCellClickRow(object sender, AntdUI.TableClickEventArgs e)
        {
            if (e == null || e.Button != MouseButtons.Right) return;
            if (e.RowType != AntdUI.RowType.None) return;             // 表头/汇总栏不弹
            if (e.Record is not SearchResultRow row) return;
            _searchCtxRow = row;
            _searchCtxColumn = e.Column;
            ShowSearchResultContextMenu();
        }

        private void ShowSearchResultContextMenu()
        {
            var row = _searchCtxRow;
            if (row == null || _searchResultTable == null || _searchResultTable.IsDisposed) return;

            string columnKey = _searchCtxColumn?.Key;
            bool isActionColumn = string.Equals(columnKey, "Action", StringComparison.Ordinal);
            string raw = GetSearchCellRawText(row, columnKey);

            var openItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("SearchTab.Open", "打开"))
            { Tag = "open", Enabled = !row.IsDirectory && !isActionColumn };

            var copyItem = new AntdUI.ContextMenuStripItem(
                LanguageManager.Instance.GetText("SearchTab.ContextCopy", "复制"))
            { Tag = "copy", Enabled = !isActionColumn && !string.IsNullOrEmpty(raw) };

            AntdUI.ContextMenuStrip.open(_searchResultTable, it =>
            {
                switch (it.Tag as string)
                {
                    case "open": OpenSearchResult(row); break;
                    case "copy": CopySearchCellText(raw); break;
                }
            }, new AntdUI.IContextMenuStripItem[] { openItem, copyItem });
        }

        /// <summary>复制到剪贴板（失败只写日志，不弹窗打断）</summary>
        private static void CopySearchCellText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (Exception ex)
            {
                try { LogService.Warn($"[search] 复制失败：{ex.Message}"); } catch { }
            }
        }

        /// <summary>
        /// 取该单元格的**未截断原值**（#7 的「复制」用）。
        /// <para>表格显示字段已过 <c>SafeCellText</c> 去换行+截断（见 #10），所以复制要回到
        /// `Hit` 上取原值；本地化名称按行内 `RawTag` 就地重算，保证跟随当前语言。</para>
        /// </summary>
        private string GetSearchCellRawText(SearchResultRow row, string columnKey)
        {
            if (row == null) return "";
            var hit = row.Hit;
            switch (columnKey)
            {
                case "Archive": return row.ArchivePath ?? "";      // 完整路径（显示列可能被截断）
                case "File": return row.RelPath ?? "";
                case "Size": return row.Size ?? "";
                case "Time": return row.Time ?? "";
                case "Description": return hit?.Description ?? row.Description ?? "";
                case "Localized":
                    if (!string.IsNullOrEmpty(row.RawTag))
                        return LocalizationHelper.LookupLocalizedTag(row.RawTag, _searchLangTags) ?? "";
                    return hit?.LocalizedName ?? row.Localized ?? "";
                case "Template": return hit?.Template ?? row.Template ?? "";
                case "Hits": return row.Hits ?? "";
                case "Content": return hit?.Context ?? row.ContentRaw ?? "";   // 复制取**原值**（多行）
                default: return "";
            }
        }

        /// <summary>打开一条搜索结果（按搜索模式决定"只在文件列表定位"还是"打开文件内容"）</summary>
        private void OpenSearchResult(SearchResultRow row)
        {
            if (row == null) return;
            // 文件树里的目录/包节点不可打开（#5；双击目录节点按你的决定不实现展开/折叠，只用左侧箭头）
            if (row.IsDirectory) return;
            if (string.IsNullOrEmpty(row.ArchivePath) || !File.Exists(row.ArchivePath))
            {
                MessageBox.Show(
                    LanguageManager.Instance.GetText("Common.MessageBoxFileNotFound", "文件不存在，无法打开编辑器"),
                    LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            EditorLauncher.Open(row.ArchivePath, row.RelPath, row.Keyword, openFileContent: row.ContentMode);
        }

        /// <summary>
        /// 筛选：按关键词对 包名/包内文件名/说明/模板/本地化名称/内容 做包含匹配，从全量命中重建表格数据源。
        /// <para>筛选为空 = 显示全部。重建走 AntList.Clear + AddRange（项目现行写法）。</para>
        /// </summary>
        /// <summary>
        /// 结果表列头排序（除"操作"列）：AntdUI 只负责表头 UI 与 <c>SortMode</c> 状态（NONE→ASC→DESC），
        /// **数据由我们排** —— 排完重走筛选刷新显示集（与编辑器 DBR 网格同一范式）。
        /// <para>限定：**平铺 + 搜索已完成**才允许。树形下排序会把文件排到别的目录节点下、破坏层级；
        /// 搜索进行中流式追加会与排序打架 —— 这两种情况把表头箭头退回并提示，避免"点了没反应却显示已排序"。</para>
        /// </summary>
        private void OnSearchResultSortRows(object sender, AntdUI.IntEventArgs e)
        {
            var table = _searchResultTable;
            if (table == null || table.IsDisposed || e == null) return;
            if (e.Value < 0 || e.Value >= table.Columns.Count) return;
            var col = table.Columns[e.Value];
            if (col == null) return;
            string key = col.Key;
            if (string.IsNullOrEmpty(key) || string.Equals(key, "Action", StringComparison.Ordinal)) return;   // 操作列不参与

            if (_searchTreeMode || _searchRunning)
            {
                col.SortMode = AntdUI.SortMode.NONE;   // 退回箭头
                if (_searchStatusLabel != null)
                    _searchStatusLabel.Text = _searchTreeMode
                        ? LanguageManager.Instance.GetText("SearchTab.SortTreeDisabled", "树形显示下不支持按列排序，请切到平铺显示")
                        : LanguageManager.Instance.GetText("SearchTab.SortBusyDisabled", "搜索进行中，请等搜索完成后再排序");
                return;
            }

            bool asc = col.SortMode == AntdUI.SortMode.ASC;
            SortSearchRows(key, asc);
        }

        /// <summary>按列排序结果（</summary>
        private void SortSearchRows(string key, bool asc)
        {
            if (_searchAllRows.Count == 0) return;
            Comparison<SearchResultRow> cmp;
            switch (key)
            {
                case "Size": cmp = (a, b) => a.SizeBytes.CompareTo(b.SizeBytes); break;   // 按原始字节数（显示文本 "1.2 KB" 排不了）
                case "Hits": cmp = (a, b) => a.MatchCount.CompareTo(b.MatchCount); break; // 命中数按数值
                default: cmp = (a, b) => string.Compare(SearchRowSortText(a, key), SearchRowSortText(b, key), StringComparison.OrdinalIgnoreCase); break;
            }
            _searchAllRows.Sort(asc ? cmp : (a, b) => cmp(b, a));
            ApplySearchFilter();   // 按当前筛选重建显示集（未筛选即全量），顺带刷新表格
        }

        private static string SearchRowSortText(SearchResultRow row, string key)
        {
            if (row == null) return "";
            switch (key)
            {
                case "Archive": return row.ArchivePath ?? "";
                case "File": return row.RelPath ?? "";
                case "Time": return row.Time ?? "";
                case "Description": return row.Description ?? "";
                case "Localized": return row.Localized ?? "";
                case "Template": return row.Template ?? "";
                case "Content": return row.Content ?? "";   // 内容列：按**单元格显示的单行值**排序（与所见一致）
                default: return "";
            }
        }

        private void ApplySearchFilter()
        {
            if (_searchResultRows == null) return;
            string filter = _searchFilterBox?.Text?.Trim() ?? "";
            try
            {
                // 先算出"筛选后的命中行"
                List<SearchResultRow> kept;
                if (string.IsNullOrEmpty(filter))
                {
                    kept = _searchAllRows;
                }
                else
                {
                    kept = new List<SearchResultRow>();
                    foreach (var row in _searchAllRows)
                    {
                        if (ContainsIgnoreCase(row.Archive, filter)
                            || ContainsIgnoreCase(row.File, filter)
                            || ContainsIgnoreCase(row.Description, filter)
                            || ContainsIgnoreCase(row.Template, filter)
                            || ContainsIgnoreCase(row.Localized, filter)
                            || ContainsIgnoreCase(row.ContentRaw, filter))
                            kept.Add(row);
                    }
                }

                if (_searchTreeMode)
                {
                    // 树形：用筛选结果**重建树**（不匹配的叶子自然消失；空目录在建树时不产生）。
                    // 建树后必须走全量 Binding（增量路径不扁平化子节点，见 SetSearchRowsTree）
                    if (kept.Count > 0) SetSearchRowsTree(BuildSearchTreeRows(kept));
                    else SetSearchRowsTree(new List<SearchResultRow>());
                    _searchResultTable?.ExpandAll(true);   // 筛选后结果少，直接展开便于查看
                }
                else
                {
                    SetSearchRowsFlat(kept);
                }
            }
            catch (Exception ex)
            {
                try { LogService.Warn($"[search] 筛选失败：{ex.Message}"); } catch { }
            }
        }

        private static bool ContainsIgnoreCase(string text, string value)
            => !string.IsNullOrEmpty(text) && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>大小显示：解压后大小，自动 KB / MB</summary>
        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.##") + " MB";
        }

        /// <summary>结果表单元格文本的上限（含省略号）。见 <see cref="SafeCellText"/> 的说明。</summary>
        private const int SearchCellMaxChars = 400;

        /// <summary>
        /// 进表格的字符串统一走这里：**去掉换行/控制字符 + 限长**。
        /// <para>⚠ 为什么必须做：AntdUI 的单元格测量把文本交给 GDI+ `MeasureString`，**"多行 + 超长"** 时
        /// GDI+ 换行排版溢出、抛 `ExternalException 0x80004005`，且异常发生在 UI 线程（鼠标滚动触发重排）→ **直接闪退**。
        /// 实测（真实包 grimarillion）：40,905 字符的单元格文本必崩，32,000 正常，去掉换行后 40,905 也正常。</para>
        /// <para>引擎侧已对上下文做单行化 + 限长；这里是**兜底**，覆盖路径 / 说明 / 本地化名等其它来源。</para>
        /// <para>截断只影响表格显示，完整内容仍可在编辑器里查看。</para>
        /// </summary>
        /// <param name="max">限长（0 = 不限长；引擎侧已限过时长用 0，避免二次截断）</param>
        /// <param name="keepNewline">
        /// **保留换行**（仅内容列用）：值里的 `\n` 要留着 —— AntdUI 自带单元格 tip 显示的
        /// 就是这个原始值，留着换行 tip 才能换行显示、复制出来也是多行。
        /// 其余列保持默认（false）：那些是单行文本，混入换行只会显示成脏字符。
        /// 无论哪种模式，`\r` / `\t` / 其它控制字符都仍会换成空格。
        /// </param>
        private static string SafeCellText(string text, int max = SearchCellMaxChars, bool keepNewline = false)
        {
            if (string.IsNullOrEmpty(text)) return "";
            bool dirty = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\t' || char.IsControl(c)) { dirty = true; break; }
                if (!keepNewline && c == '\n') { dirty = true; break; }
            }
            string s = text;
            if (dirty)
            {
                var sb = new System.Text.StringBuilder(text.Length);
                foreach (char c in text)
                {
                    if (keepNewline && c == '\n') { sb.Append('\n'); continue; }
                    sb.Append(c == '\r' || c == '\t' || char.IsControl(c) ? ' ' : c);
                }
                s = sb.ToString();
            }
            if (max > 0 && s.Length > max) s = s.Substring(0, max) + "…";
            return s;
        }

        /// <summary>内容列多处命中之间的显示分隔符（全角斜杠，视觉窄、不与内容里的半角 `／` 混）</summary>
        private const string ContentCellSep = " ／ ";

        /// <summary>
        /// 内容列的**单元格显示文本**：把多处命中之间的换行压成 <see cref="ContentCellSep"/>，得到**单行**文本。
        /// <para>⚠ 为什么必须单行（实测结论）：AntdUI 单元格是"整块文本在行高内竖直居中"。
        /// 多行文本（如 5 段命中行）整块高度约 95px，远超 26px 行高；居中后单元格矩形内
        /// **没有任何一行是完整的**，而 `StringFormat.GenericTypographic` 自带 `LineLimit`
        /// （只画完整行）→ **整格空白**。现象正是"命中数=1（单行）正常、多行空白，鼠标悬停 tip 仍正常"。
        /// 压成单行后文本块高度 ≤ 行高，居中即完整显示，`Ellipsis` 的省略号也正常生效。</para>
        /// <para>本方法在**建行时调用一次**（结果缓存进行模型 <see cref="SearchResultRow.Content"/>），
        /// 不在每次布局时重算 —— 否则 5 万行 × 每次布局一遍字符串替换会拖慢滚动。</para>
        /// </summary>
        private static string BuildContentLineDisplay(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw;
            if (s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0)
            {
                s = s.Replace("\r\n", "\n").Replace('\r', '\n');
                if (s.IndexOf('\n') >= 0) s = s.Replace("\n", ContentCellSep);
            }
            return SafeCellText(s);   // 再走一遍统一净化（控制字符 → 空格 + 限长）
        }

        /// <summary>统计标签：已扫描包数（ARZ/ARC 分列）· 命中条数 · 耗时 · 失败包数</summary>
        private void UpdateSearchStats(int totalPackages, int foundCount, int failedCount, double seconds)
        {
            if (_searchStatsLabel == null) return;
            _searchStatsLabel.Text = string.Format(LanguageManager.Instance.GetText("SearchTab.Stats",
                "已扫描 {0} 个包 · 命中 {1} 条 · 耗时 {2:0.0}s · 失败 {3} 个"),
                totalPackages, foundCount, seconds, failedCount);
        }

        #endregion

        #region MOD合并选项卡

        private AntdUI.TabPage CreateModMergeTab()
        {
            AntdUI.TabPage tabPage = new AntdUI.TabPage() { Text = LanguageManager.Instance.GetText("ModMergeTool.TabTitle", "MOD合并") };
            tabPage.Name = "ModMergeTool.TabTitle";

            AntdUI.Panel mainPanel = new AntdUI.Panel() { Back = Color.Transparent };
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.Padding = new Padding(20);

            var dropPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderWidth = 1F,
                AllowDrop = true,
                HandDragFolder = false,
                Back = ThemeManager.BgContainer,
                BackColor = ThemeManager.BgContainer
            };

            var historyTable = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                VisibleHeader = true,
                Bordered = true,
                MultipleRows = false,
                EditMode = AntdUI.TEditMode.None,
                LostFocusClearSelection = false,
                Font = FontHelper.UiFont(9),
                AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill
            };

            var bottomBar = new AntdUI.Panel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                Back = Color.Transparent,
                BackColor = Color.Transparent
            };
            var clearAllBtn = new AntdUI.Button
            {
                Text = LanguageManager.Instance.GetText("ModMergeTool.HistoryClear", "清空历史"),
                Type = AntdUI.TTypeMini.Error,
                AutoSize = true,
                
                Font = FontHelper.UiFont(9),
                Dock = DockStyle.Right,
                Margin = new Padding(0, 2, 4, 0)
            };
            bottomBar.Controls.Add(clearAllBtn);

            var dropLabel = new Label
            {
                Name = "ModMergeTool.DropHint",
                Text = LanguageManager.Instance.GetText("ModMergeTool.Hint", "请选择两个同格式的包文件（同为 .arz 或同为 .arc），工具将自动识别格式并扫描对比。"),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top,
                Height = 36,
                Font = FontHelper.UiFont(10),
                ForeColor = ThemeManager.TextTertiary,
                BackColor = ThemeManager.BgContainer
            };

            void RefreshMergeHistoryTable()
            {
                historyTable.Columns.Clear();
                historyTable.Columns.Add(new AntdUI.Column("BasePath",
                    LanguageManager.Instance.GetText("ModMergeTool.HistoryBase", "基准包")) { Width = "35%", LineBreak = true });
                historyTable.Columns.Add(new AntdUI.Column("ModPath",
                    LanguageManager.Instance.GetText("ModMergeTool.HistoryMod", "并入包")) { Width = "35%", LineBreak = true });
                historyTable.Columns.Add(new AntdUI.Column("OpenTime",
                    LanguageManager.Instance.GetText("ModMergeTool.HistoryTime", "打开时间")) { Width = "18%", Align = AntdUI.ColumnAlign.Right });
                historyTable.Columns.Add(new AntdUI.Column("CellLinks", "") { Width = "130" });
                historyTable.EmptyText = LanguageManager.Instance.GetText("ModMergeTool.HistoryEmpty", "暂无历史记录");

                var list = SettingsManager.GetMergeHistory();
                var dataSource = new List<object>();
                foreach (var e in list)
                {
                    dataSource.Add(new
                    {
                        BasePath = e.BasePath,
                        ModPath = e.ModPath,
                        ExistsBase = PathCheckHelper.Exists(e.BasePath),
                        ExistsMod = PathCheckHelper.Exists(e.ModPath),
                        OpenTime = e.OpenTime.ToString("yyyy-MM-dd HH:mm"),
                        CellLinks = new AntdUI.CellLink[]
                        {
                            new AntdUI.CellButton("open", LanguageManager.Instance.GetText("ModMergeTool.HistoryOpen", "打开"), AntdUI.TTypeMini.Primary),
                            new AntdUI.CellButton("clear", LanguageManager.Instance.GetText("ModMergeTool.HistoryUndo", "撤销"))
                        },
                        Id = e.Id
                    });
                }
                historyTable.DataSource = dataSource;
            }

            historyTable.CellPaintBegin += (s, e) =>
            {
                if (e.RowType == AntdUI.RowType.Column || e.RowType == AntdUI.RowType.Summary || e.Record == null) return;
                if (e.Column == null) return;
                if (e.Column.Key == "BasePath")
                {
                    bool missing = !(bool)((dynamic)e.Record).ExistsBase;
                    PathCheckHelper.ApplyMissingCellStyle(e, "BasePath", missing, historyTable.Font);
                }
                else if (e.Column.Key == "ModPath")
                {
                    bool missing = !(bool)((dynamic)e.Record).ExistsMod;
                    PathCheckHelper.ApplyMissingCellStyle(e, "ModPath", missing, historyTable.Font);
                }
            };

            historyTable.CellButtonClick += (s, e) =>
            {
                var btn = e.Btn;
                var data = e.Record as dynamic;
                string id = data.Id as string;
                if (btn.Id == "open")
                {
                    bool existsBase = (bool)data.ExistsBase;
                    bool existsMod = (bool)data.ExistsMod;
                    if (!existsBase || !existsMod)
                    {
                        var missingMsgs = new List<string>(2);
                        if (!existsBase)
                            missingMsgs.Add(string.Format(LanguageManager.Instance.GetText("ModMergeTool.FileMissingPkg1", "包1文件不存在：{0}"), data.BasePath as string));
                        if (!existsMod)
                            missingMsgs.Add(string.Format(LanguageManager.Instance.GetText("ModMergeTool.FileMissingPkg2", "包2文件不存在：{0}"), data.ModPath as string));
                        AntdUI.Notification.open(new AntdUI.Notification.Config(this,
                            LanguageManager.Instance.GetText("Common.Notice", "提示"),
                            string.Join("\n", missingMsgs),
                            AntdUI.TType.Warn,
                            AntdUI.TAlignFrom.Top,
                            FontHelper.UiFont(11), 8));
                        return;
                    }
                    string basePath = data.BasePath as string;
                    string modPath = data.ModPath as string;
                    var mergeForm = new ModMergeToolForm();
                    mergeForm.LoadHistoryPair(basePath, modPath);
                    mergeForm.Show(this);
                    mergeForm.FormClosed += (s2, ev) => RefreshMergeHistoryTable();
                    RefreshMergeHistoryTable();
                }
                else if (btn.Id == "clear")
                {
                    SettingsManager.RemoveMergeHistory(id);
                    RefreshMergeHistoryTable();
                }
            };

            clearAllBtn.Click += (s, ev) =>
            {
                SettingsManager.ClearMergeHistory();
                RefreshMergeHistoryTable();
            };

            dropPanel.Controls.Add(historyTable);
            dropPanel.Controls.Add(bottomBar);
            dropPanel.Controls.Add(dropLabel);

            LanguageManager.Instance.LanguageChanged += () =>
            {
                clearAllBtn.Text = LanguageManager.Instance.GetText("ModMergeTool.HistoryClear", "清空历史");
                dropLabel.Text = LanguageManager.Instance.GetText("ModMergeTool.Hint", "请选择两个同格式的包文件（同为 .arz 或同为 .arc），工具将自动识别格式并扫描对比。");
                RefreshMergeHistoryTable();
            };

            tabPage.VisibleChanged += (s, e) =>
            {
                if (tabPage.Visible) RefreshMergeHistoryTable();
            };

            dropPanel.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
                else
                    e.Effect = DragDropEffects.None;
            };
            dropPanel.DragDrop += (s, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length == 0) return;
                string file = files[0];
                string ext = Path.GetExtension(file)?.ToLower();
                if (ext == ".arz" || ext == ".arc")
                {
                    var mergeForm = new ModMergeToolForm();
                    if (files.Length >= 2)
                        mergeForm.LoadHistoryPair(files[0], files[1]);
                    else
                        mergeForm.LoadHistoryPair(file, "");
                    mergeForm.Show(this);
                    mergeForm.FormClosed += (s2, ev) => RefreshMergeHistoryTable();
                    RefreshMergeHistoryTable();
                }
                else
                {
                    MessageBox.Show(
                        LanguageManager.Instance.GetText("Common.MessageBoxUnsupportedFormat", "不支持的文件格式，请选择 ARZ 或 ARC 文件"),
                        LanguageManager.Instance.GetText("Common.MessageBoxTitle.Warning", "警告"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            mainPanel.Controls.Add(dropPanel);

            // 与其它页签主按钮（解包ARC / 打包ARC / 解包ARZ / 打包ARZ）保持完全一致：
            // AutoSize + 9 号粗体 + 主色；此前 Height=44 + 11 号粗体导致比其它页签明显偏大
            var launchButton = new AntdUI.Button
            {
                Name = "ModMergeTool.LaunchButton",
                Text = LanguageManager.Instance.GetText("ModMergeTool.LaunchButton", "启动 MOD 合并工具"),
                Type = AntdUI.TTypeMini.Primary,
                AutoSize = true,
                Font = FontHelper.UiFont(9, FontStyle.Bold)
            };
            launchButton.Click += (s, e) =>
            {
                var mergeForm = new ModMergeToolForm();
                mergeForm.Show(this);
                mergeForm.FormClosed += (s2, ev) => RefreshMergeHistoryTable();
            };

            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 70,
                Back = Color.Transparent
            };
            buttonPanel.Controls.Add(launchButton);
            buttonPanel.Layout += (s, e) => {
                if (buttonPanel.Controls.Count == 0) return;
                int totalWidth = 0;
                int gap = 10;
                foreach (Control c in buttonPanel.Controls) totalWidth += c.Width;
                totalWidth += gap * (buttonPanel.Controls.Count - 1);
                int x = (buttonPanel.Width - totalWidth) / 2;
                int y = (buttonPanel.Height - buttonPanel.Controls[0].Height) / 2;
                foreach (Control c in buttonPanel.Controls)
                {
                    c.Location = new Point(x, y);
                    x += c.Width + gap;
                }
            };
            mainPanel.Controls.Add(buttonPanel);
            tabPage.Controls.Add(mainPanel);

            // 初始刷新
            RefreshMergeHistoryTable();

            // AntdUI.Tabs 切页时只改 Page.Showed、不变化 Visible，故用 SelectedIndexChanged 保证历史实时刷新
            _mainTabs.SelectedIndexChanged += (s, e) =>
            {
                if (e.Value >= 0 && e.Value < _mainTabs.Pages.Count && _mainTabs.Pages[e.Value] == tabPage)
                    RefreshMergeHistoryTable();
            };

            return tabPage;
        }

        #endregion

        #region 编辑器静态方法
        // 静态方法供 ArzArcEditorForm 调用，由旧版本同名私有方法迁移而来
        public static List<NodeData> BuildHierarchicalStructureStatic(List<(string name, DateTime fileTime, int index, string recordType)> entries)
        {
            var root = new List<NodeData>();
            var pathCache = new Dictionary<string, NodeData>();

            foreach (var (entryName, fileTime, recordIndex, recordType) in entries)
            {
                if (string.IsNullOrEmpty(entryName)) continue;
                var parts = entryName.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                NodeData currentLevel = null;
                List<NodeData> currentParent = root;
                string currentPath = "";

                for (int i = 0; i < parts.Length; i++)
                {
                    bool isLastPart = i == parts.Length - 1;
                    currentPath = string.IsNullOrEmpty(currentPath) ? parts[i] : $"{currentPath}/{parts[i]}";

                    if (pathCache.TryGetValue(currentPath, out var existingNode))
                    {
                        currentLevel = existingNode;
                        currentParent = existingNode.Children;
                        existingNode.TotalItemCount++;
                        continue;
                    }

                    var newNode = new NodeData
                    {
                        Name = parts[i],
                        IsDirectory = !isLastPart,
                        TotalItemCount = 1,
                        FileTime = isLastPart ? fileTime : (DateTime?)null,
                        RecordIndex = isLastPart ? recordIndex : (int?)null,
                        RecordType = isLastPart ? recordType : null
                    };

                    currentParent.Add(newNode);
                    pathCache[currentPath] = newNode;

                    currentLevel = newNode;
                    currentParent = newNode.Children;
                }
            }

            return root;
        }

        #endregion

        #region 多语言支持
        
        // 初始化语言支持（在构造函数或SetupUI末尾调用）
        
        private void InitLanguageSupport()
        {
            // 订阅语言变更事件，触发UI刷新
            LanguageManager.Instance.LanguageChanged += RefreshUI;
            
            // 初始加载默认语言
            RefreshUI();
        }

        // 递归刷新所有控件文本（核心方法）
        private void RefreshUI()
        {
            // 从主窗口开始递归刷新所有控件
            RefreshControlText(this);
        }

        // 递归处理所有子控件
        private void RefreshControlText(Control parent)
        {
            if (parent == null) return;

            // 仅处理设置了Name且需要多语言的控件
            if (!string.IsNullOrEmpty(parent.Name))
            {
                // 根据控件类型更新文本（支持Label/Button/GroupBox/TabPage等）
                switch (parent)
                {
                    case Label label:
                        label.Text = LanguageManager.Instance.GetText(label.Name, label.Text); // 第二个参数为默认值（开发时的中文）
                        break;
                    case AntdUI.Button button:
                        button.Text = LanguageManager.Instance.GetText(button.Name, button.Text);
                        break;
                    case AntdUI.Panel groupBox:
                        groupBox.Text = LanguageManager.Instance.GetText(groupBox.Name, groupBox.Text);
                        break;
                    case AntdUI.TabPage tabPage:
                        tabPage.Text = LanguageManager.Instance.GetText(tabPage.Name, tabPage.Text);
                        break;
                    case Form form:
                        form.Text = LanguageManager.Instance.GetText(form.Name, form.Text);
                        break;
                    case TextBox textBox:
                        // Input/Select的多语言支持由各自处理
                        break;
                    case CheckBox checkBox:
                        checkBox.Text = LanguageManager.Instance.GetText(checkBox.Name, checkBox.Text);
                        break;
                    case RadioButton radioButton:
                        radioButton.Text = LanguageManager.Instance.GetText(radioButton.Name, radioButton.Text);
                        break;
                    case AntdUI.Tabs tabControl:
                        foreach (AntdUI.TabPage tp in tabControl.Pages)
                            RefreshControlText(tp);
                        return; // TabPages already recursed, skip child Controls iteration
                    // 可根据需要添加更多控件类型
                }
            }

            // 递归刷新子控件
            foreach (Control child in parent.Controls)
            {
                RefreshControlText(child);
            }
        }
        #endregion

        // 添加静态变量记住上次选择的位置
        private static string lastSelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        private void BrowseFile(TextBox textBox, string title, string filter)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = title;
                dialog.Filter = filter;
                if (!string.IsNullOrEmpty(textBox.Text))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(textBox.Text);
                }
                else if (Directory.Exists(lastSelectedPath))
                {
                    dialog.InitialDirectory = lastSelectedPath;
                }
                
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    textBox.Text = dialog.FileName;
                    lastSelectedPath = Path.GetDirectoryName(dialog.FileName);
                }
            }
        }

        private void BrowseFolder(TextBox textBox, string description)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = description;
                if (!string.IsNullOrEmpty(textBox.Text) && Directory.Exists(textBox.Text))
                {
                    dialog.SelectedPath = textBox.Text;
                }
                else if (Directory.Exists(lastSelectedPath))
                {
                    dialog.SelectedPath = lastSelectedPath;
                }
                
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    textBox.Text = dialog.SelectedPath;
                    lastSelectedPath = dialog.SelectedPath;
                }
            }
        }
        
        #region 执行方法
        
        private Task ExecuteUnpackArc(string inputFile, string outputDir, Control contentPanel, LogView logView, AntdUI.Button unpackArcButton)
        {
            if (string.IsNullOrEmpty(inputFile))
            {
                ShowResultBox("Common.MessageBoxNoSelectUnPackfile", "请选择要解包的文件", MessageBoxIcon.Warning);
                return Task.CompletedTask;
            }

            if (!File.Exists(inputFile))
            {
                ShowResultBox("Common.MessageFileNotFound", "文件不存在，请检查", MessageBoxIcon.Error);
                return Task.CompletedTask;
            }

            // 【恢复】操作开始前清空界面日志框：保持本次日志干净（历史由日志文件承载，文件不受影响）
            logView?.Clear();

            return RunTabOperationAsync(contentPanel, logView, new[] { unpackArcButton },
                LanguageManager.Instance.GetText("UnpackArcTab.ExecuteButton", "解包ARC"),
                async (progress, token, cfg) =>
                {
                    bool success = await GDPackEditorAPI.UnpackArcAsync(inputFile, outputDir, progress, token);
                    if (success)
                    {
                        ShowResultBox("Common.MessageBoxUnPackSuccess", "解包完成！", MessageBoxIcon.Information);
                    }
                    else if (!progress.IsCancellationRequested)
                    {
                        // 操作已取消时由统一骨架记录「操作已终止」，此处仅处理真实失败
                        ShowResultBox("Common.MessageBoxUnPackFail", "解包失败，请查看日志", MessageBoxIcon.Error);
                    }
                });
        }
        
        private Task ExecutePackArc(string inputDir, string outputFile, Control contentPanel, LogView logView,
            AntdUI.Button packArcButton, AntdUI.Button compactArcButton, bool concurrent, bool includeDeletions = true)
        {
            if (string.IsNullOrEmpty(inputDir))
            {
                ShowResultBox("Common.MessageBoxNoSelectPackDir", "请选择要打包的目录", MessageBoxIcon.Warning);
                return Task.CompletedTask;
            }

            if (!Directory.Exists(inputDir))
            {
                ShowResultBox("Common.MessageBoxDirNotFound", "选择的目录不存在", MessageBoxIcon.Error);
                return Task.CompletedTask;
            }

            // 文件名编码检查不再弹窗：结论由打包过程末尾的日志汇总输出
            // 【恢复】操作开始前清空界面日志框：保持本次日志干净（历史由日志文件承载，文件不受影响）
            logView?.Clear();

            return RunTabOperationAsync(contentPanel, logView, new[] { packArcButton, compactArcButton },
                LanguageManager.Instance.GetText("PackArcTab.ExecuteButton", "打包ARC"),
                async (progress, token, cfg) =>
                {
                    // 变更确认回调（在后台线程调用，通过Invoke切换到UI线程）
                    Func<List<string>, bool> confirmCallback = (changes) => {
                        if (logView.InvokeRequired)
                            return (bool)logView.Invoke(new Func<List<string>, bool>(ConfirmChangesDialog), new object[] { changes });
                        return ConfirmChangesDialog(changes);
                    };

                    bool success;
                    if (concurrent)
                    {
                        if (!string.IsNullOrEmpty(outputFile) && File.Exists(outputFile))
                        {
                            progress.ReportLog(LanguageManager.Instance.GetText("LOG.IncrementalOldArcFound",
                                "增量模式：检测到旧ARC文件"));
                            success = await GDPackEditorAPI.PackArcIncrementalAsync(inputDir, outputFile, outputFile,
                                progress, confirmCallback, includeDeletions, token);
                        }
                        else
                        {
                            progress.ReportLog(LanguageManager.Instance.GetText("LOG.IncrementalNoOldArc",
                                "增量模式：未找到旧ARC文件，执行完整打包"));
                            success = await GDPackEditorAPI.PackArcAsync(inputDir, outputFile, progress, token);
                        }
                    }
                    else
                    {
                        success = await GDPackEditorAPI.PackArcAsync(inputDir, outputFile, progress, token);
                    }

                    if (success)
                    {
                        ShowResultBox("Common.MessageBoxPackSuccess", "打包完成！", MessageBoxIcon.Information);
                    }
                    else if (!progress.IsCancellationRequested)
                    {
                        // 操作已取消时由统一骨架记录「操作已终止」，此处仅处理真实失败
                        ShowResultBox("Common.MessageBoxPackFail", "打包失败，请查看日志", MessageBoxIcon.Error);
                    }
                });
        }
        
        /// <summary>
        /// 「检测」按钮：对指定 ARZ 做内容检查（等价 CLI --check 的内容检查）。
        /// 进度反映在 Spin 遮罩文字上（转圈 + 百分比），逐条可疑与结尾汇总写入本页日志区。
        /// 只读、不改动包。取消走 Spin 自带「取消」→ 令牌（修复旧实现未注册取消目标导致取消无效/误伤的问题）。
        /// </summary>
        private Task ExecuteCheckArz(string arzPath, Control contentPanel, LogView logView, AntdUI.Button checkButton)
        {
            string path = arzPath?.Trim().Trim('"');
            if (string.IsNullOrEmpty(path))
            {
                ShowResultBox("Common.MessageFileNotFound", "文件不存在，请检查", MessageBoxIcon.Warning);
                return Task.CompletedTask;
            }
            // 批D：自动判断路径 —— 目录 → 目录 dbr 文本检测；文件 → 包检测（与 CLI --check 同一自动判断）
            if (Directory.Exists(path))
            {
                return ExecuteCheckDbrDirectory(path, contentPanel, logView, checkButton);
            }
            if (!File.Exists(path))
            if (!string.Equals(Path.GetExtension(path), ".arz", StringComparison.OrdinalIgnoreCase))
            {
                ShowResultBox("Common.CheckArzOnly",
                    "内容检测仅支持 .arz 文件（ARC 为二进制容器，无此项检查）", MessageBoxIcon.Warning);
                return Task.CompletedTask;
            }

            logView?.Clear();   // 本次检测日志保持干净（历史由日志文件承载）

            return RunTabOperationAsync(contentPanel, logView, new[] { checkButton },
                LanguageManager.Instance.GetText("Common.CheckButton", "检测"),
                async (progress, token, cfg) =>
                {
                    // 进度只更新 Spin 遮罩文字（转圈 + 百分比），不进日志
                    void OnCheckProgress(int pct, string msg)
                    {
                        try
                        {
                            if (IsDisposed) return;
                            BeginInvoke((Action)(() => cfg.Text = msg));
                        }
                        catch { }
                    }

                    var (issues, records, newlineIssues) = await Task.Run(
                        () => GDPackEditorAPI.CheckArzContent(path, progress, token, OnCheckProgress), token);
                    logView?.Enqueue(string.Format(
                        LanguageManager.Instance.GetText("LOG.CheckArzContentResult",
                            "内容检测完成：检查 {0} 条记录，发现 {1} 处可疑"), records, issues));
                    // 换行说明放"检测完成"之后：此处用户注意力最高（解释"为何导出/源码里看不到换行"）
                    if (newlineIssues > 0)
                        logView?.Enqueue(LanguageManager.Instance.GetText("LOG.CheckArzNewlineNote",
                            "说明：值里的换行会在解包 / 编辑器源码模式被自动删除（否则会破坏 DBR 行结构），"
                            + "所以导出的文件与源码视图里看不到它——本检测读取的是包内原始数据"));
                });
        }

        /// <summary>
        /// 「检测」按钮传入**目录**时：遍历目录下全部 .dbr 按【内容检测】同一套规则检查（批D，需求6）。
        /// 进度/取消/日志呈现与包检测完全一致；只读、不改动任何文件。
        /// </summary>
        private Task ExecuteCheckDbrDirectory(string dirPath, Control contentPanel, LogView logView, AntdUI.Button checkButton)
        {
            logView?.Clear();   // 本次检测日志保持干净（历史由日志文件承载）

            return RunTabOperationAsync(contentPanel, logView, new[] { checkButton },
                LanguageManager.Instance.GetText("Common.CheckButton", "检测"),
                async (progress, token, cfg) =>
                {
                    // 进度只更新 Spin 遮罩文字（转圈 + 百分比），不进日志
                    void OnCheckProgress(int pct, string msg)
                    {
                        try
                        {
                            if (IsDisposed) return;
                            BeginInvoke((Action)(() => cfg.Text = msg));
                        }
                        catch { }
                    }

                    var (issues, files) = await Task.Run(
                        () => GDPackEditorAPI.CheckDbrDirectory(dirPath, progress, token, OnCheckProgress), token);
                    logView?.Enqueue(string.Format(
                        LanguageManager.Instance.GetText("LOG.CheckDbrDirResult",
                            "目录检测完成：检查 {0} 个文件，发现 {1} 处可疑"), files, issues));
                });
        }

        private Task ExecuteUnpackArz(string inputFile, string outputDir, Control contentPanel, LogView logView, AntdUI.Button unpackArzButton)
        {
            if (string.IsNullOrEmpty(inputFile))
            {
                ShowResultBox("Common.MessageBoxNoSelectUnPackfile", "请选择要解包的文件", MessageBoxIcon.Warning);
                return Task.CompletedTask;
            }

            if (!File.Exists(inputFile))
            {
                ShowResultBox("Common.MessageFileNotFound", "文件不存在，请检查", MessageBoxIcon.Error);
                return Task.CompletedTask;
            }

            // 【恢复】操作开始前清空界面日志框：保持本次日志干净（历史由日志文件承载，文件不受影响）
            logView?.Clear();

            return RunTabOperationAsync(contentPanel, logView, new[] { unpackArzButton },
                LanguageManager.Instance.GetText("UnpackArzTab.ExecuteButton", "解包ARZ"),
                async (progress, token, cfg) =>
                {
                    bool success = await GDPackEditorAPI.UnpackArzAsync(inputFile, outputDir, progress, token);
                    if (success)
                    {
                        ShowResultBox("Common.MessageBoxUnPackSuccess", "解包完成！", MessageBoxIcon.Information);
                    }
                    else if (!progress.IsCancellationRequested)
                    {
                        // 操作已取消时由统一骨架记录「操作已终止」，此处仅处理真实失败
                        ShowResultBox("Common.MessageBoxUnPackFail", "解包失败，请查看日志", MessageBoxIcon.Error);
                    }
                });
        }
        
        private Task ExecutePackArz(string modDir, string outputFile, Control contentPanel, LogView logView,
            AntdUI.Button packArzButton, AntdUI.Button compactArzButton, bool multiThreadRead = false)
        {
            if (string.IsNullOrEmpty(modDir))
            {
                ShowResultBox("Common.MessageBoxNoSelectPackDir", "请选择要打包的目录", MessageBoxIcon.Warning);
                return Task.CompletedTask;
            }

            if (!Directory.Exists(modDir))
            {
                ShowResultBox("Common.MessageBoxModDirNotFound", "选择的mod目录不存在", MessageBoxIcon.Error);
                return Task.CompletedTask;
            }

            // 收集勾选的子目录（打包范围，与旧实现一致）
            List<string> selectedSubDirs = new List<string>();
            foreach (var ctl in databaseSubDirsCheckedListBox.Controls)
            {
                if (ctl is CheckBox cb && cb.Checked)
                {
                    selectedSubDirs.Add(cb.Text);
                }
            }

            // 【恢复】操作开始前清空界面日志框：保持本次日志干净（历史由日志文件承载，文件不受影响）
            logView?.Clear();

            return RunTabOperationAsync(contentPanel, logView, new[] { packArzButton, compactArzButton },
                LanguageManager.Instance.GetText("PackArzTab.ExecuteButton", "打包ARZ"),
                async (progress, token, cfg) =>
                {
                    // multiThreadRead 为历史遗留参数（打包实现内部自行决定读取方式），保留以免改动调用方
                    bool success = await GDPackEditorAPI.BuildArzWithSelectedDirsAsync(modDir, null, null, selectedSubDirs,
                        progress, outputFile, token);

                    if (success)
                    {
                        ShowResultBox("Common.MessageBoxPackSuccess", "打包完成！", MessageBoxIcon.Information);
                    }
                    else if (!progress.IsCancellationRequested)
                    {
                        // 操作已取消时由统一骨架记录「操作已终止」，此处仅处理真实失败
                        ShowResultBox("Common.MessageBoxPackFail", "打包失败，请查看日志", MessageBoxIcon.Error);
                    }
                });
        }
        
        private Task ExecuteCompactArc(string arcFile, Control contentPanel, LogView logView, AntdUI.Button compactArcButton)
        {
            if (string.IsNullOrEmpty(arcFile) || !File.Exists(arcFile))
            {
                ShowResultBox("Common.MessageBoxNoArcFile", "请先打包生成ARC文件", MessageBoxIcon.Warning);
                return Task.CompletedTask;
            }

            // 【恢复】操作开始前清空界面日志框：保持本次日志干净（历史由日志文件承载，文件不受影响）
            logView?.Clear();

            return RunTabOperationAsync(contentPanel, logView, new[] { compactArcButton },
                LanguageManager.Instance.GetText("PackArcTab.CompactButton", "固实"),
                async (progress, token, cfg) =>
                {
                    bool result = await GDPackEditorAPI.CompactArcAsync(arcFile, arcFile, progress, token);
                    if (result)
                    {
                        ShowResultBox("Common.CompactArcSuccess", "固实完成！已清理无效条目。", MessageBoxIcon.Information);
                    }
                    else if (!progress.IsCancellationRequested)
                    {
                        // 操作已取消时由统一骨架记录「操作已终止」，此处仅处理真实失败
                        ShowResultBox("Common.CompactArcFail", "固实失败，请查看日志。", MessageBoxIcon.Error);
                    }
                });
        }

        private Task ExecuteCompactArz(string arzFile, Control contentPanel, LogView logView, AntdUI.Button compactArzButton)
        {
            if (string.IsNullOrEmpty(arzFile) || !File.Exists(arzFile))
            {
                ShowResultBox("Common.MessageBoxNoArzFile", "请先打包生成ARZ文件", MessageBoxIcon.Warning);
                return Task.CompletedTask;
            }

            // 【恢复】操作开始前清空界面日志框：保持本次日志干净（历史由日志文件承载，文件不受影响）
            logView?.Clear();

            return RunTabOperationAsync(contentPanel, logView, new[] { compactArzButton },
                LanguageManager.Instance.GetText("PackArzTab.CompactButton", "固实"),
                async (progress, token, cfg) =>
                {
                    bool result = await GDPackEditorAPI.CompactArzAsync(arzFile, arzFile, progress, token);
                    if (result)
                    {
                        ShowResultBox("Common.CompactArzSuccess", "固实完成！已清理墓碑记录。", MessageBoxIcon.Information);
                    }
                    else if (!progress.IsCancellationRequested)
                    {
                        // 操作已取消时由统一骨架记录「操作已终止」，此处仅处理真实失败
                        ShowResultBox("Common.CompactArzFail", "固实失败，请查看日志。", MessageBoxIcon.Error);
                    }
                });
        }

        // 复制目录的辅助方法
        private void CopyDirectory(string sourceDir, string destinationDir)
        {
            // 创建目标目录
            Directory.CreateDirectory(destinationDir);
            
            // 复制文件
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }
            
            // 递归复制子目录
            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string destDir = Path.Combine(destinationDir, Path.GetFileName(dir));
                CopyDirectory(dir, destDir);
            }
        }
        
        #endregion
        
        // 显示更新日志窗口
        private void ShowDonateDialog()
        {
            var content = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                Size = new Size(740, 420)
            };

            var imagePanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(5)
            };
            imagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            imagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            var wxPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };
            AddDonateImageContent(wxPanel, "GDPackEditor.wx.jpg", "微信");
            var zfbPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };
            AddDonateImageContent(zfbPanel, "GDPackEditor.zfb.jpg", "支付宝");

            imagePanel.Controls.Add(wxPanel, 0, 0);
            imagePanel.Controls.Add(zfbPanel, 1, 0);
            content.Controls.Add(imagePanel);

            var descLabel = new Label
            {
                Text = LanguageManager.Instance.GetText("MainForm.DonateDescription",
                    "如果你觉得该工具还不错，那么可以请老张喝杯奶茶！\n金额不限，感谢打赏。"),
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 60,
                Dock = DockStyle.Bottom,
                Font = FontHelper.UiFont(11)
            };
            content.Controls.Add(descLabel);

            var config = new AntdUI.Modal.Config(this,
                LanguageManager.Instance.GetText("MainForm.DonateTitle", "打赏"),
                content)
            {
                Resizable = true,
                Draggable = true,
                MaskClosable = false
            };
            config.SetWidth(800);
            config.SetOk(LanguageManager.Instance.GetText("Common.Close", "关闭"));
            config.SetCancelHide();
            AntdUI.Modal.open(config);
        }

        private static void AddDonateImageContent(Panel cellPanel, string resourceName, string label)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        var pb = new PictureBox
                        {
                            Dock = DockStyle.Fill,
                            Image = Image.FromStream(stream),
                            SizeMode = PictureBoxSizeMode.Zoom
                        };
                        cellPanel.Controls.Add(pb);
                    }
                    else
                    {
                        cellPanel.Controls.Add(new Label
                        {
                            Text = string.Format(LanguageManager.Instance.GetText("MainForm.DonateImageNotFound", "未找到图片: {0}"), label),
                            TextAlign = ContentAlignment.MiddleCenter,
                            Dock = DockStyle.Fill,
                            ForeColor = ThemeManager.TextTertiary
                        });
                    }
                }
            }
            catch
            {
                cellPanel.Controls.Add(new Label
                {
                    Text = string.Format(LanguageManager.Instance.GetText("MainForm.DonateImageNotFound", "未找到图片: {0}"), label),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill,
                    ForeColor = ThemeManager.TextTertiary
                });
            }
        }

        #region 变更确认对话框

        /// <summary>
        /// 变更确认对话框（带滚动条列表+固定按钮）
        /// </summary>
        private bool ConfirmChangesDialog(List<string> changes)
        {
            if (changes == null || changes.Count == 0) return true;

            using (AntdUI.BaseForm dlg = new AntdUI.BaseForm())
            {
                dlg.Text = LanguageManager.Instance.GetText("Common.ConfirmChangesTitle", "检测到文件变更");
                dlg.Size = new System.Drawing.Size(700, 500);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.Owner = this;
                dlg.MinimumSize = new System.Drawing.Size(400, 300);

                // 顶部提示文字
                Label infoLabel = new Label();
                infoLabel.Text = string.Format(LanguageManager.Instance.GetText("Common.ConfirmChangesInfo", "检测到 {0} 项变更，是否继续打包？"), changes.Count);
                infoLabel.Dock = DockStyle.Top;
                infoLabel.Height = 30;
                infoLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
                infoLabel.Padding = new Padding(10, 0, 0, 0);

                // 中部文件列表
                AntdUI.Input listBox = new AntdUI.Input();
                listBox.Multiline = true;
                listBox.ReadOnly = true;
                listBox.WordWrap = false;
                listBox.Dock = DockStyle.Fill;
                listBox.Font = FontHelper.MonoFont(9f);
                listBox.Text = string.Join(Environment.NewLine, changes);

                // 底部按钮面板
                Panel buttonPanel = new Panel();
                buttonPanel.Dock = DockStyle.Bottom;
                buttonPanel.Height = 40;

                AntdUI.Button yesButton = new AntdUI.Button();
                yesButton.Text = LanguageManager.Instance.GetText("Common.ConfirmChangesYes", "继续(Y)");
                yesButton.DialogResult = DialogResult.Yes;
                yesButton.AutoSize = true;
                yesButton.Font = FontHelper.UiFont(12);
                yesButton.Height = 30;
                yesButton.Top = 5;

                AntdUI.Button noButton = new AntdUI.Button();
                noButton.Text = LanguageManager.Instance.GetText("Common.ConfirmChangesNo", "取消(N)");
                noButton.DialogResult = DialogResult.No;
                noButton.AutoSize = true;
                noButton.Font = FontHelper.UiFont(12);
                noButton.Height = 30;
                noButton.Top = 5;

                buttonPanel.Controls.Add(yesButton);
                buttonPanel.Controls.Add(noButton);

                // 按钮居中
                buttonPanel.Resize += (s, e) => {
                    yesButton.Left = (buttonPanel.Width - 220) / 2;
                    noButton.Left = yesButton.Left + 120;
                };

                dlg.Controls.Add(listBox);
                dlg.Controls.Add(infoLabel);
                dlg.Controls.Add(buttonPanel);

                dlg.AcceptButton = yesButton;
                dlg.CancelButton = noButton;

                var result = dlg.ShowDialog(this);
                return result == DialogResult.Yes;
            }
        }

        #endregion

        #region 设置集中管理窗

        private void ShowSettingsDialog()
        {
            using var dlg = new SettingsForm();
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                // 各设置项的持久化与即时刷新均在 SettingsForm 内完成
            }
            // 设置内触发重启（清除配置 / 主题缩放保存）：设置窗已关，这里关主窗体结束程序，
            // 由 Program.Main 在 Application.Run 返回后启动新进程（见 Program.PendingRestart）
            if (Program.PendingRestart) Close();
        }

        #endregion
    }
}