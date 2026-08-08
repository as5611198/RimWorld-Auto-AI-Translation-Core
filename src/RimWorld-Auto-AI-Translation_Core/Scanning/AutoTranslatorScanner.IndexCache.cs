using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private static readonly object TranslationIndexCacheLock = new object();
        private static readonly Dictionary<string, XmlParseCacheEntry> XmlParseCache =
            new Dictionary<string, XmlParseCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, XmlFileListCacheEntry> XmlFileListCache =
            new Dictionary<string, XmlFileListCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ModPathIndexCacheEntry> ModPathIndexCache =
            new Dictionary<string, ModPathIndexCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, LanguageFolderResolveCacheEntry> LanguageFolderResolveCache =
            new Dictionary<string, LanguageFolderResolveCacheEntry>(StringComparer.OrdinalIgnoreCase);

        private class XmlParseCacheEntry
        {
            public long Length;
            public long LastWriteTicks;
            public bool ParseFailed;
            public Dictionary<string, string> Data;
        }

        private class XmlFileListCacheEntry
        {
            public string Fingerprint;
            public List<string> Files;
        }

        private class ModPathIndexCacheEntry
        {
            public string Key;
            public List<string> EffectiveLangPaths;
            public List<string> TranslationPatchLangPaths;
            public List<string> EffectiveDefsPaths;
        }

        private class LanguageFolderResolveCacheEntry
        {
            public long LastWriteTicks;
            public List<string> Matches;
        }

        private static Dictionary<string, string> LoadRawXmlFileToDictCached(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return new Dictionary<string, string>();
            }

            string fullPath = NormalizeCachePath(filePath);
            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
            }
            catch
            {
                return new Dictionary<string, string>();
            }

            lock (TranslationIndexCacheLock)
            {
                if (XmlParseCache.TryGetValue(fullPath, out XmlParseCacheEntry cached) &&
                    cached.Length == info.Length &&
                    cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
                {
                    return cached.ParseFailed || cached.Data == null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(cached.Data);
                }
            }

            bool parsed = TryParseXmlFileToDict(fullPath, out Dictionary<string, string> parsedData);
            lock (TranslationIndexCacheLock)
            {
                XmlParseCache[fullPath] = new XmlParseCacheEntry
                {
                    Length = info.Length,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                    ParseFailed = !parsed,
                    Data = parsed ? new Dictionary<string, string>(parsedData) : new Dictionary<string, string>()
                };
            }

            return parsed ? parsedData : new Dictionary<string, string>();
        }

        private static bool TryParseXmlFileToDict(string filePath, out Dictionary<string, string> dict)
        {
            dict = new Dictionary<string, string>();

            try
            {
                XmlReaderSettings settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(filePath, settings))
                {
                    doc.Load(reader);
                }
                if (doc.DocumentElement == null ||
                    !doc.DocumentElement.Name.Equals("LanguageData", StringComparison.Ordinal))
                {
                    return false;
                }

                dict = TranslationXmlDictionaryParser.Parse(doc.DocumentElement);

                return true;
            }
            catch
            {
                dict.Clear();
                return false;
            }
        }

        internal static bool TryLoadRawXmlFileToDict(
            string filePath,
            out Dictionary<string, string> dict)
        {
            dict = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

            try
            {
                return TryParseXmlFileToDict(NormalizeCachePath(filePath), out dict);
            }
            catch
            {
                dict.Clear();
                return false;
            }
        }

        public static void NotifyTranslationFileChanged(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            InvalidateXmlFileCache(filePath);
            InvalidateXmlFileListCachesForChangedPath(filePath);
            InvalidateLanguageFolderResolveCachesForChangedPath(filePath);
            ResetKeyedMemoryDropStamp();
            InvalidateGlobalTranslationDatabaseCache();
        }

        public static void NotifyTranslationFilesChanged(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                ResetKeyedMemoryDropStamp();
                InvalidateGlobalTranslationDatabaseCache();
                return;
            }

            InvalidateXmlCachesUnder(rootPath);
            InvalidateXmlFileListCachesForChangedPath(rootPath);
            InvalidateLanguageFolderResolveCachesForChangedPath(rootPath);
            ResetKeyedMemoryDropStamp();
            InvalidateGlobalTranslationDatabaseCache();
        }

        public static List<string> GetXmlFilesForTranslationCache(string path, SearchOption searchOption)
        {
            return GetXmlFilesCached(path, searchOption);
        }

        private static void InvalidateXmlFileCache(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            string fullPath = NormalizeCachePath(filePath);
            lock (TranslationIndexCacheLock)
            {
                XmlParseCache.Remove(fullPath);
            }
        }

        private static void InvalidateXmlCachesUnder(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath)) return;

            string fullRoot = NormalizeCachePath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            lock (TranslationIndexCacheLock)
            {
                foreach (string key in XmlParseCache.Keys.Where(k => k.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    XmlParseCache.Remove(key);
                }
            }
        }

        private static List<string> GetXmlFilesCached(string path, SearchOption searchOption)
        {
            List<string> files = new List<string>();
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return files;

            string fullPath = NormalizeCachePath(path);
            string cacheKey = fullPath + "|" + searchOption;
            string fingerprint = BuildDirectoryListingFingerprint(fullPath, searchOption);

            lock (TranslationIndexCacheLock)
            {
                if (XmlFileListCache.TryGetValue(cacheKey, out XmlFileListCacheEntry cached) &&
                    string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    List<string> existingFiles = cached.Files.Where(File.Exists).ToList();
                    if (existingFiles.Count == cached.Files.Count)
                    {
                        return new List<string>(existingFiles);
                    }

                    XmlFileListCache.Remove(cacheKey);
                }
            }

            try
            {
                files = Directory.GetFiles(fullPath, "*.xml", searchOption).ToList();
            }
            catch
            {
                files = new List<string>();
            }

            lock (TranslationIndexCacheLock)
            {
                XmlFileListCache[cacheKey] = new XmlFileListCacheEntry
                {
                    Fingerprint = fingerprint,
                    Files = new List<string>(files)
                };
            }

            return files;
        }

        private static void InvalidateXmlFileListCachesForChangedPath(string changedPath)
        {
            if (string.IsNullOrEmpty(changedPath)) return;

            string fullChanged = NormalizeCachePath(changedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(fullChanged)) return;

            string parentPath = fullChanged;
            try
            {
                string parent = Path.GetDirectoryName(fullChanged);
                if (!string.IsNullOrEmpty(parent)) parentPath = NormalizeCachePath(parent);
            }
            catch { }

            lock (TranslationIndexCacheLock)
            {
                foreach (string key in XmlFileListCache.Keys.ToList())
                {
                    string root = key;
                    int separator = key.LastIndexOf('|');
                    if (separator > 0) root = key.Substring(0, separator);

                    string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (IsSameOrAncestorPath(normalizedRoot, fullChanged) ||
                        IsSameOrAncestorPath(normalizedRoot, parentPath) ||
                        IsSameOrAncestorPath(fullChanged, normalizedRoot))
                    {
                        XmlFileListCache.Remove(key);
                    }
                }
            }
        }

        private static void InvalidateLanguageFolderResolveCachesForChangedPath(string changedPath)
        {
            if (string.IsNullOrEmpty(changedPath)) return;

            string fullChanged = NormalizeCachePath(changedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(fullChanged)) return;

            lock (TranslationIndexCacheLock)
            {
                foreach (string key in LanguageFolderResolveCache.Keys.ToList())
                {
                    string root = key;
                    int separator = key.LastIndexOf('|');
                    if (separator > 0) root = key.Substring(0, separator);

                    string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (IsSameOrAncestorPath(normalizedRoot, fullChanged) ||
                        IsSameOrAncestorPath(fullChanged, normalizedRoot))
                    {
                        LanguageFolderResolveCache.Remove(key);
                    }
                }
            }
        }

        private static void ResetKeyedMemoryDropStamp()
        {
            lock (_memoryDropStampLock)
            {
                _lastKeyedMemoryDropStamp = null;
                _lastFullMemoryDropStamp = null;
            }
        }

        private static ModPathIndexCacheEntry GetModPathIndex(ModMetaData mod)
        {
            if (mod == null || mod.RootDir == null)
            {
                return new ModPathIndexCacheEntry
                {
                    Key = "",
                    EffectiveLangPaths = new List<string>(),
                    TranslationPatchLangPaths = new List<string>(),
                    EffectiveDefsPaths = new List<string>()
                };
            }

            return GetModPathIndex(mod.PackageId, mod.RootDir.FullName);
        }

        private static ModPathIndexCacheEntry GetModPathIndex(string packageId, string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir))
            {
                return new ModPathIndexCacheEntry
                {
                    Key = "",
                    EffectiveLangPaths = new List<string>(),
                    TranslationPatchLangPaths = new List<string>(),
                    EffectiveDefsPaths = new List<string>()
                };
            }

            string normalizedRoot = NormalizeCachePath(rootDir);
            string cacheId = (packageId ?? "") + "|" + normalizedRoot;
            string key = BuildModPathIndexKey(packageId, normalizedRoot);

            lock (TranslationIndexCacheLock)
            {
                if (ModPathIndexCache.TryGetValue(cacheId, out ModPathIndexCacheEntry cached) &&
                    string.Equals(cached.Key, key, StringComparison.Ordinal))
                {
                    return new ModPathIndexCacheEntry
                    {
                        Key = cached.Key,
                        EffectiveLangPaths = new List<string>(cached.EffectiveLangPaths),
                        TranslationPatchLangPaths = new List<string>(cached.TranslationPatchLangPaths),
                        EffectiveDefsPaths = new List<string>(cached.EffectiveDefsPaths)
                    };
                }
            }

            List<string> effectiveLangPaths = BuildEffectiveLangPathsUncached(normalizedRoot);
            List<string> translationPatchLangPaths = effectiveLangPaths.ToList();
            AddLanguageRootsFrom(normalizedRoot, normalizedRoot, translationPatchLangPaths, true);
            translationPatchLangPaths = translationPatchLangPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            ModPathIndexCacheEntry entry = new ModPathIndexCacheEntry
            {
                Key = key,
                EffectiveLangPaths = effectiveLangPaths,
                TranslationPatchLangPaths = translationPatchLangPaths,
                EffectiveDefsPaths = BuildEffectiveDefsPathsUncached(normalizedRoot)
            };

            lock (TranslationIndexCacheLock)
            {
                ModPathIndexCache[cacheId] = new ModPathIndexCacheEntry
                {
                    Key = entry.Key,
                    EffectiveLangPaths = new List<string>(entry.EffectiveLangPaths),
                    TranslationPatchLangPaths = new List<string>(entry.TranslationPatchLangPaths),
                    EffectiveDefsPaths = new List<string>(entry.EffectiveDefsPaths)
                };
            }

            return entry;
        }

        private static string BuildModPathIndexKey(ModMetaData mod)
        {
            return BuildModPathIndexKey(mod.PackageId, mod.RootDir.FullName);
        }

        private static string BuildModPathIndexKey(string packageId, string rootDir)
        {
            string root = NormalizeCachePath(rootDir);
            string loadFoldersPath = Path.Combine(root, "LoadFolders.xml");
            string loadFoldersSig = BuildFileSignature(loadFoldersPath);
            long rootTicks = Directory.Exists(root) ? Directory.GetLastWriteTimeUtc(root).Ticks : 0L;
            return CurrentRimWorldVersion + "|" + (packageId ?? "") + "|" + root + "|" + rootTicks + "|" + loadFoldersSig;
        }

        private static List<string> BuildEffectiveLangPathsUncached(ModMetaData mod)
        {
            return mod == null || mod.RootDir == null
                ? new List<string>()
                : BuildEffectiveLangPathsUncached(mod.RootDir.FullName);
        }

        private static List<string> BuildEffectiveLangPathsUncached(string rootDir)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(rootDir)) return result;

            string modRoot = NormalizeCachePath(rootDir);
            var activeRoots = ParseLoadFolders(modRoot);

            foreach (var root in activeRoots)
            {
                AddLanguageRootsFrom(root, modRoot, result);
            }

            AddLanguageRootsFrom(modRoot, modRoot, result);

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> BuildEffectiveDefsPathsUncached(ModMetaData mod)
        {
            return mod == null || mod.RootDir == null
                ? new List<string>()
                : BuildEffectiveDefsPathsUncached(mod.RootDir.FullName);
        }

        private static List<string> BuildEffectiveDefsPathsUncached(string rootDir)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(rootDir)) return result;

            string modRoot = NormalizeCachePath(rootDir);
            var activeRoots = ParseLoadFolders(modRoot);

            foreach (var root in activeRoots)
            {
                AddDefsRootsFrom(root, modRoot, result);
            }

            AddDefsRootsFrom(modRoot, modRoot, result);

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> ResolveLanguageFoldersCached(string langRoot, string folderName)
        {
            List<string> matches = new List<string>();
            if (string.IsNullOrEmpty(langRoot) || string.IsNullOrEmpty(folderName) || !Directory.Exists(langRoot)) return matches;

            string fullRoot = NormalizeCachePath(langRoot);
            string cacheKey = fullRoot + "|" + folderName;
            long ticks = 0L;
            try
            {
                ticks = Directory.GetLastWriteTimeUtc(fullRoot).Ticks;
            }
            catch { }

            lock (TranslationIndexCacheLock)
            {
                if (LanguageFolderResolveCache.TryGetValue(cacheKey, out LanguageFolderResolveCacheEntry cached) &&
                    cached.LastWriteTicks == ticks)
                {
                    return new List<string>(cached.Matches);
                }
            }

            string direct = Path.Combine(fullRoot, folderName);
            if (Directory.Exists(direct)) matches.Add(direct);

            try
            {
                foreach (string dir in Directory.GetDirectories(fullRoot))
                {
                    if (IsLanguageFolderMatch(Path.GetFileName(dir), folderName) &&
                        !matches.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    {
                        matches.Add(dir);
                    }
                }
            }
            catch { }

            lock (TranslationIndexCacheLock)
            {
                LanguageFolderResolveCache[cacheKey] = new LanguageFolderResolveCacheEntry
                {
                    LastWriteTicks = ticks,
                    Matches = new List<string>(matches)
                };
            }

            return matches;
        }

        private static string BuildXmlTreeFingerprint(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) return "missing";

            unchecked
            {
                long count = 0L;
                long totalLength = 0L;
                long latestTicks = 0L;
                long hash = 1469598103934665603L;

                try
                {
                    foreach (string file in GetXmlFilesCached(rootPath, SearchOption.AllDirectories))
                    {
                        FileInfo info;
                        try
                        {
                            info = new FileInfo(file);
                        }
                        catch
                        {
                            continue;
                        }

                        count++;
                        totalLength += info.Length;
                        latestTicks = Math.Max(latestTicks, info.LastWriteTimeUtc.Ticks);

                        string normalized = NormalizeCachePath(file);
                        hash = (hash ^ StringComparer.OrdinalIgnoreCase.GetHashCode(normalized)) * 1099511628211L;
                        hash = (hash ^ info.Length) * 1099511628211L;
                        hash = (hash ^ info.LastWriteTimeUtc.Ticks) * 1099511628211L;
                    }
                }
                catch { }

                return count + ":" + totalLength + ":" + latestTicks + ":" + hash;
            }
        }

        private static string BuildDirectoryListingFingerprint(string rootPath, SearchOption searchOption)
        {
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) return "missing";

            unchecked
            {
                long count = 0L;
                long latestTicks = 0L;
                long hash = 1469598103934665603L;

                Action<string> addDirectory = dir =>
                {
                    try
                    {
                        DirectoryInfo info = new DirectoryInfo(dir);
                        count++;
                        latestTicks = Math.Max(latestTicks, info.LastWriteTimeUtc.Ticks);

                        string normalized = NormalizeCachePath(dir);
                        hash = (hash ^ StringComparer.OrdinalIgnoreCase.GetHashCode(normalized)) * 1099511628211L;
                        hash = (hash ^ info.LastWriteTimeUtc.Ticks) * 1099511628211L;
                    }
                    catch { }
                };

                try
                {
                    addDirectory(rootPath);
                    if (searchOption == SearchOption.AllDirectories)
                    {
                        foreach (string dir in Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories))
                        {
                            addDirectory(dir);
                        }
                    }
                }
                catch { }

                return searchOption + ":" + count + ":" + latestTicks + ":" + hash;
            }
        }

        private static string BuildFileSignature(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return "missing";

            try
            {
                FileInfo info = new FileInfo(filePath);
                return info.Length + ":" + info.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                return "missing";
            }
        }

        private static string NormalizeCachePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static bool IsSameOrAncestorPath(string ancestor, string path)
        {
            if (string.IsNullOrEmpty(ancestor) || string.IsNullOrEmpty(path)) return false;

            string normalizedAncestor = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(normalizedAncestor, normalizedPath, StringComparison.OrdinalIgnoreCase)) return true;

            normalizedAncestor += Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(normalizedAncestor, StringComparison.OrdinalIgnoreCase);
        }
    }
}
