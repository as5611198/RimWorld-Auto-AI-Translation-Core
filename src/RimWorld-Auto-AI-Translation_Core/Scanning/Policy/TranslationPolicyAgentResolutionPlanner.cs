using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core.TranslationPolicy
{
    public sealed class TranslationPolicyAgentCachedCandidate
    {
        public TranslationPolicyAgentCachedCandidate()
        {
            CandidateId = string.Empty;
            GroupKey = string.Empty;
            CacheKey = string.Empty;
        }

        public TranslationPolicyCandidate Candidate { get; set; }
        public string CandidateId { get; set; }
        public string GroupKey { get; set; }
        public string CacheKey { get; set; }
        public TranslationPolicyAgentGroupDecision Decision { get; set; }
    }

    public sealed class TranslationPolicyAgentCandidateMissGroup
    {
        public TranslationPolicyAgentCandidateMissGroup()
        {
            GroupKey = string.Empty;
            Candidates = new List<TranslationPolicyCandidate>();
        }

        public string GroupKey { get; set; }
        public List<TranslationPolicyCandidate> Candidates { get; set; }
    }

    public sealed class TranslationPolicyAgentRequestScope
    {
        public TranslationPolicyAgentRequestScope()
        {
            GroupKey = string.Empty;
            RequestId = string.Empty;
            Candidates = new List<TranslationPolicyCandidate>();
        }

        public string GroupKey { get; set; }
        public string RequestId { get; set; }
        public bool IsCandidateScoped { get; set; }
        public List<TranslationPolicyCandidate> Candidates { get; set; }
    }

    public sealed class TranslationPolicyAgentCandidateResolutionPlan
    {
        public TranslationPolicyAgentCandidateResolutionPlan()
        {
            CachedCandidates = new List<TranslationPolicyAgentCachedCandidate>();
            MissGroups = new List<TranslationPolicyAgentCandidateMissGroup>();
        }

        public List<TranslationPolicyAgentCachedCandidate> CachedCandidates { get; set; }
        public List<TranslationPolicyAgentCandidateMissGroup> MissGroups { get; set; }

        public int CacheHitCandidateCount
        {
            get { return CachedCandidates == null ? 0 : CachedCandidates.Count; }
        }

        public List<TranslationPolicyCandidate> GetMissingCandidates()
        {
            return (MissGroups ?? new List<TranslationPolicyAgentCandidateMissGroup>())
                .Where(group => group != null)
                .SelectMany(group => group.Candidates ?? new List<TranslationPolicyCandidate>())
                .ToList();
        }

        public List<TranslationPolicyAgentRequestScope> CreateRequestScopes()
        {
            HashSet<string> cachedGroupKeys = new HashSet<string>(
                (CachedCandidates ?? new List<TranslationPolicyAgentCachedCandidate>())
                    .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.GroupKey))
                    .Select(candidate => candidate.GroupKey),
                StringComparer.Ordinal);
            List<TranslationPolicyAgentRequestScope> scopes =
                new List<TranslationPolicyAgentRequestScope>();

            foreach (TranslationPolicyAgentCandidateMissGroup missGroup in
                (MissGroups ?? new List<TranslationPolicyAgentCandidateMissGroup>())
                    .Where(group => group != null && !string.IsNullOrWhiteSpace(group.GroupKey))
                    .OrderBy(group => group.GroupKey, StringComparer.Ordinal))
            {
                List<TranslationPolicyCandidate> candidates = (missGroup.Candidates ??
                        new List<TranslationPolicyCandidate>())
                    .Where(candidate => candidate != null)
                    .OrderBy(GetCandidateId, StringComparer.Ordinal)
                    .ToList();
                if (candidates.Count == 0) continue;

                if (!cachedGroupKeys.Contains(missGroup.GroupKey))
                {
                    scopes.Add(new TranslationPolicyAgentRequestScope
                    {
                        GroupKey = missGroup.GroupKey,
                        RequestId = missGroup.GroupKey,
                        IsCandidateScoped = false,
                        Candidates = candidates
                    });
                    continue;
                }

                // A provider response is scoped to its top-level group id. Once a group has
                // both cached and uncached candidates, give every miss a distinct request id so
                // its result cannot be applied to any cached peer.
                foreach (TranslationPolicyCandidate candidate in candidates)
                {
                    string candidateId = GetCandidateId(candidate);
                    scopes.Add(new TranslationPolicyAgentRequestScope
                    {
                        GroupKey = missGroup.GroupKey,
                        RequestId = TranslationPolicyIdentity.CreateAgentCandidateRequestId(
                            missGroup.GroupKey,
                            candidateId),
                        IsCandidateScoped = true,
                        Candidates = new List<TranslationPolicyCandidate> { candidate }
                    });
                }
            }

            return scopes;
        }

        private static string GetCandidateId(TranslationPolicyCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.CandidateId)) return candidate.CandidateId;
            candidate.CandidateId = TranslationPolicyIdentity.CreateCandidateId(candidate);
            return candidate.CandidateId;
        }
    }

    public static class TranslationPolicyAgentResolutionPlanner
    {
        public static TranslationPolicyAgentCandidateResolutionPlan CreateCandidatePlan(
            IEnumerable<TranslationPolicyCandidate> candidates,
            string policyVersion,
            string promptVersion,
            string evaluatorFingerprint,
            Func<string, string, string, TranslationPolicyAgentGroupDecision> lookup)
        {
            TranslationPolicyAgentCandidateResolutionPlan plan =
                new TranslationPolicyAgentCandidateResolutionPlan();
            Dictionary<string, List<TranslationPolicyCandidate>> misses =
                new Dictionary<string, List<TranslationPolicyCandidate>>(StringComparer.Ordinal);

            List<TranslationPolicyCandidate> uniqueCandidates = (candidates ??
                    Enumerable.Empty<TranslationPolicyCandidate>())
                .Where(candidate => candidate != null)
                .Select(candidate => new
                {
                    Candidate = candidate,
                    CandidateId = GetCandidateId(candidate)
                })
                .GroupBy(item => item.CandidateId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.CandidateId, StringComparer.Ordinal)
                .Select(item => item.Candidate)
                .ToList();

            foreach (TranslationPolicyCandidate candidate in uniqueCandidates)
            {
                string candidateId = GetCandidateId(candidate);
                string groupKey = TranslationPolicyGrouping.CreateGroupKey(candidate);
                string cacheKey = TranslationPolicyIdentity.CreateAgentCandidateCacheKey(
                    policyVersion,
                    promptVersion,
                    evaluatorFingerprint,
                    groupKey,
                    candidateId);
                TranslationPolicyAgentGroupDecision decision = lookup != null
                    ? lookup(cacheKey, candidateId, groupKey)
                    : null;
                if (IsUsableDecision(decision, groupKey))
                {
                    plan.CachedCandidates.Add(new TranslationPolicyAgentCachedCandidate
                    {
                        Candidate = candidate,
                        CandidateId = candidateId,
                        GroupKey = groupKey,
                        CacheKey = cacheKey,
                        Decision = decision
                    });
                    continue;
                }

                List<TranslationPolicyCandidate> groupMisses;
                if (!misses.TryGetValue(groupKey, out groupMisses))
                {
                    groupMisses = new List<TranslationPolicyCandidate>();
                    misses[groupKey] = groupMisses;
                }
                groupMisses.Add(candidate);
            }

            plan.MissGroups = misses
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new TranslationPolicyAgentCandidateMissGroup
                {
                    GroupKey = pair.Key,
                    Candidates = pair.Value
                        .OrderBy(candidate => GetCandidateId(candidate), StringComparer.Ordinal)
                        .ToList()
                })
                .ToList();
            return plan;
        }

        private static string GetCandidateId(TranslationPolicyCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.CandidateId)) return candidate.CandidateId;
            candidate.CandidateId = TranslationPolicyIdentity.CreateCandidateId(candidate);
            return candidate.CandidateId;
        }

        private static bool IsUsableDecision(
            TranslationPolicyAgentGroupDecision decision,
            string groupKey)
        {
            if (decision == null ||
                !string.Equals(decision.Id, groupKey, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(decision.Reason))
            {
                return false;
            }

            return decision.Decision == TranslationPolicyAgentDecision.Allow ||
                decision.Decision == TranslationPolicyAgentDecision.Deny ||
                decision.Decision == TranslationPolicyAgentDecision.Review;
        }
    }

    public static class TranslationPolicyAgentResponseMapper
    {
        public static bool TryMap(
            string semanticGroupKey,
            string requestId,
            IDictionary<string, TranslationPolicyAgentGroupDecision> providerDecisions,
            out TranslationPolicyAgentGroupDecision decision)
        {
            decision = null;
            if (string.IsNullOrWhiteSpace(semanticGroupKey) ||
                string.IsNullOrWhiteSpace(requestId) ||
                providerDecisions == null)
            {
                return false;
            }

            TranslationPolicyAgentGroupDecision providerDecision;
            if (!providerDecisions.TryGetValue(requestId, out providerDecision) ||
                providerDecision == null ||
                !string.Equals(providerDecision.Id, requestId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(providerDecision.Reason))
            {
                return false;
            }

            if (providerDecision.Decision != TranslationPolicyAgentDecision.Allow &&
                providerDecision.Decision != TranslationPolicyAgentDecision.Deny &&
                providerDecision.Decision != TranslationPolicyAgentDecision.Review)
            {
                return false;
            }

            decision = new TranslationPolicyAgentGroupDecision
            {
                Id = semanticGroupKey,
                Decision = providerDecision.Decision,
                Reason = providerDecision.Reason
            };
            return true;
        }
    }
}
