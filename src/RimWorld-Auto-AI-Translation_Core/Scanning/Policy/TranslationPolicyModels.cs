using System;
using System.Collections.Generic;

namespace AutoTranslator_Core.TranslationPolicy
{
    public enum TranslationPolicyBucket
    {
        Keyed = 0,
        DefInjected = 1
    }

    public enum TranslationPolicyDecision
    {
        HardAllow = 0,
        HardDeny = 1,
        Ambiguous = 2
    }

    public sealed class TranslationPolicySourceContext
    {
        public TranslationPolicySourceContext()
        {
            PackageId = string.Empty;
            ModName = string.Empty;
            SourceFile = string.Empty;
            DeclaringAssembly = string.Empty;
            SchemaFingerprint = string.Empty;
        }

        public string PackageId { get; set; }
        public string ModName { get; set; }
        public string SourceFile { get; set; }
        public string DeclaringAssembly { get; set; }
        public string SchemaFingerprint { get; set; }
    }

    public sealed class TranslationPolicyCandidate
    {
        public TranslationPolicyCandidate()
        {
            CandidateId = string.Empty;
            PackageId = string.Empty;
            ModName = string.Empty;
            SourceFile = string.Empty;
            DefType = string.Empty;
            KeyOrPath = string.Empty;
            FieldName = string.Empty;
            SourceText = string.Empty;
            DeclaringAssembly = string.Empty;
            SchemaFingerprint = string.Empty;
        }

        public string CandidateId { get; set; }
        public string PackageId { get; set; }
        public string ModName { get; set; }
        public string SourceFile { get; set; }
        public TranslationPolicyBucket Bucket { get; set; }
        public string DefType { get; set; }
        public string KeyOrPath { get; set; }
        public string FieldName { get; set; }
        public string SourceText { get; set; }
        public string DeclaringAssembly { get; set; }
        public string SchemaFingerprint { get; set; }
    }

    public sealed class TranslationPolicyClassification
    {
        public TranslationPolicyClassification()
        {
            CandidateId = string.Empty;
            ReasonCode = string.Empty;
        }

        public string CandidateId { get; set; }
        public TranslationPolicyDecision Decision { get; set; }
        public string ReasonCode { get; set; }
    }

    public sealed class TranslationPolicyShadowOptions
    {
        public TranslationPolicyShadowOptions()
        {
            MaxSamplesPerGroup = 5;
            GroupsPerRequest = 20;
            MaxConcurrency = 3;
            PromptTokenEstimate = 1000;
            CharactersPerToken = 3.0d;
            OutputTokensPerGroup = 32;
            MaxRetriesPerRequest = 1;
            EstimatedMillisecondsPerRequest = 60000;
            MaxCandidates = 5000000;
            MaxAmbiguousGroups = 500000;
            MaxReportedAmbiguousGroups = 1000;
            MaxDiagnosticSamples = 100;
        }

        public int MaxSamplesPerGroup { get; set; }
        public int GroupsPerRequest { get; set; }
        public int MaxConcurrency { get; set; }
        public int PromptTokenEstimate { get; set; }
        public double CharactersPerToken { get; set; }
        public int OutputTokensPerGroup { get; set; }
        public int MaxRetriesPerRequest { get; set; }
        public int EstimatedMillisecondsPerRequest { get; set; }
        public int MaxCandidates { get; set; }
        public int MaxAmbiguousGroups { get; set; }
        public int MaxReportedAmbiguousGroups { get; set; }
        public int MaxDiagnosticSamples { get; set; }
    }

    public sealed class TranslationPolicyShadowInput
    {
        public TranslationPolicyShadowInput()
        {
            Candidates = new List<TranslationPolicyCandidate>();
            Options = new TranslationPolicyShadowOptions();
        }

        public List<TranslationPolicyCandidate> Candidates { get; set; }
        public TranslationPolicyShadowOptions Options { get; set; }
    }

    public sealed class TranslationPolicyCandidateResult
    {
        public TranslationPolicyCandidateResult()
        {
            CandidateId = string.Empty;
            PackageId = string.Empty;
            ModName = string.Empty;
            SourceFile = string.Empty;
            DefType = string.Empty;
            KeyOrPath = string.Empty;
            NormalizedPath = string.Empty;
            FieldName = string.Empty;
            SourceText = string.Empty;
            ReasonCode = string.Empty;
            GroupKey = string.Empty;
        }

        public string CandidateId { get; set; }
        public string PackageId { get; set; }
        public string ModName { get; set; }
        public string SourceFile { get; set; }
        public TranslationPolicyBucket Bucket { get; set; }
        public string DefType { get; set; }
        public string KeyOrPath { get; set; }
        public string NormalizedPath { get; set; }
        public string FieldName { get; set; }
        public string SourceText { get; set; }
        public TranslationPolicyDecision Decision { get; set; }
        public string ReasonCode { get; set; }
        public string GroupKey { get; set; }
    }

