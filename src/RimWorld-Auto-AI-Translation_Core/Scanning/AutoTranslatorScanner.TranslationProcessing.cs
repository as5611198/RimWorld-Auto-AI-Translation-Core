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

            AutoTranslatorSettings.AddLog("⚙️ " + "ATC_Log_KeyedScan".Translate());
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

        private static List<List<KeyedFileWorkItem>> BuildKeyedFileWorkItems(ModMetaData mod, List<string> keyedSourcePaths)
        {
            var groups = new Dictionary<string, List<KeyedFileWorkItem>>(StringComparer.OrdinalIgnoreCase);
            if (mod == null || keyedSourcePaths == null) return new List<List<KeyedFileWorkItem>>();

            string modIdClean = mod.PackageId.Replace(".", "_");
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

                    string targetFileName = $"{modIdClean}_{Path.GetFileName(file)}";
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
            var packDict = pureAiWorkspace
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : LoadXmlFileToDict(targetFile);
            Dictionary<string, string> nativeTargetDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TranslationProvenanceEntry> nativeTargetSourceDict =
                new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);

            string secondaryTag = "";
            if (settings.TargetLang == TargetLanguage.Traditional)
                secondaryTag = "ATC_Tag_FromSimplified".Translate().ToString();
            else if (settings.TargetLang == TargetLanguage.Simplified)
                secondaryTag = "ATC_Tag_FromTraditional".Translate().ToString();

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

                Dictionary<string, string> finalData = new Dictionary<string, string>(packDict, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, TranslationProvenanceEntry> provenanceByKey =
                    new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in packDict)
                {
                    provenanceByKey[pair.Key] = GetFileEntryProvenance(packLangRoot, mod.PackageId, targetFile, pair.Key, pair.Value);
                }

                List<string> keysToAI = new List<string>();
                List<string> valuesToAI = new List<string>();
                List<TranslationProvenanceEntry> aiSources = new List<TranslationProvenanceEntry>();

                foreach (string key in orderedKeys)
                {
                    if (processedKeys != null && processedKeys.Contains(key)) continue;
                    if (!sourceEntries.TryGetValue(key, out List<KeyedSourceEntry> entries) || entries.Count == 0) continue;

                    KeyedSourceEntry sourceEntry = PickBestKeyedSourceEntry(entries, settings.TargetLang);
                    string sourceText = sourceEntry != null ? sourceEntry.Value : "";

                    if (!pureAiWorkspace && nativeTargetDict.TryGetValue(key, out string nativeVal))
                    {
                        finalData[key] = nativeVal;
                        if (nativeTargetSourceDict.TryGetValue(key, out TranslationProvenanceEntry nativeSource))
                            provenanceByKey[key] = CloneProvenance(nativeSource, nativeVal);
                    }
                    else if (!pureAiWorkspace && packDict.TryGetValue(key, out string packVal))
                    {
                        int before = keysToAI.Count;
                        bool usedExistingValue;
                        bool setValue = UseExistingOrQueueForAI(finalData, keysToAI, valuesToAI, key, packVal, sourceText, out usedExistingValue);
                        if (setValue)
                        {
                            provenanceByKey[key] = usedExistingValue
                                ? GetFileEntryProvenance(packLangRoot, mod.PackageId, targetFile, key, finalData[key])
                                : CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, sourceEntry != null ? sourceEntry.SourceFile : "", targetFolder, finalData[key]);
                        }
                        else if (keysToAI.Count > before)
                        {
                            aiSources.Add(CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceEntry != null ? sourceEntry.SourceFile : "", "English", ""));
                        }
                    }
                    else if (!pureAiWorkspace && GlobalPrimaryKeyedDict.TryGetValue(key, out string pVal))
                    {
                        int before = keysToAI.Count;
                        bool usedExistingValue;
                        bool setValue = UseExistingOrQueueForAI(finalData, keysToAI, valuesToAI, key, pVal, sourceText, out usedExistingValue);
                        if (setValue)
                        {
                            TranslationProvenanceEntry sourceInfo = null;
                            GlobalPrimaryKeyedSourceDict.TryGetValue(key, out sourceInfo);
                            provenanceByKey[key] = usedExistingValue
                                ? CloneProvenance(sourceInfo, finalData[key])
                                : CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, sourceEntry != null ? sourceEntry.SourceFile : "", targetFolder, finalData[key]);
                        }
                        else if (keysToAI.Count > before)
                        {
                            aiSources.Add(CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceEntry != null ? sourceEntry.SourceFile : "", "English", ""));
                        }
                    }
                    else if (!pureAiWorkspace && GlobalSecondaryKeyedDict.TryGetValue(key, out string sVal) && !string.IsNullOrEmpty(secondaryTag))
                    {
                        keysToAI.Add(key);
                        valuesToAI.Add(PrepareSecondaryTranslationSource(sVal, sourceText));
                        TranslationProvenanceEntry secondarySource = null;
                        GlobalSecondaryKeyedSourceDict.TryGetValue(key, out secondarySource);
                        aiSources.Add(CreateProvenance(
                            ProvenanceKindAIFromSecondary,
                            secondarySource != null ? secondarySource.SourcePackageId : mod.PackageId,
                            secondarySource != null ? secondarySource.SourceModName : mod.Name,
                            secondarySource != null ? secondarySource.SourceFile : "",
                            secondarySource != null ? secondarySource.SourceLanguage : "",
                            "",
                            secondarySource != null ? secondarySource.SourceKind : ""));
                    }
                    else if (sourceEntry != null &&
                             !pureAiWorkspace &&
                             (sourceEntry.FileLooksLikeTarget || LanguageDetector.LooksLikeTargetLanguage(sourceEntry.Value, settings.TargetLang)))
                    {
                        finalData[key] = sourceEntry.Value;
                        provenanceByKey[key] = CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, sourceEntry.SourceFile, targetFolder, sourceEntry.Value);
                    }
                    else if (sourceEntry != null)
                    {
                        keysToAI.Add(key);
                        valuesToAI.Add(sourceEntry.Value);
                        aiSources.Add(CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceEntry.SourceFile, "English", ""));
                    }

                    if (processedKeys != null && finalData.ContainsKey(key)) processedKeys.Add(key);
                }

                if (keysToAI.Count > 0)
                {
                    AutoTranslatorSettings.AddLog("🔌 " + AutoTranslatorAPI.TranslateText("ATC_Log_FoundMissing", "Keyed", keysToAI.Count));
                    var res = await SafeTranslateBatch(valuesToAI, $"{mod.Name} / {sourceFiles[0].TargetFileName}");
                    if (res != null)
                    {
                        int acceptedCount = 0;
                        for (int i = 0; i < keysToAI.Count; i++)
                        {
                            string k = keysToAI[i];
                            string v = res[i];

                            if (!TryAcceptTranslatedValue(v, valuesToAI[i], out v))
                            {
                                continue;
                            }

                            finalData[k] = v;
                            provenanceByKey[k] = i < aiSources.Count
                                ? CloneProvenance(aiSources[i], v)
                                : CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, sourceFiles[0].File, "English", v);
                            if (processedKeys != null) processedKeys.Add(k);
                            acceptedCount++;
                        }

                        AutoTranslatorSettings.AddLog("✨ " + "ATC_Log_AIFinish".Translate("Keyed"));
                        aiTranslatedCount += acceptedCount;
                    }
                    else AutoTranslatorSettings.AddLog("⚠️ " + "ATC_Log_AIFail".Translate("Keyed"));
                }

                AutoTranslatorSettings.AddLog("✅ " + AutoTranslatorAPI.TranslateText("ATC_Log_NoMissing", Path.GetFileName(sourceFiles[0].File)));
                if (finalData.Count > 0)
                {
                    SaveXml(targetFile, finalData);
                    SaveProvenanceForFile(packLangRoot, mod.PackageId, targetFile, finalData, provenanceByKey);
                }
            }
            catch (XmlException xmlEx)
            {
                string file = sourceFiles.Count > 0 ? sourceFiles[0].File : "";
                AutoTranslatorSettings.AddErrorLog("❌ " + AutoTranslatorAPI.TranslateText("ATC_LogError_Format", mod.Name, GetShortPath(file)));
                Log.Warning($"[AutoTranslationCore] XML Format Error ({mod.Name}): {xmlEx.Message}");
            }
            catch (Exception ex)
            {
                string file = sourceFiles.Count > 0 ? sourceFiles[0].File : "";
                AutoTranslatorSettings.AddErrorLog("❌ " + AutoTranslatorAPI.TranslateText("ATC_LogError_Unknown", mod.Name, GetShortPath(file)));
                Log.Warning($"[AutoTranslationCore] Process Error ({mod.Name}): {ex.Message}");
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
                secondaryTag = "ATC_Tag_FromSimplified".Translate().ToString();
            else if (settings.TargetLang == TargetLanguage.Simplified)
                secondaryTag = "ATC_Tag_FromTraditional".Translate().ToString();
            string packLangRoot = GetTranslationOutputLanguageRoot(mod, targetFolder, outputMode);
            string packDefBaseDir = Path.Combine(packLangRoot, "DefInjected");
            bool pureAiWorkspace = outputMode == TranslationOutputMode.PureAiWorkspace;


            var englishKeys = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
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
                var extracted = ExtractEnglishFromRawDefs(dRoot);
                foreach (var kv in extracted)
                {
                    if (!englishKeys.ContainsKey(kv.Key))
                        englishKeys[kv.Key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var inner in kv.Value) englishKeys[kv.Key][inner.Key] = inner.Value;
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

            foreach (var defType in allDefTypes)
            {
                if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested) return aiTranslatedCount;

                currentDef++;
                AutoTranslatorMod.Settings.SubProgress = (float)currentDef / totalDefs;


                string defTypeLower = defType.ToLower();
                if (defTypeLower.Contains("facedef") || defTypeLower.Contains("eyedef") ||
                    defTypeLower.Contains("browdef") || defTypeLower.Contains("liddef") ||
                    defTypeLower.Contains("lashdef") || defTypeLower.Contains("mouthdef") ||
                    defTypeLower.Contains("nosedef") || defTypeLower.Contains("eardef") ||
                    defTypeLower.Contains("skindef") || defTypeLower.Contains("facialanimation"))
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
                    : LoadXmlFileToDict(targetFile);

                Dictionary<string, string> finalData = new Dictionary<string, string>();
                Dictionary<string, TranslationProvenanceEntry> provenanceByKey =
                    new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
                List<string> keysToAI = new List<string>();
                List<string> valuesToAI = new List<string>();
                List<TranslationProvenanceEntry> aiSources = new List<TranslationProvenanceEntry>();

                foreach (var key in keysForThisType)
                {
                    string globalKey = $"{defType}/{key}";
                    string globalKeyGen = $"General/{key}";


                    if (!pureAiWorkspace && selfDict != null && selfDict.TryGetValue(key, out string selfVal)
                              && !string.IsNullOrWhiteSpace(selfVal))
                    {
                        finalData[key] = selfVal;
                        if (modSelfTargetSources.TryGetValue(defType, out Dictionary<string, TranslationProvenanceEntry> selfSourceDict) &&
                            selfSourceDict.TryGetValue(key, out TranslationProvenanceEntry selfSource))
                        {
                            provenanceByKey[key] = CloneProvenance(selfSource, selfVal);
                        }
                        else
                        {
                            provenanceByKey[key] = CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, "", targetFolder, selfVal);
                        }
                    }


                    else if (!pureAiWorkspace && packDict.TryGetValue(key, out string packVal))
                    {
                        int before = keysToAI.Count;
                        bool usedExistingValue;
                        bool setValue = UseExistingOrQueueForAI(finalData, keysToAI, valuesToAI, key, packVal, engDict != null && engDict.TryGetValue(key, out string packSourceVal) ? packSourceVal : "", out usedExistingValue);
                        if (setValue)
                        {
                            provenanceByKey[key] = usedExistingValue
                                ? GetFileEntryProvenance(packLangRoot, mod.PackageId, targetFile, key, finalData[key])
                                : CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, "", targetFolder, finalData[key]);
                        }
                        else if (keysToAI.Count > before)
                        {
                            aiSources.Add(CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, "", "English", ""));
                        }
                    }

                    else if (!pureAiWorkspace &&
                             (GlobalPrimaryDefDict.TryGetValue(globalKey, out string pVal)
                             || GlobalPrimaryDefDict.TryGetValue(globalKeyGen, out pVal)))
                    {
                        int before = keysToAI.Count;
                        bool usedExistingValue;
                        bool setValue = UseExistingOrQueueForAI(finalData, keysToAI, valuesToAI, key, pVal, engDict != null && engDict.TryGetValue(key, out string globalSourceVal) ? globalSourceVal : "", out usedExistingValue);
                        if (setValue)
                        {
                            TranslationProvenanceEntry sourceInfo = null;
                            if (!GlobalPrimaryDefSourceDict.TryGetValue(globalKey, out sourceInfo))
                                GlobalPrimaryDefSourceDict.TryGetValue(globalKeyGen, out sourceInfo);
                            provenanceByKey[key] = usedExistingValue
                                ? CloneProvenance(sourceInfo, finalData[key])
                                : CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, "", targetFolder, finalData[key]);
                        }
                        else if (keysToAI.Count > before)
                        {
                            aiSources.Add(CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, "", "English", ""));
                        }
                    }

                    else if (!pureAiWorkspace &&
                             modSelfSecondaryLang.TryGetValue(defType, out var secDict)
                              && secDict.TryGetValue(key, out string secVal)
                              && !string.IsNullOrEmpty(secondaryTag))
                    {
                        keysToAI.Add(key);
                        valuesToAI.Add(PrepareSecondaryTranslationSource(secVal, engDict != null && engDict.TryGetValue(key, out string secondarySourceVal) ? secondarySourceVal : ""));
                        TranslationProvenanceEntry secondarySource = null;
                        if (modSelfSecondarySources.TryGetValue(defType, out Dictionary<string, TranslationProvenanceEntry> secSourceDict))
                            secSourceDict.TryGetValue(key, out secondarySource);
                        aiSources.Add(CreateProvenance(
                            ProvenanceKindAIFromSecondary,
                            secondarySource != null ? secondarySource.SourcePackageId : mod.PackageId,
                            secondarySource != null ? secondarySource.SourceModName : mod.Name,
                            secondarySource != null ? secondarySource.SourceFile : "",
                            secondarySource != null ? secondarySource.SourceLanguage : "",
                            "",
                            secondarySource != null ? secondarySource.SourceKind : ""));
                    }

                    else if (!pureAiWorkspace &&
                             ((GlobalSecondaryDefDict.TryGetValue(globalKey, out string sVal)
                              || GlobalSecondaryDefDict.TryGetValue(globalKeyGen, out sVal))
                             && !string.IsNullOrEmpty(secondaryTag)))
                    {
                        keysToAI.Add(key);
                        valuesToAI.Add(PrepareSecondaryTranslationSource(sVal, engDict != null && engDict.TryGetValue(key, out string globalSecondarySourceVal) ? globalSecondarySourceVal : ""));
                        TranslationProvenanceEntry secondarySource = null;
                        if (!GlobalSecondaryDefSourceDict.TryGetValue(globalKey, out secondarySource))
                            GlobalSecondaryDefSourceDict.TryGetValue(globalKeyGen, out secondarySource);
                        aiSources.Add(CreateProvenance(
                            ProvenanceKindAIFromSecondary,
                            secondarySource != null ? secondarySource.SourcePackageId : mod.PackageId,
                            secondarySource != null ? secondarySource.SourceModName : mod.Name,
                            secondarySource != null ? secondarySource.SourceFile : "",
                            secondarySource != null ? secondarySource.SourceLanguage : "",
                            "",
                            secondarySource != null ? secondarySource.SourceKind : ""));
                    }

                    else if (engDict != null && engDict.TryGetValue(key, out string engVal)
                             && !string.IsNullOrEmpty(engVal))
                    {
                        if (!pureAiWorkspace && LanguageDetector.LooksLikeTargetLanguage(engVal, settings.TargetLang))
                        {
                            finalData[key] = engVal;
                            provenanceByKey[key] = CreateProvenance(ProvenanceKindModNativeTarget, mod.PackageId, mod.Name, "", targetFolder, engVal);
                        }
                        else
                        {
                            keysToAI.Add(key);
                            valuesToAI.Add(engVal);
                            aiSources.Add(CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, "", "English", ""));
                        }
                    }
                }

                if (keysToAI.Count > 0)
                {
                    AutoTranslatorSettings.AddLog("🔌 " + AutoTranslatorAPI.TranslateText("ATC_Log_FoundMissing", defType, keysToAI.Count));
                    var res = await SafeTranslateBatch(valuesToAI, $"{mod.Name} / Defs: {defType}");
                    if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested) return aiTranslatedCount;
                    if (res != null)
                    {
                        int acceptedCount = 0;
                        for (int i = 0; i < keysToAI.Count; i++)
                        {
                            string k = keysToAI[i];
                            string v = res[i];


                            if (!TryAcceptTranslatedValue(v, valuesToAI[i], out v))
                            {
                                continue;
                            }

                            finalData[k] = v;
                            provenanceByKey[k] = i < aiSources.Count
                                ? CloneProvenance(aiSources[i], v)
                                : CreateProvenance(ProvenanceKindAI, mod.PackageId, mod.Name, "", "English", v);
                            acceptedCount++;
                        }
                        AutoTranslatorSettings.AddLog("✨ " + AutoTranslatorAPI.TranslateText("ATC_Log_AIFinish", defType));
                        aiTranslatedCount += acceptedCount;
                    }
                    else AutoTranslatorSettings.AddLog("⚠️ " + AutoTranslatorAPI.TranslateText("ATC_Log_AIFail", defType));
                }
                else
                {
                    AutoTranslatorSettings.AddLog("✅ " + AutoTranslatorAPI.TranslateText("ATC_Log_NoMissing", $"Def:{defType}"));
                }

                if (finalData.Count > 0)
                {
                    SaveXml(targetFile, finalData);
                    SaveProvenanceForFile(packLangRoot, mod.PackageId, targetFile, finalData, provenanceByKey);
                }
            }
            return aiTranslatedCount;
        }


        // 這個方法負責處理 Safe翻譯Batch 相關流程。
        // EN: This method handles safe translate batch.
        private static async Task<List<string>> SafeTranslateBatch(List<string> texts, string contextInfo)
        {
            if (texts == null || texts.Count == 0) return new List<string>();

            var uniqueTexts = texts.Distinct().ToList();
            var translatedDict = new Dictionary<string, string>();

            int chunkSize = Math.Max(1, AutoTranslatorAPI.GetCurrentRuntimeProfile().BatchSize);
            int maxConcurrency = Math.Max(1, AutoTranslatorMod.Settings.MaxThreads);
            List<Task> tasks = new List<Task>();

            using (SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrency))
            {
                for (int i = 0; i < uniqueTexts.Count; i += chunkSize)
                {
                    int chunkIndex = i;
                    int currentChunkSize = Math.Min(chunkSize, uniqueTexts.Count - chunkIndex);
                    List<string> chunk = SafeSlice(uniqueTexts, chunkIndex, currentChunkSize);
                    if (chunk.Count == 0) continue;

                    tasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested) return;

                            // TranslateBatchAsync already owns network and format retries. Retrying the
                            // same chunk again here multiplied worst-case waits into tens of minutes.
                            List<string> chunkRes = await AutoTranslatorAPI.TranslateBatchAsync(chunk, suppressFinalParseError: true);

                            if (chunkRes == null || chunkRes.Count != chunk.Count)
                            {
                                AutoTranslatorSettings.AddLog("🔄 " + "ATC_Log_ApiFallback".Translate());
                                AutoTranslatorSettings.AddErrorLog("❌ " + AutoTranslatorAPI.TranslateText("ATC_LogError_ApiCritical", contextInfo));
                                chunkRes = new List<string>(chunk);
                            }

                            if (chunkRes != null && chunkRes.Count == chunk.Count)
                            {
                                chunkRes = await RetryLikelyEnglishResiduals(chunk, chunkRes, contextInfo);
                            }

                            if (chunkRes == null || chunkRes.Count != chunk.Count)
                            {
                                return;
                            }

                            lock (translatedDict)
                            {
                                for (int j = 0; j < chunk.Count; j++)
                                {
                                    translatedDict[chunk[j]] = chunkRes[j];
                                }
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);
            }

            List<string> finalResults = new List<string>(texts.Count);
            foreach (var t in texts)
            {
                if (translatedDict.TryGetValue(t, out string translated))
                {
                    finalResults.Add(translated);
                }
                else
                {
                    finalResults.Add(t);
                }
            }

            return finalResults;
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


        // 這個方法負責處理 UseExistingOr佇列ForAI 相關流程。
        // EN: This method handles use existing or queue for AI.
        private static bool UseExistingOrQueueForAI(Dictionary<string, string> finalData, List<string> keysToAI, List<string> valuesToAI, string key, string existingTranslation, string sourceText)
        {
            bool usedExistingValue;
            return UseExistingOrQueueForAI(finalData, keysToAI, valuesToAI, key, existingTranslation, sourceText, out usedExistingValue);
        }

        private static bool UseExistingOrQueueForAI(Dictionary<string, string> finalData, List<string> keysToAI, List<string> valuesToAI, string key, string existingTranslation, string sourceText, out bool usedExistingValue)
        {
            usedExistingValue = false;
            if (!string.IsNullOrWhiteSpace(sourceText) && IsUntranslatableGrammarRule(sourceText))
            {
                finalData[key] = sourceText;
                return true;
            }

            string candidate = existingTranslation;
            if (!string.IsNullOrWhiteSpace(sourceText))
            {
                candidate = SanitizeTranslationResult(existingTranslation, sourceText);
            }

            if (!string.IsNullOrWhiteSpace(sourceText) &&
                (HasProtectedTokenMismatch(candidate, sourceText) || HasFormatArgumentMismatch(candidate, sourceText)))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchDetected++);
                keysToAI.Add(key);
                valuesToAI.Add(sourceText);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(sourceText) && TranslationHasLikelyEnglishResidual(candidate, sourceText, true))
            {
                keysToAI.Add(key);
                valuesToAI.Add(sourceText);
                return false;
            }

            finalData[key] = candidate;
            usedExistingValue = true;
            return true;
        }

        private static string PrepareSecondaryTranslationSource(string secondaryTranslation, string primarySourceText)
        {
            if (string.IsNullOrWhiteSpace(primarySourceText)) return secondaryTranslation;
            if (IsUntranslatableGrammarRule(primarySourceText)) return primarySourceText;

            string candidate = SanitizeTranslationResult(secondaryTranslation, primarySourceText);
            if (HasProtectedTokenMismatch(candidate, primarySourceText) || HasFormatArgumentMismatch(candidate, primarySourceText))
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
            if (chunk == null || chunk.Count <= 1) return null;

            int smallChunkSize = Math.Min(4, chunk.Count);
            if (smallChunkSize <= 0) return null;

            List<string> merged = new List<string>(chunk.Count);

            for (int i = 0; i < chunk.Count; i += smallChunkSize)
            {
                if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested) return null;

                List<string> smallChunk = SafeSlice(chunk, i, Math.Min(smallChunkSize, chunk.Count - i));
                if (smallChunk.Count == 0) return null;

                List<string> smallResult = await AutoTranslatorAPI.TranslateBatchAsync(smallChunk, suppressFinalParseError: true);
                if (smallResult == null || smallResult.Count != smallChunk.Count)
                {
                    return null;
                }

                smallResult = await RetryLikelyEnglishResiduals(smallChunk, smallResult, contextInfo);
                if (smallResult == null || smallResult.Count != smallChunk.Count)
                {
                    return null;
                }

                merged.AddRange(smallResult);
            }

            AutoTranslatorSettings.AddLog("[API] Adaptive small-batch retry succeeded.");
            return merged;
        }


        // 這個方法負責處理 RetryLikelyEnglishResiduals 相關流程。
        // EN: This method handles retry likely english residuals.
        private static async Task<List<string>> RetryLikelyEnglishResiduals(List<string> sourceTexts, List<string> translatedTexts, string contextInfo)
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
                    HasFormatArgumentMismatch(sanitized, sourceTexts[i]);
                bool englishResidual = false;
                if (tokenMismatch)
                {
                    AddValidationStat(s => s.ProtectedTokenMismatchDetected++);
                }
                else
                {
                    englishResidual = TranslationHasLikelyEnglishResidual(sanitized, sourceTexts[i], true);
                    if (!englishResidual)
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
                    else AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                    translatedTexts[i] = englishResidual ? sanitized : null;
                    continue;
                }

                residualRetries++;
                List<string> single = await AutoTranslatorAPI.TranslateBatchAsync(new List<string> { sourceTexts[i] }, suppressFinalParseError: true);
                if (single != null && single.Count > 0)
                {
                    string singleSanitized = SanitizeTranslationResult(single[0], sourceTexts[i]);
                    if (!HasProtectedTokenMismatch(singleSanitized, sourceTexts[i]) &&
                        !HasFormatArgumentMismatch(singleSanitized, sourceTexts[i]) &&
                        !TranslationHasLikelyEnglishResidual(singleSanitized, sourceTexts[i], false))
                    {
                        translatedTexts[i] = singleSanitized;
                        continue;
                    }
                }

                if (TrySplitGrammarRule(sourceTexts[i], out string grammarPrefix, out string grammarRuleName, out string grammarRightSide) &&
                    ShouldTranslateGrammarRuleRightSide(grammarRuleName, grammarRightSide))
                {
                    List<string> rightSideOnly = await AutoTranslatorAPI.TranslateBatchAsync(new List<string> { grammarRightSide.Trim() }, suppressFinalParseError: true);
                    if (rightSideOnly != null && rightSideOnly.Count > 0)
                    {
                        string merged = grammarPrefix + rightSideOnly[0].TrimStart();
                        string mergedSanitized = SanitizeTranslationResult(merged, sourceTexts[i]);
                        if (!HasProtectedTokenMismatch(mergedSanitized, sourceTexts[i]) &&
                            !HasFormatArgumentMismatch(mergedSanitized, sourceTexts[i]) &&
                            !TranslationHasLikelyEnglishResidual(mergedSanitized, sourceTexts[i], false))
                        {
                            translatedTexts[i] = mergedSanitized;
                            continue;
                        }
                    }
                }

                if (englishResidual)
                {
                    MarkEnglishResidualRejected(contextInfo);
                    translatedTexts[i] = sanitized;
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
        private static bool TryAcceptTranslatedValue(string translated, string sourceText, out string sanitized)
        {
            sanitized = SanitizeTranslationResult(translated, sourceText);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return false;
            }

            if (HasProtectedTokenMismatch(sanitized, sourceText))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                return false;
            }

            if (HasFormatArgumentMismatch(sanitized, sourceText))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                return false;
            }

            if (RequiresProtectedTokenParity(sourceText) &&
                !LanguageDetector.LooksLikeTargetLanguage(sourceText, AutoTranslatorMod.Settings.TargetLang) &&
                string.Equals(sanitized, sourceText, StringComparison.Ordinal))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                return false;
            }

            if (!TranslationHasLikelyEnglishResidual(sanitized, sourceText, false))
            {
                return true;
            }

            return !IsUnchangedLikelyEnglishSource(sanitized, sourceText);
        }

        private static bool IsUnchangedLikelyEnglishSource(string translated, string sourceText)
        {
            if (string.IsNullOrWhiteSpace(translated) || string.IsNullOrWhiteSpace(sourceText)) return false;
            if (!string.Equals(translated.Trim(), sourceText.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (LanguageDetector.LooksLikeTargetLanguage(sourceText, AutoTranslatorMod.Settings.TargetLang)) return false;
            return TranslationHasLikelyEnglishResidual(translated, sourceText, false);
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
