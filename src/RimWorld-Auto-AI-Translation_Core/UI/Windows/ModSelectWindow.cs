using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
// 這個檔案負責模組選取視窗內容。
// EN: This file draws the mod selection window.

namespace AutoTranslator_Core
{
    // 這個類別負責 模組Select視窗 的主要流程與狀態。
    // EN: This class manages the main workflow and state for ModSelectWindow.
    public class ModSelectWindow : Window
    {
        // 這個常數定義 RowHeight 的固定值。
        // EN: This constant defines the fixed value for row height.
        private const float RowHeight = 60f;

        // 這個欄位保存 searchText 的執行狀態或快取資料。
        // EN: This field stores search text runtime state or cached data.
        private string searchText = "";
        // 這個欄位保存 scrollPos 的執行狀態或快取資料。
        // EN: This field stores scroll pos runtime state or cached data.
        private Vector2 scrollPos = Vector2.zero;
        private readonly HashSet<ModMetaData> selectedMods = new HashSet<ModMetaData>();
        // 這個欄位保存 preSelected模組 的執行狀態或快取資料。
        // EN: This field stores pre selected mods runtime state or cached data.
        private readonly List<ModMetaData> preSelectedMods;
        // 這個欄位保存 drag目標狀態 的執行狀態或快取資料。
        // EN: This field stores drag target state runtime state or cached data.
        private bool? dragTargetState = null;
        // 這個欄位保存 isTranslating模組Names 的執行狀態或快取資料。
        // EN: This field stores is translating mod names runtime state or cached data.
        private static bool isTranslatingModNames = false;
        private List<ModMetaData> cachedDisplayMods = null;
        private string cachedSearchText = null;
        private int cachedValidModCount = -1;
        private int cachedValidModVersion = -1;
        private readonly HashSet<string> queuedStatusChecks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object statusCheckLock = new object();
        private static readonly HashSet<string> statusChecksInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int MaxStatusChecksPerPass = 3;

        private sealed class PendingStatusCheck
        {
            public string Key;
            public string PackageId;
            public ModUpdateDetector.TranslationStatusCheckSnapshot Snapshot;
        }

        // 這個屬性提供 InitialSize 的讀寫或計算結果。
        // EN: This method handles vector2.
        public override Vector2 InitialSize => new Vector2(600f, 700f);

        // 這個方法負責處理 模組Select視窗 相關流程。
        // EN: This constructor initializes mod select window.
        public ModSelectWindow(List<ModMetaData> updatedMods = null)
        {
            doCloseButton = false;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            preSelectedMods = updatedMods ?? new List<ModMetaData>();

            if (AutoTranslatorMod.Settings.AutoClearOldOnUpdate)
            {
                foreach (var mod in preSelectedMods)
                {
                    if (!AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(mod.PackageId))
                    {
                        selectedMods.Add(mod);
                    }
                }
            }
        }

        // 這個方法負責處理 Do視窗Contents 相關流程。
        // EN: This method handles do window contents.
        public override void DoWindowContents(Rect inRect)
        {
            bool previousBypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0, 0, inRect.width, 40f), "ATC_MultiSelect_Title".Translate());
                Text.Font = GameFont.Small;
                if (AutoTranslatorMod.Settings.TranslateWorkbenchModNames)
                {
                    ModNameTranslationCache.PreloadAsync();
                }

