using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace GDPackEditor
{
    /// <summary>
    /// DBR 字段名本地化字典：内置资源 + 外部条目按顺序合并，提供本地化名查询。
    /// </summary>
    public static class DbrKeyMapping
    {
        private static Dictionary<string, string> _merged;
        private static readonly object _lock = new object();

        /// <summary>
        /// 获取合并后的字典（内置资源按资源名排序 → 外部启用条目按列表顺序，后者覆盖前者）。
        /// </summary>
        public static Dictionary<string, string> GetMerged()
        {
            lock (_lock)
            {
                if (_merged != null) return _merged;

                _merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var packs = SettingsManager.DbrKeyMappingPacks;
                var asm = Assembly.GetExecutingAssembly();
                var allResourceNames = asm.GetManifestResourceNames()
                    .Where(n => IsBuiltInDictResource(n))
                    .ToList();

                // 按 packs 中勾选的内置条目逐个加载对应内嵌资源
                foreach (var entry in packs.Where(e => e != null && e.Type == "BuiltIn" && e.IsChecked))
                {
                    var fullName = allResourceNames.FirstOrDefault(n =>
                        n.EndsWith(entry.FilePath, StringComparison.OrdinalIgnoreCase));
                    if (fullName == null) continue;
                    try
                    {
                        using var stream = asm.GetManifestResourceStream(fullName);
                        if (stream == null) continue;
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        ParseText(reader.ReadToEnd(), _merged);
                    }
                    catch (Exception ex)
                    {
                        try { LogService.Warn($"[dict] built-in DBR mapping failed: {fullName} ({ex.Message})"); } catch { }
                    }
                }

                // 外部启用条目，按列表顺序加载（后者覆盖前者）
                foreach (var entry in packs.Where(e => e != null && e.Type != "BuiltIn" && e.IsChecked))
                {
                    if (string.IsNullOrWhiteSpace(entry.FilePath) || !File.Exists(entry.FilePath)) continue;
                    try
                    {
                        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        ParseText(File.ReadAllText(entry.FilePath, Encoding.UTF8), dict);
                        foreach (var kv in dict)
                            _merged[kv.Key] = kv.Value;
                    }
                    catch (Exception ex)
                    {
                        try { LogService.Warn($"[dict] external DBR mapping failed: {entry.FilePath} ({ex.Message})"); } catch { }
                    }
                }
                return _merged;
            }
        }

        private static bool IsBuiltInDictResource(string resourceName) =>
            resourceName.IndexOf("DbrKeyMapping_", StringComparison.OrdinalIgnoreCase) >= 0
            && resourceName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 内置字典资源文件名列表（如 DbrKeyMapping_cn.txt），按资源名排序，用于管理弹窗显示。
        /// </summary>
        public static List<string> GetBuiltInFileNames()
        {
            var asm = Assembly.GetExecutingAssembly();
            return asm.GetManifestResourceNames()
                .Where(n => IsBuiltInDictResource(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => n.Substring(n.LastIndexOf("DbrKeyMapping_", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        /// <summary>
        /// 读取指定内嵌字典资源的完整文本（用于"另存为"），未找到返回 null。
        /// </summary>
        public static string GetBuiltInContent(string builtInFileName)
        {
            if (string.IsNullOrWhiteSpace(builtInFileName)) return null;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var fullName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(builtInFileName, StringComparison.OrdinalIgnoreCase));
                if (fullName == null) return null;
                using var stream = asm.GetManifestResourceStream(fullName);
                if (stream == null) return null;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch { return null; }
        }

        /// <summary>
        /// 使合并字典缓存失效（管理弹窗保存后调用）。
        /// </summary>
        public static void Rebuild()
        {
            lock (_lock) _merged = null;
        }

        /// <summary>
        /// 获取字段名的本地化显示：有映射返回本地化名，否则返回原英文。
        /// </summary>
        public static string GetLocalizedName(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            return GetMerged().TryGetValue(key, out var loc) && !string.IsNullOrWhiteSpace(loc) ? loc : key;
        }

        /// <summary>
        /// 是否命中映射（用于过滤等逻辑）。
        /// </summary>
        public static bool HasMapping(string key) =>
            !string.IsNullOrEmpty(key) && GetMerged().ContainsKey(key);

        /// <summary>
        /// 解析字典文本：仅读取 KEY=VALUE 格式行，其余行（空行、#注释、非键值行）视为备注跳过。
        /// </summary>
        public static void ParseText(string text, Dictionary<string, string> dict)
        {
            if (string.IsNullOrEmpty(text)) return;
            using var reader = new StringReader(text);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim();
                if (key.Length == 0) continue;
                dict[key] = line[(eq + 1)..].Trim();
            }
        }
    }
}
