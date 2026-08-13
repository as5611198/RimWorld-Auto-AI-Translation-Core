using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責 翻譯工作台分頁搜尋 相關邏輯，支援 Auto Translation Core 的執行流程。
// EN: This file contains translation workbench tab search support code.

namespace AutoTranslator_Core
{
        // 這個類別負責 翻譯工作台分頁 的主要流程與狀態。
        // EN: This class manages the main workflow and state for TranslationWorkbenchTab.
        public static partial class TranslationWorkbenchTab
        {

            // 這個方法負責送出 Refresh 請求。
            // EN: This method requests refresh.
            public static void RequestRefresh()
            {
                _translatedModsCacheGeneration++;
                _translatedPackageIds = null;
                _cachedModSelectionList = null;
                _cachedModSelectionValidVersion = -1;
                _translatedModsCacheError = null;
                _isTranslatedModsCacheLoading = false;
            }

            public static void MarkPackageTranslated(string packageId)
            {
                if (string.IsNullOrWhiteSpace(packageId)) return;
                _translatedModsCacheGeneration++;
                _isTranslatedModsCacheLoading = false;
                if (_translatedPackageIds == null)
                {
                    _translatedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                _translatedPackageIds.Add(packageId);
                _cachedModSelectionList = null;
                _cachedModSelectionValidVersion = -1;
            }


            // 這個方法負責處理 InitTranslated模組快取 相關流程。
            // EN: This method handles init translated mods cache.
            private static void InitTranslatedModsCache()
            {
                if (_translatedPackageIds != null) return;
                if (_isTranslatedModsCacheLoading) return;

                _isTranslatedModsCacheLoading = true;
                _translatedModsCacheError = null;
                int generation = _translatedModsCacheGeneration;
                System.Threading.Tasks.Task.Run(() =>
                {
                    HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string error = null;
                    try
                    {
                        BuildTranslatedModsCache(result);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }

                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        if (generation != _translatedModsCacheGeneration)
                        {
                            return;
                        }

                        _translatedPackageIds = result;
                        _translatedModsCacheError = error;
                        _cachedModSelectionList = null;
                        _cachedModSelectionValidVersion = -1;
                        _isTranslatedModsCacheLoading = false;
                        if (!string.IsNullOrEmpty(error))
                        {
                            Verse.Log.Warning($"[AutoTranslationCore] Workbench translated-mod cache failed: {error}");
                        }
                    });
                });
            }

