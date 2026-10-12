using RimWorld;
using AutoTranslator_Core.Workflow;
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
            TranslationGroup,
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

                    string modeLabel = GetCloudBatchDownloadModeLabel(mode);
                    string scopeLabel = WfText("已加载 Mod / DLC", "Loaded mods / DLC");
                    string targetLangLabel = GetLangLabel(Settings.CloudTargetLang);
                    string preview = string.Join("、", result.Items
                        .Take(8)
                        .Select(item => !string.IsNullOrWhiteSpace(item.DisplayName)
                            ? item.DisplayName
                            : item.PackageId)
                        .ToArray());
                    if (result.Items.Count > 8)
                    {
                        preview += WfText(
                            " 等 " + result.Items.Count + " 个 Mod",
                            " and " + (result.Items.Count - 8) + " more");
                    }
                    string message = WfText(
                        "即将从公共云端下载译文，并替换这些 Mod 在 ATC 生成包中的现有本地译文文件。\n\n" +
                        "下载策略：" + modeLabel + "\n" +
                        "下载范围：" + scopeLabel + "\n" +
                        "目标语言：" + targetLangLabel + "（" + targetLangStr + "）\n" +
                        "涉及 Mod：" + result.Items.Count + " 个\n" +
                        "内容预览：" + preview + "\n\n" +
                        "通过译文编辑器保存、已记录到 V4 数据库的“手动翻译”会在下载后重新恢复。" +
                        "直接修改 XML 但尚未点击“刷新状态”的内容可能被覆盖。\n\n" +
                        "建议先返回翻译工作台刷新状态。是否仍要开始下载？",
                        "Cloud translations will be downloaded and will replace existing local translation files for these mods in the ATC generated pack.\n\n" +
                        "Download policy: " + modeLabel + "\n" +
                        "Scope: " + scopeLabel + "\n" +
                        "Target language: " + targetLangLabel + " (" + targetLangStr + ")\n" +
                        "Mods affected: " + result.Items.Count + "\n" +
                        "Preview: " + preview + "\n\n" +
                        "Manual translations saved through the translation editor and recorded in the V4 database will be restored after download. " +
                        "Direct XML edits that have not been synchronized with Refresh Status may be overwritten.\n\n" +
                        "Refreshing status in Translation Workbench first is recommended. Start download anyway?");
                    Find.WindowStack.Add(new Window_AtcDialog(
                        message,
                        WfText("确认下载", "Download"),
                        () =>
                        {
                            Verse.Messages.Message("ATC_Msg_BatchStart".Translate(result.Items.Count),
                                RimWorld.MessageTypeDefOf.NeutralEvent, false);
                            StartPreparedBatchDownload(result.Items, targetLangStr);
                        },
                        WfText("取消", "Cancel"),
                        null,
                        WfText("确认批量下载", "Confirm batch download")));
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
            if (string.Equals(targetType, "Official_Group", StringComparison.OrdinalIgnoreCase)) return CloudBatchDownloadMode.TranslationGroup;
            if (string.Equals(targetType, "Manual", StringComparison.OrdinalIgnoreCase)) return CloudBatchDownloadMode.Manual;
            if (string.Equals(targetType, "Best", StringComparison.OrdinalIgnoreCase)) return CloudBatchDownloadMode.Best;
            return CloudBatchDownloadMode.AI;
        }

        private static string GetCloudBatchDownloadModeLabel(CloudBatchDownloadMode mode)
        {
            switch (mode)
            {
                case CloudBatchDownloadMode.TranslationGroup:
                    return WfText("仅汉化组精翻", "Translation-group only");
                case CloudBatchDownloadMode.Manual:
                    return WfText("仅人工精翻", "Human-curated only");
                case CloudBatchDownloadMode.AI:
                    return WfText("仅 AI 译文", "AI only");
                default:
                    return WfText("最佳可用：汉化组精翻 > 人工精翻 > AI", "Best available: translation-group curated > human-curated > AI");
            }
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
                case CloudBatchDownloadMode.TranslationGroup:
                    return IsTranslationGroupCloudRecord(record);
                case CloudBatchDownloadMode.Manual:
                    return string.Equals(record.TranslationType, "Manual", StringComparison.OrdinalIgnoreCase);
                case CloudBatchDownloadMode.Best:
                    return GetBatchRecordPriority(record) > 0;
                case CloudBatchDownloadMode.AI:
                default:
                    return string.Equals(record.TranslationType, "AI_Auto", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool IsTranslationGroupCloudRecord(CloudModRecord record)
        {
            return record != null &&
                   string.Equals(record.TranslationType, "Official_Group", StringComparison.OrdinalIgnoreCase);
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
            if (IsTranslationGroupCloudRecord(record)) return 3;
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
            int queuedCount = modsToDownload != null ? modsToDownload.Count : 0;
            if (queuedCount <= 0) return;
            if (!WorkflowTaskCoordinator.Instance.TryBegin(
                    WorkflowTaskKind.CloudTranslation,
                    "Cloud batch download",
                    out WorkflowTaskLease taskLease))
            {
                Verse.Messages.Message(
                    WfText("已有后台任务正在运行，请等待当前任务完成后再下载。",
                        "Another background task is running. Wait for it to finish before downloading."),
                    RimWorld.MessageTypeDefOf.RejectInput,
                    false);
                return;
            }

            System.Threading.Tasks.Task.Run(async () =>
            {
                int successCount = 0;
                int failCount = 0;
                List<string> failedMods = new List<string>();
                List<string> repairedPackages = new List<string>();
                int totalCount = queuedCount;
                HashSet<string> pendingPreclearedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> packagesWithPreviousTranslations = ModUpdateDetector.GetPackageIdsWithLocalTranslationFilesForKnownPackages(
                    (modsToDownload ?? new List<BatchDownloadItem>()).Select(item => item.PackageId),
                    AutoTranslatorMod.Settings.CloudTargetLang);

                try
                {
                    WorkflowTaskCoordinator.Instance.ReportProgress(
                        0,
                        totalCount,
                        string.Empty,
                        WfText("下载队列已建立 · 成功 0 · 失败 0 · 剩余 ",
                            "Download queue ready · Succeeded 0 · Failed 0 · Remaining ") + totalCount,
                        0,
                        totalCount,
                        writeRuntimeLog: false);
                    AutoTranslatorSettings.AddLog(
                        "▶ " + WfText("云端下载", "Cloud download") +
                        WfText(" · 队列已建立 · 共 ", " · Queue ready · ") + totalCount +
                        WfText(" 个 Mod", " mods"));
                    AutoTranslatorSettings.AddDebugLog(
                        "cloud.download.batch start total=" + totalCount + " language=" + targetLangStr);

                    const int clearChunkSize = 32;
                    int completedCount = 0;
                    int maxParallel = Math.Max(1, Math.Min(4, AutoTranslatorMod.Settings.CloudBatchDownloadConcurrency));
                    using (var semaphore = new System.Threading.SemaphoreSlim(maxParallel, maxParallel))
                    {
                        for (int chunkStart = 0; chunkStart < totalCount; chunkStart += clearChunkSize)
                        {
                            taskLease.CancellationToken.ThrowIfCancellationRequested();
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
                            foreach (BatchDownloadItem queuedItem in chunkItems)
                            {
                                int queuedNumber = modsToDownload.IndexOf(queuedItem) + 1;
                                AutoTranslatorSettings.AddDebugLog(
                                    "cloud.download.mod queued index=" + queuedNumber + "/" + totalCount +
                                    " package=" + queuedItem.PackageId +
                                    " record=" + (queuedItem.Record?.RecordId ?? string.Empty));
                            }
                            Task<BatchDownloadExecutionResult>[] downloads = chunkItems
                                .Select((item, chunkIndex) => DownloadPreparedBatchItemAsync(
                                    item,
                                    targetLangStr,
                                    semaphore,
                                    taskLease.CancellationToken,
                                    chunkStart + chunkIndex + 1,
                                    totalCount))
                                .ToArray();
                            BatchDownloadExecutionResult[] chunkResults = await Task.WhenAll(downloads);

                            foreach (BatchDownloadExecutionResult download in chunkResults)
                            {
                                BatchDownloadItem mod = download.Item;
                                completedCount++;
                                if (download.Success)
                                {
                                    pendingPreclearedPackages.Remove(mod.PackageId);
                                    successCount++;
                                    repairedPackages.Add(mod.PackageId);
                                    AutoTranslatorSettings.AddLog(
                                        "✓ " + WfText("云端下载", "Cloud download") +
                                        " · " + completedCount + "/" + totalCount + " · " + mod.DisplayName +
                                        WfText(" · 下载成功", " · Succeeded"));
                                }
                                else
                                {
                                    RestorePreclearedBatchPackage(mod.PackageId, packagesWithPreviousTranslations);
                                    pendingPreclearedPackages.Remove(mod.PackageId);
                                    failCount++;
                                    failedMods.Add(mod.DisplayName);
                                    AutoTranslatorSettings.AddErrorLog(
                                        WfText("云端下载失败", "Cloud download failed") +
                                        " · " + completedCount + "/" + totalCount + " · " + mod.DisplayName +
                                        " · " + mod.PackageId,
                                        showMessage: false);
                                }

                                WorkflowTaskCoordinator.Instance.ReportProgress(
                                    completedCount,
                                    totalCount,
                                    mod.DisplayName,
                                    WfText("已处理", "Processed") +
                                    " · " + WfText("成功 ", "Succeeded ") + successCount +
                                    " · " + WfText("失败 ", "Failed ") + failCount +
                                    " · " + WfText("剩余 ", "Remaining ") + (totalCount - completedCount),
                                    completedCount,
                                    totalCount,
                                    writeRuntimeLog: false);
                            }
                        }
                    }

                    if (successCount > 0)
                    {
                        WorkflowTaskCoordinator.Instance.ReportProgress(
                            totalCount,
                            totalCount,
                            string.Empty,
                            WfText("正在整理已下载译文", "Finalizing downloaded translations") +
                            " · " + WfText("成功 ", "Succeeded ") + successCount +
                            " · " + WfText("失败 ", "Failed ") + failCount,
                            totalCount,
                            totalCount,
                            writeRuntimeLog: false);
                        AutoTranslatorSettings.AddLog(
                            "▶ " + WfText("云端下载", "Cloud download") +
                            WfText(" · 正在整理已下载译文", " · Finalizing downloaded translations"));
                        TranslationFileRepairService.RepairPackages(repairedPackages, targetLangStr, requestMemoryDrop: false);
                    }

                    WorkflowTaskCoordinator.Instance.MarkTerminal(taskLease.RunId, WorkflowRunState.Completed);
                    taskLease.Dispose();
                    taskLease = null;

                    string synchronizationError = string.Empty;
                    if (successCount > 0)
                    {
                        AutoTranslatorMod.Settings.CurrentTaskName = "";
                        AutoTranslatorMod.Settings.CurrentProgress = 0f;
                        synchronizationError = await SynchronizeCloudDownloadAsync(targetLangStr);
                    }
                    else if (failCount > 0)
                    {
                        AutoTranslatorScanner.RequestMemoryDrop();
                    }

                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        AutoTranslatorMod.Settings.CurrentTaskName = "";
                        AutoTranslatorMod.Settings.CurrentProgress = 0f;
                        AutoTranslatorSettings.AddLog("✓ " + "ATC_Log_BatchDownloadSummary".Translate(successCount, failCount, totalCount));
                        if (failedMods.Count > 0)
                        {
                            AutoTranslatorSettings.AddWarningLog("ATC_Log_BatchDownloadFailedList".Translate(string.Join(", ", failedMods.Take(5).ToArray())));
                        }
                        Verse.Messages.Message("ATC_Msg_BatchSuccess".Translate(successCount, totalCount), RimWorld.MessageTypeDefOf.PositiveEvent, false);
                        if (successCount > 0)
                        {
                            if (string.IsNullOrWhiteSpace(synchronizationError))
                                Verse.Messages.Message(
                                    WfText("云端译文已下载，并已同步到 V4 译文状态。",
                                        "Cloud translations were downloaded and synchronized with the V4 translation state."),
                                    RimWorld.MessageTypeDefOf.PositiveEvent, false);
                            else
                                Verse.Messages.Message(
                                    WfText("云端译文已下载，但状态同步失败：", "Cloud download completed, but state synchronization failed: ") +
                                    synchronizationError,
                                    RimWorld.MessageTypeDefOf.RejectInput, false);
                        }
                    });
                }
                catch (OperationCanceledException ex)
                {
                    foreach (string packageId in pendingPreclearedPackages)
                    {
                        RestorePreclearedBatchPackage(packageId, packagesWithPreviousTranslations);
                    }
                    if (taskLease != null)
                        WorkflowTaskCoordinator.Instance.MarkTerminal(
                            taskLease.RunId, WorkflowRunState.Cancelled, ex.Message);
                    AutoTranslatorSettings.AddLog(
                        "■ " + WfText("云端下载已停止", "Cloud download stopped") +
                        " · " + WfText("成功 ", "Succeeded ") + successCount +
                        " · " + WfText("失败 ", "Failed ") + failCount +
                        " · " + WfText("未处理 ", "Not processed ") +
                        Math.Max(0, totalCount - successCount - failCount));
                }
                catch (Exception ex)
                {
                    foreach (string packageId in pendingPreclearedPackages)
                    {
                        RestorePreclearedBatchPackage(packageId, packagesWithPreviousTranslations);
                    }
                    if (taskLease != null)
                        WorkflowTaskCoordinator.Instance.MarkTerminal(
                            taskLease.RunId, WorkflowRunState.Failed, ex.Message);
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        AutoTranslatorMod.Settings.CurrentTaskName = "";
                        AutoTranslatorMod.Settings.CurrentProgress = 0f;
                        AutoTranslatorSettings.AddErrorLog("[Cloud] Batch download failed: " + ex.Message);
                        Verse.Messages.Message("ATC_Msg_DownloadFailed".Translate(ex.Message), RimWorld.MessageTypeDefOf.RejectInput, false);
                    });
                }
                finally
                {
                    taskLease?.Dispose();
                }
            });
        }

        private static async Task<string> SynchronizeCloudDownloadAsync(string targetLanguage)
        {
            try
            {
                AutoTranslatorSettings.AddLog(
                    WfText("云端翻译：开始同步下载后的译文状态", "Cloud translation: synchronizing downloaded state"));
                await WorkflowBackendRuntime.GetOrCreate()
                    .RunCloudTranslationStateRefreshAsync(targetLanguage);
                AutoTranslatorSettings.AddLog(
                    WfText("云端翻译：译文状态同步完成", "Cloud translation: state synchronization complete"));
                return string.Empty;
            }
            catch (Exception ex)
            {
                AutoTranslatorSettings.AddErrorLog(
                    WfText("云端翻译状态同步失败：", "Cloud translation synchronization failed: ") + ex.Message);
                return ex.Message;
            }
        }

        private static async Task<BatchDownloadExecutionResult> DownloadPreparedBatchItemAsync(
            BatchDownloadItem item,
            string targetLangStr,
            System.Threading.SemaphoreSlim semaphore,
            System.Threading.CancellationToken cancellationToken,
            int currentNumber,
            int totalCount)
        {
            bool acquired = false;
            try
            {
                await semaphore.WaitAsync(cancellationToken);
                acquired = true;
                cancellationToken.ThrowIfCancellationRequested();
                AutoTranslatorSettings.AddLog(
                    "▶ " + WfText("云端下载", "Cloud download") +
                    " · " + currentNumber + "/" + totalCount + " · " + item.DisplayName);
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
                    restoreBackupOnFailure: false,
                    cancellationToken: cancellationToken);
                return new BatchDownloadExecutionResult { Item = item, Success = success };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[AutoTranslationCore] Batch cloud download failed for " + item.PackageId + ": " + ex.Message);
                return new BatchDownloadExecutionResult { Item = item, Success = false };
            }
            finally
            {
                if (acquired)
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
            string targetLangLabel = GetLangLabel(Settings.CloudTargetLang);
            string liveLangDir = System.IO.Path.Combine(packPath, "Languages", targetLangFolder);
            string uNickname = Settings.CloudNickname;
            string uToken = Settings.CloudAdminToken;
            string uploadType = NormalizeCloudUploadType(Settings.CloudUploadType, !string.IsNullOrWhiteSpace(uToken));
            string uploadTypeLabel = GetCloudUploadTypeLabel(uploadType);
            string updateLog = (Settings.CloudBatchUploadLog ?? "").Trim();

            Settings.CloudUploadType = uploadType;
            WriteSettings();

            List<CloudLocalModSnapshot> installedModSnapshots = Verse.ModLister.AllInstalledMods
                .Where(m => m != null && m.Active && !string.IsNullOrEmpty(m.PackageId))
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
                targetLangLabel,
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
                    if (mod == null || !mod.Active || string.IsNullOrEmpty(mod.PackageId)) continue;
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
                                AutoTranslatorSettings.AddWarningLog("ATC_Log_BatchUploadMissingFolder".Translate(displayName));
                                continue;
                            }

                            if (!AutoTranslatorScanner.GetXmlFilesForTranslationCache(langDir, System.IO.SearchOption.AllDirectories)
                                    .Any(file => !AutoTranslatorScanner.IsWorkbenchManualExportPath(file)))
                            {
                                failCount++;
                                failedMods.Add(displayName);
                                AutoTranslatorSettings.AddWarningLog("ATC_Log_BatchUploadNoXml".Translate(displayName));
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
                                AutoTranslatorScanner.UploadLabelValidationSummary provenanceSummary =
                                    uploadResult != null ? uploadResult.ValidationSummary : null;
                                
                            }
                        }
                        catch (Exception itemEx)
                        {
                            failCount++;
                            failedMods.Add(displayName);
                            AutoTranslatorSettings.AddWarningLog("ATC_Log_BatchUploadItemFailed".Translate(displayName, itemEx.Message));
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
                            AutoTranslatorSettings.AddWarningLog("ATC_Log_BatchUploadFailedList".Translate(failCount, string.Join(", ", failedMods.Take(8).ToArray())));
                        }

                        

                        AutoTranslatorSettings.HasFetchedCloudThisSession = false;
                        ModUpdateDetector.ClearStatusCache();
                        AutoTranslator_Core.Workflow.WorkflowTaskCoordinator.Instance.NotifyWorkbenchDataChanged();
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
            string targetLangLabel,
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

                    string sourceLabel = source == CloudBatchUploadSource.Workspace
                        ? WfText("贡献工作区", "contribution workspace")
                        : WfText("当前本地译文", "current local translations");
                    string updateLogState = string.IsNullOrWhiteSpace(updateLog)
                        ? WfText("未填写", "not provided")
                        : WfText("已填写", "provided");
                    string uploadPreview = string.Join("、", result.UploadSources
                        .Take(8)
                        .Select(item => !string.IsNullOrWhiteSpace(item.DisplayName)
                            ? item.DisplayName
                            : item.PackageId)
                        .ToArray());
                    if (result.UploadSources.Count > 8)
                    {
                        uploadPreview += WfText(
                            " 等 " + result.UploadSources.Count + " 个 Mod",
                            " and " + (result.UploadSources.Count - 8) + " more");
                    }
                    string message = WfText(
                        "即将把本机译文上传到公共云端。其他用户可能会下载这些内容。\n\n" +
                        "上传来源：" + sourceLabel + "\n" +
                        "目标语言：" + targetLangLabel + "（" + targetLangFolder + "）\n" +
                        "云端来源类别：" + uploadTypeLabel + "\n" +
                        "待上传 Mod：" + result.UploadSources.Count + " 个\n" +
                        "内容预览：" + uploadPreview + "\n" +
                        "更新说明：" + updateLogState + "\n\n" +
                        "请确认来源、语言和类别均正确。是否开始上传？",
                        "Translations from this computer are about to be uploaded to the public cloud. Other users may download them.\n\n" +
                        "Source: " + sourceLabel + "\n" +
                        "Target language: " + targetLangLabel + " (" + targetLangFolder + ")\n" +
                        "Cloud source category: " + uploadTypeLabel + "\n" +
                        "Mods to upload: " + result.UploadSources.Count + "\n" +
                        "Preview: " + uploadPreview + "\n" +
                        "Update note: " + updateLogState + "\n\n" +
                        "Confirm that the source, language, and category are correct. Start upload?");
                    Find.WindowStack.Add(new Window_AtcDialog(
                        message,
                        WfText("确认上传", "Upload"),
                        () =>
                        {
                            Messages.Message("ATC_Msg_BatchUploadStart".Translate(result.UploadSources.Count),
                                MessageTypeDefOf.NeutralEvent, false);
                            StartPreparedBatchUpload(
                                result.UploadSources,
                                targetLangFolder,
                                liveLangDir,
                                uNickname,
                                uToken,
                                uploadType,
                                uploadTypeLabel,
                                updateLog);
                        },
                        WfText("取消", "Cancel"),
                        null,
                        WfText("确认批量上传", "Confirm batch upload")));
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
                                AutoTranslatorSettings.AddWarningLog("ATC_Log_BatchUploadMissingFolder".Translate(displayName));
                                continue;
                            }

                            if (!AutoTranslatorScanner.GetXmlFilesForTranslationCache(langDir, SearchOption.AllDirectories)
                                    .Any(file => !AutoTranslatorScanner.IsWorkbenchManualExportPath(file)))
                            {
                                failCount++;
                                failedMods.Add(displayName);
                                AutoTranslatorSettings.AddWarningLog("ATC_Log_BatchUploadNoXml".Translate(displayName));
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
                                AutoTranslatorScanner.UploadLabelValidationSummary provenanceSummary =
                                    uploadResult != null ? uploadResult.ValidationSummary : null;
                                
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
                    AutoTranslatorSettings.AddErrorLog("ATC_Log_BatchUploadWorkerFailed".Translate(ex.Message));
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
                            AutoTranslatorSettings.AddWarningLog("ATC_Log_BatchUploadFailedList".Translate(failCount, string.Join(", ", failedMods.Take(8).ToArray())));
                        }

                        

                        AutoTranslatorSettings.HasFetchedCloudThisSession = false;
                        ModUpdateDetector.ClearStatusCache();
                        AutoTranslator_Core.Workflow.WorkflowTaskCoordinator.Instance.NotifyWorkbenchDataChanged();
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
        internal static string NormalizeCloudUploadType(string uploadType, bool allowTranslationGroup = true)
        {
            if (uploadType == "Manual") return uploadType;
            if (uploadType == "Official_Group" && allowTranslationGroup) return uploadType;
            return "AI_Auto";
        }

        // 這個方法負責取得 雲端上傳TypeLabel 資料。
        // EN: This method gets cloud upload type label.
        private static string GetCloudUploadTypeLabel(string uploadType)
        {
            switch (NormalizeCloudUploadType(uploadType))
            {
                case "Official_Group": return "ATC_Type_Official".Translate().ToString();
                case "Manual": return WfText("人工精翻", "Human-curated");
                default: return "ATC_Type_AI".Translate().ToString();
            }
        }

        

    }
}
