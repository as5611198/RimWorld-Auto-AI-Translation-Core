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
            string stagingDir = null, pendingZip = null;
            try
            {
                string packPath = GetLocalPackPath();
                string backupRoot = Path.Combine(packPath, "Backups", packageId);
                Directory.CreateDirectory(backupRoot);


                stagingDir = Path.Combine(Path.GetTempPath(), "ATC_Backup_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingDir);

                string langsPath = Path.Combine(packPath, "Languages");


                foreach (string file in filesToBackup)
                {
                    string root = Path.GetFullPath(langsPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    string absolute = Path.GetFullPath(file);
                    if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unowned backup input.");
                    TranslationFileTransaction.RejectReparsePoints(absolute);
                    string relPath = absolute.Substring(root.Length);
                    string destPath = Path.Combine(stagingDir, relPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                    File.Copy(file, destPath, true);
                }


                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff") + "_" + Guid.NewGuid().ToString("N");
                string zipPath = Path.Combine(backupRoot, $"{packageId}_{timestamp}.zip");
                pendingZip = zipPath + ".pending";

                var manifest = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories).ToDictionary(
                    path => path.Substring(stagingDir.Length).TrimStart('\\', '/').Replace('\\', '/'), TranslationFileTransaction.FileHash, StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(Path.Combine(stagingDir, "ATC_BackupManifest.json"), JsonConvert.SerializeObject(new { PackageId = packageId, Files = manifest }));
                System.IO.Compression.ZipFile.CreateFromDirectory(stagingDir, pendingZip);
                using (var verify = ZipFile.OpenRead(pendingZip))
                    foreach (var entry in verify.Entries.Where(e => e.FullName != "ATC_BackupManifest.json"))
                    {
                        using (var input = entry.Open())
                        using (var bytes = new MemoryStream())
                        {
                            input.CopyTo(bytes);
                            if (!manifest.TryGetValue(entry.FullName.Replace('\\', '/'), out string expected) || TranslationFileTransaction.Hash(bytes.ToArray()) != expected)
                                throw new IOException("Backup verification failed.");
                        }
                    }
                File.Move(pendingZip, zipPath);


                // Transaction backups are immutable. Retention is an explicit maintenance
                // operation, never part of a concurrent destructive update.

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
            finally
            {
                try { if (stagingDir != null && Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch (IOException) { }
                try { if (pendingZip != null && File.Exists(pendingZip)) File.Delete(pendingZip); } catch (IOException) { }
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
                        ? indexedFiles.Where(file => IsConfirmedPackageOutput(file, packageId, langsPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        : new List<string>();
                    var expectedFiles = filesToDelete.Where(File.Exists).ToDictionary(f => f, TranslationFileTransaction.FileHash, StringComparer.OrdinalIgnoreCase);

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
                        else
                        {
                            result.Errors.Add(modName + ": required backup failed; originals retained.");
                            continue;
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
                            TranslationXmlAtomicFileStore.DeleteIfCurrent(file, () =>
                                expectedFiles.TryGetValue(file, out string expected) && TranslationFileTransaction.FileHash(file) == expected);
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

        private static bool IsConfirmedPackageOutput(string file, string packageId, string languagesRoot)
        {
            string name = Path.GetFileName(file), clean = TranslationGeneratedOutputOwnership.GetCleanPackageId(packageId);
            string relative = Path.GetFullPath(file).Substring(Path.GetFullPath(languagesRoot).TrimEnd('\\', '/').Length).TrimStart('\\', '/');
            string folder = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            string languageRoot = Path.Combine(languagesRoot, folder);
            string claim = Path.Combine(languageRoot, "ATC_Ownership", clean + ".json");
            if (File.Exists(claim) && !string.Equals(JsonConvert.DeserializeObject<string>(File.ReadAllText(claim)), packageId, StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals(clean + "_AutoTranslated.xml", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(clean + "_CloudCorrections.xml", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(clean + "_ManualTranslation.xml", StringComparison.OrdinalIgnoreCase)) return true;
            string provenancePath = GetProvenancePath(languageRoot, packageId);
            if (!File.Exists(provenancePath)) return false;
            var index = LoadProvenanceIndex(languageRoot, packageId);
            string sourceRelative = GetRelativeTranslationPath(languageRoot, file);
            return index.Keys.Any(k => k.StartsWith(sourceRelative + "|", StringComparison.OrdinalIgnoreCase));
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
            int cancellation = AutoTranslatorSettings.PipelineCancellationGeneration;

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

                    var transaction = new TranslationFileTransaction(Path.Combine(modBackupDir, "Restore"));
                    var restoredPaths = new List<string>();
                    using (var archive = System.IO.Compression.ZipFile.OpenRead(latest.FullName))
                    {
                        if (archive.Entries.Count > 10000) throw new InvalidDataException("Backup entry limit exceeded.");
                        var metadata = archive.GetEntry("ATC_BackupManifest.json");
                        if (archive.Entries.Count(e => e.FullName == "ATC_BackupManifest.json") > 1 || metadata?.Length > 4 * 1024 * 1024)
                            throw new InvalidDataException("Invalid backup manifest.");
                        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        long totalBytes = 0;
                        Newtonsoft.Json.Linq.JObject manifest = null;
                        if (metadata != null)
                            using (var reader = new StreamReader(metadata.Open())) manifest = Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd());
                        if (manifest != null && (string)manifest["PackageId"] != target.PackageId) throw new InvalidDataException("Backup package mismatch.");
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name) || entry == metadata) continue;
                            if (entry.Length > 32 * 1024 * 1024 || (totalBytes += entry.Length) > 256 * 1024 * 1024) throw new InvalidDataException("Backup size limit exceeded.");

                            string destPath = Path.GetFullPath(Path.Combine(langsPath, entry.FullName));
                            string safeRoot = Path.GetFullPath(langsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            if (Path.IsPathRooted(entry.FullName)) throw new InvalidDataException("Absolute backup path.");
                            if (!destPath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException("Backup path escapes Languages.");
                            }
                            if (!destinations.Add(destPath)) throw new InvalidDataException("Duplicate backup destination.");
                            byte[] bytes;
                            using (var input = entry.Open())
                            using (var buffer = new MemoryStream()) { input.CopyTo(buffer); bytes = buffer.ToArray(); }
                            string relative = entry.FullName.Replace('\\', '/');
                            if (manifest != null)
                            {
                                if ((string)manifest["Files"]?[relative] != TranslationFileTransaction.Hash(bytes)) throw new InvalidDataException("Backup checksum mismatch.");
                            }
                            else if (!entry.Name.Equals(TranslationGeneratedOutputOwnership.GetCanonicalFileName(target.PackageId), StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Legacy backup ownership cannot be established.");
                            transaction.Stage(destPath, bytes);
                            restoredPaths.Add(destPath);
                        }
                        if (manifest != null && manifest["Files"]?.Count() != restoredPaths.Count) throw new InvalidDataException("Incomplete backup manifest.");
                    }

                    transaction.Commit(() => !AutoTranslatorSettings.IsCancellationRequested &&
                        cancellation == AutoTranslatorSettings.PipelineCancellationGeneration);
                    foreach (string path in restoredPaths) NotifyTranslationFileChanged(path);
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
