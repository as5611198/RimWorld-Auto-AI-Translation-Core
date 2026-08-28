using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class UITranslationManagedEntry
    {
        public string Original;
        public string Translation;
        public string PackageId;
        public bool IsIgnored;
    }

    public static partial class UIInterceptor
    {
        private static readonly object SourceOwnerMapLock = new object();
        private static List<SourceOwnerRoot> _sourceOwnerRoots = new List<SourceOwnerRoot>();
        private static int _sourceOwnerRootModCount = -1;

        public static int GetManagedEntryCount()
        {
            return Math.Max(SourceOwners.Count, Math.Max(Cache.Count, IgnoredCache.Count));
        }

        public static List<UITranslationManagedEntry> GetManagedEntries()
        {
            var entries = new Dictionary<string, UITranslationManagedEntry>(StringComparer.Ordinal);
            AddManagedEntries(Cache.Keys, entries);
            AddManagedEntries(IgnoredCache.Keys, entries);
            AddManagedEntries(SourceOwners.Keys, entries);

            foreach (var pair in entries)
            {
                string key = BuildCacheKey(pair.Key);
                Cache.TryGetValue(key, out pair.Value.Translation);
                SourceOwners.TryGetValue(key, out pair.Value.PackageId);
                pair.Value.IsIgnored = IgnoredCache.ContainsKey(key);
            }

            return entries.Values
                .OrderBy(entry => entry.PackageId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Original ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool TrySetManualTranslation(string original, string translated, out string error)
        {
            error = null;
            string cleanOriginal = GetTranslationLookupText(original);
            string cleanTranslated = SanitizeUITranslationResult(cleanOriginal, GetTranslationLookupText(translated));
            if (string.IsNullOrWhiteSpace(cleanOriginal) || string.IsNullOrWhiteSpace(cleanTranslated))
            {
                error = "ATC_UIManager_InvalidTranslation".Translate().ToString();
                return false;
            }

            if (string.Equals(cleanOriginal, cleanTranslated, StringComparison.Ordinal) ||
                ShouldSkipUITranslationText(cleanTranslated) ||
                !IsCachedTranslationCompatibleWithCurrentLanguage(cleanTranslated))
            {
                error = "ATC_UIManager_InvalidTranslation".Translate().ToString();
                return false;
            }

            string key = BuildCacheKey(cleanOriginal);
            Cache[key] = cleanTranslated;
            IgnoredCache.TryRemove(key, out _);
            PendingClassifications.TryRemove(key, out _);
            PendingTranslations.TryRemove(key, out _);
            MarkCacheDirty();
            MarkIgnoredCacheDirty();
            RefreshRuntimeUICache();
            SaveCacheIfDue(true);
            return true;
        }

        public static void RemoveManagedTranslation(string original)
        {
            string key = BuildCacheKey(GetTranslationLookupText(original));
            if (Cache.TryRemove(key, out _)) MarkCacheDirty();
            PendingTranslations.TryRemove(key, out _);
            RefreshRuntimeUICache();
            SaveCacheIfDue(true);
        }

        public static void SetManagedIgnored(string original, bool ignored)
        {
            string cleanOriginal = GetTranslationLookupText(original);
            string key = BuildCacheKey(cleanOriginal);
            if (ignored)
            {
                if (IgnoredCache.TryAdd(key, true)) MarkIgnoredCacheDirty();
                if (Cache.TryRemove(key, out _)) MarkCacheDirty();
                PendingClassifications.TryRemove(key, out _);
                PendingTranslations.TryRemove(key, out _);
            }
            else if (IgnoredCache.TryRemove(key, out _))
            {
                MarkIgnoredCacheDirty();
            }

            RefreshRuntimeUICache();
            SaveCacheIfDue(true);
        }

        public static string GetManagedCacheDirectory()
        {
            return Path.GetDirectoryName(CacheFilePath) ?? AutoTranslatorScanner.GetLocalPackPath();
        }

        private static void AddManagedEntries(IEnumerable<string> keys, IDictionary<string, UITranslationManagedEntry> entries)
        {
            foreach (string key in keys)
            {
                if (!TryGetOriginalTextFromCacheKey(key, out string original, out TargetLanguage? language)) continue;
                if (language.HasValue && language.Value != AutoTranslatorMod.Settings.TargetLang) continue;
                if (string.IsNullOrWhiteSpace(original) || entries.ContainsKey(original)) continue;
                entries[original] = new UITranslationManagedEntry { Original = original };
            }
        }

        private static void LoadSourceOwners()
        {
            try
            {
                if (!File.Exists(SourceOwnersFilePath)) return;
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(SourceOwnersFilePath));
                if (loaded == null) return;

                foreach (var pair in loaded)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                    if (TryGetOriginalTextFromCacheKey(pair.Key, out string original, out TargetLanguage? language) &&
                        (!language.HasValue || language.Value == AutoTranslatorMod.Settings.TargetLang))
                    {
                        string cleanOriginal = GetTranslationLookupText(original);
                        if (!string.IsNullOrWhiteSpace(cleanOriginal))
                        {
                            SourceOwners[BuildCacheKey(cleanOriginal)] = pair.Value.Trim().ToLowerInvariant();
                            if (!language.HasValue) _sourceOwnersDirty = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                QuarantineBrokenCacheFile(SourceOwnersFilePath);
                Log.Warning("[AutoTranslationCore] UI source-owner cache load failed: " + ex.Message);
            }
        }

        private static void SaveSourceOwners()
        {
            lock (_cachePersistenceLock)
            {
                try
                {
                    var merged = LoadExistingCacheFileForMerge(SourceOwnersFilePath, false);
                    RemoveCurrentLanguageEntries(merged);
                    foreach (var pair in SourceOwners) merged[pair.Key] = pair.Value;
                    WriteAllTextAtomic(SourceOwnersFilePath, JsonConvert.SerializeObject(merged, Formatting.Indented));
                    _sourceOwnersDirty = false;
                    System.Threading.Interlocked.Exchange(ref _lastCacheSaveTicks, DateTime.UtcNow.Ticks);
                }
                catch (Exception ex)
                {
                    Log.Warning("[AutoTranslationCore] UI source-owner cache save failed: " + ex.Message);
                }
            }
        }

        private static void CaptureSourceOwnerIfNeeded(string original)
        {
            string lookup = GetTranslationLookupText(original);
            string key = BuildCacheKey(lookup);
            if (SourceOwners.ContainsKey(key)) return;

            string packageId = ResolveSourceOwnerFromStack();
            if (string.IsNullOrWhiteSpace(packageId)) return;
            if (SourceOwners.TryAdd(key, packageId))
            {
                _sourceOwnersDirty = true;
            }
        }

        private static bool IsSourceOwnerBlacklisted(string original)
        {
            string key = BuildCacheKey(GetTranslationLookupText(original));
            return SourceOwners.TryGetValue(key, out string packageId) &&
                   AutoTranslatorMod.Settings.IsUiTranslationModBlacklisted(packageId);
        }

        private static string ResolveSourceOwnerFromStack()
        {
            try
            {
                List<SourceOwnerRoot> roots = GetSourceOwnerRoots();
                StackFrame[] frames = new StackTrace(2, false).GetFrames();
                if (frames == null) return null;

                Assembly ownAssembly = typeof(UIInterceptor).Assembly;
                foreach (StackFrame frame in frames)
                {
                    Assembly assembly = frame.GetMethod()?.DeclaringType?.Assembly;
                    if (assembly == null || assembly == ownAssembly) continue;
                    string location;
                    try { location = Path.GetFullPath(assembly.Location ?? string.Empty); }
                    catch { continue; }
                    if (string.IsNullOrWhiteSpace(location)) continue;

                    SourceOwnerRoot match = roots.FirstOrDefault(root => IsPathUnderRoot(location, root.Root));
                    if (match != null) return match.PackageId;
                }
            }
            catch
            {
            }
            return null;
        }

        private static List<SourceOwnerRoot> GetSourceOwnerRoots()
        {
            List<ModContentPack> running = LoadedModManager.RunningModsListForReading?.ToList() ?? new List<ModContentPack>();
            if (_sourceOwnerRootModCount == running.Count) return _sourceOwnerRoots;

            lock (SourceOwnerMapLock)
            {
                if (_sourceOwnerRootModCount == running.Count) return _sourceOwnerRoots;
                _sourceOwnerRoots = running
                    .Where(mod => mod != null && !string.IsNullOrWhiteSpace(mod.RootDir) && !string.IsNullOrWhiteSpace(mod.PackageId))
                    .Select(mod => new SourceOwnerRoot
                    {
                        PackageId = mod.PackageId.Trim().ToLowerInvariant(),
                        Root = NormalizeRoot(mod.RootDir)
                    })
                    .Where(root => !string.IsNullOrWhiteSpace(root.Root))
                    .OrderByDescending(root => root.Root.Length)
                    .ToList();
                _sourceOwnerRootModCount = running.Count;
                return _sourceOwnerRoots;
            }
        }

        private static string NormalizeRoot(string path)
        {
            try
            {
                return Path.GetFullPath(path ?? string.Empty)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            }
            catch { return string.Empty; }
        }

        private static bool IsPathUnderRoot(string path, string root)
        {
            return !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(root) &&
                   path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class SourceOwnerRoot
        {
            public string PackageId;
            public string Root;
        }
    }
}
