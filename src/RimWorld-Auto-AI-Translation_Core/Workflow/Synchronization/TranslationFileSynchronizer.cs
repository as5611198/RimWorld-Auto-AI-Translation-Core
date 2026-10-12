using AutoTranslator_Core.TargetedHardcodedUi;
using AutoTranslator_Core.Workflow.Output;
using AutoTranslator_Core.Workflow.Persistence;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Xml.Linq;

namespace AutoTranslator_Core.Workflow.Synchronization
{
    public sealed class ObservedTranslationEntry
    {
        public string CandidateId { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string EntryKey { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string TextHash { get; set; } = string.Empty;
    }

    public enum TranslationSynchronizationMutationKind : byte
    {
        Save = 1,
        Clear = 2,
        ReadError = 3,
        Invalid = 4
    }

    public sealed class TranslationSynchronizationMutation
    {
        public TranslationSynchronizationMutationKind Kind { get; set; }
        public string CandidateId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public TranslationOrigin Origin { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public string EntryKey { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }

    public sealed class TranslationFileIndexResult
    {
        public Dictionary<string, ObservedTranslationEntry> Entries { get; } =
            new Dictionary<string, ObservedTranslationEntry>(StringComparer.Ordinal);
        public List<string> Diagnostics { get; } = new List<string>();
        public HashSet<string> FailedRelativePaths { get; } =
            new HashSet<string>(WorkflowPath.Comparer);
        public HashSet<string> ChangedRelativePaths { get; } =
            new HashSet<string>(WorkflowPath.Comparer);
        public int IndexedFiles { get; set; }
        public List<TranslationFileCacheRecord> FileCache { get; } = new List<TranslationFileCacheRecord>();

        public static string CreateKey(string relativePath, string entryKey)
        {
            return (relativePath ?? string.Empty).Replace('\\', '/') + "\n" + (entryKey ?? string.Empty);
        }
    }

    public sealed class TranslationFileCacheRecord
    {
        public string RelativePath { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public string EntriesJson { get; set; } = string.Empty;
        public long FileLength { get; set; } = -1;
        public DateTime? LastWriteUtc { get; set; }
    }

    internal sealed class TranslationFileIndex
    {
        private readonly string _packRoot;
        private readonly WorkflowRepository _repository;

        public TranslationFileIndex(string generatedPackRoot, WorkflowRepository repository)
        {
            _packRoot = Path.GetFullPath(generatedPackRoot ?? throw new ArgumentNullException(nameof(generatedPackRoot)));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public string PackRoot => _packRoot;

        public TranslationFileIndexResult Build(string targetLanguage, CancellationToken cancellationToken)
        {
            TranslationFileIndexResult result = new TranslationFileIndexResult();
            Dictionary<string, TranslationFileCacheRecord> previous =
                _repository.GetTranslationFileCache(targetLanguage);
            List<TranslationFileCacheRecord> current = new List<TranslationFileCacheRecord>();
            string languageRoot = Path.Combine(_packRoot, "Languages", targetLanguage ?? string.Empty);
            List<string> xmlFiles = Directory.Exists(languageRoot)
                ? Directory.EnumerateFiles(languageRoot, "*.xml", SearchOption.AllDirectories)
                    .OrderBy(path => path, WorkflowPath.Comparer).ToList()
                : new List<string>();
            string manifestPath = Path.Combine(_packRoot, "HardcodedUiPatchPrototype.json");
            bool hasManifest = File.Exists(manifestPath);
            int totalFiles = xmlFiles.Count + (hasManifest ? 1 : 0);
            int completedFiles = 0;
            WorkflowTaskCoordinator.Instance.ReportStage(
                string.Empty,
                "扫描本地译文文件 0/" + totalFiles,
                "⏳ 译文状态同步：扫描本地译文文件开始（共 " + totalFiles + " 个）",
                "workflow.synchronization file-index start files=" + totalFiles +
                " cached=" + previous.Count);
            if (Directory.Exists(languageRoot))
            {
                foreach (string file in xmlFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (ReadFileWithCache(file, previous, current, result, false))
                            result.ChangedRelativePaths.Add(Relative(file));
                    }
                    catch (Exception ex) { PreserveFailedFile(file, previous, current, result, ex); }
                    completedFiles++;
                    WorkflowTaskCoordinator.Instance.ReportProgress(
                        completedFiles, totalFiles, string.Empty,
                        "扫描本地译文文件 " + completedFiles + "/" + totalFiles,
                        subCompletedUnits: completedFiles,
                        subTotalUnits: totalFiles,
                        writeRuntimeLog: false);
                }
            }
            if (hasManifest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (ReadFileWithCache(manifestPath, previous, current, result, true, targetLanguage))
                        result.ChangedRelativePaths.Add(Relative(manifestPath));
                }
                catch (Exception ex) { PreserveFailedFile(manifestPath, previous, current, result, ex); }
                completedFiles++;
                WorkflowTaskCoordinator.Instance.ReportProgress(
                    completedFiles, totalFiles, string.Empty,
                    "扫描本地译文文件 " + completedFiles + "/" + totalFiles,
                    subCompletedUnits: completedFiles,
                    subTotalUnits: totalFiles,
                    writeRuntimeLog: false);
            }
            HashSet<string> currentPaths = new HashSet<string>(
                current.Select(record => record.RelativePath), WorkflowPath.Comparer);
            foreach (string previousPath in previous.Keys)
            {
                if (!currentPaths.Contains(previousPath))
                    result.ChangedRelativePaths.Add(previousPath);
            }
            cancellationToken.ThrowIfCancellationRequested();
            result.IndexedFiles = current.Count;
            // This is an uncommitted snapshot. Publish it only together with the
            // corresponding translation mutations and completed synchronization.
            result.FileCache.AddRange(current);
            WorkflowTaskCoordinator.Instance.ReportStage(
                string.Empty,
                "本地译文文件索引完成",
                "✓ 译文状态同步：本地译文文件索引完成（文件 " + current.Count +
                " 个，变化 " + result.ChangedRelativePaths.Count + " 个，条目 " +
                result.Entries.Count + " 条）",
                "workflow.synchronization file-index complete files=" + current.Count +
                " changed=" + result.ChangedRelativePaths.Count +
                " entries=" + result.Entries.Count + " failed=" + result.FailedRelativePaths.Count);
            return result;
        }

        private void PreserveFailedFile(
            string file,
            IDictionary<string, TranslationFileCacheRecord> previous,
            ICollection<TranslationFileCacheRecord> current,
            TranslationFileIndexResult result,
            Exception error)
        {
            string relative = Relative(file);
            result.ChangedRelativePaths.Add(relative);
            result.FailedRelativePaths.Add(relative);
            result.Diagnostics.Add(relative + ": " + (error?.Message ?? "Unable to read translation file."));
            if (!previous.TryGetValue(relative, out TranslationFileCacheRecord cached)) return;
            List<ObservedTranslationEntry> entries;
            try
            {
                entries = JsonConvert.DeserializeObject<List<ObservedTranslationEntry>>(cached.EntriesJson) ??
                          new List<ObservedTranslationEntry>();
            }
            catch
            {
                return;
            }
            foreach (ObservedTranslationEntry entry in entries)
                result.Entries[TranslationFileIndexResult.CreateKey(entry.RelativePath, entry.EntryKey)] = entry;
            current.Add(cached);
            if (Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                SeedRuntimeXmlCache(file, entries);
        }

        private bool ReadFileWithCache(
            string file,
            IDictionary<string, TranslationFileCacheRecord> previous,
            ICollection<TranslationFileCacheRecord> current,
            TranslationFileIndexResult result,
            bool dllManifest,
            string targetLanguage = null)
        {
            string relative = Relative(file);
            FileInfo fileInfo = new FileInfo(file);
            List<ObservedTranslationEntry> entries;
            bool contentChanged = true;
            if (previous.TryGetValue(relative, out TranslationFileCacheRecord cached) &&
                cached.FileLength == fileInfo.Length &&
                cached.LastWriteUtc.HasValue &&
                cached.LastWriteUtc.Value == fileInfo.LastWriteTimeUtc)
            {
                entries = JsonConvert.DeserializeObject<List<ObservedTranslationEntry>>(cached.EntriesJson) ??
                          new List<ObservedTranslationEntry>();
                current.Add(cached);
                contentChanged = false;
            }
            else
            {
                byte[] bytes = File.ReadAllBytes(file);
                string hash = Hash(bytes);
                if (cached != null && string.Equals(cached.ContentHash, hash, StringComparison.Ordinal))
                {
                    entries = JsonConvert.DeserializeObject<List<ObservedTranslationEntry>>(cached.EntriesJson) ??
                              new List<ObservedTranslationEntry>();
                    contentChanged = false;
                }
                else
                {
                    entries = dllManifest
                        ? ReadDllManifest(bytes, relative, targetLanguage)
                        : ReadXml(bytes, relative);
                }
                current.Add(new TranslationFileCacheRecord
                {
                    RelativePath = relative,
                    ContentHash = hash,
                    EntriesJson = JsonConvert.SerializeObject(entries),
                    FileLength = fileInfo.Length,
                    LastWriteUtc = fileInfo.LastWriteTimeUtc
                });
            }
            foreach (ObservedTranslationEntry entry in entries)
                result.Entries[TranslationFileIndexResult.CreateKey(entry.RelativePath, entry.EntryKey)] = entry;
            if (!dllManifest) SeedRuntimeXmlCache(file, entries);
            return contentChanged;
        }

        private static void SeedRuntimeXmlCache(
            string file,
            IEnumerable<ObservedTranslationEntry> entries)
        {
            AutoTranslatorScanner.SeedWorkflowTranslationXmlCache(
                file,
                (entries ?? Enumerable.Empty<ObservedTranslationEntry>())
                .Select(entry => new KeyValuePair<string, string>(
                    entry.EntryKey ?? string.Empty,
                    entry.Text ?? string.Empty)));
        }

        private static List<ObservedTranslationEntry> ReadXml(byte[] bytes, string relative)
        {
            List<ObservedTranslationEntry> entries = new List<ObservedTranslationEntry>();
            XDocument document;
            using (MemoryStream stream = new MemoryStream(bytes, false))
                document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            if (document.Root == null) return entries;
            foreach (XElement element in document.Root.Elements().Where(item => !item.HasElements))
            {
                ObservedTranslationEntry observed = new ObservedTranslationEntry
                {
                    RelativePath = relative,
                    EntryKey = element.Name.LocalName,
                    Text = element.Value,
                    TextHash = WorkflowIdentity.HashText(element.Value)
                };
                entries.Add(observed);
            }
            return entries;
        }

        private static List<ObservedTranslationEntry> ReadDllManifest(
            byte[] bytes,
            string relative,
            string targetLanguage)
        {
            List<ObservedTranslationEntry> entries = new List<ObservedTranslationEntry>();
            string json = System.Text.Encoding.UTF8.GetString(bytes);
            HardcodedUiPatchManifest manifest = JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(json);
            foreach (HardcodedUiPatchEntry entry in manifest?.Entries ?? new List<HardcodedUiPatchEntry>())
            {
                if (entry?.Translations == null || !entry.Translations.TryGetValue(targetLanguage ?? string.Empty, out string text))
                    continue;
                entries.Add(new ObservedTranslationEntry
                {
                    RelativePath = relative,
                    EntryKey = entry.EntryId,
                    Text = text ?? string.Empty,
                    TextHash = WorkflowIdentity.HashText(text)
                });
            }
            return entries;
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private string Relative(string file)
        {
            string full = Path.GetFullPath(file);
            string prefix = _packRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, WorkflowPath.Comparison)
                ? full.Substring(prefix.Length).Replace('\\', '/')
                : Path.GetFileName(full);
        }
    }

    public sealed class TranslationSynchronizationResult
    {
        public long CompletedGeneration { get; set; }
        public int IndexedEntries { get; set; }
        public int IndexedFiles { get; set; }
        public int ModCount { get; set; }
        public int ConsistentEntries { get; set; }
        public int ImportedBaselineEntries { get; set; }
        public int ManualChanges { get; set; }
        public int ClearedMissingEntries { get; set; }
        public int RemovedManagedOutputEntries { get; set; }
        public int InvalidEntries { get; set; }
        public int ReadErrorEntries { get; set; }
        public int RecoveredPendingOperations { get; set; }
        public int FailedPendingOperations { get; set; }
        public long DurationMilliseconds { get; set; }
        public List<string> Diagnostics { get; set; } = new List<string>();
    }

    internal sealed class TranslationFileSynchronizer
    {
        private readonly WorkflowRepository _repository;
        private readonly TranslationFileIndex _index;
        private readonly ITranslationOutputStore _outputStore;

        public TranslationFileSynchronizer(
            WorkflowRepository repository,
            TranslationFileIndex index,
            ITranslationOutputStore outputStore)
        {
            _repository = repository;
            _index = index;
            _outputStore = outputStore;
        }

        public TranslationSynchronizationResult Synchronize(
            CancellationToken cancellationToken,
            string targetLanguage = null)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(targetLanguage))
                targetLanguage = WorkflowRuntimeSettings.GetTargetLanguageFolder();
            AutoTranslatorSettings.AddDebugLog(
                "workflow.synchronization start targetLanguage=" + (targetLanguage ?? string.Empty));
            TranslationFileIndexResult index = _index.Build(targetLanguage, cancellationToken);
            TranslationSynchronizationResult result = new TranslationSynchronizationResult
            {
                IndexedEntries = index.Entries.Count,
                IndexedFiles = index.IndexedFiles,
                Diagnostics = index.Diagnostics
            };

            WorkflowTaskCoordinator.Instance.ReportStage(
                string.Empty,
                "恢复未完成的译文文件操作",
                "⏳ 译文状态同步：恢复未完成的文件操作",
                "workflow.synchronization pending-operations start");
            RecoverPendingOperations(targetLanguage, index, result, cancellationToken);
            WorkflowTaskCoordinator.Instance.ReportStage(
                string.Empty,
                "读取上次同步基线",
                "⏳ 译文状态同步：读取上次同步基线",
                "workflow.synchronization observed-hashes start");
            List<TranslationSynchronizationMutation> mutations =
                new List<TranslationSynchronizationMutation>();
            Dictionary<string, string> previousObservedHashes =
                _repository.GetObservedTranslationHashes(targetLanguage);

            WorkflowTaskCoordinator.Instance.ReportStage(
                string.Empty,
                "统计需要同步的数据库条目",
                "⏳ 译文状态同步：统计需要同步的数据库条目",
                "workflow.synchronization candidate-count start");
            int totalCandidates =
                _repository.CountCandidatesWithTranslationBindings(
                    targetLanguage, index.ChangedRelativePaths);
            WorkflowTaskCoordinator.Instance.ReportStage(
                string.Empty,
                "数据库条目统计完成，共 " + totalCandidates + " 条",
                "✓ 译文状态同步：数据库条目统计完成（共 " + totalCandidates + " 条）",
                "workflow.synchronization candidate-count complete count=" + totalCandidates);

            const int candidatePageSize = 1000;
            HashSet<string> synchronizedMods = new HashSet<string>(StringComparer.Ordinal);
            int synchronizedCandidates = 0;
            string afterCandidateId = string.Empty;
            while (synchronizedCandidates < totalCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WorkflowTaskCoordinator.Instance.ReportStage(
                    string.Empty,
                    "分页读取数据库条目 " + synchronizedCandidates + "/" + totalCandidates,
                    string.Empty,
                    "workflow.synchronization candidate-page start after=" +
                    (string.IsNullOrEmpty(afterCandidateId) ? "<first>" : afterCandidateId) +
                    " completed=" + synchronizedCandidates + " total=" + totalCandidates);
                List<CandidateRecord> candidates =
                    _repository.GetCandidatesWithTranslationBindingsPage(
                        targetLanguage, afterCandidateId, candidatePageSize,
                        index.ChangedRelativePaths);
                if (candidates.Count == 0)
                {
                    result.Diagnostics.Add(
                        "Synchronization candidate paging ended before the counted total. " +
                        "Processed " + synchronizedCandidates + " of " + totalCandidates + ".");
                    break;
                }
                foreach (CandidateRecord candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    synchronizedMods.Add(candidate.ModIdentity);
                    WorkflowTaskCoordinator.Instance.ReportProgress(
                        synchronizedCandidates, totalCandidates,
                        candidate.PackageId,
                        "核对数据库译文条目 " + synchronizedCandidates + "/" + totalCandidates +
                        " · " + candidate.SourceFileRelativePath,
                        subCompletedUnits: synchronizedCandidates,
                        subTotalUnits: totalCandidates);
                    synchronizedCandidates++;
                    TranslationWriteTarget target;
                    try
                    {
                        target = string.IsNullOrWhiteSpace(candidate.TranslationFileRelativePath)
                            ? _outputStore.Resolve(candidate, targetLanguage)
                            : new TranslationWriteTarget
                            {
                                RelativePath = candidate.TranslationFileRelativePath,
                                EntryKey = candidate.TranslationEntryKey
                            };
                    }
                    catch (Exception ex)
                    {
                        result.Diagnostics.Add(candidate.CandidateId + ": " + ex.Message);
                        continue;
                    }

                    string key = TranslationFileIndexResult.CreateKey(target.RelativePath, target.EntryKey);
                    if (index.FailedRelativePaths.Contains(target.RelativePath))
                    {
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.synchronization preserve-read-error candidate=" + candidate.CandidateId +
                        " file=" + target.RelativePath);
                    mutations.Add(new TranslationSynchronizationMutation
                    {
                        Kind = TranslationSynchronizationMutationKind.ReadError,
                        CandidateId = candidate.CandidateId,
                        Error = index.Diagnostics.FirstOrDefault(
                            diagnostic => diagnostic.StartsWith(
                                target.RelativePath + ":", WorkflowPath.Comparison)) ??
                                "Translation file could not be read."
                    });
                    result.ReadErrorEntries++;
                        continue;
                    }
                    if (string.Equals(
                        candidate.LastSyncStatus, "SourceChanged", StringComparison.OrdinalIgnoreCase) &&
                    candidate.TranslationState == CandidateTranslationState.Translated)
                {
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.synchronization mirror-source-change candidate=" + candidate.CandidateId +
                        " origin=" + candidate.TranslationOrigin +
                        " dbHash=" + ShortHash(candidate.TranslationHash));
                    WriteManagedTranslation(candidate, targetLanguage, target);
                    index.Entries[key] = CreateObserved(candidate, target);
                    continue;
                }
                if (string.Equals(
                        candidate.LastSyncStatus, "SourceMissing", StringComparison.OrdinalIgnoreCase))
                {
                    if (index.Entries.ContainsKey(key))
                        RemoveStaleManagedTranslation(
                            candidate, targetLanguage, target, index, key, result);
                    mutations.Add(new TranslationSynchronizationMutation
                    {
                        Kind = TranslationSynchronizationMutationKind.Clear,
                        CandidateId = candidate.CandidateId
                    });
                    result.ClearedMissingEntries++;
                    continue;
                }
                if (!candidate.IsPresent)
                {
                    if (index.Entries.ContainsKey(key))
                        RemoveStaleManagedTranslation(
                            candidate, targetLanguage, target, index, key, result);
                    mutations.Add(new TranslationSynchronizationMutation
                    {
                        Kind = TranslationSynchronizationMutationKind.Clear,
                        CandidateId = candidate.CandidateId
                    });
                    result.ClearedMissingEntries++;
                    continue;
                }
                if (!index.Entries.TryGetValue(key, out ObservedTranslationEntry observed))
                {
                    if (!string.IsNullOrEmpty(candidate.SourceTextHashAtTranslation) &&
                        !string.Equals(
                            candidate.SourceTextHashAtTranslation,
                            candidate.SourceTextHash,
                            StringComparison.Ordinal))
                    {
                        mutations.Add(new TranslationSynchronizationMutation
                        {
                            Kind = TranslationSynchronizationMutationKind.Clear,
                            CandidateId = candidate.CandidateId
                        });
                        result.ClearedMissingEntries++;
                        continue;
                    }
                    if (candidate.TranslationState == CandidateTranslationState.Translated)
                    {
                        if (candidate.TranslationOrigin == TranslationOrigin.ModNative ||
                            candidate.TranslationOrigin == TranslationOrigin.ThirdParty ||
                            candidate.TranslationOrigin == TranslationOrigin.Manual)
                        {
                            AutoTranslatorSettings.AddDebugLog(
                                "workflow.synchronization mirror-missing candidate=" + candidate.CandidateId +
                                " origin=" + candidate.TranslationOrigin);
                            WriteManagedTranslation(candidate, targetLanguage, target);
                            observed = CreateObserved(candidate, target);
                            index.Entries[key] = observed;
                        }
                        else
                        {
                            AutoTranslatorSettings.AddDebugLog(
                                "workflow.synchronization clear-missing candidate=" + candidate.CandidateId +
                                " origin=" + candidate.TranslationOrigin);
                            mutations.Add(new TranslationSynchronizationMutation
                            {
                                Kind = TranslationSynchronizationMutationKind.Clear,
                                CandidateId = candidate.CandidateId
                            });
                            result.ClearedMissingEntries++;
                        }
                    }
                    continue;
                }
                observed.CandidateId = candidate.CandidateId;

                bool hadBaseline = previousObservedHashes.TryGetValue(key, out string previousObservedHash);
                bool changedAfterBaseline = hadBaseline &&
                    !string.Equals(previousObservedHash, observed.TextHash, StringComparison.Ordinal);
                TranslationOrigin observedOrigin = ResolveObservedOrigin(
                    candidate, observed, targetLanguage, changedAfterBaseline);
                if (candidate.TranslationState == CandidateTranslationState.Translated &&
                    !string.Equals(candidate.TranslationHash, observed.TextHash, StringComparison.Ordinal) &&
                    (int)candidate.TranslationOrigin > (int)observedOrigin)
                {
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.synchronization restore-priority candidate=" + candidate.CandidateId +
                        " databaseOrigin=" + candidate.TranslationOrigin +
                        " observedOrigin=" + observedOrigin +
                        " dbHash=" + ShortHash(candidate.TranslationHash) +
                        " fileHash=" + ShortHash(observed.TextHash));
                    WriteManagedTranslation(candidate, targetLanguage, target);
                    observed = CreateObserved(candidate, target);
                    index.Entries[key] = observed;
                    continue;
                }
                if (!AutoTranslatorScanner.TryAcceptTranslatedValue(
                        observed.Text, candidate.SourceText,
                        out string ignoredSanitized, out string validationReason, out string validationDetail))
                {
                    mutations.Add(new TranslationSynchronizationMutation
                    {
                        Kind = TranslationSynchronizationMutationKind.Invalid,
                        CandidateId = candidate.CandidateId,
                        Text = observed.Text,
                        Origin = observedOrigin,
                        RelativePath = observed.RelativePath,
                        EntryKey = observed.EntryKey,
                        Error = string.IsNullOrWhiteSpace(validationDetail)
                            ? validationReason
                            : validationDetail
                    });
                    result.Diagnostics.Add(
                        candidate.CandidateId + ": invalid translation: " + validationReason);
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.synchronization invalid candidate=" + candidate.CandidateId +
                        " file=" + observed.RelativePath + " key=" + observed.EntryKey +
                        " dbHash=" + ShortHash(candidate.TranslationHash) +
                        " fileHash=" + ShortHash(observed.TextHash) +
                        " reason=" + validationReason);
                    result.InvalidEntries++;
                    continue;
                }
                if (candidate.TranslationState != CandidateTranslationState.Translated)
                {
                    if (candidate.TranslationState == CandidateTranslationState.Translating)
                        continue;
                    if (!string.IsNullOrEmpty(candidate.SourceTextHashAtTranslation) &&
                        !string.Equals(candidate.SourceTextHashAtTranslation, candidate.SourceTextHash, StringComparison.Ordinal))
                    {
                        RemoveStaleManagedTranslation(candidate, targetLanguage, target, index, key, result);
                        mutations.Add(new TranslationSynchronizationMutation
                        {
                            Kind = TranslationSynchronizationMutationKind.Clear,
                            CandidateId = candidate.CandidateId
                        });
                        result.ClearedMissingEntries++;
                        continue;
                    }
                    mutations.Add(CreateSaveMutation(candidate, observed, observedOrigin));
                    if (observedOrigin == TranslationOrigin.Manual) result.ManualChanges++;
                    else result.ImportedBaselineEntries++;
                }
                else if (!string.Equals(candidate.TranslationHash, observed.TextHash, StringComparison.Ordinal))
                {
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.synchronization accept-file candidate=" + candidate.CandidateId +
                        " databaseOrigin=" + candidate.TranslationOrigin + " observedOrigin=" + observedOrigin +
                        " dbHash=" + ShortHash(candidate.TranslationHash) +
                        " fileHash=" + ShortHash(observed.TextHash));
                    mutations.Add(CreateSaveMutation(candidate, observed, observedOrigin));
                    if (observedOrigin == TranslationOrigin.Manual) result.ManualChanges++;
                }
                else if (!string.Equals(candidate.LastSyncStatus, "Synced", StringComparison.OrdinalIgnoreCase) ||
                         !string.IsNullOrWhiteSpace(candidate.LastSyncError))
                {
                    mutations.Add(CreateSaveMutation(candidate, observed, candidate.TranslationOrigin));
                }
                    else
                    {
                        result.ConsistentEntries++;
                    }
                }
                afterCandidateId = candidates[candidates.Count - 1].CandidateId;
                WorkflowTaskCoordinator.Instance.ReportProgress(
                    synchronizedCandidates, totalCandidates, string.Empty,
                    "核对数据库译文条目 " + synchronizedCandidates + "/" + totalCandidates,
                    subCompletedUnits: synchronizedCandidates,
                    subTotalUnits: totalCandidates,
                    writeRuntimeLog: false);
                AutoTranslatorSettings.AddDebugLog(
                    "workflow.synchronization candidate-page complete rows=" + candidates.Count +
                    " completed=" + synchronizedCandidates + " total=" + totalCandidates);
            }
            result.ModCount = synchronizedMods.Count;

