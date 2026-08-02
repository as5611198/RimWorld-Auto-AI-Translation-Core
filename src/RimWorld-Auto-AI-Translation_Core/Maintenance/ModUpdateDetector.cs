using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責偵測模組是否更新或需要重新掃描。
// EN: This file detects mod updates and translation freshness.

namespace AutoTranslator_Core
{
    // 這個列舉定義 模組翻譯Status 可使用的固定選項。
    // EN: This enum defines the available mod translation status options.
    public enum ModTranslationStatus
    {
        Untranslated,
        Translated,
        PossiblyOutdated,
        Filtered
    }

    // 這個類別負責 模組Update偵測器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for ModUpdateDetector.
    public static class ModUpdateDetector
    {
        // 這個常數定義 UpdatedList快取Seconds 的固定值。
        // EN: This constant defines the fixed value for updated list cache seconds.
        private const float UpdatedListCacheSeconds = 300f;
        // 這個常數定義 TranslatedFile快取Seconds 的固定值。
        // EN: This constant defines the fixed value for translated file cache seconds.
        private const float TranslatedFileCacheSeconds = 60f;
        // 這個常數定義 SourceFingerprint快取Seconds 的固定值。
        // EN: This constant defines the fixed value for source fingerprint cache seconds.
        private const float SourceFingerprintCacheSeconds = 300f;
        // 這個常數定義 Status快取Seconds 的固定值。
        // EN: This constant defines the fixed value for status cache seconds.
        private const float StatusCacheSeconds = 300f;

        // 這個欄位保存 cached模組 的執行狀態或快取資料。
        // EN: This field stores cached mods runtime state or cached data.
        private static readonly object _cacheLock = new object();
        private static readonly DateTime _cacheClockOriginUtc = DateTime.UtcNow;
        private static List<ModMetaData> _cachedMods = null;
        // 這個欄位保存 lastCheckTime 的執行狀態或快取資料。
        // EN: This field stores last check time runtime state or cached data.
        private static float _lastCheckTime = 0f;
        // 這個欄位保存 translatedFilePackageIds 的執行狀態或快取資料。
        // EN: This field stores translated file package ids runtime state or cached data.
        private static HashSet<string> _translatedFilePackageIds = null;
        // 這個欄位保存 translatedFileLatestTicksByPackageId 的執行狀態或快取資料。
        // EN: This field stores translated file latest ticks by package id runtime state or cached data.
        private static Dictionary<string, long> _translatedFileLatestTicksByPackageId = null;
        // 這個欄位保存 translatedFile快取語言 的執行狀態或快取資料。
        // EN: This field stores translated file cache language runtime state or cached data.
        private static TargetLanguage _translatedFileCacheLang;
        // 這個欄位保存 lastTranslatedFile快取Time 的執行狀態或快取資料。
        // EN: This field stores last translated file cache time runtime state or cached data.
        private static float _lastTranslatedFileCacheTime = 0f;
        // 這個欄位保存 sourceSnapshot快取 的執行狀態或快取資料。
        // EN: This field stores source snapshot cache runtime state or cached data.
        private static readonly Dictionary<string, SourceSnapshot> _sourceSnapshotCache =
            new Dictionary<string, SourceSnapshot>(StringComparer.OrdinalIgnoreCase);
        // 這個欄位保存 status快取 的執行狀態或快取資料。
        // EN: This field stores status cache runtime state or cached data.
        private static readonly Dictionary<string, StatusSnapshot> _statusCache =
            new Dictionary<string, StatusSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static int _cacheGeneration = 0;
        private static bool _isUpdatedListRefreshing = false;
        private static int _updatedListRefreshGeneration = 0;
        private static string _updatedListRefreshError = null;

        public static bool IsRefreshingUpdatedList
        {
            get
            {
                lock (_cacheLock)
                {
                    return _isUpdatedListRefreshing;
                }
            }
        }

        public static bool HasUpdatedListCache
        {
            get
            {
                lock (_cacheLock)
                {
                    return _cachedMods != null;
                }
            }
        }

        public static string LastUpdatedListRefreshError
        {
            get
            {
                lock (_cacheLock)
                {
                    return _updatedListRefreshError;
                }
            }
        }

        private static float NowSeconds()
        {
            return (float)(DateTime.UtcNow - _cacheClockOriginUtc).TotalSeconds;
        }

        // 這個類別負責 SourceSnapshot 的主要流程與狀態。
        // EN: This class manages the main workflow and state for SourceSnapshot.
        private class SourceSnapshot
        {
            // 這個欄位保存 Fingerprint 的執行狀態或快取資料。
            // EN: This field stores fingerprint runtime state or cached data.
            public string Fingerprint = "";
            // 這個欄位保存 LatestTicks 的執行狀態或快取資料。
            // EN: This field stores latest ticks runtime state or cached data.
            public long LatestTicks = 0L;
            // 這個欄位保存 HasSourceFiles 的執行狀態或快取資料。
            // EN: This field stores has source files runtime state or cached data.
            public bool HasSourceFiles = false;
            public bool HasTranslatableSourceFiles = false;
            // 這個欄位保存 HasNative目標語言 的執行狀態或快取資料。
            // EN: This field stores has native target language runtime state or cached data.
            public bool HasNativeTargetLanguage = false;
            // 這個欄位保存 HasExternal目標語言補丁 的執行狀態或快取資料。
            // EN: This field stores has external target language patch runtime state or cached data.
            public bool HasExternalTargetLanguagePatch = false;
            // 這個欄位保存 CachedAt 的執行狀態或快取資料。
            // EN: This field stores cached at runtime state or cached data.
            public float CachedAt = 0f;
        }

