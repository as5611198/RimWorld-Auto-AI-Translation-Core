using System;
using System.Collections.Generic;
using System.IO;

namespace AutoTranslator_Core
{
    internal static class TranslationGeneratedOutputOwnership
    {
        internal static string GetCleanPackageId(string packageId)
        {
            return (packageId ?? string.Empty).Replace('.', '_').ToLowerInvariant();
        }

        internal static string GetCanonicalFileName(string packageId)
        {
            return GetCleanPackageId(packageId) + "_AutoTranslated.xml";
        }

        internal static string GetKeyedFileName(string packageId, string sourceFile)
        {
            string sourceName = Path.GetFileName(sourceFile ?? string.Empty);
            return string.IsNullOrWhiteSpace(sourceName)
                ? GetCanonicalFileName(packageId)
                : GetCleanPackageId(packageId) + "_" + sourceName;
        }

        internal static HashSet<string> BuildKeyedFileNameSet(
            string packageId,
            IEnumerable<string> sourceFiles)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                GetCanonicalFileName(packageId)
            };

            foreach (string sourceFile in sourceFiles ?? new string[0])
            {
                names.Add(GetKeyedFileName(packageId, sourceFile));
            }

            return names;
        }

        internal static bool IsOwnedKeyedFile(
            string filePath,
            string packageId,
            ISet<string> ownedNames)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(packageId)) return false;
            string fileName = Path.GetFileName(filePath);
            return ownedNames != null && ownedNames.Contains(fileName);
        }
    }
}
