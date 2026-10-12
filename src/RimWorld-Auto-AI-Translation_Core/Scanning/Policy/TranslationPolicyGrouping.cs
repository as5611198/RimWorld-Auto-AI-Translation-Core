using System;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core.TranslationPolicy
{
    public static class TranslationPolicyGrouping
    {
        private static readonly Regex BracketedIndexRegex = new Regex(
            @"\[\d+\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string NormalizeIndexedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            string[] parts = path.Replace('\\', '.').Replace('/', '.').Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = BracketedIndexRegex.Replace(parts[i].Trim(), "[]");
                if (IsDigits(part)) part = "[]";
                parts[i] = part.ToLowerInvariant();
            }

            return string.Join(".", parts);
        }

        public static string CreateGroupKey(TranslationPolicyCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));

            string normalizedPath = NormalizeForGrouping(candidate);
            string canonical = TranslationPolicyIdentity.JoinCanonical(
                ((int)candidate.Bucket).ToString(System.Globalization.CultureInfo.InvariantCulture),
                TranslationPolicyIdentity.NormalizeIdentityPart(candidate.PackageId),
                TranslationPolicyIdentity.NormalizeIdentityPart(candidate.DeclaringAssembly),
                TranslationPolicyIdentity.NormalizeIdentityPart(candidate.SchemaFingerprint),
                TranslationPolicyIdentity.NormalizeIdentityPart(candidate.DefType),
                normalizedPath,
                TranslationPolicyIdentity.NormalizeIdentityPart(candidate.FieldName));

            return "tpg_" + TranslationPolicyIdentity.ComputeSha256(canonical);
        }

        internal static string NormalizeForGrouping(TranslationPolicyCandidate candidate)
        {
            string normalized = NormalizeIndexedPath(candidate.KeyOrPath);
            if (candidate.Bucket != TranslationPolicyBucket.DefInjected || normalized.Length == 0)
            {
                return normalized;
            }

            int separator = normalized.IndexOf('.');
            return separator < 0 ? "$def" : "$def" + normalized.Substring(separator);
        }

        private static bool IsDigits(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i])) return false;
            }

            return true;
        }
    }
}