        // 這個類別負責 StatusSnapshot 的主要流程與狀態。
        // EN: This class manages the main workflow and state for StatusSnapshot.
        private class StatusSnapshot
        {
            // 這個欄位保存 Status 的執行狀態或快取資料。
            // EN: This field stores status runtime state or cached data.
            public ModTranslationStatus Status;
            // 這個欄位保存 CachedAt 的執行狀態或快取資料。
            // EN: This field stores cached at runtime state or cached data.
            public float CachedAt;
        }

        public sealed class InstalledModStatusSnapshot
        {
            public string PackageId;
            public string Name;
            public string RootDir;
            public bool Active;
            public bool IsTranslationPatchMod;
        }

        public sealed class TranslationStatusCheckSnapshot
        {
            public string PackageId;
            public string RootDir;
            public TargetLanguage TargetLang;
            public Dictionary<string, long> SavedTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> SavedFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<InstalledModStatusSnapshot> ActiveMods = new List<InstalledModStatusSnapshot>();
        }

        public sealed class SourceFingerprintSnapshot
        {
            public long LatestTicks;
            public string Fingerprint = "";
        }

        private sealed class LocalTranslationFilesSnapshot
        {
            public HashSet<string> PackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, long> LatestTicksByPackageId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        // 這個方法負責取得 模組快取Key 資料。
        // EN: This method gets mod cache key.
        private static string GetModCacheKey(ModMetaData mod)
        {
            return GetModCacheKey(AutoTranslatorMod.Settings.TargetLang, mod.PackageId);
        }

        private static string GetModCacheKey(TargetLanguage targetLang, string packageId)
        {
            return $"{targetLang}|{packageId}";
        }

        // 這個方法負責嘗試執行 GetSavedTick 並回報是否成功。
        // EN: This method tries to get saved tick and reports whether it succeeded.
        private static bool TryGetSavedTick(string packageId, out long savedTicks)
        {
            savedTicks = 0L;
            var dict = AutoTranslatorMod.Settings.ModLastVerifiedTimes;
            if (dict == null || string.IsNullOrEmpty(packageId)) return false;
            if (dict.TryGetValue(packageId, out savedTicks)) return true;

            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, packageId, StringComparison.OrdinalIgnoreCase))
                {
                    savedTicks = kv.Value;
                    return true;
                }
            }

