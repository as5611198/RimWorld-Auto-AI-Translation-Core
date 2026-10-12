using AutoTranslator_Core.TargetedHardcodedUi;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Reflection;

internal static class Program
{
    private sealed class Baseline
    {
        internal int Classification;
        internal string Reason;
        internal HardcodedUiPatchEntry Entry;
    }

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: probe <assembly-path> <workflow-db-path>");
            return 2;
        }

        var baseline = new Dictionary<string, Baseline>(StringComparer.Ordinal);
        using (var connection = new SQLiteConnection(
                   "Data Source=" + args[1] + ";Version=3;Read Only=True;Pooling=False;"))
        {
            connection.Open();
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT candidate_id, context_json,
                    ((classification_flags >> 2) & 3) AS local_classification,
                    dll_reason_code
                    FROM Candidates
                    WHERE mod_identity='pkg:slim.quartermaster'
                      AND source_domain=1 AND is_present=1
                    ORDER BY candidate_id;";
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        HardcodedUiPatchEntry entry = JsonConvert.DeserializeObject<HardcodedUiPatchEntry>(
                            Convert.ToString(reader["context_json"]));
                        if (entry == null || string.IsNullOrWhiteSpace(entry.EntryId)) continue;
                        baseline[entry.EntryId] = new Baseline
                        {
                            Classification = Convert.ToInt32(reader["local_classification"]),
                            Reason = Convert.ToString(reader["dll_reason_code"]),
                            Entry = entry
                        };
                    }
                }
            }
        }

        Assembly core = typeof(HardcodedUiPatchEntry).Assembly;
        Type analyzer = core.GetType(
            "AutoTranslator_Core.TargetedHardcodedUi.HardcodedUiIlDataflowAnalyzer", true);
        MethodInfo analyze = analyzer.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "Analyze" && method.GetParameters().Length == 3);
        object result = analyze.Invoke(null, new object[]
        {
            args[0], baseline.Values.Select(value => value.Entry).ToList(), null
        });
        FieldInfo decisionsField = result.GetType().GetField(
            "Decisions", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo diagnosticsField = result.GetType().GetField(
            "Diagnostics", BindingFlags.Instance | BindingFlags.NonPublic);
        var decisions = (Dictionary<string, HardcodedUiDecisionRecord>)decisionsField.GetValue(result);
        var diagnostics = (List<string>)diagnosticsField.GetValue(result);

        Console.WriteLine("BASELINE_TOTAL=" + baseline.Count);
        foreach (IGrouping<int, Baseline> group in baseline.Values
                     .GroupBy(value => value.Classification).OrderBy(group => group.Key))
            Console.WriteLine("BASELINE_CLASS_" + group.Key + "=" + group.Count());

        Console.WriteLine("NEW_TOTAL=" + decisions.Count);
        foreach (IGrouping<HardcodedUiAutomaticDecision, HardcodedUiDecisionRecord> group in decisions.Values
                     .GroupBy(value => value.AutomaticDecision).OrderBy(group => (int)group.Key))
            Console.WriteLine("NEW_" + group.Key.ToString().ToUpperInvariant() + "=" + group.Count());
        Console.WriteLine("DIAGNOSTICS=" + diagnostics.Count);

        Console.WriteLine("REASONS");
        foreach (var group in decisions.Values.GroupBy(value => new
                 {
                     value.AutomaticDecision,
                     value.AutomaticReasonCode
                 }).OrderByDescending(group => group.Count()).ThenBy(group => group.Key.AutomaticReasonCode))
            Console.WriteLine(group.Key.AutomaticDecision + "\t" +
                              group.Key.AutomaticReasonCode + "\t" + group.Count());

        Console.WriteLine("CHANGES");
        var changed = decisions.Values.Where(record =>
                baseline.TryGetValue(record.EntryId, out Baseline old) &&
                MapDecision(record.AutomaticDecision) != old.Classification)
            .ToList();
        foreach (var group in changed.GroupBy(record => new
                 {
                     Old = baseline[record.EntryId].Classification,
                     New = record.AutomaticDecision,
                     record.AutomaticReasonCode
                 }).OrderByDescending(group => group.Count()))
            Console.WriteLine(group.Key.Old + "->" + group.Key.New + "\t" +
                              group.Key.AutomaticReasonCode + "\t" + group.Count());

        Console.WriteLine("SAMPLES");
        foreach (var group in changed.GroupBy(record => new
                 {
                     record.AutomaticDecision,
                     record.AutomaticReasonCode
                 }).OrderByDescending(group => group.Count()))
        {
            Console.WriteLine("[" + group.Key.AutomaticDecision + "/" +
                              group.Key.AutomaticReasonCode + "]");
            foreach (HardcodedUiDecisionRecord record in group.Take(8))
            {
                HardcodedUiPatchEntry entry = baseline[record.EntryId].Entry;
                Console.WriteLine(entry.DeclaringType + "." + entry.MethodName + "\t" +
                                  Escape(entry.Literal) + "\t" + record.EvidencePath);
            }
        }
        Console.WriteLine("UNCERTAIN_METHODS");
        foreach (var group in decisions.Values
                     .Where(record => record.AutomaticDecision == HardcodedUiAutomaticDecision.Uncertain)
                     .GroupBy(record =>
                     {
                         HardcodedUiPatchEntry entry = baseline[record.EntryId].Entry;
                         return entry.DeclaringType + "." + entry.MethodName;
                     })
                     .OrderByDescending(group => group.Count()).Take(45))
        {
            Console.WriteLine(group.Count() + "\t" + group.Key + "\t" +
                              string.Join(" | ", group.Take(12).Select(record =>
                                  Escape(baseline[record.EntryId].Entry.Literal))));
        }
        return 0;
    }

    private static int MapDecision(HardcodedUiAutomaticDecision decision)
    {
        if (decision == HardcodedUiAutomaticDecision.Translate) return 2;
        if (decision == HardcodedUiAutomaticDecision.DoNotTranslate) return 3;
        return 1;
    }

    private static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
