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

        internal sealed class UploadLabelValidationSummary
        {
            public int IncludedEntries;
            public string BlockReason;
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
