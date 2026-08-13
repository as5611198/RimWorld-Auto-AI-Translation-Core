using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
// 這個檔案負責設定分頁的 UI 與參數編輯。
// EN: This file draws the settings tab and edits runtime options.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器模組 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorMod.
    public partial class AutoTranslatorMod : Mod
    {
        private static bool _settingsLegacyRepairRunning;
        private static bool _settingsRestoreBackupRunning;
        private static bool _settingsFactoryResetRunning;


        // 這個方法負責繪製 設定分頁 介面。
        // EN: This method draws config tab.
        private void DrawConfigTab(Listing_Standard l, Rect viewRect)
        {

            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_ShowWorldMainButton".Translate(), ref Settings.ShowWorldMainButton);
            l.Gap(5f);

            Rect blacklistButtonRect = l.GetRect(35f);
            if (Widgets.ButtonText(blacklistButtonRect, "ATC_Blacklist_Open".Translate(
                    Settings.TranslationBlacklist.Count,
                    Settings.CloudDownloadBlacklist.Count)))
            {
                Find.WindowStack.Add(new Window_ModBlacklists());
            }
            if (Mouse.IsOver(blacklistButtonRect))
            {
                TooltipHandler.TipRegion(blacklistButtonRect, "ATC_Blacklist_OpenTip".Translate());
            }
            l.Gap(10f);

            if (AutoTranslatorSettings.IsRunning) GUI.color = Color.grey;
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_AutoClearOldOnUpdate".Translate(), ref Settings.AutoClearOldOnUpdate);
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_AutoTranslateOnUpdate".Translate(), ref Settings.AutoTranslateOnUpdate);

            l.Gap(5f);
            bool previousUiInterceptor = Settings.EnableUIInterceptor;
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_EnableUIInterceptor".Translate(), ref Settings.EnableUIInterceptor);
            if (previousUiInterceptor != Settings.EnableUIInterceptor)
            {
                if (Settings.EnableUIInterceptor)
                    Settings.EnableHardcodedUiPrototype = false;
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.RequestReload();
            }
            if (!Settings.EnableUIInterceptor && !AutoTranslatorSettings.IsRunning) GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_EnableUINewTranslation".Translate(), ref Settings.EnableUINewTranslation);
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_EnableUIErrorLogInterception".Translate(), ref Settings.EnableUIErrorLogInterception);
            Rect debugLogRow = l.GetRect(30f);
            Widgets.CheckboxLabeled(debugLogRow, "ATC_EnableDevelopmentDebugLogging".Translate(), ref Settings.EnableDevelopmentDebugLogging);
            if (Mouse.IsOver(debugLogRow))
                TooltipHandler.TipRegion(debugLogRow, "ATC_EnableDevelopmentDebugLoggingTooltip".Translate());
            GUI.color = AutoTranslatorSettings.IsRunning ? Color.grey : Color.white;
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_TranslateWorkbenchModNames".Translate(), ref Settings.TranslateWorkbenchModNames);
            if (!Settings.EnableUIInterceptor && !AutoTranslatorSettings.IsRunning) GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_ShowOriginalUI".Translate(), ref Settings.ShowOriginalUI);
            GUI.color = Color.white;
            l.Gap(10f);
            DrawHardcodedUiPrototypeSettings(l);
            l.Gap(15f);


            Rect row1 = l.GetRect(30f);
            Rect langRect = new Rect(row1.x, row1.y, row1.width * 0.4f, row1.height);
            if (AutoTranslatorSettings.IsRunning) GUI.color = Color.grey;
            if (Mouse.IsOver(langRect)) TooltipHandler.TipRegion(langRect, "ATC_Tooltip_TargetLang".Translate());
            if (Widgets.ButtonText(langRect, "ATC_TargetLang".Translate() + ": " + GetLangLabel(Settings.TargetLang)))
            {
                if (!AutoTranslatorSettings.IsRunning)
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    foreach (TargetLanguage lang in Enum.GetValues(typeof(TargetLanguage)))
                    {
                        TargetLanguage capturedLang = lang;
                        options.Add(new FloatMenuOption(GetLangLabel(lang), () => SetTargetLanguage(capturedLang)));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }
            Rect activeScanRect = new Rect(row1.x + row1.width * 0.45f, row1.y, row1.width * 0.55f, row1.height);
            Widgets.CheckboxLabeled(activeScanRect, "ATC_OnlyScanActive".Translate(), ref Settings.OnlyScanActiveMods);
            GUI.color = Color.white;
            l.Gap(15f);


            Rect threadRow = l.GetRect(30f);
            if (AutoTranslatorSettings.IsRunning) GUI.color = Color.grey;
            Settings.MaxThreads = (int)Widgets.HorizontalSlider(
                threadRow, Settings.MaxThreads, 1f, 30f, false,
                $"{"ATC_MaxThreads".Translate()}: {Settings.MaxThreads}  ({"ATC_MaxThreadsTip".Translate()})", "1", "30"
            );
            GUI.color = Color.white;
            l.Gap(15f);


            Rect timeoutRow = l.GetRect(30f);
            if (AutoTranslatorSettings.IsRunning) GUI.color = Color.grey;

            if (Mouse.IsOver(timeoutRow)) TooltipHandler.TipRegion(timeoutRow, "ATC_Setting_Timeout_Tooltip".Translate());

            Settings.TimeoutSeconds = (int)Widgets.HorizontalSlider(
                timeoutRow,
                Settings.TimeoutSeconds,
                15f,
                600f,
                false,
                "ATC_Setting_Timeout".Translate(Settings.TimeoutSeconds.ToString()),
                "15",
                "600"
            );
            GUI.color = Color.white;
            l.Gap(15f);

            DrawTranslationUsageBudgetSettings(l);
            l.Gap(15f);

            DrawTranslationPolicyAgentSettings(l);
            l.Gap(15f);

            DrawTerminologySettings(l);
            l.Gap(15f);

            DrawRuntimeProfilePanel(l, viewRect);
            l.Gap(15f);


            Text.Font = GameFont.Small;
            Widgets.Label(l.GetRect(24f), "🔧 " + "ATC_ApiConfigTitle".Translate());
            l.Gap(2f);
            Widgets.DrawLineHorizontal(0, l.CurHeight, viewRect.width);
            l.Gap(5f);

            for (int i = 0; i < Settings.ApiConfigs.Count; i++)
            {
                var config = Settings.ApiConfigs[i];
                if (AutoTranslatorSettings.IsRunning) GUI.color = Color.grey;

                Rect noteRow = l.GetRect(28f);
                Rect noteRect = new Rect(noteRow.x, noteRow.y, noteRow.width * 0.66f, noteRow.height - 2f);
                Rect enabledRect = new Rect(noteRow.x + noteRow.width * 0.69f, noteRow.y, noteRow.width * 0.31f, noteRow.height - 2f);
                config.Label = Widgets.TextField(noteRect, config.Label ?? "");
                if (string.IsNullOrEmpty(config.Label))
                {
                    GUI.color = Color.gray;
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(noteRect.x + 5f, noteRect.y + 4f, noteRect.width - 10f, noteRect.height), "ATC_ApiKeyNoteHint".Translate());
                    Text.Font = GameFont.Small;
                    GUI.color = AutoTranslatorSettings.IsRunning ? Color.grey : Color.white;
                }
                Widgets.CheckboxLabeled(enabledRect, "ATC_ApiKeyEnabled".Translate(), ref config.Enabled);
                GUI.color = config.Enabled
                    ? (AutoTranslatorSettings.IsRunning ? Color.grey : Color.white)
                    : new Color(0.55f, 0.55f, 0.55f, 0.85f);

                Rect rowA = l.GetRect(30f);
                Rect providerRect = new Rect(rowA.x, rowA.y, rowA.width * 0.3f, rowA.height - 2f);
                if (Widgets.ButtonText(providerRect, "ATC_Provider".Translate() + ": " + config.Provider))
                {
                    if (!AutoTranslatorSettings.IsRunning)
                    {
                        List<FloatMenuOption> opts = new List<FloatMenuOption>();
                        foreach (TranslatorProvider p in Enum.GetValues(typeof(TranslatorProvider)))
                        {
                            opts.Add(new FloatMenuOption(p.ToString(), () =>
                            {
                                config.Provider = p;
                                config.SelectedModel = "";
                                AutoTranslatorAPI.ResetModelFetchState(config, clearModels: true);
                            }));
                        }
                        Find.WindowStack.Add(new FloatMenu(opts));
                    }
                }

                Rect urlRect = new Rect(rowA.x + rowA.width * 0.32f, rowA.y, rowA.width * 0.58f, rowA.height - 2f);
                if (config.Provider != TranslatorProvider.Google)
                {
                    config.CustomBaseUrl = Widgets.TextField(urlRect, config.CustomBaseUrl);
                    if (string.IsNullOrEmpty(config.CustomBaseUrl)) Widgets.Label(urlRect, "  " + "ATC_CustomUrlOptional".Translate());
                }

                Rect delRect = new Rect(rowA.x + rowA.width * 0.92f, rowA.y, rowA.width * 0.08f, rowA.height - 2f);
                GUI.color = new Color(1f, 0.4f, 0.4f);
                if (Settings.ApiConfigs.Count > 1 && Widgets.ButtonText(delRect, "ATC_Delete".Translate()))
                {
                    Settings.ApiConfigs.RemoveAt(i);
                    GUI.color = Color.white;
                    break;
                }

                GUI.color = AutoTranslatorSettings.IsRunning ? Color.grey : Color.white;
                Rect rowB = l.GetRect(30f);
                Rect keyRect = new Rect(rowB.x, rowB.y, rowB.width * 0.45f, rowB.height - 2f);

                config.Key = Widgets.TextField(keyRect, config.Key);
                if (string.IsNullOrEmpty(config.Key)) Widgets.Label(keyRect, "  " + "ATC_PasteKey".Translate());

                Rect modelInputRect = new Rect(rowB.x + rowB.width * 0.47f, rowB.y, rowB.width * 0.45f, rowB.height - 2f);
                Rect modelBtnRect = new Rect(modelInputRect.xMax + 5f, rowB.y, rowB.width * 0.08f - 5f, rowB.height - 2f);

                if (config.IsFetching)
                {
                    GUI.color = Color.yellow;
                    Widgets.Label(modelInputRect, "📡 " + "ATC_FetchingModel".Translate());
                    GUI.color = AutoTranslatorSettings.IsRunning ? Color.grey : Color.white;
                }
                else
                {
                    config.SelectedModel = Widgets.TextField(modelInputRect, config.SelectedModel);
                    if (string.IsNullOrEmpty(config.SelectedModel))
                    {
                        GUI.color = Color.gray;
                        Text.Font = GameFont.Tiny;
                        Widgets.Label(new Rect(modelInputRect.x + 5f, modelInputRect.y + 2f, modelInputRect.width, modelInputRect.height), "ATC_InputOrSelectModel".Translate());
                        Text.Font = GameFont.Small;
                        GUI.color = AutoTranslatorSettings.IsRunning ? Color.grey : Color.white;
                    }
                }

                if (Widgets.ButtonText(modelBtnRect, "▼"))
                {
                    if (config.FetchedModels.Count > 0 && !AutoTranslatorSettings.IsRunning && !config.IsFetching)
                    {
                        List<FloatMenuOption> opts = new List<FloatMenuOption>();
                        foreach (string m in config.FetchedModels) opts.Add(new FloatMenuOption(m, () => config.SelectedModel = m));
                        Find.WindowStack.Add(new FloatMenu(opts));
                    }
                    else if (!config.IsFetching && config.FetchedModels.Count == 0)
                    {
                        Messages.Message("ATC_Msg_NoModelListManualInput".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                    }
                }
                GUI.color = AutoTranslatorSettings.IsRunning ? Color.grey : Color.white;

                DrawStructuredOutputSelector(l, config, !AutoTranslatorSettings.IsRunning);
                DrawTranslationTaskTierSelector(l, config, !AutoTranslatorSettings.IsRunning);

                Rect rowC = l.GetRect(24f);
                Rect testBtnRect = new Rect(rowC.x, rowC.y + 2f, 120f, rowC.height);
                Rect refetchBtnRect = new Rect(testBtnRect.xMax + 10f, rowC.y + 2f, 140f, rowC.height);

                if (Widgets.ButtonText(refetchBtnRect, "↻ " + "ATC_RefetchModels".Translate()))
                {
                    if (!config.Enabled)
                    {
                        Messages.Message("ATC_Msg_ApiKeyDisabled".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                    }
                    else if (string.IsNullOrEmpty(config.Key) || config.Key.Length <= 10)
                    {
                        Messages.Message("ATC_EmptyConfigWarning".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                    }
                    else if (!AutoTranslatorSettings.IsRunning &&
                             !AutoTranslatorAPI.HasOutstandingTranslationWork)
                    {
                        AutoTranslatorAPI.AutoFetchForConfig(config, true);
                    }
                }

                if (config.IsTesting)
                {
                    GUI.color = Color.yellow;
                    Widgets.Label(testBtnRect, "⏳ " + "ATC_Testing".Translate());
                }
                else
                {
                    bool canTestConnection = !AutoTranslatorSettings.IsRunning &&
                                             !AutoTranslatorAPI.HasOutstandingTranslationWork;
                    GUI.color = canTestConnection ? new Color(0.6f, 0.9f, 0.6f) : Color.grey;
                    if (Widgets.ButtonText(testBtnRect, "🔌 " + "ATC_TestConnection".Translate()))
                    {
                        if (!config.Enabled)
                        {
                            Messages.Message("ATC_Msg_ApiKeyDisabled".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                        }
                        else if (string.IsNullOrEmpty(config.Key) || string.IsNullOrEmpty(config.SelectedModel))
                        {
                            Messages.Message("ATC_EmptyConfigWarning".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                        }
                        else if (canTestConnection)
                        {

                            AutoTranslatorAPI.RunConnectionTest(config);
                        }
                    }
                }
                GUI.color = AutoTranslatorSettings.IsRunning ? Color.grey : Color.white;

                l.Gap(15f);
            }

            GUI.color = new Color(0.4f, 0.8f, 1f);
            if (!AutoTranslatorSettings.IsRunning && l.ButtonText("＋ " + "ATC_AddApiBtn".Translate()))
            {
                Settings.ApiConfigs.Add(new ApiKeyConfig());
            }
            GUI.color = Color.white;


            l.Gap(20f);
            Widgets.DrawLineHorizontal(0, l.CurHeight, viewRect.width);
            l.Gap(10f);

            Text.Font = GameFont.Small;
            Widgets.Label(l.GetRect(24f), "🚑 " + "ATC_EmergencyResetTitle".Translate());


            Rect clearUIBtnRect = l.GetRect(35f);
            GUI.color = new Color(1f, 0.7f, 0.3f);
            if (Widgets.ButtonText(clearUIBtnRect, "🧹 " + "ATC_Btn_ClearUICache".Translate()))
            {
                UIInterceptor.ClearUICache();
                Messages.Message("ATC_Msg_UICacheCleared".Translate(), MessageTypeDefOf.PositiveEvent, false);
            }
            l.Gap(5f);

            Rect repairLegacyBtnRect = l.GetRect(35f);
            GUI.color = _settingsLegacyRepairRunning ? Color.grey : new Color(0.6f, 0.9f, 0.75f);
            string repairLegacyLabel = _settingsLegacyRepairRunning
                ? "ATC_CheckingModStatus".Translate().ToString()
                : "🧰 " + "ATC_Btn_RepairLegacyTranslations".Translate().ToString();
            if (Widgets.ButtonText(repairLegacyBtnRect, repairLegacyLabel) && !_settingsLegacyRepairRunning)
            {
                QueueLegacyRepairFromSettings();
            }
            l.Gap(5f);

            Rect restoreBtnRect = l.GetRect(35f);
            GUI.color = _settingsRestoreBackupRunning ? Color.grey : new Color(0.5f, 0.8f, 1f);
            string restoreLabel = _settingsRestoreBackupRunning
                ? "ATC_CheckingModStatus".Translate().ToString()
                : "↩ " + "ATC_Btn_RestoreLatestBackup".Translate().ToString();
            if (Widgets.ButtonText(restoreBtnRect, restoreLabel) && !_settingsRestoreBackupRunning)
            {
                Find.WindowStack.Add(new Dialog_MessageBox(
                    "ATC_Msg_ConfirmRestoreLatestBackup".Translate(),
                    "ATC_Btn_Confirm".Translate(),
                    () => {
                        QueueRestoreLatestBackupsFromSettings();
                    },
                    "ATC_Btn_Cancel".Translate(),
                    null,
                    "ATC_Btn_RestoreLatestBackup".Translate()
                ));
            }
            l.Gap(5f);


            Rect resetBtnRect = l.GetRect(35f);
            GUI.color = _settingsFactoryResetRunning ? Color.grey : new Color(1f, 0.3f, 0.3f);
            string resetLabel = _settingsFactoryResetRunning
                ? "ATC_CheckingModStatus".Translate().ToString()
                : "⚠️ " + "ATC_Btn_FactoryReset".Translate().ToString();
            if (Widgets.ButtonText(resetBtnRect, resetLabel) && !_settingsFactoryResetRunning)
            {
                Find.WindowStack.Add(new Dialog_MessageBox(
                    "ATC_Msg_ConfirmFactoryReset".Translate(),
                    "ATC_Btn_Confirm".Translate(),
                    () => { ExecuteFactoryReset(); },
                    "ATC_Btn_Cancel".Translate(),
                    null,
                    "ATC_EmergencyResetTitle".Translate()
                ));
            }
            GUI.color = Color.white;
        }

        private void DrawTranslationUsageBudgetSettings(Listing_Standard l)
        {
            bool canEdit = !AutoTranslatorSettings.IsRunning;
            bool enabled = Settings.EnableTranslationUsageBudget;
            long characters = Math.Min(
                10000000L,
                Math.Max(100000L, Settings.TranslationBudgetSourceCharactersPerRun));

            GUI.color = canEdit ? Color.white : Color.grey;
            Rect enableRect = l.GetRect(30f);
            Widgets.CheckboxLabeled(enableRect, "ATC_UsageBudget_Enable".Translate(), ref enabled);
            if (Mouse.IsOver(enableRect))
                TooltipHandler.TipRegion(enableRect, "ATC_UsageBudget_EnableTooltip".Translate());

            GUI.color = canEdit && enabled ? Color.white : Color.grey;
            Rect characterRect = l.GetRect(30f);
            float sliderValue = Widgets.HorizontalSlider(
                characterRect,
                characters,
                100000f,
                10000000f,
                false,
                "ATC_UsageBudget_Characters".Translate(characters),
                "100000",
                "10000000");
            characters = Math.Max(100000L, (long)Math.Round(sliderValue / 100000f) * 100000L);

            Text.Font = GameFont.Tiny;
            Widgets.Label(l.GetRect(44f), "ATC_UsageBudget_Notice".Translate());
            Text.Font = GameFont.Small;

            if (canEdit)
            {
                Settings.EnableTranslationUsageBudget = enabled;
                Settings.TranslationBudgetSourceCharactersPerRun = characters;
                Settings.TranslationBudgetEstimatedTokensPerRun = Math.Max(
                    1000L,
                    (characters * 5L + 8L) / 9L);
            }
            GUI.color = Color.white;
        }

        private void DrawTranslationPolicyAgentSettings(Listing_Standard l)
        {
            bool canEdit = !AutoTranslatorSettings.IsRunning;
            bool enabled = Settings.EnableTranslationPolicyAgent;
            bool enableCloudCache = false;
            int maxCallsPerRun = Math.Min(20, Math.Max(0, Settings.PolicyAgentMaxCallsPerRun));
            long maxEstimatedTokensPerRun = Math.Min(
                200000L,
                Math.Max(0L, Settings.PolicyAgentMaxEstimatedTokensPerRun));
            int maxCallsPerMod = Math.Min(20, Math.Max(0, Settings.PolicyAgentMaxCallsPerMod));

            GUI.color = canEdit ? Color.white : Color.grey;
            Rect enableRect = l.GetRect(30f);
            Widgets.CheckboxLabeled(enableRect, "ATC_PolicyAgent_Enable".Translate(), ref enabled);
            if (Mouse.IsOver(enableRect))
            {
                TooltipHandler.TipRegion(enableRect, "ATC_PolicyAgent_EnableTooltip".Translate());
            }

            Text.Font = GameFont.Tiny;
            Widgets.Label(l.GetRect(42f), "ATC_PolicyAgent_SharedModelPoolNotice".Translate());
            Text.Font = GameFont.Small;

            Rect cloudCacheRect = l.GetRect(30f);
            GUI.color = Color.grey;
            Widgets.CheckboxLabeled(
                cloudCacheRect,
                "ATC_PolicyCloud_Enable".Translate(),
                ref enableCloudCache);
            enableCloudCache = false;
            TooltipHandler.TipRegion(
                cloudCacheRect,
                "ATC_DisabledReason".Translate("ATC_PolicyCloud_ServiceUpgradePending".Translate()));
            GUI.color = canEdit ? Color.white : Color.grey;

            Rect callsPerRunRect = l.GetRect(30f);
            maxCallsPerRun = Mathf.RoundToInt(Widgets.HorizontalSlider(
                callsPerRunRect,
                maxCallsPerRun,
                0f,
                20f,
                false,
                "ATC_PolicyAgent_MaxCallsPerRun".Translate(maxCallsPerRun),
                "0",
                "20"));

            Rect tokensPerRunRect = l.GetRect(30f);
            float tokenSliderValue = Widgets.HorizontalSlider(
                tokensPerRunRect,
                maxEstimatedTokensPerRun,
                0f,
                200000f,
                false,
                "ATC_PolicyAgent_MaxEstimatedTokensPerRun".Translate(maxEstimatedTokensPerRun),
                "0",
                "200000");
            maxEstimatedTokensPerRun = Mathf.RoundToInt(tokenSliderValue / 10000f) * 10000L;

            Rect callsPerModRect = l.GetRect(30f);
            maxCallsPerMod = Mathf.RoundToInt(Widgets.HorizontalSlider(
                callsPerModRect,
                maxCallsPerMod,
                0f,
                20f,
                false,
                "ATC_PolicyAgent_MaxCallsPerMod".Translate(maxCallsPerMod),
                "0",
                "20"));

            Text.Font = GameFont.Tiny;
            Widgets.Label(l.GetRect(24f), "ATC_PolicyAgent_RetryNotice".Translate(Settings.PolicyAgentMaxRetriesPerRequest));
            Widgets.Label(l.GetRect(58f), "ATC_PolicyAgent_BudgetPrompt_Notice".Translate());
            Text.Font = GameFont.Small;

            Rect clearCacheRect = l.GetRect(32f);
            bool clearClicked = Widgets.ButtonText(clearCacheRect, "ATC_PolicyAgent_ClearCache".Translate());
            if (clearClicked && canEdit)
            {
                bool cacheCleared = AutoTranslatorScanner.ClearTranslationPolicyAgentCache();
                Messages.Message(
                    (cacheCleared
                        ? "ATC_PolicyAgent_CacheCleared"
                        : "ATC_PolicyAgent_CacheClearFailed").Translate(),
                    cacheCleared ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.RejectInput,
                    false);
            }

            Rect clearTranslationCacheRect = l.GetRect(32f);
            bool clearTranslationCacheClicked = Widgets.ButtonText(
                clearTranslationCacheRect,
                "ATC_TranslationCache_Clear".Translate());
            if (clearTranslationCacheClicked && canEdit)
            {
                bool cacheCleared = AutoTranslatorScanner.ClearValidatedTranslationResultCache();
                Messages.Message(
                    (cacheCleared
                        ? "ATC_TranslationCache_Cleared"
                        : "ATC_TranslationCache_ClearFailed").Translate(),
                    cacheCleared ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.RejectInput,
                    false);
            }

            Rect sourcePriorityRect = l.GetRect(34f);
            if (Widgets.ButtonText(sourcePriorityRect, "ATC_SourcePriority_Open".Translate()) && canEdit)
            {
                Find.WindowStack.Add(new Window_TranslationSourcePriority());
            }

            if (canEdit)
            {
                Settings.EnableTranslationPolicyAgent = enabled;
                Settings.EnablePolicyAnalysisCloudCache = false;
                Settings.PolicyAgentMaxCallsPerRun = Math.Min(20, Math.Max(0, maxCallsPerRun));
                Settings.PolicyAgentMaxEstimatedTokensPerRun = Math.Min(
                    200000L,
                    Math.Max(0L, maxEstimatedTokensPerRun));
                Settings.PolicyAgentMaxCallsPerMod = Math.Min(20, Math.Max(0, maxCallsPerMod));
            }

            GUI.color = Color.white;
        }

        private void DrawStructuredOutputSelector(Listing_Standard l, ApiKeyConfig config, bool canEdit)
        {
            if (config == null) return;

            Rect row = l.GetRect(30f);
            Rect selectorRect = new Rect(row.x, row.y, row.width * 0.47f, row.height - 2f);
            Rect statusRect = new Rect(row.x + row.width * 0.49f, row.y + 3f, row.width * 0.51f, row.height - 2f);
            bool supported = config.Provider != TranslatorProvider.DeepL;
            GUI.color = canEdit && supported ? Color.white : Color.grey;

            string preferenceLabel = GetStructuredOutputPreferenceLabel(config.StructuredOutput);
            if (Widgets.ButtonText(
                    selectorRect,
                    "ATC_StructuredOutput_Label".Translate() + ": " + preferenceLabel) &&
                canEdit && supported)
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (StructuredOutputPreference value in Enum.GetValues(typeof(StructuredOutputPreference)))
                {
                    StructuredOutputPreference captured = value;
                    options.Add(new FloatMenuOption(
                        GetStructuredOutputPreferenceLabel(captured),
                        () => config.StructuredOutput = captured));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            Text.Font = GameFont.Tiny;
            Widgets.Label(
                statusRect,
                "ATC_StructuredOutput_Effective".Translate(GetEffectiveStructuredOutputLabel(config)));
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(row, "ATC_StructuredOutput_Tooltip".Translate());
            GUI.color = Color.white;
        }

        private void DrawTerminologySettings(Listing_Standard l)
        {
            bool canEdit = !AutoTranslatorSettings.IsRunning;
            bool enabled = Settings.EnableTerminologyConsistency;
            GUI.color = canEdit ? Color.white : Color.gray;
            Rect enableRect = l.GetRect(30f);
            Widgets.CheckboxLabeled(enableRect, "ATC_Terminology_Enable".Translate(), ref enabled);
            TooltipHandler.TipRegion(enableRect, "ATC_Terminology_EnableTooltip".Translate());
            if (canEdit) Settings.EnableTerminologyConsistency = enabled;

            GUI.color = canEdit && enabled ? Color.white : Color.gray;
            Rect configureRect = l.GetRect(34f);
            if (Widgets.ButtonText(configureRect,
                    "ATC_Terminology_Configure".Translate(Settings.TerminologyEnabledPackageIds.Count)) &&
                canEdit && enabled)
                Find.WindowStack.Add(new Window_TerminologySettings());
            GUI.color = Color.white;
        }

        private void DrawTranslationTaskTierSelector(Listing_Standard l, ApiKeyConfig config, bool canEdit)
        {
            if (config == null) return;
            Rect row = l.GetRect(30f);
            bool hasOtherBulkFoundation = Settings.ApiConfigs != null && Settings.ApiConfigs.Any(candidate =>
                !ReferenceEquals(candidate, config) &&
                AutoTranslatorAPI.IsConfigReady(candidate) &&
                candidate.TaskTier == TranslationTaskTier.Bulk);
            bool canSelectOptionalTier = hasOtherBulkFoundation;

            GUI.color = canEdit ? Color.white : Color.grey;
            if (Widgets.ButtonText(
                    row,
                    "ATC_TaskTier_Label".Translate() + ": " + GetTranslationTaskTierLabel(config.TaskTier)) &&
                canEdit)
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption(
                        GetTranslationTaskTierLabel(TranslationTaskTier.Bulk),
                        () => config.TaskTier = TranslationTaskTier.Bulk)
                };
                if (canSelectOptionalTier)
                {
                    options.Add(new FloatMenuOption(
                        GetTranslationTaskTierLabel(TranslationTaskTier.Standard),
                        () => config.TaskTier = TranslationTaskTier.Standard));
                    options.Add(new FloatMenuOption(
                        GetTranslationTaskTierLabel(TranslationTaskTier.Precision),
                        () => config.TaskTier = TranslationTaskTier.Precision));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            TooltipHandler.TipRegion(
                row,
                (canSelectOptionalTier
                    ? "ATC_TaskTier_Tooltip"
                    : "ATC_TaskTier_RequiresBulk").Translate());
            GUI.color = Color.white;
        }

        private static string GetTranslationTaskTierLabel(TranslationTaskTier tier)
        {
            switch (tier)
            {
                case TranslationTaskTier.Standard:
                    return "ATC_TaskTier_Standard".Translate();
                case TranslationTaskTier.Precision:
                    return "ATC_TaskTier_Precision".Translate();
                default:
                    return "ATC_TaskTier_Bulk".Translate();
            }
        }

        private static string GetStructuredOutputPreferenceLabel(StructuredOutputPreference preference)
        {
            switch (preference)
            {
                case StructuredOutputPreference.PromptOnly:
                    return "ATC_StructuredOutput_PromptOnly".Translate();
                case StructuredOutputPreference.JsonObject:
                    return "ATC_StructuredOutput_JsonObject".Translate();
                case StructuredOutputPreference.JsonSchema:
                    return "ATC_StructuredOutput_JsonSchema".Translate();
                default:
                    return "ATC_StructuredOutput_Auto".Translate();
            }
        }

        private static string GetEffectiveStructuredOutputLabel(ApiKeyConfig config)
        {
            if (config == null || config.Provider == TranslatorProvider.DeepL)
                return "ATC_StructuredOutput_Native".Translate();

            if (config.Provider == TranslatorProvider.DeepSeek)
            {
                string baseUrl = string.IsNullOrWhiteSpace(config.CustomBaseUrl)
                    ? DeepSeekProviderAdapter.OfficialBaseUrl
                    : config.CustomBaseUrl;
                PolicyStructuredMode mode = PolicyStructuredProviderAdapter.ResolveMode(config, baseUrl);
                return mode == PolicyStructuredMode.DeepSeekFunction
                    ? "ATC_StructuredOutput_StrictFunction".Translate().ToString()
                    : GetPolicyStructuredModeLabel(mode);
            }

            StructuredTranslationMode translationMode = StructuredTranslationProviderAdapter.ResolveMode(config);
            switch (translationMode)
            {
                case StructuredTranslationMode.JsonObject:
                    return "ATC_StructuredOutput_JsonObject".Translate();
                case StructuredTranslationMode.JsonSchema:
                    return "ATC_StructuredOutput_JsonSchema".Translate();
                case StructuredTranslationMode.GeminiSchema:
                    return "ATC_StructuredOutput_GeminiSchema".Translate();
                default:
                    return "ATC_StructuredOutput_PromptOnly".Translate();
            }
        }

        private static string GetPolicyStructuredModeLabel(PolicyStructuredMode mode)
        {
            switch (mode)
            {
                case PolicyStructuredMode.JsonObject:
                    return "ATC_StructuredOutput_JsonObject".Translate();
                case PolicyStructuredMode.JsonSchema:
                    return "ATC_StructuredOutput_JsonSchema".Translate();
                case PolicyStructuredMode.GeminiSchema:
                    return "ATC_StructuredOutput_GeminiSchema".Translate();
                case PolicyStructuredMode.DeepSeekFunction:
                    return "ATC_StructuredOutput_StrictFunction".Translate();
                default:
                    return "ATC_StructuredOutput_PromptOnly".Translate();
            }
        }

        private void DrawHardcodedUiPrototypeSettings(Listing_Standard l)
        {
            bool previousEnabled = Settings.EnableHardcodedUiPrototype;
            bool enabled = previousEnabled;
            Rect enableRect = l.GetRect(30f);
            Widgets.CheckboxLabeled(enableRect, "ATC_HardcodedUi_EnablePrototype".Translate(), ref enabled);
            if (Mouse.IsOver(enableRect))
            {
                TooltipHandler.TipRegion(enableRect, "ATC_HardcodedUi_EnablePrototypeTooltip".Translate());
            }

            if (enabled != previousEnabled)
            {
                Settings.EnableHardcodedUiPrototype = enabled;
                if (enabled)
                    Settings.EnableUIInterceptor = false;
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.RequestReload();
                WriteSettings();
            }

            bool previousAgentEnabled = Settings.EnableTranslationPolicyAgent;
            bool agentEnabled = previousAgentEnabled;
            Rect agentRect = l.GetRect(30f);
            Widgets.CheckboxLabeled(agentRect, "ATC_HardcodedUi_UseAgent".Translate(), ref agentEnabled);
            if (Mouse.IsOver(agentRect))
                TooltipHandler.TipRegion(agentRect, "ATC_HardcodedUi_UseAgentTooltip".Translate());
            if (agentEnabled != previousAgentEnabled)
            {
                Settings.EnableTranslationPolicyAgent = agentEnabled;
                WriteSettings();
            }

            Text.Font = GameFont.Tiny;
            Widgets.Label(l.GetRect(24f), "ATC_HardcodedUi_Status".Translate(
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.GetStatusLine()));
            Text.Font = GameFont.Small;

            Rect reloadRect = l.GetRect(32f);
            Rect workbenchRect = new Rect(reloadRect.x, reloadRect.y, reloadRect.width * 0.62f, reloadRect.height);
            Rect reloadButtonRect = new Rect(reloadRect.x + reloadRect.width * 0.64f, reloadRect.y, reloadRect.width * 0.36f, reloadRect.height);
            if (Widgets.ButtonText(workbenchRect, "ATC_HardcodedUi_OpenWorkbench".Translate()))
            {
                Find.WindowStack.Add(new Window_HardcodedUiWorkbench());
            }
            if (Widgets.ButtonText(reloadButtonRect, "ATC_HardcodedUi_ReloadManifest".Translate()))
            {
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.RequestReload();
            }

        }

        private static void QueueLegacyRepairFromSettings()
        {
            if (_settingsLegacyRepairRunning) return;
            _settingsLegacyRepairRunning = true;

            Task.Run(() =>
            {
                AutoTranslatorLegacyRepairer.RepairSummary summary = null;
                Exception failure = null;
                try
                {
                    summary = AutoTranslatorLegacyRepairer.RepairCurrentLanguagePack(requestMemoryDrop: true);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _settingsLegacyRepairRunning = false;
                    if (failure != null)
                    {
                        Log.Warning($"[AutoTranslationCore] Legacy repair failed: {failure.Message}");
                        AutoTranslatorSettings.AddErrorLog("Legacy repair failed: " + failure.Message);
                        return;
                    }

                    summary = summary ?? new AutoTranslatorLegacyRepairer.RepairSummary();
                    Messages.Message(
                        "ATC_Msg_RepairLegacyTranslationsDone".Translate(summary.FilesTouched, summary.EntriesFixed, summary.StructureWarnings),
                        summary.FilesTouched > 0 ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NeutralEvent,
                        false);
                });
            });
        }

        private static void QueueRestoreLatestBackupsFromSettings()
        {
            if (_settingsRestoreBackupRunning) return;
            _settingsRestoreBackupRunning = true;

            List<AutoTranslatorScanner.LocalTranslationRestoreTarget> targets = Verse.ModLister.AllInstalledMods
                .Where(m => m != null && m.Active && !string.IsNullOrWhiteSpace(m.PackageId))
                .Select(m => new AutoTranslatorScanner.LocalTranslationRestoreTarget { PackageId = m.PackageId })
                .ToList();

            Task.Run(() =>
            {
                int restored = 0;
                Exception failure = null;
                try
                {
                    restored = AutoTranslatorScanner.RestoreLatestBackups(targets);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _settingsRestoreBackupRunning = false;
                    if (failure != null)
                    {
                        Log.Warning($"[AutoTranslationCore] Restore latest backups failed: {failure.Message}");
                        AutoTranslatorSettings.AddErrorLog("Restore latest backups failed: " + failure.Message);
                        return;
                    }

                    Messages.Message("ATC_Msg_RestoreLatestBackupDone".Translate(restored), restored > 0 ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NeutralEvent, false);
                });
            });
        }


// 這個方法負責繪製 執行期ProfilePanel 介面。
// EN: This method draws runtime profile panel.
private void DrawRuntimeProfilePanel(Listing_Standard l, Rect viewRect)
        {
            var profile = AutoTranslatorAPI.GetCurrentRuntimeProfile();
            Rect panelRect = l.GetRect(78f);
            Widgets.DrawBoxSolid(panelRect, new Color(0.06f, 0.07f, 0.08f, 0.85f));
            Widgets.DrawBox(panelRect, 1);

            Text.Font = GameFont.Tiny;
            Rect left = new Rect(panelRect.x + 8f, panelRect.y + 6f, panelRect.width * 0.5f - 10f, panelRect.height - 8f);
            Rect right = new Rect(panelRect.x + panelRect.width * 0.52f, panelRect.y + 6f, panelRect.width * 0.48f - 10f, panelRect.height - 8f);

            string profileLine = "ATC_Profile_Current".Translate(
                profile.BatchSize.ToString(),
                profile.FormatRetries.ToString(),
                Settings.TimeoutSeconds.ToString());

            Widgets.Label(left,
                "⚙️ " + profileLine + "\n" +
                "🧭 " + profile.QualityHintKey.Translate());

            Widgets.Label(right,
                "📡 " + "ATC_Perf_Api".Translate(
                    AutoTranslatorPerf.ActiveApiRequests.ToString(),
                    AutoTranslatorPerf.AverageApiMs.ToString(),
                    AutoTranslatorPerf.LastApiMs.ToString()) + "\n" +
                "🪂 " + "ATC_Perf_MemoryDrop".Translate(
                    AutoTranslatorPerf.LastMemoryDropMs.ToString(),
                    AutoTranslatorPerf.LastMemoryDropKeyed.ToString(),
                    AutoTranslatorPerf.LastMemoryDropDefs.ToString()) + "\n" +
                "🛡️ " + "ATC_Perf_UI".Translate(
                    UIInterceptor.GetQueueCount().ToString(),
                    UIInterceptor.GetPendingCount().ToString(),
                    UIInterceptor.GetIgnoredCount().ToString()));

            Text.Font = GameFont.Small;
        }


        // 這個方法負責執行 FactoryReset 動作。
        // EN: This method executes factory reset.
        private void ExecuteFactoryReset()
        {
            if (_settingsFactoryResetRunning) return;
            _settingsFactoryResetRunning = true;

            try
            {
                string packPath = AutoTranslatorScanner.GetLocalPackPath();
                string langsPath = System.IO.Path.Combine(packPath, "Languages");
                Task.Run(() =>
                {
                    Exception failure = null;
                    try
                    {
                        if (System.IO.Directory.Exists(langsPath))
                        {
                            foreach (string file in System.IO.Directory.GetFiles(langsPath, "*", System.IO.SearchOption.AllDirectories))
                            {
                                System.IO.File.SetAttributes(file, System.IO.FileAttributes.Normal);
                            }

                            System.IO.Directory.Delete(langsPath, true);
                            AutoTranslatorScanner.NotifyTranslationFilesChanged(langsPath);
                        }

                        AutoTranslatorScanner.EnsurePackInitialized(runFullMaintenance: false);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }

                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        _settingsFactoryResetRunning = false;

                        if (failure != null)
                        {
                            Verse.Log.Error($"[AutoTranslationCore] Factory Reset Failed: {failure.Message}");
                            AutoTranslatorSettings.AddErrorLog("Factory Reset Failed: " + failure.Message);
                            return;
                        }

                        UIInterceptor.ClearUICache();


                        AutoTranslatorMod.Settings.ModLastVerifiedTimes.Clear();
                        AutoTranslatorMod.Settings.ModLastVerifiedFingerprints.Clear();
                        AutoTranslatorMod.Settings.ClearPackageBlacklists();
                        LoadedModManager.GetMod<AutoTranslatorMod>().WriteSettings();


                        AutoTranslatorScanner.RestoreRuntimeTranslationsAfterPackReset();
                        AutoTranslatorSettings.ClearLog();
                        AutoTranslatorSettings.AddLog("🚑 " + "ATC_Log_FactoryResetSuccess".Translate());

                        Verse.Messages.Message("ATC_Msg_FactoryResetSuccess".Translate(), RimWorld.MessageTypeDefOf.PositiveEvent, false);
                    });
                });
            }
            catch (Exception ex)
            {
                _settingsFactoryResetRunning = false;
                Verse.Log.Error($"[AutoTranslationCore] Factory Reset Failed: {ex.Message}");
                AutoTranslatorSettings.AddErrorLog("Factory Reset Failed: " + ex.Message);
            }
        }
    }
}
