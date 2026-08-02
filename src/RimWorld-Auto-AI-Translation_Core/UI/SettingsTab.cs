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
            Widgets.CheckboxLabeled(l.GetRect(30f), "ATC_EnableUIInterceptor".Translate(), ref Settings.EnableUIInterceptor);
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
