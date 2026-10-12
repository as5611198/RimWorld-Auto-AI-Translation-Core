using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    public enum HardcodedUiAutomaticDecision
    {
        Uncertain = 0,
        Translate = 1,
        DoNotTranslate = 2
    }

    public enum HardcodedUiUserOverride
    {
        None = 0,
        Translate = 1,
        DoNotTranslate = 2,
        Uncertain = 3
    }

    public sealed class HardcodedUiDecisionRecord
    {
        public HardcodedUiDecisionRecord()
        {
            EntryId = string.Empty;
            PackageId = string.Empty;
            AnalysisInputFingerprint = string.Empty;
            AutomaticReasonCode = string.Empty;
            SemanticRole = string.Empty;
            EvidencePath = string.Empty;
            DiagnosticFlags = new List<string>();
        }

        [JsonProperty("entryId")]
        public string EntryId { get; set; }

        [JsonProperty("packageId")]
        public string PackageId { get; set; }

        [JsonProperty("analysisInputFingerprint")]
        public string AnalysisInputFingerprint { get; set; }

        [JsonProperty("analyzerVersion")]
        public int AnalyzerVersion { get; set; }

        [JsonProperty("automaticDecision")]
        public HardcodedUiAutomaticDecision AutomaticDecision { get; set; }

        [JsonProperty("automaticReasonCode")]
        public string AutomaticReasonCode { get; set; }

        [JsonProperty("semanticRole")]
        public string SemanticRole { get; set; }

        [JsonProperty("confidence")]
        public float Confidence { get; set; }

        [JsonProperty("evidencePath")]
        public string EvidencePath { get; set; }

        [JsonProperty("diagnosticFlags")]
        public List<string> DiagnosticFlags { get; set; }

        [JsonProperty("userOverride")]
        public HardcodedUiUserOverride UserOverride { get; set; }

        [JsonProperty("updatedUtc")]
        public string UpdatedUtc { get; set; } = string.Empty;

        [JsonIgnore]
        public HardcodedUiAutomaticDecision EffectiveDecision =>
            UserOverride == HardcodedUiUserOverride.Translate
                ? HardcodedUiAutomaticDecision.Translate
                : UserOverride == HardcodedUiUserOverride.DoNotTranslate
                    ? HardcodedUiAutomaticDecision.DoNotTranslate
                    : UserOverride == HardcodedUiUserOverride.Uncertain
                        ? HardcodedUiAutomaticDecision.Uncertain
                    : AutomaticDecision;

        public void SetAutomaticDecision(
            HardcodedUiAutomaticDecision decision,
            string reasonCode,
            int analyzerVersion,
            string inputFingerprint,
            string semanticRole,
            float confidence,
            string evidencePath)
        {
            AutomaticDecision = decision;
            AutomaticReasonCode = reasonCode ?? string.Empty;
            AnalyzerVersion = Math.Max(0, analyzerVersion);
            AnalysisInputFingerprint = inputFingerprint ?? string.Empty;
            SemanticRole = semanticRole ?? string.Empty;
            Confidence = Math.Max(0f, Math.Min(1f, confidence));
            EvidencePath = evidencePath ?? string.Empty;
            UpdatedUtc = DateTime.UtcNow.ToString("o");
        }

        public void SetUserOverride(HardcodedUiUserOverride value)
        {
            UserOverride = value;
            UpdatedUtc = DateTime.UtcNow.ToString("o");
        }

        public void RestoreAutomaticDecision()
        {
            SetUserOverride(HardcodedUiUserOverride.None);
        }

        public bool IsAutomaticDecisionCurrent(string inputFingerprint, int analyzerVersion)
        {
            return AnalyzerVersion == analyzerVersion &&
                   !string.IsNullOrWhiteSpace(AnalysisInputFingerprint) &&
                   string.Equals(
                       AnalysisInputFingerprint,
                       inputFingerprint ?? string.Empty,
                       StringComparison.Ordinal);
        }

        public HardcodedUiDecisionRecord Clone()
        {
            return new HardcodedUiDecisionRecord
            {
                EntryId = EntryId,
                PackageId = PackageId,
                AnalysisInputFingerprint = AnalysisInputFingerprint,
                AnalyzerVersion = AnalyzerVersion,
                AutomaticDecision = AutomaticDecision,
                AutomaticReasonCode = AutomaticReasonCode,
                SemanticRole = SemanticRole,
                Confidence = Confidence,
                EvidencePath = EvidencePath,
                DiagnosticFlags = new List<string>(DiagnosticFlags ?? new List<string>()),
                UserOverride = UserOverride,
                UpdatedUtc = UpdatedUtc
            };
        }

        public static string CreateAnalysisInputFingerprint(HardcodedUiPatchEntry entry)
        {
            if (entry == null) return string.Empty;
            string material = string.Join(
                "|",
                entry.PackageId ?? string.Empty,
                HardcodedUiMethodIdentity.NormalizeRelativePath(entry.AssemblyRelativePath),
                entry.AssemblySha256 ?? string.Empty,
                entry.AssemblyMvid ?? string.Empty,
                entry.MethodSignature ?? string.Empty,
                entry.MethodMetadataToken.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.MethodIlFingerprint ?? string.Empty,
                entry.LiteralOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.Literal ?? string.Empty);
            return "hardcoded-analysis:" + HardcodedUiMethodIdentity.ComputeSha256(material);
        }
    }

    public sealed class HardcodedUiDecisionStoreFile
    {
        public HardcodedUiDecisionStoreFile()
        {
            StoreVersion = 1;
            Records = new List<HardcodedUiDecisionRecord>();
        }

        [JsonProperty("storeVersion")]
        public int StoreVersion { get; set; }

        [JsonProperty("records")]
        public List<HardcodedUiDecisionRecord> Records { get; set; }
    }
}
