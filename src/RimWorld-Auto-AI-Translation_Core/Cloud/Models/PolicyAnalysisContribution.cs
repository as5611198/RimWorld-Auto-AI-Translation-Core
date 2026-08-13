using System;
using System.Collections.Generic;

namespace AutoTranslator_Core
{
    public sealed class PolicyAnalysisContribution
    {
        public PolicyAnalysisContribution()
        {
            SchemaVersion = 2;
            CandidateDomain = PolicyAnalysisCandidateDomain.Xml;
            PackageId = string.Empty;
            ModName = string.Empty;
            GameVersion = string.Empty;
            SourceFingerprint = string.Empty;
            PolicyVersion = string.Empty;
            PromptVersion = string.Empty;
            ContributorId = string.Empty;
            ContributionId = string.Empty;
            AddAllowedCandidateIds = new List<string>();
            AnalyzedUtc = string.Empty;
        }

        public int SchemaVersion { get; set; }
        public string CandidateDomain { get; set; }
        public string PackageId { get; set; }
        public string ModName { get; set; }
        public string GameVersion { get; set; }
        public string SourceFingerprint { get; set; }
        public string PolicyVersion { get; set; }
        public string PromptVersion { get; set; }
        public string ContributorId { get; set; }
        public string ContributionId { get; set; }
        public int CandidateCount { get; set; }
        public List<string> AddAllowedCandidateIds { get; set; }
        public string AnalyzedUtc { get; set; }
    }
}
