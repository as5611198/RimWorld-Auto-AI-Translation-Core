using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        internal static bool MemoryDropBusy
        {
            get
            {
                lock (_pendingInjectLock) return _activeMemoryDropApply != null || _pendingMemoryDropPayload != null ||
                    _memoryDropPrepareRunning != 0 || _pendingMemoryDrop || _pendingKeyedMemoryDrop || _pendingMemoryDropPackageIds.Count > 0;
            }
        }

        internal static bool PackMaintenanceBusy => System.Threading.Volatile.Read(ref _packMaintenanceRunning) != 0;

        internal static string MemoryDropWaitState
        {
            get
            {
                lock (_pendingInjectLock)
                    return "apply=" + (_activeMemoryDropApply == null ? "idle" : _activeMemoryDropApply.Preservation == null ? "injecting" : "saving") +
                        ", prepared=" + (_pendingMemoryDropPayload != null) + ", preparing=" + (_memoryDropPrepareRunning != 0) +
                        ", full=" + _pendingMemoryDrop + ", keyed=" + _pendingKeyedMemoryDrop + ", packages=" + _pendingMemoryDropPackageIds.Count;
            }
        }

        internal sealed class MainMenuRepairWork
        {
            private const int FileBatchSize = 16;
            private sealed class FileData
            {
                public string Path, Type, Signature;
                public bool PreserveListStructure;
                public Exception Error;
                public Dictionary<string, string> Original;
                public List<KeyValuePair<string, string>> Candidates;
                public readonly HashSet<string> Recovered = new HashSet<string>(StringComparer.Ordinal);
                public readonly List<TranslationRepairRecord> Held = new List<TranslationRepairRecord>();
            }
            private sealed class FileWrite
            {
                public FileData File;
                public Dictionary<string, string> Output;
                public Exception Error;
            }
            private readonly string root;
            private readonly int epoch = GameDataEpoch;
            private readonly int cancellationGeneration = AutoTranslatorSettings.PipelineCancellationGeneration;
            private readonly LoadedLanguage language = LanguageDatabase.activeLanguage;
            private readonly TargetLanguage target = AutoTranslatorMod.Settings.TargetLang;
            private bool CanPublish => !cancelled && epoch == GameDataEpoch &&
                cancellationGeneration == AutoTranslatorSettings.PipelineCancellationGeneration &&
                ReferenceEquals(language, LanguageDatabase.activeLanguage) && AutoTranslatorMod.Settings.TargetLang == target;
            private readonly Task<string[]> discovery;
            private readonly TranslationRepairRun run;
            private readonly NativeTranslationRepairWork native;
            private readonly string[] packageScope;
            internal readonly CommittedChangeSet Changes;
            private string[] files;
            private Task<FileData[]> reading;
            private readonly Queue<FileData> readAhead = new Queue<FileData>();
            private int nextRead;
            private readonly Queue<FileWrite> pendingWrites = new Queue<FileWrite>();
            private Task<FileWrite[]> writing;
            private readonly Queue<FileWrite> completedWrites = new Queue<FileWrite>();
            internal Task PendingWrite => writing;
            private volatile bool cancelled;
            internal void Cancel() { cancelled = true; }
            private FileData current;
            private int entry, heldEntry;
            private readonly Dictionary<string, string> output = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> canonical = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly HashSet<string> selectedOutputKeys = new HashSet<string>(StringComparer.Ordinal);
            private int processedFiles;
            public int Processed => processedFiles + (native?.Processed ?? 0);
            public int Total => (files == null ? 0 : files.Length) + (native?.Total ?? 0);
            public bool Done { get; private set; }

            public MainMenuRepairWork(string languageRoot, TranslationRepairRun run)
                : this(languageRoot, run, true, null) { }

            internal MainMenuRepairWork(string languageRoot, TranslationRepairRun run,
                bool includeNative, string[] packageIds)
                : this(languageRoot, run, includeNative, packageIds, null) { }

            internal MainMenuRepairWork(string languageRoot, TranslationRepairRun run,
                bool includeNative, string[] packageIds, string[] changedFiles)
            {
                root = languageRoot;
                this.run = run;
                packageScope = packageIds;
                Changes = CaptureCommittedChanges(packageIds);
                native = includeNative ? new NativeTranslationRepairWork(LanguageDatabase.activeLanguage, run) : null;
                discovery = Task.Run(() => changedFiles != null ? changedFiles.Where(f => File.Exists(f) && f.Replace('\\', '/').IndexOf("/DefInjected/", StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(f => f, StringComparer.Ordinal).ToArray() : Changes != null ? Changes.Files.Keys.Where(f => f.Replace('\\', '/').IndexOf("/DefInjected/", StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(f => f, StringComparer.Ordinal).ToArray() : Directory.Exists(Path.Combine(root, "DefInjected"))
                    ? Directory.GetFiles(Path.Combine(root, "DefInjected"), "*.xml", SearchOption.AllDirectories)
                        .Where(f => IsInPackageScope(f, packageScope))
                        .OrderBy(f => f, StringComparer.Ordinal).ToArray()
                    : new string[0]);
            }

            internal static bool IsInPackageScope(string path, string[] packageIds)
            {
                if (packageIds == null) return true;
                string stem = Path.GetFileNameWithoutExtension(path ?? "") ?? "";
                return packageIds.Any(id => !string.IsNullOrWhiteSpace(id) &&
                    new[] { "_AutoTranslated", "_CloudCorrections", "_ManualTranslation" }.Any(suffix =>
                        stem.Equals(TranslationGeneratedOutputOwnership.GetCleanPackageId(id) + suffix, StringComparison.OrdinalIgnoreCase)));
            }

            // Unity/Verse traversal is confined to this method, called by the
            // main-thread pump. File enumeration and parsing run off-thread.
            public void Tick()
            {
                if (Done || cancelled) return;
                var timer = Stopwatch.StartNew();
                if (writing != null && writing.IsCompleted)
                {
                    foreach (var result in writing.GetAwaiter().GetResult()) completedWrites.Enqueue(result);
                    writing = null;
                }
                while (completedWrites.Count > 0 && TranslationFrameBudget.HasTime)
                {
                    var result = completedWrites.Dequeue();
                    if (result.Error != null) FileFailure(result.File.Path, result.Error);
                    processedFiles++;
                }
                if (completedWrites.Count > 0 || !TranslationFrameBudget.HasTime) return;
                StartWriting();
                if (files == null)
                {
                    if (!discovery.IsCompleted) return;
                    files = discovery.GetAwaiter().GetResult();
                }
                if (processedFiles >= files.Length)
                {
                    if (native == null) { Done = true; return; }
                    native.Tick(); Done = native.Done; return;
                }

                int budget = 2048;
                try
                {
                    // One time budget spans every file in this update. Completed
                    // IO does not cost another update per read/check/write stage.
                    while (budget > 0 && TranslationFrameBudget.HasTime)
                    {
                        if (reading != null && reading.IsCompleted)
                        {
                            foreach (var file in reading.GetAwaiter().GetResult()) if (file != null) readAhead.Enqueue(file);
                            reading = null;
                        }
                        StartReading();
                        if (current == null)
                        {
                            // At most one batch being written and one waiting;
                            // slow disks apply backpressure without blocking UI.
                            if (pendingWrites.Count >= FileBatchSize || readAhead.Count == 0) break;
                            current = readAhead.Dequeue();
                            if (current.Error != null)
                            {
                                FileFailure(current.Path, current.Error);
                                current = null; processedFiles++; budget--; continue;
                            }
                            entry = 0; heldEntry = 0; output.Clear(); canonical.Clear(); selectedOutputKeys.Clear();
                            if (Changes != null)
                            {
                                foreach (var pair in current.Original) output[pair.Key] = pair.Value;
                                var changed = Changes.Files[Path.GetFullPath(current.Path)];
                                current.Candidates = current.Candidates.Where(p => changed.Contains(p.Key)).ToList();
                                current.Held.RemoveAll(p => !changed.Contains(p.OriginalKey));
                            }
                        }
                        while (entry < current.Candidates.Count && budget-- > 0 && TranslationFrameBudget.HasTime)
                        {
                            var pair = current.Candidates[entry++];
                            // An earlier alias can already have selected this exact
                            // output key. Removing it here both loses that paid value
                            // and leaves canonical pointing at a missing dictionary
                            // entry when the real key is encountered later.
                            if (!selectedOutputKeys.Contains(pair.Key)) output.Remove(pair.Key);
                            var check = ValidateDefTranslation(current.Type, pair.Key);
                            string value = PrepareDefTranslationText(check, pair.Value);
                            if (check.Allowed && IsFailedNativeCandidate(TranslationRuntimeResolver.DefType(current.Type), check.Path, pair.Value, current.Path))
                                check = new DefTranslationPathResult { Path = check.Path, Reason = "native_injection_failed", Evidence = "unchanged candidate retained after prior native rejection" };
                            run.Observe(RecordDefRepair(current.Type, pair.Key, pair.Value, current.Path, check,
                                current.Recovered.Contains(pair.Key) ? "recovered" : "main-menu-check",
                                current.Recovered.Contains(pair.Key) && check.Allowed ? "已沿用修復" : null, value));
                            if (!check.Allowed) continue;
                            if (Changes != null) Changes.Files[Path.GetFullPath(current.Path)].Add(check.Path);
                            if (canonical.TryGetValue(check.ResolvedPath, out string previous))
                            {
                                run.Observe(RecordDefRepair(current.Type, pair.Key, pair.Value, current.Path, check, "main-menu-conflict",
                                    output[previous] == value ? "相同譯文合併" : "衝突", value));
                                if (output[previous] != value)
                                    run.Observe(RecordDefRepair(current.Type, previous, output[previous], current.Path, check, "main-menu-selected", "保留候選"));
                                continue;
                            }
                            canonical[check.ResolvedPath] = check.Path;
                            output[check.Path] = value;
                            selectedOutputKeys.Add(check.Path);
                        }
                        if (entry < current.Candidates.Count) break;
                        while (heldEntry < current.Held.Count && budget-- > 0 && TranslationFrameBudget.HasTime)
                        {
                            var held = current.Held[heldEntry++];
                            var check = ValidateDefTranslation(current.Type, held.OriginalKey);
                            // Revalidate retained alternatives, but never select one for
                            // the player or put it back into the writer's output.
                            run.Observe(RecordDefRepair(current.Type, held.OriginalKey, held.Translation, current.Path, check,
                                "main-menu-held", check.Allowed ? "衝突" : null));
                        }
                        if (heldEntry < current.Held.Count) break;
                        pendingWrites.Enqueue(new FileWrite { File = current, Output = new Dictionary<string, string>(output, StringComparer.Ordinal) });
                        current = null;
                        if (pendingWrites.Count == FileBatchSize) StartWriting();
                        budget--; // Empty XML files also consume the work budget.
                    }
                }
                finally { StartWriting(); }
            }

            private void StartReading()
            {
                if (reading != null || nextRead >= files.Length || readAhead.Count >= FileBatchSize) return;
                int start = nextRead, count = Math.Min(FileBatchSize, files.Length - nextRead);
                nextRead += count;
                reading = Task.Run(() => {
                    var batch = new FileData[count];
                    for (int i = 0; i < count && !cancelled; i++)
                    {
                        string path = files[start + i];
                        try { batch[i] = ReadFile(path); }
                        catch (Exception ex) { batch[i] = new FileData { Path = path, Error = ex }; }
                    }
                    return batch;
                });
            }

            private void StartWriting()
            {
                if (cancelled || writing != null || pendingWrites.Count == 0) return;
                var batch = pendingWrites.ToArray(); pendingWrites.Clear();
                writing = Task.Run(() => {
                    foreach (var item in batch)
                    {
                        if (cancelled) break;
                        try
                        {
                            var file = item.File;
                            if (BuildFileSignature(file.Path) != file.Signature) throw new IOException("Translation file changed during repair; original was not replaced.");
                            if (!file.PreserveListStructure && (file.Original.Count != item.Output.Count || file.Original.Any(p => !item.Output.TryGetValue(p.Key, out string value) || value != p.Value)))
                                // This writer preserves/checkpoints paid values
                                // before replacing each individual source file.
                                SaveValidatedXml(file.Path, item.Output, () => CanPublish && BuildFileSignature(file.Path) == file.Signature);
                        }
                        catch (Exception ex) { item.Error = ex; }
                    }
                    // Unchanged source files can share a checkpoint. Changed
                    // files already checkpointed before their atomic commit.
                    try { FlushSelfRepair(); }
                    catch (Exception ex) { foreach (var item in batch) if (item.Error == null) item.Error = ex; }
                    return batch;
                });
            }

            private static FileData ReadFile(string path)
            {
                string signature = BuildFileSignature(path);
                if (!TryLoadRawXmlFileToDict(path, out var data))
                {
                    PreserveRepairInput(path);
                    throw new InvalidDataException("XML could not be parsed; original bytes preserved.");
                }
                if (signature != BuildFileSignature(path)) throw new IOException("Translation file changed while reading.");
                string normalized = path.Replace('\\', '/');
                var file = new FileData { Path = path, Signature = signature,
                    Type = normalized.Substring(normalized.IndexOf("/DefInjected/", StringComparison.OrdinalIgnoreCase) + 13).Split('/')[0],
                    Original = data, Candidates = data.ToList(), PreserveListStructure = TranslationXmlDictionaryParser.ReadFullLists(path).Count != 0 };
                // Recheck preserved candidates only for this existing source.
                // Resolved history already represented in the XML is not an issue.
                foreach (var group in RepairJournal.ForSource(path).Where(r => r.OriginalKey != null && r.DefType != null &&
                    r.SourceKind != "native-full-list" && r.SourceKind != "native-duplicate-list" && r.SourceKind != "native-manual-list" && r.SourceKind != "native-config-list").GroupBy(r => r.OriginalKey))
                {
                    bool ambiguous = group.Select(r => r.Translation).Distinct(StringComparer.Ordinal).Count() != 1;
                    var held = group.Where(r => ambiguous || r.Status == "衝突")
                        .GroupBy(r => r.OriginalKey + "\n" + r.Translation).Select(g => g.First()).ToList();
                    if (held.Count > 0) { file.Held.AddRange(held); continue; }
                    if (group.Any(r => r.Status == "保留候選")) continue;
                    var record = group.First();
                    if (data.ContainsKey(record.NewKey ?? record.OriginalKey) || data.ContainsKey(record.OriginalKey)) continue;
                    file.Candidates.Add(new KeyValuePair<string, string>(record.OriginalKey, record.Translation));
                    file.Recovered.Add(record.OriginalKey);
                }
                return file;
            }

            private void FileFailure(string path, Exception ex)
            {
                var record = new TranslationRepairRecord { SourceFile = path, SourceKind = "main-menu-file",
                    Reason = "file_operation_failed", Status = "仍待處理", Evidence = ex.GetBaseException().Message };
                RepairJournal.Record(record);
                run.Observe(record);
                run.Verify(record, false, false);
                Log.Warning("[AutoTranslationCore] Repair retained original file: " + path + ": " + record.Evidence);
            }
        }

        internal static void VerifyRepairRecord(TranslationRepairRun run, TranslationRepairRecord record)
        {
            if (record.SourceKind == "third-party-diagnostic") { run.Verify(record, false, record.Status == "暫不適用"); return; }
            if (record.Reason == "config_error_regression" || record.Reason == "config_validation_unavailable" || record.Reason == "native_injection_failed")
            { run.Verify(record, false, record.Reason == "config_validation_unavailable"); return; }
            if (record.SourceKind == "main-menu-file") { run.Verify(record, false, false); return; }
            if (record.SourceKind == "native-general-error") { run.Verify(record, false, record.Reason == "missing_def_type"); return; }
            if (record.SourceKind == "third-party-read-overlay")
            {
                run.Verify(record, VerifyKnownRepairOverlay(record.SourceFile), false);
                return;
            }
            if (record.SourceKind == "native-duplicate-list")
            {
                var listType = TranslationRuntimeResolver.DefType(record.DefType);
                var listCheck = ValidateLoadedDefList(listType, record.NewKey ?? record.OriginalKey);
                var expected = record.Status == "相同譯文合併" ? Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(record.Translation) : null;
                run.Verify(record, listCheck.Allowed && expected != null && RuntimeListMatches(listType, listCheck, expected), listCheck.Temporary);
                return;
            }
            if (record.SourceKind == "native-manual-list")
            {
                var listType = TranslationRuntimeResolver.DefType(record.DefType);
                var listCheck = ValidateLoadedDefList(listType, record.NewKey ?? record.OriginalKey);
                var expected = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(record.Translation);
                run.Verify(record, listCheck.Allowed && expected != null && RuntimeListMatches(listType, listCheck, expected), listCheck.Temporary);
                return;
            }
            string type = record.DefType;
            string path = (record.SourceFile ?? "").Replace('\\', '/');
            int marker = path.IndexOf("/DefInjected/", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0) type = path.Substring(marker + 13).Split('/')[0];
            string key = record.NewKey ?? record.OriginalKey;
            string expectedValue = record.AppliedTranslation ?? record.Translation;
            if (type == "Keyed")
            {
                bool applied = LanguageDatabase.activeLanguage?.keyedReplacements != null &&
                    LanguageDatabase.activeLanguage.keyedReplacements.TryGetValue(key, out var replacement) && replacement.value == expectedValue;
                run.Verify(record, applied, false);
                return;
            }
            var check = ValidateDefTranslation(type, key);
            bool verified = check.Allowed && RuntimeTextMatches(type, check, expectedValue);
            run.Verify(record, verified, check.Temporary);
        }

        private static bool RuntimeTextMatches(string typeName, DefTranslationPathResult check, string value)
        {
            Type type = TranslationRuntimeResolver.DefType(typeName);
            var packages = LanguageDatabase.activeLanguage?.defInjections;
            if (type == null || packages == null) return false;
            var matching = packages.Where(p => p.defType == type).ToList();
            bool injected = matching.Any(p => p.injections.TryGetValue(check.Path, out var i) && i.injected && i.injection == value);
            if (!injected) injected = matching.SelectMany(p => p.injections.Values)
                .Any(i => i.injected && i.injection == value && ValidateLoadedDefTranslation(type, i.path).ResolvedPath == check.ResolvedPath);
            if (!injected) return false;
            var root = GenDefDatabase.GetDefSilentFail(type, check.ResolvedPath.Split('.')[0], false);
            return DefTranslationPathValidator.ReadValidatedText(root, check.ResolvedPath) == value;
        }

        private static bool VerifyKnownRepairOverlay(string file)
        {
            if (!File.Exists(file) || !KnownTranslationXmlRepair.TryRepair(file, File.ReadAllText(file), out string xml, out _)) return false;
            var document = new System.Xml.XmlDocument { XmlResolver = null }; document.LoadXml(xml);
            HashSet<string> changedKeys = null;
            try
            {
                var original = new System.Xml.XmlDocument { XmlResolver = null }; original.Load(file);
                var before = original.DocumentElement.ChildNodes.OfType<System.Xml.XmlElement>().GroupBy(n => n.Name)
                    .ToDictionary(g => g.Key, g => g.Select(n => n.OuterXml).OrderBy(v => v, StringComparer.Ordinal).ToArray());
                changedKeys = new HashSet<string>(document.DocumentElement.ChildNodes.OfType<System.Xml.XmlElement>()
                    .GroupBy(n => n.Name).Where(g => !before.TryGetValue(g.Key, out var old) ||
                        !old.SequenceEqual(g.Select(n => n.OuterXml).OrderBy(v => v, StringComparer.Ordinal), StringComparer.Ordinal))
                    .Select(g => g.Key), StringComparer.Ordinal);
            }
            catch (System.Xml.XmlException) { } // A malformed source needs the entire repaired document verified.
            string type = file.Replace('\\', '/').Split(new[] { "/DefInjected/" }, StringSplitOptions.None).Last().Split('/')[0];
            bool any = false;
            foreach (System.Xml.XmlNode node in document.DocumentElement.ChildNodes)
            {
                if (node.NodeType != System.Xml.XmlNodeType.Element) continue;
                if (changedKeys != null && !changedKeys.Contains(node.Name)) continue;
                var elements = node.ChildNodes.OfType<System.Xml.XmlElement>().ToList();
                if (elements.Count > 0)
                {
                    if (elements.Any(e => e.Name != "li")) return false;
                    var listType = TranslationRuntimeResolver.DefType(type);
                    var check = ValidateLoadedDefList(listType, node.Name);
                    if (!check.Allowed || !RuntimeListMatches(listType, check, elements.Select(e => e.InnerText.Replace("\\n", "\n")).ToList())) return false;
                }
                else
                {
                    var check = ValidateDefTranslation(type, node.Name);
                    if (!check.Allowed || !RuntimeTextMatches(type, check, node.InnerText.Replace("\\n", "\n"))) return false;
                }
                any = true;
            }
            return any;
        }

        private static bool RuntimeListMatches(Type type, DefTranslationPathResult check, List<string> expected)
        {
            var packages = LanguageDatabase.activeLanguage?.defInjections;
            if (type == null || packages == null) return false;
            bool injected = packages.Where(p => p.defType == type).SelectMany(p => p.injections.Values).Any(i =>
                i.injected && i.IsFullListInjection && i.fullListInjection.SequenceEqual(expected, StringComparer.Ordinal) &&
                ValidateLoadedDefList(type, i.path).ResolvedPath == check.ResolvedPath);
            if (!injected) return false;
            var root = GenDefDatabase.GetDefSilentFail(type, check.ResolvedPath.Split('.')[0], false);
            var value = DefTranslationPathValidator.ReadValidatedValue(root, check.ResolvedPath) as List<string>;
            return value != null && value.SequenceEqual(expected, StringComparer.Ordinal);
        }
    }
}
