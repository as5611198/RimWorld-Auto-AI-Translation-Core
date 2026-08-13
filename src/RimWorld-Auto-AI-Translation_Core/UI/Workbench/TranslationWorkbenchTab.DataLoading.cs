using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using AutoTranslator_Core.TranslationPolicy;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責 翻譯工作台分頁資料載入 相關邏輯，支援 Auto Translation Core 的執行流程。
// EN: This file contains translation workbench tab data loading support code.

namespace AutoTranslator_Core
{
        // 這個類別負責 翻譯工作台分頁 的主要流程與狀態。
        // EN: This class manages the main workflow and state for TranslationWorkbenchTab.
        public static partial class TranslationWorkbenchTab
        {
            private static TargetLanguage? _loadedWorkbenchTargetLanguage;

            // 這個方法負責讀取 Real資料 資料。
            // EN: This method loads real data.
            private static void LoadRealData(Verse.ModMetaData targetMod)
            {
                WorkbenchModSnapshot snapshot = CreateWorkbenchModSnapshot(targetMod);
                if (snapshot == null)
                {
                    ATC_Dispatcher.RunOnMainThread(() => _isLoading = false);
                    return;
                }

                LoadRealData(snapshot);
            }

            private static WorkbenchModSnapshot CreateWorkbenchModSnapshot(Verse.ModMetaData targetMod)
            {
                if (targetMod == null || string.IsNullOrWhiteSpace(targetMod.PackageId)) return null;

                return new WorkbenchModSnapshot
                {
                    Mod = targetMod,
                    PackageId = targetMod.PackageId,
                    ModName = targetMod.Name ?? "",
                    RootDir = targetMod.RootDir != null ? targetMod.RootDir.FullName : "",
                    TargetLang = AutoTranslatorMod.Settings.TargetLang,
                    TargetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang)
                };
            }

            private static bool IsOwnedWorkbenchOutputFile(
                string filePath,
                string packageId,
                ISet<string> ownedKeyedFileNames = null)
            {
                if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(packageId)) return false;

