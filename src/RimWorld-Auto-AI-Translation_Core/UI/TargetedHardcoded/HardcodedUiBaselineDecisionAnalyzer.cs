using System;
using System.Collections.Generic;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal static class HardcodedUiBaselineDecisionAnalyzer
    {
        internal const int AnalyzerVersion = 1;

        internal static HardcodedUiDecisionRecord Analyze(
            HardcodedUiPatchEntry entry,
            HardcodedUiDecisionRecord existing = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            string fingerprint = HardcodedUiDecisionRecord.CreateAnalysisInputFingerprint(entry);
            if (existing != null && existing.AnalyzerVersion >= AnalyzerVersion &&
                string.Equals(existing.AnalysisInputFingerprint, fingerprint, StringComparison.Ordinal))
                return existing.Clone();

            HardcodedUiUserOverride userOverride = existing?.UserOverride ?? HardcodedUiUserOverride.None;
            var record = new HardcodedUiDecisionRecord
            {
                EntryId = entry.EntryId,
                PackageId = entry.PackageId,
                UserOverride = userOverride,
                DiagnosticFlags = new List<string>()
            };
            if (existing != null && !string.IsNullOrWhiteSpace(existing.AnalysisInputFingerprint))
                record.DiagnosticFlags.Add("previous_analysis_stale");

            if (string.Equals(entry.DiscoveryKind, "direct_ui_call", StringComparison.Ordinal))
            {
                record.SetAutomaticDecision(
                    HardcodedUiAutomaticDecision.Translate,
                    "UI_DIRECT_CALL",
                    AnalyzerVersion,
                    fingerprint,
                    GetSemanticRole(entry),
                    1f,
                    BuildEvidence(entry));
            }
            else
            {
                record.SetAutomaticDecision(
                    HardcodedUiAutomaticDecision.Uncertain,
                    string.Equals(entry.DiscoveryKind, "ui_method_literal", StringComparison.Ordinal)
                        ? "UNKNOWN_UI_METHOD_FLOW"
                        : "UNKNOWN_DATA_FLOW",
                    AnalyzerVersion,
                    fingerprint,
                    string.Empty,
                    0f,
                    BuildEvidence(entry));
            }
            return record;
        }

        private static string GetSemanticRole(HardcodedUiPatchEntry entry)
        {
            string name = entry?.CallMethodName ?? string.Empty;
            if (name.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0) return "button";
            if (name.IndexOf("Tooltip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Tip", StringComparison.OrdinalIgnoreCase) >= 0) return "tooltip";
            if (name.IndexOf("Message", StringComparison.OrdinalIgnoreCase) >= 0) return "message";
            if (name.IndexOf("Checkbox", StringComparison.OrdinalIgnoreCase) >= 0) return "settings_item";
            return "label";
        }

        private static string BuildEvidence(HardcodedUiPatchEntry entry)
        {
            return (entry.DeclaringType ?? string.Empty) + "." +
                   (entry.MethodName ?? string.Empty) + " -> " +
                   (string.IsNullOrWhiteSpace(entry.CallDeclaringType)
                       ? entry.DiscoveryKind ?? string.Empty
                       : entry.CallDeclaringType + "." + entry.CallMethodName);
        }
    }
}
