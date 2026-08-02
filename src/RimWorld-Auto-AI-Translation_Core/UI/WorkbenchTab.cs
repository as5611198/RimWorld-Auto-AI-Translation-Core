using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責翻譯工作台分頁的入口與狀態切換。
// EN: This file owns the entry point and state switching for the translation workbench tab.

namespace AutoTranslator_Core
{


        // 這個類別負責 翻譯工作台分頁 的主要流程與狀態。
        // EN: This class manages the main workflow and state for TranslationWorkbenchTab.
        public static partial class TranslationWorkbenchTab
        {
            // 這個類別負責 工作台Item 的主要流程與狀態。
            // EN: This class manages the main workflow and state for WorkbenchItem.
            public class WorkbenchItem
            {
                public string Category;
                // 這個欄位保存 Key 的執行狀態或快取資料。
                // EN: This field stores key runtime state or cached data.
                public string Key;
                // 這個欄位保存 OriginalText 的執行狀態或快取資料。
                // EN: This field stores original text runtime state or cached data.
                public string OriginalText;
                // 這個欄位保存 TranslatedText 的執行狀態或快取資料。
                // EN: This field stores translated text runtime state or cached data.
                public string TranslatedText;
                public string OriginalTranslatedText;
                public bool OriginalTranslatedTextIsReadOnlyReference;
                public string SavedTranslatedText;
                public bool SavedTranslatedTextIsReadOnlyReference;
                // 這個欄位保存 IsModified 的執行狀態或快取資料。
                // EN: This field stores is modified runtime state or cached data.
                public bool IsModified;
                public bool IsTranslatingOriginal;
            }

            // 這個欄位保存 editing模組 的執行狀態或快取資料。
            // EN: This field stores editing mod runtime state or cached data.
            private static Verse.ModMetaData _editingMod = null;
            // 這個欄位保存 is載入 的執行狀態或快取資料。
            // EN: This field stores is loading runtime state or cached data.
            private static bool _isLoading = false;
            private static bool _isSavingModifications = false;
            private static bool _isManualXmlBusy = false;
            // 這個欄位保存 mod搜尋Text 的執行狀態或快取資料。
            // EN: This field stores mod search text runtime state or cached data.
            private static string _modSearchText = "";

            // 這個欄位保存 showOnlyTranslated 的執行狀態或快取資料。
            // EN: This field stores show only translated runtime state or cached data.
            private static bool _showOnlyTranslated = false;
            private static bool _showOnlyUntranslated = false;
            // 這個欄位保存 modListScroll 的執行狀態或快取資料。
            // EN: This field stores mod list scroll runtime state or cached data.
            private static UnityEngine.Vector2 _modListScroll = UnityEngine.Vector2.zero;
            // 這個欄位保存 isTranslating模組Names 的執行狀態或快取資料。
            // EN: This field stores is translating mod names runtime state or cached data.
            private static bool _isTranslatingModNames = false;
            // 這個欄位保存 isTranslated模組快取載入 的執行狀態或快取資料。
            // EN: This field stores is translated mods cache loading runtime state or cached data.
            private static bool _isTranslatedModsCacheLoading = false;
            // 這個欄位保存 translated模組快取Error 的執行狀態或快取資料。
            // EN: This field stores translated mods cache error runtime state or cached data.
            private static string _translatedModsCacheError = null;
            private static int _translatedModsCacheGeneration = 0;
            // 這個欄位保存 cached模組選取List 的執行狀態或快取資料。
            // EN: This field stores cached mod selection list runtime state or cached data.
            private static List<Verse.ModMetaData> _cachedModSelectionList = null;
            // 這個欄位保存 cached模組選取搜尋 的執行狀態或快取資料。
            // EN: This field stores cached mod selection search runtime state or cached data.
            private static string _cachedModSelectionSearch = null;
            // 這個欄位保存 cached模組選取ShowTranslatedOnly 的執行狀態或快取資料。
            // EN: This field stores cached mod selection show translated only runtime state or cached data.
            private static bool _cachedModSelectionShowTranslatedOnly = false;
            private static bool _cachedModSelectionShowUntranslatedOnly = false;
            // 這個欄位保存 cached模組選取翻譯Names 的執行狀態或快取資料。
            // EN: This field stores cached mod selection translate names runtime state or cached data.
            private static bool _cachedModSelectionTranslateNames = false;
            // 這個欄位保存 cached模組選取ValidCount 的執行狀態或快取資料。
            // EN: This field stores cached mod selection valid count runtime state or cached data.
            private static int _cachedModSelectionValidCount = -1;
            // 這個欄位保存 cached模組選取TranslatedCount 的執行狀態或快取資料。
            // EN: This field stores cached mod selection translated count runtime state or cached data.
            private static int _cachedModSelectionTranslatedCount = -1;
            private static int _cachedModSelectionTranslatedHash = 0;
            private static int _cachedModSelectionValidVersion = -1;
            private static int _lastStablePackageHash = 0;
            private static int _lastStablePackageHashCount = -1;
            private static int _lastStablePackageHashGeneration = -1;

