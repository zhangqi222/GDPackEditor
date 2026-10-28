using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace GDPackEditor
{
    /// <summary>
    /// 设置集中管理窗：主设置 / 编码 / 日志 / 模板设置 / 本地化dbr名 / 本地化dbr字段 / 本地化文件夹
    /// </summary>
    public class SettingsForm : AntdUI.BaseForm
    {
        private AntdUI.Radio _radioThemeSystem;
        private AntdUI.Radio _radioThemeLight;
        private AntdUI.Radio _radioThemeDark;
        private AntdUI.Radio _radioLangZh;
        private AntdUI.Radio _radioLangEn;
        /// <summary>「使用自定义语言」单选（选中表示用磁盘上的自定义语言包）</summary>
        private AntdUI.Radio _radioLangOther;
        /// <summary>导入自定义语言包的按钮（只校验+记路径，点「保存」才真正应用）</summary>
        private AntdUI.Button _btnImportLang;
        /// <summary>自定义语言包的完整路径显示（超长从前往后截断，始终保证文件名可见）</summary>
        private AntdUI.Label _lblLangPath;
        /// <summary>当前待应用的自定义语言包路径（来自配置或本次导入）</summary>
        private string _customLangPath;
        /// <summary>语言组里「导入语言文件 / 导出内置语言文件」两行的容器（只在选中「使用自定义语言」时显示）</summary>
        private AntdUI.Panel _langDetailPanel;
        // 「编码」页签：文件名 1 组（二选一）+ 内容 2 组（三选一：原样 / 统一本机ACP / 统一UTF-8）
        private AntdUI.Radio _radioNameAcp;            // 文件名：本机编码（官方行为）
        private AntdUI.Radio _radioNameFilterAscii;    // 文件名：过滤非ASCII
        private AntdUI.Radio _radioPackOriginal;       // 内容打包：原样（官方行为）
        private AntdUI.Radio _radioPackAnsi;           // 内容打包：包内统一本机ACP
        private AntdUI.Radio _radioPackUtf8;           // 内容打包：包内统一UTF-8 no BOM
        private AntdUI.Radio _radioUnpackOriginal;     // 内容解包：原样（官方行为）
        private AntdUI.Radio _radioUnpackAnsi;         // 内容解包：解出文件统一本机ACP
        private AntdUI.Radio _radioUnpackUtf8;         // 内容解包：解出文件统一UTF-8 no BOM
        /// <summary>打开设置窗时的编码三件套（用于 R10：检测"编码设置真的变了"才提醒临时文件）</summary>
        private string _openNameMode, _openPackMode, _openUnpackMode;
        /// <summary>打开设置窗时是否已存在编辑器/合并临时文件（R10 提醒只对"变化且存在"触发）</summary>
        private bool _encodingHadTempFiles;
        private readonly Dictionary<string, AntdUI.Radio> _accentRadios = new Dictionary<string, AntdUI.Radio>();
        private AntdUI.Switch _detailSwitch;
        private AntdUI.Switch _logDetailToFileSwitch;   // 「详情记录在日志文件中」：过程行是否写入日志文件（默认禁用，需先开 _detailSwitch）
        private AntdUI.Switch _fileLogSwitch;       // 「保存日志」：主日志文件是否写入
        private AntdUI.Switch _packageLogSwitch;    // 「单独保存操作日志」：各包日志文件是否写入
        private AntdUI.Switch _scaleSwitch;
        // 「记住表格列宽」4 表分开关（重置按钮即时执行不暂存；勾选状态随「保存」落盘）
        private AntdUI.Checkbox _twFileListCheckbox;
        private AntdUI.Checkbox _twDbrGridCheckbox;
        private AntdUI.Checkbox _twMergeTableCheckbox;
        private AntdUI.Checkbox _twSearchResultsCheckbox;
        /// <summary>「检测」页签的逐项开关（key = SettingsManager.Check* 属性名，保存时统一写回）</summary>
        private readonly Dictionary<string, AntdUI.Switch> _detectionSwitches = new Dictionary<string, AntdUI.Switch>();
        private AntdUI.InputNumber _logMaxLinesInput;
        private AntdUI.Button _openLogFolderBtn;
        /// <summary>「设置项」列统一宽度（按当前语言最长标签测量，使各行设置项与设置值左右对齐）</summary>
        private int _labelColumnWidth = 120;
        /// <summary>
        /// 单选按钮「圆点」相对控件左边缘的绘制内缩（AntdUI：h=(int)(文本高*0.8)，圆点左边缘=控件左+h/2）。
        /// 用于把「单选按钮 / 开关 / 按钮」三种设置值的可见左边缘统一对齐到同一条竖线。
        /// </summary>
        private int _radioVisualInset;
        private readonly List<Action> _applyActions = new List<Action>();
        private TemplateManagerForm _templateManager;

        private string GetText(string key, string fallback)
            => LanguageManager.Instance.GetText(key, fallback);

        public SettingsForm()
        {
            Text = GetText("Settings.Title", "设置");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            MinimumSize = new Size(640, 520);
            ClientSize = new Size(760, 580);
            // 默认宽度与主窗口默认宽度一致：按外框宽度对齐，两个窗口看起来一样宽（高度沿用上面的 ClientSize）
            Size = new Size(GuiMainForm.DefaultWindowSize.Width, Height);
            BackColor = ThemeManager.BgLayout;

            // 页签标题文字加大 2 号（仅页签标题，页签内设置项字体不受影响）
            var tabs = new AntdUI.Tabs
            {
                Dock = DockStyle.Fill,
                TabMenuVisible = true,
                Font = FontHelper.UiFont(11)
            };

            // 单选按钮圆点内缩（AntdUI IconRectL：h=(int)(文本高*0.8)，圆点左边缘 = 控件左 + h/2）。
            // 值控件左边缘对齐与标签内边距都要用它，故在任何页签构建之前先算好（此前放在主设置页里，页签一多就依赖构建顺序）。
            _radioVisualInset = (int)(TextRenderer.MeasureText("汉", FontHelper.UiFont(9)).Height * 0.8f) / 2;

            tabs.Pages.Add(BuildMainTab());
            tabs.Pages.Add(BuildEncodingTab());
            tabs.Pages.Add(BuildLogTab());
            tabs.Pages.Add(BuildDetectionTab());
            tabs.Pages.Add(BuildTemplateTab());
            tabs.Pages.Add(BuildLocalizeFileTab());
            tabs.Pages.Add(BuildLocalizeFieldTab());
            tabs.Pages.Add(BuildLocalizeFolderTab());

            // 页签标题加粗（用户定案：所有选项卡文字加粗）
            foreach (AntdUI.TabPage p in tabs.Pages)
                p.Font = FontHelper.UiFont(11, FontStyle.Bold);

            // 底部按钮区（AntdUI.Panel + Dock，WinForms 从后往前处理 Dock）
            // 尺寸与「清除配置」按钮一致（宽 90、高 30）
            var btnPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                Back = Color.Transparent,
                Padding = new Padding(10, 0, 10, 0)
            };
            var btnCancel = new AntdUI.Button
            {
AutoSize = true,
                Text = GetText("Common.Cancel", "取消"),
                
                Font = FontHelper.UiFont(9),
                Dock = DockStyle.Right,
                Margin = new Padding(6, 7, 0, 7),
                DialogResult = DialogResult.Cancel
            };
            var btnSave = new AntdUI.Button
            {
AutoSize = true,
                Text = GetText("Settings.Save", "保存"),
                
                Font = FontHelper.UiFont(9),
                Dock = DockStyle.Right,
                Margin = new Padding(0, 7, 0, 7),
                Type = AntdUI.TTypeMini.Primary
            };
            btnSave.Click += (s, e) => ApplyAndClose();
            // Dock=Right：先加取消、后加保存 → 反向处理后保存靠右、取消在其左
            btnPanel.Controls.Add(btnCancel);
            btnPanel.Controls.Add(btnSave);

            // 加入顺序：先 Fill（Tabs）→ 再 Bottom（按钮区）
            Controls.Add(tabs);
            Controls.Add(btnPanel);

            // ⚠ **刻意不设 AcceptButton**：本窗体里嵌了模板管理的**可编辑表格**（备注等单元格就地编辑），
            // 回车必须留给"确认本格编辑"；一旦把保存按钮设为 AcceptButton，回车会被窗体截走去保存并关闭，
            // 表格里刚敲的值就丢了（实测过的真实问题）。保存请点右下「保存」按钮。
            CancelButton = btnCancel;

            // 内嵌模板管理 Form 实例不作为独立窗口显示，随设置窗一并释放，避免泄漏
            Disposed += (s, e) => _templateManager?.Dispose();
        }

        // 行布局设计基准常量（设计值 100%；行高按控件实际尺寸自适应增长，适配任意系统缩放）
        private const int DesignRowHeight = 42;   // 行设计高度
        private const int DesignNameGap = 14;     // 「设置项」列与「设置值」列之间的间距
        private const int DesignValueGap = 14;    // 同一行多个值控件之间的间距

        /// <summary>
        /// 「设置项」列宽统一：取当前语言下所有标签文本的最大宽度 + 间距余量，
        /// 使各行设置项、各行设置值分别左右对齐（表格式对齐，但不使用表格控件）
        /// </summary>
        private void InitLabelColumnWidth(params string[] labelTexts)
        {
            var font = FontHelper.UiFont(9);
            int max = 0;
            foreach (string text in labelTexts)
            {
                int w = TextRenderer.MeasureText(text, font).Width;
                if (w > max) max = w;
            }
            _labelColumnWidth = max + DpiManager.S(DesignNameGap);
        }

        /// <summary>
        /// 行左侧「设置项」标签（Dock=Left，统一固定列宽；默认垂直居中，topPadding&gt;0 时改为顶部对齐）。
        /// 文字<b>左对齐</b>：同类设置的名称前缀相同（如两行都以「打包ARC时」开头）时，
        /// 左对齐才能一眼看出它们属于同一类；右对齐会因文字长短不同而让开头参差不齐。
        /// 右侧仍留固定内边距，使「文字 → 设置值」的间距一致。
        /// </summary>
        private AntdUI.Label MakeLabel(string text, int topPadding = 0)
            => new AntdUI.Label
            {
                Text = text,
                AutoSize = false,
                Width = _labelColumnWidth,
                Dock = DockStyle.Left,
                TextAlign = topPadding > 0 ? ContentAlignment.TopLeft : ContentAlignment.MiddleLeft,
                // 右内边距扣掉单选按钮圆点内缩，使「文字 → 可见值图形」的间距与其它值类型一致
                Padding = new Padding(0, topPadding, Math.Max(0, DpiManager.S(DesignNameGap) - _radioVisualInset), 0),
                // 设置项名称加粗（用户定案：与右侧值控件区分层级）
                Font = FontHelper.UiFont(9, FontStyle.Bold),
                ForeColor = ThemeManager.Text
            };

        /// <summary>
        /// 设置项的「下一行说明」：整行宽、正常文字色、正体、自动换行（AntdUI.Label 默认多行）。
        /// 与「设置项 / 设置值」靠"另起一行"区分，不改颜色、不用斜体。
        /// 与上一行的「设置项 + 设置值」之间**不画分隔线**，使"设置项 → 设置值 → 说明"在视觉上属于同一块；
        /// 需要与下一组设置分隔时，把分隔线画在本说明行的底部（withDivider）。
        /// 高度按换行后的实际行数自适应（宽度变化时重算）。
        /// </summary>
        private AntdUI.Panel BuildDescRow(string text, bool withDivider)
        {
            int dividerH = withDivider ? DpiManager.S(1) : 0;
            var row = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = DpiManager.S(DesignRowHeight),
                Back = Color.Transparent,
                Margin = new Padding(0)
            };

            var label = new AntdUI.Label
            {
                Text = text,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                // 正体（用户反馈斜体看着费劲）：说明靠"另起一行"与设置项/设置值区分
                Font = FontHelper.UiFont(9f),
                ForeColor = ThemeManager.Text,   // 正常文字色：说明是正文，不做灰色弱化
                // 说明整体左侧缩进 20px（用户定案）：与上方的设置项/设置值明显错位，一眼看出层级
                Padding = new Padding(DpiManager.S(20), 0, DpiManager.S(DesignNameGap), 0)
            };
            row.Controls.Add(label);

            if (withDivider)
            {
                row.Controls.Add(new AntdUI.Divider
                {
                    Dock = DockStyle.Bottom,
                    Height = DpiManager.S(1),
                    Thickness = DpiManager.SF(1f),
                    ColorSplit = ThemeManager.BorderColor
                });
            }

            // 高度 = 文字换行后的实际高度（值未变化则不赋值，避免布局循环）
            void FitHeight()
            {
                int availW = row.ClientSize.Width - label.Padding.Horizontal;
                if (availW <= 0 || string.IsNullOrEmpty(text)) return;
                int textH = TextRenderer.MeasureText(text, label.Font, new Size(availW, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
                int h = textH + DpiManager.S(6) + dividerH;
                if (row.Height != h) row.Height = h;
            }
            row.SizeChanged += (s, e) => FitHeight();
            return row;
        }

        /// <summary>
        /// 控件可见图形相对其左边缘的额外内缩（ReadRectangle 之外的部分）：
        /// Radio 的圆点由 AntdUI 的 IconRectL 绘制，会再向内缩 (int)(文本高×0.8)/2 ≈ 6px；其它控件为 0。
        /// </summary>
        private int VisibleInset(AntdUI.IControl control)
            => control is AntdUI.Radio ? _radioVisualInset : 0;

        /// <summary>
        /// 生成一行设置：左侧统一列宽的「设置项」名称 + 右侧「设置值」控件（可多个，左到右排列）。
        /// 对齐保证（不依赖控件类型，任意缩放下均成立）：
        /// 1. 值控件 X 反向补偿其自身绘制内边距（ReadRectangle.X：Radio=0 / Switch=WaveSize×Dpi / Button=(WaveSize+边框/2)×Dpi），
        ///    使不同控件的可见左边缘落在同一条竖线上（此前开关行比单选行右偏约 7px 的根因）；
        /// 2. 值控件按行内容高度垂直居中，与名称文字同一水平中线；
        /// 3. 行高按最高值控件自适应增长，缩放后控件变大也不会被裁切或错位。
        /// 加入顺序：值控件（绝对定位）→ 名称标签（Dock=Left）→ 分隔线（Dock=Bottom，最后加入最先处理 Dock，占满整行宽）。
        /// </summary>
        private AntdUI.Panel BuildRow(string labelText, bool withDivider, params Control[] values)
        {
            var row = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = DesignRowHeight,
                Back = Color.Transparent,
                Margin = new Padding(0)
            };

            // 行内容高度：按最高值控件自适应（设计行高为下限），避免缩放后控件被裁切或挤压
            // 注意：AntdUI 控件没有默认尺寸（未覆盖 WinForms 的 DefaultSize），绝对定位前未设尺寸会高度为 0 而不可见，这里按设计高度兜底
            int contentHeight = 0;
            foreach (AntdUI.IControl v in values)
            {
                if (v.Height <= 0) v.Height = DpiManager.S(30);
                contentHeight = Math.Max(contentHeight, v.Height);
            }
            int dividerHeight = withDivider ? DpiManager.S(1) : 0;
            int innerHeight = DesignRowHeight - dividerHeight;
            int needHeight = contentHeight + DpiManager.S(8);
            if (needHeight > innerHeight)
            {
                row.Height = needHeight + dividerHeight;
                innerHeight = needHeight;
            }

            // 值控件：X 反向补偿「控件左边缘 → 可见图形」的全部内缩（ReadRectangle + Radio 圆点内缩），
            // 使三种值类型（单选按钮/开关/按钮）的可见左边缘落在同一条竖线上；Y 垂直居中（绝对定位，不参与 Dock）
            int valueAnchorX = _labelColumnWidth + _radioVisualInset;
            int x = valueAnchorX;
            foreach (AntdUI.IControl v in values)
            {
                row.Controls.Add(v);
                v.Location = new Point(x - v.ReadRectangle.X - VisibleInset(v), (innerHeight - v.Height) / 2);
                x += v.Width + DpiManager.S(DesignValueGap);
            }

            // 名称标签：Dock=Left 占左侧固定列宽（值控件为绝对定位，不受 Dock 处理顺序影响）
            row.Controls.Add(MakeLabel(labelText));

            // 行分隔线（AntdUI 原生 Divider）：最后加入 → Dock 处理顺序最先 → 占满整行宽度底部；末行不画线
            if (withDivider)
            {
                row.Controls.Add(new AntdUI.Divider
                {
                    Dock = DockStyle.Bottom,
                    Height = DpiManager.S(1),
                    Thickness = DpiManager.SF(1f),
                    ColorSplit = ThemeManager.BorderColor
                });
            }
            return row;
        }

        /// <summary>
        /// 生成单选按钮（AutoSize 自适应尺寸，位置由 BuildRow 统一排布）
        /// </summary>
        private AntdUI.Radio MakeRadio(string text, bool isChecked)
            => new AntdUI.Radio
            {
                Text = text,
                Checked = isChecked,
                AutoSize = true,
                Font = FontHelper.UiFont(9)
            };

        /// <summary>「记住表格列宽」组的表名复选框</summary>
        private AntdUI.Checkbox MakeTwCheckbox(string text, bool isChecked)
            => new AntdUI.Checkbox
            {
                Text = text,
                Checked = isChecked,
                AutoSize = true,
                Font = FontHelper.UiFont(9)
            };

        /// <summary>「记住表格列宽」组的单独重置按钮（即时执行：仅清记录，下次开窗生效；设置页无表格实例）</summary>
        private AntdUI.Button MakeTwResetButton(TableWidthStore.TableId id)
        {
            var btn = new AntdUI.Button
            {
                AutoSize = true,
                Text = GetText("Settings.TableWidthReset", "重置"),
                Font = FontHelper.UiFont(9)
            };
            btn.Click += (s, e) => TableWidthStore.Reset(id);
            return btn;
        }

        /// <summary>
        /// 导入自定义语言包：**只做校验 + 记住路径 + 回显路径 + 勾选「自定义」**，
        /// 不立即切换、不落盘 —— 尊重设置窗"点保存才生效、取消不生效"的既有语义。
        /// </summary>
        private void ImportCustomLanguage()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = GetText("Settings.ImportLanguageTitle", "导入自定义语言包"),
                Filter = GetText("Settings.LanguageJsonFilter", "JSON 语言文件|*.json")
                         + "|" + GetText("Editor.AllFilesFilter", "所有文件|*.*"),
                CheckFileExists = true
            })
            {
                if (!string.IsNullOrEmpty(_customLangPath))
                {
                    try { dlg.InitialDirectory = Path.GetDirectoryName(_customLangPath); } catch { }
                }

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                if (!LanguageManager.ValidateLanguageFile(dlg.FileName, out string error))
                {
                    MessageBox.Show(this,
                        string.Format(GetText("Settings.CustomLanguageLoadFail", "自定义语言包加载失败：{0}"), error),
                        GetText("Common.MessageBoxTitle.Error", "错误"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                _customLangPath = dlg.FileName;
                _radioLangOther.Checked = true;
                UpdateLangPathText(_lblLangPath.Width);
            }
        }

        /// <summary>导出两份内置语言包（ZH.json / EN.json）到所选目录</summary>
        private void ExportBuiltInLanguagePacks()
        {
            using (var dlg = new FolderBrowserDialog
            {
                Description = GetText("Settings.ExportLanguageFolderTitle", "选择导出目录"),
                ShowNewFolderButton = true
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                // 目标已有同名文件时先确认一次（覆盖写）
                string zhPath = Path.Combine(dlg.SelectedPath, LanguageManager.GetBuiltInFileName(LanguageManager.BuiltInZh));
                string enPath = Path.Combine(dlg.SelectedPath, LanguageManager.GetBuiltInFileName(LanguageManager.BuiltInEn));
                if ((File.Exists(zhPath) || File.Exists(enPath)) &&
                    MessageBox.Show(this,
                        GetText("Settings.ExportLanguageOverwrite", "目标文件已存在，是否覆盖？"),
                        GetText("Common.MessageBoxTitle.Confirm", "确认"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                if (LanguageManager.TryExportBuiltInPacks(dlg.SelectedPath, out string error, out var written))
                {
                    MessageBox.Show(this,
                        string.Format(GetText("Settings.ExportLanguageSuccess", "已导出：{0}"), string.Join("、", written)),
                        GetText("Common.MessageBoxTitle.Info", "提示"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this,
                        string.Format(GetText("Settings.ExportLanguageFail", "导出失败：{0}"), error),
                        GetText("Common.MessageBoxTitle.Error", "错误"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// 刷新「自定义语言文件」的路径显示：显示**完整路径**，超出可用宽度时**从前往后截断**，
        /// 始终保证文件名可见（G:\a\b\languages\jp.json → …\languages\jp.json → …\jp.json）。
        /// 不能用 AutoEllipsis：那是截**尾部**，会把最该看的文件名截掉。
        /// </summary>
        private void UpdateLangPathText(int availableWidth)
        {
            if (_lblLangPath == null || _lblLangPath.IsDisposed) return;

            _lblLangPath.Width = Math.Max(DpiManager.S(80), availableWidth);

            if (string.IsNullOrEmpty(_customLangPath))
            {
                _lblLangPath.Text = GetText("Settings.CustomLanguageNone", "（未选择）");
                _lblLangPath.ForeColor = ThemeManager.TextTertiary;
                return;
            }

            _lblLangPath.ForeColor = ThemeManager.Text;
            _lblLangPath.Text = FitPathFromTail(_customLangPath, _lblLangPath.Font, _lblLangPath.Width);
        }

        /// <summary>
        /// 路径超长时从前往后丢目录段；连文件名都放不下才对文件名做尾部省略。
        /// </summary>
        private static string FitPathFromTail(string path, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            if (TextRenderer.MeasureText(path, font).Width <= maxWidth) return path;

            const string ellipsis = "…";
            string[] parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1)
                return FitTextFromTail(path, font, maxWidth);

            // 逐级保留更多尾部目录段：…\languages\jp.json → …\jp.json
            for (int keep = 1; keep <= parts.Length; keep++)
            {
                string candidate = ellipsis + "\\" + string.Join("\\", parts, parts.Length - keep, keep);
                if (TextRenderer.MeasureText(candidate, font).Width <= maxWidth) return candidate;
            }

            // 连文件名都放不下：对文件名本身做尾部省略
            return FitTextFromTail(parts[parts.Length - 1], font, maxWidth);
        }

        /// <summary>普通文本超宽时保留**开头**、尾部省略（与 AutoEllipsis 同向，仅用于兜底）</summary>
        private static string FitTextFromTail(string text, Font font, int maxWidth)
        {
            const string ellipsis = "…";
            for (int len = text.Length - 1; len >= 1; len--)
            {
                string candidate = text.Substring(0, len) + ellipsis;
                if (TextRenderer.MeasureText(candidate, font).Width <= maxWidth) return candidate;
            }
            return ellipsis;
        }

        /// <summary>
        /// 页签 1：主设置（明暗 / 色系 / 语言 / 自动缩放 / 清除配置；日志项见「日志」页签，打包ARC项见「编码」页签）
        /// </summary>
        private AntdUI.TabPage BuildMainTab()
        {
            var page = new AntdUI.TabPage
            {
                Text = GetText("Settings.MainTab", "主设置"),
                Name = "Settings.MainTab"
            };
            // 内容区左右各缩进（比页签标题窄），形成"标题宽、内容略窄"的层次
            var stack = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent,
                Padding = new Padding(25, 10, 25, 10)
            };

            // 行反向插入（§6.6：最后添加的在最上）：视觉自上而下 = 明暗 → 色系 → 语言 → 缩放 → 清除
            // （日志项见「日志」页签；两个打包ARC相关设置见「编码」页签）
            InitLabelColumnWidth(
                GetText("Settings.ThemeMode", "明暗模式"),
                GetText("Settings.Accent", "色系"),
                GetText("Settings.Language", "语言"),
                GetText("Settings.UiScale", "自动缩放"),
                GetText("Settings.TableWidth", "记住表格列宽"),
                GetText("Settings.ClearConfig", "清除配置"));

            // 清除配置（立即执行，红色按钮）—— 最先添加，显示在最下（末行不画分隔线）
            var btnClear = new AntdUI.Button
            {
AutoSize = true,
                Text = GetText("Settings.ClearConfigButton", "清除配置"),
                // 必须显式设尺寸：AntdUI 控件无默认尺寸
                Font = FontHelper.UiFont(9),
                Type = AntdUI.TTypeMini.Error
            };
            btnClear.Click += (s, e) => ClearSettings();
            stack.Controls.Add(BuildRow(GetText("Settings.ClearConfig", "清除配置"), false, btnClear));

            // 记住表格列宽（2×2：每行 2 组「表名复选 + 重置」）——视觉在「自动缩放」下方、「清除配置」上方。
            // 反向插入：先加第 2 行（视觉在下、画底线与「清除配置」分隔），再加第 1 行（带名称标签）。
            _twMergeTableCheckbox = MakeTwCheckbox(GetText("Settings.TwMergeTable", "合并表"), SettingsManager.TwEnabledMergeTable);
            _twSearchResultsCheckbox = MakeTwCheckbox(GetText("Settings.TwSearchResults", "包内搜索结果"), SettingsManager.TwEnabledSearchResults);
            stack.Controls.Add(BuildRow("", true,
                _twMergeTableCheckbox, MakeTwResetButton(TableWidthStore.TableId.MergeTable),
                _twSearchResultsCheckbox, MakeTwResetButton(TableWidthStore.TableId.SearchResults)));

            _twFileListCheckbox = MakeTwCheckbox(GetText("Settings.TwFileList", "文件列表"), SettingsManager.TwEnabledFileList);
            _twDbrGridCheckbox = MakeTwCheckbox(GetText("Settings.TwDbrGrid", "DBR编辑器"), SettingsManager.TwEnabledDbrGrid);
            stack.Controls.Add(BuildRow(GetText("Settings.TableWidth", "记住表格列宽"), false,
                _twFileListCheckbox, MakeTwResetButton(TableWidthStore.TableId.FileList),
                _twDbrGridCheckbox, MakeTwResetButton(TableWidthStore.TableId.DbrGrid)));

            // 自动缩放（重启生效）
            _scaleSwitch = new AntdUI.Switch { Checked = SettingsManager.UiScaleEnabled, Size = new Size(40, 22) };
            stack.Controls.Add(BuildRow(GetText("Settings.UiScale", "自动缩放"), true, _scaleSwitch));

            // ── 语言组：语言行 + 下面两行（导入 / 导出）视觉上是一整块 ──
            // 组内行与行之间不画分隔线；只在整组底部画一条线，与下方的「自动缩放」分隔。
            // 下面两行只在选中「使用自定义语言」时才显示（见 UpdateLanguageGroupVisibility）。

            // 组底部分隔线（独立一行，不随下面两行的显隐变化）
            var langGroupDivider = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = DpiManager.S(1),
                Back = Color.Transparent,
                Margin = new Padding(0)
            };
            langGroupDivider.Controls.Add(new AntdUI.Divider
            {
                Dock = DockStyle.Bottom,
                Height = DpiManager.S(1),
                Thickness = DpiManager.SF(1f),
                ColorSplit = ThemeManager.BorderColor
            });
            stack.Controls.Add(langGroupDivider);

            // 下面两行统一放进一个容器，**容器内用绝对定位固定顺序：导出在上、导入在下**。
            // 为什么不靠 Dock 排这两行：它们初始可能是隐藏的（选内置语言时），而"隐藏→再显示"之后
            // Dock 处理顺序会变，导致"先点开自定义"与"打开时本来就是自定义"两种情况下顺序相反（用户反馈）。
            var langDetailPanel = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Back = Color.Transparent,
                Margin = new Padding(0)
            };
            _langDetailPanel = langDetailPanel;

            // 导出内置语言文件（按钮宽度随文案自适应：各语言长短不一，写死宽度迟早不够）
            // 【不要设 Padding】AntdUI 的可见按钮矩形 = ClientRectangle.PaddingRect(Padding)，
            // 而自动宽度只按文字算（文字宽 + 自带边距），不吃 Padding —— 设了会把画出来的按钮缩窄，
            // 把自带边距吃光，文字直接贴边。
            var btnExportLang = new AntdUI.Button
            {
                Text = GetText("Settings.ExportLanguage", "导出内置语言文件（2份）"),
                AutoSize = true,
                MinimumSize = new Size(0, DpiManager.S(30)),
                Font = FontHelper.UiFont(9)
            };
            btnExportLang.Click += (s, e) => ExportBuiltInLanguagePacks();
            var langRowExport = BuildRow(string.Empty, false, btnExportLang);

            // 导入语言文件 + 完整路径（只为「导入」服务；点「保存」才真正生效）
            _btnImportLang = new AntdUI.Button
            {
                Text = GetText("Settings.ImportLanguageButton", "导入语言文件"),
                AutoSize = true,
                MinimumSize = new Size(0, DpiManager.S(30)),
                Font = FontHelper.UiFont(9)
            };
            _btnImportLang.Click += (s, e) => ImportCustomLanguage();

            _customLangPath = SettingsManager.Language != null && SettingsManager.Language.IsCustom
                ? SettingsManager.Language.Path
                : null;

            _lblLangPath = new AntdUI.Label
            {
                Text = string.Empty,
                AutoSize = false,
                Width = DpiManager.S(300),      // 首帧占位，实际宽度在行的 Layout 里按剩余空间重算
                Height = DpiManager.S(30),
                Font = FontHelper.UiFont(9),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ThemeManager.Text
            };
            UpdateLangPathText(_lblLangPath.Width);

            var langRowImport = BuildRow(string.Empty, false, _btnImportLang, _lblLangPath);
            // 行宽变化（窗口缩放 / 150% 缩放 / 按钮文案换语言后宽度变化）时重算路径可用宽度
            langRowImport.Layout += (s, e) =>
            {
                int avail = langRowImport.ClientSize.Width - _labelColumnWidth
                            - _btnImportLang.Width - DpiManager.S(DesignNameGap) * 2;
                UpdateLangPathText(avail);
            };

            // 容器内固定顺序（绝对定位，不走 Dock）：导出在上、导入在下
            langRowExport.Dock = DockStyle.None;
            langRowImport.Dock = DockStyle.None;
            langRowExport.Location = new Point(0, 0);
            langRowImport.Location = new Point(0, langRowExport.Height);
            langDetailPanel.Height = langRowExport.Height + langRowImport.Height;
            langDetailPanel.Controls.Add(langRowExport);
            langDetailPanel.Controls.Add(langRowImport);
            // 行内是绝对定位排版，宽度必须显式给：容器宽度变化时同步两行宽度
            langDetailPanel.Layout += (s, e) =>
            {
                int w = langDetailPanel.ClientSize.Width;
                if (langRowExport.Width != w) langRowExport.Width = w;
                if (langRowImport.Width != w) langRowImport.Width = w;
            };
            stack.Controls.Add(langDetailPanel);

            // 语言（即时生效；三选一：简体中文 / English / 使用自定义语言）
            string curLang = LanguageManager.Instance.GetInitialLanguageCode();
            bool isCustom = string.Equals(curLang, LanguageManager.CustomCode, StringComparison.OrdinalIgnoreCase);
            bool isEn = !isCustom && string.Equals(curLang, LanguageManager.BuiltInEn, StringComparison.OrdinalIgnoreCase);
            _radioLangZh = MakeRadio("简体中文", !isEn && !isCustom);
            _radioLangEn = MakeRadio("English", isEn);
            _radioLangOther = MakeRadio(GetText("Settings.LanguageUseCustom", "使用自定义语言"), isCustom);
            // 选「自定义」才显示下面两行
            foreach (var radio in new[] { _radioLangZh, _radioLangEn, _radioLangOther })
                radio.CheckedChanged += (s, e) => UpdateLanguageGroupVisibility();
            stack.Controls.Add(BuildRow(GetText("Settings.Language", "语言"), false,
                _radioLangZh, _radioLangEn, _radioLangOther));

            UpdateLanguageGroupVisibility();

            // 色系（重启生效）：单选按钮带对应颜色，超宽自动换行（FlowPanel），单独构建（见下）
            var accentFlow = new AntdUI.FlowPanel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent,
                Gap = DesignValueGap,
                // AntdUI 默认 Align=LeftCenter 会在换行时把整行水平居中（首行右移），必须显式左对齐
                Align = AntdUI.TAlignFlow.Left
            };
            // 反向插入：倒序添加后显示顺序 = 默认蓝 → 林塘-金 → 翡翠绿 → 紫罗兰 → 琥珀橙 → 红 → 青
            AddAccentRadio(accentFlow, "cyan", GetText("MainForm.AccentCyan", "青"));
            AddAccentRadio(accentFlow, "red", GetText("MainForm.AccentRed", "红"));
            AddAccentRadio(accentFlow, "amber", GetText("MainForm.AccentAmber", "琥珀橙"));
            AddAccentRadio(accentFlow, "violet", GetText("MainForm.AccentViolet", "紫罗兰"));
            AddAccentRadio(accentFlow, "emerald", GetText("MainForm.AccentEmerald", "翡翠绿"));
            AddAccentRadio(accentFlow, "lintan", GetText("MainForm.AccentLintan", "林塘-金"));
            AddAccentRadio(accentFlow, "default", GetText("MainForm.AccentDefault", "默认蓝"));

            // 行高按「实际换行行数」动态计算：不超宽 = 单行紧凑高度，超宽 = 两行（首行先按单行设置，布局后自动校正）
            int accentTop = DpiManager.S(6);
            int accentRadioH = _accentRadios["default"].Height;
            var accentRow = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = accentTop * 2 + accentRadioH,
                Back = Color.Transparent,
                Margin = new Padding(0)
            };
            accentRow.Controls.Add(accentFlow);

            // 标签顶部内边距 = 首行单选按钮顶部位置 + (单选按钮高 - 标签单行文字高) / 2，与文字中线精确对齐
            int labelLineH = TextRenderer.MeasureText(GetText("Settings.Accent", "色系"), FontHelper.UiFont(9)).Height;
            int accentLabelPad = accentTop + Math.Max(0, (accentRadioH - labelLineH) / 2);
            accentRow.Controls.Add(MakeLabel(GetText("Settings.Accent", "色系"), accentLabelPad));

            accentRow.Controls.Add(new AntdUI.Divider
            {
                Dock = DockStyle.Bottom,
                Height = DpiManager.S(1),
                Thickness = DpiManager.SF(1f),
                ColorSplit = ThemeManager.BorderColor
            });

            // 尺寸变化（首次布局 / 窗口缩放）时按实际换行行数重算行高；值未变化则不赋值，避免布局循环
            accentFlow.SizeChanged += (s, e) =>
            {
                int h = ComputeAccentRowHeight(accentFlow.ClientSize.Width);
                if (h > 0 && accentRow.Height != h) accentRow.Height = h;
            };
            stack.Controls.Add(accentRow);

            // 明暗模式（重启生效，默认跟随系统）—— 最后添加，显示在最上
            string curTheme = string.IsNullOrEmpty(SettingsManager.ThemeMode) ? "System" : SettingsManager.ThemeMode;
            _radioThemeSystem = MakeRadio(GetText("MainForm.ThemeSystem", "跟随系统"), string.Equals(curTheme, "System", StringComparison.OrdinalIgnoreCase));
            _radioThemeLight = MakeRadio(GetText("MainForm.ThemeLight", "亮色"), string.Equals(curTheme, "Light", StringComparison.OrdinalIgnoreCase));
            _radioThemeDark = MakeRadio(GetText("MainForm.ThemeDark", "暗色"), string.Equals(curTheme, "Dark", StringComparison.OrdinalIgnoreCase));
            stack.Controls.Add(BuildRow(GetText("Settings.ThemeMode", "明暗模式"), true, _radioThemeSystem, _radioThemeLight, _radioThemeDark));

            page.Controls.Add(stack);
            return page;
        }

        /// <summary>
        /// 页签：编码（**文件名 1 组 + 内容 2 组**）。
        /// 模型与官方行为见 plan_20260918_恐怖黎明包文件编码完善方案.md §3：
        /// 三条规则各自独立设置，且三项**都同时管 ARC 与 ARZ**（不再只作用打包ARC）。
        /// 名字与内容是两套独立机制（名字跟机器走、内容零转码为底），故分组展示、不混排。
        /// </summary>
        private AntdUI.TabPage BuildEncodingTab()
        {
            var page = new AntdUI.TabPage
            {
                Text = GetText("Settings.EncodingTab", "编码"),
                Name = "Settings.EncodingTab"
            };
            var stack = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent,
                Padding = new Padding(25, 10, 25, 10)
            };

            // 本机码页的动态显示名（如「GBK（CP936）」）：所有涉及"本机 ACP"的文案一律动态拼接
            string acp = NameCodec.AcpFriendlyName;

            // 设置项列宽按本页最长标签测量
            InitLabelColumnWidth(
                GetText("Settings.EncContentPack", "内容-打包"),
                GetText("Settings.EncContentUnpack", "内容-解包"),
                GetText("Settings.EncFileName", "文件名-打/解包"));

            // 行反向插入（§6.6：最后添加的在最上）：视觉自上而下 =
            // [文件名 行] [其说明] [内容打包 行] [其说明] [内容解包 行] [其说明]
            // 分隔线画在每段"说明行"底部，用于隔开下一组

            // ── 组③ 文件内容解包（最下）──
            bool unpackOrig = SettingsManager.ContentUnpackMode == SettingsManager.ContentModeOriginal;
            bool unpackAnsi = SettingsManager.ContentUnpackMode == SettingsManager.ContentModeAnsi;
            // R10：记下打开设置窗时的编码三件套与"是否有活跃临时文件"，供保存时判断
            _openNameMode = SettingsManager.NameMode;
            _openPackMode = SettingsManager.ContentPackMode;
            _openUnpackMode = SettingsManager.ContentUnpackMode;
            _encodingHadTempFiles = TempSessionManager.CountLiveEditorSessions() > 0 || TempSessionManager.HasMergeTempFiles();
            _radioUnpackOriginal = MakeRadio(GetText("Settings.EncUnpackOriginal", "原样（官方行为）"), unpackOrig);
            _radioUnpackAnsi = MakeRadio(string.Format(GetText("Settings.EncUnpackAnsi", "解出文件统一：{0}"), acp), unpackAnsi);
            _radioUnpackUtf8 = MakeRadio(GetText("Settings.EncUnpackUtf8", "解出文件统一：UTF-8 no BOM"),
                !unpackOrig && !unpackAnsi);
            stack.Controls.Add(BuildDescRow(GetText("Settings.EncUnpackDesc",
                "官方行为：原样解包，不统一转码。（存在多文件不同编码的问题）\n统一：统一转码\n仅影响txt/lua/dbr/tpl文件"),
                withDivider: false));   // 末行：不画分隔线
            stack.Controls.Add(BuildRow(GetText("Settings.EncContentUnpack", "内容-解包"),
                false, _radioUnpackOriginal, _radioUnpackAnsi, _radioUnpackUtf8));

            // ── 组② 文件内容打包（中间）──
            bool packOrig = SettingsManager.ContentPackMode == SettingsManager.ContentModeOriginal;
            bool packAnsi = SettingsManager.ContentPackMode == SettingsManager.ContentModeAnsi;
            _radioPackOriginal = MakeRadio(GetText("Settings.EncPackOriginal", "原样（官方行为）"), packOrig);
            _radioPackAnsi = MakeRadio(string.Format(GetText("Settings.EncPackAnsi", "包内统一：{0}"), acp), packAnsi);
            _radioPackUtf8 = MakeRadio(GetText("Settings.EncPackUtf8", "包内统一：UTF-8 no BOM"), !packOrig && !packAnsi);
            stack.Controls.Add(BuildDescRow(GetText("Settings.EncPackDesc",
                "官方行为：原样入包，不统一转码。（存在多文件不同编码的问题）\n统一：统一转码\n仅影响txt/lua/dbr/tpl文件"),
                withDivider: true));
            stack.Controls.Add(BuildRow(GetText("Settings.EncContentPack", "内容-打包"),
                false, _radioPackOriginal, _radioPackAnsi, _radioPackUtf8));

            // ── 组① 文件名（最上）──
            bool nameAcp = SettingsManager.NameMode != SettingsManager.NameModeFilterAscii;
            _radioNameAcp = MakeRadio(string.Format(GetText("Settings.EncNameAcp", "本机编码（官方行为）：{0}"), acp), nameAcp);
            _radioNameFilterAscii = MakeRadio(GetText("Settings.EncNameFilterAscii", "过滤非ASCII文件名"), !nameAcp);
            stack.Controls.Add(BuildDescRow(GetText("Settings.EncNameDesc",
                "官方行为：打/解包本机可识别的文件名，如【中文.txt】【aaa-备份.dbr】；\n过滤：打/解包时忽略。"),
                withDivider: true));
            stack.Controls.Add(BuildRow(GetText("Settings.EncFileName", "文件名-打/解包"),
                false, _radioNameAcp, _radioNameFilterAscii));

            page.Controls.Add(stack);
            return page;
        }

        /// <summary>
        /// 计算「色系」行的实际高度：按 AntdUI.FlowPanel 的换行规则复算行数（不超宽=单行，超宽=多行）。
        /// FlowPanel 内部：累计 Width+Margin.Horizontal 超过可用即换行，use_y += 行高 + Gap + Margin.Vertical；
        /// Gap 由 FlowPanel 内部按 Dpi 换算，子控件 Margin 为原始像素；显示顺序为添加顺序的倒序。
        /// </summary>
        private int ComputeAccentRowHeight(int availWidth)
        {
            if (availWidth <= 0 || _accentRadios.Count == 0) return 0;

            int top = DpiManager.S(6);                                    // 单选项顶部间距（与 AddAccentRadio 一致）
            int gap = (int)Math.Round(DesignValueGap * DpiManager.Scale);  // 与 FlowPanel 内部 Gap 换算方式一致
            int useX = 0, lineMaxH = 0, y = top;

            var list = new List<AntdUI.Radio>(_accentRadios.Values);
            list.Reverse();                                               // 显示顺序 = 添加顺序倒序
            foreach (var radio in list)
            {
                int w = radio.Width + radio.Margin.Horizontal;
                if (useX > 0 && useX + w > availWidth)
                {
                    y += lineMaxH + gap + radio.Margin.Vertical;
                    useX = 0;
                    lineMaxH = 0;
                }
                useX += w + gap;
                lineMaxH = Math.Max(lineMaxH, radio.Height);
            }
            return y + lineMaxH + top + DpiManager.S(1);                   // 内容高 + 底部留白 + 分隔线高度
        }

        /// <summary>
        /// 生成色系单选按钮：选中色 = 该主色方案的实际颜色，文字也着同色，便于用户一眼区分。
        /// 左边距为 0，保证首个单选按钮圆形与「设置值」列左边缘对齐；行间距由 FlowPanel.Gap 控制。
        /// </summary>
        private void AddAccentRadio(AntdUI.FlowPanel flow, string accentName, string text)
        {
            Color accentColor = ThemeManager.GetAccentColor(accentName, ThemeManager.IsDark);
            var radio = new AntdUI.Radio
            {
                Text = text,
                Checked = string.Equals(SettingsManager.ThemeAccent, accentName, StringComparison.OrdinalIgnoreCase),
                AutoSize = true,
                Fill = accentColor,
                ForeColor = accentColor,
                Margin = new Padding(0, DpiManager.S(6), 0, 0),
                Font = FontHelper.UiFont(9)
            };
            _accentRadios[accentName] = radio;
            flow.Add(radio);
        }

        /// <summary>
        /// 页签 2：日志（日志显示详情 / 日志最大行数 / 报告保留天数 / 日志目录 / 清除日志）。
        /// 从「主设置」整体迁入，布局与主设置完全一致（同左右缩进、统一标签列宽、行分隔线风格）。
        /// </summary>
        private AntdUI.TabPage BuildLogTab()
        {
            var page = new AntdUI.TabPage
            {
                Text = GetText("Settings.LogTab", "日志"),
                Name = "Settings.LogTab"
            };
            var stack = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent,
                Padding = new Padding(25, 10, 25, 10)
            };

            // 行反向插入（§6.6：最后添加的在最上）：视觉自上而下 =
            // 保存日志 → 单独保存操作日志 → 日志显示详情 → 最大行数 → 日志目录 → 清除日志
            // （"报告保留天数"已按用户决定移除：各包日志与主日志共用 LogMaxLines 行数上限）
            InitLabelColumnWidth(
                GetText("Settings.LogFileEnabled", "保存日志"),
                GetText("Settings.PackageLogEnabled", "单独保存操作日志"),
                GetText("Settings.ShowDetailLog", "日志显示详情"),
                GetText("Settings.LogMaxLines", "日志最大行数"),
                GetText("Settings.LogFolder", "日志目录"),
                GetText("Settings.ClearLog", "清除日志"));

            // 清除日志（立即执行，红色按钮，与「清除配置」同一范式）—— 最先添加，显示在最下（末行不画分隔线）
            var btnClearLog = new AntdUI.Button
            {
AutoSize = true,
                Text = GetText("Settings.ClearLogButton", "清除日志"),
                // 必须显式设尺寸：AntdUI 控件无默认尺寸
                Font = FontHelper.UiFont(9),
                Type = AntdUI.TTypeMini.Error
            };
            btnClearLog.Click += (s, e) => ClearLogFiles();
            stack.Controls.Add(BuildRow(GetText("Settings.ClearLog", "清除日志"), false, btnClearLog));

            // 打开日志目录（便于用户自查/取证）
            _openLogFolderBtn = new AntdUI.Button
            {
                Text = GetText("Settings.OpenLogFolder", "打开日志目录"),
                Font = FontHelper.UiFont(9),
                
                AutoSizeMode = AntdUI.TAutoSize.Width
            };
            _openLogFolderBtn.Click += (s, e) =>
            {
                try
                {
                    string dir = LogService.LogDirectory;
                    if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                        System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                }
                catch { }
            };
            stack.Controls.Add(BuildRow(GetText("Settings.LogFolder", "日志目录"), true, _openLogFolderBtn));

            // 日志单文件行数上限（单位：**万行**；保存后生效，范围 1~100 万行；写满即分卷为下一份文件，不裁剪）
            // 注："报告保留天数"已按用户决定移除——各包日志不再按天数删除，统一按本行数上限分卷。
            // 值在点「保存」时由 ApplyAndClose 统一读取并落盘（不点保存不生效、不写配置文件）
            _logMaxLinesInput = new AntdUI.InputNumber
            {
                Minimum = 1,
                Maximum = 100,
                DecimalPlaces = 0,
                Increment = 1,
                Value = Math.Max(1, SettingsManager.LogMaxLines / 10000),   // 显示单位 = 万行
                Width = 70,
                Height = 30
            };
            var logUnitLabel = new AntdUI.Label
            {
                Text = GetText("Settings.LogMaxLinesUnit", "万行"),
                AutoSize = true,
                Font = FontHelper.UiFont(9),
                ForeColor = ThemeManager.Text
            };
            stack.Controls.Add(BuildRow(GetText("Settings.LogMaxLines", "日志最大行数"), true, _logMaxLinesInput, logUnitLabel));

            // 日志显示详情 [开关] 详情也记录到日志文件 [开关]（**同一行**，两者均"保存后生效"）
            // 全部设置项统一"点保存才生效"：此处只改控件状态，由 ApplyAndClose 统一写入并落盘（取消则什么都不变）
            _detailSwitch = new AntdUI.Switch { Checked = SettingsManager.ShowDetailLog, Size = new Size(40, 22) };
            // 文字用独立 Label（本项目惯例：AntdUI.Switch 不承载文本，给它是固定尺寸会把文字挤掉）
            var detailToFileLabel = new AntdUI.Label
            {
                Text = GetText("Settings.LogDetailToFile", "详情也记录到日志文件"),
                AutoSize = true,
                Font = FontHelper.UiFont(9),
                ForeColor = ThemeManager.Text
            };
            _logDetailToFileSwitch = new AntdUI.Switch
            {
                // 详情极易污染日志，故默认关；且需先开「日志显示详情」才能点（默认禁用）
                Checked = SettingsManager.ShowDetailLog && SettingsManager.LogDetailToFile,
                Enabled = SettingsManager.ShowDetailLog,
                Size = new Size(40, 22)
            };
            // 仅 UI 联动（不改设置值）：关掉「日志显示详情」时禁用本开关（连同文字一起变灰更直观）
            _detailSwitch.CheckedChanged += (s, e) =>
            {
                _logDetailToFileSwitch.Enabled = _detailSwitch.Checked;
                detailToFileLabel.ForeColor = _detailSwitch.Checked ? ThemeManager.Text : ThemeManager.TextTertiary;
            };
            detailToFileLabel.ForeColor = _detailSwitch.Checked ? ThemeManager.Text : ThemeManager.TextTertiary;
            stack.Controls.Add(BuildRow(GetText("Settings.ShowDetailLog", "日志显示详情"), true,
                _detailSwitch, detailToFileLabel, _logDetailToFileSwitch));

            // 「单独保存操作日志」（保存后生效）—— 控制 GDPackEditor-<包名>-<日期>.log 是否写入
            _packageLogSwitch = new AntdUI.Switch { Checked = SettingsManager.PackageLogEnabled, Size = new Size(40, 22) };
            stack.Controls.Add(BuildRow(GetText("Settings.PackageLogEnabled", "单独保存操作日志"), true, _packageLogSwitch));

            // 「保存日志」（保存后生效）—— 控制主日志 GDPackEditor.log 是否写入（界面日志显示不受影响）
            // —— 最后添加，显示在最上（用户要求：两开关置于日志页签顶部）
            _fileLogSwitch = new AntdUI.Switch { Checked = SettingsManager.LogFileEnabled, Size = new Size(40, 22) };
            stack.Controls.Add(BuildRow(GetText("Settings.LogFileEnabled", "保存日志"), true, _fileLogSwitch));

            page.Controls.Add(stack);
            return page;
        }

        /// <summary>
        /// 页签：检测（内容检测逐项开关，plan_20260923 §批B）。
        /// 前 6 项默认勾选（内容字符类 + 模板类），4 项候选项默认关。
        /// 「记录解压失败 / 记录无字段」属结构性异常，不可关闭，不在此列（始终报告）。
        /// 模板类两项在"模板未启用"时自动跳过（不报错、不计数）。
        /// </summary>
        private AntdUI.TabPage BuildDetectionTab()
        {
            var page = new AntdUI.TabPage
            {
                Text = GetText("Settings.DetectionTab", "检测"),
                Name = "Settings.DetectionTab"
            };
            var stack = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Back = Color.Transparent,
                Padding = new Padding(25, 10, 25, 10)
            };

            // 逐项开关的统一构建（键 = SettingsManager.Check* 属性名，ApplyAndClose 据此写回）
            void AddSwitchRow(string key, string labelKey, string fallback, bool withDivider)
            {
                var sw = new AntdUI.Switch { Checked = GetDetectionDefault(key), Size = new Size(40, 22) };
                _detectionSwitches[key] = sw;
                stack.Controls.Add(BuildRow(GetText(labelKey, fallback), withDivider, sw));
            }
            bool GetDetectionDefault(string key) => key switch
            {
                "CheckNewline" => SettingsManager.CheckNewline,
                "CheckComma" => SettingsManager.CheckComma,
                "CheckControlChar" => SettingsManager.CheckControlChar,
                "CheckTrimWhitespace" => SettingsManager.CheckTrimWhitespace,
                "CheckKeyTrimWhitespace" => SettingsManager.CheckKeyTrimWhitespace,
                "CheckTplFieldMissing" => SettingsManager.CheckTplFieldMissing,
                "CheckTplValueFormat" => SettingsManager.CheckTplValueFormat,
                "CheckSemicolon" => SettingsManager.CheckSemicolon,
                "CheckLongValue" => SettingsManager.CheckLongValue,
                "CheckDuplicateField" => SettingsManager.CheckDuplicateField,
                "CheckNoTemplateName" => SettingsManager.CheckNoTemplateName,
                _ => false
            };

            // 标签列宽按本页最宽标签对齐（与其它页签同一惯例）
            InitLabelColumnWidth(
                GetText("Settings.CheckNewline", "值含换行"),
                GetText("Settings.CheckComma", "值含逗号"),
                GetText("Settings.CheckControlChar", "值含控制字符"),
                GetText("Settings.CheckTrimWhitespace", "值首尾空白"),
                GetText("Settings.CheckKeyTrimWhitespace", "字段首尾空白"),
                GetText("Settings.CheckTplFieldMissing", "字段不在模板中（需模板）"),
                GetText("Settings.CheckTplValueFormat", "字段值格式错误（以模板为准）"),
                GetText("Settings.CheckSemicolon", "非数组字段含分号"),
                GetText("Settings.CheckLongValue", "值过长（超 20 万字符）"),
                GetText("Settings.CheckDuplicateField", "同名字段重复出现"),
                GetText("Settings.CheckNoTemplateName", "记录缺 templateName 行"));

            // 行反向插入（§6.6）：视觉自上而下 = 6 项默认开 → 4 项候选项
            AddSwitchRow("CheckNoTemplateName", "Settings.CheckNoTemplateName", "记录缺 templateName 行", false);
            AddSwitchRow("CheckDuplicateField", "Settings.CheckDuplicateField", "同名字段重复出现", true);
            AddSwitchRow("CheckLongValue", "Settings.CheckLongValue", "值过长（超 20 万字符）", true);
            AddSwitchRow("CheckSemicolon", "Settings.CheckSemicolon", "非数组字段含分号", true);
            AddSwitchRow("CheckTplValueFormat", "Settings.CheckTplValueFormat", "字段值格式错误（以模板为准）", true);
            AddSwitchRow("CheckTplFieldMissing", "Settings.CheckTplFieldMissing", "字段不在模板中（需模板）", true);
            AddSwitchRow("CheckTrimWhitespace", "Settings.CheckTrimWhitespace", "值首尾空白", true);
            AddSwitchRow("CheckKeyTrimWhitespace", "Settings.CheckKeyTrimWhitespace", "字段首尾空白", true);
            AddSwitchRow("CheckControlChar", "Settings.CheckControlChar", "值含控制字符", true);
            AddSwitchRow("CheckComma", "Settings.CheckComma", "值含逗号", true);
            AddSwitchRow("CheckNewline", "Settings.CheckNewline", "值含换行", true);

            page.Controls.Add(stack);
            return page;
        }

        /// <summary>
        /// 清除日志：删除日志目录下全部 .log（主日志 GDPackEditor.log + 各包变更报告），二次确认后执行。
        /// 按用户决定：只删文件，不清空界面上的日志框，也不动 settings.jsonc 等配置。
        /// </summary>
        private void ClearLogFiles()
        {
            var result = MessageBox.Show(
                GetText("Settings.ClearLogConfirm", "确定清除所有日志文件？\n包括主日志与各包变更报告，此操作不可恢复。"),
                GetText("Settings.ClearLogTitle", "清除日志"),
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (result != DialogResult.OK) return;

            try
            {
                int deleted = LogService.ClearLogFiles();
                MessageBox.Show(
                    string.Format(GetText("Settings.ClearLogDone", "已清除 {0} 个日志文件。"), deleted),
                    GetText("Settings.ClearLogTitle", "清除日志"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(GetText("Settings.ClearLogFailed", "清除日志失败：{0}"), ex.Message),
                    GetText("Settings.ClearLogTitle", "清除日志"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 页签 3：模板设置（原「模板管理」弹窗内容搬入）
        /// </summary>
        private AntdUI.TabPage BuildTemplateTab()
        {
            var page = new AntdUI.TabPage
            {
                Text = GetText("Settings.TemplateTab", "模板设置"),
                Name = "Settings.TemplateTab"
            };
            _templateManager = new TemplateManagerForm(this);
            _applyActions.Add(_templateManager.ApplyChanges);
            page.Controls.Add(_templateManager.Content);
            return page;
        }

        /// <summary>
        /// 页签 4：本地化dbr名（原编辑器/合并内嵌「语言包管理」内容搬入）
        /// </summary>
        private AntdUI.TabPage BuildLocalizeFileTab()
        {
            var page = new AntdUI.TabPage
            {
                Text = GetText("Settings.LocalizeFileTab", "本地化dbr名"),
                Name = "Settings.LocalizeFileTab"
            };
            var panel = new LocalizePackSettingsPanel(this);
            _applyActions.Add(panel.Apply);
            page.Controls.Add(panel);
            return page;
        }

        /// <summary>
        /// 页签 5：本地化dbr字段（原「DBR 字段名本地化」弹窗内容搬入）
        /// </summary>
        private AntdUI.TabPage BuildLocalizeFieldTab()
        {
            var page = new AntdUI.TabPage
            {
                Text = GetText("Settings.LocalizeFieldTab", "本地化dbr字段"),
                Name = "Settings.LocalizeFieldTab"
            };
            var panel = new DbrKeyMappingPanel(this);
            _applyActions.Add(panel.Apply);
            page.Controls.Add(panel);
            return page;
        }

        /// <summary>
        /// 页签 6：本地化文件夹（原「文件夹名本地化」弹窗内容搬入）
        /// </summary>
        private AntdUI.TabPage BuildLocalizeFolderTab()
        {
            var page = new AntdUI.TabPage
            {
                Text = GetText("Settings.LocalizeFolderTab", "本地化文件夹"),
                Name = "Settings.LocalizeFolderTab"
            };
            var panel = new FolderMappingPanel(this);
            _applyActions.Add(panel.Apply);
            page.Controls.Add(panel);
            return page;
        }

        /// <summary>
        /// 保存后按白名单刷新已打开的编辑器 / 合并窗口中的本地化展示（各自带忙状态守卫）。
        /// 先按索引取快照再遍历：Application.OpenForms 是活集合，直接 foreach 枚举期间
        /// 若有窗体被创建/显示/销毁会抛 InvalidOperationException（集合已修改）。
        /// 白名单两档粒度：<paramref name="refreshLanguagePacks"/>（语言包变更，两窗都消费）时合并窗也刷；
        /// dbr字段/文件夹映射变更仅编辑器消费，只刷编辑器。
        /// </summary>
        public static void RefreshOpenWindows(bool refreshLanguagePacks, bool refreshEditorMappings)
        {
            var snapshot = new List<Form>();
            for (int i = 0; i < Application.OpenForms.Count; i++)
                snapshot.Add(Application.OpenForms[i]);
            foreach (Form f in snapshot)
            {
                if (f == null || f.IsDisposed) continue;
                if (f is ArzArcEditorForm editor)
                {
                    if (refreshLanguagePacks || refreshEditorMappings)
                        editor.RefreshLocalization();
                }
                else if (f is ModMergeToolForm merge)
                {
                    if (refreshLanguagePacks)
                        merge.RefreshLocalization();
                }
            }
        }

        /// <summary>本地化三组设置的"值签名"快照（白名单变更检测用）。
        /// 面板 Apply 无论改没改都会整体替换 List 引用，故必须比内容值而非引用。</summary>
        private sealed class LocalizationSnapshot
        {
            public string Packs;        // ImportedPacks + ActivePackKeys（编辑器+合并窗消费）
            public string DbrPacks;     // DbrKeyMappingPacks（仅编辑器消费）
            public string FolderPacks;  // FolderMappingPacks（仅编辑器消费）
        }

        private static string Sig(object v) => v?.ToString() ?? "";

        private static LocalizationSnapshot CaptureLocalizationSnapshot()
        {
            var snap = new LocalizationSnapshot();

            var lines = new List<string>();
            var packs = SettingsManager.ImportedPacks;
            if (packs != null)
                foreach (var p in packs)
                    lines.Add($"{Sig(p.Name)}|{Sig(p.Note)}|{Sig(p.FilePath)}|{p.IsChecked}");
            var keys = new List<string>();
            var activeKeys = SettingsManager.ActivePackKeys;
            if (activeKeys != null)
                foreach (var k in activeKeys)
                    keys.Add(k.ToString());
            snap.Packs = string.Join("\n", lines) + "\n#" + string.Join(",", keys);

            lines = new List<string>();
            var dbrPacks = SettingsManager.DbrKeyMappingPacks;
            if (dbrPacks != null)
                foreach (var p in dbrPacks)
                    lines.Add($"{Sig(p.Id)}|{Sig(p.Note)}|{Sig(p.FilePath)}|{Sig(p.Type)}|{p.IsChecked}");
            snap.DbrPacks = string.Join("\n", lines);

            lines = new List<string>();
            var folderPacks = SettingsManager.FolderMappingPacks;
            if (folderPacks != null)
                foreach (var p in folderPacks)
                    lines.Add($"{Sig(p.Id)}|{Sig(p.Note)}|{Sig(p.FilePath)}|{Sig(p.Type)}|{p.IsChecked}");
            snap.FolderPacks = string.Join("\n", lines);

            return snap;
        }

        /// <summary>
        /// 清除配置：删除 settings.jsonc 后重启（与原底部「清除配置」行为一致）。
        /// 重启不走 <see cref="Application.Restart"/>（模态对话框场景下关窗时序不可靠，
        /// 新进程会被单实例保护拦截）：置 <see cref="Program.PendingRestart"/> 标志 →
        /// 关设置窗 → 主窗体随之关闭 → Main 在 Run 返回后启动新进程。
        /// </summary>
        private void ClearSettings()
        {
            var result = MessageBox.Show(
                GetText("MainForm.ClearSettingsConfirm", "确定清除所有配置？工具将重启。"),
                GetText("MainForm.ClearSettingsTitle", "清除配置"),
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (result != DialogResult.OK) return;
            // 先抑制一切配置回写再删文件：否则主窗体 OnFormClosing（窗口状态/列宽记忆）会把
            // 内存配置原样写回磁盘，历史记录等全部"复活"，等于清除无效
            SettingsManager.SuppressSave = true;
            try
            {
                if (File.Exists(SettingsManager.SettingsFile))
                    File.Delete(SettingsManager.SettingsFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(GetText("Settings.ClearConfigFailed", "清除配置失败：{0}"), ex.Message),
                    GetText("MainForm.ClearSettingsTitle", "清除配置"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Program.PendingRestart = true;
            Close();
        }

        /// <summary>
        /// 「导入语言文件 / 导出内置语言文件」两行是否显示：只有选中「使用自定义语言」才显示。
        /// 绝大多数用户只用内置语言，这两行常驻纯属干扰；且与"自定义包不可用时引导去设置里改"不冲突 ——
        /// 那种情况下配置里仍是自定义，单选本来就选中，两行自然可见。
        /// </summary>
        private void UpdateLanguageGroupVisibility()
        {
            if (_langDetailPanel == null) return;

            bool show = _radioLangOther != null && _radioLangOther.Checked;
            if (_langDetailPanel.Visible != show)
            {
                _langDetailPanel.Visible = show;
                // Dock=Top 的隐藏控件不参与布局，隐藏后下方各行会自动上移；显式触发一次布局更稳
                _langDetailPanel.Parent?.PerformLayout();
            }
        }

        /// <summary>
        /// 应用语言选择（点「保存」时调用；取消则完全不生效）。
        /// ① 选「使用自定义语言」→ 路径为空则提示并回落；与当前一致则跳过；否则 TryLoadCustom（失败弹窗 + 回落中文）；
        /// ② 选内置语言 → 与持久化配置一致则跳过，否则 LoadBuiltIn。
        /// 落盘与 <c>LanguageChanged</c> 都由 <see cref="LanguageManager"/> 内部完成，这里只管"该不该切、失败了怎么提示"。
        /// </summary>
        private void ApplyLanguageChoice()
        {
            if (_radioLangOther.Checked)
            {
                if (string.IsNullOrEmpty(_customLangPath))
                {
                    MessageBox.Show(this,
                        GetText("Settings.CustomLanguageNotSelected", "请先导入自定义语言文件"),
                        GetText("Common.MessageBoxTitle.Warning", "警告"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _radioLangZh.Checked = true;
                    return;
                }

                var cur = SettingsManager.Language;
                if (cur != null && cur.IsCustom &&
                    string.Equals(cur.Path, _customLangPath, StringComparison.OrdinalIgnoreCase))
                    return;   // 用的就是这个自定义语言包，无需重载

                if (LanguageManager.Instance.TryLoadCustom(_customLangPath, out string error)) return;

                MessageBox.Show(this,
                    string.Format(GetText("Settings.CustomLanguageLoadFail", "自定义语言包加载失败：{0}"), error),
                    GetText("Common.MessageBoxTitle.Error", "错误"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _radioLangZh.Checked = true;                       // 失败回落内置中文
                LanguageManager.Instance.LoadBuiltIn(LanguageManager.BuiltInZh);
                return;
            }

            string code = _radioLangEn.Checked ? LanguageManager.BuiltInEn : LanguageManager.BuiltInZh;
            var builtin = SettingsManager.Language;
            if (builtin != null && builtin.Builtin &&
                string.Equals(LanguageManager.NormalizeBuiltInCode(builtin.Name), code, StringComparison.OrdinalIgnoreCase))
                return;   // 已经是这个内置语言

            LanguageManager.Instance.LoadBuiltIn(code);
        }

        /// <summary>
        /// 保存关闭：设置窗内**唯一的应用入口** —— 把所有 UI 状态一次性写入 SettingsManager / LogService 并落盘。
        /// 生效时机：语言立即；明暗/色系/自动缩放需重启；日志相关（显示详情 / 最大行数 / 两个写入开关）保存即生效。
        /// 取消（btnCancel 自带 DialogResult.Cancel）不经过本方法，因此不产生任何变化。
        /// </summary>
        private void ApplyAndClose()
        {
            // 主设置：从单选按钮读取当前选择（明暗默认跟随系统；语言默认简体中文）
            string themeMode = _radioThemeLight.Checked ? "Light"
                : _radioThemeDark.Checked ? "Dark"
                : "System";
            string accent = null;
            foreach (var kv in _accentRadios)
            {
                if (kv.Value.Checked)
                {
                    accent = kv.Key;
                    break;
                }
            }
            if (string.IsNullOrEmpty(accent)) accent = SettingsManager.ThemeAccent;

            bool themeChanged = !string.Equals(SettingsManager.ThemeMode, themeMode, StringComparison.OrdinalIgnoreCase);
            bool accentChanged = !string.Equals(SettingsManager.ThemeAccent, accent, StringComparison.OrdinalIgnoreCase);
            bool scaleChanged = SettingsManager.UiScaleEnabled != _scaleSwitch.Checked;

            // ── 语言：即时生效（三种选择各自的落盘与切换都收在 LanguageManager 里）──
            ApplyLanguageChoice();

            SettingsManager.ThemeMode = themeMode ?? SettingsManager.ThemeMode;
            SettingsManager.ThemeAccent = accent ?? SettingsManager.ThemeAccent;
            SettingsManager.UiScaleEnabled = _scaleSwitch.Checked;
            SettingsManager.ShowDetailLog = _detailSwitch.Checked;
            // ── 设置 → 记住表格列宽：4 表分开关（重置按钮点击即时执行，不在此处理）──
            SettingsManager.TwEnabledFileList = _twFileListCheckbox.Checked;
            SettingsManager.TwEnabledDbrGrid = _twDbrGridCheckbox.Checked;
            SettingsManager.TwEnabledMergeTable = _twMergeTableCheckbox.Checked;
            SettingsManager.TwEnabledSearchResults = _twSearchResultsCheckbox.Checked;
            // ── 设置 → 检测：逐项开关（保存后生效，plan_20260923 §批B）──
            foreach (var kv in _detectionSwitches)
            {
                bool val = kv.Value.Checked;
                switch (kv.Key)
                {
                    case "CheckNewline": SettingsManager.CheckNewline = val; break;
                    case "CheckComma": SettingsManager.CheckComma = val; break;
                    case "CheckControlChar": SettingsManager.CheckControlChar = val; break;
                    case "CheckTrimWhitespace": SettingsManager.CheckTrimWhitespace = val; break;
                    case "CheckKeyTrimWhitespace": SettingsManager.CheckKeyTrimWhitespace = val; break;
                    case "CheckTplFieldMissing": SettingsManager.CheckTplFieldMissing = val; break;
                    case "CheckTplValueFormat": SettingsManager.CheckTplValueFormat = val; break;
                    case "CheckSemicolon": SettingsManager.CheckSemicolon = val; break;
                    case "CheckLongValue": SettingsManager.CheckLongValue = val; break;
                    case "CheckDuplicateField": SettingsManager.CheckDuplicateField = val; break;
                    case "CheckNoTemplateName": SettingsManager.CheckNoTemplateName = val; break;
                }
            }
            // ── 设置 → 编码：文件名 1 项 + 内容 2 项（三项都同时管 ARC 与 ARZ）──
            // R10：编码设置真的变了，且还有正在编辑/合并的临时文件 → 提醒（只提示不阻止，用户已定案）
            string newNameMode = _radioNameFilterAscii.Checked
                ? SettingsManager.NameModeFilterAscii
                : SettingsManager.NameModeAcp;
            string newPackMode = _radioPackAnsi.Checked
                ? SettingsManager.ContentModeAnsi
                : (_radioPackUtf8.Checked ? SettingsManager.ContentModeUtf8NoBom : SettingsManager.ContentModeOriginal);
            string newUnpackMode = _radioUnpackAnsi.Checked
                ? SettingsManager.ContentModeAnsi
                : (_radioUnpackUtf8.Checked ? SettingsManager.ContentModeUtf8NoBom : SettingsManager.ContentModeOriginal);

            bool encodingChanged = newNameMode != SettingsManager.NameMode
                                || newPackMode != SettingsManager.ContentPackMode
                                || newUnpackMode != SettingsManager.ContentUnpackMode;
            if (encodingChanged && _encodingHadTempFiles)
            {
                int live = TempSessionManager.CountLiveEditorSessions();
                bool hasMerge = TempSessionManager.HasMergeTempFiles();
                var result = MessageBox.Show(this,
                    string.Format(GetText("Settings.EncodingChangeWithTemp",
                        "检测到仍有正在编辑/合并产生的临时文件（编辑器会话 {0} 处{1}）。\n修改编码设置后，这些会话里已生成的临时文件仍按旧设置编码，保存进包时会出现新旧混用。\n建议先关闭编辑器 / MOD 合并窗口再改。\n\n是否仍要保存？"),
                        live, hasMerge ? "，以及 MOD 合并临时文件" : ""),
                    GetText("Settings.EncodingChangeTitle", "编码设置变更"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result != DialogResult.Yes)
                    return;
            }

            SettingsManager.NameMode = newNameMode;
            SettingsManager.ContentPackMode = newPackMode;
            SettingsManager.ContentUnpackMode = newUnpackMode;

            // 日志页签的数值与开关：统一在点「保存」时应用（对话框内改动仅暂存于界面，
            // 取消则完全不生效、不落盘）。夹紧范围与 LogService.MaxLines 的取值规则一致。
            SettingsManager.LogMaxLines = Math.Max(10000, Math.Min(1000000, (int)_logMaxLinesInput.Value * 10000));
            // 详情记录到文件：必须「日志显示详情」也开启才成立（界面已联动禁用，这里再兜一层）
            SettingsManager.LogDetailToFile = _detailSwitch.Checked && _logDetailToFileSwitch.Checked;
            SettingsManager.LogFileEnabled = _fileLogSwitch.Checked;
            SettingsManager.PackageLogEnabled = _packageLogSwitch.Checked;
            // 运行期门控同步：LogService 两个静态开关决定文件通道是否写入，必须在此一并应用
            LogService.FileEnabled = SettingsManager.LogFileEnabled;
            LogService.PackageLogEnabled = SettingsManager.PackageLogEnabled;

            SettingsManager.Save();

            // 各页签内容（模板/本地化）落盘；Apply 前后对比本地化三组设置的值快照，
            // 只有真的变更才刷新子窗口（白名单：三个本地化页签；模板/语言/日志/编码/检测等不在名单内）
            var locBefore = CaptureLocalizationSnapshot();
            foreach (var action in _applyActions)
                action();
            var locAfter = CaptureLocalizationSnapshot();
            bool langChanged = !string.Equals(locBefore.Packs, locAfter.Packs, StringComparison.Ordinal);
            bool mappingChanged = !string.Equals(locBefore.DbrPacks, locAfter.DbrPacks, StringComparison.Ordinal)
                || !string.Equals(locBefore.FolderPacks, locAfter.FolderPacks, StringComparison.Ordinal);
            if (langChanged || mappingChanged)
                RefreshOpenWindows(langChanged, mappingChanged);

            if (themeChanged || accentChanged || scaleChanged)
            {
                bool scaleOnly = scaleChanged && !themeChanged && !accentChanged;
                var restartResult = MessageBox.Show(
                    scaleOnly
                        ? GetText("MainForm.UiScaleRestartHint", "界面缩放设置将在重启后生效，是否立即重启？")
                        : GetText("MainForm.ThemeRestartHint", "主题设置将在重启后生效，是否立即重启？"),
                    scaleOnly
                        ? GetText("MainForm.UiScaleTitle", "界面缩放")
                        : GetText("MainForm.ThemeTitle", "主题设置"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (restartResult == DialogResult.Yes)
                {
                    DialogResult = DialogResult.OK;
                    // 重启统一走 PendingRestart：Main 在 Run 返回后启动新进程（模态场景下
                    // Application.Restart 不可靠，见 ClearSettings 注释）
                    Program.PendingRestart = true;
                    return;
                }
            }

            DialogResult = DialogResult.OK;
        }
    }
}