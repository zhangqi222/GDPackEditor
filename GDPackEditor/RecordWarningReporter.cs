using System;

namespace GDPackEditor
{
    /// <summary>
    /// 记录级问题上报**公共方法**（打包 ARZ / ARZ 编辑器保存 / MOD 合并写记录三处共用，见
    /// plan_20291013_日志折叠 3.3B"公共化一个上报方法"）。
    ///
    /// <para>上报内容来自 <see cref="ARZRecord"/> 构造阶段填充的三份清单：</para>
    /// <para>① <see cref="ARZRecord.TemplateSkipped"/>：模板里找不到、不会写进包的字段（字段级警告）；</para>
    /// <para>② <see cref="ARZRecord.DataFixes"/>：已自动修复的行（补尾逗号/清空白等）；</para>
    /// <para>③ <see cref="ARZRecord.DataWarnings"/>：可疑行（值含逗号等，不影响出包）。</para>
    ///
    /// <para>所有警告都携带 <c>groupKey = 记录名</c>：日志区（主界面 LogView / 编辑器日志面板）
    /// 据此把"同一条记录的连续报错"折叠成组（见 <see cref="LogFoldSupport"/>）。</para>
    /// </summary>
    internal static class RecordWarningReporter
    {
        /// <summary>
        /// 上报一条记录的字段级问题（逐条警告，带折叠分组键）。
        /// </summary>
        /// <param name="progress">进度回调（可空：调用方以 <c>?.</c> 守卫后传入即可）</param>
        /// <param name="recordName">记录名（包内相对路径），同时作为折叠分组键</param>
        /// <param name="rec">已构造的记录对象（可空：复制构造等路径无此信息时直接跳过）</param>
        /// <returns>(已修复数, 可疑数)：供调用方做操作末尾的汇总统计</returns>
        public static (int fixes, int warnings) ReportRecordIssues(
            IProgressCallback progress, string recordName, ARZRecord rec)
        {
            int fixes = 0, warnings = 0;
            if (rec == null) return (fixes, warnings);

            // 字段级日志：模板里没有的字段不会写进包（打包/保存时唯一会被静默过滤的环节）
            if (rec.TemplateSkipped != null)
                foreach (string s in rec.TemplateSkipped)
                    progress?.ReportWarning(string.Format(
                        LanguageManager.Instance.GetText("LOG.TemplateFieldSkipped",
                            "字段不在模板中，已跳过：{0} → {1}"), recordName, s), recordName);

            // 行格式：已修复项与可疑项（逐条警告上报；末尾只报数量，避免双份显示）
            if (rec.DataFixes != null)
                foreach (string s in rec.DataFixes)
                {
                    fixes++;
                    progress?.ReportWarning(recordName + "：" + s, recordName);
                }
            if (rec.DataWarnings != null)
                foreach (string s in rec.DataWarnings)
                {
                    warnings++;
                    progress?.ReportWarning(recordName + "：" + s, recordName);
                }

            return (fixes, warnings);
        }

        /// <summary>
        /// 模板未找到的记录级跳过警告（打包/保存共用文案与分组键）。
        /// 该记录**整体不入包**（打包侧跳过整个文件；保存侧该记录保持原包原样），由调用方计数并继续处理后续记录。
        /// </summary>
        public static void ReportTemplateMissing(IProgressCallback progress, string recordName)
            => progress?.ReportWarning(string.Format(
                LanguageManager.Instance.GetText("LOG.TemplateMissingSkip",
                    "模板未找到，该记录已跳过（不入包）：{0}"), recordName), recordName);
    }
}
