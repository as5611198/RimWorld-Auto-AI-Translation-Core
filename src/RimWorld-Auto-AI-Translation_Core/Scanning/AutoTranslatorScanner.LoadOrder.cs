using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private static readonly object TranslationLoadOrderLock = new object();
        private static List<string> CachedGamePackageOrder = new List<string>();
        private static int CachedGamePackageCount = -1;

        private static List<string> ApplyTranslationFileLoadOrder(string rootPath, IEnumerable<string> files)
        {
            List<string> materialized = (files ?? Enumerable.Empty<string>()).ToList();
            if (!IsLocalTranslationLanguagePath(rootPath))
            {
                return materialized.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList();
            }

            return TranslationLoadOrderPolicy.OrderFiles(
                materialized,
                GetGamePackageOrderSnapshot(),
                AutoTranslatorMod.Settings?.CloudTranslationPriority);
        }

        public static void NotifyTranslationLoadOrderChanged()
        {
            string languagesRoot = Path.Combine(GetLocalPackPath(), "Languages");
            NotifyTranslationFilesChanged(languagesRoot);
            RequestMemoryDrop();
        }

        private static List<string> GetGamePackageOrderSnapshot()
        {
            try
            {
                List<ModContentPack> running = LoadedModManager.RunningModsListForReading?.ToList()
                    ?? new List<ModContentPack>();
                lock (TranslationLoadOrderLock)
                {
                    if (CachedGamePackageCount != running.Count)
                    {
                        CachedGamePackageOrder = running
                            .Where(mod => mod != null && !string.IsNullOrWhiteSpace(mod.PackageId))
                            .Select(mod => mod.PackageId)
                            .ToList();
                        CachedGamePackageCount = running.Count;
                    }
                    return new List<string>(CachedGamePackageOrder);
                }
            }
            catch
            {
                lock (TranslationLoadOrderLock)
                {
                    return new List<string>(CachedGamePackageOrder);
                }
            }
        }

        private static bool IsLocalTranslationLanguagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string languagesRoot = Path.GetFullPath(Path.Combine(GetLocalPackPath(), "Languages"))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string fullPath = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(languagesRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
