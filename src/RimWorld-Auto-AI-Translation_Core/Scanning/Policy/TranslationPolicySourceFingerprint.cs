using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AutoTranslator_Core.TranslationPolicy
{
    internal static class TranslationPolicySourceFingerprint
    {
        internal static string Compute(
            string modRoot,
            string branchIdentity,
            IEnumerable<string> sourceFiles)
        {
            if (string.IsNullOrWhiteSpace(modRoot)) return string.Empty;

            string root;
            try
            {
                root = Path.GetFullPath(modRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return string.Empty;
            }

            string rootPrefix = root + Path.DirectorySeparatorChar;
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string candidate in sourceFiles ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                        continue;
                    string relative = full.Substring(rootPrefix.Length).Replace('\\', '/').ToLowerInvariant();
                    if (!files.ContainsKey(relative)) files.Add(relative, full);
                }
                catch
                {
                }
            }
            if (files.Count == 0) return string.Empty;

            var canonical = new StringBuilder();
            canonical.Append("policy-source-v1\n")
                .Append(branchIdentity ?? string.Empty)
                .Append('\n');
            foreach (KeyValuePair<string, string> file in files)
            {
                try
                {
                    var info = new FileInfo(file.Value);
                    canonical.Append(file.Key)
                        .Append('|')
                        .Append(info.Length)
                        .Append('|')
                        .Append(ComputeFileSha256(file.Value))
                        .Append('\n');
                }
                catch
                {
                    return string.Empty;
                }
            }
            return "tpsrc_" + ComputeTextSha256(canonical.ToString());
        }

        internal static string ComputeCanonicalRecords(
            string branchIdentity,
            IEnumerable<string> records)
        {
            string[] normalized = (records ?? Enumerable.Empty<string>())
                .Where(record => !string.IsNullOrWhiteSpace(record))
                .Select(record => record.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(record => record, StringComparer.Ordinal)
                .ToArray();
            if (normalized.Length == 0) return string.Empty;

            var canonical = new StringBuilder();
            canonical.Append("policy-source-records-v1\n")
                .Append(branchIdentity ?? string.Empty)
                .Append('\n');
            foreach (string record in normalized)
                canonical.Append(record).Append('\n');
            return "tpsrc_" + ComputeTextSha256(canonical.ToString());
        }

        private static string ComputeTextSha256(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var result = new StringBuilder(hash.Length * 2);
                foreach (byte item in hash) result.Append(item.ToString("x2"));
                return result.ToString();
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                var result = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) result.Append(value.ToString("x2"));
                return result.ToString();
            }
        }
    }
}
