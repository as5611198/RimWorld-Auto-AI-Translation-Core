using System;

namespace AutoTranslator_Core
{
    internal static class PolicyAnalysisRecordValidator
    {
        internal static bool IsUsable(
            PolicyAnalysisCloudRecord record,
            string packageId,
            string gameVersion,
            string sourceFingerprint,
            string policyVersion,
            string promptVersion)
        {
            return IsUsable(
                record,
                PolicyAnalysisCandidateDomain.Xml,
                packageId,
                gameVersion,
                sourceFingerprint,
                policyVersion,
                promptVersion);
        }

        internal static bool IsUsable(
            PolicyAnalysisCloudRecord record,
            string candidateDomain,
            string packageId,
            string gameVersion,
            string sourceFingerprint,
            string policyVersion,
            string promptVersion)
        {
            if (record == null) return false;
            string expectedDomain = PolicyAnalysisCandidateDomain.Normalize(candidateDomain);
            string recordDomain = PolicyAnalysisCandidateDomain.Normalize(record.CandidateDomain);
            bool compatibleSchema = record.SchemaVersion == 2
                ? expectedDomain.Length > 0 && recordDomain == expectedDomain
                : record.SchemaVersion == 1 && expectedDomain == PolicyAnalysisCandidateDomain.Xml &&
                  (recordDomain.Length == 0 || recordDomain == PolicyAnalysisCandidateDomain.Xml);
            return compatibleSchema &&
                   record.Complete &&
                   record.CandidateCount >= 0 &&
                   record.AllowedCandidateIds != null &&
                   record.AllowedCandidateIds.Count <= record.CandidateCount &&
                   PolicyAnalysisCandidateDomain.AreCandidateIdsValid(
                       expectedDomain,
                       record.AllowedCandidateIds) &&
                   string.Equals(record.PackageId ?? string.Empty, packageId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(record.GameVersion ?? string.Empty, gameVersion ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(record.SourceFingerprint ?? string.Empty, sourceFingerprint ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(record.PolicyVersion ?? string.Empty, policyVersion ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(record.PromptVersion ?? string.Empty, promptVersion ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
