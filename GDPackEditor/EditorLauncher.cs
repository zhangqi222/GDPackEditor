using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace GDPackEditor
{
    /// <summary>
    /// 编辑器的**打开 / 激活 / 定位**公共入口。
    ///
    /// <para>统一三件事，避免各处自行 `new ArzArcEditorForm(...)` 造成同一个包被反复打开：</para>
    /// <list type="number">
    /// <item>**打开或激活**：同一个包已在编辑器中打开 → 激活原窗口（还原最小化 / 提到前台）；否则新建并加载。</item>
    /// <item>**加载**：新建时用**带路径构造**（与主界面"把包拖到编辑器页签"同一条路），
    /// 再调 <see cref="ArzArcEditorForm.EnsureArchiveLoaded"/> 兜底，保证包一定加载出内容。</item>
    /// <item>**定位**：可选定位到包内文件（<see cref="ArzArcEditorForm.LocateFileWhenReady"/> 会等加载完成后重试）。</item>
    /// </list>
    ///
    /// <para>路径比较统一按"规范化 + 忽略大小写"，避免 `\\?\`、斜杠方向、大小写差异导致重复开窗。</para>
    /// </summary>
    internal static class EditorLauncher
    {
        /// <summary>
        /// 打开（或激活）指定包的编辑器，并可选定位到包内文件与关键词。
        /// </summary>
        /// <param name="archivePath">包文件路径</param>
        /// <param name="relPath">包内相对路径（如 `records/items/a.dbr`）；null = 只开包不定位</param>
        /// <param name="keyword">内容搜索的关键词（记入编辑器，供"定位到关键词"使用；本步骤只记不用）</param>
        /// <param name="openFileContent">
        /// 定位方式：
        /// <para>true = 打开该文件内容（包内文件内容搜索的结果用这个；关键词定位在后续步骤接入）；</para>
        /// <para>false = **只在「文件列表」里选中并滚动到该行**，不打开文件内容（包内文件名搜索的结果用这个）。</para>
        /// </param>
        /// <returns>被打开/激活的编辑器实例；路径为空时返回 null</returns>
        public static ArzArcEditorForm Open(string archivePath, string relPath = null, string keyword = null,
            bool openFileContent = true)
        {
            if (string.IsNullOrEmpty(archivePath)) return null;
            string full = Normalize(archivePath);

            ArzArcEditorForm editor = Application.OpenForms.OfType<ArzArcEditorForm>()
                .FirstOrDefault(f => f != null && !f.IsDisposed && SamePath(f.CurrentArchivePath, full));

            if (editor != null)
            {
                // 已打开：激活原窗口，不重复新建
                if (editor.WindowState == FormWindowState.Minimized)
                    editor.WindowState = FormWindowState.Normal;
                editor.Activate();
                editor.BringToFront();
            }
            else
            {
                // 新建：**用带路径构造**（与主界面"把包拖到编辑器页签"完全同一条路）。
                // ⚠ 不能用 `new ArzArcEditorForm(null)` 再显式加载：空路径会在 Load 事件里弹出**历史记录**弹窗，
                //   搜索结果「打开」时不该出现它（实测反馈）。
                editor = new ArzArcEditorForm(full);
                editor.FormClosed += (s, e) => editor.Dispose();
                editor.Show();
                editor.EnsureArchiveLoaded();   // 兜底：该构造路径若未加载出内容，这里补一次
            }

            if (!string.IsNullOrEmpty(relPath))
            {
                if (openFileContent) editor.LocateFileWhenReady(relPath, keyword);
                else editor.LocateInFileListWhenReady(relPath);
            }

            return editor;
        }

        /// <summary>规范化包路径（绝对路径 + 去掉结尾分隔符），用于路径比较</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return path.TrimEnd('\\', '/'); }
        }

        /// <summary>是否为同一个包（规范化后忽略大小写）</summary>
        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
