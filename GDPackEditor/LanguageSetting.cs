using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GDPackEditor
{
    /// <summary>
    /// UI 语言设置（settings.jsonc 里的二级结构）：
    ///   内置中文：{ "name": "zh", "builtin": true }
    ///   内置英文：{ "name": "en", "builtin": true }
    ///   自定义  ：{ "name": "other", "builtin": false, "path": "G:\...\jp.json" }
    /// 约定：name 一律小写（zh / en / other）；读取时忽略大小写；builtin 为 true 时不写 path。
    /// </summary>
    public class LanguageSetting
    {
        /// <summary>语言标识：zh / en / other</summary>
        public string Name { get; set; } = LanguageManager.BuiltInZh;

        /// <summary>是否使用内置语言包（true 时 Path 无意义）</summary>
        public bool Builtin { get; set; } = true;

        /// <summary>自定义语言包的完整路径（仅 Builtin=false 时有效）</summary>
        public string Path { get; set; }

        /// <summary>是否指向一个可用的自定义语言包配置</summary>
        [JsonIgnore]
        public bool IsCustom => !Builtin && !string.IsNullOrWhiteSpace(Path);

        public LanguageSetting Clone()
            => new LanguageSetting { Name = Name, Builtin = Builtin, Path = Path };
    }

    /// <summary>
    /// <see cref="LanguageSetting"/> 的序列化器：**同时兼容两代格式**。
    /// ① 旧格式：扁平字符串（老版本 settings.jsonc 里的 <c>"Language": "zh-CN"</c>）
    /// ② 新格式：上面那个二级对象
    ///
    /// 为什么必须做这个兼容：`Language` 键属于"同键异类型"。若直接把属性类型从 string 换成对象，
    /// 老配置一读就抛 JsonException，而 `SettingsManager.Load()` 是**整体 catch** 的 ——
    /// 后果是**整个 settings.jsonc 都读不进来**（不只是语言选择丢失）。
    /// </summary>
    public sealed class LanguageSettingConverter : JsonConverter<LanguageSetting>
    {
        public override LanguageSetting Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;

                // 旧格式：字符串语言代码 → 迁移为内置设置（zh-CN/zh/cn → zh，en-US/en → en）
                case JsonTokenType.String:
                    return new LanguageSetting
                    {
                        Name = LanguageManager.NormalizeBuiltInCode(reader.GetString()) ?? LanguageManager.BuiltInZh,
                        Builtin = true
                    };

                case JsonTokenType.StartObject:
                    // 新格式：逐字段手工读。
                    // 注意：不能在这里再调 JsonSerializer.Deserialize<LanguageSetting>()，
                    // 那会再次进入本转换器 → 无限递归。
                    var setting = new LanguageSetting();
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndObject) break;
                        if (reader.TokenType != JsonTokenType.PropertyName) continue;

                        string prop = reader.GetString();
                        if (!reader.Read()) break;

                        if (string.Equals(prop, "name", StringComparison.OrdinalIgnoreCase))
                        {
                            setting.Name = reader.TokenType == JsonTokenType.String ? reader.GetString() : setting.Name;
                        }
                        else if (string.Equals(prop, "builtin", StringComparison.OrdinalIgnoreCase))
                        {
                            if (reader.TokenType == JsonTokenType.True) setting.Builtin = true;
                            else if (reader.TokenType == JsonTokenType.False) setting.Builtin = false;
                        }
                        else if (string.Equals(prop, "path", StringComparison.OrdinalIgnoreCase))
                        {
                            setting.Path = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                        }
                        else
                        {
                            // 未知字段（含对象/数组）整体跳过；不 Skip 会把内层属性名当成外层字段误读
                            reader.Skip();
                        }
                    }
                    return Normalize(setting);
            }

            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, LanguageSetting value, JsonSerializerOptions options)
        {
            var s = Normalize(value);
            writer.WriteStartObject();
            writer.WriteString("name", s.Name);
            writer.WriteBoolean("builtin", s.Builtin);
            if (!string.IsNullOrEmpty(s.Path)) writer.WriteString("path", s.Path);
            writer.WriteEndObject();
        }

        /// <summary>
        /// 归一化：内置 → name 归「zh/en」（非法回落 zh）、清掉 path；
        /// 自定义 → name 固定「other」且必须有路径，缺路径视为配置无效、退化为内置中文（免得运行时无从下手）。
        /// </summary>
        private static LanguageSetting Normalize(LanguageSetting s)
        {
            if (s == null) return new LanguageSetting();

            if (s.Builtin)
                return new LanguageSetting
                {
                    Name = LanguageManager.NormalizeBuiltInCode(s.Name) ?? LanguageManager.BuiltInZh,
                    Builtin = true
                };

            if (string.IsNullOrWhiteSpace(s.Path))
                return new LanguageSetting();

            return new LanguageSetting
            {
                Name = LanguageManager.CustomCode,
                Builtin = false,
                Path = s.Path.Trim()
            };
        }
    }
}
