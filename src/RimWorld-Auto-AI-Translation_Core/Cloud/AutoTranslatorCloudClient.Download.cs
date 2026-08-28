using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine.Networking;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責雲端下載流程。
// EN: This file downloads translation packages from the cloud service.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器雲端Client 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorCloudClient.
    public static partial class AutoTranslatorCloudClient
    {
        private sealed class CloudDownloadAttemptResult
        {
            public bool Success;
            public long StatusCode;
            public string Error;
            public string FilePath;
            public string ServedRecordId;
            public string FallbackReason;
        }

        // 這個方法負責下載 AndInjectAsync 資料。
        // EN: This method downloads and inject async.
        public static async Task<bool> DownloadAndInjectAsync(string packageId, string targetLangFolder, CloudModRecord targetRecord = null, bool requestMemoryDrop = true, bool requestRuntimeRefreshAfterClear = true, AutoTranslatorScanner.LocalTranslationDeleteTarget clearTarget = null, bool clearExistingTranslations = true, bool restoreBackupOnFailure = true)
        {
            if (AutoTranslatorMod.Settings != null && AutoTranslatorMod.Settings.IsCloudDownloadBlacklisted(packageId))
            {
                AutoTranslatorSettings.AddLog(AutoTranslatorAPI.TranslateText("ATC_Blacklist_DownloadSkipped", packageId));
                return false;
            }

            Verse.ModMetaData targetMod = null;
            int maxRetries = 4;
            string downloadedZipPath = null;
            CloudModRecord resolvedRecord = targetRecord;
            bool registryRefreshAttempted = false;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    string baseUrl = attempt >= 3 ? BackupApiBaseUrl : PrimaryApiBaseUrl;
                    string url = $"{baseUrl}/download/{Uri.EscapeDataString(packageId)}/{Uri.EscapeDataString(targetLangFolder)}";
                    bool usedRecordId = resolvedRecord != null && !string.IsNullOrEmpty(resolvedRecord.RecordId);
                    if (usedRecordId)
                    {
                        url += $"?recordId={Uri.EscapeDataString(resolvedRecord.RecordId)}";
                    }

                    string attemptZipPath = Path.Combine(
                        Path.GetTempPath(),
                        "ATC_Cloud_" + Guid.NewGuid().ToString("N") + ".zip");
                    CloudDownloadAttemptResult response = await DownloadCloudArchiveAttemptAsync(
                        url,
                        attemptZipPath,
                        120 + attempt * 60);
                    if (response.Success)
                    {
                        downloadedZipPath = response.FilePath;
                        if (!string.IsNullOrWhiteSpace(response.ServedRecordId) &&
                            (resolvedRecord == null || !string.Equals(response.ServedRecordId, resolvedRecord.RecordId, StringComparison.OrdinalIgnoreCase)))
                        {
                            resolvedRecord = FindCloudRecordById(response.ServedRecordId) ?? new CloudModRecord
                            {
                                RecordId = response.ServedRecordId,
                                PackageId = packageId,
                                Language = targetLangFolder,
                                TargetModVersion = resolvedRecord?.TargetModVersion ?? "Unknown",
                                TranslationDate = DateTime.UtcNow
                            };
                        }
                        if (!string.IsNullOrWhiteSpace(response.FallbackReason))
                        {
                            LogCloudTranslatedMessage("ATC_Cloud_RecordRecovered", packageId);
                        }
                        break;
                    }

                    TryDeleteCloudTempFile(response.FilePath);
                    if (CloudDownloadRecoveryPolicy.ShouldRefreshRegistry(response.StatusCode, usedRecordId) && !registryRefreshAttempted)
                    {
                        registryRefreshAttempted = true;
                        List<CloudModRecord> refreshed = await FetchRegistryAsync();
                        CloudModRecord replacement = CloudDownloadRecoveryPolicy.SelectReplacement(
                            refreshed,
                            packageId,
                            targetLangFolder,
                            resolvedRecord);
                        if (replacement != null)
                        {
                            resolvedRecord = replacement;
                            AutoTranslatorMod.ReplaceCloudRegistryAfterRecovery(refreshed, packageId, replacement);
                            LogCloudTranslatedMessage("ATC_Cloud_RecordRecovered", packageId);
                            attempt--;
                            continue;
                        }

                        // Older Workers can still recover by selecting their latest active record.
                        resolvedRecord = null;
                        attempt--;
                        continue;
                    }

                    if (!CloudDownloadRecoveryPolicy.ShouldRetry(response.StatusCode))
                    {
                        LogCloudTranslatedError("ATC_Cloud_DownloadHttpFailed", packageId, response.StatusCode, response.Error ?? string.Empty);
                        return false;
                    }
                    if (attempt == maxRetries)
                    {
                        LogCloudTranslatedError("ATC_Cloud_DownloadHttpFailed", packageId, response.StatusCode, response.Error ?? string.Empty);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries)
                    {
                        LogCloudTranslatedError("ATC_Cloud_DownloadRetryFailed", ex.Message);
                        return false;
                    }
                }

                int delayMs = (int)Math.Pow(2, attempt + 1) * 1000 + new System.Random().Next(100, 500);
                await Task.Delay(delayMs);
            }

            if (string.IsNullOrEmpty(downloadedZipPath) || !File.Exists(downloadedZipPath) || new FileInfo(downloadedZipPath).Length == 0)
            {
                TryDeleteCloudTempFile(downloadedZipPath);
                return false;
            }

            try
            {
                if (clearTarget == null)
                {
                    foreach (var m in Verse.ModLister.AllInstalledMods)
                    {
                        if (m.PackageId.ToLower() == packageId.ToLower()) { targetMod = m; break; }
                    }
                }
                if (clearExistingTranslations)
                {
                    ClearOldTranslationFilesForDownload(targetMod, clearTarget, requestRuntimeRefreshAfterClear);
                }

                string packPath = AutoTranslatorScanner.GetLocalPackPath();
                string extractRoot = System.IO.Path.Combine(packPath, "Languages", targetLangFolder);
                System.IO.Directory.CreateDirectory(extractRoot);

                string workspaceDir = System.IO.Path.Combine(packPath, "Upload_Workspace", packageId, targetLangFolder);
                if (System.IO.Directory.Exists(workspaceDir))
                {
                    System.IO.Directory.Delete(workspaceDir, true);
                    AutoTranslatorScanner.NotifyTranslationFilesChanged(workspaceDir);
                }
                System.IO.Directory.CreateDirectory(workspaceDir);

                using (var archive = System.IO.Compression.ZipFile.OpenRead(downloadedZipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        string destPath = GetSafeCloudExtractPath(extractRoot, entry.FullName);
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destPath));
                        entry.ExtractToFile(destPath, true);
                        AutoTranslatorScanner.NotifyTranslationFileChanged(destPath);

                        string wsDestPath = GetSafeCloudExtractPath(workspaceDir, entry.FullName);
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(wsDestPath));
                        entry.ExtractToFile(wsDestPath, true);
                        AutoTranslatorScanner.NotifyTranslationFileChanged(wsDestPath);
                    }
                }
                TryDeleteCloudTempFile(downloadedZipPath);

                if (resolvedRecord != null)
                {
                    var meta = new LocalModMeta
                    {
                        OriginalRecordId = resolvedRecord.RecordId,
                        TargetModVersion = resolvedRecord.TargetModVersion ?? "Unknown",
                        TranslationDate = resolvedRecord.TranslationDate,
                        IsSmartMerged = resolvedRecord.IsSmartMerged,
                        MergedAiCount = resolvedRecord.MergedAiCount
                    };
                    string cleanPackageId = packageId.Replace(".", "_").ToLower();
                    string metaPath = System.IO.Path.Combine(extractRoot, $"{cleanPackageId}_ATC_Meta.json");
                    System.IO.File.WriteAllText(metaPath, JsonConvert.SerializeObject(meta, Newtonsoft.Json.Formatting.Indented));
                }

                AutoTranslatorScanner.NotifyTranslationFilesChanged(extractRoot);
                AutoTranslatorScanner.NotifyTranslationFilesChanged(workspaceDir);

                if (requestMemoryDrop)
                {
                    AutoTranslatorLegacyRepairer.RepairPackage(packageId, targetLangFolder, requestMemoryDrop: false);
                    AutoTranslatorScanner.RequestMemoryDrop();
                }
                return true;
            }
            catch (Exception ex)
            {
                RollbackFailedCloudDownload(targetMod, clearTarget, requestRuntimeRefreshAfterClear, restoreBackupOnFailure);
                TryDeleteCloudTempFile(downloadedZipPath);

                ATC_Dispatcher.RunOnMainThread(() => AutoTranslatorSettings.AddErrorLog(
                    AutoTranslatorAPI.TranslateText("ATC_LogError_DownloadCorrupted", packageId, ex.Message)));
                return false;
            }
        }

        private static async Task<CloudDownloadAttemptResult> DownloadCloudArchiveAttemptAsync(
            string url,
            string tempFilePath,
            int timeoutSeconds)
        {
            var tcs = new TaskCompletionSource<CloudDownloadAttemptResult>();
            ATC_Dispatcher.RunOnMainThread(() =>
            {
                try
                {
                    TryDeleteCloudTempFile(tempFilePath);
                    var request = new UnityWebRequest(url, "GET")
                    {
                        timeout = timeoutSeconds,
                        downloadHandler = new DownloadHandlerFile(tempFilePath) { removeFileOnAbort = true }
                    };
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                    operation.completed += _ =>
                    {
                        try
                        {
                            tcs.TrySetResult(new CloudDownloadAttemptResult
                            {
                                Success = UnityWebRequestCompat.IsSuccess(request),
                                StatusCode = request.responseCode,
                                Error = request.error,
                                FilePath = tempFilePath,
                                ServedRecordId = request.GetResponseHeader("X-ATC-Record-Id"),
                                FallbackReason = request.GetResponseHeader("X-ATC-Record-Fallback")
                            });
                        }
                        catch (Exception innerEx)
                        {
                            tcs.TrySetException(innerEx);
                        }
                        finally
                        {
                            request.Dispose();
                        }
                    };
                }
                catch (Exception dispatchEx)
                {
                    tcs.TrySetException(dispatchEx);
                }
            });
            return await WaitForCloudTask(tcs.Task, timeoutSeconds + 10, "cloud download");
        }

        private static CloudModRecord FindCloudRecordById(string recordId)
        {
            if (string.IsNullOrWhiteSpace(recordId)) return null;
            return AutoTranslatorSettings.CloudRegistry.FirstOrDefault(record =>
                record != null && string.Equals(record.RecordId, recordId, StringComparison.OrdinalIgnoreCase));
        }

        private static void TryDeleteCloudTempFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static void RollbackFailedCloudDownload(ModMetaData targetMod, AutoTranslatorScanner.LocalTranslationDeleteTarget clearTarget, bool requestRuntimeRefresh, bool restoreBackup)
        {
            string packageId = clearTarget != null && !string.IsNullOrWhiteSpace(clearTarget.PackageId)
                ? clearTarget.PackageId
                : targetMod?.PackageId;
            if (string.IsNullOrWhiteSpace(packageId)) return;

            AutoTranslatorScanner.DeleteLocalTranslationFiles(
                new List<AutoTranslatorScanner.LocalTranslationDeleteTarget>
                {
                    new AutoTranslatorScanner.LocalTranslationDeleteTarget
                    {
                        PackageId = packageId,
                        ModName = clearTarget?.ModName ?? targetMod?.Name ?? packageId
                    }
                },
                createBackup: false,
                requestRuntimeRefresh: false,
                logResult: false);

            int restored = restoreBackup
                ? AutoTranslatorScanner.RestoreLatestBackups(
                    new List<AutoTranslatorScanner.LocalTranslationRestoreTarget>
                    {
                        new AutoTranslatorScanner.LocalTranslationRestoreTarget { PackageId = packageId }
                    })
                : 0;

            if (restored == 0 && requestRuntimeRefresh)
            {
                AutoTranslatorScanner.RequestMemoryDropForPackage(packageId);
            }
        }

        private static string GetSafeCloudExtractPath(string root, string entryName)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(entryName))
                throw new InvalidDataException("Cloud archive contains an empty path.");

            string normalizedEntry = entryName
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedEntry))
                throw new InvalidDataException("Cloud archive contains an absolute path.");

            string safeRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string destination = Path.GetFullPath(Path.Combine(safeRoot, normalizedEntry));
            if (!destination.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cloud archive contains a path outside the translation folder.");

            return destination;
        }

        private static void ClearOldTranslationFilesForDownload(ModMetaData targetMod, AutoTranslatorScanner.LocalTranslationDeleteTarget clearTarget, bool requestRuntimeRefresh)
        {
            if (clearTarget != null && !string.IsNullOrWhiteSpace(clearTarget.PackageId))
            {
                AutoTranslatorScanner.LocalTranslationDeleteResult result = AutoTranslatorScanner.DeleteLocalTranslationFiles(
                    new List<AutoTranslatorScanner.LocalTranslationDeleteTarget> { clearTarget },
                    createBackup: true,
                    requestRuntimeRefresh: requestRuntimeRefresh,
                    logResult: false);

                if (result.DeletedFiles > 0)
                {
                    AutoTranslatorSettings.AddLog(AutoTranslatorAPI.TranslateText("ATC_ClearCacheSuccess", result.DeletedFiles));
                    Log.Message($"[AutoTranslationCore] Auto-cleared {result.DeletedFiles} old files for updated mods (Backup created).");
                }

                if (result.HasErrors)
                {
                    AutoTranslatorSettings.AddErrorLog($"Auto Clear Error: {result.FirstError}");
                }
                return;
            }

            if (targetMod != null)
            {
                AutoTranslatorScanner.ClearOldTranslationFiles(new List<Verse.ModMetaData> { targetMod }, requestRuntimeRefresh: requestRuntimeRefresh);
            }
        }

        public static async Task<int> SyncAppliedCorrectionsOverlayAsync(string packageId, string targetLangFolder)
        {
            string packPath = AutoTranslatorScanner.GetLocalPackPath();
            string extractRoot = Path.Combine(packPath, "Languages", targetLangFolder);
            string workspaceDir = Path.Combine(packPath, "Upload_Workspace", packageId, targetLangFolder);
            return await SyncAppliedCorrectionsOverlayAsync(packageId, targetLangFolder, extractRoot, workspaceDir);
        }

        private static async Task<int> SyncAppliedCorrectionsOverlayAsync(string packageId, string targetLangFolder, string extractRoot, string workspaceDir)
        {
            List<AppliedTranslationCorrection> corrections = await FetchAppliedCorrectionsAsync(packageId, targetLangFolder);
            if (corrections == null) return 0;
            WriteAppliedCorrectionsOverlay(packageId, corrections, extractRoot);
            WriteAppliedCorrectionsOverlay(packageId, corrections, workspaceDir);
            return corrections.Count;
        }

        public static int ApplySingleCorrectionOverlay(string packageId, AppliedTranslationCorrection correction, string targetLangFolder)
        {
            if (correction == null || string.IsNullOrWhiteSpace(targetLangFolder)) return 0;
            if (!string.Equals(correction.Status, "applied", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.IsNullOrWhiteSpace(correction.EntryKey) || string.IsNullOrWhiteSpace(correction.ProposedTranslation)) return 0;

            string packPath = AutoTranslatorScanner.GetLocalPackPath();
            string extractRoot = Path.Combine(packPath, "Languages", targetLangFolder);
            return UpsertSingleCorrectionOverlay(packageId, correction, extractRoot) > 0 ? 1 : 0;
        }

        public static int RemoveSingleCorrectionOverlay(string packageId, AppliedTranslationCorrection correction, string targetLangFolder)
        {
            if (correction == null || string.IsNullOrWhiteSpace(targetLangFolder)) return 0;
            if (string.IsNullOrWhiteSpace(correction.EntryKey)) return 0;

            string packPath = AutoTranslatorScanner.GetLocalPackPath();
            string extractRoot = Path.Combine(packPath, "Languages", targetLangFolder);
            return RemoveSingleCorrectionOverlayFromRoot(packageId, correction, extractRoot) > 0 ? 1 : 0;
        }

        public static bool IsSingleCorrectionOverlayApplied(string packageId, AppliedTranslationCorrection correction, string targetLangFolder)
        {
            if (correction == null || string.IsNullOrWhiteSpace(targetLangFolder)) return false;
            if (string.IsNullOrWhiteSpace(correction.EntryKey)) return false;

            string packPath = AutoTranslatorScanner.GetLocalPackPath();
            string extractRoot = Path.Combine(packPath, "Languages", targetLangFolder);
            string path = GetSingleCorrectionOverlayPath(packageId, correction, extractRoot);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            Dictionary<string, string> data = AutoTranslatorScanner.LoadXmlFileToDict(path);
            return data.TryGetValue(correction.EntryKey.Trim(), out string value) &&
                   string.Equals(value, correction.ProposedTranslation ?? "", StringComparison.Ordinal);
        }

        private static int UpsertSingleCorrectionOverlay(string packageId, AppliedTranslationCorrection correction, string languageRoot)
        {
            string path = GetSingleCorrectionOverlayPath(packageId, correction, languageRoot);
            if (string.IsNullOrWhiteSpace(path)) return 0;

            Dictionary<string, string> data = File.Exists(path)
                ? AutoTranslatorScanner.LoadXmlFileToDict(path)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string key = correction.EntryKey.Trim();
            string value = correction.ProposedTranslation ?? "";
            data[key] = value;
            AutoTranslatorScanner.SaveXml(path, data);
            return 1;
        }

        private static int RemoveSingleCorrectionOverlayFromRoot(string packageId, AppliedTranslationCorrection correction, string languageRoot)
        {
            string path = GetSingleCorrectionOverlayPath(packageId, correction, languageRoot);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;

            Dictionary<string, string> data = AutoTranslatorScanner.LoadXmlFileToDict(path);
            string key = correction.EntryKey.Trim();
            if (!data.Remove(key)) return 0;

            if (data.Count == 0)
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                AutoTranslatorScanner.NotifyTranslationFileChanged(path);
            }
            else
            {
                AutoTranslatorScanner.SaveXml(path, data);
            }
            return 1;
        }

        private static string GetSingleCorrectionOverlayPath(string packageId, AppliedTranslationCorrection correction, string languageRoot)
        {
            if (string.IsNullOrWhiteSpace(languageRoot) || correction == null) return "";

            string cleanPackageId = MakeOverlaySafePackageId(packageId);
            if (string.Equals(correction.ScopeType, "Keyed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(correction.EntryType, "Keyed", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(languageRoot, "Keyed", $"{cleanPackageId}_CloudCorrections.xml");
            }

            string defType = string.IsNullOrWhiteSpace(correction.EntryType)
                ? "General"
                : correction.EntryType.Trim();
            string safeDefType = MakeOverlaySafePathSegment(defType);
            if (string.IsNullOrWhiteSpace(safeDefType)) safeDefType = "General";
            return Path.Combine(languageRoot, "DefInjected", safeDefType, $"{cleanPackageId}_CloudCorrections.xml");
        }

        private static void WriteAppliedCorrectionsOverlay(string packageId, List<AppliedTranslationCorrection> corrections, string languageRoot)
        {
            if (string.IsNullOrWhiteSpace(languageRoot)) return;

            string cleanPackageId = MakeOverlaySafePackageId(packageId);
            DeleteOldCorrectionOverlayFiles(languageRoot, cleanPackageId);

            if (corrections == null || corrections.Count == 0)
                return;

            Dictionary<string, string> keyed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, string>> defsByType =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (AppliedTranslationCorrection correction in corrections)
            {
                if (correction == null || !string.Equals(correction.Status, "applied", StringComparison.OrdinalIgnoreCase))
                    continue;

                string key = (correction.EntryKey ?? "").Trim();
                string value = correction.ProposedTranslation ?? "";
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    continue;

                if (string.Equals(correction.ScopeType, "Keyed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(correction.EntryType, "Keyed", StringComparison.OrdinalIgnoreCase))
                {
                    keyed[key] = value;
                    continue;
                }

                string defType = string.IsNullOrWhiteSpace(correction.EntryType)
                    ? "General"
                    : correction.EntryType.Trim();
                if (!defsByType.TryGetValue(defType, out Dictionary<string, string> defs))
                {
                    defs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    defsByType[defType] = defs;
                }
                defs[key] = value;
            }

            if (keyed.Count > 0)
            {
                string keyedPath = Path.Combine(languageRoot, "Keyed", $"{cleanPackageId}_CloudCorrections.xml");
                AutoTranslatorScanner.SaveXml(keyedPath, keyed);
            }

            foreach (var pair in defsByType)
            {
                if (pair.Value == null || pair.Value.Count == 0) continue;
                string safeDefType = MakeOverlaySafePathSegment(pair.Key);
                if (string.IsNullOrWhiteSpace(safeDefType)) safeDefType = "General";
                string defPath = Path.Combine(languageRoot, "DefInjected", safeDefType, $"{cleanPackageId}_CloudCorrections.xml");
                AutoTranslatorScanner.SaveXml(defPath, pair.Value);
            }
        }

        private static void DeleteOldCorrectionOverlayFiles(string languageRoot, string cleanPackageId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(languageRoot) || !Directory.Exists(languageRoot)) return;
                string fileName = $"{cleanPackageId}_CloudCorrections.xml";
                foreach (string file in Directory.GetFiles(languageRoot, fileName, SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    AutoTranslatorScanner.NotifyTranslationFileChanged(file);
                }
            }
            catch (Exception ex)
            {
                Verse.Log.Warning($"[ATC Cloud] Failed to clear old correction overlay: {ex.Message}");
            }
        }

        private static string MakeOverlaySafePackageId(string packageId)
        {
            string safe = (packageId ?? "unknown").Replace(".", "_").ToLowerInvariant();
            return MakeOverlaySafePathSegment(safe);
        }

        private static string MakeOverlaySafePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string safe = new string(value.Select(ch =>
                char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray());
            while (safe.Contains("__")) safe = safe.Replace("__", "_");
            return safe.Trim('_');
        }

    }
}
