using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using AutoTranslator_Core.TargetedHardcodedUi;
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
        private const float RowHeight = 94f;

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
        private bool isScanningAllDll;
        private volatile bool cancelDllScanRequested;
        private volatile int dllScanCurrent;
        private volatile int dllScanTotal;
        private string dllScanCurrentMod = string.Empty;
        private bool isRunningModalPreflight;
        private bool allowCloseWhileTaskRuns;
        private bool closeConfirmationOpen;
        private TranslationStatusFilter translationStatusFilter;
        private TranslationStatusFilter cachedTranslationStatusFilter;

        private enum TranslationStatusFilter
        {
            All,
            Translated,
            Untranslated,
            UntranslatedIncludingDll
        }

        private sealed class PendingStatusCheck
        {
            public string Key;
            public string PackageId;
            public ModUpdateDetector.TranslationStatusCheckSnapshot Snapshot;
        }

        // 這個屬性提供 InitialSize 的讀寫或計算結果。
        // EN: This method handles vector2.
        public override Vector2 InitialSize
        {
            get
            {
                SettingsWindowSize size = SettingsWindowSizePolicy.Resolve(
                    UI.screenWidth,
                    UI.screenHeight);
                return new Vector2(size.Width, size.Height);
            }
        }

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

                Rect searchRect = new Rect(0, 45f, inRect.width * 0.62f, 28f);
                searchText = Widgets.TextField(searchRect, searchText);
                if (string.IsNullOrEmpty(searchText))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(searchRect.x + 5f, searchRect.y + 2f, searchRect.width, searchRect.height), "ATC_MultiSelect_Search".Translate());
                    GUI.color = Color.white;
                }
                DrawTranslationStatusFilter(new Rect(searchRect.xMax + 8f, 45f, inRect.width - searchRect.width - 8f, 28f));

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
                translationStatusFilter == TranslationStatusFilter.All &&
                cachedValidModCount == currentValidCount &&
                cachedValidModVersion == AutoTranslatorMod.ValidModsCacheVersion &&
                string.Equals(cachedSearchText, searchText ?? "", StringComparison.Ordinal) &&
                cachedTranslationStatusFilter == translationStatusFilter)
            {
                return cachedDisplayMods;
            }

            IEnumerable<ModMetaData> mods = validMods
                .Where(m => !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(m.PackageId) &&
                            !AutoTranslatorScanner.IsTranslationPatchMod(m) &&
                            !AutoTranslatorMod.Settings.IsTranslationBlacklisted(m.PackageId));

            if (!string.IsNullOrEmpty(searchText))
            {
                string searchLower = searchText.ToLowerInvariant();
                mods = mods.Where(m =>
                    (m.Name ?? "").ToLowerInvariant().Contains(searchLower) ||
                    (m.PackageId ?? "").ToLowerInvariant().Contains(searchLower) ||
                    GetCachedTranslatedModName(m).ToLowerInvariant().Contains(searchLower));
            }

            if (translationStatusFilter != TranslationStatusFilter.All)
            {
                List<ModMetaData> statusCandidates = mods.ToList();
                QueueVisibleStatusChecks(statusCandidates);
                mods = statusCandidates.Where(ModMatchesTranslationStatusFilter);
            }

            cachedDisplayMods = mods.OrderBy(m => m.Name).ToList();
            cachedSearchText = searchText ?? "";
            cachedValidModCount = currentValidCount;
            cachedValidModVersion = AutoTranslatorMod.ValidModsCacheVersion;
            cachedTranslationStatusFilter = translationStatusFilter;
            return cachedDisplayMods;
        }

        public override void Close(bool doCloseSound = true)
        {
            bool taskRunning = isScanningAllDll || isRunningModalPreflight;
            if (!allowCloseWhileTaskRuns && taskRunning)
            {
                if (closeConfirmationOpen) return;
                closeConfirmationOpen = true;
                Find.WindowStack.Add(new Dialog_MessageBox(
                    "ATC_Preflight_CloseRunningConfirm".Translate(),
                    "ATC_Preflight_StopAndClose".Translate(),
                    () =>
                    {
                        closeConfirmationOpen = false;
                        if (isScanningAllDll) cancelDllScanRequested = true;
                        AutoTranslatorSettings.RequestPipelineCancellation();
                        allowCloseWhileTaskRuns = true;
                        Close();
                    },
                    "ATC_Preflight_KeepOpen".Translate(),
                    () => closeConfirmationOpen = false,
                    "ATC_Preflight_CloseRunningTitle".Translate()));
                return;
            }

            base.Close(doCloseSound);
        }

        private bool ModMatchesTranslationStatusFilter(ModMetaData mod)
        {
            if (!ModUpdateDetector.TryGetCachedTranslationStatus(mod, out ModTranslationStatus status))
                return translationStatusFilter == TranslationStatusFilter.Untranslated ||
                       translationStatusFilter == TranslationStatusFilter.UntranslatedIncludingDll;
            bool translated = status == ModTranslationStatus.Translated || status == ModTranslationStatus.Filtered;
            if (translationStatusFilter == TranslationStatusFilter.Translated) return translated;
            if (!translated) return true;
            return translationStatusFilter == TranslationStatusFilter.UntranslatedIncludingDll &&
                   HasUnfinishedDllWork(mod);
        }

        private static bool HasUnfinishedDllWork(ModMetaData mod)
        {
            if (AutoTranslatorMod.Settings == null ||
                !AutoTranslatorMod.Settings.EnableHardcodedUiPrototype ||
                !HardcodedUiBatchScanCoordinator.TryGet(mod?.PackageId, out HardcodedUiBatchScanSummary summary) ||
                summary?.Result == null)
                return false;
            string targetFolder = AutoTranslatorScanner.GetFolderNameByLanguage(
                AutoTranslatorMod.Settings.TargetLang);
            foreach (HardcodedUiPatchEntry entry in summary.Result.Entries)
            {
                if (entry == null ||
                    !summary.Result.Decisions.TryGetValue(entry.EntryId, out HardcodedUiDecisionRecord decision))
                    continue;
                if (decision.EffectiveDecision == HardcodedUiAutomaticDecision.Uncertain) return true;
                if (decision.EffectiveDecision != HardcodedUiAutomaticDecision.Translate) continue;
                if (entry.Translations == null ||
                    !entry.Translations.TryGetValue(targetFolder, out string translation) ||
                    string.IsNullOrWhiteSpace(translation) ||
                    string.Equals(translation.Trim(), entry.Literal?.Trim(), StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private void DrawTranslationStatusFilter(Rect rect)
        {
            string label = translationStatusFilter == TranslationStatusFilter.Translated
                ? "ATC_Filter_Translated".Translate().ToString()
                : translationStatusFilter == TranslationStatusFilter.Untranslated
                    ? "ATC_Filter_Untranslated".Translate().ToString()
                    : translationStatusFilter == TranslationStatusFilter.UntranslatedIncludingDll
                        ? "ATC_Filter_UntranslatedIncludingDll".Translate().ToString()
                    : "ATC_Filter_AllTranslationStates".Translate().ToString();
            if (!Widgets.ButtonText(rect, label)) return;
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new FloatMenuOption("ATC_Filter_AllTranslationStates".Translate(), () => SetTranslationStatusFilter(TranslationStatusFilter.All)),
                new FloatMenuOption("ATC_Filter_Translated".Translate(), () => SetTranslationStatusFilter(TranslationStatusFilter.Translated)),
                new FloatMenuOption("ATC_Filter_Untranslated".Translate(), () => SetTranslationStatusFilter(TranslationStatusFilter.Untranslated)),
                new FloatMenuOption("ATC_Filter_UntranslatedIncludingDll".Translate(), () => SetTranslationStatusFilter(TranslationStatusFilter.UntranslatedIncludingDll))
            }));
        }

        private void SetTranslationStatusFilter(TranslationStatusFilter value)
        {
            translationStatusFilter = value;
            cachedDisplayMods = null;
            scrollPos = Vector2.zero;
        }

        // 這個方法負責繪製 選取Buttons 介面。
        // EN: This method draws selection buttons.
        private void DrawSelectionButtons(Rect inRect, List<ModMetaData> displayMods)
        {
            Rect btnRow = new Rect(0, 80f, inRect.width, 26f);
            const float buttonGap = 8f;
            float buttonWidth = (btnRow.width - buttonGap * 2f) / 3f;
            List<ModMetaData> defaultSelectableMods = displayMods
                .Where(m => !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(m.PackageId))
                .ToList();
            bool isAllSelected = defaultSelectableMods.Count > 0 && defaultSelectableMods.All(m => selectedMods.Contains(m));
            string btnLabel = isAllSelected ? "ATC_DeselectAll".Translate().ToString() : "ATC_SelectAll".Translate().ToString();

            if (Widgets.ButtonText(new Rect(btnRow.x, btnRow.y, buttonWidth, btnRow.height), btnLabel))
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
            Rect randomRect = new Rect(
                btnRow.x + buttonWidth + buttonGap,
                btnRow.y,
                buttonWidth,
                btnRow.height);
            if (Widgets.ButtonText(randomRect, "ATC_One_click_chaos".Translate()))
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

            Rect dllGroupRect = new Rect(
                btnRow.x + (buttonWidth + buttonGap) * 2f,
                btnRow.y,
                buttonWidth,
                btnRow.height);
            bool dllFeatureEnabled = AutoTranslatorMod.Settings != null &&
                                     AutoTranslatorMod.Settings.EnableHardcodedUiPrototype;
            bool hasSelection = selectedMods.Count > 0;
            bool canScanDll = dllFeatureEnabled && hasSelection && !isScanningAllDll &&
                              !AutoTranslatorSettings.IsRunning &&
                              !AutoTranslatorAPI.HasOutstandingTranslationWork;
            Rect dllScanRect = dllGroupRect;
            GUI.color = canScanDll ? new Color(0.55f, 0.85f, 1f) : Color.grey;
            string dllButtonLabel = isScanningAllDll
                ? "ATC_HardcodedUi_BatchScanning".Translate().ToString()
                : "ATC_HardcodedUi_AnalyzeSelectedDll".Translate().ToString();
            if (Widgets.ButtonText(dllScanRect, dllButtonLabel) && canScanDll)
            {
                StartDllBatchScan();
            }
            string dllDisabledReason = !dllFeatureEnabled
                ? "ATC_Disabled_DllFeatureOff".Translate().ToString()
                : GetCurrentDisabledReason(true, false, true);
            AddDisabledReasonTooltip(dllScanRect, !canScanDll && !isScanningAllDll, dllDisabledReason);

            GUI.color = Color.white;
        }

        private void StartDllBatchScan()
        {
            if (isScanningAllDll ||
                AutoTranslatorSettings.IsRunning ||
                AutoTranslatorAPI.HasOutstandingTranslationWork ||
                selectedMods.Count == 0)
                return;

            isScanningAllDll = true;
            AutoTranslatorSettings.ResetPipelineCancellation();
            cancelDllScanRequested = false;
            dllScanCurrent = 0;
            dllScanTotal = 0;
            dllScanCurrentMod = string.Empty;
            isRunningModalPreflight = true;
            List<ModMetaData> scanTargets = selectedMods
                .Where(mod => mod != null &&
                              !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(mod.PackageId))
                .ToList();
            HardcodedUiBatchScanCoordinator.ScanActiveModsAsync(
                    scanTargets,
                    (current, total, modName) =>
                    {
                        dllScanCurrent = current;
                        dllScanTotal = total;
                        dllScanCurrentMod = modName ?? string.Empty;
                    },
                    () => cancelDllScanRequested)
                .ContinueWith(task => ATC_Dispatcher.RunOnMainThread(() =>
                {
                    isScanningAllDll = false;
                    if (task.IsFaulted || task.IsCanceled)
                    {
                        Verse.Log.Warning("[AutoTranslationCore] DLL batch scan failed: " +
                            task.Exception?.GetBaseException());
                        Messages.Message(
                            "ATC_HardcodedUi_BatchScanFailed".Translate(),
                            MessageTypeDefOf.RejectInput,
                            false);
                        return;
                    }

                    List<HardcodedUiBatchScanSummary> results = task.Result;
                    if (cancelDllScanRequested)
                    {
                        Messages.Message(
                            "ATC_HardcodedUi_BatchScanStopped".Translate(results.Count),
                            MessageTypeDefOf.NeutralEvent,
                            false);
                        return;
                    }
                    int candidates = results.Sum(result => result.CandidateCount);
                    dllScanCurrent = results.Count;
                    dllScanTotal = results.Count;
                    dllScanCurrentMod = string.Empty;
                    Messages.Message(
                        "ATC_HardcodedUi_BatchScanDone".Translate(results.Count, candidates),
                        MessageTypeDefOf.PositiveEvent,
                        false);
                }));
        }

        // 這個方法負責繪製 模組List 介面。
        // EN: This method draws mod list.
        private void DrawModList(Rect inRect, List<ModMetaData> displayMods)
        {
            float listTop = 116f;
            Widgets.DrawLineHorizontal(0, listTop - 10f, inRect.width);
            const float bottomReserve = 100f;
            Rect listOutRect = new Rect(0, listTop, inRect.width, inRect.height - listTop - bottomReserve);
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

            bool showDllDetails = AutoTranslatorMod.Settings != null &&
                                  AutoTranslatorMod.Settings.EnableHardcodedUiPrototype;
            bool showPreflightDetails = TranslationPolicyPreflightResultCache.TryGetMod(
                mod?.PackageId,
                out _);
            int manageButtonCount = 1 + (showPreflightDetails ? 1 : 0) + (showDllDetails ? 1 : 0);
            float manageButtonHeight = manageButtonCount >= 3 ? 26f : 30f;
            float manageButtonGap = manageButtonCount >= 3 ? 3f : 5f;
            float manageHeight = manageButtonCount * manageButtonHeight +
                                 (manageButtonCount - 1) * manageButtonGap;
            Rect manageRect = new Rect(
                rowRect.xMax - 92f,
                rowRect.y + (rowRect.height - manageHeight) / 2f,
                88f,
                manageHeight);

            if (Mouse.IsOver(rowRect) && !Mouse.IsOver(manageRect))
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
            string policyStatus = GetPolicyCloudStatusLine(mod);
            string dllStatus = GetDllScanStatusLine(mod);
            string preflightStatus = GetPreflightStatusLine(mod);
            Rect labelRect = new Rect(rowRect.x + 30f, rowRect.y, rowRect.width - 128f, rowRect.height);

            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = true;
            Widgets.Label(labelRect, $"{displayName}\n<size=10><color=#888888>{mod.PackageId}</color>  {statusLine}\n{policyStatus}{dllStatus}{preflightStatus}</size>");
            TooltipHandler.TipRegion(labelRect,
                $"{displayName}\n{mod.PackageId}\n{GetPlainModStatusText(mod)}\n{GetPlainPolicyCloudStatus(mod)}\n{GetPlainDllScanStatus(mod)}\n{GetPlainPreflightStatus(mod)}");
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;

            float manageButtonY = manageRect.y;
            Rect cloudManageRect = new Rect(
                manageRect.x,
                manageButtonY,
                manageRect.width,
                manageButtonHeight);
            GUI.color = Color.grey;
            Widgets.ButtonText(cloudManageRect, "ATC_PolicyCloud_Manage".Translate());
            AddDisabledReasonTooltip(
                cloudManageRect,
                true,
                "ATC_PolicyCloud_ServiceUpgradePending".Translate().ToString());
            GUI.color = Color.white;
            manageButtonY += manageButtonHeight + manageButtonGap;
            if (showPreflightDetails)
            {
                if (Widgets.ButtonText(
                        new Rect(
                            manageRect.x,
                            manageButtonY,
                            manageRect.width,
                            manageButtonHeight),
                        "ATC_PolicyPreflight_Details".Translate()))
                {
                    Find.WindowStack.Add(new Window_TranslationPolicyPreflightResults(mod.PackageId));
                }
                manageButtonY += manageButtonHeight + manageButtonGap;
            }
            if (showDllDetails && Widgets.ButtonText(
                    new Rect(
                        manageRect.x,
                        manageButtonY,
                        manageRect.width,
                        manageButtonHeight),
                    "ATC_HardcodedUi_BatchDetails".Translate()))
            {
                Find.WindowStack.Add(new Window_HardcodedUiWorkbench(mod));
            }

            if (isChecked) selectedMods.Add(mod);
            else selectedMods.Remove(mod);
        }

        // 這個方法負責繪製 StartButton 介面。
        // EN: This method draws start button.
        private void DrawStartButton(Rect inRect)
        {
            const float buttonGap = 8f;
            Rect bottomRowRect = new Rect(0, inRect.height - 40f, inRect.width, 40f);
            DrawModalTaskProgress(inRect, bottomRowRect);
            bool showCloudAction = true;
            bool cloudCacheEnabled = AutoTranslatorSettings.IsPolicyAnalysisCloudCacheAvailable &&
                                     AutoTranslatorMod.Settings != null &&
                                     AutoTranslatorMod.Settings.EnablePolicyAnalysisCloudCache;
            bool showAgentAction = AutoTranslatorMod.Settings != null &&
                                   AutoTranslatorMod.Settings.EnableTranslationPolicyAgent;
            int actionCount = 2 + (showCloudAction ? 1 : 0) + (showAgentAction ? 1 : 0);
            float buttonWidth = (bottomRowRect.width - buttonGap * (actionCount - 1)) / actionCount;
            int actionIndex = 0;
            Func<int, Rect> actionRect = index => new Rect(
                bottomRowRect.x + index * (buttonWidth + buttonGap),
                bottomRowRect.y,
                buttonWidth,
                bottomRowRect.height);
            Rect shadowRunRect = actionRect(actionIndex++);
            bool hasSelection = selectedMods.Count > 0;
            bool isIdle = !AutoTranslatorSettings.IsRunning &&
                          !AutoTranslatorAPI.HasOutstandingTranslationWork &&
                          !isScanningAllDll;
            bool hasReadyApiConfig = AutoTranslatorAPI.HasAnyReadyConfig();
            bool hasPolicyAgentConfig = AutoTranslatorMod.Settings?.ApiConfigs != null &&
                                        AutoTranslatorMod.Settings.ApiConfigs.Any(
                                            AutoTranslatorAPI.IsPolicyAgentConfigReady);
            bool canStartShadowRun = hasSelection && isIdle && !isTranslatingModNames;
            bool canStartTranslation = hasSelection && isIdle && hasReadyApiConfig;

            const float resultButtonWidth = 46f;
            Rect localRuleRunRect = new Rect(
                shadowRunRect.x,
                shadowRunRect.y,
                shadowRunRect.width - resultButtonWidth - 4f,
                shadowRunRect.height);
            Rect resultRect = new Rect(localRuleRunRect.xMax + 4f, shadowRunRect.y, resultButtonWidth, shadowRunRect.height);
            GUI.color = canStartShadowRun ? new Color(0.55f, 0.8f, 1f) : Color.grey;
            if (Widgets.ButtonText(localRuleRunRect, "ATC_PolicyShadowRun_Button".Translate(selectedMods.Count)) && canStartShadowRun)
            {
                AutoTranslatorSettings.ResetPipelineCancellation();
                AutoTranslatorScanner.StartTranslationPolicyPreflightRun(
                    selectedMods.ToList(),
                    false,
                    false,
                    "mod-select/local-rules-button");
                isRunningModalPreflight = true;
            }
            AddDisabledReasonTooltip(localRuleRunRect, !canStartShadowRun, GetCurrentDisabledReason(true, true, true));

            bool hasPreflightResult = TranslationPolicyPreflightResultCache.TryGetLatest(out _);
            GUI.color = hasPreflightResult ? Color.white : Color.grey;
            if (Widgets.ButtonText(resultRect, "ATC_PolicyPreflight_ViewShort".Translate()) && hasPreflightResult)
                Find.WindowStack.Add(new Window_TranslationPolicyPreflightResults());
            AddDisabledReasonTooltip(
                resultRect,
                !hasPreflightResult,
                "ATC_Disabled_NoResult".Translate().ToString());

            if (showCloudAction)
            {
                Rect cloudRect = actionRect(actionIndex++);
                bool canRunCloud = cloudCacheEnabled && hasSelection &&
                                   !AutoTranslatorSettings.IsRunning &&
                                   !AutoTranslatorAPI.HasOutstandingTranslationWork &&
                                   !isScanningAllDll &&
                                   !isTranslatingModNames;
                GUI.color = Color.grey;
                if (Widgets.ButtonText(cloudRect, "ATC_PolicyPreflight_SyncCloud".Translate()) && canRunCloud)
                {
                    AutoTranslatorSettings.ResetPipelineCancellation();
                    AutoTranslatorScanner.StartTranslationPolicyPreflightRun(
                        selectedMods.ToList(),
                        true,
                        false,
                        "mod-select/cloud-button");
                }
                AddDisabledReasonTooltip(
                    cloudRect,
                    true,
                    "ATC_PolicyCloud_ServiceUpgradePending".Translate().ToString());
            }

            if (showAgentAction)
            {
                Rect agentRect = actionRect(actionIndex++);
                bool canRunAgent = hasSelection &&
                                   !AutoTranslatorSettings.IsRunning &&
                                   !AutoTranslatorAPI.HasOutstandingTranslationWork &&
                                   !isScanningAllDll &&
                                   !isTranslatingModNames &&
                                   hasPolicyAgentConfig;
                GUI.color = canRunAgent ? new Color(0.75f, 0.65f, 1f) : Color.grey;
                bool agentClicked = Widgets.ButtonText(agentRect, "ATC_PolicyPreflight_RunAgent".Translate());
                if (agentClicked && hasSelection && isIdle && !hasPolicyAgentConfig)
                {
                    Messages.Message(
                        "ATC_PolicyAgent_NoProvider".Translate(),
                        MessageTypeDefOf.RejectInput,
                        false);
                }
                else if (agentClicked && canRunAgent)
                {
                    AutoTranslatorSettings.ResetPipelineCancellation();
                    AutoTranslatorScanner.StartTranslationPolicyPreflightRun(
                        selectedMods.ToList(),
                        cloudCacheEnabled,
                        true,
                        "mod-select/agent-button");
                    isRunningModalPreflight = true;
                }
                string agentDisabledReason = GetCurrentDisabledReason(true, true, true);
                if (hasSelection && isIdle && !isTranslatingModNames && !hasPolicyAgentConfig)
                    agentDisabledReason = "ATC_Disabled_AgentNotConfigured".Translate().ToString();
                AddDisabledReasonTooltip(agentRect, !canRunAgent, agentDisabledReason);
            }

            Rect translationRect = actionRect(actionIndex);
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
                allowCloseWhileTaskRuns = true;
                Close();
            }
            string translationDisabledReason = GetCurrentDisabledReason(true, false, true);
            if (hasSelection && isIdle && !hasReadyApiConfig)
                translationDisabledReason = "ATC_Disabled_TranslationNotConfigured".Translate().ToString();
            AddDisabledReasonTooltip(translationRect, !canStartTranslation, translationDisabledReason);
            GUI.color = Color.white;
        }

        private string GetCurrentDisabledReason(bool requireSelection, bool blockModNameTranslation, bool blockDllScan)
        {
            if (requireSelection && selectedMods.Count == 0)
                return "ATC_Disabled_NoModSelected".Translate().ToString();
            if (AutoTranslatorSettings.IsStopping)
                return "ATC_Disabled_Stopping".Translate().ToString();
            if (AutoTranslatorSettings.IsRunning)
                return "ATC_Disabled_TaskRunning".Translate().ToString();
            if (AutoTranslatorAPI.HasOutstandingTranslationWork)
                return "ATC_Disabled_ApiBusy".Translate().ToString();
            if (blockDllScan && isScanningAllDll)
                return "ATC_Disabled_DllScanning".Translate().ToString();
            if (blockModNameTranslation && isTranslatingModNames)
                return "ATC_Disabled_ModNameTranslation".Translate().ToString();
            return "ATC_Disabled_NotReady".Translate().ToString();
        }

        private static void AddDisabledReasonTooltip(Rect rect, bool disabled, string reason)
        {
            if (!disabled || string.IsNullOrWhiteSpace(reason)) return;
            TooltipHandler.TipRegion(rect, "ATC_DisabledReason".Translate(reason));
        }

        private void DrawModalTaskProgress(Rect inRect, Rect bottomRowRect)
        {
            AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
            if (settings == null) return;
            if (!AutoTranslatorSettings.IsRunning &&
                !AutoTranslatorAPI.HasOutstandingTranslationWork &&
                !isScanningAllDll)
            {
                isRunningModalPreflight = false;
            }

            Rect barRect = new Rect(0f, bottomRowRect.y - 50f, inRect.width - 104f, 20f);
            bool hasModalTask = isRunningModalPreflight || isScanningAllDll;
            bool showingAgentBatches = hasModalTask && !isScanningAllDll &&
                                       !string.IsNullOrWhiteSpace(AutoTranslatorSettings.AgentBatchProgressText);
            float taskProgress = !hasModalTask
                ? 0f
                : isScanningAllDll
                ? (dllScanTotal > 0 ? Mathf.Clamp01((float)dllScanCurrent / dllScanTotal) : 0f)
                : showingAgentBatches
                ? Mathf.Clamp01(AutoTranslatorSettings.AgentBatchProgress)
                : Mathf.Clamp01(settings.CurrentProgress);
            Widgets.FillableBar(barRect, taskProgress);
            Text.Anchor = TextAnchor.MiddleCenter;
            bool resultReadyWhileFinishing = !isScanningAllDll && settings.CurrentProgress >= 0.999f &&
                                             TranslationPolicyPreflightResultCache.TryGetLatest(out _);
            string taskLabel = !isRunningModalPreflight && !isScanningAllDll
                ? "ATC_Disabled_NoActiveTask".Translate().ToString()
                : isScanningAllDll
                ? "ATC_HardcodedUi_BatchProgress".Translate(
                    dllScanCurrent,
                    dllScanTotal,
                    dllScanCurrentMod ?? string.Empty).ToString()
                : showingAgentBatches
                    ? AutoTranslatorSettings.AgentBatchProgressText
                : resultReadyWhileFinishing
                    ? "ATC_PolicyPreflight_Finalizing".Translate().ToString()
                    : string.IsNullOrWhiteSpace(settings.CurrentTaskName)
                        ? "ATC_PolicyPreflight_AgentTask".Translate().ToString()
                        : settings.CurrentTaskName;
            Widgets.Label(barRect, taskLabel);
            Text.Anchor = TextAnchor.UpperLeft;
            bool canStop = hasModalTask;
            GUI.color = canStop ? new Color(1f, 0.45f, 0.4f) : Color.grey;
            if (Widgets.ButtonText(new Rect(barRect.xMax + 8f, barRect.y, 96f, barRect.height), "ATC_Stop".Translate()) && canStop)
            {
                if (isScanningAllDll) cancelDllScanRequested = true;
                else AutoTranslatorSettings.RequestPipelineCancellation();
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

        private static string GetDllScanStatusLine(ModMetaData mod)
        {
            string plain = GetPlainDllScanStatus(mod);
            return string.IsNullOrWhiteSpace(plain) ? string.Empty : "\n<color=#9dc8ff>" + plain + "</color>";
        }

        private static string GetPreflightStatusLine(ModMetaData mod)
        {
            string plain = GetPlainPreflightStatus(mod);
            return string.IsNullOrWhiteSpace(plain)
                ? string.Empty
                : "\n<color=#b9e68c>" + plain + "</color>";
        }

        private static string GetPlainPreflightStatus(ModMetaData mod)
        {
            if (!TranslationPolicyPreflightResultCache.TryGetMod(
                    mod?.PackageId,
                    out TranslationPolicyPreflightModResult result))
                return string.Empty;
            return "ATC_PolicyPreflight_RowSummary".Translate(
                result.XmlCandidates,
                result.LocalAllows,
                result.LocalDenies,
                result.Ambiguous,
                result.Unresolved,
                result.FinalTranslationCandidates);
        }

        private static string GetPlainDllScanStatus(ModMetaData mod)
        {
            if (AutoTranslatorMod.Settings == null ||
                !AutoTranslatorMod.Settings.EnableHardcodedUiPrototype) return string.Empty;
            if (mod != null && !mod.Active)
                return "ATC_HardcodedUi_ModNotLoaded".Translate();
            if (!HardcodedUiBatchScanCoordinator.TryGet(mod?.PackageId, out HardcodedUiBatchScanSummary summary))
                return "ATC_HardcodedUi_BatchNotScanned".Translate();
            if (!string.IsNullOrWhiteSpace(summary.Error))
                return "ATC_HardcodedUi_BatchItemFailed".Translate(summary.Error);
            string summaryText = "ATC_HardcodedUi_BatchItemSummary".Translate(
                summary.AssemblyCount,
                summary.MethodCount,
                summary.CandidateCount,
                summary.DiagnosticCount,
                summary.TranslateCount,
                summary.DoNotTranslateCount,
                summary.UncertainCount,
                summary.UserOverrideCount,
                summary.AnalyzerVersion);
            return summary.CandidateCount == 0
                ? summaryText + "\n" + "ATC_HardcodedUi_NoCandidates".Translate().ToString()
                : summaryText;
        }

        private static string GetPolicyCloudStatusLine(ModMetaData mod)
        {
            string text = GetPlainPolicyCloudStatus(mod);
            string color = AutoTranslatorMod.Settings.IsPolicyCloudAccelerationDisabled(mod?.PackageId)
                ? "#e8a64a"
                : "#79b8ff";
            return $"<color={color}>{text}</color>";
        }

        private static string GetPlainPolicyCloudStatus(ModMetaData mod)
        {
            if (!AutoTranslatorSettings.IsPolicyAnalysisCloudCacheAvailable)
                return "ATC_PolicyCloud_StatusUnavailable".Translate();
            string packageId = mod?.PackageId ?? string.Empty;
            if (!AutoTranslatorMod.Settings.EnablePolicyAnalysisCloudCache)
                return "ATC_PolicyCloud_StatusGlobalOff".Translate();
            if (AutoTranslatorMod.Settings.IsPolicyCloudAccelerationDisabled(packageId))
                return "ATC_PolicyCloud_StatusDisabled".Translate();
            PolicyAnalysisLocalState state = PolicyAnalysisLocalStateManager.Get(packageId);
            if (state == null) return "ATC_PolicyCloud_StatusNotUsed".Translate();
            if (string.Equals(state.Status, PolicyAnalysisLocalStateStore.AcceleratedStatus, StringComparison.Ordinal))
                return "ATC_PolicyCloud_StatusAccelerated".Translate();
            if (string.Equals(state.Status, PolicyAnalysisLocalStateStore.PendingUploadStatus, StringComparison.Ordinal))
                return "ATC_PolicyCloud_StatusPending".Translate();
            if (string.Equals(state.Status, PolicyAnalysisLocalStateStore.UploadedStatus, StringComparison.Ordinal))
                return "ATC_PolicyCloud_StatusUploaded".Translate();
            return "ATC_PolicyCloud_StatusNotUsed".Translate();
        }

        private void QueueVisibleStatusChecks(List<ModMetaData> displayMods)
        {
            if (displayMods == null || displayMods.Count == 0) return;
            lock (statusCheckLock)
            {
                if (statusChecksInFlight.Count > 0) return;
            }

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
