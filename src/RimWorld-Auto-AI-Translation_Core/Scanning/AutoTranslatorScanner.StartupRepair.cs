using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        // Completion is independent of the optional all-valid injection cache.
        // Unresolved records stay in the journal; they are not unfinished work.
        private static MemoryDropPayload completedRepairPayload;
        private sealed class CompletedRepairReceipt
        {
            public int Schema = 1;
            public string GameVersion, TargetFolder;
            public Dictionary<string, string> FileHashes;
        }

        private sealed class RepairBaseline
        {
            public int Schema = 1;
            public Dictionary<string, Dictionary<string, string>> Languages = new Dictionary<string, Dictionary<string, string>>();
        }

        internal sealed class StartupRepairPlan
        {
            internal bool BaselineExists;
            internal string[] ChangedFiles = new string[0];
            internal string Decision => TranslationStartupRepairPolicy.DecideStartup(BaselineExists, ChangedFiles.Length != 0);
        }

        private static readonly object baselineGate = new object();

        private static string CompletedRepairPath(string folder) => Path.Combine(GetLocalPackPath(), "SelfRepair",
            "baseline.json");

        // Existence is the only automatic-full gate. Contents, game version,
        // language, rejected entries and cache validity cannot change it.
        internal static bool RepairBaselineExists => File.Exists(CompletedRepairPath(null));

        private static RepairBaseline ReadRepairBaseline()
        {
            try
            {
                var baseline = JsonConvert.DeserializeObject<RepairBaseline>(File.ReadAllText(CompletedRepairPath(null)));
                if (baseline?.Schema != 1 || baseline.Languages == null) return null;
                foreach (var folder in baseline.Languages.Keys.ToArray())
                    baseline.Languages[folder] = new Dictionary<string, string>(baseline.Languages[folder], StringComparer.OrdinalIgnoreCase);
                return baseline;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException)
            { return null; }
        }

        private static void SaveRepairBaseline(RepairBaseline baseline, Func<bool> current)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(baseline));
            TranslationXmlAtomicFileStore.SaveIfCurrent(CompletedRepairPath(null),
                stream => stream.Write(bytes, 0, bytes.Length), current);
        }

        internal static StartupRepairPlan PlanStartupRepair(TargetLanguage language)
        {
            lock (baselineGate)
            {
                string folder = GetFolderNameByLanguage(language);
                // One-way migration: a previous completed repair is already the
                // first full run, even when its files have since changed.
                if (!RepairBaselineExists)
                {
                    string history = Path.Combine(GetLocalPackPath(), "SelfRepair");
                    var legacyFiles = Directory.Exists(history) ? Directory.GetFiles(history, "completed-*.json") : new string[0];
                    bool acknowledged = (AutoTranslatorMod.Settings?.LastRepairNotice ?? "").StartsWith("main-menu-repair-v3:", StringComparison.Ordinal) ||
                        !string.IsNullOrEmpty(AutoTranslatorMod.Settings?.StartupRepairSignature);
                    if (legacyFiles.Length != 0 || acknowledged)
                    {
                        var migrated = new RepairBaseline();
                        foreach (string file in legacyFiles.OrderBy(File.GetLastWriteTimeUtc))
                        {
                            try
                            {
                                var old = JsonConvert.DeserializeObject<CompletedRepairReceipt>(File.ReadAllText(file));
                                if (old?.Schema == 1 && old.TargetFolder != null && old.FileHashes != null)
                                    migrated.Languages[old.TargetFolder] = old.FileHashes;
                            }
                            catch (Exception ex) when (ex is IOException || ex is JsonException)
                            { Log.Warning("[ATC] Legacy repair receipt skipped: " + ex.Message); }
                        }
                        // Missing legacy hashes are adopted once, not repaired again.
                        if (!migrated.Languages.ContainsKey(folder)) migrated.Languages[folder] = OwnedTranslationFileHashes(folder);
                        SaveRepairBaseline(migrated, () => !RepairBaselineExists);
                    }
                }
                var plan = new StartupRepairPlan { BaselineExists = RepairBaselineExists };
                if (!plan.BaselineExists) return plan;
                var baseline = ReadRepairBaseline();
                if (baseline == null)
                {
                    Log.Warning("[ATC] baseline.json is unreadable; skipping automatic repair. Automatic full repair is forbidden while it exists.");
                    return plan;
                }
                var current = OwnedTranslationFileHashes(folder);
                if (!baseline.Languages.TryGetValue(folder, out var previous))
                    previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                plan.ChangedFiles = current.Where(p => !previous.TryGetValue(p.Key, out string hash) || hash != p.Value)
                    .Select(p => p.Key).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                // Deletions require no XML parsing or verification. Keep changes
                // pending until their scoped run completes.
                var remaining = previous.Where(p => File.Exists(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                if (remaining.Count != previous.Count)
                {
                    baseline.Languages[folder] = remaining;
                    SaveRepairBaseline(baseline, () => RepairBaselineExists);
                }
                return plan;
            }
        }

        private static CompletedRepairReceipt ReadCompletedRepairReceipt(string folder)
        {
            lock (baselineGate)
            {
                var baseline = ReadRepairBaseline();
                return baseline != null && baseline.Languages.TryGetValue(folder, out var hashes)
                    ? new CompletedRepairReceipt { GameVersion = CurrentRimWorldVersion, TargetFolder = folder, FileHashes = hashes } : null;
            }
        }

        private static Dictionary<string, string> OwnedTranslationFileHashes(string folder)
        {
            string root = Path.Combine(GetLocalPackPath(), "Languages", folder);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(root)) return hashes;
            foreach (string file in Directory.GetFiles(root, "*.xml", SearchOption.AllDirectories))
                try { hashes[Path.GetFullPath(file)] = TranslationFileTransaction.FileHash(file); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { Log.Warning("[ATC] Translation fingerprint skipped: " + file + ": " + ex.Message); }
            return hashes;
        }

        private static bool SameRepairFiles(Dictionary<string, string> expected, Dictionary<string, string> current)
            => expected != null && current != null && expected.Count == current.Count &&
                expected.All(p => current.TryGetValue(p.Key, out string hash) && hash == p.Value);

        internal static bool HasCompletedStartupRepair(TargetLanguage language)
        {
            string folder = GetFolderNameByLanguage(language);
            var receipt = ReadCompletedRepairReceipt(folder);
            return receipt != null && SameRepairFiles(receipt.FileHashes, OwnedTranslationFileHashes(folder));
        }

        // Capture on the main thread and save as soon as verification finishes,
        // before the larger report checkpoint. Ordinary injection cannot certify repair.
        internal static Action CaptureRepairCompletionWriter()
            => CaptureScopedRepairCompletionWriter(null);

        internal static Action CaptureScopedRepairCompletionWriter(IEnumerable<string> changedFiles)
        {
            var payload = completedRepairPayload;
            var files = changedFiles == null ? null : new HashSet<string>(changedFiles, StringComparer.OrdinalIgnoreCase);
            return () => {
                if (payload == null || payload.KeyedOnly || payload.RepairRequest == 0 || payload.FileHashes == null) return;
                bool Current() => payload.RepairRequest == repairMemoryRequest && payload.Epoch == GameDataEpoch &&
                    payload.CancellationGeneration == AutoTranslatorSettings.PipelineCancellationGeneration &&
                    ReferenceEquals(payload.Language, LanguageDatabase.activeLanguage) && AutoTranslatorMod.Settings != null &&
                    payload.TargetFolder == GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
                if (!Current()) throw new OperationCanceledException("Repair completion belongs to an obsolete request.");
                lock (baselineGate)
                {
                    var baseline = ReadRepairBaseline() ?? new RepairBaseline();
                    var packages = (payload.PackageId ?? "").Split(',');
                    bool Covered(string file) => files != null ? files.Contains(file) : !payload.PackageScoped ||
                        (payload.ChangedKeys != null ? payload.ChangedKeys.ContainsKey(file) : MainMenuRepairWork.IsInPackageScope(file, packages));
                    if (!baseline.Languages.TryGetValue(payload.TargetFolder, out var previous))
                        previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var hashes = previous.Where(p => File.Exists(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in payload.FileHashes.Where(p => Covered(p.Key))) hashes[pair.Key] = pair.Value;
                    baseline.Languages[payload.TargetFolder] = hashes;
                    SaveRepairBaseline(baseline, () => Current() && SameRepairFiles(payload.FileHashes, OwnedTranslationFileHashes(payload.TargetFolder)));
                }
            };
        }

        // This identity deliberately covers only ATC's own target-language pack.
        // The active mod list is excluded so installing/removing/updating a mod
        // cannot trigger a paid or full repair before the player translates it.
        internal static string OwnedTranslationPackSignature(TargetLanguage language)
        {
            string root = Path.Combine(GetLocalPackPath(), "Languages", GetFolderNameByLanguage(language));
            string schema = typeof(AutoTranslatorScanner).Assembly.ManifestModule.ModuleVersionId.ToString("D");
            return "owned-pack-v1:" + CurrentRimWorldVersion + ":" + language + ":" +
                TranslationRepairJournal.Hash(schema + "\n" + BuildXmlTreeFingerprint(root));
        }
    }
}