            // 這個方法負責取得 TranslatedPackageIdsSafe 資料。
            // EN: This method gets translated package ids safe.
            private static HashSet<string> GetTranslatedPackageIdsSafe()
            {
                InitTranslatedModsCache();
                return _translatedPackageIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            private static void InvalidateVisibleItemCache()
            {
                _cachedVisibleItems = null;
                _cachedVisibleSource = WorkbenchSourceKind.Xml;
                _cachedVisibleCategory = "";
                _cachedVisibleSearchText = "";
                _cachedVisibleFocusCategory = "";
                _cachedVisibleFocusKey = "";
                _cachedVisibleRetainedCategory = "";
                _cachedVisibleRetainedKey = "";
                _cachedVisibleSourceCount = -1;
                _cachedVisibleDataVersion = -1;
                _cachedAllCategoryItems = null;
                _cachedAllCategoryItemsDataVersion = -1;
            }

            private static bool IsAllWorkbenchCategoriesSelected()
            {
                return string.Equals(_selectedCategory, AllWorkbenchCategoriesView, StringComparison.Ordinal);
            }

            private static bool IsWorkbenchCategoryVisible(string category)
            {
                return _selectedWorkbenchSource == WorkbenchSourceKind.All ||
                       (_selectedWorkbenchSource == WorkbenchSourceKind.Xml && IsAllWorkbenchCategoriesSelected()) ||
                       string.Equals(_selectedCategory ?? "", category ?? "", StringComparison.OrdinalIgnoreCase);
            }

            private static string GetWorkbenchItemCategory(WorkbenchItem item)
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Category)) return item.Category;
                return IsAllWorkbenchCategoriesSelected() ? "" : _selectedCategory ?? "";
            }

            private static List<WorkbenchItem> GetCurrentWorkbenchSourceItems()
            {
                if (_selectedWorkbenchSource == WorkbenchSourceKind.All)
                {
                    if (_cachedAllCategoryItems != null &&
                        _cachedAllCategoryItemsDataVersion == _categorizedDataVersion &&
                        _cachedAllCategoryItemsSource == WorkbenchSourceKind.All)
                        return _cachedAllCategoryItems;

                    _cachedAllCategoryItems = _categorizedData
                        .SelectMany(pair => pair.Value ?? new List<WorkbenchItem>())
                        .Concat(_dllCategorizedData.SelectMany(pair => pair.Value ?? new List<WorkbenchItem>()))
                        .Where(item => item != null)
                        .ToList();
                    _cachedAllCategoryItemsDataVersion = _categorizedDataVersion;
                    _cachedAllCategoryItemsSource = WorkbenchSourceKind.All;
                    return _cachedAllCategoryItems;
                }

                Dictionary<string, List<WorkbenchItem>> source =
                    _selectedWorkbenchSource == WorkbenchSourceKind.Dll
                        ? _dllCategorizedData
                        : _categorizedData;
                if (!IsAllWorkbenchCategoriesSelected())
                {
                    return source.TryGetValue(_selectedCategory ?? "", out List<WorkbenchItem> categoryItems) && categoryItems != null
                        ? categoryItems
                        : new List<WorkbenchItem>();
                }

                if (_cachedAllCategoryItems != null &&
                    _cachedAllCategoryItemsDataVersion == _categorizedDataVersion &&
                    _cachedAllCategoryItemsSource == _selectedWorkbenchSource)
                {
                    return _cachedAllCategoryItems;
                }

                _cachedAllCategoryItems = source
                    .SelectMany(pair => pair.Value ?? new List<WorkbenchItem>())
                    .Where(item => item != null)
                    .ToList();
                _cachedAllCategoryItemsDataVersion = _categorizedDataVersion;
                _cachedAllCategoryItemsSource = _selectedWorkbenchSource;
                return _cachedAllCategoryItems;
            }

            private static List<WorkbenchItem> GetVisibleItemsForCurrentCategory(List<WorkbenchItem> sourceItems)
            {
                if (sourceItems == null) return new List<WorkbenchItem>();

                string searchText = _itemSearchText ?? "";
                string focusCategory = _activeWorkbenchFocus != null ? _activeWorkbenchFocus.Category ?? "" : "";
                string focusKey = _activeWorkbenchFocus != null ? _activeWorkbenchFocus.Key ?? "" : "";
                string retainedCategory = _retainedEditedCategory ?? "";
                string retainedKey = _retainedEditedKey ?? "";
                if (_cachedVisibleItems != null &&
                    _cachedVisibleDataVersion == _categorizedDataVersion &&
                    _cachedVisibleSource == _selectedWorkbenchSource &&
                    _cachedVisibleSourceCount == sourceItems.Count &&
                    string.Equals(_cachedVisibleCategory, _selectedCategory ?? "", StringComparison.Ordinal) &&
                    string.Equals(_cachedVisibleSearchText, searchText, StringComparison.Ordinal) &&
                    _cachedVisibleTranslationFilter == _workbenchItemTranslationFilter &&
                    string.Equals(_cachedVisibleFocusCategory, focusCategory, StringComparison.Ordinal) &&
                    string.Equals(_cachedVisibleFocusKey, focusKey, StringComparison.Ordinal) &&
                    string.Equals(_cachedVisibleRetainedCategory, retainedCategory, StringComparison.Ordinal) &&
                    string.Equals(_cachedVisibleRetainedKey, retainedKey, StringComparison.Ordinal))
                {
                    return _cachedVisibleItems;
                }

                if (string.IsNullOrWhiteSpace(searchText))
                {
                    _cachedVisibleItems = sourceItems;
                }
                else
                {
                    string searchLower = searchText.ToLowerInvariant();
                    _cachedVisibleItems = sourceItems
                        .Select((item, index) => new { Item = item, Index = index, Rank = GetWorkbenchSearchRank(item, searchLower) })
                        .Where(entry => entry.Rank < int.MaxValue)
                        .OrderBy(entry => entry.Rank)
                        .ThenBy(entry => entry.Index)
                        .Select(entry => entry.Item)
                        .ToList();
                }

                if (_workbenchItemTranslationFilter != WorkbenchItemTranslationFilter.All)
                {
                    _cachedVisibleItems = _cachedVisibleItems.Where(item =>
                    {
                        string translatedText = (item?.TranslatedText ?? string.Empty).Trim();
                        string originalText = (item?.OriginalText ?? string.Empty).Trim();
                        bool translated = translatedText.Length > 0 &&
                                          !string.Equals(translatedText, originalText, StringComparison.Ordinal);
                        return _workbenchItemTranslationFilter == WorkbenchItemTranslationFilter.Translated
                            ? translated
                            : !translated;
                    }).ToList();
                }

                if (_activeWorkbenchFocus != null &&
                    IsWorkbenchCategoryVisible(_activeWorkbenchFocus.Category) &&
                    !string.IsNullOrWhiteSpace(_activeWorkbenchFocus.Key) &&
                    !_cachedVisibleItems.Any(i => i != null &&
                                                  string.Equals(GetWorkbenchItemCategory(i), _activeWorkbenchFocus.Category ?? "", StringComparison.OrdinalIgnoreCase) &&
                                                  string.Equals(i.Key, _activeWorkbenchFocus.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    WorkbenchItem focusedItem = sourceItems.FirstOrDefault(i => i != null &&
                        string.Equals(GetWorkbenchItemCategory(i), _activeWorkbenchFocus.Category ?? "", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(i.Key, _activeWorkbenchFocus.Key, StringComparison.OrdinalIgnoreCase));
                    if (focusedItem != null)
                    {
                        _cachedVisibleItems = new List<WorkbenchItem>(_cachedVisibleItems) { focusedItem };
                    }
                }

                if (!string.IsNullOrWhiteSpace(retainedKey) &&
                    IsWorkbenchCategoryVisible(retainedCategory) &&
                    !_cachedVisibleItems.Any(i => i != null &&
                                                  string.Equals(GetWorkbenchItemCategory(i), retainedCategory, StringComparison.OrdinalIgnoreCase) &&
                                                  string.Equals(i.Key, retainedKey, StringComparison.OrdinalIgnoreCase)))
                {
                    WorkbenchItem retainedItem = sourceItems.FirstOrDefault(i => i != null &&
                        string.Equals(GetWorkbenchItemCategory(i), retainedCategory, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(i.Key, retainedKey, StringComparison.OrdinalIgnoreCase));
                    if (retainedItem != null)
                    {
                        _cachedVisibleItems = new List<WorkbenchItem>(_cachedVisibleItems) { retainedItem };
                    }
                }

                _cachedVisibleCategory = _selectedCategory ?? "";
                _cachedVisibleSource = _selectedWorkbenchSource;
                _cachedVisibleSearchText = searchText;
                _cachedVisibleTranslationFilter = _workbenchItemTranslationFilter;
                _cachedVisibleFocusCategory = focusCategory;
                _cachedVisibleFocusKey = focusKey;
                _cachedVisibleRetainedCategory = retainedCategory;
                _cachedVisibleRetainedKey = retainedKey;
                _cachedVisibleSourceCount = sourceItems.Count;
                _cachedVisibleDataVersion = _categorizedDataVersion;
                return _cachedVisibleItems;
            }

            private static int GetWorkbenchSearchRank(WorkbenchItem item, string searchLower)
            {
                if (item == null || string.IsNullOrEmpty(searchLower)) return int.MaxValue;

                string key = (item.Key ?? "").ToLowerInvariant();
                string original = (item.OriginalText ?? "").ToLowerInvariant();
                string translated = (item.TranslatedText ?? "").ToLowerInvariant();

                if (key.StartsWith(searchLower) || original.StartsWith(searchLower) || translated.StartsWith(searchLower)) return 0;
                if (key.Contains(searchLower) || original.Contains(searchLower) || translated.Contains(searchLower)) return 1;
                return int.MaxValue;
            }

            // 這個方法負責建立 Translated模組快取 所需資料。
            // EN: This method builds translated mods cache.
            private static void BuildTranslatedModsCache(HashSet<string> result)
            {
                if (result == null) return;
                string packPath = AutoTranslatorScanner.GetLocalPackPath();


                string targetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
                string langRoot = System.IO.Path.Combine(packPath, "Languages", targetLangFolder);

                if (System.IO.Directory.Exists(langRoot))
                {
                    foreach (var file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(langRoot, System.IO.SearchOption.AllDirectories))
                    {
                        string pid = ExtractPackageIdFromTranslationFile(file);
                        if (!string.IsNullOrEmpty(pid)) result.Add(pid);
                    }
                }

            }

            // 這個方法負責處理 ExtractPackageIdFrom翻譯File 相關流程。
            // EN: This method handles extract package id from translation file.
            private static string ExtractPackageIdFromTranslationFile(string file)
            {
                string fileName = System.IO.Path.GetFileName(file);
                int splitIdx = fileName.IndexOf("_AutoTranslated", StringComparison.OrdinalIgnoreCase);
                if (splitIdx == -1) splitIdx = fileName.LastIndexOf('_');
                return splitIdx > 0 && splitIdx < fileName.Length
                    ? fileName.Substring(0, splitIdx).Replace("_", ".")
                    : "";
            }


            // 這個方法負責執行 Global搜尋 動作。
            // EN: This method executes global search.
            private static void ExecuteGlobalSearch(string keyword, TargetLanguage? langFilter)
            {
                _isGlobalSearching = true;
                _globalSearchProgress = 0f;
                _globalSearchResults.Clear();
                _globalSearchScroll = UnityEngine.Vector2.zero;
                _globalSearchSnapshotText = (keyword ?? "").Trim();
                _globalSearchSnapshotLangFilter = langFilter;
                _globalSearchHasSnapshot = true;
                string snapshotSearchText = _globalSearchSnapshotText;
                string searchLower = snapshotSearchText.ToLowerInvariant();
                string targetFolderFilter = langFilter.HasValue ? AutoTranslatorScanner.GetFolderNameByLanguage(langFilter.Value) : null;
                List<GlobalSearchModSnapshot> activeMods = Verse.ModLister.AllInstalledMods
                    .Where(m => m != null && m.Active &&
                                !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(m.PackageId))
                    .Select(m => new GlobalSearchModSnapshot
                    {
                        Mod = m,
                        PackageId = m.PackageId ?? "",
                        ModName = m.Name ?? "",
                        RootDir = m.RootDir != null ? m.RootDir.FullName : ""
                    })
                    .ToList();
                Dictionary<string, GlobalSearchModSnapshot> activeModsByPackageId = activeMods
                    .Where(m => !string.IsNullOrEmpty(m.PackageId))
                    .GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                System.Threading.Tasks.Task.Run(() => {
                    var results = new List<GlobalSearchResult>();


                    var filesToScan = new List<GlobalSearchFileWorkItem>();


                    string packPath = AutoTranslatorScanner.GetLocalPackPath();
                    string langsRoot = System.IO.Path.Combine(packPath, "Languages");
                    if (System.IO.Directory.Exists(langsRoot))
                    {
                        string[] searchDirs = targetFolderFilter != null
                            ? (System.IO.Directory.Exists(System.IO.Path.Combine(langsRoot, targetFolderFilter)) ? new[] { System.IO.Path.Combine(langsRoot, targetFolderFilter) } : new string[0])
                            : System.IO.Directory.GetDirectories(langsRoot);

                        foreach (var dir in searchDirs)
                        {
                            var allXmls = AutoTranslatorScanner.GetXmlFilesForTranslationCache(dir, System.IO.SearchOption.AllDirectories);
                            foreach (var file in allXmls)
                            {
                                string fileName = System.IO.Path.GetFileName(file);
                                int splitIdx = fileName.IndexOf("_AutoTranslated", StringComparison.OrdinalIgnoreCase);
                                if (splitIdx == -1) splitIdx = fileName.LastIndexOf('_');

                                if (splitIdx > 0)
                                {
                                    string pid = fileName.Substring(0, splitIdx).Replace("_", ".");
                                    activeModsByPackageId.TryGetValue(pid, out GlobalSearchModSnapshot targetMod);
                                    if (targetMod != null) filesToScan.Add(new GlobalSearchFileWorkItem { FilePath = file, Mod = targetMod.Mod, Category = GetWorkbenchCategoryFromTranslationFile(file) });
                                }
                            }
                        }
                    }


                    foreach (var mod in activeMods)
                    {
                        string packageId = mod.PackageId ?? "";
                        if (packageId.Equals("auto.aitranslation.core", StringComparison.OrdinalIgnoreCase) ||
                            packageId.Equals("aitranslation.pack", StringComparison.OrdinalIgnoreCase)) continue;

                        List<string> langRoots = AutoTranslatorScanner.GetAllEffectiveLangPaths(mod.PackageId, mod.RootDir);
                        foreach (var langRoot in langRoots)
                        {
                            string[] searchDirs = targetFolderFilter != null
                                ? (System.IO.Directory.Exists(System.IO.Path.Combine(langRoot, targetFolderFilter)) ? new[] { System.IO.Path.Combine(langRoot, targetFolderFilter) } : new string[0])
                                : System.IO.Directory.GetDirectories(langRoot);

                            foreach (var dir in searchDirs)
                            {
                                var allXmls = AutoTranslatorScanner.GetXmlFilesForTranslationCache(dir, System.IO.SearchOption.AllDirectories);
                                foreach (var file in allXmls) filesToScan.Add(new GlobalSearchFileWorkItem { FilePath = file, Mod = mod.Mod, Category = GetWorkbenchCategoryFromTranslationFile(file) });
                            }
                        }

                        if (targetFolderFilter != null && langFilter.HasValue &&
                            AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(packageId))
                        {
                            foreach (var tarFile in AutoTranslatorScanner.GetOfficialTarTranslationFiles(packageId, mod.RootDir, langFilter.Value))
                            {
                                filesToScan.Add(new GlobalSearchFileWorkItem { TarFile = tarFile, Mod = mod.Mod, Category = tarFile.Category });
                            }
                        }
                    }


                    int totalFiles = filesToScan.Count;
                    if (totalFiles == 0) goto SearchDone;

                    for (int i = 0; i < totalFiles; i++)
                    {
                        var item = filesToScan[i];


                        _globalSearchProgress = (float)i / totalFiles;

                        var dict = item.TarFile != null && langFilter.HasValue
                            ? AutoTranslatorScanner.LoadOfficialTarXmlFileToDict(item.TarFile, langFilter.Value)
                            : AutoTranslatorScanner.LoadXmlFileToDict(item.FilePath);
                        foreach (var kv in dict)
                        {
                            if ((kv.Value != null && kv.Value.ToLowerInvariant().Contains(searchLower)) ||
                                (kv.Key != null && kv.Key.ToLowerInvariant().Contains(searchLower)))
                            {
                                if (!results.Any(r => r.Mod == item.Mod && r.Key == kv.Key && string.Equals(r.Category, item.Category, StringComparison.OrdinalIgnoreCase)))
                                {
                                    results.Add(new GlobalSearchResult
                                    {
                                        Mod = item.Mod,
                                        Key = kv.Key,
                                        Category = item.Category,
                                        TranslatedText = kv.Value,
                                        SearchText = snapshotSearchText
                                    });
                                }
                                if (results.Count >= 200) goto SearchDone;
                            }
                        }
                    }

                SearchDone:

                    _globalSearchProgress = 1f;
                    results = results
                        .OrderBy(result => GetGlobalSearchResultRank(result, searchLower))
                        .ThenBy(result => result.Key ?? "", StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    ATC_Dispatcher.RunOnMainThread(() => {
                        _globalSearchResults = results;
                        _isGlobalSearching = false;
                    });
                });
            }

            private static int GetGlobalSearchResultRank(GlobalSearchResult result, string searchLower)
            {
                if (result == null || string.IsNullOrEmpty(searchLower)) return int.MaxValue;
                string key = (result.Key ?? "").ToLowerInvariant();
                string translated = (result.TranslatedText ?? "").ToLowerInvariant();
                if (key.StartsWith(searchLower) || translated.StartsWith(searchLower)) return 0;
                if (key.Contains(searchLower) || translated.Contains(searchLower)) return 1;
                return int.MaxValue;
            }

            private static string GetWorkbenchCategoryFromTranslationFile(string filePath)
            {
                if (string.IsNullOrWhiteSpace(filePath)) return "Keyed";

                try
                {
                    DirectoryInfo dir = new FileInfo(filePath).Directory;
                    while (dir != null)
                    {
                        if (dir.Name.Equals("Keyed", StringComparison.OrdinalIgnoreCase))
                        {
                            return "Keyed";
                        }

                        if (dir.Parent != null && dir.Parent.Name.Equals("DefInjected", StringComparison.OrdinalIgnoreCase))
                        {
                            return dir.Name;
                        }

                        dir = dir.Parent;
                    }
                }
                catch
                {
                }

                return "Keyed";
            }
        }
}
