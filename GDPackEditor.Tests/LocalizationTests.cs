using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace GDPackEditor.Tests
{
    public class LocalizationTests
    {
        [Fact]
        public void EnglishAndChineseLanguageFilesHaveTheSameKeys()
        {
            string root = FindRepositoryRoot();
            var en = ReadLanguage(Path.Combine(root, "GDPackEditor", "languages", "EN.json"));
            var zh = ReadLanguage(Path.Combine(root, "GDPackEditor", "languages", "ZH.json"));

            var missingInEn = zh.Keys.Except(en.Keys, StringComparer.Ordinal).OrderBy(k => k).ToArray();
            var missingInZh = en.Keys.Except(zh.Keys, StringComparer.Ordinal).OrderBy(k => k).ToArray();

            Assert.True(missingInEn.Length == 0, "Keys missing in EN.json: " + string.Join(", ", missingInEn));
            Assert.True(missingInZh.Length == 0, "Keys missing in ZH.json: " + string.Join(", ", missingInZh));
        }

        private static Dictionary<string, string> ReadLanguage(string path)
        {
            Assert.True(File.Exists(path), "Language file not found: " + path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        }

        private static string FindRepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GDPackEditor.sln")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
