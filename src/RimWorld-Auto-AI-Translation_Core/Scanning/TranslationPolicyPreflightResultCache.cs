using AutoTranslator_Core.TranslationPolicy;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core
{
    internal sealed class TranslationPolicyPreflightModResult
    {
        internal string PackageId = string.Empty;
        internal string ModName = string.Empty;
        internal int XmlCandidates;
        internal int LocalAllows;
        internal int LocalDenies;
        internal int Ambiguous;
        internal int CloudAllows;
        internal int CloudDenies;
        internal int AgentAllows;
        internal int AgentDenies;
        internal int AgentReviews;
        internal int Unresolved;
        internal readonly List<TranslationPolicyCandidateResult> DiagnosticSamples =
            new List<TranslationPolicyCandidateResult>();

        internal int FinalTranslationCandidates =>
            Math.Max(0, LocalAllows + CloudAllows + AgentAllows);

        internal TranslationPolicyPreflightModResult Clone()
        {
            var clone = new TranslationPolicyPreflightModResult
            {
                PackageId = PackageId,
                ModName = ModName,
                XmlCandidates = XmlCandidates,
                LocalAllows = LocalAllows,
                LocalDenies = LocalDenies,
                Ambiguous = Ambiguous,
                CloudAllows = CloudAllows,
                CloudDenies = CloudDenies,
                AgentAllows = AgentAllows,
                AgentDenies = AgentDenies,
                AgentReviews = AgentReviews,
                Unresolved = Unresolved
            };
            foreach (TranslationPolicyCandidateResult sample in DiagnosticSamples)
                clone.DiagnosticSamples.Add(CloneSample(sample));
            return clone;
        }

        internal static TranslationPolicyCandidateResult CloneSample(TranslationPolicyCandidateResult sample)
        {
            if (sample == null) return null;
            return new TranslationPolicyCandidateResult
            {
                CandidateId = sample.CandidateId,
                PackageId = sample.PackageId,
                ModName = sample.ModName,
                SourceFile = sample.SourceFile,
                Bucket = sample.Bucket,
                DefType = sample.DefType,
                KeyOrPath = sample.KeyOrPath,
                NormalizedPath = sample.NormalizedPath,
                FieldName = sample.FieldName,
                SourceText = sample.SourceText,
                Decision = sample.Decision,
                ReasonCode = sample.ReasonCode,
                GroupKey = sample.GroupKey
            };
        }
    }

    internal sealed class TranslationPolicyPreflightSnapshot
    {
        internal DateTime GeneratedUtc;
        internal string ReportPath = string.Empty;
        internal int ScannedXmlFiles;
        internal int ScanErrors;
        internal int TotalCandidates;
        internal int LocalAllows;
        internal int LocalDenies;
        internal int Ambiguous;
        internal readonly Dictionary<string, TranslationPolicyPreflightModResult> Mods =
            new Dictionary<string, TranslationPolicyPreflightModResult>(StringComparer.OrdinalIgnoreCase);

        internal TranslationPolicyPreflightSnapshot Clone()
        {
            var clone = new TranslationPolicyPreflightSnapshot
            {
                GeneratedUtc = GeneratedUtc,
                ReportPath = ReportPath,
                ScannedXmlFiles = ScannedXmlFiles,
                ScanErrors = ScanErrors,
                TotalCandidates = TotalCandidates,
                LocalAllows = LocalAllows,
                LocalDenies = LocalDenies,
                Ambiguous = Ambiguous
            };
            foreach (KeyValuePair<string, TranslationPolicyPreflightModResult> pair in Mods)
                clone.Mods[pair.Key] = pair.Value?.Clone();
            return clone;
        }
    }

    internal static class TranslationPolicyPreflightResultCache
    {
        private static readonly object Gate = new object();
        private static TranslationPolicyPreflightSnapshot _latest;

        internal static bool TryGetLatest(out TranslationPolicyPreflightSnapshot snapshot)
        {
            lock (Gate)
            {
                snapshot = _latest?.Clone();
                return snapshot != null;
            }
        }

        internal static bool TryGetMod(string packageId, out TranslationPolicyPreflightModResult result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(packageId)) return false;
            lock (Gate)
            {
                if (_latest == null || !_latest.Mods.TryGetValue(packageId, out TranslationPolicyPreflightModResult found))
                    return false;
                result = found?.Clone();
                return result != null;
            }
        }

        internal static void StoreLocalRuleResult(
            TranslationPolicyShadowResult result,
            IEnumerable<KeyValuePair<string, string>> selectedMods,
            DateTime generatedUtc,
            string reportPath,
            int scannedXmlFiles,
            int scanErrors)
        {
            if (result == null) return;
            TranslationPolicySummary summary = result.Summary ?? new TranslationPolicySummary();
            var snapshot = new TranslationPolicyPreflightSnapshot
            {
                GeneratedUtc = generatedUtc,
                ReportPath = reportPath ?? string.Empty,
                ScannedXmlFiles = Math.Max(0, scannedXmlFiles),
                ScanErrors = Math.Max(0, scanErrors),
                TotalCandidates = Math.Max(0, summary.TotalCandidates),
                LocalAllows = Math.Max(0, summary.HardAllowCount),
                LocalDenies = Math.Max(0, summary.HardDenyCount),
                Ambiguous = Math.Max(0, summary.AmbiguousCount)
            };

            foreach (KeyValuePair<string, string> mod in selectedMods ??
                     Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (string.IsNullOrWhiteSpace(mod.Key)) continue;
                snapshot.Mods[mod.Key] = new TranslationPolicyPreflightModResult
                {
                    PackageId = mod.Key,
                    ModName = mod.Value ?? string.Empty
                };
            }

            foreach (IGrouping<string, TranslationPolicyModSummary> group in
                     (result.ModSummaries ?? new List<TranslationPolicyModSummary>())
                     .Where(item => item != null && !string.IsNullOrWhiteSpace(item.PackageId))
                     .GroupBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                if (!snapshot.Mods.TryGetValue(group.Key, out TranslationPolicyPreflightModResult mod))
                {
                    mod = new TranslationPolicyPreflightModResult { PackageId = group.Key };
                    snapshot.Mods[group.Key] = mod;
                }

                mod.XmlCandidates = group.Sum(item => Math.Max(0, item.TotalCandidates));
                mod.LocalAllows = group.Sum(item => Math.Max(0, item.HardAllowCount));
                mod.LocalDenies = group.Sum(item => Math.Max(0, item.HardDenyCount));
                mod.Ambiguous = group.Sum(item => Math.Max(0, item.AmbiguousCount));
                mod.Unresolved = mod.Ambiguous;
            }

            foreach (TranslationPolicyCandidateResult sample in
                     result.DiagnosticSamples ?? new List<TranslationPolicyCandidateResult>())
            {
                if (sample == null || string.IsNullOrWhiteSpace(sample.PackageId) ||
                    !snapshot.Mods.TryGetValue(sample.PackageId, out TranslationPolicyPreflightModResult mod))
                    continue;
                mod.DiagnosticSamples.Add(TranslationPolicyPreflightModResult.CloneSample(sample));
            }

            lock (Gate) _latest = snapshot;
        }

        internal static void ApplyResolution(
            string packageId,
            IEnumerable<TranslationPolicyAgentCandidateOutcome> outcomes)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return;
            List<TranslationPolicyAgentCandidateOutcome> materialized =
                (outcomes ?? Enumerable.Empty<TranslationPolicyAgentCandidateOutcome>())
                .Where(outcome => outcome != null)
                .ToList();

            lock (Gate)
            {
                if (_latest == null || !_latest.Mods.TryGetValue(packageId, out TranslationPolicyPreflightModResult mod))
                    return;

                mod.CloudAllows = 0;
                mod.CloudDenies = 0;
                mod.AgentAllows = 0;
                mod.AgentDenies = 0;
                mod.AgentReviews = 0;
                mod.Unresolved = 0;
                foreach (TranslationPolicyAgentCandidateOutcome outcome in materialized)
                {
                    if (outcome.Status != TranslationPolicyAgentOutcomeStatus.Classified)
                    {
                        mod.Unresolved++;
                        continue;
                    }

                    bool fromCloud = string.Equals(
                        outcome.Reason,
                        "cloud_policy_analysis",
                        StringComparison.Ordinal);
                    if (outcome.Decision == TranslationPolicyAgentDecision.Allow)
                    {
                        if (fromCloud) mod.CloudAllows++;
                        else mod.AgentAllows++;
                    }
                    else if (outcome.Decision == TranslationPolicyAgentDecision.Deny)
                    {
                        if (fromCloud) mod.CloudDenies++;
                        else mod.AgentDenies++;
                    }
                    else
                    {
                        mod.AgentReviews++;
                    }
                }
            }
        }
    }
}
