using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責 翻譯工作台分頁模組選取繪製 相關邏輯，支援 Auto Translation Core 的執行流程。
// EN: This file contains translation workbench tab mod selection renderer support code.

namespace AutoTranslator_Core
{
    // 這個類別負責 翻譯工作台分頁 的主要流程與狀態。
    // EN: This class manages the main workflow and state for TranslationWorkbenchTab.
    public static partial class TranslationWorkbenchTab
    {
        // 這個常數定義 模組RowHeight 的固定值。
        // EN: This constant defines the fixed value for mod row height.
        private const float ModRowHeight = 44f;
        // 這個常數定義 搜尋ResultRowHeight 的固定值。
        // EN: This constant defines the fixed value for search result row height.
        private const float SearchResultRowHeight = 65f;

        // 這個方法負責繪製 模組選取Mode 介面。
        // EN: This method draws mod selection mode.
        private static void DrawModSelectionMode(Rect leftOutRect, Rect rightOutRect)
        {
            Rect searchRect = new Rect(leftOutRect.x + 5f, leftOutRect.y + 5f, leftOutRect.width - 10f, 30f);
            _modSearchText = Widgets.TextField(searchRect, _modSearchText);
            if (string.IsNullOrEmpty(_modSearchText))
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(searchRect.x + 5f, searchRect.y + 2f, searchRect.width, searchRect.height), "ATC_Workbench_SearchMod".Translate());
                GUI.color = Color.white;
            }

            Rect translatedFilterRect = new Rect(leftOutRect.x + 5f, leftOutRect.y + 40f, leftOutRect.width - 10f, 24f);
            bool oldShowTranslated = _showOnlyTranslated;
            Widgets.CheckboxLabeled(translatedFilterRect, "ATC_Workbench_ShowTranslatedOnly".Translate(), ref _showOnlyTranslated);
            if (_showOnlyTranslated != oldShowTranslated && _showOnlyTranslated)
            {
                _showOnlyUntranslated = false;
                _cachedModSelectionList = null;
                _modListScroll = Vector2.zero;
            }

            Rect untranslatedFilterRect = new Rect(leftOutRect.x + 5f, leftOutRect.y + 64f, leftOutRect.width - 10f, 24f);
            bool oldShowUntranslated = _showOnlyUntranslated;
            Widgets.CheckboxLabeled(untranslatedFilterRect, "ATC_Workbench_ShowUntranslatedOnly".Translate(), ref _showOnlyUntranslated);
            if (_showOnlyUntranslated != oldShowUntranslated && _showOnlyUntranslated)
            {
                _showOnlyTranslated = false;
                _cachedModSelectionList = null;
                _modListScroll = Vector2.zero;
            }

            Widgets.DrawLineHorizontal(leftOutRect.x, leftOutRect.y + 94f, leftOutRect.width);