                return IsWorkbenchGeneratedOutputFile(filePath, packageId, ownedKeyedFileNames) ||
                       IsWorkbenchCorrectionOutputFile(filePath, packageId);
            }

            private static bool IsWorkbenchGeneratedOutputFile(
                string filePath,
                string packageId,
                ISet<string> ownedKeyedFileNames = null)
            {
                return IsExactWorkbenchPackageFile(filePath, packageId, "_AutoTranslated.xml") ||
                       TranslationGeneratedOutputOwnership.IsOwnedKeyedFile(
                           filePath,
                           packageId,
                           ownedKeyedFileNames);
            }

            private static bool IsWorkbenchCorrectionOutputFile(string filePath, string packageId)
            {
                return IsExactWorkbenchPackageFile(filePath, packageId, "_CloudCorrections.xml");
            }

            private static bool IsExactWorkbenchPackageFile(string filePath, string packageId, string suffix)
            {
                if (string.IsNullOrWhiteSpace(filePath) ||
                    string.IsNullOrWhiteSpace(packageId) ||
                    string.IsNullOrWhiteSpace(suffix))
                {
                    return false;
                }

                string cleanPackageId = packageId.Replace(".", "_").ToLowerInvariant();
                return string.Equals(
                    Path.GetFileName(filePath),
                    cleanPackageId + suffix,
                    StringComparison.OrdinalIgnoreCase);
            }

            private static HashSet<string> GetWorkbenchOwnedKeyedFileNames(
                string packageId,
                TargetLanguage targetLang,
                IEnumerable<string> langRoots)
            {
                List<string> sourceFiles = new List<string>();
                foreach (string langRoot in langRoots ?? Enumerable.Empty<string>())
                {
                    foreach (string sourceKeyedPath in AutoTranslatorScanner.GetTranslatableLanguageBucketPaths(
                        langRoot,
                        targetLang,
                        "Keyed",
                        false))
                    {
                        sourceFiles.AddRange(AutoTranslatorScanner.GetXmlFilesForTranslationCache(
                            sourceKeyedPath,
                            SearchOption.AllDirectories));
                    }
                }

                return TranslationGeneratedOutputOwnership.BuildKeyedFileNameSet(packageId, sourceFiles);
            }

            private static void StartLoadingModForEditing(Verse.ModMetaData targetMod, string initialSearchText)
            {
                StartLoadingModForEditing(targetMod, initialSearchText, null);
            }

            private static void StartLoadingModForEditing(Verse.ModMetaData targetMod, WorkbenchFocusRequest focusRequest)
            {
                StartLoadingModForEditing(targetMod, focusRequest != null ? focusRequest.SearchText : "", focusRequest);
            }

            private static void StartLoadingModForEditing(Verse.ModMetaData targetMod, string initialSearchText, WorkbenchFocusRequest focusRequest)
            {
                WorkbenchModSnapshot snapshot = CreateWorkbenchModSnapshot(targetMod);
                if (snapshot == null) return;

                _editingMod = targetMod;
                _loadedWorkbenchTargetLanguage = null;
                _isLoading = true;
                _itemSearchText = initialSearchText ?? "";
                // Direct navigation must not inherit a stale translation filter from the
                // previous visit, otherwise the requested item is loaded but stays hidden.
                if (focusRequest != null)
                    _workbenchItemTranslationFilter = WorkbenchItemTranslationFilter.All;
                _pendingWorkbenchFocus = focusRequest;
                _activeWorkbenchFocus = null;
                _itemScroll = UnityEngine.Vector2.zero;
                InvalidateVisibleItemCache();
                Task.Run(() => LoadRealData(snapshot));
            }

            private static void LoadRealData(WorkbenchModSnapshot targetMod)
            {
                var resultData = new Dictionary<string, List<WorkbenchItem>>();
                var langRoots = AutoTranslatorScanner.GetAllEffectiveLangPaths(targetMod.PackageId, targetMod.RootDir);
                var defsRoots = AutoTranslatorScanner.GetAllEffectiveDefsPaths(targetMod.PackageId, targetMod.RootDir);
                HashSet<string> ownedKeyedFileNames = GetWorkbenchOwnedKeyedFileNames(
                    targetMod.PackageId,
                    targetMod.TargetLang,
                    langRoots);


                string targetLangFolder = targetMod.TargetLangFolder;
                bool officialTarReference = AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(targetMod.PackageId);
                Dictionary<string, WorkbenchRestoreBaselineEntry> restoreBaselines =
                    LoadWorkbenchRestoreBaselines(targetMod.PackageId, targetLangFolder);

                var engKeyed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var transKeyed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var nativeReferenceKeyed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var officialReferenceKeyed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var readOnlyReferenceKeyed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var langRoot in langRoots)
                {
                    foreach (string sourceKeyedPath in AutoTranslatorScanner.GetTranslatableLanguageBucketPaths(langRoot, targetMod.TargetLang, "Keyed", false))
                    {
                        var dict = AutoTranslatorScanner.LoadXmlFilesToDict(sourceKeyedPath);
                        foreach (var kv in dict)
                        {
                            if (!engKeyed.ContainsKey(kv.Key)) engKeyed[kv.Key] = kv.Value;
                        }
                    }
                    foreach (string modTransKeyedPath in AutoTranslatorScanner.GetTargetLanguageBucketPaths(langRoot, targetMod.TargetLang, "Keyed"))
                    {
                        var dict = AutoTranslatorScanner.LoadXmlFilesToDict(modTransKeyedPath);
                        foreach (var kv in dict)
                        {
                            nativeReferenceKeyed[kv.Key] = kv.Value;
                            if (!transKeyed.ContainsKey(kv.Key))
                            {
                                transKeyed[kv.Key] = kv.Value;
                                readOnlyReferenceKeyed.Add(kv.Key);
                            }
                        }
                    }
                }

                string packKeyedDir = System.IO.Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Languages", targetLangFolder, "Keyed");
                if (System.IO.Directory.Exists(packKeyedDir))
                {
                    foreach (var file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(packKeyedDir, System.IO.SearchOption.AllDirectories))
                    {
                        if (IsOwnedWorkbenchOutputFile(file, targetMod.PackageId, ownedKeyedFileNames))
                        {
                            var d = AutoTranslatorScanner.LoadXmlFileToDict(file, targetMod.TargetLang);
                            foreach (var kv in d)
                            {
                                transKeyed[kv.Key] = kv.Value;
                                readOnlyReferenceKeyed.Remove(kv.Key);
                            }
                        }
                    }
                }

                string workspaceKeyedDir = System.IO.Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Upload_Workspace", targetMod.PackageId, targetLangFolder, "Keyed");
                if (System.IO.Directory.Exists(workspaceKeyedDir))
                {
                    foreach (var file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(workspaceKeyedDir, System.IO.SearchOption.AllDirectories))
                    {
                        var d = AutoTranslatorScanner.LoadXmlFileToDict(file);
                        foreach (var kv in d)
                        {
                            transKeyed[kv.Key] = kv.Value;
                            readOnlyReferenceKeyed.Remove(kv.Key);
                        }
                    }
                }

                if (officialTarReference)
                {
                    foreach (var kv in AutoTranslatorScanner.LoadOfficialTarTranslationsByCategory(
                                 targetMod.PackageId,
                                 targetMod.RootDir,
                                 targetMod.TargetLang,
                                 "Keyed"))
                    {
                        officialReferenceKeyed[kv.Key] = kv.Value;
                        if (!transKeyed.ContainsKey(kv.Key))
                        {
                            transKeyed[kv.Key] = kv.Value;
                            readOnlyReferenceKeyed.Add(kv.Key);
                        }
                    }
                }

                if (engKeyed.Count > 0)
                {
                    var list = new List<WorkbenchItem>();
                    foreach (var kv in engKeyed)
                    {
                        string translated = transKeyed.ContainsKey(kv.Key) ? transKeyed[kv.Key] : "";
                        bool readOnlyReference = readOnlyReferenceKeyed.Contains(kv.Key);
                        string originalTranslatedText = translated;
                        bool originalTranslatedTextIsReadOnlyReference = readOnlyReference;
                        if (TryGetWorkbenchRestoreBaseline(restoreBaselines, "Keyed", kv.Key, out WorkbenchRestoreBaselineEntry baseline))
                        {
                            originalTranslatedText = baseline.OriginalTranslatedText ?? "";
                            originalTranslatedTextIsReadOnlyReference = baseline.OriginalTranslatedTextIsReadOnlyReference;
                            if (string.IsNullOrEmpty(originalTranslatedText) &&
                                nativeReferenceKeyed.TryGetValue(kv.Key, out string nativeTranslated) &&
                                !string.IsNullOrEmpty(nativeTranslated))
                            {
                                originalTranslatedText = nativeTranslated;
                                originalTranslatedTextIsReadOnlyReference = true;
                            }
                            else if (string.IsNullOrEmpty(originalTranslatedText) &&
                                     officialReferenceKeyed.TryGetValue(kv.Key, out string baselineOfficialTranslated) &&
                                     !string.IsNullOrEmpty(baselineOfficialTranslated))
                            {
                                originalTranslatedText = baselineOfficialTranslated;
                                originalTranslatedTextIsReadOnlyReference = true;
                            }
                        }
                        else if (nativeReferenceKeyed.TryGetValue(kv.Key, out string nativeTranslated))
                        {
                            originalTranslatedText = nativeTranslated ?? "";
                            originalTranslatedTextIsReadOnlyReference = true;
                        }
                        else if (officialReferenceKeyed.TryGetValue(kv.Key, out string officialTranslated))
                        {
                            originalTranslatedText = officialTranslated ?? "";
                            originalTranslatedTextIsReadOnlyReference = true;
                        }

                        list.Add(new WorkbenchItem
                        {
                            Key = kv.Key,
                            OriginalText = kv.Value,
                            TranslatedText = translated,
                            OriginalTranslatedText = originalTranslatedText,
                            OriginalTranslatedTextIsReadOnlyReference = originalTranslatedTextIsReadOnlyReference,
                            SavedTranslatedText = translated,
                            SavedTranslatedTextIsReadOnlyReference = readOnlyReference
                        });
                    }
                    resultData["Keyed"] = list;
                }

                var engDefs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                var transDefs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                var nativeReferenceDefs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                var officialReferenceDefs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                var readOnlyReferenceDefs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var officialNormalizedDefPaths = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                var rawDefTypesAlreadyTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rawDefLanguageSamples = new List<string>();

                foreach (var defRoot in defsRoots)
                {
                    var dict = AutoTranslatorScanner.ExtractEnglishFromRawDefs(defRoot, true);
                    if (officialTarReference)
                    {
                        MergeOfficialDefPathAliases(
                            officialNormalizedDefPaths,
                            AutoTranslatorScanner.ExtractOfficialDefPathAliasesFromRawDefs(defRoot));
                    }
                    foreach (var typeKv in dict)
                    {
                        List<KeyValuePair<string, string>> visibleCandidates = typeKv.Value
                            .Where(kv => TranslationPolicyNativeTargetFilter.ShouldKeep(
                                TranslationPolicyBucket.DefInjected,
                                typeKv.Key,
                                kv.Key,
                                kv.Value))
                            .ToList();
                        if (visibleCandidates.Count == 0) continue;

                        rawDefLanguageSamples.AddRange(visibleCandidates.Select(kv => kv.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Take(40));
                        string sample = string.Join("\n", visibleCandidates.Select(kv => kv.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Take(120).ToArray());
                        if (LanguageDetector.LooksLikeTargetLanguage(sample, targetMod.TargetLang))
                        {
                            rawDefTypesAlreadyTarget.Add(typeKv.Key);
                        }

                        if (!engDefs.ContainsKey(typeKv.Key)) engDefs[typeKv.Key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in visibleCandidates) engDefs[typeKv.Key][kv.Key] = kv.Value;
                    }
                }
                bool rawDefsLookLikeTarget = LanguageDetector.LooksLikeTargetLanguage(
                    string.Join("\n", rawDefLanguageSamples.Take(240).ToArray()),
                    targetMod.TargetLang);

                string packDefDir = System.IO.Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Languages", targetLangFolder, "DefInjected");
                if (System.IO.Directory.Exists(packDefDir))
                {
                    foreach (var typeDir in System.IO.Directory.GetDirectories(packDefDir))
                    {
                        string defType = System.IO.Path.GetFileName(typeDir);
                        foreach (var file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(typeDir, System.IO.SearchOption.TopDirectoryOnly))
                        {
                            if (IsOwnedWorkbenchOutputFile(file, targetMod.PackageId))
                            {
                                if (!transDefs.ContainsKey(defType)) transDefs[defType] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                var d = AutoTranslatorScanner.LoadXmlFileToDict(file);
                                foreach (var kv in d) transDefs[defType][kv.Key] = kv.Value;
                            }
                        }
                    }
                }

                foreach (var langRoot in langRoots)
                {
                    foreach (string modTransDefDir in AutoTranslatorScanner.GetTargetLanguageBucketPaths(langRoot, targetMod.TargetLang, "DefInjected"))
                    {
                        LoadWorkbenchDefInjectedTranslations(
                            modTransDefDir,
                            nativeReferenceDefs,
                            targetMod.TargetLang,
                            transDefs,
                            readOnlyReferenceDefs);
                    }
                }

                string workspaceDefDir = System.IO.Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Upload_Workspace", targetMod.PackageId, targetLangFolder, "DefInjected");
                if (System.IO.Directory.Exists(workspaceDefDir))
                {
                    foreach (var typeDir in System.IO.Directory.GetDirectories(workspaceDefDir))
                    {
                        string defType = System.IO.Path.GetFileName(typeDir);
                        if (!transDefs.ContainsKey(defType)) transDefs[defType] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(typeDir, System.IO.SearchOption.TopDirectoryOnly))
                        {
                            var d = AutoTranslatorScanner.LoadXmlFileToDict(file);
                            foreach (var kv in d)
                            {
                                transDefs[defType][kv.Key] = kv.Value;
                                if (readOnlyReferenceDefs.TryGetValue(defType, out HashSet<string> readOnlyKeys))
                                {
                                    readOnlyKeys.Remove(kv.Key);
                                }
                            }
                        }
                    }
                }

                if (officialTarReference)
                {
                    var officialDefTranslations = AutoTranslatorScanner.LoadOfficialTarDefTranslations(
                        targetMod.PackageId,
                        targetMod.RootDir,
                        targetMod.TargetLang);
                    foreach (var typeKv in officialDefTranslations)
                    {
                        if (!officialReferenceDefs.ContainsKey(typeKv.Key))
                            officialReferenceDefs[typeKv.Key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!transDefs.ContainsKey(typeKv.Key))
                            transDefs[typeKv.Key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!readOnlyReferenceDefs.ContainsKey(typeKv.Key))
                            readOnlyReferenceDefs[typeKv.Key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var kv in typeKv.Value)
                        {
                            string workbenchKey = ResolveOfficialWorkbenchDefKey(
                                typeKv.Key,
                                kv.Key,
                                engDefs,
                                officialNormalizedDefPaths);
                            officialReferenceDefs[typeKv.Key][workbenchKey] = kv.Value;
                            if (!transDefs[typeKv.Key].ContainsKey(workbenchKey))
                            {
                                transDefs[typeKv.Key][workbenchKey] = kv.Value;
                                readOnlyReferenceDefs[typeKv.Key].Add(workbenchKey);
                            }
                        }
                    }
                }

                foreach (var typeKv in engDefs)
                {
                    string defType = typeKv.Key;
                    var list = new List<WorkbenchItem>();
                    foreach (var kv in typeKv.Value)
                    {
                        string translated = "";
                        bool readOnlyReference = false;
                        if (transDefs.ContainsKey(defType) && transDefs[defType].ContainsKey(kv.Key))
                        {
                            translated = transDefs[defType][kv.Key];
                            readOnlyReference = readOnlyReferenceDefs.TryGetValue(defType, out HashSet<string> readOnlyKeys) && readOnlyKeys.Contains(kv.Key);
                        }
                        else if (rawDefsLookLikeTarget || rawDefTypesAlreadyTarget.Contains(defType) || LanguageDetector.LooksLikeTargetLanguage(kv.Value, targetMod.TargetLang))
                        {
                            translated = kv.Value;
                        }

                        string originalTranslatedText = translated;
                        bool originalTranslatedTextIsReadOnlyReference = readOnlyReference;
                        if (TryGetWorkbenchRestoreBaseline(restoreBaselines, defType, kv.Key, out WorkbenchRestoreBaselineEntry baseline))
                        {
                            originalTranslatedText = baseline.OriginalTranslatedText ?? "";
                            originalTranslatedTextIsReadOnlyReference = baseline.OriginalTranslatedTextIsReadOnlyReference;
                            if (string.IsNullOrEmpty(originalTranslatedText) &&
                                nativeReferenceDefs.TryGetValue(defType, out Dictionary<string, string> nativeDefBaselineTranslations) &&
                                nativeDefBaselineTranslations.TryGetValue(kv.Key, out string nativeTranslated) &&
                                !string.IsNullOrEmpty(nativeTranslated))
                            {
                                originalTranslatedText = nativeTranslated;
                                originalTranslatedTextIsReadOnlyReference = true;
                            }
                            else if (string.IsNullOrEmpty(originalTranslatedText) &&
                                     officialReferenceDefs.TryGetValue(defType, out Dictionary<string, string> baselineOfficialDefTypeTranslations) &&
                                     baselineOfficialDefTypeTranslations.TryGetValue(kv.Key, out string baselineOfficialTranslated) &&
                                     !string.IsNullOrEmpty(baselineOfficialTranslated))
                            {
                                originalTranslatedText = baselineOfficialTranslated;
                                originalTranslatedTextIsReadOnlyReference = true;
                            }
                        }
                        else if (nativeReferenceDefs.TryGetValue(defType, out Dictionary<string, string> nativeDefTypeTranslations) &&
                                 nativeDefTypeTranslations.TryGetValue(kv.Key, out string nativeTranslated))
                        {
                            originalTranslatedText = nativeTranslated ?? "";
                            originalTranslatedTextIsReadOnlyReference = true;
                        }
                        else if (officialReferenceDefs.TryGetValue(defType, out Dictionary<string, string> officialDefTypeTranslations) &&
                                 officialDefTypeTranslations.TryGetValue(kv.Key, out string officialTranslated))
                        {
                            originalTranslatedText = officialTranslated ?? "";
                            originalTranslatedTextIsReadOnlyReference = true;
                        }

                        list.Add(new WorkbenchItem
                        {
                            Key = kv.Key,
                            OriginalText = kv.Value,
                            TranslatedText = translated,
                            OriginalTranslatedText = originalTranslatedText,
                            OriginalTranslatedTextIsReadOnlyReference = originalTranslatedTextIsReadOnlyReference,
                            SavedTranslatedText = translated,
                            SavedTranslatedTextIsReadOnlyReference = readOnlyReference
                        });
                    }
                    if (list.Count > 0) resultData[defType] = list;
                }

                foreach (KeyValuePair<string, List<WorkbenchItem>> categoryPair in resultData)
                {
                    if (categoryPair.Value == null) continue;
                    foreach (WorkbenchItem item in categoryPair.Value)
                    {
                        if (item != null) item.Category = categoryPair.Key;
                    }
                }

                WorkbenchDllLoadResult dllLoad = LoadDllWorkbenchData(
                    targetMod.Mod,
                    targetMod.TargetLang);

                ATC_Dispatcher.RunOnMainThread(() => {
                    if (_editingMod != targetMod.Mod) return;
                    if (AutoTranslatorMod.Settings.TargetLang != targetMod.TargetLang)
                    {
                        StartLoadingModForEditing(targetMod.Mod, _itemSearchText, _pendingWorkbenchFocus);
                        return;
                    }
                    _loadedWorkbenchTargetLanguage = targetMod.TargetLang;
                    _categorizedData = resultData;
                    _dllCategorizedData = dllLoad.Categories ??
                        new Dictionary<string, List<WorkbenchItem>>(StringComparer.OrdinalIgnoreCase);
                    _dllScanResult = dllLoad.ScanResult;
                    _dllStaleSavedEntryCount = dllLoad.StaleSavedEntryCount;
                    WorkbenchFocusRequest focus = _pendingWorkbenchFocus;
                    if (focus != null && officialTarReference && !string.IsNullOrWhiteSpace(focus.Category))
                    {
                        focus.Key = ResolveOfficialWorkbenchDefKey(
                            focus.Category,
                            focus.Key,
                            engDefs,
                            officialNormalizedDefPaths);
                    }
                    string selectedCategory = AllWorkbenchCategoriesView;
                    _selectedWorkbenchSource = WorkbenchSourceKind.All;
                    IDictionary<string, List<WorkbenchItem>> focusData = focus != null &&
                        focus.Source == WorkbenchSourceKind.Dll
                        ? _dllCategorizedData
                        : _categorizedData;
                    if (focus != null && !string.IsNullOrWhiteSpace(focus.Category) &&
                        focusData.ContainsKey(focus.Category))
                    {
                        selectedCategory = focus.Category;
                        _selectedWorkbenchSource = focus.Source;
                    }

                    _selectedCategory = selectedCategory;
                    _activeWorkbenchFocus = focus;
                    _pendingWorkbenchFocus = null;
                    _itemScroll = new UnityEngine.Vector2(0f, GetInitialItemScrollForFocus(focus, selectedCategory));
                    _catListScroll = new UnityEngine.Vector2(0f, GetInitialCategoryScrollForFocus(selectedCategory));
                    _categorizedDataVersion++;
                    _cachedVisibleItems = null;
                    _isLoading = false;
                    if (!string.IsNullOrWhiteSpace(dllLoad.Error))
                        SetWorkbenchStatus("ATC_Workbench_DllLoadFailed".Translate(dllLoad.Error).ToString());
                });
            }

            private static void MergeOfficialDefPathAliases(
                Dictionary<string, Dictionary<string, string>> target,
                Dictionary<string, Dictionary<string, string>> source)
            {
                if (target == null || source == null) return;
                foreach (KeyValuePair<string, Dictionary<string, string>> typePair in source)
                {
                    if (!target.TryGetValue(typePair.Key, out Dictionary<string, string> aliases))
                    {
                        aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        target[typePair.Key] = aliases;
                    }

                    if (typePair.Value == null) continue;
                    foreach (KeyValuePair<string, string> alias in typePair.Value)
                    {
                        aliases[alias.Key] = alias.Value;
                    }
                }
            }

            private static string ResolveOfficialWorkbenchDefKey(
                string defType,
                string officialKey,
                Dictionary<string, Dictionary<string, string>> englishDefs,
                Dictionary<string, Dictionary<string, string>> normalizedPathsBySuggestedPath)
            {
                string key = officialKey ?? "";
                if (string.IsNullOrWhiteSpace(defType) || string.IsNullOrWhiteSpace(key)) return key;
                if (englishDefs != null &&
                    englishDefs.TryGetValue(defType, out Dictionary<string, string> directEnglish) &&
                    directEnglish.ContainsKey(key))
                {
                    return key;
                }

                if (normalizedPathsBySuggestedPath != null &&
                    normalizedPathsBySuggestedPath.TryGetValue(defType, out Dictionary<string, string> aliases) &&
                    aliases.TryGetValue(key, out string normalizedKey) &&
                    !string.IsNullOrWhiteSpace(normalizedKey))
                {
                    return normalizedKey;
                }

                return key;
            }

            private static void LoadWorkbenchDefInjectedTranslations(
                string defInjectedDir,
                Dictionary<string, Dictionary<string, string>> target,
                TargetLanguage targetLang)
            {
                LoadWorkbenchDefInjectedTranslations(defInjectedDir, target, targetLang, null, null);
            }

            private static void LoadWorkbenchDefInjectedTranslations(
                string defInjectedDir,
                Dictionary<string, Dictionary<string, string>> target,
                TargetLanguage targetLang,
                Dictionary<string, Dictionary<string, string>> currentTranslations,
                Dictionary<string, HashSet<string>> readOnlyReferenceKeys)
            {
                if (target == null || string.IsNullOrEmpty(defInjectedDir) || !Directory.Exists(defInjectedDir)) return;

                foreach (string typeDir in Directory.GetDirectories(defInjectedDir))
                {
                    string defType = Path.GetFileName(typeDir);
                    if (string.IsNullOrEmpty(defType)) continue;
                    if (!target.ContainsKey(defType))
                    {
                        target[defType] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    foreach (string file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(typeDir, SearchOption.AllDirectories))
                    {
                        var d = AutoTranslatorScanner.LoadXmlFileToDict(file, targetLang);
                        foreach (var kv in d)
                        {
                            target[defType][kv.Key] = kv.Value;
                            AddWorkbenchReadOnlyReferenceTranslation(currentTranslations, readOnlyReferenceKeys, defType, kv.Key, kv.Value);
                        }
                    }
                }

                foreach (string file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(defInjectedDir, SearchOption.TopDirectoryOnly))
                {
                    if (!target.ContainsKey("General"))
                    {
                        target["General"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    var d = AutoTranslatorScanner.LoadXmlFileToDict(file, targetLang);
                    foreach (var kv in d)
                    {
                        target["General"][kv.Key] = kv.Value;
                        AddWorkbenchReadOnlyReferenceTranslation(currentTranslations, readOnlyReferenceKeys, "General", kv.Key, kv.Value);
                    }
                }
            }

            private static void AddWorkbenchReadOnlyReferenceTranslation(
                Dictionary<string, Dictionary<string, string>> currentTranslations,
                Dictionary<string, HashSet<string>> readOnlyReferenceKeys,
                string defType,
                string key,
                string value)
            {
                if (currentTranslations == null || readOnlyReferenceKeys == null) return;
                if (string.IsNullOrEmpty(defType) || string.IsNullOrEmpty(key)) return;
                if (!currentTranslations.ContainsKey(defType))
                {
                    currentTranslations[defType] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                if (currentTranslations[defType].ContainsKey(key)) return;

                currentTranslations[defType][key] = value;
                if (!readOnlyReferenceKeys.ContainsKey(defType))
                {
                    readOnlyReferenceKeys[defType] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                readOnlyReferenceKeys[defType].Add(key);
            }

            private static float GetInitialItemScrollForFocus(WorkbenchFocusRequest focus, string selectedCategory)
            {
                if (focus == null || string.IsNullOrWhiteSpace(focus.Key)) return 0f;
                if (string.IsNullOrWhiteSpace(selectedCategory)) return 0f;
                IDictionary<string, List<WorkbenchItem>> source = focus.Source == WorkbenchSourceKind.Dll
                    ? _dllCategorizedData
                    : _categorizedData;
                if (!source.TryGetValue(selectedCategory, out List<WorkbenchItem> items) || items == null) return 0f;

                int index = items.FindIndex(i => i != null && string.Equals(i.Key, focus.Key, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return 0f;
                int visibleIndex = index;
                if (!string.IsNullOrWhiteSpace(focus.SearchText))
                {
                    int matchedBefore = 0;
                    for (int i = 0; i < index; i++)
                    {
                        if (DoesWorkbenchItemMatchSearch(items[i], focus.SearchText)) matchedBefore++;
                    }

                    visibleIndex = matchedBefore;
                }

                return Mathf.Max(0f, visibleIndex * WorkbenchItemRowHeight - WorkbenchItemRowHeight);
            }

            private static float GetInitialCategoryScrollForFocus(string selectedCategory)
            {
                if (string.IsNullOrWhiteSpace(selectedCategory)) return 0f;

                IDictionary<string, List<WorkbenchItem>> source = _selectedWorkbenchSource == WorkbenchSourceKind.Dll
                    ? _dllCategorizedData
                    : _categorizedData;
                if (source == null || source.Count == 0) return 0f;

                int index = 0;
                foreach (string category in source.Keys)
                {
                    if (string.Equals(category, selectedCategory, StringComparison.OrdinalIgnoreCase))
                    {
                        return Mathf.Max(0f, index * WorkbenchCategoryRowHeight - WorkbenchCategoryRowHeight);
                    }

                    index++;
                }

                return 0f;
            }

        }
}
