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
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.RequestReload();
            }
            if (!Settings.EnableUIInterceptor && !AutoTranslatorSettings.IsRunning) GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_EnableUINewTranslation".Translate(), ref Settings.EnableUINewTranslation);
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_EnableUIErrorLogInterception".Translate(), ref Settings.EnableUIErrorLogInterception);
            GUI.color = AutoTranslatorSettings.IsRunning ? Color.grey : Color.white;
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_TranslateWorkbenchModNames".Translate(), ref Settings.TranslateWorkbenchModNames);
            if (!Settings.EnableUIInterceptor && !AutoTranslatorSettings.IsRunning) GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_ShowOriginalUI".Translate(), ref Settings.ShowOriginalUI);
            GUI.color = Color.white;
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

            DrawTranslationPolicyAgentSettings(l);
            l.Gap(15f);

            DrawHardcodedUiPrototypeSettings(l);
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
                    else if (!AutoTranslatorSettings.IsRunning)
                    {
                        AutoTranslatorAPI.AutoFetchForConfig(config, true);
                    }
                }

                if (config.IsTesting && config.TestStartedUtcTicks > 0)
                {
                    double elapsedSeconds = (DateTime.UtcNow.Ticks - config.TestStartedUtcTicks) / (double)TimeSpan.TicksPerSecond;
                    if (elapsedSeconds > Math.Max(30, AutoTranslatorMod.Settings.TimeoutSeconds + 15))
                    {
                        config.IsTesting = false;
                        config.TestStartedUtcTicks = 0L;
                        config.TestGeneration++;
                        AutoTranslatorSettings.AddErrorLog(AutoTranslatorAPI.TranslateText("ATC_Error_TestConnectionTimeout", config.Provider.ToString()));
                    }
                }

                if (config.IsTesting)
                {
                    GUI.color = Color.yellow;
                    Widgets.Label(testBtnRect, "⏳ " + "ATC_Testing".Translate());
                }
                else
                {
                    GUI.color = AutoTranslatorSettings.IsRunning ? Color.grey : new Color(0.6f, 0.9f, 0.6f);
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
                        else if (!AutoTranslatorSettings.IsRunning)
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

        private void DrawTranslationPolicyAgentSettings(Listing_Standard l)
        {
            bool canEdit = !AutoTranslatorSettings.IsRunning;
            bool enabled = Settings.EnableTranslationPolicyAgent;
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

            DrawPolicyAgentApiConfig(l, canEdit);

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

            if (canEdit)
            {
                Settings.EnableTranslationPolicyAgent = enabled;
                Settings.PolicyAgentMaxCallsPerRun = Math.Min(20, Math.Max(0, maxCallsPerRun));
                Settings.PolicyAgentMaxEstimatedTokensPerRun = Math.Min(
                    200000L,
                    Math.Max(0L, maxEstimatedTokensPerRun));
                Settings.PolicyAgentMaxCallsPerMod = Math.Min(20, Math.Max(0, maxCallsPerMod));
            }

            GUI.color = Color.white;
        }

        private void DrawPolicyAgentApiConfig(Listing_Standard l, bool canEdit)
        {
            if (Settings.PolicyAgentApiConfig == null)
            {
                Settings.PolicyAgentApiConfig = new ApiKeyConfig
                {
                    Label = "Policy Agent"
                };
            }

            ApiKeyConfig config = Settings.PolicyAgentApiConfig;
            if (config.FetchedModels == null) config.FetchedModels = new List<string>();

            Text.Font = GameFont.Small;
            Widgets.Label(l.GetRect(24f), "ATC_PolicyAgent_ApiTitle".Translate());
            Text.Font = GameFont.Tiny;
            Widgets.Label(l.GetRect(58f), "ATC_PolicyAgent_ApiNotice".Translate());
            Text.Font = GameFont.Small;

            GUI.color = canEdit ? Color.white : Color.grey;
            bool configEnabled = config.Enabled;
            Rect enabledRect = l.GetRect(30f);
            Widgets.CheckboxLabeled(enabledRect, "ATC_PolicyAgent_ApiEnabled".Translate(), ref configEnabled);
            if (canEdit) config.Enabled = configEnabled;

            Rect providerRow = l.GetRect(30f);
            Rect providerRect = new Rect(providerRow.x, providerRow.y, providerRow.width * 0.30f, providerRow.height - 2f);
            if (Widgets.ButtonText(providerRect, "ATC_Provider".Translate() + ": " + config.Provider))
            {
                if (canEdit)
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    foreach (TranslatorProvider provider in Enum.GetValues(typeof(TranslatorProvider)))
                    {
                        if (provider == TranslatorProvider.DeepL) continue;
                        TranslatorProvider capturedProvider = provider;
                        options.Add(new FloatMenuOption(capturedProvider.ToString(), () =>
                        {
                            config.Provider = capturedProvider;
                            config.SelectedModel = "";
                            AutoTranslatorAPI.ResetModelFetchState(config, clearModels: true);
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }

            Rect urlRect = new Rect(providerRow.x + providerRow.width * 0.32f, providerRow.y, providerRow.width * 0.68f, providerRow.height - 2f);
            if (canEdit)
            {
                config.CustomBaseUrl = Widgets.TextField(urlRect, config.CustomBaseUrl ?? "");
                if (string.IsNullOrEmpty(config.CustomBaseUrl))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(urlRect, "  " + "ATC_CustomUrlOptional".Translate());
                    GUI.color = Color.white;
                }
            }
            else
            {
                Widgets.Label(urlRect, config.CustomBaseUrl ?? "");
            }

            GUI.color = canEdit ? Color.white : Color.grey;
            Rect credentialsRow = l.GetRect(30f);
            Rect keyRect = new Rect(credentialsRow.x, credentialsRow.y, credentialsRow.width * 0.45f, credentialsRow.height - 2f);
            if (canEdit) config.Key = Widgets.TextField(keyRect, config.Key ?? "");
            else Widgets.Label(keyRect, string.IsNullOrEmpty(config.Key) ? "" : "********");
            if (string.IsNullOrEmpty(config.Key))
            {
                GUI.color = Color.gray;
                Widgets.Label(keyRect, "  " + "ATC_PasteKey".Translate());
                GUI.color = canEdit ? Color.white : Color.grey;
            }

            Rect modelInputRect = new Rect(credentialsRow.x + credentialsRow.width * 0.47f, credentialsRow.y, credentialsRow.width * 0.45f, credentialsRow.height - 2f);
            Rect modelButtonRect = new Rect(modelInputRect.xMax + 5f, credentialsRow.y, credentialsRow.width * 0.08f - 5f, credentialsRow.height - 2f);
            if (config.IsFetching)
            {
                GUI.color = Color.yellow;
                Widgets.Label(modelInputRect, "ATC_FetchingModel".Translate());
                GUI.color = canEdit ? Color.white : Color.grey;
            }
            else if (canEdit)
            {
                config.SelectedModel = Widgets.TextField(modelInputRect, config.SelectedModel ?? "");
                if (string.IsNullOrEmpty(config.SelectedModel))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(modelInputRect, "  " + "ATC_InputOrSelectModel".Translate());
                    GUI.color = Color.white;
                }
            }
            else
            {
                Widgets.Label(modelInputRect, config.SelectedModel ?? "");
            }

            if (Widgets.ButtonText(modelButtonRect, "▼") && canEdit && !config.IsFetching)
            {
                if (config.FetchedModels.Count > 0)
                {
                    List<FloatMenuOption> options = config.FetchedModels
                        .Where(model => !string.IsNullOrWhiteSpace(model))
                        .Select(model => new FloatMenuOption(model, () => config.SelectedModel = model))
                        .ToList();
                    if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
                }
                else
                {
                    Messages.Message("ATC_Msg_NoModelListManualInput".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                }
            }

            GUI.color = canEdit ? Color.white : Color.grey;
            Rect actionsRow = l.GetRect(28f);
            Rect fetchRect = new Rect(actionsRow.x, actionsRow.y + 1f, 150f, actionsRow.height - 2f);
            Rect testRect = new Rect(fetchRect.xMax + 10f, actionsRow.y + 1f, 130f, actionsRow.height - 2f);

            if (config.IsFetching)
            {
                GUI.color = Color.yellow;
                Widgets.Label(fetchRect, "ATC_FetchingModel".Translate());
            }
            else if (Widgets.ButtonText(fetchRect, "↻ " + "ATC_RefetchModels".Translate()) && canEdit)
            {
                if (!config.Enabled)
                {
                    Messages.Message("ATC_Msg_ApiKeyDisabled".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                }
                else if ((config.Provider != TranslatorProvider.Custom_OpenAI ||
                          string.IsNullOrWhiteSpace(config.CustomBaseUrl)) &&
                         (string.IsNullOrWhiteSpace(config.Key) || config.Key.Trim().Length <= 10))
                {
                    Messages.Message("ATC_EmptyConfigWarning".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    AutoTranslatorAPI.AutoFetchForConfig(config, true);
                }
            }

            GUI.color = canEdit ? new Color(0.6f, 0.9f, 0.6f) : Color.grey;
            if (config.IsTesting)
            {
                GUI.color = Color.yellow;
                Widgets.Label(testRect, "ATC_Testing".Translate());
            }
            else if (Widgets.ButtonText(testRect, "ATC_TestConnection".Translate()) && canEdit)
            {
                if (!config.Enabled)
                {
                    Messages.Message("ATC_Msg_ApiKeyDisabled".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                }
                else if (!AutoTranslatorAPI.IsPolicyAgentConfigReady(config))
                {
                    Messages.Message("ATC_PolicyAgent_ApiIncomplete".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    AutoTranslatorAPI.RunConnectionTest(config);
                }
            }

            if (config.IsTesting && config.TestStartedUtcTicks > 0)
            {
                double elapsedSeconds = (DateTime.UtcNow.Ticks - config.TestStartedUtcTicks) / (double)TimeSpan.TicksPerSecond;
                if (elapsedSeconds > Math.Max(30, AutoTranslatorMod.Settings.TimeoutSeconds + 15))
                {
                    config.IsTesting = false;
                    config.TestStartedUtcTicks = 0L;
                    config.TestGeneration++;
                    AutoTranslatorSettings.AddErrorLog(AutoTranslatorAPI.TranslateText(
                        "ATC_Error_TestConnectionTimeout", config.Provider.ToString()));
                }
            }

            bool ready = AutoTranslatorAPI.IsPolicyAgentConfigReady(config);
            Text.Font = GameFont.Tiny;
            GUI.color = ready ? new Color(0.55f, 0.9f, 0.65f) : new Color(1f, 0.75f, 0.35f);
            Widgets.Label(l.GetRect(30f), (ready
                ? "ATC_PolicyAgent_ApiReady"
                : "ATC_PolicyAgent_ApiIncomplete").Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            l.Gap(10f);
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
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.RequestReload();
                WriteSettings();
            }

            Text.Font = GameFont.Tiny;
            Widgets.Label(l.GetRect(24f), "ATC_HardcodedUi_Status".Translate(
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.GetStatusLine()));
            Text.Font = GameFont.Small;

            Rect reloadRect = l.GetRect(32f);
            if (Widgets.ButtonText(reloadRect, "ATC_HardcodedUi_ReloadManifest".Translate()))
            {
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.RequestReload();
            }

            if (Settings.EnableUIInterceptor && Settings.EnableHardcodedUiPrototype)
            {
                GUI.color = new Color(1f, 0.65f, 0.25f);
                Widgets.Label(l.GetRect(24f), "ATC_HardcodedUi_Conflict".Translate());
                GUI.color = Color.white;
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
                profile.TimeoutFloorSeconds.ToString());

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