            DrawModList(leftOutRect);
            DrawGlobalSearchPanel(rightOutRect);
        }

        // 這個方法負責繪製 模組List 介面。
        // EN: This method draws mod list.
        private static void DrawModList(Rect leftOutRect)
        {
            HashSet<string> translatedPackageIds = GetTranslatedPackageIdsSafe();
            List<ModMetaData> displayMods = GetModSelectionDisplayMods(translatedPackageIds);
            bool showModListProgress = AutoTranslatorMod.IsValidModsCacheRefreshing && displayMods.Count == 0;
            float listTop = showModListProgress ? 166f : 99f;

            if (showModListProgress)
            {
                DrawModListLoadingProgress(new Rect(leftOutRect.x, leftOutRect.y + 99f, leftOutRect.width, 62f));
            }

            Rect listOutRect = new Rect(leftOutRect.x, leftOutRect.y + listTop, leftOutRect.width, leftOutRect.height - listTop);
            Rect listViewRect = new Rect(0, 0, listOutRect.width - 20f, Mathf.Max(1f, displayMods.Count * ModRowHeight));
            int firstVisible = Mathf.Max(0, Mathf.FloorToInt(_modListScroll.y / ModRowHeight) - 2);
            int lastVisible = Mathf.Min(displayMods.Count - 1, Mathf.CeilToInt((_modListScroll.y + listOutRect.height) / ModRowHeight) + 2);

            if (firstVisible <= lastVisible)
            {
                QueueVisibleModNameTranslations(displayMods, firstVisible, lastVisible);
            }

            Widgets.BeginScrollView(listOutRect, ref _modListScroll, listViewRect);
            try
            {
                for (int i = firstVisible; i <= lastVisible; i++)
                {
                    ModMetaData mod = displayMods[i];
                    if (mod == null) continue;

                    Rect rowRect = new Rect(5f, i * ModRowHeight, listViewRect.width - 5f, ModRowHeight - 5f);
                    Widgets.DrawHighlightIfMouseover(rowRect);

                    if (Widgets.ButtonInvisible(rowRect))
                    {
                        StartLoadingModForEditing(mod, "");
                    }

                    bool isTranslated = translatedPackageIds.Contains(mod.PackageId ?? "");
                    GUI.color = isTranslated ? new Color(0.6f, 1f, 0.6f) : Color.white;

                    Text.WordWrap = false;
                    string displayName = (isTranslated ? "* " : "") + GetDisplayModName(mod);
                    Widgets.Label(new Rect(rowRect.x + 5f, rowRect.y + 5f, rowRect.width - 10f, rowRect.height - 10f), displayName);
                    TooltipHandler.TipRegion(rowRect, $"{GetDisplayModName(mod)}\n{mod.PackageId}");
                    GUI.color = Color.white;
                }
            }
            finally
            {
                Text.WordWrap = true;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                Widgets.EndScrollView();
            }

            if (_isTranslatedModsCacheLoading)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(leftOutRect.x + 8f, leftOutRect.yMax - 22f, leftOutRect.width - 16f, 18f), "Loading translation cache...");
                GUI.color = Color.white;
            }
        }

        private static void DrawModListLoadingProgress(Rect panelRect)
        {
            int current = AutoTranslatorMod.ValidModsCacheProgressCurrent;
            int total = AutoTranslatorMod.ValidModsCacheProgressTotal;
            float progress = AutoTranslatorMod.ValidModsCacheProgress;
            string currentMod = AutoTranslatorMod.ValidModsCacheProgressModName;

            panelRect = new Rect(panelRect.x + 5f, panelRect.y, panelRect.width - 10f, panelRect.height);
            Widgets.DrawBoxSolid(panelRect, new Color(0.08f, 0.08f, 0.08f, 0.85f));

            GUI.color = Color.gray;
            string label = total > 0
                ? $"Loading mod list... {current}/{total}"
                : "Loading mod list...";
            Widgets.Label(new Rect(panelRect.x + 8f, panelRect.y + 5f, panelRect.width - 16f, 18f), label);

            Rect barRect = new Rect(panelRect.x + 8f, panelRect.y + 26f, panelRect.width - 16f, 18f);
            GUI.color = Color.white;
            Widgets.FillableBar(barRect, progress);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(barRect, $"{(progress * 100f):F0}%");
            Text.Anchor = TextAnchor.UpperLeft;

            GUI.color = Color.gray;
            string currentLabel = string.IsNullOrWhiteSpace(currentMod) ? "" : currentMod;
            Text.WordWrap = false;
            Widgets.Label(new Rect(panelRect.x + 8f, panelRect.y + 44f, panelRect.width - 16f, 16f), currentLabel);
            Text.WordWrap = true;
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }

        // 這個方法負責繪製 Global搜尋Panel 介面。
        // EN: This method draws global search panel.
        private static void DrawGlobalSearchPanel(Rect rightOutRect)
        {
            Text.Font = GameFont.Medium;
            string searchTitle = "ATC_Workbench_GlobalSearchTitle".CanTranslate()
                ? "ATC_Workbench_GlobalSearchTitle".Translate().ToString()
                : "Global Search";
            Widgets.Label(new Rect(rightOutRect.x + 10f, rightOutRect.y + 5f, rightOutRect.width, 30f), searchTitle);
            Text.Font = GameFont.Small;

            Rect searchBoxRect = new Rect(rightOutRect.x + 10f, rightOutRect.y + 40f, rightOutRect.width - 240f, 30f);
            _globalSearchText = Widgets.TextField(searchBoxRect, _globalSearchText);

            Rect langFilterRect = new Rect(rightOutRect.xMax - 220f, rightOutRect.y + 40f, 110f, 30f);
            string allLabel = "ATC_Lang_All".CanTranslate() ? "ATC_Lang_All".Translate().ToString() : "All";
            string filterLabel = _globalSearchLangFilter.HasValue ? _globalSearchLangFilter.Value.ToString() : allLabel;
            if (Widgets.ButtonText(langFilterRect, filterLabel))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption(allLabel, () => _globalSearchLangFilter = null)
                };
                foreach (TargetLanguage lang in Enum.GetValues(typeof(TargetLanguage)))
                {
                    TargetLanguage captureLang = lang;
                    opts.Add(new FloatMenuOption(captureLang.ToString(), () => _globalSearchLangFilter = captureLang));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            GUI.color = new Color(0.4f, 0.8f, 1f);
            string searchBtnLabel = "ATC_Btn_Search".CanTranslate() ? "ATC_Btn_Search".Translate().ToString() : "Search";
            if (Widgets.ButtonText(new Rect(rightOutRect.xMax - 100f, rightOutRect.y + 40f, 90f, 30f), searchBtnLabel))
            {
                if (!string.IsNullOrWhiteSpace(_globalSearchText) && !_isGlobalSearching)
                {
                    ExecuteGlobalSearch(_globalSearchText, _globalSearchLangFilter);
                }
            }
            GUI.color = Color.white;

            Rect snapshotRect = new Rect(rightOutRect.x + 10f, rightOutRect.y + 74f, rightOutRect.width - 20f, 20f);
            DrawGlobalSearchSnapshotStatus(snapshotRect);

            Widgets.DrawLineHorizontal(rightOutRect.x, rightOutRect.y + 100f, rightOutRect.width);

            if (_isGlobalSearching)
            {
                DrawGlobalSearchLoading(rightOutRect);
            }
            else if (_globalSearchResults.Count > 0)
            {
                DrawGlobalSearchResults(rightOutRect);
            }
            else if (!string.IsNullOrWhiteSpace(_globalSearchText))
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = Color.gray;
                string notFoundMsg = "ATC_Workbench_GlobalSearchNoResult".CanTranslate()
                    ? "ATC_Workbench_GlobalSearchNoResult".Translate().ToString()
                    : "No results";
                Widgets.Label(new Rect(rightOutRect.x, rightOutRect.y + 110f, rightOutRect.width, 100f), notFoundMsg);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
            }
        }

        private static void DrawGlobalSearchSnapshotStatus(Rect statusRect)
        {
            if (!_globalSearchHasSnapshot) return;

            bool isCurrentSearchText = string.Equals((_globalSearchText ?? "").Trim(), _globalSearchSnapshotText ?? "", StringComparison.Ordinal);
            GUI.color = isCurrentSearchText ? Color.gray : new Color(1f, 0.82f, 0.35f);

            string statusKey = isCurrentSearchText
                ? "ATC_Workbench_SearchSnapshotLabel"
                : "ATC_Workbench_SearchTextChanged";
            string fallback = isCurrentSearchText
                ? $"Showing results for: {_globalSearchSnapshotText}"
                : "Search text changed. Press Search to update results.";
            string status = statusKey.CanTranslate()
                ? statusKey.Translate(_globalSearchSnapshotText ?? "").ToString()
                : fallback;
            if (_globalSearchSnapshotLangFilter.HasValue)
            {
                status += $" [{_globalSearchSnapshotLangFilter.Value}]";
            }

            Text.Font = GameFont.Tiny;
            Text.WordWrap = false;
            Widgets.Label(statusRect, status);
            Text.WordWrap = true;
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }

        // 這個方法負責繪製 Global搜尋載入 介面。
        // EN: This method draws global search loading.
        private static void DrawGlobalSearchLoading(Rect rightOutRect)
        {
            Text.Anchor = TextAnchor.MiddleCenter;
            Rect loadingArea = new Rect(rightOutRect.x, rightOutRect.y + 110f, rightOutRect.width, 80f);

            GUI.color = Color.yellow;
            string loadingMsg = "ATC_Workbench_GlobalSearching".CanTranslate()
                ? "ATC_Workbench_GlobalSearching".Translate().ToString()
                : "Searching...";
            Widgets.Label(new Rect(loadingArea.x, loadingArea.y, loadingArea.width, 30f), loadingMsg);
            GUI.color = Color.white;

            float barWidth = loadingArea.width * 0.6f;
            Rect barRect = new Rect(loadingArea.x + (loadingArea.width - barWidth) / 2f, loadingArea.y + 35f, barWidth, 22f);
            Widgets.FillableBar(barRect, _globalSearchProgress);

            Text.Font = GameFont.Tiny;
            Widgets.Label(barRect, $"{(_globalSearchProgress * 100f):F0}%");
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // 這個方法負責繪製 Global搜尋Results 介面。
        // EN: This method draws global search results.
        private static void DrawGlobalSearchResults(Rect rightOutRect)
        {
            Rect resOutRect = new Rect(rightOutRect.x, rightOutRect.y + 105f, rightOutRect.width, rightOutRect.height - 105f);
            Rect resViewRect = new Rect(0, 0, resOutRect.width - 20f, Mathf.Max(1f, _globalSearchResults.Count * SearchResultRowHeight));
            int firstVisible = Mathf.Max(0, Mathf.FloorToInt(_globalSearchScroll.y / SearchResultRowHeight) - 2);
            int lastVisible = Mathf.Min(_globalSearchResults.Count - 1, Mathf.CeilToInt((_globalSearchScroll.y + resOutRect.height) / SearchResultRowHeight) + 2);

            Widgets.BeginScrollView(resOutRect, ref _globalSearchScroll, resViewRect);
            try
            {
                for (int i = firstVisible; i <= lastVisible; i++)
                {
                    GlobalSearchResult res = _globalSearchResults[i];
                    if (res == null || res.Mod == null) continue;

                    Rect rowRect = new Rect(5f, i * SearchResultRowHeight, resViewRect.width - 5f, 60f);
                    Widgets.DrawBoxSolid(rowRect, new Color(0.15f, 0.15f, 0.15f, 0.8f));
                    Widgets.DrawHighlightIfMouseover(rowRect);

                    if (Widgets.ButtonInvisible(rowRect))
                    {
                        StartLoadingModForEditing(res.Mod, new WorkbenchFocusRequest
                        {
                            Category = res.Category,
                            Key = res.Key,
                            SearchText = res.SearchText,
                            MatchedText = res.TranslatedText,
                            FromGlobalSearch = true
                        });
                    }

                    Text.Font = GameFont.Tiny;
                    GUI.color = Color.gray;
                    string categoryText = string.IsNullOrWhiteSpace(res.Category) ? "Keyed" : res.Category;
                    Widgets.Label(new Rect(rowRect.x + 5f, rowRect.y + 2f, rowRect.width, 15f), $"[{res.Mod.Name}] {categoryText} / {res.Key}");

                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;
                    Widgets.Label(new Rect(rowRect.x + 5f, rowRect.y + 20f, rowRect.width - 10f, 35f), res.TranslatedText);
                }
            }
            finally
            {
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                Widgets.EndScrollView();
            }
        }

        // 這個方法負責取得 CachedTranslated模組名稱 資料。
        // EN: This method gets cached translated mod name.
        private static string GetCachedTranslatedModName(ModMetaData mod)
        {
            if (mod == null) return "";
            return ModNameTranslationCache.TryGet(mod, out string translated) ? translated : "";
        }

        // 這個方法負責取得 Display模組名稱 資料。
        // EN: This method gets display mod name.
        private static string GetDisplayModName(ModMetaData mod)
        {
            if (mod == null) return "";
            if (!AutoTranslatorMod.Settings.TranslateWorkbenchModNames) return mod.Name;

            string translated = GetCachedTranslatedModName(mod);
            if (string.IsNullOrWhiteSpace(translated) || string.Equals(translated.Trim(), mod.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return mod.Name;
            }

            return $"{translated} / {mod.Name}";
        }

        // 這個方法負責取得 模組選取Display模組 資料。
        // EN: This method gets mod selection display mods.
        private static List<ModMetaData> GetModSelectionDisplayMods(HashSet<string> translatedPackageIds)
        {
            translatedPackageIds = translatedPackageIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ModMetaData> validMods = AutoTranslatorMod.GetValidModsCached() ?? new List<ModMetaData>();
            string searchText = _modSearchText ?? "";
            bool translateNames = AutoTranslatorMod.Settings.TranslateWorkbenchModNames;
            int translatedHash = GetStablePackageHash(translatedPackageIds);

            if (_cachedModSelectionList != null &&
                _cachedModSelectionSearch == searchText &&
                _cachedModSelectionShowTranslatedOnly == _showOnlyTranslated &&
                _cachedModSelectionShowUntranslatedOnly == _showOnlyUntranslated &&
                _cachedModSelectionTranslateNames == translateNames &&
                _cachedModSelectionValidCount == validMods.Count &&
                _cachedModSelectionValidVersion == AutoTranslatorMod.ValidModsCacheVersion &&
                _cachedModSelectionTranslatedCount == translatedPackageIds.Count &&
                _cachedModSelectionTranslatedHash == translatedHash)
            {
                return _cachedModSelectionList;
            }

            IEnumerable<ModMetaData> allMods = validMods.Where(m =>
                m != null && !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(m.PackageId));

            if (_showOnlyTranslated)
            {
                allMods = allMods.Where(m => translatedPackageIds.Contains(m.PackageId ?? ""));
            }
            else if (_showOnlyUntranslated)
            {
                allMods = allMods.Where(m => !translatedPackageIds.Contains(m.PackageId ?? ""));
            }

            if (!string.IsNullOrEmpty(searchText))
            {
                string searchLower = searchText.ToLowerInvariant();
                allMods = allMods.Where(m =>
                    ((m.Name ?? "").ToLowerInvariant().Contains(searchLower)) ||
                    ((m.PackageId ?? "").ToLowerInvariant().Contains(searchLower)) ||
                    (translateNames && GetCachedTranslatedModName(m).ToLowerInvariant().Contains(searchLower)));
            }

            _cachedModSelectionList = allMods.ToList();
            _cachedModSelectionSearch = searchText;
            _cachedModSelectionShowTranslatedOnly = _showOnlyTranslated;
            _cachedModSelectionShowUntranslatedOnly = _showOnlyUntranslated;
            _cachedModSelectionTranslateNames = translateNames;
            _cachedModSelectionValidCount = validMods.Count;
            _cachedModSelectionValidVersion = AutoTranslatorMod.ValidModsCacheVersion;
            _cachedModSelectionTranslatedCount = translatedPackageIds.Count;
            _cachedModSelectionTranslatedHash = translatedHash;
            return _cachedModSelectionList;
        }

        private static int GetStablePackageHash(HashSet<string> packageIds)
        {
            if (packageIds == null || packageIds.Count == 0) return 0;
            if (_lastStablePackageHashGeneration == _translatedModsCacheGeneration &&
                _lastStablePackageHashCount == packageIds.Count)
            {
                return _lastStablePackageHash;
            }

            unchecked
            {
                int hash = 17;
                foreach (string packageId in packageIds)
                {
                    hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(packageId ?? "");
                }

                _lastStablePackageHash = hash;
                _lastStablePackageHashCount = packageIds.Count;
                _lastStablePackageHashGeneration = _translatedModsCacheGeneration;
                return hash;
            }
        }

        // 這個方法負責排入 Visible模組名稱Translations 佇列。
        // EN: This method queues visible mod name translations.
        private static void QueueVisibleModNameTranslations(List<ModMetaData> displayMods, int firstVisible, int lastVisible)
        {
            if (!AutoTranslatorMod.Settings.TranslateWorkbenchModNames) return;
            if (AutoTranslatorSettings.IsRunning) return;
            if (_isTranslatingModNames) return;
            if (displayMods == null || displayMods.Count == 0) return;
            if (!AutoTranslatorAPI.HasAnyReadyConfig()) return;
            if (!ModNameTranslationCache.TryBeginVisibleQueue(displayMods, firstVisible, lastVisible)) return;

            List<ModMetaData> pending = new List<ModMetaData>(4);
            for (int i = firstVisible; i <= lastVisible && i < displayMods.Count; i++)
            {
                ModMetaData mod = displayMods[i];
                if (mod == null) continue;
                if (ModNameTranslationCache.TryGet(mod, out _)) continue;
                if (!ModNameTranslationCache.TryMarkQueued(mod)) continue;

                pending.Add(mod);
                if (pending.Count >= 4) break;
            }

            if (pending.Count == 0) return;

            _isTranslatingModNames = true;
            TargetLanguage targetLanguage = AutoTranslatorMod.Settings.TargetLang;
            Task.Run(async () =>
            {
                try
                {
                    List<string> translatedNames = await AutoTranslatorAPI.TranslateBatchAsync(
                        pending.Select(m => m.Name).ToList(),
                        suppressFinalParseError: true);

                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        try
                        {
                            if (translatedNames != null && translatedNames.Count == pending.Count && AutoTranslatorMod.Settings.TargetLang == targetLanguage)
                            {
                                for (int i = 0; i < pending.Count; i++)
                                {
                                    string translated = translatedNames[i]?.Trim() ?? "";
                                    if (!string.IsNullOrWhiteSpace(translated))
                                    {
                                        ModNameTranslationCache.Store(pending[i], translated);
                                    }
                                }
                                ModNameTranslationCache.SaveIfDirty();
                            }
                            else
                            {
                                ModNameTranslationCache.MarkFailed(pending);
                            }
                        }
                        finally
                        {
                            ModNameTranslationCache.ReleaseQueued(pending);
                            _isTranslatingModNames = false;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Verse.Log.Warning($"[AutoTranslationCore] Workbench mod-name translation failed: {ex.Message}");
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        ModNameTranslationCache.MarkFailed(pending);
                        ModNameTranslationCache.ReleaseQueued(pending);
                        _isTranslatingModNames = false;
                    });
                }
            });
        }
    }
}
