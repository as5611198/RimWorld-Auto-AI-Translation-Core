using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Text;
using System.Xml;
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

            if (AutoTranslatorSettings.IsCancellationRequested) return false;
            var operationToken = AutoTranslatorSettings.PipelineCancellationToken;
            int cancellationGeneration = AutoTranslatorSettings.PipelineCancellationGeneration;
            var startingTarget = AutoTranslatorMod.Settings?.TargetLang;
            Func<bool> canCommit = () => !AutoTranslatorSettings.IsCancellationRequested &&
                cancellationGeneration == AutoTranslatorSettings.PipelineCancellationGeneration &&
                AutoTranslatorMod.Settings?.TargetLang == startingTarget;

            int maxRetries = 4;
            string downloadedZipPath = null;
            CloudModRecord resolvedRecord = targetRecord;
            bool registryRefreshAttempted = false;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (!canCommit()) return false;
                string attemptZipPath = null;
                try
                {
                    string baseUrl = attempt >= 3 ? BackupApiBaseUrl : PrimaryApiBaseUrl;
                    string url = $"{baseUrl}/download/{Uri.EscapeDataString(packageId)}/{Uri.EscapeDataString(targetLangFolder)}";
                    bool usedRecordId = resolvedRecord != null && !string.IsNullOrEmpty(resolvedRecord.RecordId);
                    if (usedRecordId)
                    {
                        url += $"?recordId={Uri.EscapeDataString(resolvedRecord.RecordId)}";
                    }

                    attemptZipPath = Path.Combine(
                        Path.GetTempPath(),
                        "ATC_Cloud_" + Guid.NewGuid().ToString("N") + ".zip");
                    CloudDownloadAttemptResult response = await DownloadCloudArchiveAttemptAsync(
                        url,
                        attemptZipPath,
                        120 + attempt * 60, operationToken);
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
                catch (OperationCanceledException) { return false; }
                catch (Exception ex)
                {
                    if (attempt == maxRetries)
                    {
                        LogCloudTranslatedError("ATC_Cloud_DownloadRetryFailed", ex.Message);
                        return false;
                    }
                }
                finally
                {
                    if (!string.Equals(attemptZipPath, downloadedZipPath, StringComparison.Ordinal))
                        TryDeleteCloudTempFile(attemptZipPath);
                }

                int delayMs = (int)Math.Pow(2, attempt + 1) * 1000 + new System.Random().Next(100, 500);
                for (int remaining = delayMs; remaining > 0; remaining -= 100)
                {
                    if (!canCommit()) return false;
                    await Task.Delay(Math.Min(100, remaining)).ConfigureAwait(false);
                }
            }

            if (string.IsNullOrEmpty(downloadedZipPath) || !File.Exists(downloadedZipPath) || new FileInfo(downloadedZipPath).Length == 0)
            {
                TryDeleteCloudTempFile(downloadedZipPath);
                return false;
            }

            try
            {
                if (!canCommit()) return false;
                // Parsing, hashing, backup and commit stay off the game thread on every version.
                await Task.Run(() => CommitCloudArchive(downloadedZipPath, packageId, targetLangFolder,
                    resolvedRecord, clearExistingTranslations, canCommit)).ConfigureAwait(false);
                if (requestMemoryDrop)
                    ATC_Dispatcher.RunOnMainThread(() => {
                        if (canCommit()) AutoTranslatorScanner.RequestMemoryDropForPackage(packageId);
                    });
                return true; // commit completed; a later cancel does not pretend it was undone
            }
            catch (Exception ex)
            {
                ATC_Dispatcher.RunOnMainThread(() => AutoTranslatorSettings.AddErrorLog(
                    AutoTranslatorAPI.TranslateText("ATC_LogError_DownloadCorrupted", packageId, ex.Message)));
                return false;
            }
            finally { TryDeleteCloudTempFile(downloadedZipPath); }
        }

        private sealed class CloudArchiveManifest
        {
            public int Schema = 1;
            public string PackageId, Language;
            public Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void CanonicalizeCloudArchive(string staging, string packageId)
        {
            foreach (var group in Directory.GetFiles(staging, "*.xml", SearchOption.AllDirectories).GroupBy(Path.GetDirectoryName))
            {
                var merged = new XmlDocument { XmlResolver = null };
                merged.AppendChild(merged.CreateElement("LanguageData"));
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (string file in group.OrderBy(f => f, StringComparer.Ordinal))
                {
                    var document = new XmlDocument { XmlResolver = null };
                    using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
                    foreach (XmlElement element in document.DocumentElement.ChildNodes.OfType<XmlElement>())
                    {
                        string key = TranslationXmlEntryCodec.TryReadReplacement(element, out string replacement, out string _) ? replacement : element.Name;
                        if (!keys.Add(key)) throw new InvalidDataException("Conflicting cloud source keys require workbench consolidation: " + key);
                        merged.DocumentElement.AppendChild(merged.ImportNode(element, true));
                    }
                }
                // Only the private temporary upload tree is consolidated.
                foreach (string file in group) File.Delete(file);
                merged.Save(Path.Combine(group.Key, TranslationGeneratedOutputOwnership.GetCanonicalFileName(packageId)));
            }
        }

        private static void WriteCloudArchiveManifest(string staging, string packageId, string language)
        {
            var manifest = new CloudArchiveManifest { PackageId = packageId, Language = language };
            foreach (string file in Directory.GetFiles(staging, "*.xml", SearchOption.AllDirectories))
                manifest.Files[file.Substring(staging.Length).TrimStart('\\', '/').Replace('\\', '/')] = TranslationFileTransaction.FileHash(file);
            File.WriteAllText(Path.Combine(staging, "ATC_Manifest.json"), JsonConvert.SerializeObject(manifest));
        }

        private static void CommitCloudArchive(string archivePath, string packageId, string language,
            CloudModRecord record, bool clearExisting, Func<bool> canCommit)
        {
            if (string.IsNullOrWhiteSpace(packageId) || packageId.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || packageId == ".." ||
                string.IsNullOrWhiteSpace(language) || language.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || language == "..")
                throw new InvalidDataException("Invalid package/language scope.");
            string pack = AutoTranslatorScanner.GetLocalPackPath();
            string target = Path.Combine(pack, "Languages", language);
            string workspace = Path.Combine(pack, "Upload_Workspace", packageId, language);
            if (record != null &&
                ((!string.IsNullOrWhiteSpace(record.PackageId) && !string.Equals(record.PackageId, packageId, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(record.Language) && !string.Equals(record.Language, language, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("Cloud record package/language mismatch.");
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                if (archive.Entries.Count > 10000) throw new InvalidDataException("Archive entry limit exceeded.");
                var manifests = archive.Entries.Where(e => string.Equals(e.FullName, "ATC_Manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (manifests.Length > 1 || (manifests.Length == 1 && manifests[0].Length > 4 * 1024 * 1024))
                    throw new InvalidDataException("Invalid or duplicate archive manifest.");
                CloudArchiveManifest manifest = null;
                bool legacy = manifests.Length == 0;
                if (!legacy)
                {
                    using (var reader = new StreamReader(manifests[0].Open())) manifest = JsonConvert.DeserializeObject<CloudArchiveManifest>(reader.ReadToEnd());
                    if (manifest == null || manifest.Schema != 1 || manifest.PackageId != packageId || manifest.Language != language || manifest.Files == null)
                        throw new InvalidDataException("Archive package/language mismatch.");
                }
                // Old uploads predate manifests and contain per-source XML names.
                // Bind them to the requested scope by consolidating into our own
                // canonical destinations, never by trusting their filenames.
                var legacyDocuments = new Dictionary<string, XmlDocument>(StringComparer.OrdinalIgnoreCase);
                var legacyKeys = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                var incomingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    if (!legacy && entry == manifests[0]) continue;
                    if (!canCommit()) throw new OperationCanceledException();
                    string name = entry.FullName.Replace('\\', '/');
                    GetSafeCloudExtractPath(target, name);
                    string pathToValidate = string.IsNullOrEmpty(entry.Name) ? name.TrimEnd('/') : name;
                    if (pathToValidate.Split('/').Any(p => string.IsNullOrWhiteSpace(p) || p == "." || p == ".." ||
                        p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                        throw new InvalidDataException("Invalid archive path: " + name);
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string[] parts = name.Split('/');
                    bool keyed = string.Equals(parts[0], "Keyed", StringComparison.OrdinalIgnoreCase);
                    bool defInjected = string.Equals(parts[0], "DefInjected", StringComparison.OrdinalIgnoreCase);
                    bool scope = legacy
                        ? (parts.Length >= 2 && keyed) || (parts.Length >= 3 && defInjected)
                        : (parts.Length == 2 && keyed) || (parts.Length == 3 && defInjected);
                    // Incoming names must be canonical. Prefixes alone can claim another
                    // package (foo can claim foo_bar_AutoTranslated.xml). Legacy
                    // inputs are renamed instead of writing to that claimed path.
                    string expected = null;
                    if (!scope || !string.Equals(Path.GetExtension(name), ".xml", StringComparison.OrdinalIgnoreCase) ||
                        !incomingPaths.Add(name) || (!legacy && (!IsExactCloudPackageFile(entry.Name, packageId) ||
                        !manifest.Files.TryGetValue(name, out expected))))
                        throw new InvalidDataException("Unowned or duplicate archive entry: " + name);
                    if (entry.Length > 32 * 1024 * 1024 || (total += entry.Length) > 256 * 1024 * 1024)
                        throw new InvalidDataException("Archive size limit exceeded.");
                    byte[] bytes;
                    using (var input = entry.Open())
                    using (var output = new MemoryStream())
                    {
                        byte[] buffer = new byte[81920]; int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            if (!canCommit()) throw new OperationCanceledException();
                            if (output.Length + n > entry.Length) throw new InvalidDataException("Archive length mismatch.");
                            output.Write(buffer, 0, n);
                        }
                        bytes = output.ToArray();
                    }
                    if (bytes.LongLength != entry.Length) throw new InvalidDataException("Archive length mismatch.");
                    if (!legacy && TranslationFileTransaction.Hash(bytes) != expected) throw new InvalidDataException("Archive checksum mismatch.");
                    var document = new XmlDocument { XmlResolver = null };
                    using (var input = new MemoryStream(bytes))
                    using (var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                        document.Load(reader);
                    if (document.DocumentElement?.Name != "LanguageData") throw new InvalidDataException("Invalid translation XML.");
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (XmlNode node in document.DocumentElement.ChildNodes)
                    {
                        if (!(node is XmlElement element)) continue;
                        string key = element.Name;
                        if (defInjected && TranslationXmlEntryCodec.TryReadReplacement(element, out string replacementKey, out string replacementValue))
                            key = replacementKey;
                        if (!keys.Add(key)) throw new InvalidDataException("Duplicate translation key.");
                    }
                    if (!legacy) files.Add(name, bytes);
                    else
                    {
                        string outputName = (keyed ? "Keyed/" : "DefInjected/" + parts[1] + "/") +
                            TranslationGeneratedOutputOwnership.GetCanonicalFileName(packageId);
                        if (!legacyDocuments.TryGetValue(outputName, out var merged))
                        {
                            merged = new XmlDocument { XmlResolver = null };
                            merged.AppendChild(merged.CreateElement("LanguageData"));
                            legacyDocuments[outputName] = merged;
                            legacyKeys[outputName] = new Dictionary<string, string>(StringComparer.Ordinal);
                        }
                        foreach (XmlElement element in document.DocumentElement.ChildNodes.OfType<XmlElement>())
                        {
                            string key = defInjected && TranslationXmlEntryCodec.TryReadReplacement(element, out string replacement, out string _)
                                ? replacement : element.Name;
                            if (legacyKeys[outputName].TryGetValue(key, out string previous))
                            {
                                if (previous != element.OuterXml) throw new InvalidDataException("Conflicting legacy translation key: " + key);
                                continue;
                            }
                            legacyKeys[outputName][key] = element.OuterXml;
                            merged.DocumentElement.AppendChild(merged.ImportNode(element, true));
                        }
                    }
                }
                foreach (var pair in legacyDocuments)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(pair.Value.OuterXml);
                    if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Consolidated archive file size limit exceeded.");
                    files.Add(pair.Key, bytes);
                }
                if (files.Count == 0 || (!legacy && files.Count != manifest.Files.Count)) throw new InvalidDataException("Incomplete translation archive.");
            }
            // Stage includes the previous workspace, not merely the installed language files.
            var transaction = new TranslationFileTransaction(Path.Combine(pack, "Backups", packageId, "Cloud"));
            string ownerPath = Path.Combine(target, "ATC_Ownership", packageId.Replace('.', '_').ToLowerInvariant() + ".json");
            transaction.Observe(ownerPath);
            if (File.Exists(ownerPath) && JsonConvert.DeserializeObject<string>(File.ReadAllText(ownerPath)) != packageId)
                throw new InvalidDataException("Normalized package identity collision; existing translations retained.");
            // Provenance from local/manual producers is independent ownership evidence.
            foreach (string path in Directory.Exists(target) ? Directory.GetFiles(target, "*.json", SearchOption.AllDirectories) : new string[0])
            {
                if (path.IndexOf("Provenance", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var provenance = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                string owner = (string)provenance["PackageId"];
                if (owner != null && owner != packageId && owner.Replace('.', '_').Equals(packageId.Replace('.', '_'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Local provenance belongs to a different package.");
            }
            transaction.Stage(ownerPath, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(packageId)));
            foreach (var pair in files)
            {
                transaction.Stage(GetSafeCloudExtractPath(target, pair.Key), pair.Value);
                transaction.Stage(GetSafeCloudExtractPath(workspace, pair.Key), pair.Value);
            }
            // Only exact generated filenames are eligible for legacy cleanup. Unknown files stay intact.
            if (clearExisting && Directory.Exists(target))
                foreach (string path in Directory.GetFiles(target, "*.xml", SearchOption.AllDirectories))
                {
                    string relative = path.Substring(target.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (!files.ContainsKey(relative) && IsExactCloudPackageFile(path, packageId)) transaction.Stage(path, null);
                }
            if (record != null)
            {
                var meta = new LocalModMeta { OriginalRecordId = record.RecordId, TargetModVersion = record.TargetModVersion ?? "Unknown",
                    TranslationDate = record.TranslationDate, IsSmartMerged = record.IsSmartMerged, MergedAiCount = record.MergedAiCount };
                transaction.Stage(Path.Combine(target, packageId.Replace('.', '_').ToLowerInvariant() + "_ATC_Meta.json"),
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(meta)));
            }
            transaction.Commit(canCommit);
            foreach (var pair in files)
            {
                AutoTranslatorScanner.NotifyTranslationFileChanged(GetSafeCloudExtractPath(target, pair.Key));
                AutoTranslatorScanner.NotifyTranslationFileChanged(GetSafeCloudExtractPath(workspace, pair.Key));
            }
            AutoTranslatorScanner.NotifyTranslationFilesChanged(target);
        }

        private static bool IsExactCloudPackageFile(string path, string packageId)
        {
            string prefix = packageId.Replace('.', '_').ToLowerInvariant();
            string name = Path.GetFileName(path);
            return string.Equals(name, prefix + "_AutoTranslated.xml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, prefix + "_CloudCorrections.xml", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<CloudDownloadAttemptResult> DownloadCloudArchiveAttemptAsync(
            string url,
            string tempFilePath,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
#if RIMWORLD_1_7
            return await WaylithArchiveDownloader.DownloadAsync(url, tempFilePath, timeoutSeconds, cancellationToken);
#else
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 10));
                var tcs = new TaskCompletionSource<CloudDownloadAttemptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                UnityWebRequest request = null;
                // The waiter can cancel even when the main-thread dispatcher is stalled.
                // Creation and all request cleanup remain on the Unity thread.
                using (deadline.Token.Register(() =>
                {
                    if (!tcs.TrySetCanceled()) return;
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        var cancelled = request; request = null;
                        if (cancelled != null) { try { cancelled.Abort(); } finally { cancelled.Dispose(); } }
                        TryDeleteCloudTempFile(tempFilePath);
                    });
                }))
                {
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        if (tcs.Task.IsCompleted) return;
                        try
                        {
                            TryDeleteCloudTempFile(tempFilePath);
                            request = new UnityWebRequest(url, "GET")
                            {
                                timeout = timeoutSeconds,
                                downloadHandler = new DownloadHandlerFile(tempFilePath) { removeFileOnAbort = true }
                            };
                            request.SendWebRequest().completed += _ =>
                            {
                                var completed = request; request = null;
                                if (completed == null) return;
                                bool delivered = false;
                                try
                                {
                                    if (!tcs.Task.IsCompleted)
                                    {
                                        var result = new CloudDownloadAttemptResult
                                        {
                                            Success = UnityWebRequestCompat.IsSuccess(completed),
                                            StatusCode = completed.responseCode, Error = completed.error, FilePath = tempFilePath,
                                            ServedRecordId = completed.GetResponseHeader("X-ATC-Record-Id"),
                                            FallbackReason = completed.GetResponseHeader("X-ATC-Record-Fallback")
                                        };
                                        completed.Dispose(); completed = null;
                                        delivered = tcs.TrySetResult(result);
                                    }
                                }
                                catch (Exception ex) { tcs.TrySetException(ex); }
                                finally { completed?.Dispose(); if (!delivered) TryDeleteCloudTempFile(tempFilePath); }
                            };
                        }
                        catch (Exception ex)
                        {
                            request?.Dispose(); request = null;
                            TryDeleteCloudTempFile(tempFilePath);
                            tcs.TrySetException(ex);
                        }
                    });
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
#endif
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
