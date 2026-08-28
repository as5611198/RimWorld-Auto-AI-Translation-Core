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
// 這個檔案負責翻譯包備份、還原與舊檔清理。
// EN: This file creates backups, restores files, and clears old translation output.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器掃描器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorScanner.
    public static partial class AutoTranslatorScanner
    {
        public class LocalTranslationDeleteResult
        {
            public int RequestedMods;
            public int DeletedFiles;
            public int FailedFiles;
            public List<string> Errors = new List<string>();

            public bool HasErrors
            {
                get { return FailedFiles > 0 || Errors.Count > 0; }
            }

            public string FirstError
            {
                get { return Errors.Count > 0 ? Errors[0] : ""; }
            }
        }

        public class LocalTranslationDeleteTarget
        {
            public string PackageId;
            public string ModName;
        }

        public class LocalTranslationRestoreTarget
        {
            public string PackageId;
        }


        // 這個方法負責處理 UpdateLocal模組Meta 相關流程。
        // EN: This method handles update local mod meta.
        public static void UpdateLocalModMeta(string packageId, string targetLangFolder, int newAiCount)
        {
            try
            {
                string packPath = GetLocalPackPath();
                string extractRoot = Path.Combine(packPath, "Languages", targetLangFolder);
                Directory.CreateDirectory(extractRoot);

                string cleanPackageId = packageId.Replace(".", "_").ToLower();
                string metaPath = Path.Combine(extractRoot, $"{cleanPackageId}_ATC_Meta.json");

                LocalModMeta meta = new LocalModMeta
                {
                    OriginalRecordId = "",
                    TargetModVersion = RimWorld.VersionControl.CurrentVersionStringWithoutBuild,
                    TranslationDate = DateTime.UtcNow,
                    IsSmartMerged = false,
                    MergedAiCount = 0
                };

                bool hasCloudIdentity = false;

                if (File.Exists(metaPath))
                {
                    try
                    {
                        var existing = JsonConvert.DeserializeObject<LocalModMeta>(File.ReadAllText(metaPath));
                        if (existing != null)
                        {
                            meta = existing;
                            if (!string.IsNullOrEmpty(meta.OriginalRecordId)) hasCloudIdentity = true;
                        }
                    }
                    catch { }
                }


                if (hasCloudIdentity)
                {
                    meta.IsSmartMerged = true;
                    meta.MergedAiCount += newAiCount;
                }
                else
                {

                    meta.IsSmartMerged = false;
                    meta.MergedAiCount = 0;
                }

                meta.TargetModVersion = RimWorld.VersionControl.CurrentVersionStringWithoutBuild;

                File.WriteAllText(metaPath, JsonConvert.SerializeObject(meta, Newtonsoft.Json.Formatting.Indented));
                AutoTranslatorSettings.AddLog("📝 " + AutoTranslatorAPI.TranslateText("ATC_Log_MetaUpdated", newAiCount));


            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] UpdateLocalModMeta error: {ex.Message}");
            }
        }


        // 這個方法負責建立 備份BeforeClear 物件或檔案。
        // EN: This method creates backup before clear.
        private static bool CreateBackupBeforeClear(ModMetaData mod, List<string> filesToBackup, bool logSuccess = true)
        {
            if (mod == null) return false;
            return CreateBackupBeforeClear(mod.PackageId, mod.Name, filesToBackup, logSuccess);
        }

        private static bool CreateBackupBeforeClear(string packageId, string modName, List<string> filesToBackup, bool logSuccess = true)
        {
            if (filesToBackup == null || filesToBackup.Count == 0) return false;
            if (string.IsNullOrWhiteSpace(packageId)) return false;
            try
            {
                string packPath = GetLocalPackPath();
                string backupRoot = Path.Combine(packPath, "Backups", packageId);
                Directory.CreateDirectory(backupRoot);


                string stagingDir = Path.Combine(Path.GetTempPath(), "ATC_Backup_" + packageId);
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
                Directory.CreateDirectory(stagingDir);

                string langsPath = Path.Combine(packPath, "Languages");


                foreach (string file in filesToBackup)
                {
                    string relPath = file.Substring(langsPath.Length).TrimStart('\\', '/');
                    string destPath = Path.Combine(stagingDir, relPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                    File.Copy(file, destPath, true);
                }


                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string zipPath = Path.Combine(backupRoot, $"{packageId}_{timestamp}.zip");

                if (File.Exists(zipPath)) File.Delete(zipPath);
                System.IO.Compression.ZipFile.CreateFromDirectory(stagingDir, zipPath);
                Directory.Delete(stagingDir, true);


                var zipFiles = new DirectoryInfo(backupRoot).GetFiles("*.zip")
                                .OrderByDescending(f => f.CreationTimeUtc).ToList();

                for (int i = 3; i < zipFiles.Count; i++)
                {
                    zipFiles[i].Delete();
                }

                if (logSuccess)
                {
                    AutoTranslatorSettings.AddLog("📦 " + AutoTranslatorAPI.TranslateText("ATC_Log_BackupSuccess", string.IsNullOrWhiteSpace(modName) ? packageId : modName));
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"[ATC Backup] Failed to backup {packageId}: {ex.Message}");
                return false;
            }
        }


        // 這個方法負責清除 Old翻譯Files 資料。
        // EN: This method clears old translation files.
        public static void ClearOldTranslationFiles(List<ModMetaData> modsToClear, bool requestRuntimeRefresh = true)
        {
            LocalTranslationDeleteResult result = DeleteLocalTranslationFiles(
                modsToClear,
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
        }

        public static LocalTranslationDeleteResult DeleteLocalTranslationFiles(
            List<ModMetaData> modsToDelete,
            bool createBackup = true,
            bool requestRuntimeRefresh = true,
            bool logResult = true)
        {
            List<LocalTranslationDeleteTarget> targets = (modsToDelete ?? new List<ModMetaData>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.PackageId))
                .Select(m => new LocalTranslationDeleteTarget
                {
                    PackageId = m.PackageId,
                    ModName = m.Name
                })
                .ToList();

            return DeleteLocalTranslationFiles(
                targets,
                createBackup,
                requestRuntimeRefresh,
                logResult);
        }

        public static LocalTranslationDeleteResult DeleteLocalTranslationFiles(
            List<LocalTranslationDeleteTarget> targetsToDelete,
            bool createBackup = true,
            bool requestRuntimeRefresh = true,
            bool logResult = true)
        {
            var result = new LocalTranslationDeleteResult();
            if (targetsToDelete == null || targetsToDelete.Count == 0) return result;

            result.RequestedMods = targetsToDelete.Count(m => m != null && !string.IsNullOrWhiteSpace(m.PackageId));

            try
            {
                string packPath = GetLocalPackPath();
                string langsPath = Path.Combine(packPath, "Languages");
                bool langsPathExists = Directory.Exists(langsPath);

                string targetFolder = AutoTranslatorMod.Settings != null
                    ? GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang)
                    : null;

                var allXmls = langsPathExists
                    ? GetXmlFilesCached(langsPath, SearchOption.AllDirectories)
                    : new List<string>();
                List<string> requestedPackageIds = targetsToDelete
                    .Where(target => target != null && !string.IsNullOrWhiteSpace(target.PackageId))
                    .Select(target => target.PackageId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Dictionary<string, List<string>> filesByPackage =
                    TranslationLoadOrderPolicy.GroupFilesByPackage(allXmls, requestedPackageIds);
                var deletedPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool touchedSettings = false;
                int backupCount = 0;
                bool useCondensedBackupLog = requestedPackageIds.Count > 8;

                foreach (var target in targetsToDelete)
                {
                    if (target == null || string.IsNullOrWhiteSpace(target.PackageId)) continue;

                    string packageId = target.PackageId;
                    string modName = string.IsNullOrWhiteSpace(target.ModName) ? packageId : target.ModName;
                    List<string> filesToDelete = filesByPackage.TryGetValue(packageId, out List<string> indexedFiles)
                        ? indexedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        : new List<string>();

                    if (filesToDelete.Count > 0 && createBackup)
                    {
                        if (CreateBackupBeforeClear(
                            packageId,
                            modName,
                            filesToDelete.Where(File.Exists).ToList(),
                            logSuccess: !useCondensedBackupLog))
                        {
                            backupCount++;
                        }
                    }

                    var clearKeysByDefType = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                    foreach (string file in filesToDelete)
                    {
                        if (string.IsNullOrEmpty(file) || deletedPathSet.Contains(file)) continue;

                        try
                        {
                            if (!File.Exists(file))
                            {
                                deletedPathSet.Add(file);
                                NotifyTranslationFileChanged(file);
                                continue;
                            }

                            Dictionary<string, HashSet<string>> fileClearKeys = requestRuntimeRefresh
                                ? CollectRuntimeClearKeysForTranslationFile(langsPath, file, targetFolder)
                                : null;
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                            deletedPathSet.Add(file);
                            NotifyTranslationFileChanged(file);
                            if (fileClearKeys != null) MergeClearKeys(clearKeysByDefType, fileClearKeys);
                            result.DeletedFiles++;
                        }
                        catch (FileNotFoundException)
                        {
                            deletedPathSet.Add(file);
                            NotifyTranslationFileChanged(file);
                        }
                        catch (DirectoryNotFoundException)
                        {
                            deletedPathSet.Add(file);
                            NotifyTranslationFileChanged(file);
                        }
                        catch (Exception ex)
                        {
                            result.FailedFiles++;
                            result.Errors.Add($"{modName}: {Path.GetFileName(file)} - {ex.Message}");
                            Log.Warning($"[AutoTranslationCore] Failed to delete local translation file {file}: {ex}");
                        }
                    }

                    touchedSettings |= ClearVerificationStateForPackage(packageId);

                    if (requestRuntimeRefresh && clearKeysByDefType.Count > 0)
                    {
                        RequestMemoryDropForPackage(packageId, clearKeysByDefType);
                    }
                    else if (requestRuntimeRefresh && filesToDelete.Count > 0)
                    {
                        RequestMemoryDropForPackage(packageId);
                    }
                }

                if (useCondensedBackupLog && backupCount > 0)
                {
                    AutoTranslatorSettings.AddLog("📦 " + AutoTranslatorAPI.TranslateText("ATC_Log_BatchBackupSuccess", backupCount));
                }

                if (result.DeletedFiles > 0 || touchedSettings)
                {
                    if (langsPathExists)
                    {
                        NotifyTranslationFilesChanged(langsPath);
                    }
                    else
                    {
                        NotifyTranslationFilesChanged(null);
                    }

                    QueueLocalTranslationDeleteRefresh(touchedSettings);
                }

                if (logResult && result.DeletedFiles > 0)
                {
                    string logMsg = AutoTranslatorAPI.TranslateText("ATC_Log_DeleteTransSuccess", result.RequestedMods, result.DeletedFiles);
                    AutoTranslatorSettings.AddLog(logMsg);
                    Log.Message($"[AutoTranslationCore] {logMsg}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.Message);
                AutoTranslatorSettings.AddErrorLog(AutoTranslatorAPI.TranslateText("ATC_Message_DeleteTransError", ex.Message));
                Log.Warning($"[AutoTranslationCore] Delete local translations failed: {ex}");
            }

            return result;
        }

        private static bool IsLocalTranslationFileForPackage(string file, string id1, string id2)
        {
            if (string.IsNullOrEmpty(file)) return false;
            if (file.IndexOf("Upload_Workspace", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            string fileName = Path.GetFileName(file).ToLowerInvariant();
            return fileName.StartsWith(id1 + "_") ||
                   fileName.StartsWith(id1 + ".") ||
                   fileName.StartsWith(id2 + "_") ||
                   fileName.StartsWith(id2 + ".");
        }

        private static Dictionary<string, HashSet<string>> CollectRuntimeClearKeysForTranslationFile(string langsPath, string file, string targetFolder)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(targetFolder)) return result;

            string bucket;
            if (!TryGetRuntimeClearBucket(langsPath, file, targetFolder, out bucket)) return result;

            Dictionary<string, string> dict = LoadXmlFileToDict(file);
            foreach (string key in dict.Keys)
            {
                AddClearKey(result, bucket, key);
            }

            return result;
        }

        private static bool TryGetRuntimeClearBucket(string langsPath, string file, string targetFolder, out string bucket)
        {
            bucket = null;
            if (string.IsNullOrEmpty(langsPath) || string.IsNullOrEmpty(file) || string.IsNullOrEmpty(targetFolder)) return false;

            string root = Path.GetFullPath(langsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(file);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

            string relative = fullPath.Substring(root.Length);
            string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!IsLanguageFolderMatch(parts[0], targetFolder)) return false;

            if (parts[1].Equals("Keyed", StringComparison.OrdinalIgnoreCase))
            {
                bucket = "Keyed";
                return true;
            }

            if (parts[1].Equals("DefInjected", StringComparison.OrdinalIgnoreCase))
            {
                bucket = parts.Length >= 4 ? parts[2] : "General";
                return true;
            }

            return false;
        }

        private static void AddClearKey(Dictionary<string, HashSet<string>> target, string bucket, string key)
        {
            if (target == null || string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(key)) return;

            HashSet<string> keys;
            if (!target.TryGetValue(bucket, out keys))
            {
                keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                target[bucket] = keys;
            }

            keys.Add(key);
        }

        private static void MergeClearKeys(Dictionary<string, HashSet<string>> target, Dictionary<string, HashSet<string>> source)
        {
            if (target == null || source == null || source.Count == 0) return;

            foreach (var pair in source)
            {
                if (pair.Value == null) continue;
                foreach (string key in pair.Value)
                {
                    AddClearKey(target, pair.Key, key);
                }
            }
        }

        private static bool ClearVerificationStateForPackage(string packageId)
        {
            if (AutoTranslatorMod.Settings == null || string.IsNullOrWhiteSpace(packageId)) return false;

            bool changed = false;
            changed |= RemovePackageSettingEntry(AutoTranslatorMod.Settings.ModLastVerifiedTimes, packageId);
            changed |= RemovePackageSettingEntry(AutoTranslatorMod.Settings.ModLastVerifiedFingerprints, packageId);
            return changed;
        }

        private static bool RemovePackageSettingEntry<T>(Dictionary<string, T> dict, string packageId)
        {
            if (dict == null || string.IsNullOrWhiteSpace(packageId)) return false;

            if (dict.Remove(packageId)) return true;

            string existingKey = dict.Keys.FirstOrDefault(key => string.Equals(key, packageId, StringComparison.OrdinalIgnoreCase));
            if (existingKey == null) return false;

            dict.Remove(existingKey);
            return true;
        }

        private static void QueueLocalTranslationDeleteRefresh(bool writeSettings)
        {
            ATC_Dispatcher.RunOnMainThread(() =>
            {
                try
                {
                    if (writeSettings)
                    {
                        AutoTranslatorMod mod = LoadedModManager.GetMod<AutoTranslatorMod>();
                        if (mod != null) mod.WriteSettings();
                    }

                    ModUpdateDetector.ClearStatusCache();
                    TranslationWorkbenchTab.RequestRefresh();
                    UIInterceptor.RefreshRuntimeUICache();
                }
                catch (Exception ex)
                {
                    Log.Warning($"[AutoTranslationCore] Local translation delete refresh failed: {ex.Message}");
                }
            });
        }


        // 這個方法負責處理 RestoreLatest備份 相關流程。
        // EN: This method handles restore latest backups.
        public static int RestoreLatestBackups(List<ModMetaData> modsToRestore)
        {
            List<LocalTranslationRestoreTarget> targets = (modsToRestore ?? new List<ModMetaData>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.PackageId))
                .Select(m => new LocalTranslationRestoreTarget
                {
                    PackageId = m.PackageId
                })
                .ToList();

            return RestoreLatestBackups(targets);
        }

        public static int RestoreLatestBackups(List<LocalTranslationRestoreTarget> targetsToRestore)
        {
            if (targetsToRestore == null || targetsToRestore.Count == 0) return 0;

            string packPath = GetLocalPackPath();
            string backupRoot = Path.Combine(packPath, "Backups");
            string langsPath = Path.Combine(packPath, "Languages");
            if (!Directory.Exists(backupRoot)) return 0;

            int restored = 0;
            foreach (var target in targetsToRestore)
            {
                if (target == null || string.IsNullOrEmpty(target.PackageId)) continue;

                try
                {
                    string modBackupDir = Path.Combine(backupRoot, target.PackageId);
                    if (!Directory.Exists(modBackupDir)) continue;

                    FileInfo latest = new DirectoryInfo(modBackupDir)
                        .GetFiles("*.zip")
                        .OrderByDescending(f => f.CreationTimeUtc)
                        .FirstOrDefault();

                    if (latest == null) continue;

                    Directory.CreateDirectory(langsPath);
                    using (var archive = System.IO.Compression.ZipFile.OpenRead(latest.FullName))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;

                            string destPath = Path.GetFullPath(Path.Combine(langsPath, entry.FullName));
                            string safeRoot = Path.GetFullPath(langsPath);
                            if (!destPath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                            entry.ExtractToFile(destPath, true);
                            NotifyTranslationFileChanged(destPath);
                        }
                    }

                    restored++;
                }
                catch (Exception ex)
                {
                    Log.Warning($"[ATC Backup] Failed to restore {target.PackageId}: {ex.Message}");
                }
            }

            if (restored > 0)
            {
                NotifyTranslationFilesChanged(langsPath);
                RequestMemoryDrop();
            }

            return restored;
        }
    }
}