            WorkflowTaskCoordinator.Instance.ReportStage(
                string.Empty,
                "发布同步状态",
                "⏳ 译文状态同步：发布同步状态",
                "workflow.synchronization publish start mutations=" + mutations.Count +
                " observed=" + index.Entries.Count);

            cancellationToken.ThrowIfCancellationRequested();
            result.CompletedGeneration = _repository.CommitSynchronization(
                targetLanguage,
                mutations,
                index.Entries.Values.ToList(),
                index.FileCache,
                generation =>
                {
                    result.CompletedGeneration = generation;
                    stopwatch.Stop();
                    result.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
                    return JsonConvert.SerializeObject(result);
                },
                (completed, total, detail) =>
                {
                    WorkflowTaskCoordinator.Instance.ReportProgress(
                        completed, total, string.Empty,
                        detail + " " + completed + "/" + total,
                        subCompletedUnits: completed,
                        subTotalUnits: total,
                        writeRuntimeLog: false);
                }, cancellationToken);
            WorkflowTaskCoordinator.Instance.ReportStage(
                string.Empty,
                "译文状态同步完成",
                "✓ 译文状态同步完成（文件 " + result.IndexedFiles +
                " 个，条目 " + result.IndexedEntries + " 条，候选 " +
                synchronizedCandidates + " 条）",
                "workflow.synchronization publish complete generation=" +
                result.CompletedGeneration);
            AutoTranslatorSettings.AddDebugLog(
                "workflow.synchronization complete indexed=" + result.IndexedEntries +
                " baseline=" + result.ImportedBaselineEntries + " manual=" + result.ManualChanges +
                " cleared=" + result.ClearedMissingEntries + " diagnostics=" + result.Diagnostics.Count +
                " generation=" + result.CompletedGeneration +
                " durationMs=" + result.DurationMilliseconds);
            return result;
        }

        private void RemoveStaleManagedTranslation(
            CandidateRecord candidate,
            string targetLanguage,
            TranslationWriteTarget target,
            TranslationFileIndexResult index,
            string indexKey,
            TranslationSynchronizationResult result)
        {
            if (string.IsNullOrWhiteSpace(candidate.TranslationFileRelativePath)) return;
            Guid operationId = _repository.CreatePendingFileOperation(
                candidate.CandidateId, targetLanguage, 2, TranslationOrigin.None,
                target.RelativePath, target.EntryKey, string.Empty);
            try
            {
                _outputStore.Delete(candidate, targetLanguage, target);
                index.Entries.Remove(indexKey);
                _repository.CompletePendingDeletion(operationId, candidate.CandidateId, targetLanguage);
                result.RemovedManagedOutputEntries++;
                AutoTranslatorSettings.AddDebugLog(
                    "workflow.synchronization removed-stale-source candidate=" + candidate.CandidateId);
            }
            catch (Exception ex)
            {
                _repository.FailPendingFileOperation(operationId, ex);
                result.Diagnostics.Add(candidate.CandidateId + ": " + ex.Message);
                throw;
            }
        }

        private static TranslationSynchronizationMutation CreateSaveMutation(
            CandidateRecord candidate,
            ObservedTranslationEntry observed,
            TranslationOrigin origin)
        {
            return new TranslationSynchronizationMutation
            {
                Kind = TranslationSynchronizationMutationKind.Save,
                CandidateId = candidate.CandidateId,
                Text = observed.Text,
                Origin = origin,
                RelativePath = observed.RelativePath,
                EntryKey = observed.EntryKey
            };
        }

        private static ObservedTranslationEntry CreateObserved(
            CandidateRecord candidate,
            TranslationWriteTarget target)
        {
            return new ObservedTranslationEntry
            {
                CandidateId = candidate.CandidateId,
                RelativePath = target.RelativePath,
                EntryKey = target.EntryKey,
                Text = candidate.TranslationText,
                TextHash = WorkflowIdentity.HashText(candidate.TranslationText)
            };
        }

        private static string ShortHash(string value)
        {
            value = value ?? string.Empty;
            return value.Length <= 12 ? value : value.Substring(0, 12);
        }

        internal int WriteChangedSourceTranslations(string targetLanguage, CancellationToken cancellationToken)
        {
            int written = 0;
            string after = string.Empty;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<CandidateRecord> page = _repository.GetCurrentManagedTranslationsPage(
                    targetLanguage, after, 500, changedSourcesOnly: true);
                if (page.Count == 0) return written;
                List<TranslationOutputWrite> writes = page.Select(candidate => new TranslationOutputWrite
                {
                    Candidate = candidate,
                    TranslatedText = candidate.TranslationText,
                    Origin = candidate.TranslationOrigin,
                    Target = new TranslationWriteTarget
                    {
                        RelativePath = candidate.TranslationFileRelativePath,
                        EntryKey = candidate.TranslationEntryKey
                    }
                }).ToList();
                List<PendingFileOperationRecord> pending = writes.Select(write => new PendingFileOperationRecord
                {
                    CandidateId = write.Candidate.CandidateId,
                    TargetLanguage = targetLanguage,
                    OperationKind = 1,
                    TranslationOrigin = write.Origin,
                    RelativePath = write.Target.RelativePath,
                    EntryKey = write.Target.EntryKey,
                    DesiredText = write.TranslatedText
                }).ToList();
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _repository.CreatePendingFileOperations(pending);
                    _outputStore.WriteBatch(writes, targetLanguage);
                    _repository.CompletePendingTranslations(pending.Select(operation => new PendingTranslationCompletion
                    {
                        OperationId = operation.OperationId,
                        CandidateId = operation.CandidateId,
                        TargetLanguage = targetLanguage,
                        TranslationText = operation.DesiredText,
                        Origin = operation.TranslationOrigin,
                        RelativePath = operation.RelativePath,
                        EntryKey = operation.EntryKey
                    }).ToList());
                }
                catch (Exception ex)
                {
                    foreach (PendingFileOperationRecord operation in pending)
                        if (operation.OperationId != Guid.Empty)
                            _repository.FailPendingFileOperation(operation.OperationId, ex);
                    throw;
                }
                written += page.Count;
                after = page[page.Count - 1].CandidateId;
            }
        }

        private void WriteManagedTranslation(
            CandidateRecord candidate,
            string targetLanguage,
            TranslationWriteTarget target)
        {
            Guid operationId = _repository.CreatePendingFileOperation(
                candidate.CandidateId, targetLanguage, 1, candidate.TranslationOrigin,
                target.RelativePath, target.EntryKey, candidate.TranslationText);
            try
            {
                _outputStore.Write(
                    candidate, targetLanguage, candidate.TranslationText,
                    candidate.TranslationOrigin, target);
                _repository.CompletePendingTranslation(
                    operationId, candidate.CandidateId, targetLanguage,
                    candidate.TranslationText, candidate.TranslationOrigin,
                    target.RelativePath, target.EntryKey);
            }
            catch (Exception ex)
            {
                _repository.FailPendingFileOperation(operationId, ex);
                throw;
            }
        }

        private TranslationOrigin ResolveObservedOrigin(
            CandidateRecord candidate,
            ObservedTranslationEntry observed,
            string targetLanguage,
            bool changedAfterBaseline)
        {
            if (candidate.SourceDomain == CandidateSourceDomain.Xml)
            {
                string languageRoot = Path.Combine(_index.PackRoot, "Languages", targetLanguage ?? string.Empty);
                string fullPath = Path.Combine(_index.PackRoot, observed.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                AutoTranslatorScanner.TranslationProvenanceEntry provenance =
                    AutoTranslatorScanner.GetFileEntryProvenance(
                        languageRoot, candidate.PackageId, fullPath, observed.EntryKey, observed.Text);
                string kind = provenance?.SourceKind ?? string.Empty;
                if (string.Equals(kind, AutoTranslatorScanner.ProvenanceKindManualEdit, StringComparison.OrdinalIgnoreCase))
                    return TranslationOrigin.Manual;
                if (string.Equals(kind, AutoTranslatorScanner.ProvenanceKindExternalPatch, StringComparison.OrdinalIgnoreCase))
                    return TranslationOrigin.ThirdParty;
                if (string.Equals(kind, AutoTranslatorScanner.ProvenanceKindModNativeTarget, StringComparison.OrdinalIgnoreCase))
                    return TranslationOrigin.ModNative;
                if (string.Equals(kind, AutoTranslatorScanner.ProvenanceKindCloud, StringComparison.OrdinalIgnoreCase))
                    return TranslationOrigin.Cloud;
                if (string.Equals(kind, AutoTranslatorScanner.ProvenanceKindAI, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kind, AutoTranslatorScanner.ProvenanceKindAIFromSecondary, StringComparison.OrdinalIgnoreCase))
                    return TranslationOrigin.AiTranslation;
            }
            return changedAfterBaseline ? TranslationOrigin.Manual : TranslationOrigin.None;
        }

        private void RecoverPendingOperations(
            string targetLanguage,
            TranslationFileIndexResult index,
            TranslationSynchronizationResult result,
            CancellationToken cancellationToken)
        {
            foreach (PendingFileOperationRecord operation in _repository.GetPendingFileOperations(targetLanguage))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = TranslationFileIndexResult.CreateKey(operation.RelativePath, operation.EntryKey);
                try
                {
                    if (operation.OperationKind == 1)
                    {
                        CandidateRecord candidate = _repository.GetCandidate(
                            operation.CandidateId, targetLanguage);
                        if (candidate == null)
                        {
                            _repository.AbandonPendingFileOperation(
                                operation.OperationId, "Candidate no longer exists.");
                            result.RecoveredPendingOperations++;
                            continue;
                        }
                        if (!string.Equals(
                                operation.SourceTextHash, candidate.SourceTextHash,
                                StringComparison.Ordinal))
                        {
                            if (index.Entries.TryGetValue(key, out ObservedTranslationEntry stale) &&
                                string.Equals(stale.TextHash, operation.DesiredHash, StringComparison.Ordinal))
                            {
                                _outputStore.Delete(candidate, targetLanguage, new TranslationWriteTarget
                                {
                                    RelativePath = operation.RelativePath,
                                    EntryKey = operation.EntryKey
                                });
                                index.Entries.Remove(key);
                            }
                            _repository.ClearTranslation(operation.CandidateId, targetLanguage);
                            _repository.AbandonPendingFileOperation(
                                operation.OperationId,
                                "Pending write was abandoned because the source text changed.");
                            result.Diagnostics.Add(
                                "Pending operation " + operation.OperationId.ToString("N") +
                                " was abandoned after the source text changed.");
                            result.RecoveredPendingOperations++;
                            continue;
                        }
                        if (index.Entries.TryGetValue(key, out ObservedTranslationEntry observed) &&
                            !string.Equals(observed.TextHash, operation.DesiredHash, StringComparison.Ordinal))
                        {
                            _repository.ClearTranslation(operation.CandidateId, targetLanguage);
                            _repository.AbandonPendingFileOperation(
                                operation.OperationId,
                                "Pending write was superseded by a different file value.");
                            result.RecoveredPendingOperations++;
                            continue;
                        }
                        if (observed == null)
                        {
                            TranslationWriteTarget writeTarget = new TranslationWriteTarget
                            {
                                RelativePath = operation.RelativePath,
                                EntryKey = operation.EntryKey
                            };
                            _outputStore.Write(
                                candidate, targetLanguage, operation.DesiredText,
                                operation.TranslationOrigin, writeTarget);
                            observed = new ObservedTranslationEntry
                            {
                                CandidateId = candidate.CandidateId,
                                RelativePath = operation.RelativePath,
                                EntryKey = operation.EntryKey,
                                Text = operation.DesiredText,
                                TextHash = operation.DesiredHash
                            };
                            index.Entries[key] = observed;
                        }
                        _repository.CompletePendingTranslation(
                            operation.OperationId, operation.CandidateId, targetLanguage,
                            operation.DesiredText, operation.TranslationOrigin,
                            operation.RelativePath, operation.EntryKey,
                            operation.TranslationOrigin == TranslationOrigin.Manual,
                            operation.AiProvider, operation.AiModel,
                            operation.AiPromptVersion, operation.AiRunId,
                            operation.AiBatchIndex);
                        result.RecoveredPendingOperations++;
                    }
                    else if (operation.OperationKind == 2)
                    {
                        if (index.Entries.ContainsKey(key))
                        {
                            CandidateRecord candidate = _repository.GetCandidate(operation.CandidateId, targetLanguage);
                            if (candidate == null) throw new InvalidDataException("Pending deletion candidate is missing.");
                            _outputStore.Delete(candidate, targetLanguage, new TranslationWriteTarget
                            {
                                RelativePath = operation.RelativePath,
                                EntryKey = operation.EntryKey
                            });
                            index.Entries.Remove(key);
                        }
                        _repository.CompletePendingDeletion(
                            operation.OperationId, operation.CandidateId, targetLanguage);
                        result.RecoveredPendingOperations++;
                    }
                }
                catch (Exception ex)
                {
                    _repository.FailPendingFileOperation(operation.OperationId, ex);
                    result.Diagnostics.Add("Pending operation " + operation.OperationId.ToString("N") + ": " + ex.Message);
                    result.FailedPendingOperations++;
                }
            }
        }
    }
}
