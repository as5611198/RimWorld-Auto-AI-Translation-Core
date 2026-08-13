using System;
using System.Collections.Generic;

namespace AutoTranslator_Core
{
    public sealed class PolicyAnalysisCloudRecord
    {
        public PolicyAnalysisCloudRecord()
        {
            SchemaVersion = 2;
            CandidateDomain = PolicyAnalysisCandidateDomain.Xml;
            PackageId = string.Empty;
            ModName = string.Empty;
            GameVersion = string.Empty;
            SourceFingerprint = string.Empty;
            PolicyVersion = string.Empty;
            PromptVersion = string.Empty;
            AllowedCandidateIds = new List<string>();
            AnalyzedUtc = string.Empty;
            Complete = false;
            RetainLatestVersions = 3;
        }

        public int SchemaVersion { get; set; }
        public string CandidateDomain { get; set; }
        public string PackageId { get; set; }
        public string ModName { get; set; }
        public string GameVersion { get; set; }
        public string SourceFingerprint { get; set; }
        public string PolicyVersion { get; set; }
        public string PromptVersion { get; set; }
        public int CandidateCount { get; set; }
        public List<string> AllowedCandidateIds { get; set; }
        public string AnalyzedUtc { get; set; }
        public bool Complete { get; set; }
        public int RetainLatestVersions { get; set; }
    }
}
