using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GDPackEditor
{
    public class TemplateVariableInfo
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Class { get; set; }
        public string DefaultValue { get; set; }
        public string Description { get; set; }
        public string GroupName { get; set; }
        public string Value { get; set; }
        public bool IsReadOnly => Class == "static";
        public bool IsPicklist => Class == "picklist";
        public bool IsArray => Class == "array";
        public string[] PicklistOptions { get; set; } = Array.Empty<string>();
    }

    public class TemplateGroupInfo
    {
        public string Name { get; set; }
        public List<TemplateVariableInfo> Variables { get; set; } = new();
    }

    public static class TemplateHelper
    {
        public static List<TemplateVariableInfo> CollectAllVariables(TemplateNode root, Dictionary<string, string> dbrValues)
        {
            var result = new Dictionary<string, TemplateVariableInfo>(StringComparer.OrdinalIgnoreCase);
            var collectProcessed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectVarsFromNode(root, result, dbrValues, collectProcessed);

            var ordered = new List<TemplateVariableInfo>();
            var orderProcessed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectOrdered(root, result, ordered, orderProcessed);
            return ordered;
        }

        public static List<TemplateGroupInfo> CollectGroups(TemplateNode root, Dictionary<string, string> dbrValues)
        {
            var allVars = CollectAllVariables(root, dbrValues);
            var groups = new List<TemplateGroupInfo>();
            foreach (var g in allVars.GroupBy(v => v.GroupName ?? ""))
            {
                groups.Add(new TemplateGroupInfo { Name = g.Key, Variables = g.ToList() });
            }
            return groups;
        }

        /// <summary>"不在模板中"字段的 GroupName 哨兵（分组树 / 网格行据此识别）</summary>
        public const string NotInTemplateGroupName = "__notpl__";

        /// <summary>
        /// 收集"DBR 中存在、模板未定义"的字段。**独立列表**，不混入模板变量集——
        /// 保存 / 批量编辑 / 多文件合并等既有逻辑全部以模板变量集为准，混入会波及。
        /// Class/Type/DefaultValue/Description 均为空（模板没有定义），顺序保持 DBR 文件内出现顺序；
        /// templateName 是文件元数据而非数据字段，显式排除。
        /// </summary>
        public static List<TemplateVariableInfo> CollectNotInTemplateVariables(
            Dictionary<string, string> dbrValues, IEnumerable<TemplateVariableInfo> templateVars)
        {
            var list = new List<TemplateVariableInfo>();
            if (dbrValues == null || dbrValues.Count == 0) return list;
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (templateVars != null)
                foreach (var v in templateVars)
                {
                    if (v != null && !string.IsNullOrEmpty(v.Name)) known.Add(v.Name);
                }
            foreach (var kv in dbrValues)
            {
                if (known.Contains(kv.Key)) continue;
                if (string.Equals(kv.Key, "templateName", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new TemplateVariableInfo
                {
                    Name = kv.Key,
                    Type = "",
                    Class = "",
                    DefaultValue = "",
                    Description = "",
                    GroupName = NotInTemplateGroupName,
                    Value = kv.Value ?? ""
                });
            }
            return list;
        }

        public static Dictionary<string, string> ParseDbrLines(string[] lines)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (lines == null || lines.Length == 0) return result;
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                // 由 Split(',') 整数组改为"两次 IndexOf 取前两段"：本方法在**每条记录的每一行**上被调用，
                // 按行分配字符串数组是主要 GC 来源之一（口径与既有解析完全一致）
                if (!TryGetFirstTwoSegments(line, out string key, out string val)) continue;
                if (string.IsNullOrEmpty(key)) continue;
                result[key] = val;   // templateName 与普通字段同处理（原实现两分支赋值相同）
            }
            return result;
        }

        /// <summary>
        /// 按逗号取前两段（第 1 段 = 键、第 2 段 = 值）：两次 <see cref="string.IndexOf(char)"/> 取代
        /// <c>Split(',')</c> 的整行数组分配。口径与既有解析一致 —— 第 2 段 = 第一个逗号与第二个逗号之间的
        /// 内容（无第二个逗号时取到行尾）；整行无逗号视为"不足两段"（对应原 <c>parts.Length &lt; 2</c>）。
        /// </summary>
        private static bool TryGetFirstTwoSegments(string line, out string key, out string value)
        {
            key = null;
            value = null;
            int c1 = line.IndexOf(',');
            if (c1 < 0) return false;
            key = line.Substring(0, c1).Trim();
            int c2 = line.IndexOf(',', c1 + 1);
            value = (c2 < 0 ? line.Substring(c1 + 1) : line.Substring(c1 + 1, c2 - c1 - 1)).Trim();
            return true;
        }

        public static string GetDbrTemplateName(string[] lines)
        {
            if (lines == null || lines.Length == 0) return null;
            string first = lines[0].Trim();
            if (!first.StartsWith("templateName")) return null;
            return TryGetFirstTwoSegments(first, out _, out string val) ? val : null;
        }

        private static void CollectVarsFromNode(
            TemplateNode node,
            Dictionary<string, TemplateVariableInfo> result,
            Dictionary<string, string> dbrValues,
            HashSet<string> processedFiles)
        {
            string tplFile = node.GetTemplateFile();
            if (tplFile != null && !processedFiles.Add(tplFile.ToLowerInvariant()))
                return;

            foreach (var sub in node.subitems)
            {
                if (sub.kind == "group")
                {
                    string gName = sub.values.TryGetValue("name", out var gn) ? gn : "";
                    CollectVarsFromGroup(sub, gName, result, dbrValues, processedFiles);
                }
                else if (sub.kind == "variable")
                {
                    string type = sub.values.TryGetValue("type", out var t) ? t : "";
                    if (type == "include") continue;
                    string name = sub.values.TryGetValue("name", out var n) ? n : "";
                    if (string.IsNullOrEmpty(name)) continue;
                    if (string.Equals(name, "Object Variable", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!result.ContainsKey(name))
                    {
                        string cls = sub.values.TryGetValue("class", out var c) ? c : "variable";
                        string def = sub.values.TryGetValue("defaultValue", out var dv) ? dv : "";
                        string desc = sub.values.TryGetValue("description", out var d) ? d : "";
                        dbrValues.TryGetValue(name, out var val);
                        result[name] = new TemplateVariableInfo
                        {
                            Name = name,
                            Type = type,
                            Class = cls,
                            DefaultValue = def,
                            Description = desc,
                            GroupName = "",
                            Value = val ?? "",
                            PicklistOptions = !string.IsNullOrEmpty(def) && def.Contains(';') && cls != "array" ? def.Split(';') : Array.Empty<string>()
                        };
                    }
                }
            }

            foreach (var incl in node.includes)
                CollectVarsFromNode(incl, result, dbrValues, processedFiles);
        }

        private static void CollectVarsFromGroup(
            TemplateNode groupNode,
            string groupPath,
            Dictionary<string, TemplateVariableInfo> result,
            Dictionary<string, string> dbrValues,
            HashSet<string> processedFiles)
        {
            foreach (var sub in groupNode.subitems)
            {
                if (sub.kind == "group")
                {
                    string subName = sub.values.TryGetValue("name", out var gn) ? gn : "";
                    string subPath = string.IsNullOrEmpty(groupPath) ? subName : groupPath + "/" + subName;
                    CollectVarsFromGroup(sub, subPath, result, dbrValues, processedFiles);
                }
                else if (sub.kind == "variable")
                {
                    string type = sub.values.TryGetValue("type", out var t) ? t : "";
                    if (type == "include") continue;
                    string name = sub.values.TryGetValue("name", out var n) ? n : "";
                    if (string.IsNullOrEmpty(name)) continue;
                    if (string.Equals(name, "Object Variable", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!result.ContainsKey(name))
                    {
                        string cls = sub.values.TryGetValue("class", out var c) ? c : "variable";
                        string def = sub.values.TryGetValue("defaultValue", out var dv) ? dv : "";
                        string desc = sub.values.TryGetValue("description", out var d) ? d : "";
                        dbrValues.TryGetValue(name, out var val);
                        result[name] = new TemplateVariableInfo
                        {
                            Name = name,
                            Type = type,
                            Class = cls,
                            DefaultValue = def,
                            Description = desc,
                            GroupName = groupPath,
                            Value = val ?? "",
                            PicklistOptions = !string.IsNullOrEmpty(def) && def.Contains(';') && cls != "array" ? def.Split(';') : Array.Empty<string>()
                        };
                    }
                }
            }
            foreach (var incl in groupNode.includes)
                CollectVarsFromNode(incl, result, dbrValues, processedFiles);
        }

        private static void CollectOrdered(
            TemplateNode node,
            Dictionary<string, TemplateVariableInfo> allVars,
            List<TemplateVariableInfo> ordered,
            HashSet<string> processedFiles)
        {
            string tplFile = node.GetTemplateFile();
            if (tplFile != null && !processedFiles.Add(tplFile.ToLowerInvariant()))
                return;

            foreach (var sub in node.subitems)
            {
                if (sub.kind == "group")
                {
                    string gName = sub.values.TryGetValue("name", out var gn) ? gn : "";
                    CollectOrderedFromGroup(sub, gName, allVars, ordered);
                }
                else if (sub.kind == "variable")
                {
                    string type = sub.values.TryGetValue("type", out var t) ? t : "";
                    if (type == "include") continue;
                    string name = sub.values.TryGetValue("name", out var n) ? n : "";
                    if (allVars.TryGetValue(name, out var info) && !ordered.Contains(info))
                        ordered.Add(info);
                }
            }

            foreach (var incl in node.includes)
                CollectOrdered(incl, allVars, ordered, processedFiles);
        }

        private static void CollectOrderedFromGroup(
            TemplateNode groupNode,
            string groupPath,
            Dictionary<string, TemplateVariableInfo> allVars,
            List<TemplateVariableInfo> ordered)
        {
            foreach (var sub in groupNode.subitems)
            {
                if (sub.kind == "group")
                {
                    string subName = sub.values.TryGetValue("name", out var gn) ? gn : "";
                    string subPath = string.IsNullOrEmpty(groupPath) ? subName : groupPath + "/" + subName;
                    CollectOrderedFromGroup(sub, subPath, allVars, ordered);
                }
                else if (sub.kind == "variable")
                {
                    string type = sub.values.TryGetValue("type", out var t) ? t : "";
                    if (type == "include") continue;
                    string name = sub.values.TryGetValue("name", out var n) ? n : "";
                    if (allVars.TryGetValue(name, out var info) && !ordered.Contains(info))
                        ordered.Add(info);
                }
            }
        }

        public static string GetEntryValue(ARZEntry entry)
        {
            string text = entry.ToString().TrimEnd('\n', '\r');
            int firstComma = text.IndexOf(',');
            if (firstComma < 0) return "";
            string after = text.Substring(firstComma + 1);
            return after.TrimEnd(',');
        }

        /// <summary>
        /// 解码文本字节（统一走 <see cref="TextFileHelper.DecodeText"/>，编码判定收敛到唯一真源）。
        /// </summary>
        public static string DecodeText(byte[] data) => TextFileHelper.DecodeText(data);

        /// <summary>同上，并回带**实际用到的编码**（供调用方按同一编码回写，实现零转码）。</summary>
        public static string DecodeText(byte[] data, out Encoding usedEncoding)
            => TextFileHelper.DecodeText(data, out usedEncoding);

        /// <summary>
        /// 按实际编码读取 DBR 文本行：BOM → 严格 UTF-8 → 本机 ACP 依次判定（统一走
        /// <see cref="TextFileHelper.ReadTextLines"/>，**只读一次文件**）。
        /// 兼容 UTF-8（有/无 BOM）与本机 ANSI（如简体 GBK）源文件，避免按单一编码读取造成中文乱码。
        /// </summary>
        public static string[] ReadDbrLines(string filePath)
        {
            return TextFileHelper.ReadTextLines(filePath);
        }

        /// <summary>同上，并回带**实际用到的编码**（供打包 / 保存按同一编码回写，实现零转码）。</summary>
        public static string[] ReadDbrLines(string filePath, out Encoding usedEncoding)
        {
            return TextFileHelper.ReadTextLines(filePath, out usedEncoding);
        }

        /// <summary>
        /// 检测文本文件实际编码（统一走 <see cref="TextFileHelper.DetectEncodingFromFile"/>）。
        /// </summary>
        public static Encoding DetectFileEncoding(string filePath)
            => TextFileHelper.DetectEncodingFromFile(filePath);

        /// <summary>可编辑扩展名（收敛到公共白名单 TextFileHelper.TextExtensions，避免多处重复定义）</summary>
        public static readonly HashSet<string> EditableExtensions =
            new(TextFileHelper.TextExtensions, StringComparer.OrdinalIgnoreCase);

        public static bool IsEditable(string fileName) => TextFileHelper.IsTextFile(fileName);
    }
}