                Rect searchRect = new Rect(0, 45f, inRect.width, 30f);
                searchText = Widgets.TextField(searchRect, searchText);
                if (string.IsNullOrEmpty(searchText))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(searchRect.x + 5f, searchRect.y + 2f, searchRect.width, searchRect.height), "ATC_MultiSelect_Search".Translate());
                    GUI.color = Color.white;
                }

                List<ModMetaData> displayMods = GetDisplayMods();
                DrawSelectionButtons(inRect, displayMods);
                DrawModList(inRect, displayMods);
                DrawStartButton(inRect);
            }
            finally
            {
                Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
            }
        }

        // 這個方法負責取得 Display模組 資料。
        // EN: This method gets display mods.
        private List<ModMetaData> GetDisplayMods()
        {
            List<ModMetaData> validMods = AutoTranslatorMod.GetValidModsCached();
            int currentValidCount = validMods.Count;
            if (cachedDisplayMods != null &&
                cachedValidModCount == currentValidCount &&
                cachedValidModVersion == AutoTranslatorMod.ValidModsCacheVersion &&
                string.Equals(cachedSearchText, searchText ?? "", StringComparison.Ordinal))
            {
                return cachedDisplayMods;
            }

            IEnumerable<ModMetaData> mods = validMods
                .Where(m => !AutoTranslatorScanner.IsTranslationPatchMod(m) &&
                            !AutoTranslatorMod.Settings.IsTranslationBlacklisted(m.PackageId));

            if (!string.IsNullOrEmpty(searchText))
            {
                string searchLower = searchText.ToLowerInvariant();
                mods = mods.Where(m =>
                    (m.Name ?? "").ToLowerInvariant().Contains(searchLower) ||
                    (m.PackageId ?? "").ToLowerInvariant().Contains(searchLower) ||
                    GetCachedTranslatedModName(m).ToLowerInvariant().Contains(searchLower));
            }

            cachedDisplayMods = mods.OrderBy(m => m.Name).ToList();
            cachedSearchText = searchText ?? "";
            cachedValidModCount = currentValidCount;
            cachedValidModVersion = AutoTranslatorMod.ValidModsCacheVersion;
            return cachedDisplayMods;
        }

        // 這個方法負責繪製 選取Buttons 介面。
        // EN: This method draws selection buttons.
        private void DrawSelectionButtons(Rect inRect, List<ModMetaData> displayMods)
        {
            Rect btnRow = new Rect(0, 85f, inRect.width, 30f);
            List<ModMetaData> defaultSelectableMods = displayMods
                .Where(m => !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(m.PackageId))
                .ToList();
            bool isAllSelected = defaultSelectableMods.Count > 0 && defaultSelectableMods.All(m => selectedMods.Contains(m));
            string btnLabel = isAllSelected ? "ATC_DeselectAll".Translate().ToString() : "ATC_SelectAll".Translate().ToString();

            if (Widgets.ButtonText(new Rect(btnRow.x, btnRow.y, 120f, btnRow.height), btnLabel))
            {
                if (isAllSelected)
                {
                    foreach (var mod in displayMods) selectedMods.Remove(mod);
                }
                else
                {
                    foreach (var mod in defaultSelectableMods) selectedMods.Add(mod);
                }
            }

            GUI.color = new Color(1f, 0.6f, 0.8f);
            if (Widgets.ButtonText(new Rect(btnRow.x + 130f, btnRow.y, 120f, btnRow.height), "ATC_One_click_chaos".Translate()))
            {
                selectedMods.Clear();
                var rand = new System.Random();
                foreach (var mod in displayMods)
                {
                    if (!AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(mod.PackageId) && rand.NextDouble() > 0.5)
                    {
                        selectedMods.Add(mod);
                    }
                }
            }
            GUI.color = Color.white;
        }

        // 這個方法負責繪製 模組List 介面。
        // EN: This method draws mod list.
        private void DrawModList(Rect inRect, List<ModMetaData> displayMods)
        {
            Widgets.DrawLineHorizontal(0, 120f, inRect.width);
            Rect listOutRect = new Rect(0, 130f, inRect.width, inRect.height - 180f);
            Rect viewRect = new Rect(0, 0, listOutRect.width - 20f, displayMods.Count * RowHeight);

            Widgets.BeginScrollView(listOutRect, ref scrollPos, viewRect);

            if (Event.current.type == EventType.MouseUp) dragTargetState = null;

            int firstVisible = Mathf.Max(0, Mathf.FloorToInt(scrollPos.y / RowHeight) - 2);
            int lastVisible = Mathf.Min(displayMods.Count - 1, Mathf.CeilToInt((scrollPos.y + listOutRect.height) / RowHeight) + 2);
            if (firstVisible <= lastVisible)
            {
                List<ModMetaData> visibleMods = displayMods.GetRange(firstVisible, lastVisible - firstVisible + 1);
                QueueVisibleModNameTranslations(visibleMods);
                QueueVisibleStatusChecks(visibleMods);
            }

            for (int i = firstVisible; i <= lastVisible; i++)
            {
                DrawModRow(displayMods[i], new Rect(0, i * RowHeight, viewRect.width, RowHeight));
            }

            Widgets.EndScrollView();
        }

        // 這個方法負責繪製 模組Row 介面。
        // EN: This method draws mod row.
        private void DrawModRow(ModMetaData mod, Rect rowRect)
        {
            bool isChecked = selectedMods.Contains(mod);
            Widgets.DrawHighlightIfMouseover(rowRect);

            if (Mouse.IsOver(rowRect))
            {
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    isChecked = !isChecked;
                    dragTargetState = isChecked;
                    Event.current.Use();
                }
                else if (Event.current.type == EventType.MouseDrag && dragTargetState.HasValue)
                {
                    isChecked = dragTargetState.Value;
                    Event.current.Use();
                }
            }

            Vector2 checkPos = new Vector2(rowRect.x, rowRect.y + (rowRect.height - 24f) / 2f);
            Widgets.CheckboxDraw(checkPos.x, checkPos.y, isChecked, false, 24f, null, null);

            string displayName = GetDisplayModName(mod);
            string statusLine = GetModStatusLine(mod);
            Rect labelRect = new Rect(rowRect.x + 30f, rowRect.y, rowRect.width - 30f, rowRect.height);

            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = true;
            Widgets.Label(labelRect, $"{displayName}\n<size=10><color=#888888>{mod.PackageId}</color>  {statusLine}</size>");
            TooltipHandler.TipRegion(rowRect, $"{displayName}\n{mod.PackageId}\n{GetPlainModStatusText(mod)}");
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;

            if (isChecked) selectedMods.Add(mod);
            else selectedMods.Remove(mod);
        }

        // 這個方法負責繪製 StartButton 介面。
        // EN: This method draws start button.
        private void DrawStartButton(Rect inRect)
        {
            const float buttonGap = 10f;
            Rect bottomRowRect = new Rect(0, inRect.height - 40f, inRect.width, 40f);
            float buttonWidth = (bottomRowRect.width - buttonGap) / 2f;
            Rect shadowRunRect = new Rect(bottomRowRect.x, bottomRowRect.y, buttonWidth, bottomRowRect.height);
            Rect translationRect = new Rect(shadowRunRect.xMax + buttonGap, bottomRowRect.y, buttonWidth, bottomRowRect.height);
            bool hasSelection = selectedMods.Count > 0;
            bool isIdle = !AutoTranslatorSettings.IsRunning;
            bool hasReadyApiConfig = AutoTranslatorAPI.HasAnyReadyConfig();
            bool canStartShadowRun = hasSelection && isIdle && !isTranslatingModNames;
            bool canStartTranslation = hasSelection && isIdle && hasReadyApiConfig;

            GUI.color = canStartShadowRun ? new Color(0.55f, 0.8f, 1f) : Color.grey;
            if (Widgets.ButtonText(shadowRunRect, "ATC_PolicyShadowRun_Button".Translate(selectedMods.Count)) && canStartShadowRun)
            {
                AutoTranslatorSettings.ResetPipelineCancellation();
                AutoTranslatorScanner.StartTranslationPolicyShadowRun(selectedMods.ToList());
                Close();
            }

            GUI.color = canStartTranslation ? new Color(0.6f, 0.9f, 0.6f) : Color.grey;
            bool translationClicked = Widgets.ButtonText(
                translationRect,
                "ATC_MultiSelect_Start".Translate(selectedMods.Count));
            if (translationClicked && hasSelection && isIdle && !hasReadyApiConfig)
            {
                Messages.Message(
                    "ATC_EmptyConfigWarning".Translate().ToString(),
                    MessageTypeDefOf.RejectInput,
                    false);
            }
            else if (translationClicked && canStartTranslation)
            {
                AutoTranslatorSettings.ClearLog();
                AutoTranslatorSettings.ResetPipelineCancellation();
                AutoTranslatorScanner.StartMultiScan(selectedMods.ToList(), includeOfficialGamePackages: true);
                Close();
            }
            GUI.color = Color.white;
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
            if (string.IsNullOrWhiteSpace(translated) ||
                string.Equals(translated.Trim(), mod.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return mod.Name;
            }

            return $"{translated} / {mod.Name}";
        }

        // 這個方法負責取得 模組StatusLine 資料。
        // EN: This method gets mod status line.
        private static string GetModStatusLine(ModMetaData mod)
        {
            if (!ModUpdateDetector.TryGetCachedTranslationStatus(mod, out ModTranslationStatus status))
            {
                return $"<color=#888888>{"ATC_CheckingModStatus".Translate()}</color>";
            }

            string label = ModUpdateDetector.GetTranslationStatusLabelKey(status).Translate().ToString();
            string color = ModUpdateDetector.GetTranslationStatusColorHex(status);
            return $"<color={color}>{label}</color>";
        }

        // 這個方法負責取得 Plain模組StatusText 資料。
        // EN: This method gets plain mod status text.
        private static string GetPlainModStatusText(ModMetaData mod)
        {
            if (!ModUpdateDetector.TryGetCachedTranslationStatus(mod, out ModTranslationStatus status))
            {
                return "ATC_CheckingModStatus".Translate().ToString();
            }

            return ModUpdateDetector.GetTranslationStatusLabelKey(status).Translate().ToString();
        }

        private void QueueVisibleStatusChecks(List<ModMetaData> displayMods)
        {
            if (displayMods == null || displayMods.Count == 0) return;

            List<ModUpdateDetector.InstalledModStatusSnapshot> activeModSnapshots =
                ModUpdateDetector.CreateInstalledModStatusSnapshots(Verse.ModLister.AllInstalledMods);
            var pending = new List<PendingStatusCheck>();
            foreach (ModMetaData mod in displayMods)
            {
                if (mod == null || string.IsNullOrEmpty(mod.PackageId)) continue;
                if (ModUpdateDetector.TryGetCachedTranslationStatus(mod, out _)) continue;

                string key = $"{AutoTranslatorMod.Settings.TargetLang}|{mod.PackageId}";
                if (!queuedStatusChecks.Add(key)) continue;

                ModUpdateDetector.TranslationStatusCheckSnapshot snapshot =
                    ModUpdateDetector.CreateTranslationStatusCheckSnapshot(mod, activeModSnapshots);
                if (snapshot == null) continue;

                lock (statusCheckLock)
                {
                    if (!statusChecksInFlight.Add(key)) continue;
                }

                pending.Add(new PendingStatusCheck
                {
                    Key = key,
                    PackageId = mod.PackageId,
                    Snapshot = snapshot
                });
                if (pending.Count >= MaxStatusChecksPerPass) break;
            }

            if (pending.Count == 0) return;

            Task.Run(() =>
            {
                foreach (PendingStatusCheck check in pending)
                {
                    try
                    {
                        ModUpdateDetector.GetTranslationStatus(check.Snapshot);
                    }
                    catch (Exception ex)
                    {
                        Verse.Log.Warning($"[AutoTranslationCore] Multi-select status check failed for {check.PackageId}: {ex.Message}");
                    }
                    finally
                    {
                        lock (statusCheckLock)
                        {
                            statusChecksInFlight.Remove(check.Key);
                        }
                    }
                }
            });
        }

        // 這個方法負責排入 Visible模組名稱Translations 佇列。
        // EN: This method queues visible mod name translations.
        private static void QueueVisibleModNameTranslations(List<ModMetaData> displayMods)
        {
            if (!AutoTranslatorMod.Settings.TranslateWorkbenchModNames) return;
            if (AutoTranslatorSettings.IsRunning) return;
            if (isTranslatingModNames) return;
            if (displayMods == null || displayMods.Count == 0) return;
            if (!AutoTranslatorAPI.HasAnyReadyConfig()) return;
            if (!ModNameTranslationCache.TryBeginVisibleQueue(displayMods)) return;

            var pending = displayMods
                .Where(m => m != null)
                .Where(m => !ModNameTranslationCache.TryGet(m, out _))
                .Where(ModNameTranslationCache.TryMarkQueued)
                .Take(4)
                .ToList();

            if (pending.Count == 0) return;

            isTranslatingModNames = true;
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
                            if (translatedNames != null &&
                                translatedNames.Count == pending.Count &&
                                AutoTranslatorMod.Settings.TargetLang == targetLanguage)
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
                            isTranslatingModNames = false;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Verse.Log.Warning($"[AutoTranslationCore] Multi-select mod-name translation failed: {ex.Message}");
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        ModNameTranslationCache.MarkFailed(pending);
                        ModNameTranslationCache.ReleaseQueued(pending);
                        isTranslatingModNames = false;
                    });
                }
            });
        }
    }
}
