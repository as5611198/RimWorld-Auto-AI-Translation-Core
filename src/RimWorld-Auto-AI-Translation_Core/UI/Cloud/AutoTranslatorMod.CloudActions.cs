using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責雲端翻譯服務的 自動翻譯器模組雲端Actions，處理 registry、上傳、下載或刪除流程。
// EN: This file contains auto translator mod cloud actions support code.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器模組 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorMod.
    public partial class AutoTranslatorMod : Mod
    {
        private enum CloudBatchUploadSource
        {
            Workspace,
            LocalPack
        }

        private enum CloudBatchDownloadMode
        {
            Official,
            Manual,
            AI,
            Best
        }

        private sealed class BatchUploadSourceItem
        {
            public string PackageId;
            public string SourceDir;
            public string DisplayName;
            public string ModName;
            public string TaskName;
            public bool CopyBackToLivePack;
            public ModMetaData Mod;
        }

        private sealed class BatchAiRebuildCandidate
        {
            public string PackageId;
            public string DisplayName;
            public ModMetaData Mod;
        }

        private sealed class CloudLocalModSnapshot
        {
            public string PackageId;
            public string DisplayName;
            public bool ShouldSkipCloudSharing;
            public ModMetaData Mod;
        }

        private sealed class BatchDownloadItem
        {
            public string PackageId;
            public string DisplayName;
            public CloudModRecord Record;
        }

        private sealed class BatchDownloadPreparationResult
        {
            public List<BatchDownloadItem> Items = new List<BatchDownloadItem>();
            public string Error;
        }

        private sealed class BatchDownloadExecutionResult
        {
            public BatchDownloadItem Item;
            public bool Success;
        }

        private sealed class BatchUploadPreparationResult
        {
            public List<BatchUploadSourceItem> UploadSources = new List<BatchUploadSourceItem>();
            public int SkippedPatchModCount;
            public string Error;
        }

        private sealed class BatchUploadPackagePrefix
        {
            public string PackageId;
            public string Prefix;
        }

        // 這個方法負責判斷 ShouldSkip雲端Sharing模組 條件是否成立。
        // EN: This method checks should skip cloud sharing mod.
        internal static bool ShouldSkipCloudSharingMod(ModMetaData mod, string packageId = null, string displayName = null)
        {
            if (mod != null && AutoTranslatorScanner.IsTranslationPatchMod(mod)) return true;

            string pid = (packageId ?? (mod != null ? mod.PackageId : "") ?? "").Trim().ToLowerInvariant();
            string name = (displayName ?? (mod != null ? mod.Name : "") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(pid) && string.IsNullOrEmpty(name)) return false;

            string[] nameKeywords =
            {
                "translation", "translations", "localization", "localisation", "language pack", "l10n",
                "chinese translation", "traditional chinese", "simplified chinese",
                "\u6f22\u5316", "\u6c49\u5316", "\u4e2d\u6587", "\u7e41\u9ad4", "\u7e41\u4f53", "\u7c21\u9ad4", "\u7b80\u4f53", "\u7ffb\u8b6f", "\u7ffb\u8bd1",
                "\u65e5\u672c\u8a9e\u5316", "\u65e5\u672c\u8a9e", "\u7ffb\u8a33",
                "\ud55c\uad6d\uc5b4", "\ubc88\uc5ed",
                "\u043f\u0435\u0440\u0435\u0432\u043e\u0434", "\u0440\u0443\u0441\u0438\u0444\u0438\u043a\u0430\u0442\u043e\u0440",
                "\u0443\u043a\u0440\u0430\u0457\u043d\u0441\u044c\u043a\u0430", "traduction", "traduccion", "traducci\u00f3n",
                "traduzione", "traducao", "tradu\u00e7\u00e3o", "ubersetzung", "\u00fcbersetzung", "tlumaczenie", "t\u0142umaczenie"
            };
            foreach (string keyword in nameKeywords)
            {
                if (!string.IsNullOrEmpty(keyword) && name.Contains(keyword)) return true;
            }

            string[] pidMarkers =
            {
                ".zh", "_zh", "-zh", "zh-pack",
                ".zhtc", "_zhtc", "-zhtc", ".zhtw", "_zhtw", "-zhtw",
                ".zhcn", "_zhcn", "-zhcn", ".zh-cn", "_zh-cn", "-zh-cn", ".zh-tw", "_zh-tw", "-zh-tw",
                ".cn", "_cn", "-cn", ".tw", "_tw", "-tw",
                ".translation", "_translation", "-translation", ".translations", "_translations", "-translations",
                ".localization", "_localization", "-localization", ".localisation", "_localisation", "-localisation",
                ".language", "_language", "-language", ".l10n", "_l10n", "-l10n"
            };
            foreach (string marker in pidMarkers)
            {
                if (pid.EndsWith(marker) || pid.Contains(marker + ".") || pid.Contains(marker + "_") || pid.Contains(marker + "-")) return true;
            }

            return pid.EndsWith("zh") || pid.EndsWith("zhtw") || pid.EndsWith("zhcn");
        }


        // 這個方法負責執行 Batch下載 動作。
        // EN: This method executes batch download.
        private void ExecuteBatchDownload(string targetType)
        {
            ATC_Dispatcher.EnsureAlive();
            QueueBatchDownloadPreparation(targetType);
        }

        // 這個方法負責執行 Batch上傳 動作。
        // EN: This method executes batch upload.
        private bool QueueBatchDownloadPreparation(string targetType)
        {
            CloudBatchDownloadMode mode = ParseBatchDownloadMode(targetType);
            string targetLangStr = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.CloudTargetLang);
            List<CloudLocalModSnapshot> localMods = Verse.ModLister.AllInstalledMods
                .Where(m => m.Active &&
                            !ShouldSkipCloudSharingMod(m) &&
                            !AutoTranslatorMod.Settings.IsCloudDownloadBlacklisted(m.PackageId) &&
                            !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(m.PackageId))
                .Select(m => new CloudLocalModSnapshot
                {
                    PackageId = m.PackageId,
                    DisplayName = m.Name
                })
                .ToList();
            List<CloudModRecord> registrySnapshot = AutoTranslatorSettings.CloudRegistry
                .Where(c => IsRecordForTargetLanguage(c, targetLangStr) && CloudRecordMatchesBatchMode(c, mode))
                .ToList();
            Dictionary<string, CloudModRecord> selectedSnapshot = AutoTranslatorSettings.SelectedCloudVersion
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            AutoTranslatorSettings.IsRunning = true;
            AutoTranslatorMod.Settings.CurrentTaskName = "ATC_Cloud_PreparingBatchDownload".Translate().ToString();
            AutoTranslatorMod.Settings.CurrentProgress = 0f;

            System.Threading.Tasks.Task.Run(() =>
            {
                BatchDownloadPreparationResult result = PrepareBatchDownload(localMods, registrySnapshot, selectedSnapshot, targetLangStr, mode);
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    AutoTranslatorSettings.IsRunning = false;
                    AutoTranslatorMod.Settings.CurrentTaskName = "";
                    AutoTranslatorMod.Settings.CurrentProgress = 0f;

                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        Verse.Messages.Message(result.Error, RimWorld.MessageTypeDefOf.RejectInput, false);
                        return;
                    }

                    if (result.Items.Count == 0)
                    {
                        Verse.Messages.Message("ATC_Msg_BatchNoMods".Translate(), RimWorld.MessageTypeDefOf.RejectInput, false);
                        return;
                    }

                    Verse.Messages.Message("ATC_Msg_BatchStart".Translate(result.Items.Count), RimWorld.MessageTypeDefOf.NeutralEvent, false);
                    StartPreparedBatchDownload(result.Items, targetLangStr);
                });
            });

            return true;
        }

        private static BatchDownloadPreparationResult PrepareBatchDownload(
            List<CloudLocalModSnapshot> localMods,
            List<CloudModRecord> registrySnapshot,
            Dictionary<string, CloudModRecord> selectedSnapshot,
            string targetLangStr,
            CloudBatchDownloadMode mode)
        {
            BatchDownloadPreparationResult result = new BatchDownloadPreparationResult();
            try
            {
                Dictionary<string, CloudLocalModSnapshot> localModsByPackage = (localMods ?? new List<CloudLocalModSnapshot>())
                    .Where(m => !string.IsNullOrWhiteSpace(m.PackageId))
                    .GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                Dictionary<string, CloudModRecord> recordsByPackage = (registrySnapshot ?? new List<CloudModRecord>())
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.PackageId))
                    .GroupBy(c => c.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                        {
                            if (selectedSnapshot != null &&
                                mode != CloudBatchDownloadMode.Best &&
                                selectedSnapshot.TryGetValue(g.Key, out CloudModRecord selected) &&
                                selected != null &&
                                IsRecordForTargetLanguage(selected, targetLangStr) &&
                                CloudRecordMatchesBatchMode(selected, mode))
                            {
                                return selected;
                            }

                            return SelectBatchDownloadRecord(g, mode);
                        },
                        StringComparer.OrdinalIgnoreCase);

                foreach (CloudModRecord record in recordsByPackage.Values)
                {
                    if (record == null || string.IsNullOrWhiteSpace(record.PackageId)) continue;
                    if (AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(record.PackageId)) continue;
                    if (AutoTranslatorMod.Settings != null && AutoTranslatorMod.Settings.IsCloudDownloadBlacklisted(record.PackageId)) continue;
                    if (!localModsByPackage.TryGetValue(record.PackageId, out CloudLocalModSnapshot mod)) continue;
                    result.Items.Add(new BatchDownloadItem
                    {
                        PackageId = record.PackageId,
                        DisplayName = !string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.DisplayName : record.PackageId,
                        Record = record
                    });
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Verse.Log.Warning($"[AutoTranslationCore] Batch download preparation failed: {ex}");
            }

            return result;
        }

        private static CloudBatchDownloadMode ParseBatchDownloadMode(string targetType)
        {
            if (string.Equals(targetType, "Official_Group", StringComparison.OrdinalIgnoreCase)) return CloudBatchDownloadMode.Official;
            if (string.Equals(targetType, "Manual", StringComparison.OrdinalIgnoreCase)) return CloudBatchDownloadMode.Manual;
            if (string.Equals(targetType, "Best", StringComparison.OrdinalIgnoreCase)) return CloudBatchDownloadMode.Best;
            return CloudBatchDownloadMode.AI;
        }

        private static bool IsRecordForTargetLanguage(CloudModRecord record, string targetLangStr)
        {
            return record != null &&
                   !string.IsNullOrWhiteSpace(record.PackageId) &&
                   string.Equals(record.Language, targetLangStr, StringComparison.OrdinalIgnoreCase);
        }

        private static bool CloudRecordMatchesBatchMode(CloudModRecord record, CloudBatchDownloadMode mode)
        {
            if (record == null) return false;

            switch (mode)
            {
                case CloudBatchDownloadMode.Official:
                    return IsOfficialCloudRecord(record);
                case CloudBatchDownloadMode.Manual:
                    return string.Equals(record.TranslationType, "Manual", StringComparison.OrdinalIgnoreCase);
                case CloudBatchDownloadMode.Best:
                    return GetBatchRecordPriority(record) > 0;
                case CloudBatchDownloadMode.AI:
                default:
                    return string.Equals(record.TranslationType, "AI_Auto", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool IsOfficialCloudRecord(CloudModRecord record)
        {
            return record != null &&
                   (record.IsVerified || string.Equals(record.TranslationType, "Official_Group", StringComparison.OrdinalIgnoreCase));
        }

        private static CloudModRecord SelectBatchDownloadRecord(IEnumerable<CloudModRecord> records, CloudBatchDownloadMode mode)
        {
            IEnumerable<CloudModRecord> candidates = records ?? Enumerable.Empty<CloudModRecord>();
            if (mode == CloudBatchDownloadMode.Best)
            {
                return candidates
                    .OrderByDescending(GetBatchRecordPriority)
                    .ThenByDescending(GetBatchRecordUpdatedAt)
                    .FirstOrDefault();
            }

            return candidates
                .OrderByDescending(GetBatchRecordUpdatedAt)
                .FirstOrDefault();
        }

        private static int GetBatchRecordPriority(CloudModRecord record)
        {
            if (IsOfficialCloudRecord(record)) return 3;
            if (record != null && string.Equals(record.TranslationType, "Manual", StringComparison.OrdinalIgnoreCase)) return 2;
            if (record != null && string.Equals(record.TranslationType, "AI_Auto", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        private static DateTime GetBatchRecordUpdatedAt(CloudModRecord record)
        {
            if (record == null) return DateTime.MinValue;
            if (record.LastUpdated != DateTime.MinValue) return record.LastUpdated;
            return record.TranslationDate;
        }

        private static void StartPreparedBatchDownload(List<BatchDownloadItem> modsToDownload, string targetLangStr)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                int successCount = 0;
                int failCount = 0;
                List<string> failedMods = new List<string>();
                List<string> repairedPackages = new List<string>();
                int totalCount = modsToDownload != null ? modsToDownload.Count : 0;
                HashSet<string> pendingPreclearedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> packagesWithPreviousTranslations = ModUpdateDetector.GetPackageIdsWithLocalTranslationFilesForKnownPackages(
                    (modsToDownload ?? new List<BatchDownloadItem>()).Select(item => item.PackageId),
                    AutoTranslatorMod.Settings.CloudTargetLang);

                AutoTranslatorSettings.IsRunning = true;

                try
                {
                    const int clearChunkSize = 32;
                    int completedCount = 0;
                    int maxParallel = Math.Max(1, Math.Min(4, AutoTranslatorMod.Settings.CloudBatchDownloadConcurrency));
                    using (var semaphore = new System.Threading.SemaphoreSlim(maxParallel, maxParallel))
                    {
                        for (int chunkStart = 0; chunkStart < totalCount; chunkStart += clearChunkSize)
                        {
                            List<AutoTranslatorScanner.LocalTranslationDeleteTarget> clearTargets = modsToDownload
                                .Skip(chunkStart)
                                .Take(clearChunkSize)
                                .Select(item => new AutoTranslatorScanner.LocalTranslationDeleteTarget
                                {
                                    PackageId = item.PackageId,
                                    ModName = item.DisplayName
                                })
                                .ToList();
                            AutoTranslatorScanner.DeleteLocalTranslationFiles(
                                clearTargets,
                                createBackup: true,
                                requestRuntimeRefresh: false,
                                logResult: false);
                            foreach (AutoTranslatorScanner.LocalTranslationDeleteTarget target in clearTargets)
                            {
                                pendingPreclearedPackages.Add(target.PackageId);
                            }

                            List<BatchDownloadItem> chunkItems = modsToDownload
                                .Skip(chunkStart)
                                .Take(clearChunkSize)
                                .ToList();
                            Task<BatchDownloadExecutionResult>[] downloads = chunkItems
                                .Select(item => DownloadPreparedBatchItemAsync(item, targetLangStr, semaphore))
                                .ToArray();
                            BatchDownloadExecutionResult[] chunkResults = await Task.WhenAll(downloads);

                            foreach (BatchDownloadExecutionResult download in chunkResults)
                            {
                                BatchDownloadItem mod = download.Item;
                                completedCount++;
                                UpdateBatchTaskProgress(
                                    "ATC_Cloud_Downloading",
                                    mod.DisplayName,
                                    totalCount > 0 ? (float)completedCount / totalCount : 0f);
                                if (download.Success)
                                {
                                    pendingPreclearedPackages.Remove(mod.PackageId);
                                    successCount++;
                                    repairedPackages.Add(mod.PackageId);
                                }
                                else
                                {
                                    RestorePreclearedBatchPackage(mod.PackageId, packagesWithPreviousTranslations);
                                    pendingPreclearedPackages.Remove(mod.PackageId);
                                    failCount++;
                                    failedMods.Add(mod.DisplayName);
                                }
                            }
                        }
                    }

                    if (successCount > 0)
                    {
                        UpdateBatchTaskProgress("ATC_Cloud_RepairingBatch", successCount.ToString(), 0.98f);
                        AutoTranslatorLegacyRepairer.RepairPackages(repairedPackages, targetLangStr, requestMemoryDrop: false);
                    }

                    if (successCount > 0 || failCount > 0)
                    {
                        AutoTranslatorScanner.RequestMemoryDrop();
                    }

                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        AutoTranslatorSettings.IsRunning = false;
                        AutoTranslatorMod.Settings.CurrentTaskName = "";
                        AutoTranslatorMod.Settings.CurrentProgress = 0f;
                        AutoTranslatorSettings.AddLog("? " + "ATC_Log_BatchDownloadSummary".Translate(successCount, failCount, totalCount));
                        if (failedMods.Count > 0)
                        {
                            AutoTranslatorSettings.AddLog("? " + "ATC_Log_BatchDownloadFailedList".Translate(string.Join(", ", failedMods.Take(5).ToArray())));
                        }
                        Verse.Messages.Message("ATC_Msg_BatchSuccess".Translate(successCount, totalCount), RimWorld.MessageTypeDefOf.PositiveEvent, false);
                    });
                }
                catch (Exception ex)
                {
                    foreach (string packageId in pendingPreclearedPackages)
                    {
                        RestorePreclearedBatchPackage(packageId, packagesWithPreviousTranslations);
                    }
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        AutoTranslatorSettings.IsRunning = false;
                        AutoTranslatorMod.Settings.CurrentTaskName = "";
                        AutoTranslatorMod.Settings.CurrentProgress = 0f;
                        AutoTranslatorSettings.AddErrorLog("[Cloud] Batch download failed: " + ex.Message);
                        Verse.Messages.Message("ATC_Msg_DownloadFailed".Translate(ex.Message), RimWorld.MessageTypeDefOf.RejectInput, false);
                    });
                }
            });
        }

        private static async Task<BatchDownloadExecutionResult> DownloadPreparedBatchItemAsync(
            BatchDownloadItem item,
            string targetLangStr,
            System.Threading.SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync();
            try
            {
                var clearTarget = new AutoTranslatorScanner.LocalTranslationDeleteTarget
                {
                    PackageId = item.PackageId,
                    ModName = item.DisplayName
                };
                bool success = await AutoTranslatorCloudClient.DownloadAndInjectAsync(
                    item.PackageId,
                    targetLangStr,
                    item.Record,
                    requestMemoryDrop: false,
                    requestRuntimeRefreshAfterClear: false,
                    clearTarget: clearTarget,
                    clearExistingTranslations: false,
                    restoreBackupOnFailure: false);
                return new BatchDownloadExecutionResult { Item = item, Success = success };
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[AutoTranslationCore] Batch cloud download failed for " + item.PackageId + ": " + ex.Message);
                return new BatchDownloadExecutionResult { Item = item, Success = false };
            }
            finally
            {
                semaphore.Release();
            }
        }

        private static void RestorePreclearedBatchPackage(string packageId, HashSet<string> packagesWithPreviousTranslations)
        {
            if (string.IsNullOrWhiteSpace(packageId) ||
                packagesWithPreviousTranslations == null ||
                !packagesWithPreviousTranslations.Contains(packageId))
            {
                return;
            }

            AutoTranslatorScanner.RestoreLatestBackups(
                new List<AutoTranslatorScanner.LocalTranslationRestoreTarget>
                {
                    new AutoTranslatorScanner.LocalTranslationRestoreTarget { PackageId = packageId }
                });
        }

        private static void UpdateBatchTaskProgress(string translationKey, string argument, float progress)
        {
            string safeArgument = argument ?? "";
            float safeProgress = Mathf.Clamp01(progress);
            ATC_Dispatcher.RunOnMainThread(() =>
            {
                AutoTranslatorMod.Settings.CurrentTaskName = translationKey.Translate(safeArgument).ToString();
                AutoTranslatorMod.Settings.CurrentProgress = safeProgress;
            });
        }

        private void ExecuteBatchUpload(CloudBatchUploadSource source)
        {
            string packPath = AutoTranslatorScanner.GetLocalPackPath();
            string workspaceRoot = System.IO.Path.Combine(packPath, "Upload_Workspace");
            string targetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(Settings.CloudTargetLang);
            string liveLangDir = System.IO.Path.Combine(packPath, "Languages", targetLangFolder);
            string uNickname = Settings.CloudNickname;
            string uToken = Settings.CloudAdminToken;
            string uploadType = NormalizeCloudUploadType(Settings.CloudUploadType, !string.IsNullOrWhiteSpace(uToken));
            string uploadTypeLabel = GetCloudUploadTypeLabel(uploadType);
            string updateLog = (Settings.CloudBatchUploadLog ?? "").Trim();

            Settings.CloudUploadType = uploadType;
            WriteSettings();

            List<CloudLocalModSnapshot> installedModSnapshots = Verse.ModLister.AllInstalledMods
                .Where(m => m != null && !string.IsNullOrEmpty(m.PackageId))
                .Select(m => new CloudLocalModSnapshot
                {
                    PackageId = m.PackageId,
                    DisplayName = m.Name,
                    ShouldSkipCloudSharing = ShouldSkipCloudSharingMod(m),
                    Mod = m
                })
                .ToList();
            if (QueueBatchUploadPreparation(
                source,
                workspaceRoot,
                targetLangFolder,
                liveLangDir,
                uNickname,
                uToken,
                uploadType,
                uploadTypeLabel,
                updateLog,
                installedModSnapshots))
            {
                return;
            }

            int skippedPatchModCount = 0;
            var uploadSources = new List<BatchUploadSourceItem>();

            if (source == CloudBatchUploadSource.Workspace)
            {
                var modDirs = System.IO.Directory.Exists(workspaceRoot)
                    ? System.IO.Directory.GetDirectories(workspaceRoot)
                    .Where(modDir =>
                    {
                        string packageId = System.IO.Path.GetFileName(modDir);
                        var tempMeta = Verse.ModLister.AllInstalledMods.FirstOrDefault(m => string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
                        string displayName = tempMeta != null ? tempMeta.Name : packageId;
                        if (ShouldSkipCloudSharingMod(tempMeta, packageId, displayName))
                        {
                            skippedPatchModCount++;
                            return false;
                        }
                        return true;
                    })
                    .ToArray()
                    : new string[0];

                foreach (string modDir in modDirs)
                {
                    string packageId = System.IO.Path.GetFileName(modDir);
                    string langDir = System.IO.Path.Combine(modDir, targetLangFolder);
                    if (!HasUploadableTranslationFiles(langDir, packageId, true)) continue;
                    var tempMeta = Verse.ModLister.AllInstalledMods.FirstOrDefault(m => string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
                    string displayName = tempMeta != null ? tempMeta.Name : packageId;
                    uploadSources.Add(new BatchUploadSourceItem
                    {
                        PackageId = packageId,
                        SourceDir = langDir,
                        DisplayName = displayName,
                        ModName = displayName,
                        CopyBackToLivePack = true
                    });
                }
            }
            else
            {
                var uploadablePackages = BuildUploadableLocalPackPackageSet(liveLangDir, Verse.ModLister.AllInstalledMods);
                foreach (var mod in Verse.ModLister.AllInstalledMods)
                {
                    if (mod == null || string.IsNullOrEmpty(mod.PackageId)) continue;
                    if (ShouldSkipCloudSharingMod(mod))
                    {
                        skippedPatchModCount++;
                        continue;
                    }
                    if (!uploadablePackages.Contains(mod.PackageId)) continue;

                    uploadSources.Add(new BatchUploadSourceItem
                    {
                        PackageId = mod.PackageId,
                        SourceDir = liveLangDir,
                        DisplayName = mod.Name,
                        ModName = mod.Name,
                        CopyBackToLivePack = false
                    });
                }
            }

            if (skippedPatchModCount > 0)
            {
                Messages.Message("ATC_Msg_CloudBatchPatchModsSkipped".Translate(skippedPatchModCount), MessageTypeDefOf.NeutralEvent, false);
            }
            if (uploadSources.Count == 0)
            {
                string emptyKey = source == CloudBatchUploadSource.Workspace
                    ? "ATC_Msg_WorkspaceEmpty"
                    : "ATC_Msg_BatchUploadPackEmpty";
                Messages.Message(emptyKey.Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            Messages.Message("ATC_Msg_BatchUploadStart".Translate(uploadSources.Count), MessageTypeDefOf.NeutralEvent, false);

            System.Threading.Tasks.Task.Run(async () =>
            {
                int successCount = 0;
                int failCount = 0;
                List<string> failedMods = new List<string>();
                List<BatchAiRebuildCandidate> aiRebuildCandidates = new List<BatchAiRebuildCandidate>();

                AutoTranslatorSettings.IsRunning = true;

                try
                {
                    for (int i = 0; i < uploadSources.Count; i++)
                    {
                        BatchUploadSourceItem item = uploadSources[i];
                        string packageId = item.PackageId;
                        string displayName = !string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : packageId;

                        try
                        {
                            AutoTranslatorMod.Settings.CurrentTaskName = "ATC_Cloud_Uploading".Translate(displayName);
                            AutoTranslatorMod.Settings.CurrentProgress = (float)i / uploadSources.Count;

                            string langDir = item.SourceDir;

                            if (!System.IO.Directory.Exists(langDir))
                            {
                                failCount++;
                                failedMods.Add(displayName);
                                AutoTranslatorSettings.AddLog("⚠️ " + "ATC_Log_BatchUploadMissingFolder".Translate(displayName));
                                continue;
                            }

                            if (!AutoTranslatorScanner.GetXmlFilesForTranslationCache(langDir, System.IO.SearchOption.AllDirectories)
                                    .Any(file => !AutoTranslatorScanner.IsWorkbenchManualExportPath(file)))
                            {
                                failCount++;
                                failedMods.Add(displayName);
                                AutoTranslatorSettings.AddLog("⚠️ " + "ATC_Log_BatchUploadNoXml".Translate(displayName));
                                continue;
                            }

                            string modName = !string.IsNullOrEmpty(item.ModName) ? item.ModName : displayName;

                            AutoTranslatorCloudClient.UploadTranslationResult uploadResult =
                                await AutoTranslatorCloudClient.UploadTranslationWithDetailsAsync(packageId, targetLangFolder, modName, uNickname, uploadType, langDir, uToken, updateLog);
                            bool success = uploadResult != null && uploadResult.Success;

                            if (success)
                            {
                                successCount++;
                                if (!item.CopyBackToLivePack) continue;

                                foreach (string file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(langDir, System.IO.SearchOption.AllDirectories))
                                {
                                    if (AutoTranslatorScanner.IsWorkbenchManualExportPath(file)) continue;
                                    string relPath = file.Substring(langDir.Length).TrimStart('\\', '/');

                                    string justFileName = System.IO.Path.GetFileName(file);
                                    string justFileNameLower = justFileName.ToLower();
                                    string id1 = packageId.ToLower();
                                    string id2 = packageId.Replace(".", "_").ToLower();

                                    if (!justFileNameLower.StartsWith(id1 + "_") && !justFileNameLower.StartsWith(id1 + ".") &&
                                        !justFileNameLower.StartsWith(id2 + "_") && !justFileNameLower.StartsWith(id2 + "."))
                                    {
                                        string dirName = System.IO.Path.GetDirectoryName(relPath);
                                        string newFileName = $"{id2}_{justFileName}";
                                        relPath = string.IsNullOrEmpty(dirName) ? newFileName : System.IO.Path.Combine(dirName, newFileName);
                                    }

                                    string destPath = System.IO.Path.Combine(liveLangDir, relPath);
                                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destPath));
                                    System.IO.File.Copy(file, destPath, true);
                                }
                            }
                            else
                            {
                                failCount++;
                                failedMods.Add(displayName);
                                AutoTranslatorScanner.TranslationUploadProvenanceSummary provenanceSummary =
                                    uploadResult != null ? uploadResult.ProvenanceSummary : null;
                                if (AutoTranslatorScanner.IsAiUploadBlockedByProvenance(provenanceSummary))
                                {
                                    AutoTranslatorSettings.AddLog("⚠️ " + AutoTranslatorScanner.FormatAiUploadNoCleanLog(displayName, provenanceSummary));
                                    if (item.Mod != null && !aiRebuildCandidates.Any(c => string.Equals(c.PackageId, packageId, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        aiRebuildCandidates.Add(new BatchAiRebuildCandidate
                                        {
                                            PackageId = packageId,
                                            DisplayName = displayName,
                                            Mod = item.Mod
                                        });
                                    }
                                }
                            }
                        }
                        catch (Exception itemEx)
                        {
                            failCount++;
                            failedMods.Add(displayName);
                            AutoTranslatorSettings.AddErrorLog("⚠️ " + "ATC_Log_BatchUploadItemFailed".Translate(displayName, itemEx.Message));
                            Log.Warning($"[AutoTranslationCore] Batch upload skipped {displayName} ({packageId}): {itemEx}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AutoTranslatorSettings.AddErrorLog("❌ " + "ATC_Log_BatchUploadWorkerFailed".Translate(ex.Message));
                    Log.Warning("[AutoTranslationCore] Batch upload worker failed: " + ex);
                }
                finally
                {
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {

                        AutoTranslatorSettings.IsRunning = false;
                        AutoTranslatorMod.Settings.CurrentTaskName = "";
                        AutoTranslatorMod.Settings.CurrentProgress = 0f;

                        Verse.Messages.Message("ATC_Msg_BatchUploadSuccess".Translate(successCount, uploadTypeLabel), RimWorld.MessageTypeDefOf.PositiveEvent, false);
                        if (failCount > 0)
                        {
                            AutoTranslatorSettings.AddLog("⚠️ " + "ATC_Log_BatchUploadFailedList".Translate(failCount, string.Join(", ", failedMods.Take(8).ToArray())));
                        }

                        if (aiRebuildCandidates.Count > 0)
                        {
                            AutoTranslatorSettings.AddLog("⚠️ " + "ATC_Log_BatchAiRebuildCandidates".Translate(aiRebuildCandidates.Count));
                            Find.WindowStack.Add(new Window_BatchAiRebuildPicker(aiRebuildCandidates));
                        }

                        AutoTranslatorSettings.HasFetchedCloudThisSession = false;
                        ModUpdateDetector.ClearStatusCache();
                        TranslationWorkbenchTab.RequestRefresh();
                    });
                }
            });
        }

        // 這個方法負責判斷 HasUploadable翻譯Files 條件是否成立。
        // EN: This method checks has uploadable translation files.
        private bool QueueBatchUploadPreparation(
            CloudBatchUploadSource source,
            string workspaceRoot,
            string targetLangFolder,
            string liveLangDir,
            string uNickname,
            string uToken,
            string uploadType,
            string uploadTypeLabel,
            string updateLog,
            List<CloudLocalModSnapshot> installedModSnapshots)
        {
            AutoTranslatorSettings.IsRunning = true;
            AutoTranslatorMod.Settings.CurrentTaskName = "Preparing batch upload";
            AutoTranslatorMod.Settings.CurrentProgress = 0f;

            System.Threading.Tasks.Task.Run(() =>
            {
                BatchUploadPreparationResult result = PrepareBatchUpload(source, workspaceRoot, targetLangFolder, liveLangDir, installedModSnapshots);
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    AutoTranslatorSettings.IsRunning = false;
                    AutoTranslatorMod.Settings.CurrentTaskName = "";
                    AutoTranslatorMod.Settings.CurrentProgress = 0f;

                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        Messages.Message(result.Error, MessageTypeDefOf.RejectInput, false);
                        return;
                    }

                    if (result.SkippedPatchModCount > 0)
                    {
                        Messages.Message("ATC_Msg_CloudBatchPatchModsSkipped".Translate(result.SkippedPatchModCount), MessageTypeDefOf.NeutralEvent, false);
                    }
                    if (result.UploadSources.Count == 0)
                    {
                        string emptyKey = source == CloudBatchUploadSource.Workspace
                            ? "ATC_Msg_WorkspaceEmpty"
                            : "ATC_Msg_BatchUploadPackEmpty";
                        Messages.Message(emptyKey.Translate(), MessageTypeDefOf.RejectInput, false);
                        return;
                    }

                    foreach (BatchUploadSourceItem item in result.UploadSources)
                    {
                        string displayName = !string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : item.PackageId;
                        item.TaskName = "ATC_Cloud_Uploading".Translate(displayName).ToString();
                    }

                    Messages.Message("ATC_Msg_BatchUploadStart".Translate(result.UploadSources.Count), MessageTypeDefOf.NeutralEvent, false);
                    StartPreparedBatchUpload(result.UploadSources, targetLangFolder, liveLangDir, uNickname, uToken, uploadType, uploadTypeLabel, updateLog);
                });
            });

            return true;
        }

        private static BatchUploadPreparationResult PrepareBatchUpload(
            CloudBatchUploadSource source,
            string workspaceRoot,
            string targetLangFolder,
            string liveLangDir,
            List<CloudLocalModSnapshot> installedModSnapshots)
        {
            BatchUploadPreparationResult result = new BatchUploadPreparationResult();
            try
            {
                Dictionary<string, CloudLocalModSnapshot> installedByPackage = (installedModSnapshots ?? new List<CloudLocalModSnapshot>())
                    .Where(m => !string.IsNullOrWhiteSpace(m.PackageId))
                    .GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                if (source == CloudBatchUploadSource.Workspace)
                {
                    string[] modDirs = Directory.Exists(workspaceRoot)
                        ? Directory.GetDirectories(workspaceRoot)
                        : new string[0];

                    foreach (string modDir in modDirs)
                    {
                        string packageId = Path.GetFileName(modDir);
                        installedByPackage.TryGetValue(packageId, out CloudLocalModSnapshot installed);
                        string displayName = installed != null && !string.IsNullOrWhiteSpace(installed.DisplayName)
                            ? installed.DisplayName
                            : packageId;
                        bool shouldSkip = installed != null
                            ? installed.ShouldSkipCloudSharing
                            : ShouldSkipCloudSharingMod(null, packageId, displayName);
                        if (shouldSkip)
                        {
                            result.SkippedPatchModCount++;
                            continue;
                        }

                        string langDir = Path.Combine(modDir, targetLangFolder);
                        if (!HasUploadableTranslationFiles(langDir, packageId, true)) continue;
                        result.UploadSources.Add(new BatchUploadSourceItem
                        {
                            PackageId = packageId,
                            SourceDir = langDir,
                            DisplayName = displayName,
                            ModName = displayName,
                            CopyBackToLivePack = true,
                            Mod = installed != null ? installed.Mod : null
                        });
                    }
                }
                else
                {
                    HashSet<string> uploadablePackages = BuildUploadableLocalPackPackageSet(liveLangDir, installedModSnapshots);
                    foreach (CloudLocalModSnapshot mod in installedModSnapshots ?? new List<CloudLocalModSnapshot>())
                    {
                        if (mod == null || string.IsNullOrEmpty(mod.PackageId)) continue;
                        if (mod.ShouldSkipCloudSharing)
                        {
                            result.SkippedPatchModCount++;
                            continue;
                        }
                        if (!uploadablePackages.Contains(mod.PackageId)) continue;

                        result.UploadSources.Add(new BatchUploadSourceItem
                        {
                            PackageId = mod.PackageId,
                            SourceDir = liveLangDir,
                            DisplayName = mod.DisplayName,
                            ModName = mod.DisplayName,
                            CopyBackToLivePack = false,
                            Mod = mod.Mod
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Verse.Log.Warning($"[AutoTranslationCore] Batch upload preparation failed: {ex}");
            }

            return result;
        }

        private static void StartPreparedBatchUpload(
            List<BatchUploadSourceItem> uploadSources,
            string targetLangFolder,
            string liveLangDir,
            string uNickname,
            string uToken,
            string uploadType,
            string uploadTypeLabel,
            string updateLog)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                int successCount = 0;
                int failCount = 0;
                List<string> failedMods = new List<string>();
                List<BatchAiRebuildCandidate> aiRebuildCandidates = new List<BatchAiRebuildCandidate>();

                AutoTranslatorSettings.IsRunning = true;

                try
                {
                    for (int i = 0; i < uploadSources.Count; i++)
                    {
                        BatchUploadSourceItem item = uploadSources[i];
                        string packageId = item.PackageId;
                        string displayName = !string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : packageId;

                        try
                        {
                            AutoTranslatorMod.Settings.CurrentTaskName = item.TaskName ?? displayName;
                            AutoTranslatorMod.Settings.CurrentProgress = (float)i / uploadSources.Count;

                            string langDir = item.SourceDir;

                            if (!Directory.Exists(langDir))
                            {
                                failCount++;
                                failedMods.Add(displayName);
                                AutoTranslatorSettings.AddLog("? " + "ATC_Log_BatchUploadMissingFolder".Translate(displayName));
                                continue;
                            }

                            if (!AutoTranslatorScanner.GetXmlFilesForTranslationCache(langDir, SearchOption.AllDirectories)
                                    .Any(file => !AutoTranslatorScanner.IsWorkbenchManualExportPath(file)))
                            {
                                failCount++;
                                failedMods.Add(displayName);
                                AutoTranslatorSettings.AddLog("? " + "ATC_Log_BatchUploadNoXml".Translate(displayName));
                                continue;
                            }

                            string modName = !string.IsNullOrEmpty(item.ModName) ? item.ModName : displayName;

                            AutoTranslatorCloudClient.UploadTranslationResult uploadResult =
                                await AutoTranslatorCloudClient.UploadTranslationWithDetailsAsync(packageId, targetLangFolder, modName, uNickname, uploadType, langDir, uToken, updateLog);
                            bool success = uploadResult != null && uploadResult.Success;

                            if (success)
                            {
                                successCount++;
                                if (!item.CopyBackToLivePack) continue;

                                foreach (string file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(langDir, SearchOption.AllDirectories))
                                {
                                    if (AutoTranslatorScanner.IsWorkbenchManualExportPath(file)) continue;
                                    string relPath = file.Substring(langDir.Length).TrimStart('\\', '/');

                                    string justFileName = Path.GetFileName(file);
                                    string justFileNameLower = justFileName.ToLower();
                                    string id1 = packageId.ToLower();
                                    string id2 = packageId.Replace(".", "_").ToLower();

                                    if (!justFileNameLower.StartsWith(id1 + "_") && !justFileNameLower.StartsWith(id1 + ".") &&
                                        !justFileNameLower.StartsWith(id2 + "_") && !justFileNameLower.StartsWith(id2 + "."))
                                    {
                                        string dirName = Path.GetDirectoryName(relPath);
                                        string newFileName = $"{id2}_{justFileName}";
                                        relPath = string.IsNullOrEmpty(dirName) ? newFileName : Path.Combine(dirName, newFileName);
                                    }

                                    string destPath = Path.Combine(liveLangDir, relPath);
                                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                                    File.Copy(file, destPath, true);
                                }
                            }
                            else
                            {
                                failCount++;
                                failedMods.Add(displayName);
                                AutoTranslatorScanner.TranslationUploadProvenanceSummary provenanceSummary =
                                    uploadResult != null ? uploadResult.ProvenanceSummary : null;
                                if (AutoTranslatorScanner.IsAiUploadBlockedByProvenance(provenanceSummary))
                                {
                                    AutoTranslatorSettings.AddLog("⚠️ " + AutoTranslatorScanner.FormatAiUploadNoCleanLog(displayName, provenanceSummary));
                                    if (item.Mod != null && !aiRebuildCandidates.Any(c => string.Equals(c.PackageId, packageId, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        aiRebuildCandidates.Add(new BatchAiRebuildCandidate
                                        {
                                            PackageId = packageId,
                                            DisplayName = displayName,
                                            Mod = item.Mod
                                        });
                                    }
                                }
                            }
                        }
                        catch (Exception itemEx)
                        {
                            failCount++;
                            failedMods.Add(displayName);
                            AutoTranslatorSettings.AddErrorLog("? " + "ATC_Log_BatchUploadItemFailed".Translate(displayName, itemEx.Message));
                            Log.Warning($"[AutoTranslationCore] Batch upload skipped {displayName} ({packageId}): {itemEx}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AutoTranslatorSettings.AddErrorLog("??" + "ATC_Log_BatchUploadWorkerFailed".Translate(ex.Message));
                    Log.Warning("[AutoTranslationCore] Batch upload worker failed: " + ex);
                }
                finally
                {
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        AutoTranslatorSettings.IsRunning = false;
                        AutoTranslatorMod.Settings.CurrentTaskName = "";
                        AutoTranslatorMod.Settings.CurrentProgress = 0f;

                        Verse.Messages.Message("ATC_Msg_BatchUploadSuccess".Translate(successCount, uploadTypeLabel), RimWorld.MessageTypeDefOf.PositiveEvent, false);
                        if (failCount > 0)
                        {
                            AutoTranslatorSettings.AddLog("? " + "ATC_Log_BatchUploadFailedList".Translate(failCount, string.Join(", ", failedMods.Take(8).ToArray())));
                        }

                        if (aiRebuildCandidates.Count > 0)
                        {
                            AutoTranslatorSettings.AddLog("⚠️ " + "ATC_Log_BatchAiRebuildCandidates".Translate(aiRebuildCandidates.Count));
                            Find.WindowStack.Add(new Window_BatchAiRebuildPicker(aiRebuildCandidates));
                        }

                        AutoTranslatorSettings.HasFetchedCloudThisSession = false;
                        ModUpdateDetector.ClearStatusCache();
                        TranslationWorkbenchTab.RequestRefresh();
                    });
                }
            });
        }

        private static bool HasUploadableTranslationFiles(string sourceDir, string packageId, bool isWorkspace)
        {
            if (string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(packageId) || !System.IO.Directory.Exists(sourceDir)) return false;
            string id1 = packageId.ToLower();
            string id2 = packageId.Replace(".", "_").ToLower();

            foreach (string file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(sourceDir, System.IO.SearchOption.AllDirectories))
            {
                if (AutoTranslatorScanner.IsWorkbenchManualExportPath(file)) continue;
                if (isWorkspace) return true;

                string fileName = System.IO.Path.GetFileName(file).ToLower();
                if (fileName.StartsWith(id1 + "_") || fileName.StartsWith(id1 + ".") ||
                    fileName.StartsWith(id2 + "_") || fileName.StartsWith(id2 + "."))
                {
                    return true;
                }
            }

            return false;
        }

        private static HashSet<string> BuildUploadableLocalPackPackageSet(string sourceDir, IEnumerable<CloudLocalModSnapshot> installedMods)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(sourceDir) || !System.IO.Directory.Exists(sourceDir) || installedMods == null) return result;

            var prefixes = new List<BatchUploadPackagePrefix>();
            foreach (CloudLocalModSnapshot mod in installedMods)
            {
                if (mod == null || string.IsNullOrEmpty(mod.PackageId)) continue;

                string id1 = mod.PackageId.ToLowerInvariant();
                string id2 = mod.PackageId.Replace(".", "_").ToLowerInvariant();
                prefixes.Add(new BatchUploadPackagePrefix { PackageId = mod.PackageId, Prefix = id1 + "_" });
                prefixes.Add(new BatchUploadPackagePrefix { PackageId = mod.PackageId, Prefix = id1 + "." });
                if (!string.Equals(id1, id2, StringComparison.Ordinal))
                {
                    prefixes.Add(new BatchUploadPackagePrefix { PackageId = mod.PackageId, Prefix = id2 + "_" });
                    prefixes.Add(new BatchUploadPackagePrefix { PackageId = mod.PackageId, Prefix = id2 + "." });
                }
            }

            prefixes = prefixes
                .Where(p => !string.IsNullOrEmpty(p.Prefix))
                .OrderByDescending(p => p.Prefix.Length)
                .ToList();

            foreach (string file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(sourceDir, System.IO.SearchOption.AllDirectories))
            {
                string fileName;
                try
                {
                    fileName = System.IO.Path.GetFileName(file).ToLowerInvariant();
                }
                catch
                {
                    continue;
                }

                foreach (BatchUploadPackagePrefix prefix in prefixes)
                {
                    if (!fileName.StartsWith(prefix.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(prefix.PackageId);
                    break;
                }
            }

            return result;
        }

        private static HashSet<string> BuildUploadableLocalPackPackageSet(string sourceDir, IEnumerable<ModMetaData> installedMods)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(sourceDir) || !System.IO.Directory.Exists(sourceDir) || installedMods == null) return result;

            var prefixes = new List<BatchUploadPackagePrefix>();
            foreach (ModMetaData mod in installedMods)
            {
                if (mod == null || string.IsNullOrEmpty(mod.PackageId)) continue;

                string id1 = mod.PackageId.ToLowerInvariant();
                string id2 = mod.PackageId.Replace(".", "_").ToLowerInvariant();
                prefixes.Add(new BatchUploadPackagePrefix { PackageId = mod.PackageId, Prefix = id1 + "_" });
                prefixes.Add(new BatchUploadPackagePrefix { PackageId = mod.PackageId, Prefix = id1 + "." });
                if (!string.Equals(id1, id2, StringComparison.Ordinal))
                {
                    prefixes.Add(new BatchUploadPackagePrefix { PackageId = mod.PackageId, Prefix = id2 + "_" });
                    prefixes.Add(new BatchUploadPackagePrefix { PackageId = mod.PackageId, Prefix = id2 + "." });
                }
            }

            prefixes = prefixes
                .Where(p => !string.IsNullOrEmpty(p.Prefix))
                .OrderByDescending(p => p.Prefix.Length)
                .ToList();

            foreach (string file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(sourceDir, System.IO.SearchOption.AllDirectories))
            {
                string fileName;
                try
                {
                    fileName = System.IO.Path.GetFileName(file).ToLowerInvariant();
                }
                catch
                {
                    continue;
                }

                foreach (BatchUploadPackagePrefix prefix in prefixes)
                {
                    if (!fileName.StartsWith(prefix.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(prefix.PackageId);
                    break;
                }
            }

            return result;
        }
        // 這個方法負責清理並標準化 雲端上傳Type 內容。
        // EN: This method cleans and normalizes cloud upload type.
        internal static string NormalizeCloudUploadType(string uploadType, bool allowOfficial = true)
        {
            if (uploadType == "Manual") return uploadType;
            if (uploadType == "Official_Group" && allowOfficial) return uploadType;
            return "AI_Auto";
        }

        // 這個方法負責取得 雲端上傳TypeLabel 資料。
        // EN: This method gets cloud upload type label.
        private static string GetCloudUploadTypeLabel(string uploadType)
        {
            switch (NormalizeCloudUploadType(uploadType))
            {
                case "Official_Group": return "ATC_Type_Official".Translate().ToString();
                case "Manual": return "ATC_Type_Manual".Translate().ToString();
                default: return "ATC_Type_AI".Translate().ToString();
            }
        }

        private sealed class Window_BatchAiRebuildPicker : Window
        {
            private readonly List<BatchAiRebuildCandidate> candidates;
            private Vector2 scrollPos = Vector2.zero;

            public override Vector2 InitialSize => new Vector2(640f, 560f);

            public Window_BatchAiRebuildPicker(IEnumerable<BatchAiRebuildCandidate> candidates)
            {
                this.candidates = (candidates ?? Enumerable.Empty<BatchAiRebuildCandidate>())
                    .Where(c => c != null && c.Mod != null)
                    .GroupBy(c => c.PackageId ?? "", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(c => c.DisplayName ?? c.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                doCloseButton = false;
                doCloseX = true;
                forcePause = true;
                absorbInputAroundWindow = true;
            }

            public override void DoWindowContents(Rect inRect)
            {
                bool previousBypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
                Patch_GUI_Label_GUIContent.BypassInterceptor = true;
                try
                {
                    Text.Font = GameFont.Medium;
                    Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "ATC_Title_BatchAiRebuildCandidates".Translate(candidates.Count));
                    Text.Font = GameFont.Small;

                    Rect descRect = new Rect(0f, 38f, inRect.width, 58f);
                    Widgets.Label(descRect, "ATC_Msg_BatchAiRebuildCandidates".Translate(candidates.Count));

                    Rect listOutRect = new Rect(0f, 104f, inRect.width, inRect.height - 154f);
                    float rowHeight = 46f;
                    Rect listViewRect = new Rect(0f, 0f, listOutRect.width - 20f, Math.Max(listOutRect.height, candidates.Count * rowHeight));
                    Widgets.BeginScrollView(listOutRect, ref scrollPos, listViewRect);
                    float y = 0f;
                    foreach (BatchAiRebuildCandidate candidate in candidates)
                    {
                        Rect rowRect = new Rect(0f, y, listViewRect.width, rowHeight - 4f);
                        Widgets.DrawHighlightIfMouseover(rowRect);

                        string displayName = !string.IsNullOrWhiteSpace(candidate.DisplayName) ? candidate.DisplayName : candidate.PackageId;
                        Widgets.Label(new Rect(rowRect.x + 6f, rowRect.y + 3f, rowRect.width - 176f, 20f), displayName);
                        GUI.color = Color.gray;
                        Widgets.Label(new Rect(rowRect.x + 6f, rowRect.y + 22f, rowRect.width - 176f, 18f), candidate.PackageId ?? "");
                        GUI.color = Color.white;

                        Rect rebuildRect = new Rect(rowRect.xMax - 160f, rowRect.y + 6f, 150f, 30f);
                        if (Widgets.ButtonText(rebuildRect, "ATC_Btn_PureAiRebuildForUpload".Translate()))
                        {
                            if (AutoTranslatorSettings.IsRunning)
                            {
                                Messages.Message("ATC_Msg_PureAiRebuildBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                            }
                            else
                            {
                                Close();
                                AutoTranslatorSettings.ActiveTab = 0;
                                AutoTranslatorSettings.mainScrollPos = Vector2.zero;
                                AutoTranslatorScanner.StartPureAiRebuildForUpload(candidate.Mod);
                            }
                        }

                        y += rowHeight;
                    }
                    Widgets.EndScrollView();

                    if (Widgets.ButtonText(new Rect(inRect.width - 130f, inRect.height - 38f, 130f, 34f), "ATC_Btn_Cancel".Translate()))
                    {
                        Close();
                    }
                }
                finally
                {
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                    Text.Anchor = TextAnchor.UpperLeft;
                    Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
                }
            }
        }

    }
}
