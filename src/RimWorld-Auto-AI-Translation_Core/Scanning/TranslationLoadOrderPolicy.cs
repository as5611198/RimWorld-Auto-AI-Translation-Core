using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoTranslator_Core
{
    public static class TranslationLoadOrderPolicy
    {
        public static List<string> OrderFiles(
            IEnumerable<string> files,
            IEnumerable<string> packageIdsInGameLoadOrder,
            IEnumerable<string> explicitHighPriorityPackageIds)
        {
            List<string> sourceFiles = (files ?? Enumerable.Empty<string>())
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> gameOrder = NormalizePackageList(packageIdsInGameLoadOrder);
            List<string> explicitOrder = NormalizePackageList(explicitHighPriorityPackageIds);
            Dictionary<string, string> normalizedToPackage = BuildPackageLookup(gameOrder.Concat(explicitOrder));
            Dictionary<string, int> gameRanks = gameOrder
                .Select((packageId, index) => new { packageId, index })
                .ToDictionary(item => NormalizePackageId(item.packageId), item => item.index, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> explicitRanks = explicitOrder
                .Select((packageId, index) => new { packageId, index })
                .ToDictionary(item => NormalizePackageId(item.packageId), item => item.index, StringComparer.OrdinalIgnoreCase);

            return sourceFiles
                .Select(file => BuildSortEntry(file, normalizedToPackage, gameRanks, explicitRanks, explicitOrder.Count))
                .OrderBy(entry => entry.IsExplicit)
                .ThenBy(entry => entry.Priority)
                .ThenBy(entry => entry.Layer)
                .ThenBy(entry => entry.File, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.File)
                .ToList();
        }

        public static Dictionary<string, List<string>> GroupFilesByPackage(
            IEnumerable<string> files,
            IEnumerable<string> packageIds)
        {
            Dictionary<string, string> lookup = BuildPackageLookup(packageIds);
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files ?? Enumerable.Empty<string>())
            {
                string packageId = MatchPackageId(file, lookup);
                if (string.IsNullOrEmpty(packageId)) continue;
                if (!result.TryGetValue(packageId, out List<string> packageFiles))
                {
                    packageFiles = new List<string>();
                    result[packageId] = packageFiles;
                }
                packageFiles.Add(file);
            }
            return result;
        }

        public static string NormalizePackageId(string packageId)
        {
            return (packageId ?? string.Empty).Trim().ToLowerInvariant().Replace('.', '_');
        }

        private static TranslationFileSortEntry BuildSortEntry(
            string file,
            Dictionary<string, string> packageLookup,
            Dictionary<string, int> gameRanks,
            Dictionary<string, int> explicitRanks,
            int explicitCount)
        {
            string packageId = MatchPackageId(file, packageLookup);
            string normalized = NormalizePackageId(packageId);
            bool isExplicit = explicitRanks.TryGetValue(normalized, out int explicitIndex);
            int priority;
            if (isExplicit)
            {
                // The first UI row is highest priority, so it must be read last.
                priority = explicitCount - explicitIndex;
            }
            else if (!gameRanks.TryGetValue(normalized, out priority))
            {
                priority = -1;
            }

            return new TranslationFileSortEntry
            {
                File = file,
                IsExplicit = isExplicit ? 1 : 0,
                Priority = priority,
                Layer = GetLayer(file)
            };
        }

        private static int GetLayer(string file)
        {
            string normalized = (file ?? string.Empty).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(file) ?? string.Empty;
            if (normalized.IndexOf("/Manual_Translation/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("ManualTranslation", StringComparison.OrdinalIgnoreCase) >= 0) return 20;
            if (name.IndexOf("CloudCorrections", StringComparison.OrdinalIgnoreCase) >= 0) return 30;
            return 10;
        }

        private static Dictionary<string, string> BuildPackageLookup(IEnumerable<string> packageIds)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string packageId in NormalizePackageList(packageIds))
            {
                string normalized = NormalizePackageId(packageId);
                if (!string.IsNullOrEmpty(normalized)) result[normalized] = packageId;
            }
            return result;
        }

        private static string MatchPackageId(string file, Dictionary<string, string> packageLookup)
        {
            if (packageLookup == null || packageLookup.Count == 0) return string.Empty;
            string token = NormalizePackageId(Path.GetFileNameWithoutExtension(file));
            while (!string.IsNullOrEmpty(token))
            {
                if (packageLookup.TryGetValue(token, out string packageId)) return packageId;
                int separator = token.LastIndexOf('_');
                if (separator <= 0) break;
                token = token.Substring(0, separator);
            }
            return string.Empty;
        }

        private static List<string> NormalizePackageList(IEnumerable<string> packageIds)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (string packageId in packageIds ?? Enumerable.Empty<string>())
            {
                string trimmed = (packageId ?? string.Empty).Trim();
                string normalized = NormalizePackageId(trimmed);
                if (normalized.Length == 0 || !seen.Add(normalized)) continue;
                result.Add(trimmed);
            }
            return result;
        }

        private sealed class TranslationFileSortEntry
        {
            public string File;
            public int IsExplicit;
            public int Priority;
            public int Layer;
        }
    }
}
