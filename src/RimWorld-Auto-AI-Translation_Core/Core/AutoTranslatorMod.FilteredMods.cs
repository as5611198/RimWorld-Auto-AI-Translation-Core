using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Verse;

namespace AutoTranslator_Core
{
    internal enum FilteredModReason
    {
        ToolOrPack,
        TranslationPatch,
        TranslationBlacklist,
        MissingRoot,
        UnsupportedSourceLayout,
        PartialSourcesSkipped,
        NoTranslationSources,
        ForceIncluded,
        ScanFailed
    }

    internal sealed class FilteredModInfo
    {
        public ModMetaData Mod;
        public string PackageId = "";
        public string Name = "";
        public string RootDir = "";
        public FilteredModReason Reason;
        public string Error = "";
        public bool IsFiltered;
        public bool ForceEnabled;
        public bool CanForce;
        public List<string> CandidateSourcePaths = new List<string>();
    }

    public partial class AutoTranslatorMod
    {
        private sealed class ForceCandidatePathCacheEntry
        {
            public long RootLastWriteTicks;
            public long CachedUtcTicks;
            public List<string> Paths = new List<string>();
        }

        private static readonly object ForceCandidatePathCacheLock = new object();
        private static readonly Dictionary<string, ForceCandidatePathCacheEntry> ForceCandidatePathCache =
            new Dictionary<string, ForceCandidatePathCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly long ForceCandidatePathCacheLifetimeTicks = TimeSpan.FromMinutes(10).Ticks;
        private static List<FilteredModInfo> _cachedFilteredMods = new List<FilteredModInfo>();

        internal static List<FilteredModInfo> GetFilteredModsCached()
        {
            QueueValidModsCacheRefreshIfDirty();
            return (_cachedFilteredMods ?? new List<FilteredModInfo>())
                .Select(CloneFilteredModInfo)
                .ToList();
        }

        internal static int GetFilteredModsCountCached()
        {
            QueueValidModsCacheRefreshIfDirty();
            return (_cachedFilteredMods ?? new List<FilteredModInfo>()).Count(info => info.IsFiltered);
        }

        internal static int GetForceIncludedModsCountCached()
        {
            QueueValidModsCacheRefreshIfDirty();
            return (_cachedFilteredMods ?? new List<FilteredModInfo>()).Count(
                info => info.Reason == FilteredModReason.ForceIncluded);
        }

        public static void InvalidateValidModsCache()
        {
            System.Threading.Interlocked.Increment(ref _validModsFilterSettingsRevision);
            _nextInstalledModStateCheckUtcTicks = 0L;
            _lastInstalledModSignature = "\0";
            _pendingInstalledModSignature = "";
            _cachedCloudDisplayMods = null;
            _cachedCloudLocalModMap = null;
        }

        private static void QueueValidModsCacheRefreshIfDirty()
        {
            if (_validModsCacheRefreshInFlight) return;
            if (_cachedValidMods != null &&
                _validModsCacheAppliedSettingsRevision == _validModsFilterSettingsRevision)
            {
                return;
            }
            QueueValidModsCacheRefreshIfNeeded();
        }

