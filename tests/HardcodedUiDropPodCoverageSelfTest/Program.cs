using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

internal static class Program
{
    private static string _managedDirectory;
    private static string _dependencyDirectory;
    private static string _modAssemblyDirectory;

    private static int Main(string[] args)
    {
        if (args.Length < 5 || args.Length > 6)
        {
            Console.Error.WriteLine("Usage: coverage <managed-dir> <dependency-dir> <core.dll> <mod-root> <gold-source.cs> [report.md]");
            return 2;
        }

        _managedDirectory = Path.GetFullPath(args[0]);
        _dependencyDirectory = Path.GetFullPath(args[1]);
        string corePath = Path.GetFullPath(args[2]);
        string modRoot = Path.GetFullPath(args[3]);
        string goldSource = Path.GetFullPath(args[4]);
        string targetDll = Path.Combine(modRoot, "1.6", "Assemblies", "KKDropPodJammer.dll");
        _modAssemblyDirectory = Path.GetDirectoryName(targetDll);
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

        try
        {
            Assembly game = Assembly.LoadFrom(Path.Combine(_managedDirectory, "Assembly-CSharp.dll"));
            Assembly targetAssembly = Assembly.LoadFrom(targetDll);
            Assembly core = Assembly.LoadFrom(corePath);
            Type metadataType = game.GetType("Verse.ModMetaData", true);
            object metadata = FormatterServices.GetUninitializedObject(metadataType);
            metadataType.GetField("rootDirInt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SetValue(metadata, new DirectoryInfo(modRoot));
            metadataType.GetField("packageIdLowerCase", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SetValue(metadata, "kongkim.droppodjammer");
            Type scannerType = core.GetType(
                "AutoTranslator_Core.TargetedHardcodedUi.HardcodedUiRuntimeScanner", true);
            object result = scannerType.GetMethod(
                "Scan", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { metadata });
            Type resultType = result.GetType();
            IList entries = (IList)resultType.GetField(
                "Entries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result);
            IList diagnostics = (IList)resultType.GetField(
                "Diagnostics", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result);
            IDictionary decisions = (IDictionary)resultType.GetField(
                "Decisions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result);

            var found = new HashSet<string>(StringComparer.Ordinal);
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var candidateRows = new List<string>();
            foreach (object entry in entries)
            {
                Type entryType = entry.GetType();
                string literal = (string)entryType.GetProperty("Literal").GetValue(entry, null);
                found.Add(literal);
                string kind = (string)entryType.GetProperty("DiscoveryKind").GetValue(entry, null);
                kinds[kind] = kinds.TryGetValue(kind, out int count) ? count + 1 : 1;
                string declaringType = (string)entryType.GetProperty("DeclaringType").GetValue(entry, null);
                string methodName = (string)entryType.GetProperty("MethodName").GetValue(entry, null);
                int ordinal = (int)entryType.GetProperty("LiteralOrdinal").GetValue(entry, null);
                candidateRows.Add("| " + EscapeMarkdown(declaringType) + " | " +
                    EscapeMarkdown(methodName) + " | " + ordinal + " | " +
                    EscapeMarkdown(kind) + " | " + EscapeMarkdown(literal) + " |");
            }

            List<string> gold = string.Equals(
                    Path.GetExtension(goldSource),
                    ".md",
                    StringComparison.OrdinalIgnoreCase)
                ? ReadMarkdownCandidateGold(goldSource)
                : Regex.Matches(
                        File.ReadAllText(goldSource),
                        "\\{\\s*\"((?:\\\\.|[^\"\\\\])*)\"\\s*,\\s*\"")
                    .Cast<Match>()
                    .Select(match => Regex.Unescape(match.Groups[1].Value))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            List<string> missing = gold.Where(text => !found.Contains(text)).ToList();
            Dictionary<string, string> assemblyStringOwners = ReadAssemblyStrings(targetAssembly);
            HashSet<string> actualAssemblyStrings = new HashSet<string>(assemblyStringOwners.Keys, StringComparer.Ordinal);
            List<string> presentGold = gold.Where(actualAssemblyStrings.Contains).ToList();
            List<string> missingPresent = presentGold.Where(text => !found.Contains(text)).ToList();
            double coverage = gold.Count == 0 ? 0d : 100d * (gold.Count - missing.Count) / gold.Count;
            double presentCoverage = presentGold.Count == 0
                ? 0d
                : 100d * (presentGold.Count - missingPresent.Count) / presentGold.Count;
            Console.WriteLine("Candidates: " + entries.Count);
            Console.WriteLine("Diagnostics: " + diagnostics.Count);
            foreach (object diagnostic in diagnostics)
                Console.WriteLine("DIAGNOSTIC: " + diagnostic);
            Console.WriteLine("Gold: " + gold.Count);
            Console.WriteLine("Covered: " + (gold.Count - missing.Count));
            Console.WriteLine("Coverage: " + coverage.ToString("0.00") + "%");
            Console.WriteLine("Gold present in current DLL: " + presentGold.Count);
            Console.WriteLine("Current-DLL coverage: " + presentCoverage.ToString("0.00") + "%");
            foreach (KeyValuePair<string, int> pair in kinds.OrderBy(pair => pair.Key))
                Console.WriteLine("Kind " + pair.Key + ": " + pair.Value);
            var decisionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (DictionaryEntry pair in decisions)
            {
                object record = pair.Value;
                string decision = record.GetType().GetProperty("EffectiveDecision").GetValue(record, null).ToString();
                decisionCounts[decision] = decisionCounts.TryGetValue(decision, out int count) ? count + 1 : 1;
            }
            foreach (KeyValuePair<string, int> pair in decisionCounts.OrderBy(pair => pair.Key))
                Console.WriteLine("Decision " + pair.Key + ": " + pair.Value);
            foreach (string text in missing)
                Console.WriteLine("MISSING: " + text.Replace("\n", "\\n") + " | " + assemblyStringOwners[text]);
            if (args.Length == 6)
            {
                string reportPath = Path.GetFullPath(args[5]);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
                var report = new List<string>
                {
                    "# Drop Pod Raid Jammer DLL 硬编码文本候选清单",
                    string.Empty,
                    "- 扫描对象：`KKDropPodJammer.dll`",
                    "- 候选总数：" + entries.Count,
                    "- 已知人工补丁文本覆盖：" + (gold.Count - missing.Count) + "/" + gold.Count +
                        "（" + coverage.ToString("0.00") + "%）",
                    "- 说明：本表是召回优先的扫描结果，不代表每一条都应翻译；后续由 Agent 或人工过滤。",
                    string.Empty,
                    "| 声明类型 | 方法 | 字符串序号 | 发现类别 | 原文 |",
                    "|---|---|---:|---|---|"
                };
                report.AddRange(candidateRows);
                File.WriteAllLines(reportPath, report);
                Console.WriteLine("Report: " + reportPath);
            }
            return presentCoverage >= 100d ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static List<string> ReadMarkdownCandidateGold(string path)
    {
        var result = new List<string>();
        foreach (string line in File.ReadLines(path))
        {
            Match match = Regex.Match(
                line,
                @"^\| .*? \| .*? \| .*? \| .*? \| (.*) \|$");
            if (!match.Success || line.StartsWith("|---", StringComparison.Ordinal)) continue;
            string value = match.Groups[1].Value
                .Replace("\\|", "|")
                .Replace("<br>", "\n");
            if (string.Equals(value, "原文", StringComparison.Ordinal)) continue;
            if (!result.Contains(value, StringComparer.Ordinal)) result.Add(value);
        }
        return result;
    }

    private static string EscapeMarkdown(string value)
    {
        return (value ?? string.Empty)
            .Replace("|", "\\|")
            .Replace("\r\n", "<br>")
            .Replace("\n", "<br>")
            .Replace("\r", "<br>");
    }

    private static Dictionary<string, string> ReadAssemblyStrings(Assembly assembly)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Type type in assembly.GetTypes())
        {
            IEnumerable<MethodBase> members = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance));
            foreach (MethodBase member in members)
            {
                MethodBody body;
                try { body = member.GetMethodBody(); } catch { continue; }
                byte[] il = body?.GetILAsByteArray();
                if (il == null) continue;
                for (int i = 0; i <= il.Length - 5; i++)
                {
                    if (il[i] != 0x72) continue;
                    try
                    {
                        string text = member.Module.ResolveString(BitConverter.ToInt32(il, i + 1));
                        output[text] = member.DeclaringType.FullName + "." + member.Name;
                    }
                    catch { }
                }
            }
        }
        return output;
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        foreach (string directory in new[] { _dependencyDirectory, _managedDirectory, _modAssemblyDirectory })
        {
            string path = Path.Combine(directory ?? string.Empty, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
}
