using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
using AutoTranslator_Core.Terminology;
// 這個檔案負責 Keyed 與 DefInjected 翻譯處理。
// EN: This file processes Keyed and DefInjected translation data.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器掃描器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorScanner.
    public static partial class AutoTranslatorScanner
    {
        private enum TranslationOutputMode
        {
            LivePack,
            PureAiWorkspace
        }

        private enum NativeTargetUseResult
        {
            Accepted,
            RequiresTranslation,
            HardDenied
        }

        private static string GetTranslationOutputLanguageRoot(ModMetaData mod, string targetFolder, TranslationOutputMode outputMode)
        {
            if (outputMode == TranslationOutputMode.PureAiWorkspace && mod != null && !string.IsNullOrEmpty(mod.PackageId))
            {
                return Path.Combine(GetLocalPackPath(), "Upload_Workspace", mod.PackageId, targetFolder);
            }

            return Path.Combine(GetLocalPackPath(), "Languages", targetFolder);
        }

        private static List<string> GetPureAiSourceBucketPaths(string langRoot, TargetLanguage targetLang, string bucketName)
        {
            List<string> englishPaths = new List<string>();
            foreach (string englishDir in ResolveLanguageFolders(langRoot, GetFolderNameByLanguage(TargetLanguage.English)))
            {
                englishPaths.AddRange(GetLanguageBucketPaths(englishDir, bucketName));
            }

            englishPaths = englishPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (englishPaths.Count > 0 || targetLang == TargetLanguage.English) return englishPaths;

            return GetTranslatableLanguageBucketPaths(langRoot, targetLang, bucketName, false);
        }

        // 這個方法負責處理 模組Keyed 流程。
        // EN: This method processes mod Keyed.
        private static async Task<int> ProcessModKeyedSources(ModMetaData mod, string langRoot)
        {
            return await ProcessModKeyedSources(mod, langRoot, TranslationOutputMode.LivePack);
        }

        private static async Task<int> ProcessModKeyedSources(ModMetaData mod, string langRoot, TranslationOutputMode outputMode)
        {
            int aiTranslatedCount = 0;
            var settings = AutoTranslatorMod.Settings;
            List<string> keyedSourcePaths = outputMode == TranslationOutputMode.PureAiWorkspace
                ? GetPureAiSourceBucketPaths(langRoot, settings.TargetLang, "Keyed")
                : GetTranslatableLanguageBucketPaths(langRoot, settings.TargetLang, "Keyed", false);
            if (keyedSourcePaths.Count == 0) return 0;

            AutoTranslatorSettings.AddLog("⚙️ " + AutoTranslatorAPI.TranslateText("ATC_Log_KeyedScan"));
            HashSet<string> processedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (List<KeyedFileWorkItem> keyedFiles in BuildKeyedFileWorkItems(mod, keyedSourcePaths))
            {
                aiTranslatedCount += await ProcessMergedModKeyed(mod, langRoot, keyedFiles, processedKeys, outputMode);
            }

            return aiTranslatedCount;
        }

        private class KeyedFileWorkItem
        {
            public string File;
            public string RelativeKeyedFile;
            public string TargetFileName;
            public int SourceOrder;
        }

        private class KeyedSourceEntry
        {
            public string Value;
            public string SourceFile;
            public bool FileLooksLikeTarget;
            public int SourceOrder;
        }

        private sealed class TranslationWorkItem
        {
            public string Key;
            public string TranslationInput;
            public string PolicySourceText;
            public string SourceFile;
            public TranslationProvenanceEntry Provenance;
            public bool IsPolicyOnlyExistingTranslation;
        }

        private sealed class PreferredTranslationCandidate
        {
            public string Value;
            public TranslationProvenanceEntry Provenance;
        }

        private sealed class TranslationPolicyWorkEvaluation
        {
            public TranslationWorkItem WorkItem;
            public TranslationPolicy.TranslationPolicyCandidate Candidate;
            public TranslationPolicy.TranslationPolicyClassification Classification;
        }

        // DefInjected policy decisions are collected before any one DefType is
        // translated. This lets the coordinator fill 20-group requests instead
        // of paying the minimum request overhead once per DefType.
        private sealed class DefTranslationPolicyContext
        {
            public string DefType;
            public string TargetFile;
            public Dictionary<string, string> FinalData;
            public Dictionary<string, TranslationProvenanceEntry> ProvenanceByKey;
            public List<TranslationWorkItem> WorkItems;
            public List<TranslationPolicyWorkEvaluation> Evaluations;
            public List<TranslationPolicy.TranslationPolicyCandidate> AmbiguousCandidates;
            public int LocalAllowCount;
            public int LocalDenyCount;
        }

        private static List<List<KeyedFileWorkItem>> BuildKeyedFileWorkItems(ModMetaData mod, List<string> keyedSourcePaths)
        {
            var groups = new Dictionary<string, List<KeyedFileWorkItem>>(StringComparer.OrdinalIgnoreCase);
            if (mod == null || keyedSourcePaths == null) return new List<List<KeyedFileWorkItem>>();

            for (int sourceOrder = 0; sourceOrder < keyedSourcePaths.Count; sourceOrder++)
            {
                string sourceKeyedPath = keyedSourcePaths[sourceOrder];
                if (string.IsNullOrEmpty(sourceKeyedPath) || !Directory.Exists(sourceKeyedPath)) continue;

                string sourceKeyedRoot = Path.GetFullPath(sourceKeyedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (string file in GetXmlFilesCached(sourceKeyedPath, SearchOption.AllDirectories))
                {
                    string relativeKeyedFile = Path.GetFileName(file);
                    string fullFile = Path.GetFullPath(file);
                    if (fullFile.StartsWith(sourceKeyedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        fullFile.StartsWith(sourceKeyedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        relativeKeyedFile = fullFile.Substring(sourceKeyedRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }

                    string targetFileName = TranslationGeneratedOutputOwnership.GetCanonicalFileName(
                        mod.PackageId);
                    if (!groups.TryGetValue(targetFileName, out List<KeyedFileWorkItem> group))
                    {
                        group = new List<KeyedFileWorkItem>();
                        groups[targetFileName] = group;
                    }

                    group.Add(new KeyedFileWorkItem
                    {
                        File = file,
                        RelativeKeyedFile = relativeKeyedFile,
                        TargetFileName = targetFileName,
                        SourceOrder = sourceOrder
                    });
                }
            }

            return groups.Values
                .OrderBy(g => g.Min(i => i.SourceOrder))
                .ThenBy(g => g[0].TargetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(i => i.SourceOrder).ThenBy(i => i.RelativeKeyedFile, StringComparer.OrdinalIgnoreCase).ToList())
                .ToList();
        }

        private static async Task<int> ProcessMergedModKeyed(ModMetaData mod, string langRoot, List<KeyedFileWorkItem> sourceFiles, HashSet<string> processedKeys)
        {
            return await ProcessMergedModKeyed(mod, langRoot, sourceFiles, processedKeys, TranslationOutputMode.LivePack);
        }

        private static async Task<int> ProcessMergedModKeyed(ModMetaData mod, string langRoot, List<KeyedFileWorkItem> sourceFiles, HashSet<string> processedKeys, TranslationOutputMode outputMode)
        {
            int aiTranslatedCount = 0;
            if (sourceFiles == null || sourceFiles.Count == 0) return 0;

            var settings = AutoTranslatorMod.Settings;
            string targetFolder = GetFolderNameByLanguage(settings.TargetLang);
            string packLangRoot = GetTranslationOutputLanguageRoot(mod, targetFolder, outputMode);
            string packKeyedDir = Path.Combine(packLangRoot, "Keyed");
            string targetFile = Path.Combine(packKeyedDir, sourceFiles[0].TargetFileName);
            bool pureAiWorkspace = outputMode == TranslationOutputMode.PureAiWorkspace;
            Dictionary<string, string> packSourceFileByKey =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var packDict = pureAiWorkspace
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : LoadExistingGeneratedKeyedData(
                    packKeyedDir,
                    targetFile,
                    mod.PackageId,
                    sourceFiles.Select(item => item.File),
                    settings.TargetLang,
                    out packSourceFileByKey);
            Dictionary<string, string> nativeTargetDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TranslationProvenanceEntry> nativeTargetSourceDict =
                new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> finalData = null;
            Dictionary<string, TranslationProvenanceEntry> provenanceByKey = null;

            string secondaryTag = "";
            if (settings.TargetLang == TargetLanguage.Traditional)
                secondaryTag = AutoTranslatorAPI.TranslateText("ATC_Tag_FromSimplified");
            else if (settings.TargetLang == TargetLanguage.Simplified)
                secondaryTag = AutoTranslatorAPI.TranslateText("ATC_Tag_FromTraditional");

            string currentSourceFile = sourceFiles[0].File;
            try
            {
                if (!pureAiWorkspace)
                {
                    foreach (string relativeKeyedFile in sourceFiles.Select(i => i.RelativeKeyedFile).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        foreach (string targetLangDir in ResolveLanguageFolders(langRoot, targetFolder))
                        {
                            foreach (string targetKeyedDir in GetLanguageBucketPaths(targetLangDir, "Keyed"))
                            {
                                string targetKeyedFile = Path.Combine(targetKeyedDir, relativeKeyedFile);
                                foreach (var kv in LoadXmlFileToDict(targetKeyedFile, settings.TargetLang))
                                {
                                    nativeTargetDict[kv.Key] = kv.Value;
                                    nativeTargetSourceDict[kv.Key] = CreateProvenance(
                                        ProvenanceKindModNativeTarget,
                                        mod.PackageId,
                                        mod.Name,
                                        targetKeyedFile,
                                        targetFolder,
                                        kv.Value);
                                }
                            }
                        }
                    }
                }

                Dictionary<string, List<KeyedSourceEntry>> sourceEntries = new Dictionary<string, List<KeyedSourceEntry>>(StringComparer.OrdinalIgnoreCase);
                List<string> orderedKeys = new List<string>();
                int totalFiles = sourceFiles.Count;
                int currentFile = 0;

                foreach (KeyedFileWorkItem sourceFile in sourceFiles)
                {
                    if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested) return aiTranslatedCount;

                    currentSourceFile = sourceFile.File;
                    currentFile++;
                    AutoTranslatorMod.Settings.SubProgress = totalFiles == 0 ? 0f : (float)currentFile / totalFiles;

                    XmlDocument doc = new XmlDocument();
                    doc.Load(sourceFile.File);
                    if (doc.DocumentElement == null) continue;

                    string keyedLanguageSample = string.Join("\n", doc.DocumentElement.ChildNodes
                        .Cast<XmlNode>()
                        .Where(n => n.NodeType == XmlNodeType.Element && !string.IsNullOrWhiteSpace(n.InnerText))
                        .Select(n => n.InnerText)
                        .Take(80)
                        .ToArray());
                    bool keyedFileLooksLikeTarget = LanguageDetector.LooksLikeTargetLanguage(keyedLanguageSample, settings.TargetLang);

                    foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                    {
                        if (node.NodeType != XmlNodeType.Element || string.IsNullOrEmpty(node.InnerText)) continue;
                        string key = node.Name;
                        string value = node.InnerText;
                        if (LanguageDetector.LooksLikePlaceholderTranslation(value, settings.TargetLang)) continue;

                        if (!sourceEntries.TryGetValue(key, out List<KeyedSourceEntry> entries))
                        {
                            entries = new List<KeyedSourceEntry>();
                            sourceEntries[key] = entries;
                            orderedKeys.Add(key);
                        }

                        entries.Add(new KeyedSourceEntry
                        {
                            Value = value,
                            SourceFile = sourceFile.File,
                            FileLooksLikeTarget = keyedFileLooksLikeTarget,
                            SourceOrder = sourceFile.SourceOrder
                        });
                    }
                }

                // Keep generated keys with no current source entry; without an original value,
                // policy cannot safely distinguish a stale key from an intentionally isolated one.
                finalData = new Dictionary<string, string>(packDict, StringComparer.OrdinalIgnoreCase);
                provenanceByKey = new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in packDict)
                {
                    string sourceTranslationFile = targetFile;
                    packSourceFileByKey.TryGetValue(pair.Key, out sourceTranslationFile);
                    provenanceByKey[pair.Key] = GetFileEntryProvenance(
                        packLangRoot,
                        mod.PackageId,
                        string.IsNullOrWhiteSpace(sourceTranslationFile) ? targetFile : sourceTranslationFile,
                        pair.Key,
                        pair.Value);
                }

                List<TranslationWorkItem> workItems = new List<TranslationWorkItem>();

                foreach (string key in orderedKeys)
                {
                    if (processedKeys != null && processedKeys.Contains(key)) continue;
                    if (!sourceEntries.TryGetValue(key, out List<KeyedSourceEntry> entries) || entries.Count == 0) continue;

                    KeyedSourceEntry sourceEntry = PickBestKeyedSourceEntry(entries, settings.TargetLang);
                    string sourceText = sourceEntry != null ? sourceEntry.Value : "";

                    if (!pureAiWorkspace)
                    {
                        List<PreferredTranslationCandidate> preferredCandidates =
                            new List<PreferredTranslationCandidate>();
                        if (packDict.TryGetValue(key, out string preferredPackValue))
                        {
                            provenanceByKey.TryGetValue(key, out TranslationProvenanceEntry preferredPackSource);
                            preferredCandidates.Add(new PreferredTranslationCandidate
                            {
                                Value = preferredPackValue,
                                Provenance = preferredPackSource
                            });
                        }
                        if (GlobalPrimaryKeyedDict.TryGetValue(key, out string preferredGlobalValue) &&
                            GlobalPrimaryKeyedSourceDict.TryGetValue(key, out TranslationProvenanceEntry preferredGlobalSource) &&
                            IsShareablePreferredSource(preferredGlobalSource))
                        {
                            preferredCandidates.Add(new PreferredTranslationCandidate
                            {
                                Value = preferredGlobalValue,
                                Provenance = preferredGlobalSource
                            });
                        }
                        if (nativeTargetDict.TryGetValue(key, out string preferredNativeValue))
                        {
                            nativeTargetSourceDict.TryGetValue(key, out TranslationProvenanceEntry preferredNativeSource);
                            preferredCandidates.Add(new PreferredTranslationCandidate
                            {
                                Value = preferredNativeValue,
                                Provenance = preferredNativeSource
                            });
                        }

                        if (TryApplyPreferredTargetTranslation(
                                mod,
                                key,
                                sourceText,
                                preferredCandidates,
                                finalData,
                                provenanceByKey,
                                out bool usedPreferredExisting))
                        {
                            AddExistingTranslationPolicyWorkItem(
                                workItems,
                                usedPreferredExisting,
                                key,
                                sourceText,
                                sourceEntry != null ? sourceEntry.SourceFile : string.Empty);
                            continue;
                        }
                    }

                    if (!pureAiWorkspace && nativeTargetDict.TryGetValue(key, out string nativeVal))
                    {
                        TranslationProvenanceEntry nativeSource = null;
                        nativeTargetSourceDict.TryGetValue(key, out nativeSource);
                        string nativeSourceFile = nativeSource != null ? nativeSource.SourceFile : "";
                        NativeTargetUseResult nativeResult = TryUseNativeTargetTranslation(
                            mod,
                            TranslationPolicy.TranslationPolicyBucket.Keyed,
                            string.Empty,
                            key,
                            nativeVal,
                            sourceText,
                            nativeSourceFile,
                            finalData,
                            out string nativeTranslationInput);
                        if (nativeResult == NativeTargetUseResult.HardDenied)
                        {
                            continue;
                        }

                        if (nativeResult == NativeTargetUseResult.Accepted)
                        {
                            provenanceByKey[key] = nativeSource != null
                                ? CloneProvenance(nativeSource, finalData[key])
                                : CreateProvenance(
                                    ProvenanceKindModNativeTarget,
                                    mod.PackageId,
                                    mod.Name,
                                    nativeSourceFile,
                                    targetFolder,
                                    finalData[key]);
                        }
                        else
                        {
                            string input = string.IsNullOrWhiteSpace(nativeTranslationInput)
                                ? sourceText
                                : nativeTranslationInput;
                            workItems.Add(CreateTranslationWorkItem(
                                key,
                                input,
                                string.IsNullOrWhiteSpace(sourceText) ? input : sourceText,
                                string.IsNullOrWhiteSpace(sourceEntry != null ? sourceEntry.SourceFile : "")
                                    ? nativeSourceFile
                                    : sourceEntry.SourceFile,
                                CreateProvenance(
                                    ProvenanceKindAI,
                                    mod.PackageId,
                                    mod.Name,
                                    string.IsNullOrWhiteSpace(sourceEntry != null ? sourceEntry.SourceFile : "")
                                        ? nativeSourceFile
                                        : sourceEntry.SourceFile,
                                    "English",
                                    "")));
                        }
                    }
                    else if (!pureAiWorkspace && packDict.TryGetValue(key, out string packVal))
                    {
                        bool usedExistingValue;
                        string translationInput;
                        bool setValue = TryUseExistingTranslation(
                            finalData,
                            key,
                            packVal,
                            sourceText,
                            out usedExistingValue,
                            out translationInput);
                        if (setValue)
                        {
                            provenanceByKey[key] = usedExistingValue
                                ? GetFileEntryProvenance(packLangRoot, mod.PackageId, targetFile, key, finalData[key])
                                : CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, sourceEntry != null ? sourceEntry.SourceFile : "", targetFolder, finalData[key]);
                            AddExistingTranslationPolicyWorkItem(
                                workItems,
                                usedExistingValue,
                                key,
                                sourceText,
                                sourceEntry != null ? sourceEntry.SourceFile : "");
                        }
                        else
                        {
                            workItems.Add(CreateTranslationWorkItem(
                                key,
                                translationInput,
                                sourceText,
                                sourceEntry != null ? sourceEntry.SourceFile : "",
                                CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceEntry != null ? sourceEntry.SourceFile : "", "English", "")));
                        }
                    }
                    else if (!pureAiWorkspace && GlobalPrimaryKeyedDict.TryGetValue(key, out string pVal))
                    {
                        bool usedExistingValue;
                        string translationInput;
                        bool setValue = TryUseExistingTranslation(
                            finalData,
                            key,
                            pVal,
                            sourceText,
                            out usedExistingValue,
                            out translationInput);
                        if (setValue)
                        {
                            TranslationProvenanceEntry sourceInfo = null;
                            GlobalPrimaryKeyedSourceDict.TryGetValue(key, out sourceInfo);
                            provenanceByKey[key] = usedExistingValue
                                ? CloneProvenance(sourceInfo, finalData[key])
                                : CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, sourceEntry != null ? sourceEntry.SourceFile : "", targetFolder, finalData[key]);
                            AddExistingTranslationPolicyWorkItem(
                                workItems,
                                usedExistingValue,
                                key,
                                sourceText,
                                sourceEntry != null ? sourceEntry.SourceFile : "");
                        }
                        else
                        {
                            workItems.Add(CreateTranslationWorkItem(
                                key,
                                translationInput,
                                sourceText,
                                sourceEntry != null ? sourceEntry.SourceFile : "",
                                CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceEntry != null ? sourceEntry.SourceFile : "", "English", "")));
                        }
                    }
                    else if (!pureAiWorkspace && GlobalSecondaryKeyedDict.TryGetValue(key, out string sVal) && !string.IsNullOrEmpty(secondaryTag))
                    {
                        TranslationProvenanceEntry secondarySource = null;
                        GlobalSecondaryKeyedSourceDict.TryGetValue(key, out secondarySource);
                        workItems.Add(CreateTranslationWorkItem(
                            key,
                            PrepareSecondaryTranslationSource(sVal, sourceText),
                            sourceText,
                            sourceEntry != null ? sourceEntry.SourceFile : "",
                            CreateProvenance(
                                ProvenanceKindAIFromSecondary,
                                secondarySource != null ? secondarySource.SourcePackageId : mod.PackageId,
                                secondarySource != null ? secondarySource.SourceModName : mod.Name,
                                secondarySource != null ? secondarySource.SourceFile : "",
                                secondarySource != null ? secondarySource.SourceLanguage : "",
                                "",
                                secondarySource != null ? secondarySource.SourceKind : "")));
                    }
                    else if (sourceEntry != null &&
                             !pureAiWorkspace &&
                             (sourceEntry.FileLooksLikeTarget || LanguageDetector.LooksLikeTargetLanguage(sourceEntry.Value, settings.TargetLang)))
                    {
                        NativeTargetUseResult nativeResult = TryUseNativeTargetTranslation(
                            mod,
                            TranslationPolicy.TranslationPolicyBucket.Keyed,
                            string.Empty,
                            key,
                            sourceEntry.Value,
                            sourceText,
                            sourceEntry.SourceFile,
                            finalData,
                            out string nativeTranslationInput);
                        if (nativeResult == NativeTargetUseResult.HardDenied)
                            continue;

                        if (nativeResult == NativeTargetUseResult.Accepted)
                        {
                            provenanceByKey[key] = CreateProvenance(
                                ProvenanceKindModNativeTarget,
                                mod.PackageId,
                                mod.Name,
                                sourceEntry.SourceFile,
                                targetFolder,
                                finalData[key]);
                        }
                        else
                        {
                            string input = string.IsNullOrWhiteSpace(nativeTranslationInput)
                                ? sourceText
                                : nativeTranslationInput;
                            workItems.Add(CreateTranslationWorkItem(
                                key,
                                input,
                                string.IsNullOrWhiteSpace(sourceText) ? input : sourceText,
                                sourceEntry.SourceFile,
                                CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceEntry.SourceFile, "English", "")));
                        }
                    }
                    else if (sourceEntry != null)
                    {
                        workItems.Add(CreateTranslationWorkItem(
                            key,
                            sourceEntry.Value,
                            sourceEntry.Value,
                            sourceEntry.SourceFile,
                            CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceEntry.SourceFile, "English", "")));
                    }

                }

                if (!pureAiWorkspace && settings.IsTerminologyEnabledForPackage(mod.PackageId))
                {
                    var trustedPairs = new List<TerminologyAlignedSentencePair>();
                    foreach (KeyValuePair<string, string> translated in finalData)
                    {
                        if (!sourceEntries.TryGetValue(translated.Key, out List<KeyedSourceEntry> alignedSources) ||
                            alignedSources == null || alignedSources.Count == 0 ||
                            !provenanceByKey.TryGetValue(translated.Key, out TranslationProvenanceEntry alignedProvenance))
                            continue;
                        TranslationSourceCategory category = TranslationSourcePriorityPolicy.ClassifyProvenance(
                            alignedProvenance?.SourceKind);
                        if (category != TranslationSourceCategory.UserManual &&
                            category != TranslationSourceCategory.ExternalHuman &&
                            category != TranslationSourceCategory.ModNative)
                            continue;
                        KeyedSourceEntry alignedSource = PickBestKeyedSourceEntry(alignedSources, settings.TargetLang);
                        if (alignedSource == null) continue;
                        trustedPairs.Add(new TerminologyAlignedSentencePair
                        {
                            PairId = mod.PackageId + ":keyed:" + translated.Key,
                            PackageId = mod.PackageId,
                            Source = alignedSource.Value,
                            Target = translated.Value
                        });
                    }
                    TerminologyRuntime.ObserveAlignedTranslations(mod.PackageId, trustedPairs);
                }

                workItems = await FilterTranslationWorkItemsByPolicyAsync(
                    mod,
                    TranslationPolicy.TranslationPolicyBucket.Keyed,
                    string.Empty,
                    targetFile,
                    workItems,
                    finalData,
                    provenanceByKey);
                if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested)
                {
                    CheckpointGeneratedTranslationProgress(
                        mod,
                        "Keyed",
                        string.Empty,
                        targetFile,
                        packLangRoot,
                        finalData,
                        provenanceByKey);
                    return aiTranslatedCount;
                }

                workItems = workItems
                    .Where(item => !item.IsPolicyOnlyExistingTranslation)
                    .ToList();
                workItems = ApplyKeepOriginalDecisions(
                    mod,
                    "Keyed",
                    string.Empty,
                    workItems,
                    finalData,
                    provenanceByKey);
                if (workItems.Count > 0)
                {
                    AutoTranslatorSettings.AddLog("🔌 " + AutoTranslatorAPI.TranslateText("ATC_Log_FoundMissing", "Keyed", workItems.Count));
                    TerminologyRuntime.ObserveTranslationInputs(
                        mod.PackageId,
                        "Keyed",
                        string.Empty,
                        workItems.Select(item => new KeyValuePair<string, string>(item.Key, item.TranslationInput)));
                    await TerminologyRuntime.ResolveHighValueCandidatesAsync(mod.PackageId);
                    if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested)
                    {
                        CheckpointGeneratedTranslationProgress(
                            mod,
                            "Keyed",
                            string.Empty,
                            targetFile,
                            packLangRoot,
                            finalData,
                            provenanceByKey);
                        return aiTranslatedCount;
                    }
                    List<string> translationInputs = workItems.Select(item => item.TranslationInput).ToList();
                    List<TranslationBatchItemResult> res = await SafeTranslateBatch(
                        translationInputs,
                        $"{mod.Name} / {sourceFiles[0].TargetFileName}",
                        mod.PackageId);
                    bool interruptedAfterBatch =
                        AutoTranslatorSettings.IsCancellationRequested ||
                        AutoTranslatorSettings.IsSkipCurrentRequested;
                    if (res != null)
                    {
                        int acceptedCount = 0;
                        for (int i = 0; i < workItems.Count; i++)
                        {
                            TranslationWorkItem item = workItems[i];
                            string k = item.Key;
                            TranslationBatchItemResult batchResult = i < res.Count ? res[i] : null;
                            if (batchResult == null || !batchResult.IsSuccess)
                            {
                                if (!interruptedAfterBatch)
                                {
                                    RecordUnresolvedTranslation(
                                        mod,
                                        "Keyed",
                                        string.Empty,
                                        targetFile,
                                        item,
                                        batchResult != null ? batchResult.FailureReason : TranslationUnresolvedReasons.ApiFailure,
                                        batchResult != null ? batchResult.Detail : "No batch result was produced.");
                                }
                                continue;
                            }

                            string v = batchResult.Value;

                            if (!TryAcceptTranslatedValue(
                                    v,
                                    item.TranslationInput,
                                    out v,
                                    out string failureReason,
                                    out string failureDetail))
                            {
                                RecordUnresolvedTranslation(
                                    mod,
                                    "Keyed",
                                    string.Empty,
                                    targetFile,
                                    item,
                                    failureReason,
                                    failureDetail);
                                continue;
                            }

                            finalData[k] = v;
                            provenanceByKey[k] = item.Provenance != null
                                ? CloneProvenance(item.Provenance, v)
                                : CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceFiles[0].File, "English", v);
                            TranslationUnresolvedManager.ResolveMatching(
                                mod.PackageId,
                                "Keyed",
                                string.Empty,
                                item.Key,
                                string.IsNullOrWhiteSpace(item.PolicySourceText)
                                    ? item.TranslationInput
                                    : item.PolicySourceText,
                                AutoTranslatorMod.Settings.TargetLang.ToString());
                            acceptedCount++;
                        }

                        AutoTranslatorSettings.AddLog("✨ " + AutoTranslatorAPI.TranslateText("ATC_Log_AIFinish", "Keyed"));
                        aiTranslatedCount += acceptedCount;
                    }
                    else AutoTranslatorSettings.AddLog("⚠️ " + AutoTranslatorAPI.TranslateText("ATC_Log_AIFail", "Keyed"));
                }

                if (processedKeys != null)
                {
                    foreach (string key in orderedKeys)
                    {
                        if (finalData.ContainsKey(key)) processedKeys.Add(key);
                    }
                }

                bool interruptedBeforeSave =
                    AutoTranslatorSettings.IsCancellationRequested ||
                    AutoTranslatorSettings.IsSkipCurrentRequested;
                if (!interruptedBeforeSave)
                    AutoTranslatorSettings.AddLog("✅ " + AutoTranslatorAPI.TranslateText("ATC_Log_NoMissing", Path.GetFileName(sourceFiles[0].File)));
                if (CheckpointGeneratedTranslationProgress(
                        mod,
                        "Keyed",
                        string.Empty,
                        targetFile,
                        packLangRoot,
                        finalData,
                        provenanceByKey) &&
                    !interruptedBeforeSave)
                {
                    DeleteSupersededGeneratedKeyedFiles(
                        packKeyedDir,
                        mod,
                        sourceFiles.Select(item => item.File),
                        targetFile);
                }
            }
            catch (XmlException xmlEx)
            {
                CheckpointGeneratedTranslationProgress(
                    mod,
                    "Keyed",
                    string.Empty,
                    targetFile,
                    packLangRoot,
                    finalData,
                    provenanceByKey);
                string file = currentSourceFile ?? string.Empty;
                AutoTranslatorSettings.AddErrorLog("❌ " + AutoTranslatorAPI.TranslateText("ATC_LogError_Format", mod.Name, GetShortPath(file)));
                Log.Warning($"[AutoTranslationCore] XML Format Error ({mod.Name}): {xmlEx.Message}");
                TranslationUnresolvedManager.MarkPackageScanIncomplete(
                    mod.PackageId,
                    AutoTranslatorMod.Settings.TargetLang.ToString());
                RecordSourceProcessingFailure(
                    mod,
                    "Keyed",
                    string.Empty,
                    file,
                    "The source Keyed XML could not be parsed: " + xmlEx.Message);
            }
            catch (Exception ex)
            {
                CheckpointGeneratedTranslationProgress(
                    mod,
                    "Keyed",
                    string.Empty,
                    targetFile,
                    packLangRoot,
                    finalData,
                    provenanceByKey);
                string file = currentSourceFile ?? string.Empty;
                AutoTranslatorSettings.AddErrorLog("❌ " + AutoTranslatorAPI.TranslateText("ATC_LogError_Unknown", mod.Name, GetShortPath(file)));
                Log.Warning($"[AutoTranslationCore] Process Error ({mod.Name}): {ex.Message}");
                TranslationUnresolvedManager.MarkPackageScanIncomplete(
                    mod.PackageId,
                    AutoTranslatorMod.Settings.TargetLang.ToString());
                RecordSourceProcessingFailure(
                    mod,
                    "Keyed",
                    string.Empty,
                    file,
                    "The source Keyed content could not be processed: " + ex.Message);
            }

            return aiTranslatedCount;
        }

        private static KeyedSourceEntry PickBestKeyedSourceEntry(List<KeyedSourceEntry> entries, TargetLanguage targetLang)
        {
            if (entries == null || entries.Count == 0) return null;

            foreach (KeyedSourceEntry entry in entries.OrderBy(e => e.SourceOrder))
            {
                if (entry.FileLooksLikeTarget || LanguageDetector.LooksLikeTargetLanguage(entry.Value, targetLang))
                {
                    return entry;
                }
            }

            return entries.OrderBy(e => e.SourceOrder).FirstOrDefault();
        }

        private static async Task<int> ProcessModDefInjected(ModMetaData mod, List<string> langRoots, List<string> defsRoots)
        {
            return await ProcessModDefInjected(mod, langRoots, defsRoots, TranslationOutputMode.LivePack);
        }

        private static async Task<int> ProcessModDefInjected(ModMetaData mod, List<string> langRoots, List<string> defsRoots, TranslationOutputMode outputMode)
        {
            int aiTranslatedCount = 0;
            var settings = AutoTranslatorMod.Settings;
            string targetFolder = GetFolderNameByLanguage(settings.TargetLang);
            string otherFolder = GetSecondaryFolderNameByLanguage(settings.TargetLang);
            string secondaryTag = "";
            if (settings.TargetLang == TargetLanguage.Traditional)
                secondaryTag = AutoTranslatorAPI.TranslateText("ATC_Tag_FromSimplified");
            else if (settings.TargetLang == TargetLanguage.Simplified)
                secondaryTag = AutoTranslatorAPI.TranslateText("ATC_Tag_FromTraditional");
            string packLangRoot = GetTranslationOutputLanguageRoot(mod, targetFolder, outputMode);
            string packDefBaseDir = Path.Combine(packLangRoot, "DefInjected");
            bool pureAiWorkspace = outputMode == TranslationOutputMode.PureAiWorkspace;


            var englishKeys = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var englishSourceFiles = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var modSelfTargetLang = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var modSelfSecondaryLang = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var modSelfTargetSources = new Dictionary<string, Dictionary<string, TranslationProvenanceEntry>>(StringComparer.OrdinalIgnoreCase);
            var modSelfSecondarySources = new Dictionary<string, Dictionary<string, TranslationProvenanceEntry>>(StringComparer.OrdinalIgnoreCase);


            Action<string, Dictionary<string, Dictionary<string, string>>, Dictionary<string, Dictionary<string, TranslationProvenanceEntry>>, TargetLanguage?, string> LoadDefsToDict = (path, targetDict, sourceDict, lang, sourceKind) => {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
                foreach (var typeDir in Directory.GetDirectories(path))
                {
                    string defType = Path.GetFileName(typeDir);
                    if (!targetDict.ContainsKey(defType))
                        targetDict[defType] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (sourceDict != null && !sourceDict.ContainsKey(defType))
                        sourceDict[defType] = new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in GetXmlFilesCached(typeDir, SearchOption.AllDirectories))
                    {
                        var d = LoadXmlFileToDict(file, lang);
                        foreach (var kv in d)
                        {
                            targetDict[defType][kv.Key] = kv.Value;
                            if (sourceDict != null)
                            {
                                sourceDict[defType][kv.Key] = CreateProvenance(sourceKind, mod.PackageId, mod.Name, file, lang.HasValue ? GetFolderNameByLanguage(lang.Value) : "", kv.Value);
                            }
                        }
                    }
                }
                foreach (var file in GetXmlFilesCached(path, SearchOption.TopDirectoryOnly))
                {
                    if (!targetDict.ContainsKey("General"))
                        targetDict["General"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (sourceDict != null && !sourceDict.ContainsKey("General"))
                        sourceDict["General"] = new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
                    var d = LoadXmlFileToDict(file, lang);
                    foreach (var kv in d)
                    {
                        targetDict["General"][kv.Key] = kv.Value;
                        if (sourceDict != null)
                        {
                            sourceDict["General"][kv.Key] = CreateProvenance(sourceKind, mod.PackageId, mod.Name, file, lang.HasValue ? GetFolderNameByLanguage(lang.Value) : "", kv.Value);
                        }
                    }
                }
            };

            foreach (var dRoot in defsRoots)
            {
                var extractedSourceFiles =
                    new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                List<string> failedDefFiles;
                var extracted = ExtractEnglishFromRawDefs(
                    dRoot,
                    TranslationPolicyAgentCoordinator.IsEnabledForCurrentRun,
                    extractedSourceFiles,
                    out failedDefFiles);
                if (failedDefFiles.Count > 0)
                {
                    TranslationUnresolvedManager.MarkPackageScanIncomplete(
                        mod.PackageId,
                        settings.TargetLang.ToString());
                    foreach (string failedFile in failedDefFiles)
                    {
                        RecordSourceProcessingFailure(
                            mod,
                            "DefInjected",
                            string.Empty,
                            failedFile,
                            "The source Def XML could not be parsed or traversed.");
                    }
                }
                foreach (var kv in extracted)
                {
                    if (!englishKeys.ContainsKey(kv.Key))
                        englishKeys[kv.Key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!englishSourceFiles.ContainsKey(kv.Key))
                        englishSourceFiles[kv.Key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var inner in kv.Value)
                    {
                        englishKeys[kv.Key][inner.Key] = inner.Value;
                        if (extractedSourceFiles.TryGetValue(kv.Key, out Dictionary<string, string> sourceMap) &&
                            sourceMap.TryGetValue(inner.Key, out string sourcePath))
                        {
                            englishSourceFiles[kv.Key][inner.Key] = sourcePath;
                        }
                    }
                }
            }


            foreach (var lRoot in langRoots)
            {
                List<string> sourceDefDirs = pureAiWorkspace
                    ? GetPureAiSourceBucketPaths(lRoot, settings.TargetLang, "DefInjected")
                    : GetTranslatableLanguageBucketPaths(lRoot, settings.TargetLang, "DefInjected", false);
                for (int i = sourceDefDirs.Count - 1; i >= 0; i--)
                {
                    LoadDefsToDict(sourceDefDirs[i], englishKeys, null, null, ProvenanceKindUnknownLegacy);
                }

                if (!pureAiWorkspace)
                {
                    foreach (string targetLangDir in ResolveLanguageFolders(lRoot, targetFolder))
                    {
                        foreach (string targetDefDir in GetLanguageBucketPaths(targetLangDir, "DefInjected"))
                        {
                            LoadDefsToDict(targetDefDir, modSelfTargetLang, modSelfTargetSources, settings.TargetLang, ProvenanceKindModNativeTarget);
                        }
                    }

                    if (!string.IsNullOrEmpty(otherFolder))
                    {
                        TargetLanguage secLang = settings.TargetLang == TargetLanguage.Traditional ? TargetLanguage.Simplified : TargetLanguage.Traditional;
                        foreach (string secondaryLangDir in ResolveLanguageFolders(lRoot, otherFolder))
                        {
                            foreach (string secondaryDefDir in GetLanguageBucketPaths(secondaryLangDir, "DefInjected"))
                            {
                                LoadDefsToDict(secondaryDefDir, modSelfSecondaryLang, modSelfSecondarySources, secLang, ProvenanceKindModNativeTarget);
                            }
                        }
                    }
                }
            }

            if (!pureAiWorkspace)
            {
                foreach (var lRoot in langRoots)
                {
                    foreach (string targetLangDir in ResolveLanguageFolders(lRoot, targetFolder))
                    {
                        foreach (string targetDefDir in GetLanguageBucketPaths(targetLangDir, "DefInjected"))
                        {
                            LoadDefsToDict(targetDefDir, modSelfTargetLang, modSelfTargetSources, settings.TargetLang, ProvenanceKindModNativeTarget);
                        }
                    }

                    if (!string.IsNullOrEmpty(otherFolder))
                    {
                        TargetLanguage secLang = settings.TargetLang == TargetLanguage.Traditional ? TargetLanguage.Simplified : TargetLanguage.Traditional;
                        foreach (string secondaryLangDir in ResolveLanguageFolders(lRoot, otherFolder))
                        {
                            foreach (string secondaryDefDir in GetLanguageBucketPaths(secondaryLangDir, "DefInjected"))
                            {
                                LoadDefsToDict(secondaryDefDir, modSelfSecondaryLang, modSelfSecondarySources, secLang, ProvenanceKindModNativeTarget);
                            }
                        }
                    }
                }
            }

            if (englishKeys.Count == 0 && (pureAiWorkspace || modSelfTargetLang.Count == 0))
                return aiTranslatedCount;


            int modSelfTargetCount = modSelfTargetLang.Sum(kv => kv.Value.Count);
            if (!pureAiWorkspace && modSelfTargetCount > 0)
            {
                AutoTranslatorSettings.AddLog("✅ " +
                    AutoTranslatorAPI.TranslateText("ATC_Log_SkipExistingTranslation", mod.Name, modSelfTargetCount));
            }


            var allDefTypes = new HashSet<string>(englishKeys.Keys, StringComparer.OrdinalIgnoreCase);
            if (!pureAiWorkspace)
            {
                foreach (var k in modSelfTargetLang.Keys) allDefTypes.Add(k);
            }

            int totalDefs = allDefTypes.Count;
            int currentDef = 0;
            bool deferDefPolicy = TranslationPolicyAgentCoordinator.IsEnabledForCurrentRun;
            List<DefTranslationPolicyContext> deferredDefContexts =
                new List<DefTranslationPolicyContext>();

            foreach (var defType in allDefTypes)
            {
                if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested)
                {
                    CheckpointDeferredDefProgress(mod, packLangRoot, deferredDefContexts);
                    return aiTranslatedCount;
                }

                currentDef++;
                float defProgress = (float)currentDef / Math.Max(1, totalDefs);
                AutoTranslatorMod.Settings.SubProgress = deferDefPolicy
                    ? 0.5f * defProgress
                    : defProgress;


                if (TranslationPolicy.TranslationPolicyClassifier.IsProtectedDefType(defType))
                {
                    AutoTranslatorSettings.AddLog($"🛡️ [System] 已攔截並保護高危險臉部模型：{defType}");
                    continue;
                }


                var keysForThisType = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (englishKeys.TryGetValue(defType, out var engDict))
                    foreach (var k in engDict.Keys) keysForThisType.Add(k);
                if (modSelfTargetLang.TryGetValue(defType, out var selfDict))
                    foreach (var k in selfDict.Keys) keysForThisType.Add(k);

                if (keysForThisType.Count == 0) continue;

                string cleanPackageId = mod.PackageId.Replace(".", "_");
                string targetFile = Path.Combine(packDefBaseDir, defType, $"{cleanPackageId}_AutoTranslated.xml");
                var packDict = pureAiWorkspace
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : LoadXmlFileToDict(targetFile, settings.TargetLang);

                Dictionary<string, string> finalData =
                    new Dictionary<string, string>(packDict, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, TranslationProvenanceEntry> provenanceByKey =
                    new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in packDict)
                {
                    provenanceByKey[pair.Key] = GetFileEntryProvenance(
                        packLangRoot,
                        mod.PackageId,
                        targetFile,
                        pair.Key,
                        pair.Value);
                }

                foreach (string staleAggregateKey in TranslationGeneratedOutputCleanup.FindStaleAggregateKeys(
                    finalData.Keys,
                    keysForThisType))
                {
                    finalData.Remove(staleAggregateKey);
                    provenanceByKey.Remove(staleAggregateKey);
                }
                List<TranslationWorkItem> workItems = new List<TranslationWorkItem>();

                foreach (var key in keysForThisType)
                {
                    string globalKey = $"{defType}/{key}";
                    string globalKeyGen = $"General/{key}";
                    string sourceFile = string.Empty;
                    if (englishSourceFiles.TryGetValue(defType, out Dictionary<string, string> defSourceMap))
                        defSourceMap.TryGetValue(key, out sourceFile);

                    string preferredSourceText = engDict != null && engDict.TryGetValue(key, out string preferredEnglish)
                        ? preferredEnglish
                        : string.Empty;
                    if (!pureAiWorkspace)
                    {
                        List<PreferredTranslationCandidate> preferredCandidates =
                            new List<PreferredTranslationCandidate>();
                        if (packDict.TryGetValue(key, out string preferredPackValue))
                        {
                            provenanceByKey.TryGetValue(key, out TranslationProvenanceEntry preferredPackSource);
                            preferredCandidates.Add(new PreferredTranslationCandidate
                            {
                                Value = preferredPackValue,
                                Provenance = preferredPackSource
                            });
                        }
                        string preferredGlobalValue = null;
                        TranslationProvenanceEntry preferredGlobalSource = null;
                        if ((GlobalPrimaryDefDict.TryGetValue(globalKey, out preferredGlobalValue) ||
                             GlobalPrimaryDefDict.TryGetValue(globalKeyGen, out preferredGlobalValue)) &&
                            (GlobalPrimaryDefSourceDict.TryGetValue(globalKey, out preferredGlobalSource) ||
                             GlobalPrimaryDefSourceDict.TryGetValue(globalKeyGen, out preferredGlobalSource)) &&
                            IsShareablePreferredSource(preferredGlobalSource))
                        {
                            preferredCandidates.Add(new PreferredTranslationCandidate
                            {
                                Value = preferredGlobalValue,
                                Provenance = preferredGlobalSource
                            });
                        }
                        if (selfDict != null && selfDict.TryGetValue(key, out string preferredNativeValue))
                        {
                            TranslationProvenanceEntry preferredNativeSource = null;
                            if (modSelfTargetSources.TryGetValue(defType, out Dictionary<string, TranslationProvenanceEntry> preferredNativeSources))
                                preferredNativeSources.TryGetValue(key, out preferredNativeSource);
                            preferredCandidates.Add(new PreferredTranslationCandidate
                            {
                                Value = preferredNativeValue,
                                Provenance = preferredNativeSource
                            });
                        }

                        if (TryApplyPreferredTargetTranslation(
                                mod,
                                key,
                                preferredSourceText,
                                preferredCandidates,
                                finalData,
                                provenanceByKey,
                                out bool usedPreferredExisting))
                        {
                            AddExistingTranslationPolicyWorkItem(
                                workItems,
                                usedPreferredExisting,
                                key,
                                preferredSourceText,
                                sourceFile);
                            continue;
                        }
                    }


                    if (!pureAiWorkspace && selfDict != null && selfDict.TryGetValue(key, out string selfVal)
                              && !string.IsNullOrWhiteSpace(selfVal))
                    {
                        TranslationProvenanceEntry selfSource = null;
                        if (modSelfTargetSources.TryGetValue(defType, out Dictionary<string, TranslationProvenanceEntry> selfSourceDict))
                            selfSourceDict.TryGetValue(key, out selfSource);

                        string sourceText = engDict != null && engDict.TryGetValue(key, out string selfSourceText)
                            ? selfSourceText
                            : "";
                        NativeTargetUseResult nativeResult = TryUseNativeTargetTranslation(
                            mod,
                            TranslationPolicy.TranslationPolicyBucket.DefInjected,
                            defType,
                            key,
                            selfVal,
                            sourceText,
                            !string.IsNullOrWhiteSpace(sourceFile)
                                ? sourceFile
                                : selfSource != null ? selfSource.SourceFile : "",
                            finalData,
                            out string nativeTranslationInput);
                        if (nativeResult == NativeTargetUseResult.HardDenied)
                            continue;

                        if (nativeResult == NativeTargetUseResult.Accepted)
                        {
                            provenanceByKey[key] = selfSource != null
                                ? CloneProvenance(selfSource, finalData[key])
                                : CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, sourceFile, targetFolder, finalData[key]);
                        }
                        else
                        {
                            string input = string.IsNullOrWhiteSpace(nativeTranslationInput)
                                ? sourceText
                                : nativeTranslationInput;
                            string workSource = string.IsNullOrWhiteSpace(sourceText) ? input : sourceText;
                            workItems.Add(CreateTranslationWorkItem(
                                key,
                                input,
                                workSource,
                                !string.IsNullOrWhiteSpace(sourceFile)
                                    ? sourceFile
                                    : selfSource != null ? selfSource.SourceFile : "",
                                CreateProvenance(
                                    ProvenanceKindAI,
                                    mod.PackageId,
                                    mod.Name,
                                    !string.IsNullOrWhiteSpace(sourceFile)
                                        ? sourceFile
                                        : selfSource != null ? selfSource.SourceFile : "",
                                    "English",
                                    "")));
                        }
                    }


                    else if (!pureAiWorkspace && packDict.TryGetValue(key, out string packVal))
                    {
                        string sourceText = engDict != null && engDict.TryGetValue(key, out string packSourceVal)
                            ? packSourceVal
                            : "";
                        bool usedExistingValue;
                        string translationInput;
                        bool setValue = TryUseExistingTranslation(
                            finalData,
                            key,
                            packVal,
                            sourceText,
                            out usedExistingValue,
                            out translationInput);
                        if (setValue)
                        {
                            provenanceByKey[key] = usedExistingValue
                                ? GetFileEntryProvenance(packLangRoot, mod.PackageId, targetFile, key, finalData[key])
                                : CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, sourceFile, targetFolder, finalData[key]);
                            AddExistingTranslationPolicyWorkItem(
                                workItems,
                                usedExistingValue,
                                key,
                                sourceText,
                                sourceFile);
                        }
                        else
                        {
                            workItems.Add(CreateTranslationWorkItem(
                                key,
                                translationInput,
                                sourceText,
                                sourceFile,
                                CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceFile, "English", "")));
                        }
                    }

                    else if (!pureAiWorkspace &&
                             (GlobalPrimaryDefDict.TryGetValue(globalKey, out string pVal)
                             || GlobalPrimaryDefDict.TryGetValue(globalKeyGen, out pVal)))
                    {
                        string sourceText = engDict != null && engDict.TryGetValue(key, out string globalSourceVal)
                            ? globalSourceVal
                            : "";
                        bool usedExistingValue;
                        string translationInput;
                        bool setValue = TryUseExistingTranslation(
                            finalData,
                            key,
                            pVal,
                            sourceText,
                            out usedExistingValue,
                            out translationInput);
                        if (setValue)
                        {
                            TranslationProvenanceEntry sourceInfo = null;
                            if (!GlobalPrimaryDefSourceDict.TryGetValue(globalKey, out sourceInfo))
                                GlobalPrimaryDefSourceDict.TryGetValue(globalKeyGen, out sourceInfo);
                            provenanceByKey[key] = usedExistingValue
                                ? CloneProvenance(sourceInfo, finalData[key])
                                : CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, sourceFile, targetFolder, finalData[key]);
                            AddExistingTranslationPolicyWorkItem(
                                workItems,
                                usedExistingValue,
                                key,
                                sourceText,
                                sourceFile);
                        }
                        else
                        {
                            workItems.Add(CreateTranslationWorkItem(
                                key,
                                translationInput,
                                sourceText,
                                sourceFile,
                                CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceFile, "English", "")));
                        }
                    }

                    else if (!pureAiWorkspace &&
                             modSelfSecondaryLang.TryGetValue(defType, out var secDict)
                              && secDict.TryGetValue(key, out string secVal)
                              && !string.IsNullOrEmpty(secondaryTag))
                    {
                        string sourceText = engDict != null && engDict.TryGetValue(key, out string secondarySourceVal)
                            ? secondarySourceVal
                            : "";
                        TranslationProvenanceEntry secondarySource = null;
                        if (modSelfSecondarySources.TryGetValue(defType, out Dictionary<string, TranslationProvenanceEntry> secSourceDict))
                            secSourceDict.TryGetValue(key, out secondarySource);
                        workItems.Add(CreateTranslationWorkItem(
                            key,
                            PrepareSecondaryTranslationSource(secVal, sourceText),
                            sourceText,
                            !string.IsNullOrWhiteSpace(sourceFile)
                                ? sourceFile
                                : secondarySource != null ? secondarySource.SourceFile : "",
                            CreateProvenance(
                                ProvenanceKindAIFromSecondary,
                                secondarySource != null ? secondarySource.SourcePackageId : mod.PackageId,
                                secondarySource != null ? secondarySource.SourceModName : mod.Name,
                                secondarySource != null ? secondarySource.SourceFile : "",
                                secondarySource != null ? secondarySource.SourceLanguage : "",
                                "",
                                secondarySource != null ? secondarySource.SourceKind : "")));
                    }

                    else if (!pureAiWorkspace &&
                             ((GlobalSecondaryDefDict.TryGetValue(globalKey, out string sVal)
                              || GlobalSecondaryDefDict.TryGetValue(globalKeyGen, out sVal))
                             && !string.IsNullOrEmpty(secondaryTag)))
                    {
                        string sourceText = engDict != null && engDict.TryGetValue(key, out string globalSecondarySourceVal)
                            ? globalSecondarySourceVal
                            : "";
                        TranslationProvenanceEntry secondarySource = null;
                        if (!GlobalSecondaryDefSourceDict.TryGetValue(globalKey, out secondarySource))
                            GlobalSecondaryDefSourceDict.TryGetValue(globalKeyGen, out secondarySource);
                        workItems.Add(CreateTranslationWorkItem(
                            key,
                            PrepareSecondaryTranslationSource(sVal, sourceText),
                            sourceText,
                            !string.IsNullOrWhiteSpace(sourceFile)
                                ? sourceFile
                                : secondarySource != null ? secondarySource.SourceFile : "",
                            CreateProvenance(
                                ProvenanceKindAIFromSecondary,
                                secondarySource != null ? secondarySource.SourcePackageId : mod.PackageId,
                                secondarySource != null ? secondarySource.SourceModName : mod.Name,
                                secondarySource != null ? secondarySource.SourceFile : "",
                                secondarySource != null ? secondarySource.SourceLanguage : "",
                                "",
                                secondarySource != null ? secondarySource.SourceKind : "")));
                    }

                    else if (engDict != null && engDict.TryGetValue(key, out string engVal)
                             && !string.IsNullOrEmpty(engVal))
                    {
                        if (!pureAiWorkspace && LanguageDetector.LooksLikeTargetLanguage(engVal, settings.TargetLang))
                        {
                            NativeTargetUseResult nativeResult = TryUseNativeTargetTranslation(
                                mod,
                                TranslationPolicy.TranslationPolicyBucket.DefInjected,
                                defType,
                                key,
                                engVal,
                                engVal,
                                sourceFile,
                                finalData,
                                out string nativeTranslationInput);
                            if (nativeResult == NativeTargetUseResult.HardDenied)
                                continue;

                            if (nativeResult == NativeTargetUseResult.Accepted)
                            {
                                provenanceByKey[key] = CreateProvenance(
                                    ProvenanceKindModNativeTarget,
                                    mod.PackageId,
                                    mod.Name,
                                    sourceFile,
                                    targetFolder,
                                    finalData[key]);
                            }
                            else
                            {
                                string input = string.IsNullOrWhiteSpace(nativeTranslationInput)
                                    ? engVal
                                    : nativeTranslationInput;
                                workItems.Add(CreateTranslationWorkItem(
                                    key,
                                    input,
                                    engVal,
                                    sourceFile,
                                    CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceFile, "English", "")));
                            }
                        }
                        else
                        {
                            workItems.Add(CreateTranslationWorkItem(
                                key,
                                engVal,
                                engVal,
                                sourceFile,
                                CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceFile, "English", "")));
                        }
                    }
                }

                CheckpointGeneratedTranslationProgress(
                    mod,
                    "DefInjected",
                    defType,
                    targetFile,
                    packLangRoot,
                    finalData,
                    provenanceByKey);
                if (!deferDefPolicy)
                {
                    try
                    {
                        workItems = await FilterTranslationWorkItemsByPolicyAsync(
                            mod,
                            TranslationPolicy.TranslationPolicyBucket.DefInjected,
                            defType,
                            targetFile,
                            workItems,
                            finalData,
                            provenanceByKey);
                        aiTranslatedCount += await TranslateDefWorkItemsAsync(
                            mod,
                            defType,
                            targetFile,
                            packLangRoot,
                            workItems,
                            finalData,
                            provenanceByKey);
                    }
                    finally
                    {
                        CheckpointGeneratedTranslationProgress(
                            mod,
                            "DefInjected",
                            defType,
                            targetFile,
                            packLangRoot,
                            finalData,
                            provenanceByKey);
                    }
                    continue;
                }

                DefTranslationPolicyContext context = CreateDefTranslationPolicyContext(
                    mod,
                    defType,
                    targetFile,
                    workItems,
                    finalData,
                    provenanceByKey);
                if (context.AmbiguousCandidates.Count == 0)
                {
                    try
                    {
                        context.WorkItems = ApplyDefTranslationPolicyContext(
                            mod,
                            context,
                            new Dictionary<string, TranslationPolicy.TranslationPolicyAgentCandidateOutcome>(StringComparer.Ordinal));
                        aiTranslatedCount += await TranslateDefWorkItemsAsync(
                            mod,
                            context.DefType,
                            context.TargetFile,
                            packLangRoot,
                            context.WorkItems,
                            context.FinalData,
                            context.ProvenanceByKey);
                    }
                    finally
                    {
                        CheckpointGeneratedTranslationProgress(
                            mod,
                            "DefInjected",
                            context.DefType,
                            context.TargetFile,
                            packLangRoot,
                            context.FinalData,
                            context.ProvenanceByKey);
                    }
                    if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested)
                        return aiTranslatedCount;
                    continue;
                }

                deferredDefContexts.Add(context);
            }

            if (!deferDefPolicy)
                return aiTranslatedCount;

            if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested)
            {
                CheckpointDeferredDefProgress(mod, packLangRoot, deferredDefContexts);
                return aiTranslatedCount;
            }

            if (deferredDefContexts.Count == 0)
            {
                AutoTranslatorMod.Settings.SubProgress = 1f;
                return aiTranslatedCount;
            }

            List<TranslationPolicy.TranslationPolicyCandidate> allAmbiguousCandidates =
                deferredDefContexts
                    .SelectMany(context => context.AmbiguousCandidates ??
                        Enumerable.Empty<TranslationPolicy.TranslationPolicyCandidate>())
                    .ToList();
            Dictionary<string, TranslationPolicy.TranslationPolicyAgentCandidateOutcome> agentOutcomes;
            try
            {
                agentOutcomes = allAmbiguousCandidates.Count == 0
                    ? new Dictionary<string, TranslationPolicy.TranslationPolicyAgentCandidateOutcome>(StringComparer.Ordinal)
                    : await TranslationPolicyAgentCoordinator.ResolveCandidatesAsync(
                        mod != null ? mod.PackageId : string.Empty,
                        allAmbiguousCandidates);
            }
            catch
            {
                CheckpointDeferredDefProgress(mod, packLangRoot, deferredDefContexts);
                throw;
            }

            for (int contextIndex = 0; contextIndex < deferredDefContexts.Count; contextIndex++)
            {
                if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested)
                {
                    CheckpointDeferredDefProgress(
                        mod,
                        packLangRoot,
                        deferredDefContexts.Skip(contextIndex));
                    return aiTranslatedCount;
                }

                DefTranslationPolicyContext context = deferredDefContexts[contextIndex];
                try
                {
                    context.WorkItems = ApplyDefTranslationPolicyContext(mod, context, agentOutcomes);
                    AutoTranslatorMod.Settings.SubProgress = 0.5f +
                        (0.5f * (contextIndex + 1) / Math.Max(1, deferredDefContexts.Count));
                    aiTranslatedCount += await TranslateDefWorkItemsAsync(
                        mod,
                        context.DefType,
                        context.TargetFile,
                        packLangRoot,
                        context.WorkItems,
                        context.FinalData,
                        context.ProvenanceByKey);
                }
                finally
                {
                    CheckpointGeneratedTranslationProgress(
                        mod,
                        "DefInjected",
                        context.DefType,
                        context.TargetFile,
                        packLangRoot,
                        context.FinalData,
                        context.ProvenanceByKey);
                }
            }

            return aiTranslatedCount;
        }

        private static DefTranslationPolicyContext CreateDefTranslationPolicyContext(
            ModMetaData mod,
            string defType,
            string targetFile,
            List<TranslationWorkItem> workItems,
            Dictionary<string, string> finalData,
            Dictionary<string, TranslationProvenanceEntry> provenanceByKey)
        {
            List<TranslationWorkItem> safeItems = (workItems ?? new List<TranslationWorkItem>())
                .Where(item => item != null)
                .ToList();
            DefTranslationPolicyContext context = new DefTranslationPolicyContext
            {
                DefType = defType ?? string.Empty,
                TargetFile = targetFile ?? string.Empty,
                FinalData = finalData ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ProvenanceByKey = provenanceByKey ??
                    new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase),
                WorkItems = safeItems,
                Evaluations = new List<TranslationPolicyWorkEvaluation>(safeItems.Count),
                AmbiguousCandidates = new List<TranslationPolicy.TranslationPolicyCandidate>(),
                LocalAllowCount = 0,
                LocalDenyCount = 0
            };

            foreach (TranslationWorkItem item in safeItems)
            {
                TranslationPolicy.TranslationPolicyCandidate candidate = new TranslationPolicy.TranslationPolicyCandidate
                {
                    PackageId = mod != null ? mod.PackageId ?? string.Empty : string.Empty,
                    ModName = mod != null ? mod.Name ?? string.Empty : string.Empty,
                    SourceFile = GetTranslationPolicyRelativeSourceFile(mod, item.SourceFile),
                    Bucket = TranslationPolicy.TranslationPolicyBucket.DefInjected,
                    DefType = defType ?? string.Empty,
                    KeyOrPath = item.Key ?? string.Empty,
                    FieldName = GetTranslationPolicyTerminalField(item.Key),
                    SourceText = item.PolicySourceText ?? string.Empty,
                    DeclaringAssembly = string.Empty,
                    SchemaFingerprint = string.Empty
                };
                candidate.CandidateId = TranslationPolicy.TranslationPolicyIdentity.CreateCandidateId(candidate);

                TranslationPolicy.TranslationPolicyClassification classification =
                    TranslationPolicy.TranslationPolicyClassifier.Classify(candidate);
                context.Evaluations.Add(new TranslationPolicyWorkEvaluation
                {
                    WorkItem = item,
                    Candidate = candidate,
                    Classification = classification
                });

                if (classification.Decision == TranslationPolicy.TranslationPolicyDecision.HardAllow)
                    context.LocalAllowCount++;
                else if (classification.Decision == TranslationPolicy.TranslationPolicyDecision.HardDeny)
                    context.LocalDenyCount++;
                else if (classification.Decision == TranslationPolicy.TranslationPolicyDecision.Ambiguous)
                    context.AmbiguousCandidates.Add(candidate);
            }

            TranslationPolicyAgentCoordinator.RecordLocalOutcomes(
                context.LocalAllowCount,
                context.LocalDenyCount);
            return context;
        }

        private static List<TranslationWorkItem> ApplyDefTranslationPolicyContext(
            ModMetaData mod,
            DefTranslationPolicyContext context,
            Dictionary<string, TranslationPolicy.TranslationPolicyAgentCandidateOutcome> agentOutcomes)
        {
            if (context == null) return new List<TranslationWorkItem>();

            List<TranslationWorkItem> allowed = new List<TranslationWorkItem>(context.WorkItems ??
                new List<TranslationWorkItem>());
            allowed.Clear();
            int agentAllowCount = 0;
            int blockedCount = 0;

            foreach (TranslationPolicyWorkEvaluation evaluation in context.Evaluations ??
                new List<TranslationPolicyWorkEvaluation>())
            {
                TranslationPolicy.TranslationPolicyAgentCandidateOutcome agentOutcome = null;
                if (evaluation.Classification.Decision == TranslationPolicy.TranslationPolicyDecision.Ambiguous &&
                    agentOutcomes != null)
                {
                    agentOutcomes.TryGetValue(evaluation.Candidate.CandidateId, out agentOutcome);
                }
                if (evaluation.Classification.Decision == TranslationPolicy.TranslationPolicyDecision.Ambiguous &&
                    agentOutcome == null)
                {
                    agentOutcome = CreateMissingPolicyAgentOutcome();
                }
                TranslationPolicy.TranslationPolicyAgentDecision agentDecision = agentOutcome != null
                    ? agentOutcome.Decision
                    : TranslationPolicy.TranslationPolicyAgentDecision.Unresolved;

                TranslationPolicy.TranslationPolicyApplicationDecision application =
                    TranslationPolicy.TranslationPolicyApplication.Resolve(
                        evaluation.Classification.Decision,
                        agentDecision,
                        evaluation.WorkItem.IsPolicyOnlyExistingTranslation);
                if (application != TranslationPolicy.TranslationPolicyApplicationDecision.Remove)
                {
                    if (application == TranslationPolicy.TranslationPolicyApplicationDecision.Translate &&
                        evaluation.Classification.Decision == TranslationPolicy.TranslationPolicyDecision.Ambiguous)
                    {
                        agentAllowCount++;
                    }
                    allowed.Add(evaluation.WorkItem);
                    continue;
                }

                blockedCount++;
                if (context.FinalData != null) context.FinalData.Remove(evaluation.WorkItem.Key);
                if (context.ProvenanceByKey != null) context.ProvenanceByKey.Remove(evaluation.WorkItem.Key);
                RecordPolicyUnresolvedIfNeeded(
                    mod,
                    "DefInjected",
                    context.DefType,
                    context.TargetFile,
                    evaluation,
                    agentOutcome);
            }

            if (context.LocalDenyCount > 0 ||
                (context.AmbiguousCandidates != null && context.AmbiguousCandidates.Count > 0))
            {
                AutoTranslatorSettings.AddLog(AutoTranslatorAPI.TranslateText("ATC_PolicyAgent_BatchSummary",
                    mod != null ? mod.Name : string.Empty,
                    context.LocalAllowCount,
                    context.LocalDenyCount,
                    agentAllowCount,
                    blockedCount));
            }

            return allowed;
        }

        private static async Task<int> TranslateDefWorkItemsAsync(
            ModMetaData mod,
            string defType,
            string targetFile,
            string packLangRoot,
            List<TranslationWorkItem> workItems,
            Dictionary<string, string> finalData,
            Dictionary<string, TranslationProvenanceEntry> provenanceByKey)
        {
            if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested)
            {
                CheckpointGeneratedTranslationProgress(
                    mod,
                    "DefInjected",
                    defType,
                    targetFile,
                    packLangRoot,
                    finalData,
                    provenanceByKey);
                return 0;
            }

            int aiTranslatedCount = 0;
            try
            {
                List<TranslationWorkItem> translationItems = (workItems ?? new List<TranslationWorkItem>())
                .Where(item => item != null && !item.IsPolicyOnlyExistingTranslation)
                .ToList();
            translationItems = ApplyKeepOriginalDecisions(
                mod,
                "DefInjected",
                defType,
                translationItems,
                finalData,
                provenanceByKey);

            if (translationItems.Count > 0)
            {
                AutoTranslatorSettings.AddLog("🔌 " + AutoTranslatorAPI.TranslateText(
                    "ATC_Log_FoundMissing",
                    defType,
                    translationItems.Count));
                TerminologyRuntime.ObserveTranslationInputs(
                    mod.PackageId,
                    "DefInjected",
                    defType,
                    translationItems.Select(item => new KeyValuePair<string, string>(item.Key, item.TranslationInput)));
                await TerminologyRuntime.ResolveHighValueCandidatesAsync(mod.PackageId);
                if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested)
                    return 0;
                List<string> translationInputs = translationItems
                    .Select(item => item.TranslationInput)
                    .ToList();
                List<TranslationBatchItemResult> results = await SafeTranslateBatch(
                    translationInputs,
                    $"{mod.Name} / Defs: {defType}",
                    mod.PackageId);
                bool interruptedAfterBatch =
                    AutoTranslatorSettings.IsCancellationRequested ||
                    AutoTranslatorSettings.IsSkipCurrentRequested;

                if (results != null)
                {
                    int acceptedCount = 0;
                    for (int i = 0; i < translationItems.Count; i++)
                    {
                        TranslationWorkItem item = translationItems[i];
                        TranslationBatchItemResult batchResult = i < results.Count ? results[i] : null;
                        if (batchResult == null || !batchResult.IsSuccess)
                        {
                            if (!interruptedAfterBatch)
                            {
                                RecordUnresolvedTranslation(
                                    mod,
                                    "DefInjected",
                                    defType,
                                    targetFile,
                                    item,
                                    batchResult != null ? batchResult.FailureReason : TranslationUnresolvedReasons.ApiFailure,
                                    batchResult != null ? batchResult.Detail : "No batch result was produced.");
                            }
                            continue;
                        }

                        string value = batchResult.Value;
                        if (!TryAcceptTranslatedValue(
                                value,
                                item.TranslationInput,
                                out value,
                                out string failureReason,
                                out string failureDetail))
                        {
                            RecordUnresolvedTranslation(
                                mod,
                                "DefInjected",
                                defType,
                                targetFile,
                                item,
                                failureReason,
                                failureDetail);
                            continue;
                        }

                        finalData[item.Key] = value;
                        provenanceByKey[item.Key] = item.Provenance != null
                            ? CloneProvenance(item.Provenance, value)
                            : CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, item.SourceFile, "English", value);
                        TranslationUnresolvedManager.ResolveMatching(
                            mod.PackageId,
                            "DefInjected",
                            defType,
                            item.Key,
                            string.IsNullOrWhiteSpace(item.PolicySourceText)
                                ? item.TranslationInput
                                : item.PolicySourceText,
                            AutoTranslatorMod.Settings.TargetLang.ToString());
                        acceptedCount++;
                    }

                    AutoTranslatorSettings.AddLog("✨ " + AutoTranslatorAPI.TranslateText(
                        "ATC_Log_AIFinish",
                        defType));
                    aiTranslatedCount += acceptedCount;
                }
                else
                {
                    AutoTranslatorSettings.AddLog("⚠️ " + AutoTranslatorAPI.TranslateText(
                        "ATC_Log_AIFail",
                        defType));
                }
            }
            else
            {
                AutoTranslatorSettings.AddLog("✅ " + AutoTranslatorAPI.TranslateText(
                    "ATC_Log_NoMissing",
                    $"Def:{defType}"));
            }

                return aiTranslatedCount;
            }
            finally
            {
                CheckpointGeneratedTranslationProgress(
                    mod,
                    "DefInjected",
                    defType,
                    targetFile,
                    packLangRoot,
                    finalData,
                    provenanceByKey);
            }
        }

        private static void CheckpointDeferredDefProgress(
            ModMetaData mod,
            string packLangRoot,
            IEnumerable<DefTranslationPolicyContext> contexts)
        {
            foreach (DefTranslationPolicyContext context in
                contexts ?? Enumerable.Empty<DefTranslationPolicyContext>())
            {
                if (context == null) continue;
                CheckpointGeneratedTranslationProgress(
                    mod,
                    "DefInjected",
                    context.DefType,
                    context.TargetFile,
                    packLangRoot,
                    context.FinalData,
                    context.ProvenanceByKey);
            }
        }

        private static bool CheckpointGeneratedTranslationProgress(
            ModMetaData mod,
            string bucket,
            string defType,
            string targetFile,
            string packLangRoot,
            Dictionary<string, string> finalData,
            Dictionary<string, TranslationProvenanceEntry> provenanceByKey)
        {
            if (finalData == null)
                return true;
            if (finalData.Count == 0 && !File.Exists(targetFile))
                return true;
            if (SaveGeneratedTranslationFile(
                    mod,
                    targetFile,
                    packLangRoot,
                    finalData ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    provenanceByKey ?? new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase)))
                return true;

            RecordGeneratedFileSaveFailure(
                mod,
                bucket,
                defType,
                targetFile,
                "Completed translation progress could not be checkpointed before the task stopped.");
            return false;
        }

        private static void DeleteSupersededGeneratedKeyedFiles(
            string keyedDirectory,
            ModMetaData mod,
            IEnumerable<string> sourceFiles,
            string canonicalFile)
        {
            if (mod == null || string.IsNullOrWhiteSpace(keyedDirectory) || !Directory.Exists(keyedDirectory)) return;

            HashSet<string> ownedNames = TranslationGeneratedOutputOwnership.BuildKeyedFileNameSet(
                mod.PackageId,
                sourceFiles);
            ownedNames.Remove(TranslationGeneratedOutputOwnership.GetCanonicalFileName(mod.PackageId));

            foreach (string fileName in ownedNames)
            {
                string oldFile = Path.Combine(keyedDirectory, fileName);
                if (!File.Exists(oldFile) || string.Equals(
                        Path.GetFullPath(oldFile),
                        Path.GetFullPath(canonicalFile),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.SetAttributes(oldFile, FileAttributes.Normal);
                    File.Delete(oldFile);
                    NotifyTranslationFileChanged(oldFile);
                }
                catch (Exception ex)
                {
                    AutoTranslatorSettings.AddErrorLog(
                        "[AutoTranslationCore] Could not remove superseded Keyed file: " +
                        GetShortPath(oldFile));
                    Log.Warning("[AutoTranslationCore] Superseded Keyed cleanup failed: " + ex.Message);
                    RecordGeneratedFileSaveFailure(
                        mod,
                        "Keyed",
                        string.Empty,
                        oldFile,
                        "The canonical Keyed file was saved, but a superseded duplicate could not be removed.");
                }
            }
        }

        private static Dictionary<string, string> LoadExistingGeneratedKeyedData(
            string keyedDirectory,
            string canonicalFile,
            string packageId,
            IEnumerable<string> sourceFiles,
            TargetLanguage targetLanguage,
            out Dictionary<string, string> sourceFileByKey)
        {
            Dictionary<string, string> merged =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sourceFileByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            List<string> candidateFiles = (sourceFiles ?? Enumerable.Empty<string>())
                .Select(source => Path.Combine(
                    keyedDirectory,
                    TranslationGeneratedOutputOwnership.GetKeyedFileName(packageId, source)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToList();
            if (File.Exists(canonicalFile)) candidateFiles.Add(canonicalFile);

            foreach (string file in candidateFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (KeyValuePair<string, string> pair in LoadXmlFileToDict(file, targetLanguage))
                {
                    merged[pair.Key] = pair.Value;
                    sourceFileByKey[pair.Key] = file;
                }
            }

            return merged;
        }

        private static void RecordGeneratedFileSaveFailure(
            ModMetaData mod,
            string bucket,
            string defType,
            string targetFile,
            string detail)
        {
            TranslationUnresolvedManager.MarkPackageScanIncomplete(
                mod != null ? mod.PackageId : string.Empty,
                AutoTranslatorMod.Settings.TargetLang.ToString());
            TranslationUnresolvedManager.RecordFailure(new TranslationUnresolvedEntry
            {
                TargetLanguage = AutoTranslatorMod.Settings.TargetLang.ToString(),
                PackageId = mod != null ? mod.PackageId : string.Empty,
                ModName = mod != null ? mod.Name : string.Empty,
                Bucket = bucket ?? string.Empty,
                DefType = defType ?? string.Empty,
                Key = "__ATC_FILE_SAVE__",
                SourceText = targetFile ?? string.Empty,
                SourceFile = targetFile ?? string.Empty,
                TargetFile = targetFile ?? string.Empty,
                Reason = TranslationUnresolvedReasons.SaveFailure,
                Detail = detail ?? string.Empty,
                Attempts = 1,
                State = TranslationUnresolvedStates.Pending
            });
        }

        private static void RecordSourceProcessingFailure(
            ModMetaData mod,
            string bucket,
            string defType,
            string sourceFile,
            string detail)
        {
            TranslationUnresolvedManager.RecordFailure(new TranslationUnresolvedEntry
            {
                TargetLanguage = AutoTranslatorMod.Settings.TargetLang.ToString(),
                PackageId = mod != null ? mod.PackageId : string.Empty,
                ModName = mod != null ? mod.Name : string.Empty,
                Bucket = bucket ?? string.Empty,
                DefType = defType ?? string.Empty,
                Key = "__ATC_SOURCE_FAILURE__",
                SourceText = sourceFile ?? string.Empty,
                SourceFile = sourceFile ?? string.Empty,
                TargetFile = string.Empty,
                Reason = TranslationUnresolvedReasons.SourceFailure,
                Detail = detail ?? string.Empty,
                Attempts = 1,
                State = TranslationUnresolvedStates.Pending
            });
        }

        private static bool SaveGeneratedTranslationFile(
            ModMetaData mod,
            string targetFile,
            string packLangRoot,
            Dictionary<string, string> finalData,
            Dictionary<string, TranslationProvenanceEntry> provenanceByKey)
        {
            string modName = mod != null ? mod.Name : "<unknown mod>";
            string shortPath = GetShortPath(targetFile);
            if (!TranslationXmlAtomicFileStore.TrySave(
                    () => SaveXml(targetFile, finalData),
                    ex =>
                    {
                        AutoTranslatorSettings.AddErrorLog(
                            $"[AutoTranslationCore] Could not save translation file for {modName}: {shortPath}");
                        Log.Warning($"[AutoTranslationCore] Translation file save failed for {modName} ({shortPath}): {ex.Message}");
                    }))
            {
                return false;
            }

            if (!SaveProvenanceForFile(
                    packLangRoot,
                    mod != null ? mod.PackageId : string.Empty,
                    targetFile,
                    finalData,
                    provenanceByKey))
            {
                AutoTranslatorSettings.AddErrorLog(
                    $"[AutoTranslationCore] Could not save translation provenance for {modName}: {shortPath}");
                return false;
            }
            return true;
        }


        // 這個方法負責處理 Safe翻譯Batch 相關流程。
        // EN: This method handles safe translate batch.
        private static async Task<List<TranslationBatchItemResult>> SafeTranslateBatch(
            List<string> texts,
            string contextInfo,
            string packageId,
            string requestPurpose = "translation")
        {
            if (texts == null || texts.Count == 0) return new List<TranslationBatchItemResult>();

            var uniqueTexts = texts.Select(text => text ?? string.Empty).Distinct().ToList();
            var translatedDict = new Dictionary<string, TranslationBatchItemResult>();
            var uncachedTexts = new List<string>();
            foreach (string source in uniqueTexts)
            {
                if (TryUseCachedTranslation(packageId, source, out TranslationBatchItemResult cached))
                    translatedDict[source] = cached;
                else
                    uncachedTexts.Add(source);
            }

            int chunkSize = Math.Max(1, AutoTranslatorAPI.GetCurrentRuntimeProfile().BatchSize);
            int maxConcurrency = Math.Max(1, AutoTranslatorMod.Settings.MaxThreads);
            List<Task> tasks = new List<Task>();

            using (SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrency))
            {
                for (int i = 0; i < uncachedTexts.Count; i += chunkSize)
                {
                    int chunkIndex = i;
                    int currentChunkSize = Math.Min(chunkSize, uncachedTexts.Count - chunkIndex);
                    List<string> chunk = SafeSlice(uncachedTexts, chunkIndex, currentChunkSize);
                    if (chunk.Count == 0) continue;

                    tasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            await TranslationBatchFaultGuard.RunChunkAsync(
                                chunk,
                                translatedDict,
                                TranslationUnresolvedReasons.ApiFailure,
                                async () =>
                                {
                                    if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested) return;

                            // TranslateBatchAsync already owns network and format retries. Retrying the
                            // same chunk again here multiplied worst-case waits into tens of minutes.
                            List<string> chunkRes = await AutoTranslatorAPI.TranslateBatchAsync(
                                chunk,
                                 suppressFinalParseError: true,
                                 packageId: packageId,
                                 requestScope: contextInfo + " / chunk " + chunkIndex,
                                 requestPurpose: requestPurpose,
                                 reportFailureToUser: false);

                            if (chunkRes == null || chunkRes.Count != chunk.Count)
                            {
#if false
                                AutoTranslatorSettings.AddLog("🔄 " + AutoTranslatorAPI.TranslateText("ATC_Log_ApiFallback"));
                                AutoTranslatorSettings.AddErrorLog("❌ " + AutoTranslatorAPI.TranslateText("ATC_LogError_ApiCritical", contextInfo));
#endif
                                string failureDetail = AutoTranslatorAPI.DescribeLastTranslationFailure(
                                    contextInfo,
                                    contextInfo + " / chunk " + chunkIndex,
                                    out string aggregationKey,
                                    out int affectedItems);
                                AutoTranslatorSettings.AddAggregatedErrorLog(
                                    aggregationKey,
                                    failureDetail,
                                    failureDetail,
                                    affectedItems > 0 ? affectedItems : chunk.Count);
                                string reason = chunkRes == null
                                    ? TranslationUnresolvedReasons.ApiFailure
                                    : TranslationUnresolvedReasons.MalformedResponse;
                                string detail = chunkRes == null
                                    ? "The translation provider returned no usable batch response."
                                    : $"The translation provider returned {chunkRes.Count} results for {chunk.Count} inputs.";
                                lock (translatedDict)
                                {
                                    foreach (string source in chunk)
                                    {
                                        translatedDict[source] = new TranslationBatchItemResult
                                        {
                                            FailureReason = reason,
                                            Detail = detail
                                        };
                                    }
                                }
                                return;
                            }

                            List<string> originalResults = new List<string>(chunkRes);
                            chunkRes = ValidateTranslationBatchMechanically(chunk, chunkRes);

                            if (chunkRes == null || chunkRes.Count != chunk.Count)
                            {
                                lock (translatedDict)
                                {
                                    foreach (string source in chunk)
                                    {
                                        translatedDict[source] = new TranslationBatchItemResult
                                        {
                                            FailureReason = TranslationUnresolvedReasons.MalformedResponse,
                                            Detail = "The validation retry returned an incomplete batch."
                                        };
                                    }
                                }
                                return;
                            }

                            List<KeyValuePair<string, string>> acceptedForCache =
                                new List<KeyValuePair<string, string>>();
                            lock (translatedDict)
                            {
                                for (int j = 0; j < chunk.Count; j++)
                                {
                                    string value = chunkRes[j];
                                    if (value != null)
                                    {
                                        translatedDict[chunk[j]] = new TranslationBatchItemResult { Value = value };
                                        acceptedForCache.Add(new KeyValuePair<string, string>(chunk[j], value));
                                        continue;
                                    }

                                    string ignoredSanitized;
                                    string reason;
                                    string detail;
                                    TryAcceptTranslatedValue(
                                        originalResults[j],
                                        chunk[j],
                                        out ignoredSanitized,
                                        out reason,
                                        out detail);
                                    translatedDict[chunk[j]] = new TranslationBatchItemResult
                                    {
                                        FailureReason = reason,
                                        Detail = detail
                                    };
                                }
                            }
                            CacheValidatedTranslations(packageId, acceptedForCache);
                                },
                                ex => Log.Warning(
                                    "[AutoTranslationCore] Translation batch task failed (" +
                                    contextInfo + "): " + ex.Message));
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception ex)
                {
                    TranslationBatchFaultGuard.RecordMissingFailures(
                        uniqueTexts,
                        translatedDict,
                        TranslationUnresolvedReasons.ApiFailure,
                        "Translation batch coordination failed: " + ex.Message);
                    Log.Warning("[AutoTranslationCore] Translation batch coordination failed (" +
                        contextInfo + "): " + ex.Message);
                }
            }

            return TranslationBatchFaultGuard.CreateOrderedResults(
                texts,
                translatedDict,
                TranslationUnresolvedReasons.ApiFailure,
                "No translation result was produced for this entry.");
        }

        private static List<string> ValidateTranslationBatchMechanically(
            List<string> sourceTexts,
            List<string> translatedTexts)
        {
            if (sourceTexts == null || translatedTexts == null || sourceTexts.Count != translatedTexts.Count)
                return null;

            List<string> validated = new List<string>(translatedTexts.Count);
            for (int i = 0; i < translatedTexts.Count; i++)
            {
                if (TryAcceptTranslatedValue(
                        translatedTexts[i],
                        sourceTexts[i],
                        out string sanitized,
                        out _,
                        out _))
                {
                    validated.Add(sanitized);
                }
                else
                {
                    validated.Add(null);
                }
            }
            return validated;
        }

        private static TranslationWorkItem CreateTranslationWorkItem(
            string key,
            string translationInput,
            string policySourceText,
            string sourceFile,
            TranslationProvenanceEntry provenance,
            bool isPolicyOnlyExistingTranslation = false)
        {
            return new TranslationWorkItem
            {
                Key = key ?? string.Empty,
                TranslationInput = translationInput ?? string.Empty,
                PolicySourceText = policySourceText ?? string.Empty,
                SourceFile = sourceFile ?? string.Empty,
                Provenance = provenance,
                IsPolicyOnlyExistingTranslation = isPolicyOnlyExistingTranslation
            };
        }

        private static List<TranslationWorkItem> ApplyKeepOriginalDecisions(
            ModMetaData mod,
            string bucket,
            string defType,
            List<TranslationWorkItem> workItems,
            Dictionary<string, string> finalData,
            Dictionary<string, TranslationProvenanceEntry> provenanceByKey)
        {
            List<TranslationWorkItem> remaining = new List<TranslationWorkItem>();
            foreach (TranslationWorkItem item in workItems ?? new List<TranslationWorkItem>())
            {
                if (item == null) continue;
                string sourceText = string.IsNullOrWhiteSpace(item.PolicySourceText)
                    ? item.TranslationInput
                    : item.PolicySourceText;
                if (!TranslationUnresolvedManager.ShouldKeepOriginal(
                        mod != null ? mod.PackageId : string.Empty,
                        bucket,
                        defType,
                        item.Key,
                        sourceText,
                        AutoTranslatorMod.Settings.TargetLang.ToString()))
                {
                    remaining.Add(item);
                    continue;
                }

                if (finalData != null) finalData[item.Key] = sourceText ?? string.Empty;
                if (provenanceByKey != null)
                {
                    provenanceByKey[item.Key] = CreateProvenance(
                        ProvenanceKindModNativeTarget,
                        mod != null ? mod.PackageId : string.Empty,
                        mod != null ? mod.Name : string.Empty,
                        item.SourceFile,
                        "Original",
                        sourceText ?? string.Empty);
                }
            }

            return remaining;
        }

        private static void RecordUnresolvedTranslation(
            ModMetaData mod,
            string bucket,
            string defType,
            string targetFile,
            TranslationWorkItem item,
            string reason,
            string detail)
        {
            if (item == null) return;
            string sourceText = string.IsNullOrWhiteSpace(item.PolicySourceText)
                ? item.TranslationInput
                : item.PolicySourceText;
            TranslationUnresolvedManager.RecordFailure(new TranslationUnresolvedEntry
            {
                TargetLanguage = AutoTranslatorMod.Settings.TargetLang.ToString(),
                PackageId = mod != null ? mod.PackageId : string.Empty,
                ModName = mod != null ? mod.Name : string.Empty,
                Bucket = bucket ?? string.Empty,
                DefType = defType ?? string.Empty,
                Key = item.Key ?? string.Empty,
                SourceText = sourceText ?? string.Empty,
                SourceFile = item.SourceFile ?? string.Empty,
                TargetFile = targetFile ?? string.Empty,
                Reason = string.IsNullOrWhiteSpace(reason) ? TranslationUnresolvedReasons.Unknown : reason,
                Detail = detail ?? string.Empty,
                Attempts = 1,
                State = TranslationUnresolvedStates.Pending
            });
        }

        private static void RecordPolicyUnresolvedIfNeeded(
            ModMetaData mod,
            string bucket,
            string defType,
            string targetFile,
            TranslationPolicyWorkEvaluation evaluation,
            TranslationPolicy.TranslationPolicyAgentCandidateOutcome outcome)
        {
            if (evaluation == null || evaluation.WorkItem == null || outcome == null) return;
            if (evaluation.Classification.Decision != TranslationPolicy.TranslationPolicyDecision.Ambiguous)
                return;
            if (!outcome.ShouldReportUnresolved(evaluation.WorkItem.IsPolicyOnlyExistingTranslation))
                return;

            bool isReview = outcome.Status == TranslationPolicy.TranslationPolicyAgentOutcomeStatus.Classified &&
                outcome.Decision == TranslationPolicy.TranslationPolicyAgentDecision.Review;
            string reason = isReview
                ? TranslationUnresolvedReasons.PolicyReview
                : TranslationUnresolvedReasons.PolicyAgentFailure;
            string detail = isReview
                ? "Agent prediction requested manual review."
                : "Agent prediction could not classify this entry.";
            if (!string.IsNullOrWhiteSpace(outcome.ErrorCode))
                detail += " Error: " + outcome.ErrorCode + ".";
            if (!string.IsNullOrWhiteSpace(outcome.Reason))
                detail += " " + outcome.Reason.Trim();

            RecordUnresolvedTranslation(
                mod,
                bucket,
                defType,
                targetFile,
                evaluation.WorkItem,
                reason,
                detail);
        }

        private static TranslationPolicy.TranslationPolicyAgentCandidateOutcome CreateMissingPolicyAgentOutcome()
        {
            return new TranslationPolicy.TranslationPolicyAgentCandidateOutcome
            {
                Decision = TranslationPolicy.TranslationPolicyAgentDecision.Unresolved,
                Status = TranslationPolicy.TranslationPolicyAgentOutcomeStatus.ProviderFailure,
                ErrorCode = "missing_candidate_outcome",
                Reason = "Agent prediction did not return an outcome for this candidate."
            };
        }

        private static void AddExistingTranslationPolicyWorkItem(
            List<TranslationWorkItem> workItems,
            bool usedExistingValue,
            string key,
            string sourceText,
            string sourceFile)
        {
            if (!usedExistingValue ||
                string.IsNullOrWhiteSpace(sourceText) ||
                !TranslationPolicyAgentCoordinator.IsEnabledForCurrentRun)
            {
                return;
            }

            workItems.Add(CreateTranslationWorkItem(
                key,
                string.Empty,
                sourceText,
                sourceFile,
                null,
                isPolicyOnlyExistingTranslation: true));
        }

        private static async Task<List<TranslationWorkItem>> FilterTranslationWorkItemsByPolicyAsync(
            ModMetaData mod,
            TranslationPolicy.TranslationPolicyBucket bucket,
            string defType,
            string targetFile,
            List<TranslationWorkItem> workItems,
            Dictionary<string, string> finalData,
            Dictionary<string, TranslationProvenanceEntry> provenanceByKey)
        {
            List<TranslationWorkItem> safeItems = (workItems ?? new List<TranslationWorkItem>())
                .Where(item => item != null)
                .ToList();
            if (safeItems.Count == 0 || !TranslationPolicyAgentCoordinator.IsEnabledForCurrentRun)
                return safeItems;

            List<TranslationPolicyWorkEvaluation> evaluations = new List<TranslationPolicyWorkEvaluation>(safeItems.Count);
            foreach (TranslationWorkItem item in safeItems)
            {
                TranslationPolicy.TranslationPolicyCandidate candidate = new TranslationPolicy.TranslationPolicyCandidate
                {
                    PackageId = mod != null ? mod.PackageId ?? string.Empty : string.Empty,
                    ModName = mod != null ? mod.Name ?? string.Empty : string.Empty,
                    SourceFile = GetTranslationPolicyRelativeSourceFile(mod, item.SourceFile),
                    Bucket = bucket,
                    DefType = defType ?? string.Empty,
                    KeyOrPath = item.Key ?? string.Empty,
                    FieldName = bucket == TranslationPolicy.TranslationPolicyBucket.Keyed
                        ? item.Key ?? string.Empty
                        : GetTranslationPolicyTerminalField(item.Key),
                    SourceText = item.PolicySourceText ?? string.Empty,
                    DeclaringAssembly = string.Empty,
                    SchemaFingerprint = string.Empty
                };
                candidate.CandidateId = TranslationPolicy.TranslationPolicyIdentity.CreateCandidateId(candidate);
                evaluations.Add(new TranslationPolicyWorkEvaluation
                {
                    WorkItem = item,
                    Candidate = candidate,
                    Classification = TranslationPolicy.TranslationPolicyClassifier.Classify(candidate)
                });
            }

            int localAllowCount = evaluations.Count(evaluation =>
                evaluation.Classification.Decision == TranslationPolicy.TranslationPolicyDecision.HardAllow);
            int localDenyCount = evaluations.Count(evaluation =>
                evaluation.Classification.Decision == TranslationPolicy.TranslationPolicyDecision.HardDeny);
            TranslationPolicyAgentCoordinator.RecordLocalOutcomes(localAllowCount, localDenyCount);

            List<TranslationPolicy.TranslationPolicyCandidate> ambiguousCandidates = evaluations
                .Where(evaluation => evaluation.Classification.Decision == TranslationPolicy.TranslationPolicyDecision.Ambiguous)
                .Select(evaluation => evaluation.Candidate)
                .ToList();
            Dictionary<string, TranslationPolicy.TranslationPolicyAgentCandidateOutcome> agentOutcomes =
                ambiguousCandidates.Count == 0
                    ? new Dictionary<string, TranslationPolicy.TranslationPolicyAgentCandidateOutcome>(StringComparer.Ordinal)
                    : await TranslationPolicyAgentCoordinator.ResolveCandidatesAsync(
                        mod != null ? mod.PackageId : string.Empty,
                        ambiguousCandidates);

            List<TranslationWorkItem> allowed = new List<TranslationWorkItem>(safeItems.Count);
            int agentAllowCount = 0;
            int blockedCount = 0;
            foreach (TranslationPolicyWorkEvaluation evaluation in evaluations)
            {
                TranslationPolicy.TranslationPolicyAgentCandidateOutcome agentOutcome = null;
                if (evaluation.Classification.Decision == TranslationPolicy.TranslationPolicyDecision.Ambiguous)
                {
                    if (agentOutcomes != null)
                        agentOutcomes.TryGetValue(evaluation.Candidate.CandidateId, out agentOutcome);
                    if (agentOutcome == null)
                        agentOutcome = CreateMissingPolicyAgentOutcome();
                }
                TranslationPolicy.TranslationPolicyAgentDecision agentDecision = agentOutcome != null
                    ? agentOutcome.Decision
                    : TranslationPolicy.TranslationPolicyAgentDecision.Unresolved;

                TranslationPolicy.TranslationPolicyApplicationDecision application =
                    TranslationPolicy.TranslationPolicyApplication.Resolve(
                        evaluation.Classification.Decision,
                        agentDecision,
                        evaluation.WorkItem.IsPolicyOnlyExistingTranslation);
                if (application != TranslationPolicy.TranslationPolicyApplicationDecision.Remove)
                {
                    if (application == TranslationPolicy.TranslationPolicyApplicationDecision.Translate &&
                        evaluation.Classification.Decision == TranslationPolicy.TranslationPolicyDecision.Ambiguous)
                    {
                        agentAllowCount++;
                    }
                    allowed.Add(evaluation.WorkItem);
                    continue;
                }

                blockedCount++;
                if (finalData != null) finalData.Remove(evaluation.WorkItem.Key);
                if (provenanceByKey != null) provenanceByKey.Remove(evaluation.WorkItem.Key);
                RecordPolicyUnresolvedIfNeeded(
                    mod,
                    bucket.ToString(),
                    defType,
                    targetFile,
                    evaluation,
                    agentOutcome);
            }

            if (localDenyCount > 0 || ambiguousCandidates.Count > 0)
            {
                AutoTranslatorSettings.AddLog(AutoTranslatorAPI.TranslateText("ATC_PolicyAgent_BatchSummary",
                    mod != null ? mod.Name : string.Empty,
                    localAllowCount,
                    localDenyCount,
                    agentAllowCount,
                    blockedCount));
            }

            return allowed;
        }

        private static string GetTranslationPolicyRelativeSourceFile(ModMetaData mod, string sourceFile)
        {
            if (string.IsNullOrWhiteSpace(sourceFile)) return string.Empty;
            try
            {
                string fullFile = Path.GetFullPath(sourceFile);
                string root = mod != null && mod.RootDir != null
                    ? Path.GetFullPath(mod.RootDir.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : string.Empty;
                if (root.Length > 0)
                {
                    string prefix = root + Path.DirectorySeparatorChar;
                    if (fullFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return fullFile.Substring(prefix.Length).Replace('\\', '/');
                }

                return Path.GetFileName(fullFile);
            }
            catch
            {
                return Path.GetFileName(sourceFile) ?? string.Empty;
            }
        }

        private static string GetTranslationPolicyTerminalField(string path)
        {
            string[] segments = (path ?? string.Empty).Split('.');
            for (int index = segments.Length - 1; index >= 0; index--)
            {
                string segment = segments[index];
                int bracket = segment.IndexOf('[');
                if (bracket >= 0) segment = segment.Substring(0, bracket);
                if (segment.Length == 0 || segment.All(char.IsDigit)) continue;
                return segment;
            }

            return string.Empty;
        }

        // 這個方法負責處理 SafeSlice 相關流程。
        // EN: This method handles safe slice.
        private static List<string> SafeSlice(List<string> source, int start, int count)
        {
            if (source == null || start < 0 || count <= 0 || start >= source.Count)
            {
                return new List<string>();
            }

            int safeCount = Math.Min(count, source.Count - start);
            if (safeCount <= 0)
            {
                return new List<string>();
            }

            var result = new List<string>(safeCount);
            for (int i = 0; i < safeCount; i++)
            {
                result.Add(source[start + i]);
            }

            return result;
        }


        private static bool TryUseExistingTranslation(
            Dictionary<string, string> finalData,
            string key,
            string existingTranslation,
            string sourceText,
            out bool usedExistingValue,
            out string translationInput)
        {
            usedExistingValue = false;
            translationInput = string.Empty;
            if (!string.IsNullOrWhiteSpace(sourceText) && IsUntranslatableGrammarRule(sourceText))
            {
                finalData[key] = sourceText;
                return true;
            }

            string validationSource = string.IsNullOrWhiteSpace(sourceText)
                ? existingTranslation
                : sourceText;
            string candidate = SanitizeTranslationResult(existingTranslation, validationSource);

            if (!string.IsNullOrWhiteSpace(sourceText) &&
                (HasProtectedTokenMismatch(candidate, sourceText) ||
                 HasFormatArgumentMismatch(candidate, sourceText) ||
                 HasTranslatableTitleTagMismatch(candidate, sourceText)))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchDetected++);
                finalData.Remove(key);
                translationInput = sourceText;
                return false;
            }

            if (!TranslationResultLanguagePolicy.ShouldAccept(
                    candidate,
                    validationSource,
                    AutoTranslatorMod.Settings.TargetLang))
            {
                if (TranslationResultLanguagePolicy.HasLikelyEnglishResidual(
                        candidate,
                        validationSource,
                        AutoTranslatorMod.Settings.TargetLang))
                {
                    AddValidationStat(s => s.EnglishResidualDetected++);
                }
                finalData.Remove(key);
                translationInput = sourceText;
                if (string.IsNullOrWhiteSpace(translationInput))
                    translationInput = existingTranslation;
                return false;
            }

            finalData[key] = candidate;
            usedExistingValue = true;
            return true;
        }

        private static bool IsShareablePreferredSource(TranslationProvenanceEntry provenance)
        {
            if (provenance == null) return false;
            TranslationSourceCategory category =
                TranslationSourcePriorityPolicy.ClassifyProvenance(provenance.SourceKind);
            return category == TranslationSourceCategory.ExternalHuman ||
                   category == TranslationSourceCategory.Cloud;
        }

        private static bool TryApplyPreferredTargetTranslation(
            ModMetaData mod,
            string key,
            string sourceText,
            IEnumerable<PreferredTranslationCandidate> candidates,
            Dictionary<string, string> finalData,
            Dictionary<string, TranslationProvenanceEntry> provenanceByKey,
            out bool usedExistingValue)
        {
            usedExistingValue = false;
            List<PreferredTranslationCandidate> ordered = (candidates ??
                    Enumerable.Empty<PreferredTranslationCandidate>())
                .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Value))
                .Select((candidate, index) => new { Candidate = candidate, Index = index })
                .OrderBy(item => TranslationSourcePriorityPolicy.GetRank(
                    AutoTranslatorMod.Settings,
                    mod != null ? mod.PackageId : string.Empty,
                    TranslationSourcePriorityPolicy.ClassifyProvenance(
                        item.Candidate.Provenance?.SourceKind)))
                .ThenBy(item => item.Index)
                .Select(item => item.Candidate)
                .ToList();

            foreach (PreferredTranslationCandidate candidate in ordered)
            {
                if (!TryUseExistingTranslation(
                        finalData,
                        key,
                        candidate.Value,
                        sourceText,
                        out bool candidateUsedExisting,
                        out _))
                {
                    continue;
                }

                usedExistingValue = candidateUsedExisting;
                if (provenanceByKey != null)
                {
                    provenanceByKey[key] = candidate.Provenance != null
                        ? CloneProvenance(candidate.Provenance, finalData[key])
                        : CreateProvenance(
                            ProvenanceKindUnknownLegacy,
                            mod != null ? mod.PackageId : string.Empty,
                            mod != null ? mod.Name : string.Empty,
                            string.Empty,
                            AutoTranslatorMod.Settings.TargetLang.ToString(),
                            finalData[key]);
                }
                return true;
            }
            return false;
        }

        private static NativeTargetUseResult TryUseNativeTargetTranslation(
            ModMetaData mod,
            TranslationPolicy.TranslationPolicyBucket bucket,
            string defType,
            string key,
            string nativeTranslation,
            string sourceText,
            string sourceFile,
            Dictionary<string, string> finalData,
            out string translationInput)
        {
            translationInput = string.Empty;
            if (!TranslationPolicy.TranslationPolicyNativeTargetFilter.ShouldKeep(
                    mod != null ? mod.PackageId : string.Empty,
                    mod != null ? mod.Name : string.Empty,
                    bucket,
                    defType,
                    key,
                    nativeTranslation,
                    sourceFile))
            {
                if (finalData != null) finalData.Remove(key);
                return NativeTargetUseResult.HardDenied;
            }

            bool usedExistingValue;
            if (TryUseExistingTranslation(
                    finalData,
                    key,
                    nativeTranslation,
                    sourceText,
                    out usedExistingValue,
                    out translationInput))
            {
                return NativeTargetUseResult.Accepted;
            }

            if (string.IsNullOrWhiteSpace(translationInput))
                translationInput = string.IsNullOrWhiteSpace(sourceText) ? nativeTranslation : sourceText;
            return NativeTargetUseResult.RequiresTranslation;
        }

        private static string PrepareSecondaryTranslationSource(string secondaryTranslation, string primarySourceText)
        {
            string validationSource = string.IsNullOrWhiteSpace(primarySourceText)
                ? secondaryTranslation
                : primarySourceText;
            if (!string.IsNullOrWhiteSpace(primarySourceText) && IsUntranslatableGrammarRule(primarySourceText))
                return primarySourceText;

            string candidate = SanitizeTranslationResult(secondaryTranslation, validationSource);
            if (!string.IsNullOrWhiteSpace(primarySourceText) &&
                (HasProtectedTokenMismatch(candidate, primarySourceText) ||
                 HasFormatArgumentMismatch(candidate, primarySourceText) ||
                 HasTranslatableTitleTagMismatch(candidate, primarySourceText)))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchDetected++);
                return primarySourceText;
            }

            return candidate;
        }


        // 這個方法負責翻譯 AdaptiveSmallChunks 內容。
        // EN: This method translates adaptive small chunks.
        private static async Task<List<string>> TranslateAdaptiveSmallChunks(List<string> chunk, string contextInfo)
        {
            // Automatic fallback requests are intentionally disabled. A rejected
            // batch is persisted as unresolved and may only be retried by an
            // explicit user action.
            await Task.FromResult(false);
            return null;
        }


        // 這個方法負責處理 RetryLikelyEnglishResiduals 相關流程。
        // EN: This method handles retry likely english residuals.
        private static async Task<List<string>> RetryLikelyEnglishResiduals(
            List<string> sourceTexts,
            List<string> translatedTexts,
            string contextInfo,
            string packageId = null)
        {
            if (sourceTexts == null || translatedTexts == null || sourceTexts.Count != translatedTexts.Count)
            {
                return translatedTexts;
            }

            const int maxResidualRetriesPerBatch = 2;
            int residualRetries = 0;

            for (int i = 0; i < translatedTexts.Count; i++)
            {
                string sanitized = SanitizeTranslationResult(translatedTexts[i], sourceTexts[i]);
                bool tokenMismatch = HasProtectedTokenMismatch(sanitized, sourceTexts[i]) ||
                    HasFormatArgumentMismatch(sanitized, sourceTexts[i]) ||
                    HasTranslatableTitleTagMismatch(sanitized, sourceTexts[i]);
                bool englishResidual = false;
                bool wrongChineseVariant = false;
                bool wrongTargetScript = false;
                if (tokenMismatch)
                {
                    AddValidationStat(s => s.ProtectedTokenMismatchDetected++);
                }
                else
                {
                    englishResidual = TranslationHasLikelyEnglishResidual(sanitized, sourceTexts[i], true);
                    wrongChineseVariant = LanguageDetector.HasWrongChineseVariant(
                        sanitized,
                        AutoTranslatorMod.Settings.TargetLang);
                    wrongTargetScript = TranslationResultLanguagePolicy.HasUnexpectedScriptResidual(
                        sanitized,
                        sourceTexts[i],
                        AutoTranslatorMod.Settings.TargetLang);
                    if (!englishResidual && !wrongChineseVariant && !wrongTargetScript)
                    {
                        translatedTexts[i] = sanitized;
                        continue;
                    }
                }

                if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested)
                {
                    return translatedTexts;
                }

                if (englishResidual)
                {
                    AddValidationStat(s => s.EnglishResidualRetried++);
                }
                if (tokenMismatch)
                {
                    AddValidationStat(s => s.ProtectedTokenMismatchRetried++);
                }

                if (residualRetries >= maxResidualRetriesPerBatch)
                {
                    if (englishResidual) MarkEnglishResidualRejected(contextInfo);
                    else if (tokenMismatch) AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                    translatedTexts[i] = null;
                    continue;
                }

                residualRetries++;
                // Validation is mechanical only. Do not issue a second paid request.
                List<string> single = null;
                if (single != null && single.Count > 0)
                {
                    string singleSanitized = SanitizeTranslationResult(single[0], sourceTexts[i]);
                    if (!HasProtectedTokenMismatch(singleSanitized, sourceTexts[i]) &&
                        !HasFormatArgumentMismatch(singleSanitized, sourceTexts[i]) &&
                        !HasTranslatableTitleTagMismatch(singleSanitized, sourceTexts[i]) &&
                        TranslationResultLanguagePolicy.ShouldAccept(
                            singleSanitized,
                            sourceTexts[i],
                            AutoTranslatorMod.Settings.TargetLang))
                    {
                        translatedTexts[i] = singleSanitized;
                        continue;
                    }
                }

                if (TrySplitGrammarRule(sourceTexts[i], out string grammarPrefix, out string grammarRuleName, out string grammarRightSide) &&
                    ShouldTranslateGrammarRuleRightSide(grammarRuleName, grammarRightSide))
                {
                    // Grammar fragments follow the same rule: unresolved output is
                    // retained for an explicit user-triggered retry.
                    List<string> rightSideOnly = null;
                    if (rightSideOnly != null && rightSideOnly.Count > 0)
                    {
                        string merged = grammarPrefix + rightSideOnly[0].TrimStart();
                        string mergedSanitized = SanitizeTranslationResult(merged, sourceTexts[i]);
                        if (!HasProtectedTokenMismatch(mergedSanitized, sourceTexts[i]) &&
                            !HasFormatArgumentMismatch(mergedSanitized, sourceTexts[i]) &&
                            !HasTranslatableTitleTagMismatch(mergedSanitized, sourceTexts[i]) &&
                            TranslationResultLanguagePolicy.ShouldAccept(
                                mergedSanitized,
                                sourceTexts[i],
                                AutoTranslatorMod.Settings.TargetLang))
                        {
                            translatedTexts[i] = mergedSanitized;
                            continue;
                        }
                    }
                }

                if (englishResidual)
                {
                    MarkEnglishResidualRejected(contextInfo);
                    translatedTexts[i] = null;
                    continue;
                }
                if (tokenMismatch)
                {
                    AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                    if (!string.IsNullOrWhiteSpace(contextInfo))
                    {
                        AutoTranslatorSettings.AddLog(
                            AutoTranslatorAPI.TranslateText("ATC_Log_ProtectedTokenMismatchRejected", contextInfo));
                    }
                }
                translatedTexts[i] = null;
            }

            return translatedTexts;
        }

        // 這個方法負責嘗試執行 AcceptTranslatedValue 並回報是否成功。
        // EN: This method tries to accept translated value and reports whether it succeeded.
        internal static bool TryAcceptTranslatedValue(
            string translated,
            string sourceText,
            out string sanitized,
            out string failureReason,
            out string failureDetail)
        {
            failureReason = string.Empty;
            failureDetail = string.Empty;
            sanitized = SanitizeTranslationResult(translated, sourceText);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                failureReason = TranslationUnresolvedReasons.EmptyResponse;
                failureDetail = "The provider returned an empty or unusable translation.";
                return false;
            }

            if (HasTranslatableTitleTagMismatch(sanitized, sourceText))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                failureReason = TranslationUnresolvedReasons.TitleTagMismatch;
                failureDetail = "A [title:] tag was changed or lost.";
                return false;
            }

            if (HasProtectedTokenMismatch(sanitized, sourceText))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                failureReason = TranslationUnresolvedReasons.ProtectedTokenMismatch;
                failureDetail = "A protected token was changed or lost.";
                return false;
            }

            if (HasFormatArgumentMismatch(sanitized, sourceText))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                failureReason = TranslationUnresolvedReasons.FormatArgumentMismatch;
                failureDetail = "A format argument such as {0} was changed or lost.";
                return false;
            }

            if (RequiresProtectedTokenParity(sourceText) &&
                !LanguageDetector.LooksLikeTargetLanguage(sourceText, AutoTranslatorMod.Settings.TargetLang) &&
                string.Equals(sanitized, sourceText, StringComparison.Ordinal))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                failureReason = TranslationUnresolvedReasons.ProtectedTokenMismatch;
                failureDetail = "The provider returned the unchanged protected-token source text.";
                return false;
            }

            if (LanguageDetector.HasWrongChineseVariant(
                    sanitized,
                    AutoTranslatorMod.Settings.TargetLang))
            {
                failureReason = TranslationUnresolvedReasons.WrongChineseVariant;
                failureDetail = "The result uses the wrong Chinese writing variant.";
                return false;
            }

            if (TranslationResultLanguagePolicy.HasUnexpectedScriptResidual(
                    sanitized,
                    sourceText,
                    AutoTranslatorMod.Settings.TargetLang))
            {
                failureReason = TranslationUnresolvedReasons.WrongTargetLanguage;
                failureDetail = "The result contains unexpected text from another writing system.";
                return false;
            }

            if (!TranslationResultLanguagePolicy.ShouldAccept(
                    sanitized,
                    sourceText,
                    AutoTranslatorMod.Settings.TargetLang))
            {
                if (TranslationResultLanguagePolicy.HasLikelyEnglishResidual(
                        sanitized,
                        sourceText,
                        AutoTranslatorMod.Settings.TargetLang))
                {
                    AddValidationStat(s => s.EnglishResidualFallback++);
                    failureReason = TranslationUnresolvedReasons.EnglishResidual;
                    failureDetail = "The result still appears to contain untranslated English.";
                }
                else
                {
                    failureReason = TranslationUnresolvedReasons.Unknown;
                    failureDetail = "The result did not pass language-quality validation.";
                }
                return false;
            }

            return true;
        }

        // 這個方法負責標記 EnglishResidualRejected 狀態。
        // EN: This method marks english residual rejected.
        private static void MarkEnglishResidualRejected(string contextInfo)
        {
            AddValidationStat(s => s.EnglishResidualFallback++);
            if (!string.IsNullOrWhiteSpace(contextInfo))
            {
                bool shouldLog;
                lock (_loggedEnglishResidualContexts)
                {
                    shouldLog = _loggedEnglishResidualContexts.Add(contextInfo);
                }

                if (shouldLog)
                {
                    AutoTranslatorSettings.AddLog("🩺 " +
                        AutoTranslatorAPI.TranslateText("ATC_Log_EnglishResidualRejected", contextInfo));
                }
            }
        }
    }
}