        private static FilteredModInfo EvaluateModFilter(
            ValidModSnapshot snapshot,
            out bool includeInValidMods)
        {
            includeInValidMods = false;
            if (snapshot == null || snapshot.Mod == null)
            {
                return null;
            }

            string packageId = snapshot.PackageId ?? "";
            string rootDir = snapshot.RootDir ?? "";
            bool forceEnabled = Settings != null && Settings.IsForceTranslationEnabled(packageId);

            if (ShouldSkipValidModPackage(packageId))
            {
                return CreateFilteredInfo(snapshot, FilteredModReason.ToolOrPack, true, forceEnabled, false, null, null);
            }

            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            {
                return CreateFilteredInfo(snapshot, FilteredModReason.MissingRoot, true, forceEnabled, false, null, null);
            }

            try
            {
                bool isTranslationPatch = AutoTranslatorScanner.IsTranslationPatchMod(snapshot.Mod);
                bool isTranslationBlacklisted = Settings != null && Settings.IsTranslationBlacklisted(packageId);

                if (isTranslationPatch || isTranslationBlacklisted)
                {
                    return CreateFilteredInfo(
                        snapshot,
                        isTranslationPatch
                            ? FilteredModReason.TranslationPatch
                            : FilteredModReason.TranslationBlacklist,
                        true,
                        forceEnabled,
                        false,
                        null,
                        null);
                }

                bool isOfficialContent = AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(packageId);
                if (isOfficialContent)
                {
                    includeInValidMods = true;
                    return null;
                }

                bool hasNormalSources =
                    AutoTranslatorScanner.HasScannableTranslationSourcesNormally(packageId, rootDir);
                List<string> candidates = GetForceTranslationCandidatePathsCached(rootDir);
                bool canUseForcedSources = candidates.Count > 0;
                includeInValidMods = hasNormalSources || (forceEnabled && canUseForcedSources);

                if (!hasNormalSources)
                {
                    if (forceEnabled && canUseForcedSources)
                    {
                        return CreateFilteredInfo(
                            snapshot,
                            FilteredModReason.ForceIncluded,
                            false,
                            true,
                            true,
                            candidates,
                            null);
                    }

                    FilteredModReason reason = canUseForcedSources
                        ? FilteredModReason.UnsupportedSourceLayout
                        : FilteredModReason.NoTranslationSources;
                    return CreateFilteredInfo(
                        snapshot,
                        reason,
                        true,
                        forceEnabled,
                        canUseForcedSources,
                        candidates,
                        null);
                }

                if (forceEnabled)
                {
                    return CreateFilteredInfo(
                        snapshot,
                        FilteredModReason.ForceIncluded,
                        false,
                        true,
                        true,
                        candidates,
                        null);
                }

                List<string> normalizedNormalDefs = AutoTranslatorScanner
                    .GetAllEffectiveDefsPaths(packageId, rootDir)
                    .Select(NormalizePathForComparison)
                    .Where(path => path.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                List<string> normalizedNormalLanguages = AutoTranslatorScanner
                    .GetAllEffectiveLangPaths(packageId, rootDir)
                    .Select(NormalizePathForComparison)
                    .Where(path => path.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                List<string> extraCandidates = candidates
                    .Where(candidate => !IsCandidateAlreadyScanned(
                        candidate,
                        normalizedNormalDefs,
                        normalizedNormalLanguages))
                    .ToList();
                if (extraCandidates.Count > 0)
                {
                    return CreateFilteredInfo(
                        snapshot,
                        FilteredModReason.PartialSourcesSkipped,
                        true,
                        false,
                        true,
                        extraCandidates,
                        null);
                }

                return null;
            }
            catch (Exception ex)
            {
                includeInValidMods = false;
                return CreateFilteredInfo(
                    snapshot,
                    FilteredModReason.ScanFailed,
                    true,
                    forceEnabled,
                    false,
                    null,
                    ex.Message);
            }
        }

        private static List<string> GetForceTranslationCandidatePathsCached(string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
                return new List<string>();

            string normalizedRoot;
            long rootLastWriteTicks;
            try
            {
                normalizedRoot = Path.GetFullPath(rootDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                rootLastWriteTicks = Directory.GetLastWriteTimeUtc(normalizedRoot).Ticks;
            }
            catch
            {
                return new List<string>();
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            lock (ForceCandidatePathCacheLock)
            {
                if (ForceCandidatePathCache.TryGetValue(normalizedRoot, out ForceCandidatePathCacheEntry cached) &&
                    cached.RootLastWriteTicks == rootLastWriteTicks &&
                    nowTicks - cached.CachedUtcTicks < ForceCandidatePathCacheLifetimeTicks)
                {
                    return new List<string>(cached.Paths);
                }
            }

            List<string> paths = AutoTranslatorScanner.GetForceTranslationCandidatePaths(normalizedRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            long cachedAtTicks = DateTime.UtcNow.Ticks;

            lock (ForceCandidatePathCacheLock)
            {
                ForceCandidatePathCache[normalizedRoot] = new ForceCandidatePathCacheEntry
                {
                    RootLastWriteTicks = rootLastWriteTicks,
                    CachedUtcTicks = cachedAtTicks,
                    Paths = new List<string>(paths)
                };

                if (ForceCandidatePathCache.Count > 1024)
                {
                    foreach (string expiredRoot in ForceCandidatePathCache
                        .Where(pair => cachedAtTicks - pair.Value.CachedUtcTicks >= ForceCandidatePathCacheLifetimeTicks)
                        .Select(pair => pair.Key)
                        .ToList())
                    {
                        ForceCandidatePathCache.Remove(expiredRoot);
                    }
                }
            }

            return paths;
        }

        private static bool IsCandidateAlreadyScanned(
            string candidate,
            List<string> normalizedDefsRoots,
            List<string> normalizedLanguageRoots)
        {
            string normalizedCandidate = NormalizePathForComparison(candidate);
            if (normalizedCandidate.Length == 0) return false;

            string candidateFolderName = Path.GetFileName(normalizedCandidate) ?? "";
            if (candidateFolderName.Equals("Languages", StringComparison.OrdinalIgnoreCase))
            {
                return (normalizedLanguageRoots ?? new List<string>()).Any(sourceRoot =>
                    string.Equals(sourceRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase));
            }

            foreach (string sourceRoot in normalizedDefsRoots ?? new List<string>())
            {
                if (string.Equals(sourceRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase)) return true;
                string prefix = sourceRoot + Path.DirectorySeparatorChar;
                if (normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static string NormalizePathForComparison(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim()
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private static FilteredModInfo CreateFilteredInfo(
            ValidModSnapshot snapshot,
            FilteredModReason reason,
            bool isFiltered,
            bool forceEnabled,
            bool canForce,
            List<string> candidates,
            string error)
        {
            return new FilteredModInfo
            {
                Mod = snapshot.Mod,
                PackageId = snapshot.PackageId ?? "",
                Name = snapshot.Name ?? snapshot.PackageId ?? "",
                RootDir = snapshot.RootDir ?? "",
                Reason = reason,
                Error = error ?? "",
                IsFiltered = isFiltered,
                ForceEnabled = forceEnabled,
                CanForce = canForce,
                CandidateSourcePaths = (candidates ?? new List<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        private static FilteredModInfo CloneFilteredModInfo(FilteredModInfo source)
        {
            return new FilteredModInfo
            {
                Mod = source.Mod,
                PackageId = source.PackageId,
                Name = source.Name,
                RootDir = source.RootDir,
                Reason = source.Reason,
                Error = source.Error,
                IsFiltered = source.IsFiltered,
                ForceEnabled = source.ForceEnabled,
                CanForce = source.CanForce,
                CandidateSourcePaths = new List<string>(source.CandidateSourcePaths ?? new List<string>())
            };
        }
    }
}
