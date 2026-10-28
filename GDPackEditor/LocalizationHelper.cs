using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GDPackEditor
{
    internal static class LocalizationHelper
    {
        public static readonly string[] KnownTagFields = {
            "itemNameTag", "skillDisplayName",
            "lootRandomizerName", "journalTag", "description"
        };

        public static Dictionary<string, string> LoadLanguagePackFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var arcFile = new ARCFile();
                arcFile.ReadStream(fs);
                foreach (var entry in arcFile.toc)
                {
                    string entryName = entry.GetEntryString(arcFile.strs);
                    if (string.IsNullOrEmpty(entryName) ||
                        !entryName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        continue;
                    using var ms = new MemoryStream();
                    arcFile.UnpackToStream(entry, ms);
                    ms.Position = 0;
                    string text = TemplateHelper.DecodeText(ms.ToArray());
                    string[] lines = text.Replace("\r\n", "\n").Split('\n');
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed)) continue;
                        int eq = trimmed.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = trimmed[..eq].Trim();
                        string val = trimmed[(eq + 1)..].Trim();
                        if (!string.IsNullOrEmpty(key))
                            dict[key] = val;
                    }
                }
            }
            catch (Exception ex)
            {
                // 语言包是可选增强：读取失败不阻断主流程，但必须留下可排查的日志
                try { LogService.Warn($"[localization] failed to read language pack: {filePath} ({ex.Message})"); } catch { }
                return null;
            }
            return dict;
        }

        public static Dictionary<string, string> MergeLanguagePacks(List<int> activeKeys)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (int idx in activeKeys)
            {
                if (idx < 0 || idx >= SettingsManager.ImportedPacks.Count) continue;
                var pack = SettingsManager.ImportedPacks[idx];
                if (!File.Exists(pack.FilePath)) continue;
                var dict = LoadLanguagePackFile(pack.FilePath);
                if (dict == null) continue;
                foreach (var kvp in dict)
                    merged[kvp.Key] = kvp.Value;
            }
            return merged.Count > 0 ? merged : null;
        }

        public static string GetFolderAnnotationSuffix(string folderName)
        {
            var loc = FolderMapping.GetLocalizedName(folderName);
            if (!string.IsNullOrWhiteSpace(loc) && !string.Equals(loc, folderName, StringComparison.OrdinalIgnoreCase))
                return $" ({loc})";
            return "";
        }

        public static string LookupLocalizedTag(string tagValue, Dictionary<string, string> langDict)
        {
            if (langDict == null || string.IsNullOrEmpty(tagValue)) return tagValue ?? "";
            if (langDict.TryGetValue(tagValue, out var localized) && !string.IsNullOrEmpty(localized))
                return localized;
            return tagValue;
        }

        // 批23：原「逐词反查」FindTagsByLocalizedValue 已删除 —— v 级反查（条件组对每条译名整体求值）
        // 统一由 GuiMainForm.BuildLangReverseTagList 承担（与/或/全/排除同一套 MatchLine 语义）。

        public static string FindFirstTagValue(string dbrText)
        {
            if (string.IsNullOrEmpty(dbrText)) return null;
            foreach (var line in dbrText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || !trimmed.Contains(",")) continue;
                var parts = trimmed.Split(new[] { ',' }, 2);
                string k = parts[0].Trim();
                if (Array.IndexOf(KnownTagFields, k) >= 0)
                {
                    string v = parts[1].Trim().TrimEnd(',').Trim();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            return null;
        }
    }
}