            return false;
        }

        // 這個方法負責嘗試執行 GetSavedFingerprint 並回報是否成功。
        // EN: This method tries to get saved fingerprint and reports whether it succeeded.
        private static bool TryGetSavedFingerprint(string packageId, out string savedFingerprint)
        {
            savedFingerprint = null;
            var dict = AutoTranslatorMod.Settings.ModLastVerifiedFingerprints;
            if (dict == null || string.IsNullOrEmpty(packageId)) return false;
            if (dict.TryGetValue(packageId, out savedFingerprint)) return !string.IsNullOrEmpty(savedFingerprint);

            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, packageId, StringComparison.OrdinalIgnoreCase))
                {
                    savedFingerprint = kv.Value;
                    return !string.IsNullOrEmpty(savedFingerprint);
                }
            }

            return false;
        }

        // 這個方法負責清理並標準化 PackageForComparison 內容。
        // EN: This method cleans and normalizes package for comparison.
        private static string NormalizePackageForComparison(string packageId)
        {
            return (packageId ?? "").Trim().ToLowerInvariant().Replace(".", "_");
        }

        // 這個方法負責處理 ExtractPackagePrefixFrom翻譯File 相關流程。
        // EN: This method handles extract package prefix from translation file.
        private static string ExtractPackagePrefixFromTranslationFile(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file) ?? "";
            int splitIdx = name.IndexOf("_AutoTranslated", StringComparison.OrdinalIgnoreCase);
            if (splitIdx == -1) splitIdx = name.LastIndexOf('_');
            if (splitIdx > 0) name = name.Substring(0, splitIdx);
            return NormalizePackageForComparison(name);
        }

        // 這個方法負責取得 TranslatedFilePackageIdsCached 資料。
        // EN: This method gets translated file package ids cached.
        private static HashSet<string> GetTranslatedFilePackageIdsCached()
        {
            TargetLanguage targetLang = AutoTranslatorMod.Settings.TargetLang;
            float now = NowSeconds();
            int generation;
            lock (_cacheLock)
            {
                if (_translatedFilePackageIds != null &&
                    _translatedFileCacheLang == targetLang &&
                    now - _lastTranslatedFileCacheTime <= TranslatedFileCacheSeconds)
                {
                    return _translatedFilePackageIds;
                }

                generation = _cacheGeneration;
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var latestTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string packPath = AutoTranslatorScanner.GetLocalPackPath();
                string targetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(targetLang);
                string langRoot = Path.Combine(packPath, "Languages", targetLangFolder);
                if (Directory.Exists(langRoot))
                {
                    List<string> knownPackageIds = ModLister.AllInstalledMods
                        .Where(m => !string.IsNullOrEmpty(m.PackageId))
                        .Select(m => NormalizePackageForComparison(m.PackageId))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(id => id.Length)
                        .ToList();

                    foreach (var file in Directory.GetFiles(langRoot, "*.xml", SearchOption.AllDirectories))
                    {
                        RememberTranslationFile(result, latestTicks, MatchPackagePrefixFromTranslationFile(file, knownPackageIds), file);
                    }
                }

            }
            catch { }

            lock (_cacheLock)
            {
                if (generation == _cacheGeneration)
                {
                    _translatedFilePackageIds = result;
                    _translatedFileLatestTicksByPackageId = latestTicks;
                    _translatedFileCacheLang = targetLang;
                    _lastTranslatedFileCacheTime = now;
                    return _translatedFilePackageIds;
                }
            }

            return result;
        }

        // 這個方法負責處理 Remember翻譯File 相關流程。
        // EN: This method handles remember translation file.
        private static void RememberTranslationFile(HashSet<string> result, Dictionary<string, long> latestTicks, string packagePrefix, string file)
        {
            if (string.IsNullOrEmpty(packagePrefix)) return;

            result.Add(packagePrefix);

            long ticks = 0L;
            try
            {
                ticks = new FileInfo(file).LastWriteTimeUtc.Ticks;
            }
            catch { }

            if (!latestTicks.TryGetValue(packagePrefix, out long existingTicks) || ticks > existingTicks)
            {
                latestTicks[packagePrefix] = ticks;
            }
        }

        // 這個方法負責處理 MatchPackagePrefixFrom翻譯File 相關流程。
        // EN: This method handles match package prefix from translation file.
        private static string MatchPackagePrefixFromTranslationFile(string file, List<string> knownPackageIds)
        {
            string name = NormalizePackageForComparison(Path.GetFileNameWithoutExtension(file) ?? "");
            if (!string.IsNullOrEmpty(name) && knownPackageIds != null)
            {
                foreach (string packageId in knownPackageIds)
                {
                    if (string.IsNullOrEmpty(packageId)) continue;
                    if (name.Equals(packageId, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(packageId + "_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(packageId + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        return packageId;
                    }
                }
            }

            return ExtractPackagePrefixFromTranslationFile(file);
        }

        // 這個方法負責清除 Status快取 資料。
        // EN: This method clears status cache.
        public static void ClearStatusCache()
        {
            lock (_cacheLock)
            {
                _cacheGeneration++;
                _cachedMods = null;
                _lastCheckTime = 0f;
                _translatedFilePackageIds = null;
                _translatedFileLatestTicksByPackageId = null;
                _sourceSnapshotCache.Clear();
                _statusCache.Clear();
                _isUpdatedListRefreshing = false;
                _updatedListRefreshGeneration++;
                _updatedListRefreshError = null;
            }
            AutoTranslatorScanner.ClearExternalTargetLanguagePatchCache();
        }

        // 這個方法負責判斷 HasLocal翻譯Files 條件是否成立。
        // EN: This method checks has local translation files.
        public static bool HasLocalTranslationFiles(ModMetaData mod)
        {
            if (mod == null) return false;
            return GetTranslatedFilePackageIdsCached().Contains(NormalizePackageForComparison(mod.PackageId));
        }

        public static string NormalizePackageIdForTranslationFileLookup(string packageId)
        {
            return NormalizePackageForComparison(packageId);
        }

        public static HashSet<string> GetPackageIdsWithLocalTranslationFilesForKnownPackages(IEnumerable<string> packageIds, TargetLanguage targetLang)
        {
            var knownPackageIds = (packageIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(NormalizePackageForComparison)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(id => id.Length)
                .ToList();

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (knownPackageIds.Count == 0) return result;

            try
            {
                string packPath = AutoTranslatorScanner.GetLocalPackPath();
                string targetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(targetLang);
                string langRoot = Path.Combine(packPath, "Languages", targetLangFolder);
                if (!Directory.Exists(langRoot)) return result;

                foreach (string file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(langRoot, SearchOption.AllDirectories))
                {
                    string matched = MatchPackagePrefixFromTranslationFile(file, knownPackageIds);
                    if (!string.IsNullOrEmpty(matched) && knownPackageIds.Contains(matched))
                    {
                        result.Add(matched);
                    }
                }
            }
            catch { }

            return result;
        }

        private static LocalTranslationFilesSnapshot BuildLocalTranslationFilesSnapshot(IEnumerable<string> packageIds, TargetLanguage targetLang)
        {
            var knownPackageIds = (packageIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(NormalizePackageForComparison)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(id => id.Length)
                .ToList();

            LocalTranslationFilesSnapshot snapshot = new LocalTranslationFilesSnapshot();
            if (knownPackageIds.Count == 0) return snapshot;

            try
            {
                string packPath = AutoTranslatorScanner.GetLocalPackPath();
                string targetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(targetLang);
                string langRoot = Path.Combine(packPath, "Languages", targetLangFolder);
                if (!Directory.Exists(langRoot)) return snapshot;

                foreach (string file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(langRoot, SearchOption.AllDirectories))
                {
                    string matched = MatchPackagePrefixFromTranslationFile(file, knownPackageIds);
                    if (string.IsNullOrEmpty(matched) || !knownPackageIds.Contains(matched)) continue;
                    RememberTranslationFile(snapshot.PackageIds, snapshot.LatestTicksByPackageId, matched, file);
                }
            }
            catch { }

            return snapshot;
        }

        // 這個方法負責嘗試執行 GetLocal翻譯LatestTicks 並回報是否成功。
        // EN: This method tries to get local translation latest ticks and reports whether it succeeded.
        private static bool TryGetLocalTranslationLatestTicks(ModMetaData mod, out long latestTicks)
        {
            latestTicks = 0L;
            if (mod == null) return false;

            GetTranslatedFilePackageIdsCached();
            lock (_cacheLock)
            {
                if (_translatedFileLatestTicksByPackageId == null) return false;

                return _translatedFileLatestTicksByPackageId.TryGetValue(
                    NormalizePackageForComparison(mod.PackageId),
                    out latestTicks);
            }
        }

        // 這個方法負責取得 SourceSnapshot 資料。
        // EN: This method gets source snapshot.
        private static SourceSnapshot GetSourceSnapshot(ModMetaData mod)
        {
            if (mod == null || string.IsNullOrEmpty(mod.PackageId)) return new SourceSnapshot();

            string cacheKey = GetModCacheKey(mod);
            float now = NowSeconds();
            int generation;
            lock (_cacheLock)
            {
                if (_sourceSnapshotCache.TryGetValue(cacheKey, out SourceSnapshot cached) &&
                    now - cached.CachedAt <= SourceFingerprintCacheSeconds)
                {
                    return cached;
                }

                generation = _cacheGeneration;
            }

            SourceSnapshot snapshot = BuildSourceSnapshot(mod);
            snapshot.CachedAt = now;
            lock (_cacheLock)
            {
                if (generation == _cacheGeneration)
                {
                    _sourceSnapshotCache[cacheKey] = snapshot;
                }
            }
            return snapshot;
        }

        // 這個方法負責建立 SourceSnapshot 所需資料。
        // EN: This method builds source snapshot.
        private static SourceSnapshot BuildSourceSnapshot(ModMetaData mod)
        {
            if (mod == null || mod.RootDir == null) return new SourceSnapshot();
            SourceSnapshot snapshot = BuildSourceSnapshot(
                mod.PackageId,
                mod.RootDir.FullName,
                AutoTranslatorMod.Settings.TargetLang,
                AutoTranslatorScanner.HasActiveExternalTargetLanguagePatch(mod, AutoTranslatorMod.Settings.TargetLang));
            return snapshot;
        }

        private static SourceSnapshot BuildSourceSnapshot(string packageId, string rootDir, TargetLanguage targetLang, bool hasExternalTargetLanguagePatch)
        {
            var snapshot = new SourceSnapshot();
            var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (string.IsNullOrWhiteSpace(rootDir)) return snapshot;

                AddIfExists(files, Path.Combine(rootDir, "About", "About.xml"));
                AddIfExists(files, Path.Combine(rootDir, "LoadFolders.xml"));

                foreach (string langRoot in AutoTranslatorScanner.GetAllEffectiveLangPaths(packageId, rootDir))
                {
                    foreach (string keyedPath in AutoTranslatorScanner.GetTranslatableLanguageBucketPaths(langRoot, targetLang, "Keyed", false))
                    {
                        AddXmlFiles(files, keyedPath, snapshot);
                    }

                    foreach (string defInjectedPath in AutoTranslatorScanner.GetTranslatableLanguageBucketPaths(langRoot, targetLang, "DefInjected", false))
                    {
                        AddXmlFiles(files, defInjectedPath, snapshot);
                    }

                    if (!snapshot.HasNativeTargetLanguage)
                    {
                        snapshot.HasNativeTargetLanguage = AutoTranslatorScanner.HasNativeTargetLanguage(packageId, rootDir, targetLang);
                    }
                }

                snapshot.HasExternalTargetLanguagePatch = hasExternalTargetLanguagePatch;

                foreach (string defsRoot in AutoTranslatorScanner.GetAllEffectiveDefsPaths(packageId, rootDir))
                {
                    AddXmlFiles(files, defsRoot, snapshot);
                }
            }
            catch { }

            snapshot.HasSourceFiles = files.Count > 0;
            if (files.Count == 0)
            {
                snapshot.Fingerprint = "no-source";
                return snapshot;
            }

            var builder = new StringBuilder();
            foreach (string file in files)
            {
                try
                {
                    FileInfo info = new FileInfo(file);
                    if (!info.Exists) continue;

                    snapshot.LatestTicks = Math.Max(snapshot.LatestTicks, info.LastWriteTimeUtc.Ticks);
                    string relative = MakeRelativePath(rootDir, info.FullName);
                    builder.Append(relative.ToLowerInvariant())
                        .Append('|')
                        .Append(info.Length)
                        .Append('|')
                        .Append(info.LastWriteTimeUtc.Ticks)
                        .Append('\n');
                }
                catch { }
            }

            snapshot.Fingerprint = ComputeStableHash(builder.ToString());
            return snapshot;
        }

        // 這個方法負責處理 AddIfExists 相關流程。
        // EN: This method handles add if exists.
        private static void AddIfExists(ISet<string> files, string path)
        {
            if (File.Exists(path)) files.Add(Path.GetFullPath(path));
        }

        // 這個方法負責處理 AddXmlFiles 相關流程。
        // EN: This method handles add XML files.
        private static void AddXmlFiles(ISet<string> files, string dir, SourceSnapshot snapshot = null)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (string file in Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories))
            {
                files.Add(Path.GetFullPath(file));
                if (snapshot != null)
                {
                    snapshot.HasTranslatableSourceFiles = true;
                }
            }
        }

        // 這個方法負責判斷 HasAnyXmlFile 條件是否成立。
        // EN: This method checks has any XML file.
        private static bool HasAnyXmlFile(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            try
            {
                return Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        // 這個方法負責處理 MakeRelative路徑 相關流程。
        // EN: This method handles make relative path.
        private static string MakeRelativePath(string root, string fullPath)
        {
            try
            {
                string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string normalizedPath = Path.GetFullPath(fullPath);
                if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return normalizedPath.Substring(normalizedRoot.Length);
                }
            }
            catch { }

            return Path.GetFileName(fullPath) ?? fullPath;
        }

        // 這個方法負責處理 ComputeStableHash 相關流程。
        // EN: This method handles compute stable hash.
        private static string ComputeStableHash(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                return BitConverter.ToString(bytes).Replace("-", "");
            }
        }

        // 這個方法負責取得 翻譯Status 資料。
        // EN: This method gets translation status.
        public static List<InstalledModStatusSnapshot> CreateInstalledModStatusSnapshots(IEnumerable<ModMetaData> mods)
        {
            return (mods ?? Enumerable.Empty<ModMetaData>())
                .Where(m => m != null && m.Active && !string.IsNullOrWhiteSpace(m.PackageId))
                .Select(m => new InstalledModStatusSnapshot
                {
                    PackageId = m.PackageId,
                    Name = m.Name ?? "",
                    RootDir = m.RootDir != null ? m.RootDir.FullName : "",
                    Active = m.Active,
                    IsTranslationPatchMod = AutoTranslatorScanner.IsTranslationPatchMod(m)
                })
                .ToList();
        }

        public static TranslationStatusCheckSnapshot CreateTranslationStatusCheckSnapshot(
            ModMetaData mod,
            List<InstalledModStatusSnapshot> activeMods)
        {
            if (mod == null || string.IsNullOrWhiteSpace(mod.PackageId)) return null;

            return new TranslationStatusCheckSnapshot
            {
                PackageId = mod.PackageId,
                RootDir = mod.RootDir != null ? mod.RootDir.FullName : "",
                TargetLang = AutoTranslatorMod.Settings.TargetLang,
                SavedTicks = AutoTranslatorMod.Settings.ModLastVerifiedTimes != null
                    ? new Dictionary<string, long>(AutoTranslatorMod.Settings.ModLastVerifiedTimes, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
                SavedFingerprints = AutoTranslatorMod.Settings.ModLastVerifiedFingerprints != null
                    ? new Dictionary<string, string>(AutoTranslatorMod.Settings.ModLastVerifiedFingerprints, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ActiveMods = activeMods != null
                    ? new List<InstalledModStatusSnapshot>(activeMods)
                    : new List<InstalledModStatusSnapshot>()
            };
        }

        public static SourceFingerprintSnapshot BuildSourceFingerprintSnapshot(string packageId, string rootDir, TargetLanguage targetLang)
        {
            SourceSnapshot source = BuildSourceSnapshot(packageId, rootDir, targetLang, false);
            return new SourceFingerprintSnapshot
            {
                LatestTicks = source.LatestTicks,
                Fingerprint = source.Fingerprint
            };
        }

        public static void MarkModAsTranslatedSnapshot(string packageId, SourceFingerprintSnapshot source, bool refreshStatusCache = true)
        {
            if (string.IsNullOrWhiteSpace(packageId) || source == null) return;

            if (AutoTranslatorMod.Settings.ModLastVerifiedTimes == null)
            {
                AutoTranslatorMod.Settings.ModLastVerifiedTimes = new Dictionary<string, long>();
            }
            if (AutoTranslatorMod.Settings.ModLastVerifiedFingerprints == null)
            {
                AutoTranslatorMod.Settings.ModLastVerifiedFingerprints = new Dictionary<string, string>();
            }

            AutoTranslatorMod.Settings.ModLastVerifiedTimes[packageId] = source.LatestTicks;
            AutoTranslatorMod.Settings.ModLastVerifiedFingerprints[packageId] = source.Fingerprint;
            LoadedModManager.GetMod<AutoTranslatorMod>().WriteSettings();

            if (refreshStatusCache)
            {
                ClearStatusCache();
            }
        }

        public static ModTranslationStatus GetTranslationStatus(TranslationStatusCheckSnapshot check)
        {
            if (check == null || string.IsNullOrWhiteSpace(check.PackageId)) return ModTranslationStatus.Untranslated;

            string cacheKey = GetModCacheKey(check.TargetLang, check.PackageId);
            float now = NowSeconds();
            int generation;
            lock (_cacheLock)
            {
                if (_statusCache.TryGetValue(cacheKey, out StatusSnapshot cached) &&
                    now - cached.CachedAt <= StatusCacheSeconds)
                {
                    return cached.Status;
                }

                generation = _cacheGeneration;
            }

            ModTranslationStatus status = GetTranslationStatusUncached(check);
            lock (_cacheLock)
            {
                if (generation == _cacheGeneration)
                {
                    _statusCache[cacheKey] = new StatusSnapshot
                    {
                        Status = status,
                        CachedAt = now
                    };
                }
            }
            return status;
        }

        private static ModTranslationStatus GetTranslationStatusUncached(TranslationStatusCheckSnapshot check)
        {
            List<string> knownPackageIds = (check.ActiveMods ?? new List<InstalledModStatusSnapshot>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.PackageId))
                .Select(m => m.PackageId)
                .Concat(new[] { check.PackageId })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            LocalTranslationFilesSnapshot localFiles = BuildLocalTranslationFilesSnapshot(knownPackageIds, check.TargetLang);
            SourceSnapshot source = BuildSourceSnapshot(
                check.PackageId,
                check.RootDir,
                check.TargetLang,
                HasActiveExternalTargetLanguagePatch(check));

            bool hasLocalFiles = localFiles.PackageIds.Contains(NormalizePackageForComparison(check.PackageId));

            if (source.HasNativeTargetLanguage || source.HasExternalTargetLanguagePatch)
            {
                return ModTranslationStatus.Translated;
            }

            if (!source.HasTranslatableSourceFiles)
            {
                return ModTranslationStatus.Filtered;
            }

            if (TryGetSavedFingerprint(check.SavedFingerprints, check.PackageId, out string savedFingerprint))
            {
                return string.Equals(source.Fingerprint, savedFingerprint, StringComparison.Ordinal)
                    ? ModTranslationStatus.Translated
                    : ModTranslationStatus.PossiblyOutdated;
            }

            if (TryGetSavedTick(check.SavedTicks, check.PackageId, out long savedTicks))
            {
                return source.LatestTicks > savedTicks
                    ? ModTranslationStatus.PossiblyOutdated
                    : ModTranslationStatus.Translated;
            }

            if (hasLocalFiles)
            {
                string normalizedPackageId = NormalizePackageForComparison(check.PackageId);
                if (source.LatestTicks <= 0L ||
                    (localFiles.LatestTicksByPackageId.TryGetValue(normalizedPackageId, out long localTicks) && localTicks >= source.LatestTicks))
                {
                    return ModTranslationStatus.Translated;
                }

                return ModTranslationStatus.PossiblyOutdated;
            }

            return ModTranslationStatus.Untranslated;
        }

        private static bool HasActiveExternalTargetLanguagePatch(TranslationStatusCheckSnapshot check)
        {
            if (check == null || string.IsNullOrWhiteSpace(check.PackageId) || check.ActiveMods == null) return false;

            string targetPackageId = (check.PackageId ?? "").Trim().ToLowerInvariant();
            List<KeyValuePair<string, string>> targetCandidates = check.ActiveMods
                .Where(m => m != null && m.Active && !m.IsTranslationPatchMod && !string.IsNullOrWhiteSpace(m.PackageId))
                .Select(m => new KeyValuePair<string, string>(m.PackageId ?? "", m.Name ?? ""))
                .ToList();

            foreach (InstalledModStatusSnapshot patchMod in check.ActiveMods)
            {
                if (patchMod == null || !patchMod.Active || !patchMod.IsTranslationPatchMod) continue;
                if (string.IsNullOrWhiteSpace(patchMod.RootDir) || string.IsNullOrWhiteSpace(patchMod.PackageId)) continue;
                if (!AutoTranslatorScanner.HasTranslationPatchTargetLanguage(patchMod.PackageId, patchMod.RootDir, check.TargetLang)) continue;

                foreach (string referencedPackageId in AutoTranslatorScanner.GetReferencedTargetPackageIdsFromRoot(patchMod.RootDir))
                {
                    if (string.Equals(referencedPackageId, targetPackageId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                foreach (string inferredPackageId in AutoTranslatorScanner.InferExternalPatchTargetPackageIds(
                             patchMod.PackageId,
                             patchMod.Name,
                             targetCandidates))
                {
                    if (string.Equals(inferredPackageId, targetPackageId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static ModTranslationStatus GetTranslationStatus(ModMetaData mod)
        {
            if (mod == null) return ModTranslationStatus.Untranslated;

            string cacheKey = GetModCacheKey(mod);
            float now = NowSeconds();
            int generation;
            lock (_cacheLock)
            {
                if (_statusCache.TryGetValue(cacheKey, out StatusSnapshot cached) &&
                    now - cached.CachedAt <= StatusCacheSeconds)
                {
                    return cached.Status;
                }

                generation = _cacheGeneration;
            }

            ModTranslationStatus status = GetTranslationStatusUncached(mod);
            lock (_cacheLock)
            {
                if (generation == _cacheGeneration)
                {
                    _statusCache[cacheKey] = new StatusSnapshot
                    {
                        Status = status,
                        CachedAt = now
                    };
                }
            }
            return status;
        }

        // 這個方法負責取得 翻譯StatusUncached 資料。
        // EN: This method gets translation status uncached.
        private static ModTranslationStatus GetTranslationStatusUncached(ModMetaData mod)
        {
            SourceSnapshot source = GetSourceSnapshot(mod);
            bool hasLocalFiles = HasLocalTranslationFiles(mod);

            if (source.HasNativeTargetLanguage || source.HasExternalTargetLanguagePatch)
            {
                return ModTranslationStatus.Translated;
            }

            if (!source.HasTranslatableSourceFiles)
            {
                return ModTranslationStatus.Filtered;
            }

            if (TryGetSavedFingerprint(mod.PackageId, out string savedFingerprint))
            {
                return string.Equals(source.Fingerprint, savedFingerprint, StringComparison.Ordinal)
                    ? ModTranslationStatus.Translated
                    : ModTranslationStatus.PossiblyOutdated;
            }

            if (TryGetSavedTick(mod.PackageId, out long savedTicks))
            {
                return source.LatestTicks > savedTicks
                    ? ModTranslationStatus.PossiblyOutdated
                    : ModTranslationStatus.Translated;
            }

            if (hasLocalFiles)
            {
                if (source.LatestTicks <= 0L ||
                    (TryGetLocalTranslationLatestTicks(mod, out long localTicks) && localTicks >= source.LatestTicks))
                {
                    return ModTranslationStatus.Translated;
                }

                return ModTranslationStatus.PossiblyOutdated;
            }

            return ModTranslationStatus.Untranslated;
        }

        // 這個方法負責取得 翻譯StatusLabelKey 資料。
        // EN: This method gets translation status label key.
        public static string GetTranslationStatusLabelKey(ModTranslationStatus status)
        {
            switch (status)
            {
                case ModTranslationStatus.Translated: return "ATC_ModStatus_Translated";
                case ModTranslationStatus.PossiblyOutdated: return "ATC_ModStatus_Outdated";
                case ModTranslationStatus.Filtered: return "ATC_ModStatus_Filtered";
                default: return "ATC_ModStatus_Untranslated";
            }
        }

        // 這個方法負責取得 翻譯StatusColorHex 資料。
        // EN: This method gets translation status color hex.
        public static string GetTranslationStatusColorHex(ModTranslationStatus status)
        {
            switch (status)
            {
                case ModTranslationStatus.Translated: return "#76D66A";
                case ModTranslationStatus.PossiblyOutdated: return "#E6C35C";
                case ModTranslationStatus.Filtered: return "#8A8A8A";
                default: return "#9A9A9A";
            }
        }

        public static bool TryGetCachedTranslationStatus(ModMetaData mod, out ModTranslationStatus status)
        {
            status = ModTranslationStatus.Untranslated;
            if (mod == null) return false;

            string cacheKey = GetModCacheKey(mod);
            float now = NowSeconds();
            lock (_cacheLock)
            {
                if (_statusCache.TryGetValue(cacheKey, out StatusSnapshot cached) &&
                    now - cached.CachedAt <= StatusCacheSeconds)
                {
                    status = cached.Status;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetSavedTick(Dictionary<string, long> dict, string packageId, out long savedTicks)
        {
            savedTicks = 0L;
            if (dict == null || string.IsNullOrEmpty(packageId)) return false;
            if (dict.TryGetValue(packageId, out savedTicks)) return true;

            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, packageId, StringComparison.OrdinalIgnoreCase))
                {
                    savedTicks = kv.Value;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetSavedFingerprint(Dictionary<string, string> dict, string packageId, out string savedFingerprint)
        {
            savedFingerprint = null;
            if (dict == null || string.IsNullOrEmpty(packageId)) return false;
            if (dict.TryGetValue(packageId, out savedFingerprint)) return !string.IsNullOrEmpty(savedFingerprint);

            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, packageId, StringComparison.OrdinalIgnoreCase))
                {
                    savedFingerprint = kv.Value;
                    return !string.IsNullOrEmpty(savedFingerprint);
                }
            }

            return false;
        }

        // 這個方法負責取得 UpdatedOrNew模組Cached 資料。
        // EN: This method gets updated or new mods cached.
        public static List<ModMetaData> GetUpdatedOrNewModsCached()
        {
            float now = NowSeconds();
            List<ModMetaData> snapshot;
            bool shouldRefresh = false;

            lock (_cacheLock)
            {
                snapshot = _cachedMods != null
                    ? new List<ModMetaData>(_cachedMods)
                    : new List<ModMetaData>();

                if (!_isUpdatedListRefreshing &&
                    (_cachedMods == null || now - _lastCheckTime > UpdatedListCacheSeconds))
                {
                    shouldRefresh = true;
                }
            }

            if (shouldRefresh)
            {
                StartUpdatedListRefresh();
            }

            return snapshot;
        }

        public static List<ModMetaData> GetUpdatedOrNewModsBlocking()
        {
            float now = NowSeconds();
            lock (_cacheLock)
            {
                if (_cachedMods != null && now - _lastCheckTime <= UpdatedListCacheSeconds)
                {
                    return new List<ModMetaData>(_cachedMods);
                }
            }

            List<ModMetaData> result = GetUpdatedOrNewModsForce();
            lock (_cacheLock)
            {
                _cachedMods = new List<ModMetaData>(result);
                _lastCheckTime = NowSeconds();
                _updatedListRefreshError = null;
                return new List<ModMetaData>(_cachedMods);
            }
        }

        public static void RequestUpdatedListRefresh()
        {
            StartUpdatedListRefresh();
        }

        private static void StartUpdatedListRefresh()
        {
            int generation;
            lock (_cacheLock)
            {
                if (_isUpdatedListRefreshing) return;
                _isUpdatedListRefreshing = true;
                generation = ++_updatedListRefreshGeneration;
            }

            Task.Run(() =>
            {
                List<ModMetaData> result = null;
                string error = null;
                try
                {
                    result = GetUpdatedOrNewModsForce();
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Verse.Log.Warning($"[AutoTranslationCore] Background mod update scan failed: {ex.Message}");
                }

                lock (_cacheLock)
                {
                    if (generation == _updatedListRefreshGeneration && result != null)
                    {
                        _cachedMods = new List<ModMetaData>(result);
                        _lastCheckTime = NowSeconds();
                    }

                    if (generation == _updatedListRefreshGeneration)
                    {
                        _updatedListRefreshError = error;
                        _isUpdatedListRefreshing = false;
                    }
                }
            });
        }

        // 這個方法負責取得 UpdatedOrNew模組Force 資料。
        // EN: This method gets updated or new mods force.
        private static List<ModMetaData> GetUpdatedOrNewModsForce()
        {
            var result = new List<ModMetaData>();
            AutoTranslatorSettings.FilteredModsCount = 0;

            foreach (var mod in ModLister.AllInstalledMods.Where(m => m.Active))
            {
                string pid = (mod.PackageId ?? "").ToLowerInvariant();

                if (pid == "auto.aitranslation.core" ||
                    pid == "aitranslation.pack" ||
                    pid.StartsWith("ludeon.rimworld") ||
                    AutoTranslatorScanner.IsTranslationPatchMod(mod))
                {
                    continue;
                }

                if (IsCodeOnlyMod(mod))
                {
                    AutoTranslatorSettings.FilteredModsCount++;
                    continue;
                }

                ModTranslationStatus status = GetTranslationStatus(mod);
                if (status != ModTranslationStatus.Translated && status != ModTranslationStatus.Filtered)
                {
                    result.Add(mod);
                }
            }
            return result;
        }

        // 這個方法負責標記 模組AsTranslated 狀態。
        // EN: This method marks mod as translated.
        public static void MarkModAsTranslated(string packageId, string rootDir, bool refreshStatusCache = true)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return;

            string effectiveRootDir = rootDir ?? "";
            var meta = ModLister.AllInstalledMods.FirstOrDefault(m =>
                string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(effectiveRootDir) && meta != null && meta.RootDir != null)
            {
                effectiveRootDir = meta.RootDir.FullName;
            }

            SourceSnapshot source = BuildSourceSnapshot(
                packageId,
                effectiveRootDir,
                AutoTranslatorMod.Settings.TargetLang,
                false);
            if (AutoTranslatorMod.Settings.ModLastVerifiedTimes == null)
            {
                AutoTranslatorMod.Settings.ModLastVerifiedTimes = new Dictionary<string, long>();
            }
            if (AutoTranslatorMod.Settings.ModLastVerifiedFingerprints == null)
            {
                AutoTranslatorMod.Settings.ModLastVerifiedFingerprints = new Dictionary<string, string>();
            }

            AutoTranslatorMod.Settings.ModLastVerifiedTimes[packageId] = source.LatestTicks;
            AutoTranslatorMod.Settings.ModLastVerifiedFingerprints[packageId] = source.Fingerprint;
            LoadedModManager.GetMod<AutoTranslatorMod>().WriteSettings();

            if (refreshStatusCache)
            {
                ClearStatusCache();
            }
        }
    }
}
