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
            if (targetMod == null) return;
            if (AutoTranslatorMod.Settings != null && AutoTranslatorMod.Settings.IsTranslationBlacklisted(targetMod.PackageId))
            {
                Messages.Message("ATC_Blacklist_TranslationSkipped".Translate(targetMod.Name), MessageTypeDefOf.NeutralEvent, false);
                return;
            }

            AutoTranslatorMod.Settings.SessionCharCount = 0;
            ResetValidationStats();

            AutoTranslatorSettings.IsRunning = true;

            var settings = AutoTranslatorMod.Settings;
            settings.CurrentProgress = 0f;
            settings.CurrentTaskName = $"Translating: {targetMod.Name}";
            AutoTranslatorSettings.AddLog("🚀 " + AutoTranslatorAPI.TranslateText("ATC_Log_StartSingleMod", targetMod.Name));


            var activeMods = ModLister.AllInstalledMods.Where(m => m.Active && !BlacklistedModules.Contains(m.PackageId.ToLower())).ToList();


            Task.Run(async () =>
            {
                try
                {
                    EnsurePackInitialized(runFullMaintenance: false);
                    if (AutoTranslatorSettings.IsCancellationRequested) return;

                    if (ShouldSkipTranslationPatchMod(targetMod))
                    {
                        ModUpdateDetector.MarkModAsTranslated(targetMod.PackageId, targetMod.RootDir.FullName, false);
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
                        AutoTranslatorSettings.IsRunning = false;
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
                            }
                        }
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

                    if (!AutoTranslatorSettings.IsCancellationRequested)
                    {
                        settings.CurrentTaskName = "ATC_TaskDone".Translate();

                        if (!skippedNoSource)
                        {
                            ModUpdateDetector.MarkModAsTranslated(targetMod.PackageId, targetMod.RootDir.FullName, false);
                        }
                        settings.CurrentProgress = 1f;
                        AutoTranslatorSettings.AddLog("✨ " + "ATC_Log_SingleModDone".Translate());
                        LogValidationSummary();


                        RequestMemoryDrop();


                        AutoTranslatorSettings.ShowFinishPopup = true;
                        ModUpdateDetector.ClearStatusCache();
                        TranslationWorkbenchTab.RequestRefresh();
                    }
                }
                catch (Exception e)
                {
                    AutoTranslatorSettings.AddLog("❌ " + AutoTranslatorAPI.TranslateText("ATC_Log_TaskError", e.Message));
                    Log.Error($"[AutoTranslationCore] Single translation task interrupted: {e.Message}");
                }
                finally
                {
                    ClearGlobalTranslationDatabase();
                    AutoTranslatorSettings.IsRunning = false;
                }
            });
        }

        public static void StartPureAiRebuildForUpload(ModMetaData targetMod)
        {
            if (targetMod == null) return;

            AutoTranslatorMod.Settings.SessionCharCount = 0;
            ResetValidationStats();
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
                try
                {
                    EnsurePackInitialized(runFullMaintenance: false);
                    if (AutoTranslatorSettings.IsCancellationRequested) return;

                    if (ShouldSkipTranslationPatchMod(targetMod))
                    {
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
                            }
                        }

                        if (AutoTranslatorSettings.IsCancellationRequested) return;
                        if (hasDefs || hasLang)
                        {
                            settings.CurrentProgress = 0.55f;
                            settings.SubTaskName = "ATC_SubTask_TranslatingDef".Translate();
                            AutoTranslatorSettings.AddLog("📦 " + "ATC_Log_DefScan".Translate());
                            aiTranslatedCount += await ProcessModDefInjected(targetMod, langRoots, defsRoots, TranslationOutputMode.PureAiWorkspace);
                        }
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

                        settings.CurrentTaskName = "ATC_TaskDone".Translate();
                        settings.CurrentProgress = 1f;
                        settings.SubTaskName = "";
                        settings.SubProgress = 1f;
                        LogValidationSummary();
                        ModUpdateDetector.ClearStatusCache();
                        TranslationWorkbenchTab.RequestRefresh();
                    }
                }
                catch (Exception e)
                {
                    AutoTranslatorSettings.AddLog("❌ " + AutoTranslatorAPI.TranslateText("ATC_Log_TaskError", e.Message));
                    Log.Error($"[AutoTranslationCore] Pure AI rebuild task interrupted: {e.Message}");
                }
                finally
                {
                    AutoTranslatorSettings.IsRunning = false;
                    if (AutoTranslatorSettings.IsCancellationRequested)
                    {
                        settings.CurrentTaskName = "";
                        settings.CurrentProgress = 0f;
                        settings.SubTaskName = "";
                        settings.SubProgress = 0f;
                    }
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
            AutoTranslatorMod.Settings.SessionCharCount = 0;
            ResetValidationStats();
            AutoTranslatorSettings.IsRunning = true;

            var settings = AutoTranslatorMod.Settings;

            var mods = ModLister.AllInstalledMods.Where(m =>
                            !BlacklistedModules.Contains(m.PackageId.ToLower()) &&
                            !settings.IsTranslationBlacklisted(m.PackageId) &&
                            !IsOfficialBaseGameOrDlcPackage(m.PackageId) &&
                            (!settings.OnlyScanActiveMods || m.Active)).ToList();
            AutoTranslatorSettings.AddLog("🌐 " + AutoTranslatorAPI.TranslateText("ATC_Log_StartScan", mods.Count));


            Task.Run(async () =>
            {
                try
                {
                    EnsurePackInitialized(runFullMaintenance: false);
                    if (AutoTranslatorSettings.IsCancellationRequested) return;


                    settings.SubTaskName = "ATC_SubTask_TestingAPI".Translate();
                    AutoTranslatorSettings.AddLog("🔌 " + "ATC_Log_PreflightCheck".Translate());

                    bool isApiAlive = await AutoTranslatorAPI.TestConnectionAsync();
                    if (!isApiAlive)
                    {
                        AutoTranslatorSettings.AddErrorLog("❌ " + "ATC_LogError_ApiDeadAbort".Translate());
                        settings.CurrentTaskName = "ATC_TaskFailed".Translate();
                        AutoTranslatorSettings.IsRunning = false;
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


                        if (IsTranslationPatchMod(mod))
                        {
                            continue;
                        }

                        if (ShouldSkipTranslationPatchMod(mod))
                        {
                            ModUpdateDetector.MarkModAsTranslated(mod.PackageId, mod.RootDir.FullName, false);
                            continue;
                        }

                        if (AutoTranslatorSettings.IsCancellationRequested) break;
                        if (AutoTranslatorSettings.IsSkipCurrentRequested)
                        {
                            AutoTranslatorSettings.AddLog("⏭️ " + AutoTranslatorAPI.TranslateText("ATC_Log_SkippedMod", mod.Name));
                            AutoTranslatorSettings.IsSkipCurrentRequested = false;
                            continue;
                        }

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
                            continue;
                        }

                        if (langRoots.Count > 0)
                        {
                            foreach (var langRoot in langRoots)
                            {
                                settings.SubTaskName = "ATC_SubTask_TranslatingKeyed".Translate();
                                aiTranslatedCount += await ProcessModKeyedSources(mod, langRoot);
                            }
                        }

                        if (AutoTranslatorSettings.IsCancellationRequested) break;
                        if (AutoTranslatorSettings.IsSkipCurrentRequested) { AutoTranslatorSettings.IsSkipCurrentRequested = false; continue; }

                        if (defsRoots.Count > 0 || langRoots.Count > 0)
                        {
                            settings.SubTaskName = "ATC_SubTask_TranslatingDef".Translate();
                            aiTranslatedCount += await ProcessModDefInjected(mod, langRoots, defsRoots);
                        }


                        if (!AutoTranslatorSettings.IsSkipCurrentRequested && !AutoTranslatorSettings.IsCancellationRequested)
                        {
                            ModUpdateDetector.MarkModAsTranslated(mod.PackageId, mod.RootDir.FullName, false);


                            if (aiTranslatedCount > 0)
                            {
                                UpdateLocalModMeta(mod.PackageId, GetFolderNameByLanguage(settings.TargetLang), aiTranslatedCount);
                            }
                        }

                        if (AutoTranslatorSettings.IsSkipCurrentRequested)
                        {
                            AutoTranslatorSettings.AddLog("⏭️ " + AutoTranslatorAPI.TranslateText("ATC_Log_SkippedMod", mod.Name));
                            AutoTranslatorSettings.IsSkipCurrentRequested = false;
                        }
                    }

                    if (!AutoTranslatorSettings.IsCancellationRequested)
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
                    }
                }
                catch (Exception e)
                {
                    AutoTranslatorSettings.AddLog(AutoTranslatorAPI.TranslateText("ATC_Log_TaskError", e.Message));
                    AutoTranslatorSettings.AddLog($"[CRITICAL ERROR] {e.Message}");
                }
                finally
                {
                    ClearGlobalTranslationDatabase();
                    AutoTranslatorSettings.IsRunning = false;
                    if (AutoTranslatorSettings.IsCancellationRequested)
                    {
                        settings.CurrentTaskName = "";
                        settings.CurrentProgress = 0f;
                        settings.SubTaskName = "";
                        settings.SubProgress = 0f;
                    }
                }
            });
        }


        // 這個方法負責啟動 Multi掃描 流程。
        // EN: This method starts multi scan.
        public static void StartMultiScan(List<ModMetaData> targetMods, bool includeOfficialGamePackages = false)
        {
            targetMods = (targetMods ?? new List<ModMetaData>())
                .Where(mod => mod != null &&
                              (AutoTranslatorMod.Settings == null || !AutoTranslatorMod.Settings.IsTranslationBlacklisted(mod.PackageId)) &&
                              (includeOfficialGamePackages || !IsOfficialBaseGameOrDlcPackage(mod.PackageId)))
                .ToList();
            if (targetMods.Count == 0) return;

            AutoTranslatorMod.Settings.SessionCharCount = 0;
            ResetValidationStats();
            AutoTranslatorSettings.IsRunning = true;


            var settings = AutoTranslatorMod.Settings;
            int total = targetMods.Count;
            AutoTranslatorSettings.AddLog("🚀 " + "ATC_Log_MultiScanStart".Translate(total));

            var activeMods = ModLister.AllInstalledMods.Where(m => m.Active && !BlacklistedModules.Contains(m.PackageId.ToLower())).ToList();

            Task.Run(async () =>
            {
                try
                {
                    EnsurePackInitialized(runFullMaintenance: false);
                    if (AutoTranslatorSettings.IsCancellationRequested) return;


                    settings.SubTaskName = "ATC_SubTask_TestingAPI".Translate();
                    AutoTranslatorSettings.AddLog("🔌 " + "ATC_Log_PreflightCheck".Translate());

                    bool isApiAlive = await AutoTranslatorAPI.TestConnectionAsync();
                    if (!isApiAlive)
                    {
                        AutoTranslatorSettings.AddErrorLog("❌ " + "ATC_LogError_ApiDeadAbort".Translate());
                        settings.CurrentTaskName = "ATC_TaskFailed".Translate();
                        AutoTranslatorSettings.IsRunning = false;
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
                        if (ShouldSkipTranslationPatchMod(mod))
                        {
                            ModUpdateDetector.MarkModAsTranslated(mod.PackageId, mod.RootDir.FullName, false);
                            continue;
                        }

                        if (AutoTranslatorSettings.IsCancellationRequested) break;
                        if (AutoTranslatorSettings.IsSkipCurrentRequested)
                        {
                            AutoTranslatorSettings.AddLog("⏭️ " + AutoTranslatorAPI.TranslateText("ATC_Log_SkippedMod", mod.Name));
                            AutoTranslatorSettings.IsSkipCurrentRequested = false;
                            continue;
                        }

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
                            continue;
                        }

                        if (langRoots.Count > 0)
                        {
                            foreach (var langRoot in langRoots)
                            {
                                settings.SubTaskName = "ATC_SubTask_TranslatingKeyed".Translate();
                                aiTranslatedCount += await ProcessModKeyedSources(mod, langRoot);
                            }
                        }

                        if (AutoTranslatorSettings.IsCancellationRequested) break;
                        if (AutoTranslatorSettings.IsSkipCurrentRequested) { AutoTranslatorSettings.IsSkipCurrentRequested = false; continue; }

                        if (defsRoots.Count > 0 || langRoots.Count > 0)
                        {
                            settings.SubTaskName = "ATC_SubTask_TranslatingDef".Translate();
                            aiTranslatedCount += await ProcessModDefInjected(mod, langRoots, defsRoots);
                        }


                        if (!AutoTranslatorSettings.IsSkipCurrentRequested && !AutoTranslatorSettings.IsCancellationRequested)
                        {
                            ModUpdateDetector.MarkModAsTranslated(mod.PackageId, mod.RootDir.FullName, false);
                        }


                        if (AutoTranslatorSettings.IsSkipCurrentRequested)
                        {
                            AutoTranslatorSettings.AddLog("⏭️ " + AutoTranslatorAPI.TranslateText("ATC_Log_SkippedMod", mod.Name));
                            AutoTranslatorSettings.IsSkipCurrentRequested = false;
                        }
                    }

                    if (!AutoTranslatorSettings.IsCancellationRequested)
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
                    }
                }
                catch (Exception e)
                {
                    AutoTranslatorSettings.AddLog(AutoTranslatorAPI.TranslateText("ATC_Log_TaskError", e.Message));
                    AutoTranslatorSettings.AddLog($"[CRITICAL ERROR] {e.Message}");
                }
                finally
                {
                    ClearGlobalTranslationDatabase();
                    AutoTranslatorSettings.IsRunning = false;
                    if (AutoTranslatorSettings.IsCancellationRequested)
                    {
                        settings.CurrentTaskName = "";
                        settings.CurrentProgress = 0f;
                        settings.SubTaskName = "";
                        settings.SubProgress = 0f;
                    }
                }
            });
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
