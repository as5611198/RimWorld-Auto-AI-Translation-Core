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
using AutoTranslator_Core.TargetedHardcodedUi;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責單模組、多模組與全域掃描的流程控制。
// EN: This file orchestrates single-mod, multi-mod, and full translation scans.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器掃描器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorScanner.
    public static partial class AutoTranslatorScanner
    {
        // 這個方法負責啟動 Single掃描 流程。
        // EN: This method starts single scan.
        public static void StartSingleScan(ModMetaData targetMod)
        {
            if (targetMod == null ||
                string.IsNullOrWhiteSpace(targetMod.PackageId) ||
                AutoTranslatorSettings.IsRunning ||
                AutoTranslatorAPI.HasOutstandingTranslationWork)
                return;
            if (AutoTranslatorMod.Settings != null && AutoTranslatorMod.Settings.IsTranslationBlacklisted(targetMod.PackageId))
            {
                Messages.Message("ATC_Blacklist_TranslationSkipped".Translate(targetMod.Name), MessageTypeDefOf.NeutralEvent, false);
                return;
            }

            AutoTranslatorMod.Settings.SessionCharCount = 0;
            ResetValidationStats();
            TranslationUnresolvedManager.BeginRun();

            AutoTranslatorSettings.IsRunning = true;

            var settings = AutoTranslatorMod.Settings;
            settings.CurrentProgress = 0f;
            settings.CurrentTaskName = $"Translating: {targetMod.Name}";
            AutoTranslatorSettings.AddLog("🚀 " + AutoTranslatorAPI.TranslateText("ATC_Log_StartSingleMod", targetMod.Name));


            var activeMods = ModLister.AllInstalledMods.Where(m =>
                m != null &&
                !string.IsNullOrWhiteSpace(m.PackageId) &&
                m.Active &&
                !BlacklistedModules.Contains(m.PackageId.ToLowerInvariant())).ToList();


            Task.Run(async () =>
            {
                long policyAgentRunId = 0L;
                bool usageRunStarted = false;
                bool usageRunCompleted = false;
                try
                {
                    policyAgentRunId = BeginTranslationPolicyAgentRun();
                    EnsurePackInitialized(runFullMaintenance: false);
                    usageRunStarted = BeginTranslationUsageRun("single", new[] { targetMod });
                    if (AutoTranslatorSettings.IsCancellationRequested) return;
                    TranslationUnresolvedManager.BeginPackageScan(
                        targetMod.PackageId,
                        settings.TargetLang.ToString());

                    if (ShouldSkipTranslationPatchMod(targetMod))
                    {
                        ModUpdateDetector.MarkModAsTranslated(
                            targetMod.PackageId,
                            GetModRootPath(targetMod),
                            false);
                        TranslationUnresolvedManager.CompletePackageScan(
                            targetMod.PackageId,
                            settings.TargetLang.ToString());
                        settings.CurrentTaskName = "ATC_TaskDone".Translate();
                        settings.CurrentProgress = 1f;
                        ModUpdateDetector.ClearStatusCache();
                        TranslationWorkbenchTab.RequestRefresh();
                        return;
                    }

                    settings.SubTaskName = "ATC_SubTask_TestingAPI".Translate();
                    AutoTranslatorSettings.AddLog("🔌 " + "ATC_Log_PreflightCheck".Translate());

                    bool isApiAlive = await AutoTranslatorAPI.TestConnectionAsync();
                    if (!isApiAlive)
                    {
                        AutoTranslatorSettings.AddErrorLog("❌ " + "ATC_LogError_ApiDeadAbort".Translate());
                        settings.CurrentTaskName = "ATC_TaskFailed".Translate();
                        return;
                    }

                    if (AutoTranslatorMod.Settings.AutoClearOldOnUpdate)
                    {
                        var updatedTracker = ModUpdateDetector.GetUpdatedOrNewModsBlocking();
                        if (updatedTracker.Any(m => string.Equals(m.PackageId, targetMod.PackageId, StringComparison.OrdinalIgnoreCase)))
                        {
                            ClearOldTranslationFiles(new List<ModMetaData> { targetMod }, requestRuntimeRefresh: false);
                        }
                    }


                    BuildGlobalTranslationDatabase(activeMods);
                    if (AutoTranslatorSettings.IsCancellationRequested) return;

                    var langRoots = GetAllEffectiveLangPaths(targetMod);
                    var defsRoots = GetAllEffectiveDefsPaths(targetMod);
                    bool hasLang = langRoots.Count > 0;
                    bool hasDefs = defsRoots.Count > 0;
                    int aiTranslatedCount = 0;
                    bool skippedNoSource = false;

                    if (!hasLang && !hasDefs)
                    {
                        AutoTranslatorSettings.AddLog("⏭️ " + "ATC_Log_SkipMod".Translate());
                        skippedNoSource = true;
                    }
                    else
                    {
                        if (hasLang)
                        {
                            foreach (var langRoot in langRoots)
                            {
                                aiTranslatedCount += await ProcessModKeyedSources(targetMod, langRoot);
                                if (TranslationUsageCoordinator.IsPausedByBudget) break;
                            }
                        }
                        if (PauseScanIfTranslationBudgetReached(settings)) return;
                        if (AutoTranslatorSettings.IsCancellationRequested) return;
                        if (hasDefs || hasLang)
                        {
                            AutoTranslatorSettings.AddLog("📦 " + "ATC_Log_DefScan".Translate());
                            aiTranslatedCount += await ProcessModDefInjected(targetMod, langRoots, defsRoots);
                        }


                        if (aiTranslatedCount > 0)
                        {
                            UpdateLocalModMeta(targetMod.PackageId, GetFolderNameByLanguage(settings.TargetLang), aiTranslatedCount);
                        }
                    }

                    if (!AutoTranslatorSettings.IsCancellationRequested &&
                        !AutoTranslatorSettings.IsSkipCurrentRequested &&
                        !TranslationUsageCoordinator.IsPausedByBudget)
                    {
                        await TryRunHardcodedUiAutomaticPipelineAsync(new[] { targetMod });
                    }

                    if (AutoTranslatorSettings.IsSkipCurrentRequested)
                    {
                        TranslationUnresolvedManager.AbortPackageScan(
                            targetMod.PackageId,
                            settings.TargetLang.ToString());
                        AutoTranslatorSettings.IsSkipCurrentRequested = false;
                        return;
                    }

                    if (!AutoTranslatorSettings.IsCancellationRequested)
                    {
                        settings.CurrentTaskName = "ATC_TaskDone".Translate();

                        TranslationUnresolvedManager.CompletePackageScan(
                            targetMod.PackageId,
                            settings.TargetLang.ToString());
                        bool hasPending = TranslationUnresolvedManager.HasPendingForPackage(
                            targetMod.PackageId,
                            settings.TargetLang.ToString());
                        if (!skippedNoSource && !hasPending)
                        {
                            ModUpdateDetector.MarkModAsTranslated(
                                targetMod.PackageId,
                                GetModRootPath(targetMod),
                                false);
                        }
                        settings.CurrentProgress = 1f;
                        AutoTranslatorSettings.AddLog("✨ " + "ATC_Log_SingleModDone".Translate());
                        LogValidationSummary();


                        RequestMemoryDrop();


                        AutoTranslatorSettings.ShowFinishPopup = true;
                        ModUpdateDetector.ClearStatusCache();
                        TranslationWorkbenchTab.RequestRefresh();
                        TranslationUnresolvedManager.CompleteRun();
                        usageRunCompleted = true;
                    }
                }
                catch (Exception e)
                {
                    HandlePackageScanException(targetMod, e);
                    TryRunPipelineCleanup("set single-scan failure state", () =>
                    {
                        settings.CurrentTaskName = "ATC_TaskFailed".Translate();
                        AutoTranslatorSettings.ShowFinishPopup = true;
                    });
                }
                finally
                {
                    TryRunPipelineCleanup("save single-scan progress", () => TranslationUnresolvedManager.SaveRunProgress());
                    await TryRunPipelineCleanupAsync(
                        "end single-scan policy-agent run",
                        () => EndTranslationPolicyAgentRunAsync(policyAgentRunId, usageRunCompleted));
                    TryRunPipelineCleanup("end single-scan usage run", () => EndTranslationUsageRun(usageRunStarted, usageRunCompleted));
                    TryRunPipelineCleanup("clear single-scan translation database", () => ClearGlobalTranslationDatabase());
                    await AutoTranslatorSettings.CompleteTranslationPipelineAsync();
                }
            });
        }

        public static void StartPureAiRebuildForUpload(ModMetaData targetMod)
        {
            if (targetMod == null ||
                AutoTranslatorSettings.IsRunning ||
                AutoTranslatorAPI.HasOutstandingTranslationWork)
                return;

            AutoTranslatorMod.Settings.SessionCharCount = 0;
            ResetValidationStats();
            TranslationUnresolvedManager.BeginRun();
            AutoTranslatorSettings.ResetPipelineCancellation();
            AutoTranslatorSettings.IsRunning = true;
            AutoTranslatorSettings.ActiveTab = 0;

            var settings = AutoTranslatorMod.Settings;
            string targetFolder = GetFolderNameByLanguage(settings.TargetLang);
            settings.CurrentProgress = 0f;
            settings.SubProgress = 0f;
            settings.CurrentTaskName = AutoTranslatorAPI.TranslateText("ATC_Task_PureAiRebuildForUpload", targetMod.Name);
            settings.SubTaskName = "";
            AutoTranslatorSettings.AddLog("🚀 " + AutoTranslatorAPI.TranslateText("ATC_Log_PureAiRebuildStart", targetMod.Name));

            Task.Run(async () =>
            {
                int aiTranslatedCount = 0;
                long policyAgentRunId = 0L;
                bool usageRunStarted = false;
                bool usageRunCompleted = false;
                try
                {
                    policyAgentRunId = BeginTranslationPolicyAgentRun();
                    EnsurePackInitialized(runFullMaintenance: false);
                    usageRunStarted = BeginTranslationUsageRun("pure-ai-rebuild", new[] { targetMod });
                    if (AutoTranslatorSettings.IsCancellationRequested) return;
                    TranslationUnresolvedManager.BeginPackageScan(
                        targetMod.PackageId,
                        settings.TargetLang.ToString());

                    if (ShouldSkipTranslationPatchMod(targetMod))
                    {
                        TranslationUnresolvedManager.AbortPackageScan(
                            targetMod.PackageId,
                            settings.TargetLang.ToString());
                        AutoTranslatorSettings.AddErrorLog("❌ " + AutoTranslatorAPI.TranslateText("ATC_Log_PureAiRebuildBlockedPatchMod", targetMod.Name));
                        settings.CurrentTaskName = "ATC_TaskFailed".Translate();
                        return;
                    }

                    settings.SubTaskName = "ATC_SubTask_TestingAPI".Translate();
                    AutoTranslatorSettings.AddLog("🔌 " + "ATC_Log_PreflightCheck".Translate());

                    bool isApiAlive = await AutoTranslatorAPI.TestConnectionAsync();
                    if (!isApiAlive)
                    {
                        AutoTranslatorSettings.AddErrorLog("❌ " + "ATC_LogError_ApiDeadAbort".Translate());
                        settings.CurrentTaskName = "ATC_TaskFailed".Translate();
                        return;
                    }

                    string workspaceLangRoot = GetTranslationOutputLanguageRoot(targetMod, targetFolder, TranslationOutputMode.PureAiWorkspace);
                    if (!TranslationUsageCoordinator.WasResumed)
                        BackupAndClearPureAiWorkspace(targetMod, workspaceLangRoot);

                    var langRoots = GetAllEffectiveLangPaths(targetMod);
                    var defsRoots = GetAllEffectiveDefsPaths(targetMod);
                    bool hasLang = langRoots.Count > 0;
                    bool hasDefs = defsRoots.Count > 0;

                    if (!hasLang && !hasDefs)
                    {
                        AutoTranslatorSettings.AddLog("⏭️ " + "ATC_Log_SkipMod".Translate());
                    }
                    else
                    {
                        if (hasLang)
                        {
                            int totalLangRoots = Math.Max(1, langRoots.Count);
                            for (int i = 0; i < langRoots.Count; i++)
                            {
                                settings.CurrentProgress = 0.1f + 0.35f * i / totalLangRoots;
                                settings.SubTaskName = "ATC_SubTask_TranslatingKeyed".Translate();
                                aiTranslatedCount += await ProcessModKeyedSources(targetMod, langRoots[i], TranslationOutputMode.PureAiWorkspace);
                                if (TranslationUsageCoordinator.IsPausedByBudget) break;
                            }
                        }

                        if (PauseScanIfTranslationBudgetReached(settings)) return;
                        if (AutoTranslatorSettings.IsCancellationRequested) return;
                        if (hasDefs || hasLang)
                        {
                            settings.CurrentProgress = 0.55f;
                            settings.SubTaskName = "ATC_SubTask_TranslatingDef".Translate();
                            AutoTranslatorSettings.AddLog("📦 " + "ATC_Log_DefScan".Translate());
                            aiTranslatedCount += await ProcessModDefInjected(targetMod, langRoots, defsRoots, TranslationOutputMode.PureAiWorkspace);
                        }
                    }

                    if (AutoTranslatorSettings.IsSkipCurrentRequested)
                    {
                        TranslationUnresolvedManager.AbortPackageScan(
                            targetMod.PackageId,
                            settings.TargetLang.ToString());
                        AutoTranslatorSettings.IsSkipCurrentRequested = false;
                        return;
                    }

                    if (!AutoTranslatorSettings.IsCancellationRequested)
                    {
                        if (aiTranslatedCount > 0)
                        {
                            WritePureAiWorkspaceMeta(targetMod.PackageId, targetFolder, workspaceLangRoot, aiTranslatedCount);
                            AutoTranslatorSettings.AddLog("✅ " + AutoTranslatorAPI.TranslateText("ATC_Log_PureAiRebuildDone", targetMod.Name, aiTranslatedCount));
                            ATC_Dispatcher.RunOnMainThread(() =>
                                Messages.Message("ATC_Msg_PureAiRebuildDone".Translate(targetMod.Name), MessageTypeDefOf.PositiveEvent, false));
                        }
                        else
                        {
                            AutoTranslatorSettings.AddLog("⚠️ " + AutoTranslatorAPI.TranslateText("ATC_Log_PureAiRebuildNoEntries", targetMod.Name));
                            ATC_Dispatcher.RunOnMainThread(() =>
                                Messages.Message("ATC_Msg_PureAiRebuildNoEntries".Translate(targetMod.Name), MessageTypeDefOf.RejectInput, false));
                        }

                        TranslationUnresolvedManager.CompletePackageScan(
                            targetMod.PackageId,
                            settings.TargetLang.ToString());

                        settings.CurrentTaskName = "ATC_TaskDone".Translate();
                        settings.CurrentProgress = 1f;
                        settings.SubTaskName = "";
                        settings.SubProgress = 1f;
                        LogValidationSummary();
                        if (TranslationUnresolvedManager.HasPending)
                        {
                            AutoTranslatorSettings.ShowFinishPopup = true;
                        }
                        ModUpdateDetector.ClearStatusCache();
                        TranslationWorkbenchTab.RequestRefresh();
                        TranslationUnresolvedManager.CompleteRun();
                        usageRunCompleted = true;
                    }
                }
                catch (Exception e)
                {
                    HandlePackageScanException(targetMod, e);
                    TryRunPipelineCleanup("set pure-AI failure state", () =>
                    {
                        settings.CurrentTaskName = "ATC_TaskFailed".Translate();
                        AutoTranslatorSettings.ShowFinishPopup = true;
                    });
                }
                finally
                {
                    TryRunPipelineCleanup("save pure-AI progress", () => TranslationUnresolvedManager.SaveRunProgress());
                    await TryRunPipelineCleanupAsync(
                        "end pure-AI policy-agent run",
                        () => EndTranslationPolicyAgentRunAsync(policyAgentRunId, usageRunCompleted));
                    TryRunPipelineCleanup("end pure-AI usage run", () => EndTranslationUsageRun(usageRunStarted, usageRunCompleted));
                    if (AutoTranslatorSettings.IsCancellationRequested)
                    {
                        TryRunPipelineCleanup("reset pure-AI cancellation state", () =>
                        {
                            settings.CurrentTaskName = "";
                            settings.CurrentProgress = 0f;
                            settings.SubTaskName = "";
                            settings.SubProgress = 0f;
                        });
                    }
                    await AutoTranslatorSettings.CompleteTranslationPipelineAsync();
                }
            });
        }

        private static void BackupAndClearPureAiWorkspace(ModMetaData mod, string workspaceLangRoot)
        {
            if (mod == null || string.IsNullOrWhiteSpace(workspaceLangRoot)) return;

            try
            {
                if (Directory.Exists(workspaceLangRoot))
                {
                    string backupRoot = Path.Combine(
                        GetLocalPackPath(),
                        "Backups",
                        "PureAIRebuild",
                        DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + SanitizeFileName(mod.PackageId));
                    Directory.CreateDirectory(backupRoot);
                    CopyDirectoryContents(workspaceLangRoot, backupRoot);
                    Directory.Delete(workspaceLangRoot, true);
                    AutoTranslatorSettings.AddLog("🧰 " + AutoTranslatorAPI.TranslateText("ATC_Log_PureAiWorkspaceBackedUp", mod.Name));
                }

                Directory.CreateDirectory(workspaceLangRoot);
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Failed to backup pure AI workspace for {mod.PackageId}: {ex.Message}");
                Directory.CreateDirectory(workspaceLangRoot);
            }
        }

        private static void CopyDirectoryContents(string sourceDir, string destDir)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(destDir) || !Directory.Exists(sourceDir)) return;

            foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                string rel = dir.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destDir, rel));
            }

            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string dest = Path.Combine(destDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(file, dest, true);
            }
        }

        private static void WritePureAiWorkspaceMeta(string packageId, string targetLangFolder, string workspaceLangRoot, int newAiCount)
        {
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(workspaceLangRoot)) return;

            try
            {
                Directory.CreateDirectory(workspaceLangRoot);
                string cleanPackageId = packageId.Replace(".", "_").ToLower();
                string metaPath = Path.Combine(workspaceLangRoot, $"{cleanPackageId}_ATC_Meta.json");
                LocalModMeta meta = new LocalModMeta
                {
                    OriginalRecordId = "",
                    TargetModVersion = RimWorld.VersionControl.CurrentVersionStringWithoutBuild,
                    TranslationDate = DateTime.UtcNow,
                    IsSmartMerged = false,
                    MergedAiCount = newAiCount
                };

                File.WriteAllText(metaPath, JsonConvert.SerializeObject(meta, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] WritePureAiWorkspaceMeta error: {ex.Message}");
            }
        }
        // 這個方法負責啟動 Full掃描 流程。
        // EN: This method starts full scan.
        public static void StartFullScan()
        {
            if (AutoTranslatorSettings.IsRunning ||
                AutoTranslatorAPI.HasOutstandingTranslationWork)
                return;

            AutoTranslatorMod.Settings.SessionCharCount = 0;
            ResetValidationStats();
            TranslationUnresolvedManager.BeginRun();
            AutoTranslatorSettings.IsRunning = true;

            var settings = AutoTranslatorMod.Settings;

            var mods = ModLister.AllInstalledMods.Where(m =>
                            m != null &&
                            !string.IsNullOrWhiteSpace(m.PackageId) &&
                            !BlacklistedModules.Contains(m.PackageId.ToLowerInvariant()) &&
                            !settings.IsTranslationBlacklisted(m.PackageId) &&
                            !IsOfficialBaseGameOrDlcPackage(m.PackageId) &&
                            (!settings.OnlyScanActiveMods || m.Active)).ToList();
            AutoTranslatorSettings.AddLog("🌐 " + AutoTranslatorAPI.TranslateText("ATC_Log_StartScan", mods.Count));


            Task.Run(async () =>
            {
                long policyAgentRunId = 0L;
                bool usageRunStarted = false;
                bool usageRunCompleted = false;
                bool runHadPackageFailures = false;
                try
                {
                    policyAgentRunId = BeginTranslationPolicyAgentRun();
                    EnsurePackInitialized(runFullMaintenance: false);
                    usageRunStarted = BeginTranslationUsageRun("full", mods);
                    if (AutoTranslatorSettings.IsCancellationRequested) return;


                    settings.SubTaskName = "ATC_SubTask_TestingAPI".Translate();
                    AutoTranslatorSettings.AddLog("🔌 " + "ATC_Log_PreflightCheck".Translate());

                    bool isApiAlive = await AutoTranslatorAPI.TestConnectionAsync();
                    if (!isApiAlive)
                    {
                        AutoTranslatorSettings.AddErrorLog("❌ " + "ATC_LogError_ApiDeadAbort".Translate());
                        settings.CurrentTaskName = "ATC_TaskFailed".Translate();
                        return;
                    }

                    HashSet<string> updatedPackageIds = AutoTranslatorMod.Settings.AutoClearOldOnUpdate
                        ? new HashSet<string>(
                            ModUpdateDetector.GetUpdatedOrNewModsBlocking()
                                .Where(m => m != null && !string.IsNullOrEmpty(m.PackageId))
                                .Select(m => m.PackageId),
                            StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    BuildGlobalTranslationDatabase(mods);
                    int total = mods.Count;
                    int current = 0;
                    foreach (var mod in mods)
                    {
                        try
                        {


                        if (IsTranslationPatchMod(mod))
                        {
                            continue;
                        }

                        if (ShouldSkipTranslationPatchMod(mod))
                        {
                            ModUpdateDetector.MarkModAsTranslated(
                                mod.PackageId,
                                GetModRootPath(mod),
                                false);
                            continue;
                        }

                        if (AutoTranslatorSettings.IsCancellationRequested) break;
                        if (AutoTranslatorSettings.IsSkipCurrentRequested)
                        {
                            AutoTranslatorSettings.AddLog("⏭️ " + AutoTranslatorAPI.TranslateText("ATC_Log_SkippedMod", mod.Name));
                            AutoTranslatorSettings.IsSkipCurrentRequested = false;
                            continue;
                        }

                        TranslationUnresolvedManager.BeginPackageScan(
                            mod.PackageId,
                            settings.TargetLang.ToString());

                        current++;
                        settings.CurrentProgress = (float)current / total;
                        settings.CurrentTaskName = $"Translating: {mod.Name}";
                        settings.SubProgress = 0f;
                        settings.SubTaskName = "ATC_SubTask_Scanning".Translate();
                        AutoTranslatorSettings.AddLog("🔍 " + AutoTranslatorAPI.TranslateText("ATC_Log_ScanMod", mod.Name));

                        if (updatedPackageIds.Contains(mod.PackageId))
                        {
                            ClearOldTranslationFiles(new List<ModMetaData> { mod }, requestRuntimeRefresh: false);
                        }

                        var langRoots = GetAllEffectiveLangPaths(mod);
                        var defsRoots = GetAllEffectiveDefsPaths(mod);
                        int aiTranslatedCount = 0;
                        if (langRoots.Count == 0 && defsRoots.Count == 0)
                        {
                            AutoTranslatorSettings.AddLog("⏭️ " + "ATC_Log_SkipMod".Translate());
                            TranslationUnresolvedManager.CompletePackageScan(
                                mod.PackageId,
                                settings.TargetLang.ToString());
                            continue;
                        }

                        if (langRoots.Count > 0)
                        {
                            foreach (var langRoot in langRoots)
                            {
                                settings.SubTaskName = "ATC_SubTask_TranslatingKeyed".Translate();
                                aiTranslatedCount += await ProcessModKeyedSources(mod, langRoot);
                                if (TranslationUsageCoordinator.IsPausedByBudget) break;
                            }
                        }

                        if (TranslationUsageCoordinator.IsPausedByBudget) break;

                        if (AutoTranslatorSettings.IsCancellationRequested) break;
                        if (AutoTranslatorSettings.IsSkipCurrentRequested)
                        {
                            TranslationUnresolvedManager.AbortPackageScan(
                                mod.PackageId,
                                settings.TargetLang.ToString());
                            AutoTranslatorSettings.IsSkipCurrentRequested = false;
                            continue;
                        }

                        if (defsRoots.Count > 0 || langRoots.Count > 0)
                        {
                            settings.SubTaskName = "ATC_SubTask_TranslatingDef".Translate();
                            aiTranslatedCount += await ProcessModDefInjected(mod, langRoots, defsRoots);
                        }


                        if (!AutoTranslatorSettings.IsSkipCurrentRequested && !AutoTranslatorSettings.IsCancellationRequested)
                        {
                            TranslationUnresolvedManager.CompletePackageScan(
                                mod.PackageId,
                                settings.TargetLang.ToString());
                            bool hasPending = TranslationUnresolvedManager.HasPendingForPackage(
                                mod.PackageId,
                                settings.TargetLang.ToString());
                            if (!hasPending)
                            {
                                ModUpdateDetector.MarkModAsTranslated(
                                    mod.PackageId,
                                    GetModRootPath(mod),
                                    false);
                            }


                            if (aiTranslatedCount > 0)
                            {
                                UpdateLocalModMeta(mod.PackageId, GetFolderNameByLanguage(settings.TargetLang), aiTranslatedCount);
                            }
                        }

                        if (AutoTranslatorSettings.IsSkipCurrentRequested)
                        {
                            TranslationUnresolvedManager.AbortPackageScan(
                                mod.PackageId,
                                settings.TargetLang.ToString());
                            AutoTranslatorSettings.AddLog("⏭️ " + AutoTranslatorAPI.TranslateText("ATC_Log_SkippedMod", mod.Name));
                            AutoTranslatorSettings.IsSkipCurrentRequested = false;
                        }
                        if (TranslationUsageCoordinator.IsPausedByBudget) break;
                        }
                        catch (Exception packageException)
                        {
                            runHadPackageFailures = true;
                            HandlePackageScanException(mod, packageException);
                            if (AutoTranslatorSettings.IsCancellationRequested) break;
                        }
                    }

                    if (!AutoTranslatorSettings.IsCancellationRequested &&
                        !TranslationUsageCoordinator.IsPausedByBudget)
                    {
                        bool dllCompleted = await TryRunHardcodedUiAutomaticPipelineAsync(mods);
                        if (!dllCompleted) runHadPackageFailures = true;
                    }

                    if (PauseScanIfTranslationBudgetReached(settings))
                    {
                        AutoTranslatorSettings.ShowFinishPopup = true;
                        TranslationWorkbenchTab.RequestRefresh();
                    }
                    else if (!AutoTranslatorSettings.IsCancellationRequested)
                    {
                        settings.CurrentTaskName = "ATC_TaskDone".Translate();
                        settings.CurrentProgress = 1f;
                        settings.SubTaskName = "";
                        settings.SubProgress = 1f;
                        AutoTranslatorSettings.AddLog("🎉 " + "ATC_Log_TaskDone".Translate());
                        AutoTranslatorSettings.AddLog("🎉 " + "ATC_Log_AllTranslationWritten".Translate());
                        LogValidationSummary();
                        RequestMemoryDrop();

                        AutoTranslatorSettings.ShowFinishPopup = true;
                        ModUpdateDetector.ClearStatusCache();
                        TranslationWorkbenchTab.RequestRefresh();
                        if (!runHadPackageFailures)
                        {
                            TranslationUnresolvedManager.CompleteRun();
                            usageRunCompleted = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    TryAddLocalizedTaskError(e);
                    TryAddPipelineLog($"[CRITICAL ERROR] {e}");
                    TryLogPipelineError($"[AutoTranslationCore] Full translation run interrupted:\n{e}");
                    TryRunPipelineCleanup("set full-scan failure state", () =>
                    {
                        settings.CurrentTaskName = "ATC_TaskFailed".Translate();
                        AutoTranslatorSettings.ShowFinishPopup = true;
                    });
                }
                finally
                {
                    TryRunPipelineCleanup("save full-scan progress", () => TranslationUnresolvedManager.SaveRunProgress());
                    await TryRunPipelineCleanupAsync(
                        "end full-scan policy-agent run",
                        () => EndTranslationPolicyAgentRunAsync(policyAgentRunId, usageRunCompleted));
                    TryRunPipelineCleanup("end full-scan usage run", () => EndTranslationUsageRun(usageRunStarted, usageRunCompleted));
                    TryRunPipelineCleanup("clear full-scan translation database", () => ClearGlobalTranslationDatabase());
                    if (AutoTranslatorSettings.IsCancellationRequested)
                    {
                        TryRunPipelineCleanup("reset full-scan cancellation state", () =>
                        {
                            settings.CurrentTaskName = "";
                            settings.CurrentProgress = 0f;
                            settings.SubTaskName = "";
                            settings.SubProgress = 0f;
                        });
                    }
                    await AutoTranslatorSettings.CompleteTranslationPipelineAsync();
                }
            });
        }


        // 這個方法負責啟動 Multi掃描 流程。
        // EN: This method starts multi scan.
        public static void StartMultiScan(List<ModMetaData> targetMods, bool includeOfficialGamePackages = false)
        {
            if (AutoTranslatorSettings.IsRunning ||
                AutoTranslatorAPI.HasOutstandingTranslationWork)
                return;

            targetMods = (targetMods ?? new List<ModMetaData>())
                .Where(mod => mod != null &&
                              !string.IsNullOrWhiteSpace(mod.PackageId) &&
                              (AutoTranslatorMod.Settings == null || !AutoTranslatorMod.Settings.IsTranslationBlacklisted(mod.PackageId)) &&
                              (includeOfficialGamePackages || !IsOfficialBaseGameOrDlcPackage(mod.PackageId)))
                .ToList();
            if (targetMods.Count == 0) return;

            AutoTranslatorMod.Settings.SessionCharCount = 0;
            ResetValidationStats();
            TranslationUnresolvedManager.BeginRun();
            AutoTranslatorSettings.IsRunning = true;


            var settings = AutoTranslatorMod.Settings;
            int total = targetMods.Count;
            AutoTranslatorSettings.AddLog("🚀 " + "ATC_Log_MultiScanStart".Translate(total));

            var activeMods = ModLister.AllInstalledMods
                .Where(m => m != null &&
                            m.Active &&
                            !string.IsNullOrWhiteSpace(m.PackageId) &&
                            !BlacklistedModules.Contains(m.PackageId.ToLowerInvariant()))
                .ToList();

            Task.Run(async () =>
            {
                long policyAgentRunId = 0L;
                bool usageRunStarted = false;
                bool usageRunCompleted = false;
                bool runHadPackageFailures = false;
                try
                {
                    policyAgentRunId = BeginTranslationPolicyAgentRun();
                    EnsurePackInitialized(runFullMaintenance: false);
                    usageRunStarted = BeginTranslationUsageRun("multi", targetMods);
                    if (AutoTranslatorSettings.IsCancellationRequested) return;


                    settings.SubTaskName = "ATC_SubTask_TestingAPI".Translate();
                    AutoTranslatorSettings.AddLog("🔌 " + "ATC_Log_PreflightCheck".Translate());

                    bool isApiAlive = await AutoTranslatorAPI.TestConnectionAsync();
                    if (!isApiAlive)
                    {
                        AutoTranslatorSettings.AddErrorLog("❌ " + "ATC_LogError_ApiDeadAbort".Translate());
                        settings.CurrentTaskName = "ATC_TaskFailed".Translate();
                        return;
                    }

                    HashSet<string> updatedPackageIds = AutoTranslatorMod.Settings.AutoClearOldOnUpdate
                        ? new HashSet<string>(
                            ModUpdateDetector.GetUpdatedOrNewModsBlocking()
                                .Where(m => m != null && !string.IsNullOrEmpty(m.PackageId))
                                .Select(m => m.PackageId),
                            StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    BuildGlobalTranslationDatabase(activeMods);
                    int current = 0;
                    foreach (var mod in targetMods)
                    {
                        try
                        {
                        if (ShouldSkipTranslationPatchMod(mod))
                        {
                            ModUpdateDetector.MarkModAsTranslated(
                                mod.PackageId,
                                GetModRootPath(mod),
                                false);
                            continue;
                        }

                        if (AutoTranslatorSettings.IsCancellationRequested) break;
                        if (AutoTranslatorSettings.IsSkipCurrentRequested)
                        {
                            AutoTranslatorSettings.AddLog("⏭️ " + AutoTranslatorAPI.TranslateText("ATC_Log_SkippedMod", mod.Name));
                            AutoTranslatorSettings.IsSkipCurrentRequested = false;
                            continue;
                        }

                        TranslationUnresolvedManager.BeginPackageScan(
                            mod.PackageId,
                            settings.TargetLang.ToString());

                        current++;
                        settings.CurrentProgress = (float)current / total;
                        settings.CurrentTaskName = $"Translating: {mod.Name}";
                        settings.SubProgress = 0f;
                        settings.SubTaskName = "ATC_SubTask_Scanning".Translate();
                        AutoTranslatorSettings.AddLog(AutoTranslatorAPI.TranslateText("ATC_Log_ScanMod", mod.Name));

                        if (updatedPackageIds.Contains(mod.PackageId))
                        {
                            ClearOldTranslationFiles(new List<ModMetaData> { mod }, requestRuntimeRefresh: false);
                        }

                        var langRoots = GetAllEffectiveLangPaths(mod);
                        var defsRoots = GetAllEffectiveDefsPaths(mod);
                        int aiTranslatedCount = 0;
                        if (langRoots.Count == 0 && defsRoots.Count == 0)
                        {
                            AutoTranslatorSettings.AddLog("⏭️ " + "ATC_Log_SkipMod".Translate());
                            TranslationUnresolvedManager.CompletePackageScan(
                                mod.PackageId,
                                settings.TargetLang.ToString());
                            continue;
                        }

                        if (langRoots.Count > 0)
                        {
                            foreach (var langRoot in langRoots)
                            {
                                settings.SubTaskName = "ATC_SubTask_TranslatingKeyed".Translate();
                                aiTranslatedCount += await ProcessModKeyedSources(mod, langRoot);
                                if (TranslationUsageCoordinator.IsPausedByBudget) break;
                            }
                        }

                        if (TranslationUsageCoordinator.IsPausedByBudget) break;

                        if (AutoTranslatorSettings.IsCancellationRequested) break;
                        if (AutoTranslatorSettings.IsSkipCurrentRequested)
                        {
                            TranslationUnresolvedManager.AbortPackageScan(
                                mod.PackageId,
                                settings.TargetLang.ToString());
                            AutoTranslatorSettings.IsSkipCurrentRequested = false;
                            continue;
                        }

                        if (defsRoots.Count > 0 || langRoots.Count > 0)
                        {
                            settings.SubTaskName = "ATC_SubTask_TranslatingDef".Translate();
                            aiTranslatedCount += await ProcessModDefInjected(mod, langRoots, defsRoots);
                        }


                        if (!AutoTranslatorSettings.IsSkipCurrentRequested && !AutoTranslatorSettings.IsCancellationRequested)
                        {
                            TranslationUnresolvedManager.CompletePackageScan(
                                mod.PackageId,
                                settings.TargetLang.ToString());
                            bool hasPending = TranslationUnresolvedManager.HasPendingForPackage(
                                mod.PackageId,
                                settings.TargetLang.ToString());
                            if (!hasPending)
                            {
                                ModUpdateDetector.MarkModAsTranslated(
                                    mod.PackageId,
                                    GetModRootPath(mod),
                                    false);
                            }
                        }


                        if (AutoTranslatorSettings.IsSkipCurrentRequested)
                        {
                            TranslationUnresolvedManager.AbortPackageScan(
                                mod.PackageId,
                                settings.TargetLang.ToString());
                            AutoTranslatorSettings.AddLog("⏭️ " + AutoTranslatorAPI.TranslateText("ATC_Log_SkippedMod", mod.Name));
                            AutoTranslatorSettings.IsSkipCurrentRequested = false;
                        }
                        if (TranslationUsageCoordinator.IsPausedByBudget) break;
                        }
                        catch (Exception packageException)
                        {
                            runHadPackageFailures = true;
                            HandlePackageScanException(mod, packageException);
                            if (AutoTranslatorSettings.IsCancellationRequested) break;
                        }
                    }

                    if (!AutoTranslatorSettings.IsCancellationRequested &&
                        !TranslationUsageCoordinator.IsPausedByBudget)
                    {
                        bool dllCompleted = await TryRunHardcodedUiAutomaticPipelineAsync(targetMods);
                        if (!dllCompleted) runHadPackageFailures = true;
                    }

                    if (PauseScanIfTranslationBudgetReached(settings))
                    {
                        AutoTranslatorSettings.ShowFinishPopup = true;
                        TranslationWorkbenchTab.RequestRefresh();
                    }
                    else if (!AutoTranslatorSettings.IsCancellationRequested)
                    {
                        settings.CurrentTaskName = "ATC_TaskDone".Translate();
                        settings.CurrentProgress = 1f;
                        settings.SubTaskName = "";
                        settings.SubProgress = 1f;
                        AutoTranslatorSettings.AddLog("🎉 " + "ATC_Log_MultiScanDone".Translate());
                        LogValidationSummary();
                        RequestMemoryDrop();

                        AutoTranslatorSettings.ShowFinishPopup = true;
                        ModUpdateDetector.ClearStatusCache();
                        TranslationWorkbenchTab.RequestRefresh();
                        if (!runHadPackageFailures)
                        {
                            TranslationUnresolvedManager.CompleteRun();
                            usageRunCompleted = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    TryAddLocalizedTaskError(e);
                    TryAddPipelineLog($"[CRITICAL ERROR] {e}");
                    TryLogPipelineError($"[AutoTranslationCore] Multi-mod translation run interrupted:\n{e}");
                    TryRunPipelineCleanup("set multi-scan failure state", () =>
                    {
                        settings.CurrentTaskName = "ATC_TaskFailed".Translate();
                        AutoTranslatorSettings.ShowFinishPopup = true;
                    });
                }
                finally
                {
                    TryRunPipelineCleanup("save multi-scan progress", () => TranslationUnresolvedManager.SaveRunProgress());
                    await TryRunPipelineCleanupAsync(
                        "end multi-scan policy-agent run",
                        () => EndTranslationPolicyAgentRunAsync(policyAgentRunId, usageRunCompleted));
                    TryRunPipelineCleanup("end multi-scan usage run", () => EndTranslationUsageRun(usageRunStarted, usageRunCompleted));
                    TryRunPipelineCleanup("clear multi-scan translation database", () => ClearGlobalTranslationDatabase());
                    if (AutoTranslatorSettings.IsCancellationRequested)
                    {
                        TryRunPipelineCleanup("reset multi-scan cancellation state", () =>
                        {
                            settings.CurrentTaskName = "";
                            settings.CurrentProgress = 0f;
                            settings.SubTaskName = "";
                            settings.SubProgress = 0f;
                        });
                    }
                    await AutoTranslatorSettings.CompleteTranslationPipelineAsync();
                }
            });
        }

        private static async Task<bool> TryRunHardcodedUiAutomaticPipelineAsync(
            IEnumerable<ModMetaData> mods)
        {
            AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
            if (settings == null ||
                !settings.EnableHardcodedUiPrototype ||
                AutoTranslatorSettings.IsCancellationRequested ||
                TranslationUsageCoordinator.IsPausedByBudget)
                return true;

            try
            {
                await HardcodedUiAutomaticPipeline.RunAsync(
                    mods,
                    settings.EnableTranslationPolicyAgent);
                return !AutoTranslatorSettings.IsCancellationRequested;
            }
            catch (Exception ex)
            {
                AutoTranslatorSettings.AddErrorLog(
                    "ATC_HardcodedUi_AutoFailed".Translate(ex.Message));
                Verse.Log.Error(
                    "[AutoTranslationCore] Automatic DLL UI pipeline failed; " +
                    "the XML translation run will remain usable.\n" + ex);
                return false;
            }
        }

        private static void TryRunPipelineCleanup(string operation, Action action)
        {
            if (action == null) return;
            try
            {
                action();
            }
            catch (Exception exception)
            {
                string detail = exception.ToString();
                try
                {
                    AutoTranslatorSettings.AddLog($"[CLEANUP ERROR] {operation}: {exception.Message}");
                }
                catch
                {
                }
                TryLogPipelineError($"[AutoTranslationCore] Pipeline cleanup failed ({operation}):\n{detail}");
            }
        }

        private static async Task TryRunPipelineCleanupAsync(string operation, Func<Task> action)
        {
            try
            {
                if (action != null) await action();
            }
            catch (Exception ex)
            {
                TryLogPipelineError("[AutoTranslationCore] Pipeline cleanup failed (" +
                    operation + "): " + ex);
            }
        }

        private static void HandlePackageScanException(ModMetaData mod, Exception exception)
        {
            try
            {
                string packageId = GetModPackageId(mod);
                string modName = GetModName(mod, packageId);
                string targetLanguage = GetTargetLanguageName();
                string sourcePath = GetModRootPath(mod);
                string detail = GetExceptionDetail(exception);

                TryRunPipelineCleanup(
                    "mark package scan incomplete",
                    () => TranslationUnresolvedManager.MarkPackageScanIncomplete(packageId, targetLanguage));
                TryRunPipelineCleanup(
                    "record package failure",
                    () => RecordPackageProcessingFailure(
                        packageId,
                        modName,
                        targetLanguage,
                        sourcePath,
                        detail));
                TryRunPipelineCleanup(
                    "complete failed package scan",
                    () => TranslationUnresolvedManager.CompletePackageScan(packageId, targetLanguage));

                TryAddPipelineLog($"[PACKAGE ERROR] {modName}: {GetExceptionMessage(exception)}");
                TryAddPipelineLog("[PACKAGE ERROR DETAIL] " + detail);
                TryLogPipelineError(
                    $"[AutoTranslationCore] Package translation failed for {modName} ({packageId}); continuing with the remaining queue.\n{detail}");
            }
            catch (Exception handlerException)
            {
                TryAddPipelineLog("[PACKAGE ERROR] Failure handling also failed: " +
                    GetExceptionMessage(handlerException));
                TryLogPipelineError(
                    "[AutoTranslationCore] Package failure handler failed; the remaining queue will still continue.\n" +
                    GetExceptionDetail(handlerException));
            }
        }

        private static void RecordPackageProcessingFailure(
            string packageId,
            string modName,
            string targetLanguage,
            string sourcePath,
            string detail)
        {
            TranslationUnresolvedManager.RecordFailure(new TranslationUnresolvedEntry
            {
                TargetLanguage = targetLanguage ?? string.Empty,
                PackageId = packageId ?? string.Empty,
                ModName = modName ?? string.Empty,
                Bucket = "Package",
                DefType = string.Empty,
                Key = "__ATC_PACKAGE_FAILURE__",
                SourceText = sourcePath ?? string.Empty,
                SourceFile = sourcePath ?? string.Empty,
                TargetFile = string.Empty,
                Reason = TranslationUnresolvedReasons.SourceFailure,
                Detail = detail ?? string.Empty,
                Attempts = 1,
                State = TranslationUnresolvedStates.Pending
            });
        }

        private static string GetModRootPath(ModMetaData mod)
        {
            try
            {
                return mod != null && mod.RootDir != null
                    ? mod.RootDir.FullName
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetModPackageId(ModMetaData mod)
        {
            try
            {
                return mod != null ? mod.PackageId ?? string.Empty : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetModName(ModMetaData mod, string fallback)
        {
            try
            {
                return mod != null && !string.IsNullOrWhiteSpace(mod.Name)
                    ? mod.Name
                    : fallback ?? string.Empty;
            }
            catch
            {
                return fallback ?? string.Empty;
            }
        }

        private static string GetTargetLanguageName()
        {
            try
            {
                return AutoTranslatorMod.Settings != null
                    ? AutoTranslatorMod.Settings.TargetLang.ToString()
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetExceptionMessage(Exception exception)
        {
            try
            {
                return exception != null && !string.IsNullOrWhiteSpace(exception.Message)
                    ? exception.Message
                    : "Unhandled package translation failure.";
            }
            catch
            {
                return "Unhandled package translation failure.";
            }
        }

        private static string GetExceptionDetail(Exception exception)
        {
            try
            {
                return exception != null
                    ? exception.ToString()
                    : "Unhandled package translation failure.";
            }
            catch
            {
                return GetExceptionMessage(exception);
            }
        }

        private static void TryAddPipelineLog(string message)
        {
            try
            {
                if (!string.IsNullOrEmpty(message)) AutoTranslatorSettings.AddLog(message);
            }
            catch (Exception exception)
            {
                TryLogPipelineError("[AutoTranslationCore] Pipeline log failed: " + exception);
            }
        }

        private static void TryAddLocalizedTaskError(Exception exception)
        {
            try
            {
                TryAddPipelineLog(AutoTranslatorAPI.TranslateText(
                    "ATC_Log_TaskError",
                    exception != null ? exception.Message : string.Empty));
            }
            catch (Exception translationException)
            {
                TryAddPipelineLog("[ERROR] Translation task failed: " +
                    (exception != null ? exception.Message : "unknown error"));
                TryLogPipelineError(
                    "[AutoTranslationCore] Could not localize task error: " + translationException);
            }
        }

        private static void TryLogPipelineError(string message)
        {
            try
            {
                if (!string.IsNullOrEmpty(message)) Log.Error(message);
            }
            catch
            {
            }
        }

        // 這個方法負責判斷 ShouldSkipNative目標翻譯 條件是否成立。
        // EN: This method checks should skip native target translation.
        private static bool ShouldSkipTranslationPatchMod(ModMetaData mod)
        {
            if (mod == null) return true;
            if (IsTranslationPatchMod(mod)) return true;
            return false;
        }
    }
}
