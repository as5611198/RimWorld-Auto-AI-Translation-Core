using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        internal const string ProvenanceKindAI = "AI";
        internal const string ProvenanceKindAIFromSecondary = "AIFromSecondary";
        internal const string ProvenanceKindExternalPatch = "ExternalPatch";
        internal const string ProvenanceKindModNativeTarget = "ModNativeTarget";
        internal const string ProvenanceKindCloud = "Cloud";
        internal const string ProvenanceKindManualEdit = "ManualEdit";
        internal const string ProvenanceKindLocalPackExisting = "LocalPackExisting";
        internal const string ProvenanceKindUnknownLegacy = "UnknownLegacy";

        internal sealed class TranslationProvenanceEntry
        {
            public string SourceKind;
            public string SourcePackageId;
            public string SourceModName;
            public string SourceFile;
            public string SourceLanguage;
            public string ValueHash;
            public string UpdatedUtc;
            public string PreviousSourceKind;
        }

        private sealed class TranslationProvenanceFile
        {
            public int SchemaVersion = 1;
            public string PackageId;
            public string LanguageFolder;
            public Dictionary<string, TranslationProvenanceEntry> Entries =
                new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
        }

        // Provenance files can contain thousands of entries. Keep one parsed index per file
        // and reload it only when the file stamp changes; loading it once per key turns large
        // DefInjected passes into minutes of repeated JSON parsing and allocation.
        private sealed class ProvenanceIndexCacheEntry
        {
            public long LastWriteUtcTicks;
            public long Length;
            public Dictionary<string, TranslationProvenanceEntry> Entries;
        }

        private static readonly object ProvenanceIndexCacheGate = new object();
        private static readonly Dictionary<string, ProvenanceIndexCacheEntry> ProvenanceIndexCache =
            new Dictionary<string, ProvenanceIndexCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private const int MaximumProvenanceIndexCacheEntries = 64;

        internal sealed class TranslationUploadProvenanceSummary
        {
            public int IncludedEntries;
            public int SkippedEntries;
            public int SkippedExternalPatch;
            public int SkippedManualEdit;
            public int SkippedUnknownLegacy;
            public int SkippedOther;
            public int WrittenFiles;
        }

        internal static TranslationProvenanceEntry CreateProvenance(
            string sourceKind,
            string sourcePackageId,
            string sourceModName,
            string sourceFile,
            string sourceLanguage,
            string value,
            string previousSourceKind = null)
        {
            return new TranslationProvenanceEntry
            {
                SourceKind = NormalizeProvenanceKind(sourceKind),
                SourcePackageId = sourcePackageId ?? "",
                SourceModName = sourceModName ?? "",
                SourceFile = sourceFile ?? "",
                SourceLanguage = sourceLanguage ?? "",
                ValueHash = ComputeValueHash(value),
                UpdatedUtc = DateTime.UtcNow.ToString("O"),
                PreviousSourceKind = previousSourceKind ?? ""
            };
        }

        internal static TranslationProvenanceEntry CloneProvenance(TranslationProvenanceEntry source, string value = null)
        {
            if (source == null) return CreateProvenance(ProvenanceKindUnknownLegacy, "", "", "", "", value ?? "");

            return new TranslationProvenanceEntry
            {
                SourceKind = NormalizeProvenanceKind(source.SourceKind),
                SourcePackageId = source.SourcePackageId ?? "",
                SourceModName = source.SourceModName ?? "",
                SourceFile = source.SourceFile ?? "",
                SourceLanguage = source.SourceLanguage ?? "",
                ValueHash = value == null ? source.ValueHash ?? "" : ComputeValueHash(value),
                UpdatedUtc = DateTime.UtcNow.ToString("O"),
                PreviousSourceKind = source.PreviousSourceKind ?? ""
            };
        }

        internal static TranslationProvenanceEntry GetFileEntryProvenance(
            string languageRoot,
            string packageId,
            string translationFile,
            string key,
            string value)
        {
            Dictionary<string, TranslationProvenanceEntry> index = LoadProvenanceIndex(languageRoot, packageId);
            string entryId = BuildProvenanceEntryId(languageRoot, translationFile, key);
            if (!string.IsNullOrEmpty(entryId) &&
                index.TryGetValue(entryId, out TranslationProvenanceEntry entry) &&
                HashMatches(entry, value))
            {
                return CloneProvenance(entry, value);
            }

            return CreateProvenance(ProvenanceKindUnknownLegacy, packageId, "", translationFile, "", value);
        }

        internal static void MarkCloudDownloadedTranslations(
            string languageRoot,
            string packageId,
            string targetLanguage,
            string recordId,
            IEnumerable<string> downloadedFiles)
        {
            if (string.IsNullOrWhiteSpace(languageRoot) || !Directory.Exists(languageRoot)) return;
            foreach (string candidate in (downloadedFiles ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!CloudDownloadedFileScope.TryResolveXml(languageRoot, candidate, out string file)) continue;
                Dictionary<string, string> data = LoadXmlFileToDict(file);
                if (data.Count == 0) continue;
                Dictionary<string, TranslationProvenanceEntry> provenance = data.ToDictionary(
                    pair => pair.Key,
                    pair => CreateProvenance(
                        ProvenanceKindCloud,
                        packageId,
                        "Cloud record " + (recordId ?? string.Empty),
                        file,
                        targetLanguage,
                        pair.Value),
                    StringComparer.OrdinalIgnoreCase);
                SaveProvenanceForFile(languageRoot, packageId, file, data, provenance);
            }
        }

        internal static bool SaveProvenanceForFile(
            string languageRoot,
            string packageId,
            string translationFile,
            IDictionary<string, string> savedData,
            IDictionary<string, TranslationProvenanceEntry> sourcesByKey)
        {
            if (string.IsNullOrWhiteSpace(languageRoot) ||
                string.IsNullOrWhiteSpace(packageId) ||
                string.IsNullOrWhiteSpace(translationFile) ||
                savedData == null)
            {
                return false;
            }

            try
            {
                Dictionary<string, TranslationProvenanceEntry> index = LoadProvenanceIndex(languageRoot, packageId);
                string relativePath = GetRelativeTranslationPath(languageRoot, translationFile);
                if (string.IsNullOrEmpty(relativePath)) return false;

                string prefix = relativePath + "|";
                HashSet<string> savedKeys = new HashSet<string>(savedData.Keys.Where(k => !string.IsNullOrWhiteSpace(k)), StringComparer.OrdinalIgnoreCase);
                foreach (string staleEntryId in index.Keys
                    .Where(id => id != null && id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList())
                {
                    string staleKey = staleEntryId.Substring(prefix.Length);
                    if (!savedKeys.Contains(staleKey)) index.Remove(staleEntryId);
                }

                foreach (KeyValuePair<string, string> pair in savedData)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) continue;

                    string entryId = relativePath + "|" + pair.Key;
                    TranslationProvenanceEntry source = null;
                    if (sourcesByKey != null)
                    {
                        sourcesByKey.TryGetValue(pair.Key, out source);
                    }

                    if (source != null)
                    {
                        index[entryId] = CloneProvenance(source, pair.Value);
                    }
                    else if (!index.TryGetValue(entryId, out TranslationProvenanceEntry existing) ||
                             !HashMatches(existing, pair.Value))
                    {
                        index[entryId] = CreateProvenance(ProvenanceKindUnknownLegacy, packageId, "", translationFile, "", pair.Value);
                    }
                }

                SaveProvenanceIndex(languageRoot, packageId, index);
                return true;
            }
            catch (Exception ex)
            {
                Verse.Log.Warning($"[AutoTranslationCore] Failed to save provenance for {translationFile}: {ex.Message}");
                return false;
            }
        }

        internal static bool TryPrepareUploadSourceFolder(
            string sourceFolder,
            string packageId,
            string languageFolder,
            string translationType,
            out string preparedSourceFolder,
            out TranslationUploadProvenanceSummary summary)
        {
            preparedSourceFolder = sourceFolder;
            summary = new TranslationUploadProvenanceSummary();

            if (!IsAiUploadType(translationType)) return true;
            if (string.IsNullOrWhiteSpace(sourceFolder) ||
                string.IsNullOrWhiteSpace(packageId) ||
                !Directory.Exists(sourceFolder))
            {
                return false;
            }

            string stagingDir = Path.Combine(Path.GetTempPath(), "ATC_AIUploadFiltered_" + SanitizeFileName(packageId) + "_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(stagingDir);

                string id1 = packageId.ToLowerInvariant();
                string id2 = packageId.Replace(".", "_").ToLowerInvariant();
                bool isWorkspace = sourceFolder.IndexOf("Upload_Workspace", StringComparison.OrdinalIgnoreCase) >= 0;
                Dictionary<string, TranslationProvenanceEntry> provenance = LoadProvenanceIndex(sourceFolder, packageId);

                foreach (string file in GetXmlFilesForTranslationCache(sourceFolder, SearchOption.AllDirectories))
                {
                    if (file.IndexOf("ATC_Provenance", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (IsWorkbenchManualExportPath(file)) continue;

                    string fileName = Path.GetFileName(file).ToLowerInvariant();
                    bool shouldPack = isWorkspace ||
                                      fileName.StartsWith(id1 + "_") ||
                                      fileName.StartsWith(id1 + ".") ||
                                      fileName.StartsWith(id2 + "_") ||
                                      fileName.StartsWith(id2 + ".");
                    if (!shouldPack) continue;

                    Dictionary<string, string> sourceData = LoadXmlFileToDict(file);
                    if (sourceData.Count == 0) continue;

                    Dictionary<string, string> filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string relativePath = GetRelativeTranslationPath(sourceFolder, file);
                    foreach (KeyValuePair<string, string> pair in sourceData)
                    {
                        string entryId = string.IsNullOrEmpty(relativePath) ? "" : relativePath + "|" + pair.Key;
                        TranslationProvenanceEntry entry = null;
                        if (!string.IsNullOrEmpty(entryId)) provenance.TryGetValue(entryId, out entry);

                        if (entry != null && HashMatches(entry, pair.Value) && IsAllowedForAiUpload(entry))
                        {
                            filtered[pair.Key] = pair.Value;
                            summary.IncludedEntries++;
                        }
                        else
                        {
                            AddSkippedUploadEntry(summary, entry != null ? entry.SourceKind : ProvenanceKindUnknownLegacy);
                        }
                    }

                    if (filtered.Count == 0) continue;

                    string relPath = GetUploadRelativePath(sourceFolder, file, packageId);
                    string destPath = Path.Combine(stagingDir, relPath);
                    SaveXml(destPath, filtered);
                    summary.WrittenFiles++;
                }

                if (summary.WrittenFiles == 0)
                {
                    Directory.Delete(stagingDir, true);
                    return false;
                }

                CopyUploadMetaIfPresent(sourceFolder, stagingDir, packageId);
                preparedSourceFolder = stagingDir;
                if (summary.SkippedEntries > 0)
                {
                    AutoTranslatorSettings.AddLog("☁️ " + FormatAiUploadFilteredLog(packageId, summary));
                }
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
                }
                catch { }

                Verse.Log.Warning($"[AutoTranslationCore] Failed to prepare filtered AI upload for {packageId}: {ex.Message}");
                return false;
            }
        }

        private static void CopyUploadMetaIfPresent(string sourceFolder, string stagingDir, string packageId)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder) ||
                string.IsNullOrWhiteSpace(stagingDir) ||
                string.IsNullOrWhiteSpace(packageId))
            {
                return;
            }

            string id2 = packageId.Replace(".", "_").ToLowerInvariant();
            string sourceMeta = Path.Combine(sourceFolder, id2 + "_ATC_Meta.json");
            if (!File.Exists(sourceMeta)) return;

            Directory.CreateDirectory(stagingDir);
            File.Copy(sourceMeta, Path.Combine(stagingDir, Path.GetFileName(sourceMeta)), true);
        }

        internal static void DeletePreparedUploadSourceFolder(string originalSourceFolder, string preparedSourceFolder)
        {
            if (string.IsNullOrWhiteSpace(preparedSourceFolder) ||
                string.Equals(originalSourceFolder, preparedSourceFolder, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                if (Directory.Exists(preparedSourceFolder)) Directory.Delete(preparedSourceFolder, true);
            }
            catch { }
        }

        internal static bool IsAiUploadBlockedByProvenance(TranslationUploadProvenanceSummary summary)
        {
            return summary != null &&
                   summary.WrittenFiles == 0 &&
                   summary.IncludedEntries == 0 &&
                   summary.SkippedEntries > 0;
        }

        internal static string FormatAiUploadFilteredLog(string displayName, TranslationUploadProvenanceSummary summary)
        {
            summary = summary ?? new TranslationUploadProvenanceSummary();
            return AutoTranslatorAPI.TranslateText(
                "ATC_Log_AiUploadFiltered",
                displayName ?? "",
                summary.IncludedEntries,
                summary.SkippedEntries,
                summary.SkippedExternalPatch,
                summary.SkippedManualEdit,
                summary.SkippedUnknownLegacy,
                summary.SkippedOther);
        }

        internal static string FormatAiUploadNoCleanLog(string displayName, TranslationUploadProvenanceSummary summary)
        {
            summary = summary ?? new TranslationUploadProvenanceSummary();
            return AutoTranslatorAPI.TranslateText(
                "ATC_Log_AiUploadNoCleanEntries",
                displayName ?? "",
                summary.SkippedEntries,
                summary.SkippedExternalPatch,
                summary.SkippedManualEdit,
                summary.SkippedUnknownLegacy,
                summary.SkippedOther);
        }

        internal static string FormatAiUploadNoCleanMessage(string displayName)
        {
            return AutoTranslatorAPI.TranslateText("ATC_Msg_AiUploadNoCleanEntries", displayName ?? "");
        }

        private static Dictionary<string, TranslationProvenanceEntry> LoadProvenanceIndex(string languageRoot, string packageId)
        {
            Dictionary<string, TranslationProvenanceEntry> empty =
                new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
            string path = GetProvenancePath(languageRoot, packageId);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return empty;

            GetProvenanceFileStamp(path, out long lastWriteUtcTicks, out long length);
            lock (ProvenanceIndexCacheGate)
            {
                if (ProvenanceIndexCache.TryGetValue(path, out ProvenanceIndexCacheEntry cached) &&
                    cached != null &&
                    cached.LastWriteUtcTicks == lastWriteUtcTicks &&
                    cached.Length == length &&
                    cached.Entries != null)
                {
                    return cached.Entries;
                }
            }

            try
            {
                TranslationProvenanceFile data = JsonConvert.DeserializeObject<TranslationProvenanceFile>(File.ReadAllText(path));
                Dictionary<string, TranslationProvenanceEntry> entries = data != null && data.Entries != null
                    ? new Dictionary<string, TranslationProvenanceEntry>(data.Entries, StringComparer.OrdinalIgnoreCase)
                    : empty;
                CacheProvenanceIndex(path, lastWriteUtcTicks, length, entries);
                return entries;
            }
            catch
            {
                lock (ProvenanceIndexCacheGate)
                {
                    ProvenanceIndexCache.Remove(path);
                }
                return empty;
            }
        }

        private static void SaveProvenanceIndex(string languageRoot, string packageId, Dictionary<string, TranslationProvenanceEntry> entries)
        {
            string path = GetProvenancePath(languageRoot, packageId);
            if (string.IsNullOrEmpty(path)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            TranslationProvenanceFile data = new TranslationProvenanceFile
            {
                SchemaVersion = 1,
                PackageId = packageId ?? "",
                LanguageFolder = Path.GetFileName(languageRoot ?? "") ?? "",
                Entries = entries ?? new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase)
            };
            byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data, Formatting.Indented));
            TranslationXmlAtomicFileStore.Save(path, stream => stream.Write(json, 0, json.Length));
            GetProvenanceFileStamp(path, out long lastWriteUtcTicks, out long length);
            CacheProvenanceIndex(
                path,
                lastWriteUtcTicks,
                length,
                entries ?? new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase));
        }

        private static void CacheProvenanceIndex(
            string path,
            long lastWriteUtcTicks,
            long length,
            Dictionary<string, TranslationProvenanceEntry> entries)
        {
            if (string.IsNullOrEmpty(path) || entries == null) return;
            lock (ProvenanceIndexCacheGate)
            {
                if (!ProvenanceIndexCache.ContainsKey(path) &&
                    ProvenanceIndexCache.Count >= MaximumProvenanceIndexCacheEntries)
                {
                    string evictedPath = ProvenanceIndexCache.Keys.FirstOrDefault();
                    if (!string.IsNullOrEmpty(evictedPath)) ProvenanceIndexCache.Remove(evictedPath);
                }

                ProvenanceIndexCache[path] = new ProvenanceIndexCacheEntry
                {
                    LastWriteUtcTicks = lastWriteUtcTicks,
                    Length = length,
                    Entries = entries
                };
            }
        }

        private static void GetProvenanceFileStamp(string path, out long lastWriteUtcTicks, out long length)
        {
            lastWriteUtcTicks = 0L;
            length = -1L;
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists) return;
                lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
                length = info.Length;
            }
            catch
            {
                // A failed stamp lookup simply disables the cache hit for this call.
            }
        }

        private static string GetProvenancePath(string languageRoot, string packageId)
        {
            if (string.IsNullOrWhiteSpace(languageRoot) || string.IsNullOrWhiteSpace(packageId)) return null;
            return Path.Combine(languageRoot, "ATC_Provenance", packageId.Replace(".", "_").ToLowerInvariant() + ".json");
        }

        private static string BuildProvenanceEntryId(string languageRoot, string translationFile, string key)
        {
            string relativePath = GetRelativeTranslationPath(languageRoot, translationFile);
            return string.IsNullOrEmpty(relativePath) || string.IsNullOrWhiteSpace(key)
                ? ""
                : relativePath + "|" + key;
        }

        private static string GetRelativeTranslationPath(string languageRoot, string file)
        {
            if (string.IsNullOrWhiteSpace(languageRoot) || string.IsNullOrWhiteSpace(file)) return "";

            string root = Path.GetFullPath(languageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(file);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
            }

            return Path.GetFileName(file);
        }

        private static string GetUploadRelativePath(string sourceFolder, string file, string packageId)
        {
            string relPath = file.Substring(sourceFolder.Length).TrimStart('\\', '/');
            string id1 = packageId.ToLowerInvariant();
            string id2 = packageId.Replace(".", "_").ToLowerInvariant();
            string justFileName = Path.GetFileName(file).ToLowerInvariant();

            if (!justFileName.StartsWith(id1 + "_") && !justFileName.StartsWith(id1 + ".") &&
                !justFileName.StartsWith(id2 + "_") && !justFileName.StartsWith(id2 + "."))
            {
                string dirName = Path.GetDirectoryName(relPath);
                string newFileName = id2 + "_" + Path.GetFileName(file);
                relPath = string.IsNullOrEmpty(dirName) ? newFileName : Path.Combine(dirName, newFileName);
            }

            return relPath;
        }

        private static bool IsAiUploadType(string translationType)
        {
            return string.Equals(translationType, "AI_Auto", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAllowedForAiUpload(TranslationProvenanceEntry entry)
        {
            if (entry == null) return false;

            string sourceKind = NormalizeProvenanceKind(entry.SourceKind);
            if (sourceKind == ProvenanceKindAI) return true;
            if (sourceKind != ProvenanceKindAIFromSecondary) return false;

            string previousKind = NormalizeProvenanceKind(entry.PreviousSourceKind);
            return previousKind == ProvenanceKindModNativeTarget ||
                   previousKind == ProvenanceKindAI;
        }

        private static void AddSkippedUploadEntry(TranslationUploadProvenanceSummary summary, string sourceKind)
        {
            if (summary == null) return;

            summary.SkippedEntries++;
            sourceKind = NormalizeProvenanceKind(sourceKind);
            if (sourceKind == ProvenanceKindExternalPatch) summary.SkippedExternalPatch++;
            else if (sourceKind == ProvenanceKindManualEdit) summary.SkippedManualEdit++;
            else if (sourceKind == ProvenanceKindUnknownLegacy) summary.SkippedUnknownLegacy++;
            else summary.SkippedOther++;
        }

        private static string NormalizeProvenanceKind(string sourceKind)
        {
            if (string.IsNullOrWhiteSpace(sourceKind)) return ProvenanceKindUnknownLegacy;
            if (string.Equals(sourceKind, ProvenanceKindAI, StringComparison.OrdinalIgnoreCase)) return ProvenanceKindAI;
            if (string.Equals(sourceKind, ProvenanceKindAIFromSecondary, StringComparison.OrdinalIgnoreCase)) return ProvenanceKindAIFromSecondary;
            if (string.Equals(sourceKind, ProvenanceKindExternalPatch, StringComparison.OrdinalIgnoreCase)) return ProvenanceKindExternalPatch;
            if (string.Equals(sourceKind, ProvenanceKindModNativeTarget, StringComparison.OrdinalIgnoreCase)) return ProvenanceKindModNativeTarget;
            if (string.Equals(sourceKind, ProvenanceKindCloud, StringComparison.OrdinalIgnoreCase)) return ProvenanceKindCloud;
            if (string.Equals(sourceKind, ProvenanceKindManualEdit, StringComparison.OrdinalIgnoreCase)) return ProvenanceKindManualEdit;
            if (string.Equals(sourceKind, ProvenanceKindLocalPackExisting, StringComparison.OrdinalIgnoreCase)) return ProvenanceKindLocalPackExisting;
            return ProvenanceKindUnknownLegacy;
        }

        private static bool HashMatches(TranslationProvenanceEntry entry, string value)
        {
            if (entry == null || string.IsNullOrEmpty(entry.ValueHash)) return false;
            return string.Equals(entry.ValueHash, ComputeValueHash(value), StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeValueHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] data = Encoding.UTF8.GetBytes(value ?? "");
                byte[] hash = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string SanitizeFileName(string value)
        {
            string safe = value ?? "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(c, '_');
            }

            return safe;
        }
    }
}
