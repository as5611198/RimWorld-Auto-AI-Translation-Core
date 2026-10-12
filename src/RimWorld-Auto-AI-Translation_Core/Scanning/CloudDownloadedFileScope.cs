using System;
using System.IO;

namespace AutoTranslator_Core
{
    internal static class CloudDownloadedFileScope
    {
        internal static bool TryResolveXml(string languageRoot, string candidate, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(languageRoot) || string.IsNullOrWhiteSpace(candidate))
                return false;

            try
            {
                string root = Path.GetFullPath(languageRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(candidate);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetExtension(full), ".xml", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(full))
                {
                    return false;
                }

                fullPath = full;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