            private static Dictionary<string, List<WorkbenchItem>> _categorizedData = new Dictionary<string, List<WorkbenchItem>>();
            private const string AllWorkbenchCategoriesView = "__ATC_ALL_CATEGORIES__";
            // 這個欄位保存 selectedCategory 的執行狀態或快取資料。
            // EN: This field stores selected category runtime state or cached data.
            private static string _selectedCategory = "";
            // 這個欄位保存 item搜尋Text 的執行狀態或快取資料。
            // EN: This field stores item search text runtime state or cached data.
            private static string _itemSearchText = "";
            // 這個欄位保存 catListScroll 的執行狀態或快取資料。
            // EN: This field stores cat list scroll runtime state or cached data.
            private static UnityEngine.Vector2 _catListScroll = UnityEngine.Vector2.zero;
            // 這個欄位保存 itemScroll 的執行狀態或快取資料。
            // EN: This field stores item scroll runtime state or cached data.
            private static UnityEngine.Vector2 _itemScroll = UnityEngine.Vector2.zero;
            private const float WorkbenchCategoryRowHeight = 35f;
            private const float WorkbenchItemRowHeight = 100f;

            internal enum WorkbenchBatchReplaceScope
            {
                VisibleResults,
                CurrentCategory,
                AllCategories
            }

            // 這個欄位保存 translatedPackageIds 的執行狀態或快取資料。
            // EN: This field stores translated package ids runtime state or cached data.
            private static HashSet<string> _translatedPackageIds = null;

            // 這個欄位保存 global搜尋Text 的執行狀態或快取資料。
            // EN: This field stores global search text runtime state or cached data.
            private static string _globalSearchText = "";
            // 這個欄位保存 isGlobalSearching 的執行狀態或快取資料。
            // EN: This field stores is global searching runtime state or cached data.
            private static bool _isGlobalSearching = false;
            // 這個欄位保存 global搜尋Scroll 的執行狀態或快取資料。
            // EN: This field stores global search scroll runtime state or cached data.
            private static UnityEngine.Vector2 _globalSearchScroll = UnityEngine.Vector2.zero;
            private static List<GlobalSearchResult> _globalSearchResults = new List<GlobalSearchResult>();
            private static string _globalSearchSnapshotText = "";
            private static TargetLanguage? _globalSearchSnapshotLangFilter = null;
            private static bool _globalSearchHasSnapshot = false;
            private static List<WorkbenchItem> _cachedVisibleItems = null;
            private static string _cachedVisibleCategory = "";
            private static string _cachedVisibleSearchText = "";
            private static string _cachedVisibleFocusCategory = "";
            private static string _cachedVisibleFocusKey = "";
            private static string _cachedVisibleRetainedCategory = "";
            private static string _cachedVisibleRetainedKey = "";
            private static int _cachedVisibleSourceCount = -1;
            private static int _categorizedDataVersion = 0;
            private static int _cachedVisibleDataVersion = -1;
            private static List<WorkbenchItem> _cachedAllCategoryItems = null;
            private static int _cachedAllCategoryItemsDataVersion = -1;
            private static WorkbenchFocusRequest _pendingWorkbenchFocus = null;
            private static WorkbenchFocusRequest _activeWorkbenchFocus = null;
            private static string _retainedEditedCategory = "";
            private static string _retainedEditedKey = "";
            private static string _workbenchStatusText = "";
            private static float _workbenchStatusUntilTime = 0f;
            // 這個欄位保存 global搜尋語言Filter 的執行狀態或快取資料。
            // EN: This field stores global search language filter runtime state or cached data.
            private static TargetLanguage? _globalSearchLangFilter = null;
            // 這個欄位保存 global搜尋Progress 的執行狀態或快取資料。
            // EN: This field stores global search progress runtime state or cached data.
            private static float _globalSearchProgress = 0f;
            // 這個類別負責 Global搜尋Result 的主要流程與狀態。
            // EN: This class manages the main workflow and state for GlobalSearchResult.
            public class GlobalSearchResult
            {
                // 這個欄位保存 模組 的執行狀態或快取資料。
                // EN: This field stores mod runtime state or cached data.
                public Verse.ModMetaData Mod;
                // 這個欄位保存 Key 的執行狀態或快取資料。
                // EN: This field stores key runtime state or cached data.
                public string Key;
                public string Category;
                // 這個欄位保存 TranslatedText 的執行狀態或快取資料。
                // EN: This field stores translated text runtime state or cached data.
                public string TranslatedText;
                public bool OriginalTranslatedTextIsReadOnlyReference;
                public string SearchText;
            }