    public sealed class TranslationPolicyGroupSample
    {
        public TranslationPolicyGroupSample()
        {
            CandidateId = string.Empty;
            SourceFile = string.Empty;
            KeyOrPath = string.Empty;
            SourceText = string.Empty;
        }

        public string CandidateId { get; set; }
        public string SourceFile { get; set; }
        public string KeyOrPath { get; set; }
        public string SourceText { get; set; }
    }

    public sealed class TranslationPolicyGroup
    {
        public TranslationPolicyGroup()
        {
            GroupKey = string.Empty;
            PackageId = string.Empty;
            DeclaringAssembly = string.Empty;
            SchemaFingerprint = string.Empty;
            DefType = string.Empty;
            NormalizedPath = string.Empty;
            FieldName = string.Empty;
            Samples = new List<TranslationPolicyGroupSample>();
        }

        public string GroupKey { get; set; }
        public TranslationPolicyBucket Bucket { get; set; }
        public string PackageId { get; set; }
        public string DeclaringAssembly { get; set; }
        public string SchemaFingerprint { get; set; }
        public string DefType { get; set; }
        public string NormalizedPath { get; set; }
        public string FieldName { get; set; }
        public int CandidateCount { get; set; }
        public List<TranslationPolicyGroupSample> Samples { get; set; }
    }

    public sealed class TranslationPolicyTokenEstimate
    {
        public int AmbiguousGroupCount { get; set; }
        public int ReportedAmbiguousGroupCount { get; set; }
        public bool GroupsTruncated { get; set; }
        public bool PayloadEstimateUsesReportedSample { get; set; }
        public int GroupsPerRequest { get; set; }
        public int EstimatedRequestCount { get; set; }
        public int EstimatedRequestWaves { get; set; }
        public long EstimatedPayloadCharacters { get; set; }
        public long EstimatedInputTokens { get; set; }
        public long EstimatedOutputTokens { get; set; }
        public long EstimatedTotalTokens { get; set; }
        public int EstimatedMaximumRequestCount { get; set; }
        public long EstimatedMaximumTotalTokens { get; set; }
        public long EstimatedLatencyMilliseconds { get; set; }
        public long EstimatedMaximumLatencyMilliseconds { get; set; }
    }

    public sealed class TranslationPolicySummary
    {
        public int TotalCandidates { get; set; }
        public int HardAllowCount { get; set; }
        public int HardDenyCount { get; set; }
        public int AmbiguousCount { get; set; }
        public int AmbiguousGroupCount { get; set; }
        public int ReportedAmbiguousGroupCount { get; set; }
        public bool GroupsTruncated { get; set; }
    }

    public sealed class TranslationPolicyCount
    {
        public TranslationPolicyCount()
        {
            Key = string.Empty;
        }

        public string Key { get; set; }
        public int Count { get; set; }
    }

    public sealed class TranslationPolicyModSummary
    {
        public TranslationPolicyModSummary()
        {
            PackageId = string.Empty;
        }

        public string PackageId { get; set; }
        public TranslationPolicyBucket Bucket { get; set; }
        public int TotalCandidates { get; set; }
        public int HardAllowCount { get; set; }
        public int HardDenyCount { get; set; }
        public int AmbiguousCount { get; set; }
    }

    public sealed class TranslationPolicyShadowResult
    {
        public TranslationPolicyShadowResult()
        {
            ResultVersion = 1;
            CorpusFingerprint = string.Empty;
            DeterministicFingerprint = string.Empty;
            DistinctGroupFingerprint = string.Empty;
            AppliedOptions = new TranslationPolicyShadowOptions();
            Summary = new TranslationPolicySummary();
            RuleCounts = new List<TranslationPolicyCount>();
            ModSummaries = new List<TranslationPolicyModSummary>();
            DiagnosticSamples = new List<TranslationPolicyCandidateResult>();
            AmbiguousGroups = new List<TranslationPolicyGroup>();
            Estimate = new TranslationPolicyTokenEstimate();
        }

        public int ResultVersion { get; set; }
        public string CorpusFingerprint { get; set; }
        public string DeterministicFingerprint { get; set; }
        public string DistinctGroupFingerprint { get; set; }
        public TranslationPolicyShadowOptions AppliedOptions { get; set; }
        public TranslationPolicySummary Summary { get; set; }
        public List<TranslationPolicyCount> RuleCounts { get; set; }
        public List<TranslationPolicyModSummary> ModSummaries { get; set; }
        public List<TranslationPolicyCandidateResult> DiagnosticSamples { get; set; }
        public List<TranslationPolicyGroup> AmbiguousGroups { get; set; }
        public TranslationPolicyTokenEstimate Estimate { get; set; }
    }
}
