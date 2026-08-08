using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AutoTranslator_Core.TranslationPolicy
{
    public static class TranslationPolicyIdentity
    {
        public static string CreateCandidateId(TranslationPolicyCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));

            string canonical = JoinCanonical(
                NormalizeIdentityPart(candidate.PackageId),
                NormalizePathPart(candidate.SourceFile),
                ((int)candidate.Bucket).ToString(CultureInfo.InvariantCulture),
                NormalizeIdentityPart(candidate.DefType),
                NormalizeIdentityPart(candidate.KeyOrPath),
                NormalizeIdentityPart(candidate.FieldName),
                candidate.SourceText ?? string.Empty,
                NormalizeIdentityPart(candidate.DeclaringAssembly),
                NormalizeIdentityPart(candidate.SchemaFingerprint));

            return "tpc_" + ComputeSha256(canonical);
        }

        public static string CreateGroupCorpusFingerprint(IEnumerable<TranslationPolicyCandidate> candidates)
        {
            string[] candidateIds = (candidates ?? Enumerable.Empty<TranslationPolicyCandidate>())
                .Where(candidate => candidate != null)
                .Select(candidate => string.IsNullOrWhiteSpace(candidate.CandidateId)
                    ? CreateCandidateId(candidate)
                    : candidate.CandidateId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(candidateId => candidateId, StringComparer.Ordinal)
                .ToArray();

            return "tpcorpus_" + ComputeSha256(JoinCanonical(candidateIds));
        }

        public static string CreateAgentCacheKey(
            string policyVersion,
            string promptVersion,
            string evaluatorFingerprint,
            string groupKey,
            string groupCorpusFingerprint)
        {
            string canonical = JoinCanonical(
                NormalizeIdentityPart(policyVersion),
                NormalizeIdentityPart(promptVersion),
                NormalizeIdentityPart(evaluatorFingerprint),
                NormalizeIdentityPart(groupKey),
                NormalizeIdentityPart(groupCorpusFingerprint));
            return "tpac_" + ComputeSha256(canonical);
        }

        public static string CreateAgentCandidateCacheKey(
            string policyVersion,
            string promptVersion,
            string evaluatorFingerprint,
            string groupKey,
            string candidateId)
        {
            string canonical = JoinCanonical(
                NormalizeIdentityPart(policyVersion),
                NormalizeIdentityPart(promptVersion),
                NormalizeIdentityPart(evaluatorFingerprint),
                NormalizeIdentityPart(groupKey),
                NormalizeIdentityPart(candidateId));
            return "tpacc_" + ComputeSha256(canonical);
        }

        public static string CreateAgentCandidateRequestId(
            string groupKey,
            string candidateId)
        {
            string canonical = JoinCanonical(
                NormalizeIdentityPart(groupKey),
                NormalizeIdentityPart(candidateId));
            return "tpacr_" + ComputeSha256(canonical);
        }

        internal static string ComputeSha256(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder result = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    result.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return result.ToString();
            }
        }

        internal static string JoinCanonical(params string[] values)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i] ?? string.Empty;
                builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(value);
                builder.Append('|');
            }

            return builder.ToString();
        }

        internal static string NormalizeIdentityPart(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        internal static string NormalizePathPart(string value)
        {
            return NormalizeIdentityPart((value ?? string.Empty).Replace('\\', '/'));
        }
    }
}