            private class WorkbenchFocusRequest
            {
                public string Category;
                public string Key;
                public string SearchText;
                public string MatchedText;
                public bool FromGlobalSearch;
            }

            private class GlobalSearchModSnapshot
            {
                public Verse.ModMetaData Mod;
                public string PackageId;
                public string ModName;
                public string RootDir;
            }

            private class GlobalSearchFileWorkItem
            {
                public string FilePath;
                public AutoTranslatorScanner.OfficialTarTranslationFile TarFile;
                public Verse.ModMetaData Mod;
                public string Category;
            }

            private class WorkbenchModSnapshot
            {
                public Verse.ModMetaData Mod;
                public string PackageId;
                public string ModName;
                public string RootDir;
                public TargetLanguage TargetLang;
                public string TargetLangFolder;
            }

            private class WorkbenchSaveSnapshot
            {
                public Verse.ModMetaData Mod;
                public string PackageId;
                public string RootDir;
                public TargetLanguage TargetLang;
                public string TargetLangFolder;
                public string PackPath;
                public string CleanPackageId;
                public List<WorkbenchSaveCategorySnapshot> Categories = new List<WorkbenchSaveCategorySnapshot>();
            }

            private class WorkbenchSaveCategorySnapshot
            {
                public string Category;
                public List<WorkbenchSaveItemSnapshot> Items = new List<WorkbenchSaveItemSnapshot>();
                public HashSet<string> ClearKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            private class WorkbenchSaveItemSnapshot
            {
                public string Key;
                public string OriginalText;
                public string TranslatedText;
                public string OriginalTranslatedText;
                public bool OriginalTranslatedTextIsReadOnlyReference;
                public string SavedTranslatedText;
                public bool SavedTranslatedTextIsReadOnlyReference;
                public bool IsModified;
            }

            private class WorkbenchSaveResult
            {
                public int SavedCount;
                public bool TouchedTranslationFiles;
                public bool HasSavedTranslation;
                public Dictionary<string, HashSet<string>> ClearKeysByDefType = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                public ModUpdateDetector.SourceFingerprintSnapshot SourceFingerprint;
                public string Error;
            }
            // 這個方法負責繪製 Editor分頁 介面。
            // EN: This method draws editor tab.
            public static void DrawEditorTab(Verse.Listing_Standard l, UnityEngine.Rect viewRect)
            {
                AutoTranslatorMod.GetValidModsCached();
                if (AutoTranslatorMod.Settings.TranslateWorkbenchModNames)
                {
                    ModNameTranslationCache.PreloadAsync();
                }
                InitTranslatedModsCache();

                float contentHeight = 600f;
                float leftWidth = Mathf.Min(360f, viewRect.width * 0.36f);
                float spacing = 15f;
                float rightWidth = viewRect.width - leftWidth - spacing;

                UnityEngine.Rect fullRect = l.GetRect(contentHeight);
                UnityEngine.Rect leftOutRect = new UnityEngine.Rect(fullRect.x, fullRect.y, leftWidth, contentHeight);
                UnityEngine.Rect rightOutRect = new UnityEngine.Rect(fullRect.x + leftWidth + spacing, fullRect.y, rightWidth, contentHeight);

                Verse.Widgets.DrawBoxSolid(leftOutRect, new UnityEngine.Color(0.1f, 0.1f, 0.1f, 0.5f));
                Verse.Widgets.DrawBoxSolid(rightOutRect, new UnityEngine.Color(0.05f, 0.05f, 0.05f, 0.5f));


                if (_editingMod == null)
                {
                    DrawModSelectionMode(leftOutRect, rightOutRect);
                }
                else
                {
                    DrawEditingMode(leftOutRect, rightOutRect);
                }
            }

        }
}
