using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責雲端上傳流程。
// EN: This file uploads translation packages to the cloud service.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器雲端Client 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorCloudClient.
    public static partial class AutoTranslatorCloudClient
    {
        internal sealed class UploadTranslationResult
        {
            public bool Success;
            public AutoTranslatorScanner.TranslationUploadProvenanceSummary ProvenanceSummary;
        }

        // 這個方法負責上傳 翻譯Async 到雲端。
        // EN: This method uploads translation async.
        public static async Task<bool> UploadTranslationAsync(string packageId, string language, string modName, string author, string translationType, string sourceFolder, string adminToken, string updateLog = "")
        {
            UploadTranslationResult result = await UploadTranslationWithDetailsAsync(packageId, language, modName, author, translationType, sourceFolder, adminToken, updateLog);
            return result != null && result.Success;
        }

        internal static async Task<UploadTranslationResult> UploadTranslationWithDetailsAsync(
            string packageId,
            string language,
            string modName,
            string author,
            string translationType,
            string sourceFolder,
            string adminToken,
            string updateLog = "")
        {
            string preparedSourceFolder = sourceFolder;
            UploadTranslationResult result = new UploadTranslationResult();
            try
            {
                if (!System.IO.Directory.Exists(sourceFolder)) return result;
                AutoTranslatorScanner.TranslationUploadProvenanceSummary provenanceSummary;
                if (!AutoTranslatorScanner.TryPrepareUploadSourceFolder(sourceFolder, packageId, language, translationType, out preparedSourceFolder, out provenanceSummary))
                {
                    result.ProvenanceSummary = provenanceSummary;
                    return result;
                }
                result.ProvenanceSummary = provenanceSummary;

                string stagingDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ATC_Upload_" + packageId);
                if (System.IO.Directory.Exists(stagingDir)) System.IO.Directory.Delete(stagingDir, true);
                System.IO.Directory.CreateDirectory(stagingDir);

                string id1 = packageId.ToLower();
                string id2 = packageId.Replace(".", "_").ToLower();
                int fileCount = 0;
                bool isWorkspace = preparedSourceFolder.Contains("Upload_Workspace") ||
                                   !string.Equals(preparedSourceFolder, sourceFolder, StringComparison.OrdinalIgnoreCase);

                foreach (string file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(preparedSourceFolder, System.IO.SearchOption.AllDirectories))
                {
                    if (AutoTranslatorScanner.IsWorkbenchManualExportPath(file)) continue;
                    string fileName = System.IO.Path.GetFileName(file).ToLower();
                    bool shouldPack = isWorkspace;

                    if (!shouldPack)
                    {
                        if (fileName.StartsWith(id1 + "_") || fileName.StartsWith(id1 + ".") ||
                            fileName.StartsWith(id2 + "_") || fileName.StartsWith(id2 + "."))
                        {
                            shouldPack = true;
                        }
                    }

                    if (shouldPack)
                    {
                        string relPath = file.Substring(preparedSourceFolder.Length).TrimStart('\\', '/');
                        string justFileName = System.IO.Path.GetFileName(file).ToLower();
                        if (!justFileName.StartsWith(id1 + "_") && !justFileName.StartsWith(id1 + ".") &&
                            !justFileName.StartsWith(id2 + "_") && !justFileName.StartsWith(id2 + "."))
                        {
                            string dirName = System.IO.Path.GetDirectoryName(relPath);
                            string newFileName = $"{id2}_{System.IO.Path.GetFileName(file)}";
                            relPath = string.IsNullOrEmpty(dirName) ? newFileName : System.IO.Path.Combine(dirName, newFileName);
                        }

                        string destPath = System.IO.Path.Combine(stagingDir, relPath);
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destPath));
                        System.IO.File.Copy(file, destPath);
                        fileCount++;
                    }
                }

                if (fileCount == 0)
                {
                    System.IO.Directory.Delete(stagingDir, true);
                    return result;
                }

                string tempZipFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{packageId}_{language}_upload.zip");
                if (System.IO.File.Exists(tempZipFile)) System.IO.File.Delete(tempZipFile);
                System.IO.Compression.ZipFile.CreateFromDirectory(stagingDir, tempZipFile, System.IO.Compression.CompressionLevel.Optimal, false, System.Text.Encoding.UTF8); System.IO.Directory.Delete(stagingDir, true);

                byte[] zipBytes = System.IO.File.ReadAllBytes(tempZipFile);
                string base64File = Convert.ToBase64String(zipBytes);
                System.IO.File.Delete(tempZipFile);

                string metaPath = System.IO.Path.Combine(preparedSourceFolder, $"{id2}_ATC_Meta.json");
                string targetModVersion = "Unknown";
                DateTime translationDate = DateTime.UtcNow;
                bool isSmartMerged = false;
                int mergedAiCount = 0;

                if (System.IO.File.Exists(metaPath))
                {
                    try
                    {
                        var meta = JsonConvert.DeserializeObject<LocalModMeta>(System.IO.File.ReadAllText(metaPath));
                        if (meta != null)
                        {
                            targetModVersion = meta.TargetModVersion;
                            translationDate = meta.TranslationDate;
                            isSmartMerged = meta.IsSmartMerged;
                            mergedAiCount = meta.MergedAiCount;
                        }
                    }
                    catch { }
                }

                Verse.ModMetaData tempMeta = null;
                foreach (var m in Verse.ModLister.AllInstalledMods)
                {
                    if (m.PackageId.ToLower() == packageId.ToLower())
                    {
                        tempMeta = m;
                        break;
                    }
                }

                DateTime actualModUpdate = DateTime.UtcNow;
                if (tempMeta != null && tempMeta.RootDir != null && System.IO.Directory.Exists(tempMeta.RootDir.FullName))
                {
                    actualModUpdate = new System.IO.DirectoryInfo(tempMeta.RootDir.FullName).LastWriteTimeUtc;
                }

                var payload = new
                {
                    PackageId = packageId,
                    Language = language,
                    ModName = modName,
                    LatestVersion = RimWorld.VersionControl.CurrentVersionStringWithoutBuild,
                    ModLastUpdated = actualModUpdate.ToString("O"),
                    UploaderID = UnityEngine.SystemInfo.deviceUniqueIdentifier,
                    Author = author,
                    TranslationType = translationType,
                    TranslationSourceKind = GetCloudTranslationSourceKind(translationType),
                    SourceKind = GetCloudTranslationSourceKind(translationType),
                    TranslationSourceSchemaVersion = 1,
                    FileBase64 = base64File,
                    AdminToken = adminToken,
                    TargetModVersion = targetModVersion,
                    TranslationDate = translationDate.ToString("O"),
                    IsSmartMerged = isSmartMerged,
                    MergedAiCount = mergedAiCount,
                    UpdateLog = updateLog ?? ""
                };

                string jsonPayload = JsonConvert.SerializeObject(payload);
                System.Text.Encoding tolerantUtf8 = new System.Text.UTF8Encoding(false, false);
                byte[] payloadBytes = tolerantUtf8.GetBytes(jsonPayload);

                int maxRetries = 4;

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    if (attempt >= 3 && CloudApiBaseUrl == PrimaryApiBaseUrl)
                    {
                        CloudApiBaseUrl = BackupApiBaseUrl;
                        LogCloudTranslatedWarning("ATC_Cloud_UploadFallback");
                    }

                    try
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        ATC_Dispatcher.RunOnMainThread(() =>
                        {
                            try
                            {
                                var request = new UnityEngine.Networking.UnityWebRequest($"{CloudApiBaseUrl}/upload", "POST");
                                request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(payloadBytes);
                                request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                                request.SetRequestHeader("Content-Type", "application/json");
                                request.timeout = 120;

                                var operation = request.SendWebRequest();
                                operation.completed += (op) =>
                                {
                                    try
                                    {
                                        if (UnityWebRequestCompat.IsSuccess(request))
                                            tcs.TrySetResult(true);
                                        else
                                            tcs.TrySetException(new Exception(request.error));
                                    }
                                    catch (Exception innerEx) { tcs.TrySetException(innerEx); }
                                    finally { request.Dispose(); }
                                };
                            }
                            catch (Exception dispatchEx) { tcs.TrySetException(dispatchEx); }
                        });

                        bool success = await WaitForCloudTask(tcs.Task, 130, "cloud upload");
                        if (success)
                        {
                            result.Success = true;
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (attempt == maxRetries)
                        {
                            LogCloudTranslatedError("ATC_Cloud_UploadFailed", ex.Message); return result;
                        }
                    }
                    int delay = (int)Math.Pow(2, attempt + 1) * 1000 + new System.Random().Next(100, 500);
                    await Task.Delay(delay);
                }

                return result;
            }
            catch (Exception ex)
            {
                LogCloudTranslatedError("ATC_Cloud_UploadFailed", ex.Message); return result;
            }
            finally
            {
                AutoTranslatorScanner.DeletePreparedUploadSourceFolder(sourceFolder, preparedSourceFolder);
            }
        }

        internal static string GetCloudTranslationSourceKind(string translationType)
        {
            if (string.Equals(translationType, "AI_Auto", StringComparison.OrdinalIgnoreCase)) return AutoTranslatorScanner.ProvenanceKindAI;
            if (string.Equals(translationType, "Manual", StringComparison.OrdinalIgnoreCase)) return AutoTranslatorScanner.ProvenanceKindManualEdit;
            if (string.Equals(translationType, "Official_Group", StringComparison.OrdinalIgnoreCase)) return AutoTranslatorScanner.ProvenanceKindModNativeTarget;
            return AutoTranslatorScanner.ProvenanceKindUnknownLegacy;
        }

    }
}
