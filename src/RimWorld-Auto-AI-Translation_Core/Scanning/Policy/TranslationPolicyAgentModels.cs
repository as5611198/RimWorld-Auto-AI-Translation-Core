using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core.TranslationPolicy
{
    public enum TranslationPolicyAgentDecision
    {
        Unresolved = 0,
        Allow = 1,
        Deny = 2,
        Review = 3
    }

    public enum TranslationPolicyAgentOutcomeStatus
    {
        NotAttempted = 0,
        Classified = 1,
        ProviderFailure = 2,
        LocalOnly = 3,
        NoProvider = 4,
        Cancelled = 5,
        SafetyLimit = 6,
        BudgetLimit = 7
    }

    public sealed class TranslationPolicyAgentCandidateOutcome
    {
        public TranslationPolicyAgentCandidateOutcome()
        {
            Reason = string.Empty;
            ErrorCode = string.Empty;
        }

        public TranslationPolicyAgentDecision Decision { get; set; }
        public TranslationPolicyAgentOutcomeStatus Status { get; set; }
        public string Reason { get; set; }
        public string ErrorCode { get; set; }

        public bool ShouldReportUnresolved(bool hasExistingTranslation)
        {
            if (hasExistingTranslation) return false;
            return Status == TranslationPolicyAgentOutcomeStatus.ProviderFailure ||
                (Status == TranslationPolicyAgentOutcomeStatus.Classified &&
                 Decision == TranslationPolicyAgentDecision.Review);
        }
    }

    public static class TranslationPolicyAgentUsageSummary
    {
        public static int CountProviderFailures(IEnumerable<TranslationPolicyAgentCandidateOutcome> outcomes)
        {
            return (outcomes ?? Enumerable.Empty<TranslationPolicyAgentCandidateOutcome>())
                .Count(outcome => outcome != null &&
                    outcome.Status == TranslationPolicyAgentOutcomeStatus.ProviderFailure);
        }

        public static bool TryGetActualTokens(bool hasExactTokens, long exactTokens, out long actualTokens)
        {
            actualTokens = Math.Max(0L, exactTokens);
            return hasExactTokens;
        }
    }

    public sealed class TranslationPolicyAgentSample
    {
        public TranslationPolicyAgentSample()
        {
            CandidateId = string.Empty;
            Path = string.Empty;
            Text = string.Empty;
        }

        public string CandidateId { get; set; }
        public string Path { get; set; }
        public string Text { get; set; }
    }

    public sealed class TranslationPolicyAgentRequestGroup
    {
        public TranslationPolicyAgentRequestGroup()
        {
            Id = string.Empty;
            Bucket = string.Empty;
            PackageId = string.Empty;
            DefType = string.Empty;
            Path = string.Empty;
            Field = string.Empty;
            CorpusFingerprint = string.Empty;
            Samples = new List<TranslationPolicyAgentSample>();
        }

        public string Id { get; set; }
        public string Bucket { get; set; }
        public string PackageId { get; set; }
        public string DefType { get; set; }
        public string Path { get; set; }
        public string Field { get; set; }
        public string CorpusFingerprint { get; set; }
        public int CandidateCount { get; set; }
        public List<TranslationPolicyAgentSample> Samples { get; set; }
    }

    public sealed class TranslationPolicyAgentGroupDecision
    {
        public TranslationPolicyAgentGroupDecision()
        {
            Id = string.Empty;
            Reason = string.Empty;
        }

        public string Id { get; set; }
        public TranslationPolicyAgentDecision Decision { get; set; }
        public string Reason { get; set; }
    }

    public sealed class TranslationPolicyAgentBatchResult
    {
        public TranslationPolicyAgentBatchResult()
        {
            Decisions = new List<TranslationPolicyAgentGroupDecision>();
            ErrorCode = string.Empty;
        }

        public List<TranslationPolicyAgentGroupDecision> Decisions { get; set; }
        public bool BudgetDenied { get; set; }
        public int Attempts { get; set; }
        public long EstimatedTokensReserved { get; set; }
        public long? ExactInputTokens { get; set; }
        public long? ExactOutputTokens { get; set; }
        public long? ExactTotalTokens { get; set; }
        public string ErrorCode { get; set; }
    }

    public sealed class TranslationPolicyAgentBudgetSnapshot
    {
        public int CallsUsed { get; set; }
        public int CallsUsedForMod { get; set; }
        public int RetryCallsUsed { get; set; }
        public long EstimatedTokensReserved { get; set; }
        public int MaximumCalls { get; set; }
        public long MaximumEstimatedTokens { get; set; }
        public int MaximumCallsPerMod { get; set; }
        public bool UnlimitedGranted { get; set; }
        public bool AgentDisabled { get; set; }
        public bool EmergencyLimitReached { get; set; }
    }

    public static class TranslationPolicyAgentTokenEstimator
    {
        public static long EstimateAttemptTokens(string systemPrompt, string userPayload, int maximumOutputTokens)
        {
            long characterCount = SaturatingAdd(
                systemPrompt == null ? 0L : systemPrompt.Length,
                userPayload == null ? 0L : userPayload.Length);
            long inputTokens = characterCount <= 0L ? 0L : 1L + ((characterCount - 1L) / 2L);
            return SaturatingAdd(inputTokens, Math.Max(0, maximumOutputTokens));
        }

        private static long SaturatingAdd(long left, long right)
        {
            if (right > 0L && left > long.MaxValue - right) return long.MaxValue;
            return left + right;
        }
    }
}
