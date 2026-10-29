using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        // Manual work only: inspect failed native candidates without editing their
        // source mods, inventing missing objects or clearing the game's diagnostics.
        internal sealed class NativeTranslationRepairWork
        {
            private sealed class Candidate
            {
                public DefInjectionPackage Package;
                public string Key;
                public DefInjectionPackage.DefInjection Injection;
            }
            private readonly List<Candidate> candidates = new List<Candidate>();
            private readonly Dictionary<Type, Dictionary<string, DefInjectionPackage.DefInjection>> selected =
                new Dictionary<Type, Dictionary<string, DefInjectionPackage.DefInjection>>();
            private readonly Dictionary<Type, Dictionary<string, List<DefInjectionPackage.DefInjection>>> appliedByDef =
                new Dictionary<Type, Dictionary<string, List<DefInjectionPackage.DefInjection>>>();
            private readonly TranslationRepairRun run;
            private readonly string[] general;
            private readonly Queue<Action> changes = new Queue<Action>();
            private readonly HashSet<string> backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private Task preservation;
            private bool finalSave;
            public int Processed { get; private set; }
            public int Total => candidates.Count + general.Length;
            public bool Done { get; private set; }

            public NativeTranslationRepairWork(LoadedLanguage language, TranslationRepairRun run)
            {
                this.run = run;
                general = language?.loadErrors?.ToArray() ?? new string[0];
                foreach (var package in language?.defInjections ?? new List<DefInjectionPackage>())
                {
                    if (!selected.TryGetValue(package.defType, out var targets))
                    {
                        selected[package.defType] = targets = new Dictionary<string, DefInjectionPackage.DefInjection>(StringComparer.Ordinal);
                        appliedByDef[package.defType] = new Dictionary<string, List<DefInjectionPackage.DefInjection>>(StringComparer.Ordinal);
                    }
                    foreach (var pair in package.injections)
                    {
                        if (pair.Value.injected)
                        {
                            string name = (pair.Value.normalizedPath ?? pair.Key).Split('.')[0];
                            name = BackCompatibility.BackCompatibleDefName(package.defType, name, true);
                            var roots = appliedByDef[package.defType];
                            if (!roots.TryGetValue(name, out var values)) roots[name] = values = new List<DefInjectionPackage.DefInjection>();
                            values.Add(pair.Value);
                        }
                        else if (!pair.Value.injected && !IsOwnedTranslationFile(pair.Value.fileSource))
                            candidates.Add(new Candidate { Package = package, Key = pair.Key, Injection = pair.Value });
                    }
                }
            }

            public void Tick()
            {
                if (Done) return;
                if (preservation != null)
                {
                    if (!preservation.IsCompleted) return;
                    preservation.GetAwaiter().GetResult(); // No memory changes on preservation failure.
                    preservation = null;
                    if (finalSave) { Done = true; return; }
                }
                var timer = Stopwatch.StartNew();
                while (changes.Count > 0 && TranslationFrameBudget.HasTime) changes.Dequeue()();
                if (changes.Count > 0) return;
                if (Processed >= Total)
                {
                    finalSave = true;
                    preservation = Task.Run(() => FlushSelfRepair());
                    return;
                }
                var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int budget = 64;
                while (Processed < Total && budget-- > 0 && TranslationFrameBudget.HasTime)
                {
                    int index = Processed++;
                    if (index < candidates.Count)
                    {
                        var candidate = candidates[index];
                        if (candidate.Injection.fileSource != null && backedUp.Add(candidate.Injection.fileSource))
                            sources.Add(candidate.Injection.fileSource);
                        CheckCandidate(candidate);
                    }
                    else ObserveGeneral(general[index - candidates.Count]);
                }
                // Only file preservation runs off-thread. Def traversal and native
                // injection stay on the main-thread pump, including when minimized.
                preservation = Task.Run(() => {
                    foreach (string source in sources) if (File.Exists(source)) RepairJournal.Backup(source);
                    FlushSelfRepair();
                });
            }

            private void ObserveGeneral(string error)
            {
                var duplicate = Regex.Match(error, @"^Duplicate keyed translation key: (\S+)");
                var missing = Regex.Match(error, @": dir (.+?) doesn't correspond to any def type");
                var record = new TranslationRepairRecord {
                    SourceFile = "ATC:native-language", SourceKind = "native-general-error", GameVersion = CurrentRimWorldVersion,
                    OriginalKey = duplicate.Success ? duplicate.Groups[1].Value : missing.Success ? missing.Groups[1].Value : error,
                    DefType = duplicate.Success ? "Keyed" : missing.Success ? missing.Groups[1].Value : null,
                    Reason = duplicate.Success ? "duplicate_key" : missing.Success ? "missing_def_type" : "native_language_load_error",
                    Status = duplicate.Success ? "衝突" : missing.Success ? "暫不適用" : "仍待處理", Evidence = error
                };
                RepairJournal.Record(record);
                run.Observe(record);
            }

            private void CheckCandidate(Candidate candidate)
            {
                var package = candidate.Package;
                var original = candidate.Injection;
                if (!package.injections.TryGetValue(candidate.Key, out var current) || !ReferenceEquals(current, original) || original.injected) return;
                bool fullList = original.IsFullListInjection;
                var check = ValidateLoadedDefTranslation(package.defType, candidate.Key, fullList);
                string value = fullList ? Newtonsoft.Json.JsonConvert.SerializeObject(original.fullListInjection) : original.injection;
                run.Observe(RecordDefRepair(package.defType.FullName, candidate.Key, value, original.fileSource,
                    check, "third-party-diagnostic", check.Allowed ? "僅診斷" : null));
            }

            private static void RemoveIfUnchanged(Candidate candidate)
            {
                if (candidate.Package.injections.TryGetValue(candidate.Key, out var current) && ReferenceEquals(current, candidate.Injection))
                    candidate.Package.injections.Remove(candidate.Key);
            }
        }
    }
}
