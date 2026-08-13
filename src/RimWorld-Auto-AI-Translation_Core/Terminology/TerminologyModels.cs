using Newtonsoft.Json;
using System.Collections.Generic;

namespace AutoTranslator_Core.Terminology
{
    internal static class TerminologyScope
    {
        internal const string Global = "global";
        internal const string ModGroup = "mod_group";
        internal const string Mod = "mod";
        internal const string Session = "session";
    }

    internal static class TerminologyStatus
    {
        internal const string Candidate = "candidate";
        internal const string SessionActive = "session_active";
        internal const string ModPersistent = "mod_persistent";
        internal const string GroupPersistent = "group_persistent";
        internal const string UserApproved = "user_approved";
        internal const string Rejected = "rejected";
    }

    internal sealed class TerminologyCorpusEntry
    {
        internal TerminologyCorpusEntry()
        {
            PackageId = string.Empty;
            GroupId = string.Empty;
            Key = string.Empty;
            DefType = string.Empty;
            Field = string.Empty;
            Text = string.Empty;
            SourceKind = string.Empty;
        }

        [JsonProperty("packageId")] internal string PackageId { get; set; }
        [JsonProperty("groupId")] internal string GroupId { get; set; }
        [JsonProperty("key")] internal string Key { get; set; }
        [JsonProperty("defType")] internal string DefType { get; set; }
        [JsonProperty("field")] internal string Field { get; set; }
        [JsonProperty("text")] internal string Text { get; set; }
        [JsonProperty("sourceKind")] internal string SourceKind { get; set; }
    }

    internal sealed class TerminologyCandidate
    {
        public TerminologyCandidate()
        {
            TermId = string.Empty;
            SourceForm = string.Empty;
            NormalizedForm = string.Empty;
            Target = string.Empty;
            SemanticRole = string.Empty;
            ScopeKind = TerminologyScope.Session;
            ScopeId = string.Empty;
            SourceScopeKind = string.Empty;
            SourceScopeId = string.Empty;
            Status = TerminologyStatus.Candidate;
            EvidenceKind = "mechanical_ngram";
            PackageIds = new List<string>();
            DefTypes = new List<string>();
            Fields = new List<string>();
            Contexts = new List<string>();
        }

        [JsonProperty("termId")] public string TermId { get; set; }
        [JsonProperty("sourceForm")] public string SourceForm { get; set; }
        [JsonProperty("normalizedForm")] public string NormalizedForm { get; set; }
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("semanticRole")] public string SemanticRole { get; set; }
        [JsonProperty("scopeKind")] public string ScopeKind { get; set; }
        [JsonProperty("scopeId")] public string ScopeId { get; set; }
        [JsonProperty("sourceScopeKind")] public string SourceScopeKind { get; set; }
        [JsonProperty("sourceScopeId")] public string SourceScopeId { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("evidenceKind")] public string EvidenceKind { get; set; }
        [JsonProperty("frequency")] public int Frequency { get; set; }
        [JsonProperty("packageCount")] public int PackageCount { get; set; }
        [JsonProperty("globalFrequency")] public int GlobalFrequency { get; set; }
        [JsonProperty("score")] public float Score { get; set; }
        [JsonProperty("packageIds")] public List<string> PackageIds { get; set; }
        [JsonProperty("defTypes")] public List<string> DefTypes { get; set; }
        [JsonProperty("fields")] public List<string> Fields { get; set; }
        [JsonProperty("contexts")] public List<string> Contexts { get; set; }
        [JsonProperty("updatedUtc")] public string UpdatedUtc { get; set; } = string.Empty;
        [JsonProperty("agentAttempted")] public bool AgentAttempted { get; set; }
        [JsonProperty("agentReason")] public string AgentReason { get; set; } = string.Empty;
    }

    internal sealed class TerminologyReviewItem
    {
        internal TerminologyCandidate Term { get; set; }
        internal bool HasConflict { get; set; }
        internal List<string> ConflictingTargets { get; set; } = new List<string>();
    }

    internal sealed class TerminologyCacheFile
    {
        public TerminologyCacheFile()
        {
            SchemaVersion = 1;
            Terms = new List<TerminologyCandidate>();
        }

        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("terms")] public List<TerminologyCandidate> Terms { get; set; }
    }

    internal sealed class TerminologySessionFile
    {
        public TerminologySessionFile()
        {
            SchemaVersion = 1;
            AnalyzerVersion = 1;
            SessionId = string.Empty;
            ScopeKind = string.Empty;
            ScopeId = string.Empty;
            SourceFingerprint = string.Empty;
            Corpus = new List<TerminologyCorpusEntry>();
            Candidates = new List<TerminologyCandidate>();
        }

        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("analyzerVersion")] public int AnalyzerVersion { get; set; }
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("scopeKind")] public string ScopeKind { get; set; }
        [JsonProperty("scopeId")] public string ScopeId { get; set; }
        [JsonProperty("sourceFingerprint")] public string SourceFingerprint { get; set; }
        [JsonProperty("agentCalls")] public int AgentCalls { get; set; }
        [JsonProperty("corpus")] public List<TerminologyCorpusEntry> Corpus { get; set; }
        [JsonProperty("candidates")] public List<TerminologyCandidate> Candidates { get; set; }
        [JsonProperty("updatedUtc")] public string UpdatedUtc { get; set; } = string.Empty;
    }

    internal sealed class TerminologyAgentDecision
    {
        internal string TermId { get; set; } = string.Empty;
        internal string Decision { get; set; } = string.Empty;
        internal string Target { get; set; } = string.Empty;
        internal string SemanticRole { get; set; } = string.Empty;
        internal string Reason { get; set; } = string.Empty;
    }

    internal sealed class TerminologyAgentBatchResult
    {
        internal List<TerminologyAgentDecision> Decisions { get; set; } = new List<TerminologyAgentDecision>();
        internal string ErrorCode { get; set; } = string.Empty;
        internal long? TotalTokens { get; set; }
    }
}
