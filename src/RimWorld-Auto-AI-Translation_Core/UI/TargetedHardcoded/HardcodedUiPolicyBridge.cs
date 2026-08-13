using AutoTranslator_Core.TranslationPolicy;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal static class HardcodedUiPolicyBridge
    {
        internal static List<HardcodedUiPatchEntry> GetAgentCandidates(HardcodedUiScanResult result)
        {
            if (result == null) return new List<HardcodedUiPatchEntry>();
            return result.Entries.Where(entry =>
            {
                if (!result.Decisions.TryGetValue(entry.EntryId, out HardcodedUiDecisionRecord decision))
                    return true;
                return decision.UserOverride == HardcodedUiUserOverride.None &&
                       decision.AutomaticDecision == HardcodedUiAutomaticDecision.Uncertain;
            }).ToList();
        }

        internal static TranslationPolicyCandidate CreateCandidate(
            HardcodedUiPatchEntry entry,
            string modName)
        {
            return new TranslationPolicyCandidate
            {
                CandidateId = entry.EntryId,
                PackageId = entry.PackageId,
                ModName = modName ?? string.Empty,
                SourceFile = entry.AssemblyRelativePath,
                Bucket = TranslationPolicyBucket.Keyed,
                DefType = entry.DeclaringType,
                KeyOrPath = entry.MethodSignature + " -> " +
                    (!string.IsNullOrWhiteSpace(entry.CallDeclaringType)
                        ? entry.CallDeclaringType + "." + entry.CallMethodName
                        : entry.DiscoveryKind),
                FieldName = "runtimeUiLiteral",
                SourceText = entry.Literal,
                DeclaringAssembly = entry.AssemblyRelativePath,
                SchemaFingerprint = entry.AssemblySha256 + ":" + entry.AssemblyMvid
            };
        }

        internal static void ApplyAgentOutcomes(
            HardcodedUiScanResult result,
            IEnumerable<HardcodedUiPatchEntry> entries,
            IDictionary<string, TranslationPolicyAgentCandidateOutcome> outcomes)
        {
            if (result == null || outcomes == null) return;
            foreach (HardcodedUiPatchEntry entry in entries ?? Enumerable.Empty<HardcodedUiPatchEntry>())
            {
                if (!outcomes.TryGetValue(entry.EntryId, out TranslationPolicyAgentCandidateOutcome outcome) ||
                    outcome == null || outcome.Status != TranslationPolicyAgentOutcomeStatus.Classified)
                    continue;
                if (!result.Decisions.TryGetValue(entry.EntryId, out HardcodedUiDecisionRecord decision))
                {
                    decision = HardcodedUiBaselineDecisionAnalyzer.Analyze(entry);
                    result.Decisions[entry.EntryId] = decision;
                }
                HardcodedUiAutomaticDecision automatic =
                    outcome.Decision == TranslationPolicyAgentDecision.Allow
                        ? HardcodedUiAutomaticDecision.Translate
                        : outcome.Decision == TranslationPolicyAgentDecision.Deny
                            ? HardcodedUiAutomaticDecision.DoNotTranslate
                            : HardcodedUiAutomaticDecision.Uncertain;
                decision.SetAutomaticDecision(
                    automatic,
                    "AGENT_" + outcome.Decision.ToString().ToUpperInvariant(),
                    HardcodedUiIlDataflowAnalyzer.AnalyzerVersion,
                    HardcodedUiDecisionRecord.CreateAnalysisInputFingerprint(entry),
                    decision.SemanticRole,
                    outcome.Decision == TranslationPolicyAgentDecision.Review ? 0f : 0.8f,
                    outcome.Reason);
                entry.Enabled = decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate;
            }
            HardcodedUiDecisionState.Persist(result.Decisions.Values);
        }
    }
}
