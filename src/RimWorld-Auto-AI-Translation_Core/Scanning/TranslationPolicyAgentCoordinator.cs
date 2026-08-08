using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslator_Core.TranslationPolicy;
using Verse;

namespace AutoTranslator_Core
{
    internal static class TranslationPolicyAgentCoordinator
    {
        private const int BudgetPromptDispatchTimeoutMilliseconds = 5000;

        private sealed class GroupWork
        {
            public GroupWork()
            {
                GroupKey = string.Empty;
                RequestId = string.Empty;
                CorpusFingerprint = string.Empty;
                CacheKey = string.Empty;
                Candidates = new List<TranslationPolicyCandidate>();
                Request = new TranslationPolicyAgentRequestGroup();
            }

            public string GroupKey;
            public string RequestId;
            public string CorpusFingerprint;
            public string CacheKey;
            public List<TranslationPolicyCandidate> Candidates;
            public TranslationPolicyAgentRequestGroup Request;
        }

        private sealed class RunState
        {
            public RunState()
            {
                MemoryDecisions = new Dictionary<string, TranslationPolicyAgentGroupDecision>(StringComparer.Ordinal);
                ResolutionGate = new SemaphoreSlim(1, 1);
                EvaluatorFingerprint = string.Empty;
            }

            public long Id;
            public TranslationPolicyAgentRunBudget Budget;
            public ApiKeyConfig Config;
            public string EvaluatorFingerprint;
            public int MaximumRetries;
            public Dictionary<string, TranslationPolicyAgentGroupDecision> MemoryDecisions;
            public SemaphoreSlim ResolutionGate;
            public int LocalAllows;
            public int LocalDenies;
            public int CacheHits;
            public int AgentAllows;
            public int AgentDenies;
            public int AgentReviews;
            public int Unresolved;
            public long ExactTokens;
            public bool HasExactTokens;
            public bool NoProviderLogged;
            public bool BudgetLogged;
            public bool AgentDisabledLogged;
            public bool EmergencyLimitLogged;
            public TranslationPolicyAgentConsentDecision? ConsentDecision;
        }

        private static readonly object Gate = new object();
        private static long _nextRunId;
        private static RunState _activeRun;
        private static TranslationPolicyAgentDecisionCache _cache;

        public static long BeginRun(AutoTranslatorSettings settings)
        {
            lock (Gate)
            {
                if (settings == null || !settings.EnableTranslationPolicyAgent)
                {
                    _activeRun = null;
                    return 0L;
                }

                RunState state = new RunState
                {
                    Id = Interlocked.Increment(ref _nextRunId),
                    Budget = new TranslationPolicyAgentRunBudget(
                        settings.PolicyAgentMaxCallsPerRun,
                        settings.PolicyAgentMaxEstimatedTokensPerRun,
                        settings.PolicyAgentMaxCallsPerMod),
                    Config = AutoTranslatorAPI.GetPolicyAgentConfig(),
                    MaximumRetries = Math.Min(1, Math.Max(0, settings.PolicyAgentMaxRetriesPerRequest))
                };
                state.EvaluatorFingerprint = AutoTranslatorAPI.GetPolicyAgentEvaluatorFingerprint(state.Config);
                _activeRun = state;
                return state.Id;
            }
        }

        public static void EndRun(long runId)
        {
            RunState state;
            lock (Gate)
            {
                if (_activeRun == null || runId == 0L || _activeRun.Id != runId) return;
                state = _activeRun;
                _activeRun = null;
            }

            try
            {
                GetCache().Flush();
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[AutoTranslationCore] Policy Agent cache flush failed: " + ex.Message);
            }

            TranslationPolicyAgentBudgetSnapshot budget = state.Budget.GetSnapshot();
            AutoTranslatorSettings.AddLog(AutoTranslatorAPI.TranslateText("ATC_PolicyAgent_RunSummary",
                budget.CallsUsed,
                budget.EstimatedTokensReserved,
                state.CacheHits,
                state.LocalAllows,
                state.LocalDenies,
                state.AgentAllows,
                state.AgentDenies,
                state.AgentReviews,
                state.Unresolved));
            long actualTokens;
            if (TranslationPolicyAgentUsageSummary.TryGetActualTokens(
                    state.HasExactTokens,
                    state.ExactTokens,
                    out actualTokens))
            {
                AutoTranslatorSettings.AddLog(
                    AutoTranslatorAPI.TranslateText("ATC_PolicyAgent_ActualTokenSummary", actualTokens));
            }
        }

        public static bool IsEnabledForCurrentRun
        {
            get
            {
                lock (Gate)
                {
                    return _activeRun != null;
                }
            }
        }

        public static void RecordLocalOutcomes(int allowed, int denied)
        {
            lock (Gate)
            {
                if (_activeRun == null) return;
                _activeRun.LocalAllows += Math.Max(0, allowed);
                _activeRun.LocalDenies += Math.Max(0, denied);
            }
        }

        public static async Task<Dictionary<string, TranslationPolicyAgentCandidateOutcome>> ResolveCandidatesAsync(
            string packageId,
            IEnumerable<TranslationPolicyCandidate> candidates)
        {
            List<TranslationPolicyCandidate> materialized = (candidates ?? Enumerable.Empty<TranslationPolicyCandidate>())
                .Where(candidate => candidate != null)
                .GroupBy(GetCandidateId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            Dictionary<string, TranslationPolicyAgentCandidateOutcome> output = materialized
                .ToDictionary(
                    candidate => GetCandidateId(candidate),
                    candidate => CreateOutcome(TranslationPolicyAgentOutcomeStatus.NotAttempted),
                    StringComparer.Ordinal);
            if (materialized.Count == 0) return output;

            RunState state;
            lock (Gate)
            {
                state = _activeRun;
            }
            if (state == null)
            {
                SetRemainingOutcomes(output, TranslationPolicyAgentOutcomeStatus.NoProvider, "inactive_run", "");
                return output;
            }

            await state.ResolutionGate.WaitAsync();
            try
            {
                if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested)
                {
                    SetRemainingOutcomes(output, TranslationPolicyAgentOutcomeStatus.Cancelled, "cancelled", "");
                    return output;
                }

                if (state.Config == null || string.IsNullOrWhiteSpace(state.EvaluatorFingerprint))
                {
                    LogNoProviderOnce(state);
                    SetRemainingOutcomes(output, TranslationPolicyAgentOutcomeStatus.NoProvider, "no_policy_provider", "");
                    return output;
                }

                string modName = materialized
                    .Select(candidate => candidate.ModName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ??
                    (packageId ?? string.Empty);

                List<GroupWork> groups = BuildGroups(materialized, state.EvaluatorFingerprint);
                List<TranslationPolicyCandidate> candidateCacheCandidates =
                    new List<TranslationPolicyCandidate>();
                List<TranslationPolicyAgentDecisionCache.CandidateCacheEntry> promotedCandidateEntries =
                    new List<TranslationPolicyAgentDecisionCache.CandidateCacheEntry>();
                foreach (GroupWork group in groups)
                {
                    TranslationPolicyAgentGroupDecision decision;
                    if (TryGetGroupDecision(state, group, out decision))
                    {
                        ApplyGroupDecision(output, group, decision);
                        CountResolvedCandidates(state, group.Candidates.Count, decision.Decision, true);
                        PromoteGroupDecisionToCandidateCache(
                            state,
                            group,
                            decision,
                            packageId,
                            promotedCandidateEntries);
                        continue;
                    }

                    candidateCacheCandidates.AddRange(group.Candidates);
                }

                if (promotedCandidateEntries.Count > 0)
                {
                    try
                    {
                        GetCache().PutCandidateRangeDeferred(promotedCandidateEntries);
                    }
                    catch (Exception ex)
                    {
                        Verse.Log.Warning("[AutoTranslationCore] Policy Agent candidate cache promotion failed: " + ex.Message);
                    }
                }

                TranslationPolicyAgentCandidateResolutionPlan candidatePlan =
                    TranslationPolicyAgentResolutionPlanner.CreateCandidatePlan(
                        candidateCacheCandidates,
                        AutoTranslatorAPI.TranslationPolicyAgentPolicyVersion,
                        AutoTranslatorAPI.TranslationPolicyAgentPromptVersion,
                        state.EvaluatorFingerprint,
                        (cacheKey, candidateId, groupKey) =>
                            TryGetCandidateDecision(state, cacheKey, candidateId, groupKey));
                foreach (TranslationPolicyAgentCachedCandidate cached in candidatePlan.CachedCandidates)
                {
                    output[cached.CandidateId] = CreateOutcome(
                        TranslationPolicyAgentOutcomeStatus.Classified,
                        cached.Decision.Decision,
                        string.Empty,
                        cached.Decision.Reason);
                    CountResolvedCandidates(state, 1, cached.Decision.Decision, true);
                }

                List<GroupWork> misses = BuildGroups(
                    candidatePlan.CreateRequestScopes(),
                    state.EvaluatorFingerprint);

                if (state.Budget.IsAgentDisabled)
                {
                    SetRemainingOutcomes(
                        output,
                        GetDisabledOutcomeStatus(state),
                        "agent_disabled",
                        "");
                    return output;
                }

                int recoverableBatchFailures = 0;
                List<List<GroupWork>> batches = TranslationPolicyAgentBatchPlanner.CreateBatches(misses);
                TranslationPolicyAgentOutcomeStatus terminalStatus =
                    TranslationPolicyAgentOutcomeStatus.NotAttempted;
                string terminalErrorCode = string.Empty;
                string terminalReason = string.Empty;
                for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
                {
                    List<GroupWork> batch = batches[batchIndex];
                    if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested)
                    {
                        terminalStatus = TranslationPolicyAgentOutcomeStatus.Cancelled;
                        terminalErrorCode = "cancelled";
                        break;
                    }

                    int remainingGroups = batches
                        .Skip(batchIndex)
                        .Sum(currentBatch => currentBatch.Count);

                    TranslationPolicyAgentBatchResult result = await AutoTranslatorAPI.ClassifyTranslationPolicyGroupsAsync(
                        batch.Select(group => group.Request).ToList(),
                        state.Config,
                        state.MaximumRetries,
                        (estimatedTokens, isRetry, currentBatchExactTokens) => TryReserveAttemptAsync(
                            state,
                            packageId,
                            modName,
                            remainingGroups,
                            estimatedTokens,
                            isRetry,
                            currentBatchExactTokens));
                    AccumulateExactUsage(state, result);

                    bool cancellationRequestedAfterRequest =
                        AutoTranslatorSettings.IsCancellationRequested ||
                        AutoTranslatorSettings.IsSkipCurrentRequested;

                    if (result == null || result.Decisions == null || result.Decisions.Count != batch.Count)
                    {
                        if (cancellationRequestedAfterRequest)
                        {
                            SetGroupOutcomes(
                                output,
                                batch,
                                TranslationPolicyAgentOutcomeStatus.Cancelled,
                                "cancelled",
                                string.Empty);
                            terminalStatus = TranslationPolicyAgentOutcomeStatus.Cancelled;
                            terminalErrorCode = "cancelled";
                            break;
                        }

                        string errorCode = result != null && !string.IsNullOrWhiteSpace(result.ErrorCode)
                            ? result.ErrorCode
                            : "unknown";
                        TranslationPolicyAgentOutcomeStatus failureStatus = GetFailureOutcomeStatus(
                            state,
                            result,
                            errorCode);
                        string failureReason = failureStatus == TranslationPolicyAgentOutcomeStatus.ProviderFailure
                            ? "Policy Agent request failed (" + errorCode + ")."
                            : string.Empty;
                        SetGroupOutcomes(output, batch, failureStatus, errorCode, failureReason);
                        if (result != null && result.BudgetDenied && state.Budget.IsEmergencyLimitReached)
                        {
                            LogEmergencyLimitOnce(state);
                        }
                        else if (result != null && result.BudgetDenied && state.Budget.IsAgentDisabled)
                        {
                            LogAgentDisabledOnce(state);
                        }
                        else if (result != null && result.BudgetDenied)
                        {
                            LogBudgetOnce(state);
                        }
                        else
                        {
                            AutoTranslatorSettings.AddLog("⚠️ " + AutoTranslatorAPI.TranslateText(
                                "ATC_Log_AIFail",
                                "Policy Agent (" + errorCode + ")"));
                        }

                        bool canIsolateFailure =
                            failureStatus == TranslationPolicyAgentOutcomeStatus.ProviderFailure &&
                            (string.Equals(errorCode, "malformed_response", StringComparison.Ordinal) ||
                             string.Equals(errorCode, "truncated_response", StringComparison.Ordinal));
                        if (!canIsolateFailure || recoverableBatchFailures >= 1)
                        {
                            terminalStatus = failureStatus;
                            terminalErrorCode = errorCode;
                            terminalReason = failureReason;
                            break;
                        }

                        recoverableBatchFailures++;
                        continue;
                    }

                    Dictionary<string, TranslationPolicyAgentGroupDecision> decisions = result.Decisions
                        .Where(decision => decision != null && !string.IsNullOrWhiteSpace(decision.Id))
                        .GroupBy(decision => decision.Id, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                    List<TranslationPolicyAgentDecisionCache.CacheEntry> cacheEntries =
                        new List<TranslationPolicyAgentDecisionCache.CacheEntry>(batch.Count);
                    List<TranslationPolicyAgentDecisionCache.CandidateCacheEntry> candidateCacheEntries =
                        new List<TranslationPolicyAgentDecisionCache.CandidateCacheEntry>();
                    foreach (GroupWork group in batch)
                    {
                        TranslationPolicyAgentGroupDecision decision;
                        if (!TranslationPolicyAgentResponseMapper.TryMap(
                                group.GroupKey,
                                group.RequestId,
                                decisions,
                                out decision))
                        {
                            SetGroupOutcome(
                                output,
                                group,
                                TranslationPolicyAgentOutcomeStatus.ProviderFailure,
                                "missing_group_decision",
                                "Policy Agent response omitted this request group.");
                            continue;
                        }

                        state.MemoryDecisions[group.CacheKey] = decision;
                        cacheEntries.Add(CreateCacheEntry(group, decision, packageId, state.EvaluatorFingerprint));
                        foreach (TranslationPolicyCandidate candidate in group.Candidates)
                        {
                            string candidateCacheKey = CreateCandidateCacheKey(
                                group.GroupKey,
                                candidate,
                                state.EvaluatorFingerprint);
                            state.MemoryDecisions[candidateCacheKey] = decision;
                            candidateCacheEntries.Add(CreateCandidateCacheEntry(
                                candidateCacheKey,
                                group.GroupKey,
                                candidate,
                                decision,
                                packageId,
                                state.EvaluatorFingerprint));
                        }
                        if (!cancellationRequestedAfterRequest &&
                            !AutoTranslatorSettings.IsCancellationRequested &&
                            !AutoTranslatorSettings.IsSkipCurrentRequested)
                        {
                            ApplyGroupDecision(output, group, decision);
                            CountResolvedCandidates(state, group.Candidates.Count, decision.Decision, false);
                        }
                    }

                    try
                    {
                        GetCache().PutRange(cacheEntries, candidateCacheEntries);
                    }
                    catch (Exception ex)
                    {
                        Verse.Log.Warning("[AutoTranslationCore] Policy Agent cache save failed: " + ex.Message);
                    }

                    if (cancellationRequestedAfterRequest)
                    {
                        SetGroupOutcomes(
                            output,
                            batch,
                            TranslationPolicyAgentOutcomeStatus.Cancelled,
                            "cancelled",
                            string.Empty);
                        terminalStatus = TranslationPolicyAgentOutcomeStatus.Cancelled;
                        terminalErrorCode = "cancelled";
                        break;
                    }
                }

                if (terminalStatus == TranslationPolicyAgentOutcomeStatus.NotAttempted)
                {
                    terminalStatus = AutoTranslatorSettings.IsCancellationRequested ||
                        AutoTranslatorSettings.IsSkipCurrentRequested
                        ? TranslationPolicyAgentOutcomeStatus.Cancelled
                        : TranslationPolicyAgentOutcomeStatus.ProviderFailure;
                    terminalErrorCode = terminalStatus == TranslationPolicyAgentOutcomeStatus.Cancelled
                        ? "cancelled"
                        : "incomplete_resolution";
                    terminalReason = terminalStatus == TranslationPolicyAgentOutcomeStatus.ProviderFailure
                        ? "Policy Agent did not produce an outcome for this candidate."
                        : string.Empty;
                }
                SetRemainingOutcomes(output, terminalStatus, terminalErrorCode, terminalReason);

                int unresolved = TranslationPolicyAgentUsageSummary.CountProviderFailures(output.Values);
                RecordUnresolved(state, unresolved);
                return output;
            }
            finally
            {
                state.ResolutionGate.Release();
            }
        }

        public static void ClearCache()
        {
            lock (Gate)
            {
                GetCache().Clear();
                if (_activeRun != null) _activeRun.MemoryDecisions.Clear();
            }
        }

        private static bool TryGetGroupDecision(
            RunState state,
            GroupWork group,
            out TranslationPolicyAgentGroupDecision decision)
        {
            decision = null;
            if (state == null || group == null) return false;
            if (state.MemoryDecisions.TryGetValue(group.CacheKey, out decision) &&
                decision != null &&
                string.Equals(decision.Id, group.GroupKey, StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                if (GetCache().TryGet(group.CacheKey, out decision) &&
                    decision != null &&
                    string.Equals(decision.Id, group.GroupKey, StringComparison.Ordinal))
                {
                    state.MemoryDecisions[group.CacheKey] = decision;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[AutoTranslationCore] Policy Agent cache lookup failed: " + ex.Message);
            }

            decision = null;
            return false;
        }

        private static TranslationPolicyAgentGroupDecision TryGetCandidateDecision(
            RunState state,
            string cacheKey,
            string candidateId,
            string groupKey)
        {
            if (state == null) return null;
            TranslationPolicyAgentGroupDecision decision;
            if (state.MemoryDecisions.TryGetValue(cacheKey, out decision) &&
                decision != null &&
                string.Equals(decision.Id, groupKey, StringComparison.Ordinal))
            {
                return decision;
            }

            try
            {
                if (GetCache().TryGetCandidate(cacheKey, candidateId, groupKey, out decision) &&
                    decision != null &&
                    string.Equals(decision.Id, groupKey, StringComparison.Ordinal))
                {
                    state.MemoryDecisions[cacheKey] = decision;
                    return decision;
                }
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[AutoTranslationCore] Policy Agent candidate cache lookup failed: " + ex.Message);
            }

            return null;
        }

        private static void PromoteGroupDecisionToCandidateCache(
            RunState state,
            GroupWork group,
            TranslationPolicyAgentGroupDecision decision,
            string packageId,
            List<TranslationPolicyAgentDecisionCache.CandidateCacheEntry> entries)
        {
            if (state == null || group == null || decision == null || entries == null) return;
            foreach (TranslationPolicyCandidate candidate in group.Candidates)
            {
                string cacheKey = CreateCandidateCacheKey(
                    group.GroupKey,
                    candidate,
                    state.EvaluatorFingerprint);
                TranslationPolicyAgentGroupDecision existing = TryGetCandidateDecision(
                    state,
                    cacheKey,
                    GetCandidateId(candidate),
                    group.GroupKey);
                if (existing != null && existing.Decision == decision.Decision) continue;
                state.MemoryDecisions[cacheKey] = decision;
                entries.Add(CreateCandidateCacheEntry(
                    cacheKey,
                    group.GroupKey,
                    candidate,
                    decision,
                    packageId,
                    state.EvaluatorFingerprint));
            }
        }

        private static List<GroupWork> BuildGroups(
            List<TranslationPolicyCandidate> candidates,
            string evaluatorFingerprint)
        {
            return candidates
                .GroupBy(TranslationPolicyGrouping.CreateGroupKey, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => CreateGroupWork(
                    group.Key,
                    group.Key,
                    group,
                    evaluatorFingerprint))
                .ToList();
        }

        private static List<GroupWork> BuildGroups(
            IEnumerable<TranslationPolicyAgentRequestScope> scopes,
            string evaluatorFingerprint)
        {
            return (scopes ?? Enumerable.Empty<TranslationPolicyAgentRequestScope>())
                .Where(scope => scope != null &&
                    !string.IsNullOrWhiteSpace(scope.GroupKey) &&
                    !string.IsNullOrWhiteSpace(scope.RequestId))
                .OrderBy(scope => scope.RequestId, StringComparer.Ordinal)
                .Select(scope => CreateGroupWork(
                    scope.GroupKey,
                    scope.RequestId,
                    scope.Candidates,
                    evaluatorFingerprint))
                .ToList();
        }

        private static GroupWork CreateGroupWork(
            string groupKey,
            string requestId,
            IEnumerable<TranslationPolicyCandidate> candidates,
            string evaluatorFingerprint)
        {
            List<TranslationPolicyCandidate> groupCandidates = (candidates ??
                    Enumerable.Empty<TranslationPolicyCandidate>())
                .Where(candidate => candidate != null)
                .OrderBy(candidate => GetCandidateId(candidate), StringComparer.Ordinal)
                .ToList();
            if (groupCandidates.Count == 0)
                throw new ArgumentException("A Policy Agent request group requires at least one candidate.");

            if (groupCandidates.Any(candidate => !string.Equals(
                    TranslationPolicyGrouping.CreateGroupKey(candidate),
                    groupKey,
                    StringComparison.Ordinal)))
            {
                throw new ArgumentException("A Policy Agent request scope contains candidates from another group.");
            }

            TranslationPolicyCandidate first = groupCandidates[0];
            string corpusFingerprint = TranslationPolicyIdentity.CreateGroupCorpusFingerprint(groupCandidates);
            GroupWork work = new GroupWork
            {
                GroupKey = groupKey,
                RequestId = requestId,
                CorpusFingerprint = corpusFingerprint,
                Candidates = groupCandidates
            };
            work.CacheKey = TranslationPolicyIdentity.CreateAgentCacheKey(
                AutoTranslatorAPI.TranslationPolicyAgentPolicyVersion,
                AutoTranslatorAPI.TranslationPolicyAgentPromptVersion,
                evaluatorFingerprint,
                work.GroupKey,
                work.CorpusFingerprint);
            work.Request = new TranslationPolicyAgentRequestGroup
            {
                Id = work.RequestId,
                Bucket = first.Bucket.ToString(),
                PackageId = first.PackageId ?? string.Empty,
                DefType = first.DefType ?? string.Empty,
                Path = TranslationPolicyGrouping.NormalizeForGrouping(first),
                Field = first.FieldName ?? string.Empty,
                CorpusFingerprint = corpusFingerprint,
                CandidateCount = groupCandidates.Count,
                Samples = groupCandidates.Take(5).Select(candidate => new TranslationPolicyAgentSample
                {
                    CandidateId = GetCandidateId(candidate),
                    Path = candidate.KeyOrPath ?? string.Empty,
                    Text = candidate.SourceText ?? string.Empty
                }).ToList()
            };
            return work;
        }

        private static TranslationPolicyAgentDecisionCache.CacheEntry CreateCacheEntry(
            GroupWork group,
            TranslationPolicyAgentGroupDecision decision,
            string packageId,
            string evaluatorFingerprint)
        {
            return new TranslationPolicyAgentDecisionCache.CacheEntry
            {
                CacheKey = group.CacheKey,
                GroupKey = group.GroupKey,
                GroupCorpusFingerprint = group.CorpusFingerprint,
                PackageId = packageId ?? string.Empty,
                Decision = decision.Decision.ToString(),
                Reason = decision.Reason ?? string.Empty,
                PolicyVersion = AutoTranslatorAPI.TranslationPolicyAgentPolicyVersion,
                PromptVersion = AutoTranslatorAPI.TranslationPolicyAgentPromptVersion,
                EvaluatorFingerprint = evaluatorFingerprint
            };
        }

        private static string CreateCandidateCacheKey(
            string groupKey,
            TranslationPolicyCandidate candidate,
            string evaluatorFingerprint)
        {
            return TranslationPolicyIdentity.CreateAgentCandidateCacheKey(
                AutoTranslatorAPI.TranslationPolicyAgentPolicyVersion,
                AutoTranslatorAPI.TranslationPolicyAgentPromptVersion,
                evaluatorFingerprint,
                groupKey,
                GetCandidateId(candidate));
        }

        private static TranslationPolicyAgentDecisionCache.CandidateCacheEntry CreateCandidateCacheEntry(
            string cacheKey,
            string groupKey,
            TranslationPolicyCandidate candidate,
            TranslationPolicyAgentGroupDecision decision,
            string packageId,
            string evaluatorFingerprint)
        {
            return new TranslationPolicyAgentDecisionCache.CandidateCacheEntry
            {
                CacheKey = cacheKey,
                CandidateId = GetCandidateId(candidate),
                GroupKey = groupKey,
                PackageId = candidate != null && !string.IsNullOrWhiteSpace(candidate.PackageId)
                    ? candidate.PackageId
                    : packageId ?? string.Empty,
                Decision = decision != null ? decision.Decision.ToString() : string.Empty,
                Reason = decision != null ? decision.Reason ?? string.Empty : string.Empty,
                PolicyVersion = AutoTranslatorAPI.TranslationPolicyAgentPolicyVersion,
                PromptVersion = AutoTranslatorAPI.TranslationPolicyAgentPromptVersion,
                EvaluatorFingerprint = evaluatorFingerprint
            };
        }

        private static void ApplyGroupDecision(
            Dictionary<string, TranslationPolicyAgentCandidateOutcome> output,
            GroupWork group,
            TranslationPolicyAgentGroupDecision decision)
        {
            foreach (TranslationPolicyCandidate candidate in group.Candidates)
            {
                output[GetCandidateId(candidate)] = CreateOutcome(
                    TranslationPolicyAgentOutcomeStatus.Classified,
                    decision != null ? decision.Decision : TranslationPolicyAgentDecision.Unresolved,
                    string.Empty,
                    decision != null ? decision.Reason : string.Empty);
            }
        }

        private static void SetGroupOutcomes(
            Dictionary<string, TranslationPolicyAgentCandidateOutcome> output,
            IEnumerable<GroupWork> groups,
            TranslationPolicyAgentOutcomeStatus status,
            string errorCode,
            string reason)
        {
            foreach (GroupWork group in groups ?? Enumerable.Empty<GroupWork>())
                SetGroupOutcome(output, group, status, errorCode, reason);
        }

        private static void SetGroupOutcome(
            Dictionary<string, TranslationPolicyAgentCandidateOutcome> output,
            GroupWork group,
            TranslationPolicyAgentOutcomeStatus status,
            string errorCode,
            string reason)
        {
            if (output == null || group == null) return;
            foreach (TranslationPolicyCandidate candidate in group.Candidates)
            {
                output[GetCandidateId(candidate)] = CreateOutcome(
                    status,
                    TranslationPolicyAgentDecision.Unresolved,
                    errorCode,
                    reason);
            }
        }

        private static void SetRemainingOutcomes(
            Dictionary<string, TranslationPolicyAgentCandidateOutcome> output,
            TranslationPolicyAgentOutcomeStatus status,
            string errorCode,
            string reason)
        {
            if (output == null) return;
            foreach (string candidateId in output.Keys.ToList())
            {
                TranslationPolicyAgentCandidateOutcome current = output[candidateId];
                if (current != null && current.Status != TranslationPolicyAgentOutcomeStatus.NotAttempted)
                    continue;
                output[candidateId] = CreateOutcome(
                    status,
                    TranslationPolicyAgentDecision.Unresolved,
                    errorCode,
                    reason);
            }
        }

        private static TranslationPolicyAgentCandidateOutcome CreateOutcome(
            TranslationPolicyAgentOutcomeStatus status,
            TranslationPolicyAgentDecision decision = TranslationPolicyAgentDecision.Unresolved,
            string errorCode = "",
            string reason = "")
        {
            return new TranslationPolicyAgentCandidateOutcome
            {
                Decision = decision,
                Status = status,
                ErrorCode = errorCode ?? string.Empty,
                Reason = reason ?? string.Empty
            };
        }

        private static TranslationPolicyAgentOutcomeStatus GetDisabledOutcomeStatus(RunState state)
        {
            if (state == null) return TranslationPolicyAgentOutcomeStatus.NoProvider;
            if (state.Budget != null && state.Budget.IsEmergencyLimitReached)
                return TranslationPolicyAgentOutcomeStatus.SafetyLimit;
            if (state.ConsentDecision == TranslationPolicyAgentConsentDecision.LocalOnly)
                return TranslationPolicyAgentOutcomeStatus.LocalOnly;
            if (state.ConsentDecision == TranslationPolicyAgentConsentDecision.Cancel)
                return TranslationPolicyAgentOutcomeStatus.Cancelled;
            return TranslationPolicyAgentOutcomeStatus.BudgetLimit;
        }

        private static TranslationPolicyAgentOutcomeStatus GetFailureOutcomeStatus(
            RunState state,
            TranslationPolicyAgentBatchResult result,
            string errorCode)
        {
            if (AutoTranslatorSettings.IsCancellationRequested ||
                AutoTranslatorSettings.IsSkipCurrentRequested ||
                string.Equals(errorCode, "cancelled", StringComparison.Ordinal))
            {
                return TranslationPolicyAgentOutcomeStatus.Cancelled;
            }
            if (string.Equals(errorCode, "no_policy_provider", StringComparison.Ordinal))
                return TranslationPolicyAgentOutcomeStatus.NoProvider;
            if (result != null && result.BudgetDenied)
            {
                if (state != null && state.Budget != null && state.Budget.IsEmergencyLimitReached)
                    return TranslationPolicyAgentOutcomeStatus.SafetyLimit;
                if (state != null && state.ConsentDecision == TranslationPolicyAgentConsentDecision.LocalOnly)
                    return TranslationPolicyAgentOutcomeStatus.LocalOnly;
                if (state != null && state.ConsentDecision == TranslationPolicyAgentConsentDecision.Cancel)
                    return TranslationPolicyAgentOutcomeStatus.Cancelled;
                if (state != null && state.Budget != null && state.Budget.IsAgentDisabled)
                    return TranslationPolicyAgentOutcomeStatus.LocalOnly;
                return TranslationPolicyAgentOutcomeStatus.BudgetLimit;
            }
            return TranslationPolicyAgentOutcomeStatus.ProviderFailure;
        }

        private static void CountResolvedCandidates(
            RunState state,
            int count,
            TranslationPolicyAgentDecision decision,
            bool cacheHit)
        {
            if (cacheHit) state.CacheHits += Math.Max(0, count);
            if (decision == TranslationPolicyAgentDecision.Allow) state.AgentAllows += Math.Max(0, count);
            else if (decision == TranslationPolicyAgentDecision.Deny) state.AgentDenies += Math.Max(0, count);
            else if (decision == TranslationPolicyAgentDecision.Review) state.AgentReviews += Math.Max(0, count);
        }

        private static void RecordUnresolved(RunState state, int count)
        {
            if (state == null || count <= 0) return;
            state.Unresolved += count;
        }

        private static void AccumulateExactUsage(RunState state, TranslationPolicyAgentBatchResult result)
        {
            if (state == null || result == null || !result.ExactTotalTokens.HasValue) return;
            long value = Math.Max(0L, result.ExactTotalTokens.Value);
            state.ExactTokens = value > long.MaxValue - state.ExactTokens
                ? long.MaxValue
                : state.ExactTokens + value;
            state.HasExactTokens = true;
        }

        private static void LogNoProviderOnce(RunState state)
        {
            if (state.NoProviderLogged) return;
            state.NoProviderLogged = true;
            AutoTranslatorSettings.AddLog("ATC_PolicyAgent_NoProvider".Translate());
        }

        private static void LogBudgetOnce(RunState state)
        {
            if (state.BudgetLogged) return;
            state.BudgetLogged = true;
            AutoTranslatorSettings.AddLog("ATC_PolicyAgent_BudgetExhausted".Translate());
        }

        private static void LogAgentDisabledOnce(RunState state)
        {
            if (state.AgentDisabledLogged) return;
            state.AgentDisabledLogged = true;
            AutoTranslatorSettings.AddLog("ATC_PolicyAgent_LocalOnlySelected".Translate());
        }

        private static void LogEmergencyLimitOnce(RunState state)
        {
            if (state.EmergencyLimitLogged) return;
            state.EmergencyLimitLogged = true;
            AutoTranslatorSettings.AddLog(AutoTranslatorAPI.TranslateText("ATC_PolicyAgent_EmergencyLimitReached",
                TranslationPolicyAgentRunBudget.EmergencyMaximumCalls,
                TranslationPolicyAgentRunBudget.EmergencyMaximumEstimatedTokens));
        }

        private static async Task<bool> TryReserveAttemptAsync(
            RunState state,
            string packageId,
            string modName,
            int remainingGroups,
            long estimatedTokens,
            bool isRetry,
            long? currentBatchExactTokens)
        {
            if (state == null || state.Budget == null || state.Budget.IsAgentDisabled)
                return false;

            if (state.Budget.TryReserveAttempt(packageId, estimatedTokens, isRetry))
                return true;

            if (state.Budget.IsEmergencyLimitReached) return false;
            if (state.Budget.IsAgentDisabled) return false;
            if (state.ConsentDecision == TranslationPolicyAgentConsentDecision.ContinueWithAgent)
            {
                state.Budget.GrantUnlimited();
                return state.Budget.TryReserveAttempt(packageId, estimatedTokens, isRetry);
            }
            if (state.ConsentDecision == TranslationPolicyAgentConsentDecision.LocalOnly)
                return false;
            if (state.ConsentDecision == TranslationPolicyAgentConsentDecision.Cancel)
                return false;

            TranslationPolicyAgentConsentDecision decision = await RequestBudgetConsentAsync(
                state,
                packageId,
                modName,
                remainingGroups,
                estimatedTokens,
                isRetry,
                currentBatchExactTokens);
            if (AutoTranslatorSettings.IsCancellationRequested ||
                AutoTranslatorSettings.IsSkipCurrentRequested)
            {
                return false;
            }

            state.ConsentDecision = decision;
            if (decision == TranslationPolicyAgentConsentDecision.ContinueWithAgent)
            {
                state.Budget.GrantUnlimited();
                return state.Budget.TryReserveAttempt(packageId, estimatedTokens, isRetry);
            }
            if (decision == TranslationPolicyAgentConsentDecision.LocalOnly)
            {
                state.Budget.DisableAgent();
                return false;
            }

            if (!AutoTranslatorSettings.IsSkipCurrentRequested)
                AutoTranslatorSettings.RequestPipelineCancellation();
            return false;
        }

        private static async Task<TranslationPolicyAgentConsentDecision> RequestBudgetConsentAsync(
            RunState state,
            string packageId,
            string modName,
            int remainingGroups,
            long estimatedTokens,
            bool isRetry,
            long? currentBatchExactTokens)
        {
            TaskCompletionSource<TranslationPolicyAgentConsentDecision> completion =
                new TaskCompletionSource<TranslationPolicyAgentConsentDecision>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> dispatchStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TranslationPolicyAgentBudgetSnapshot snapshot = state.Budget.GetSnapshot(packageId);
            Window_TranslationPolicyAgentBudget promptWindow = null;
            string previousSubTaskName = AutoTranslatorMod.Settings != null
                ? AutoTranslatorMod.Settings.SubTaskName
                : string.Empty;

            if (AutoTranslatorMod.Settings != null)
            {
                AutoTranslatorMod.Settings.SubTaskName =
                    "ATC_PolicyAgent_BudgetPrompt_Waiting".Translate();
            }

            ATC_Dispatcher.RunOnMainThread(() =>
            {
                if (!dispatchStarted.TrySetResult(true)) return;
                if (completion.Task.IsCompleted)
                {
                    return;
                }

                try
                {
                    string safeModName = string.IsNullOrWhiteSpace(modName) ? packageId : modName;
                    long projectedEstimatedTokens = SaturatingAdd(
                        snapshot.EstimatedTokensReserved,
                        Math.Max(0L, estimatedTokens));
                    bool hasReportedTokens = state.HasExactTokens || currentBatchExactTokens.HasValue;
                    long reportedTokens = SaturatingAdd(
                        state.ExactTokens,
                        Math.Max(0L, currentBatchExactTokens ?? 0L));
                    string reportedTokensLine = hasReportedTokens
                        ? "\n" + "ATC_PolicyAgent_BudgetPrompt_ReportedTokens".Translate(reportedTokens).ToString()
                        : string.Empty;
                    string requestKind = (isRetry
                        ? "ATC_PolicyAgent_BudgetPrompt_RequestRetry"
                        : "ATC_PolicyAgent_BudgetPrompt_RequestNew").Translate();
                    string message = "ATC_PolicyAgent_BudgetPrompt_Message".Translate(
                        safeModName ?? string.Empty,
                        snapshot.CallsUsed,
                        snapshot.CallsUsed + 1,
                        snapshot.MaximumCalls,
                        snapshot.CallsUsedForMod,
                        snapshot.CallsUsedForMod + 1,
                        snapshot.MaximumCallsPerMod,
                        snapshot.EstimatedTokensReserved,
                        projectedEstimatedTokens,
                        snapshot.MaximumEstimatedTokens,
                        reportedTokensLine,
                        Math.Max(0, remainingGroups),
                        requestKind);
                    promptWindow = new Window_TranslationPolicyAgentBudget(
                        "ATC_PolicyAgent_BudgetPrompt_Title".Translate(),
                        message,
                        decision => completion.TrySetResult(decision));
                    if (completion.Task.IsCompleted) return;
                    Find.WindowStack.Add(promptWindow);
                    if (completion.Task.IsCompleted) promptWindow.Close();
                }
                catch (Exception ex)
                {
                    Verse.Log.Warning("[AutoTranslationCore] Policy Agent budget prompt failed: " + ex.Message);
                    completion.TrySetResult(TranslationPolicyAgentConsentDecision.LocalOnly);
                }
            });

            int dispatchWaitMilliseconds = 0;
            while (!completion.Task.IsCompleted &&
                   !dispatchStarted.Task.IsCompleted &&
                   dispatchWaitMilliseconds < BudgetPromptDispatchTimeoutMilliseconds)
            {
                if (AutoTranslatorSettings.IsCancellationRequested ||
                    AutoTranslatorSettings.IsSkipCurrentRequested)
                {
                    completion.TrySetResult(TranslationPolicyAgentConsentDecision.Cancel);
                    break;
                }

                await Task.Delay(100);
                dispatchWaitMilliseconds += 100;
            }

            if (!completion.Task.IsCompleted && !dispatchStarted.Task.IsCompleted)
            {
                if (dispatchStarted.TrySetResult(false))
                {
                    Verse.Log.Warning("[AutoTranslationCore] Policy Agent budget prompt dispatch timed out; continuing locally.");
                    completion.TrySetResult(TranslationPolicyAgentConsentDecision.LocalOnly);
                }
            }
            else if (!completion.Task.IsCompleted && !await dispatchStarted.Task)
            {
                completion.TrySetResult(TranslationPolicyAgentConsentDecision.LocalOnly);
            }

            while (!completion.Task.IsCompleted)
            {
                if (AutoTranslatorSettings.IsCancellationRequested ||
                    AutoTranslatorSettings.IsSkipCurrentRequested)
                {
                    completion.TrySetResult(TranslationPolicyAgentConsentDecision.Cancel);
                    break;
                }

                await Task.Delay(100);
            }

            TranslationPolicyAgentConsentDecision finalDecision = await completion.Task;
            if (finalDecision == TranslationPolicyAgentConsentDecision.Cancel && promptWindow != null)
            {
                ATC_Dispatcher.RunOnMainThread(() => promptWindow.Close());
            }
            if (AutoTranslatorMod.Settings != null &&
                string.Equals(
                    AutoTranslatorMod.Settings.SubTaskName,
                    "ATC_PolicyAgent_BudgetPrompt_Waiting".Translate().ToString(),
                    StringComparison.Ordinal))
            {
                AutoTranslatorMod.Settings.SubTaskName = previousSubTaskName;
            }

            return finalDecision;
        }

        private static long SaturatingAdd(long left, long right)
        {
            if (right > 0L && left > long.MaxValue - right) return long.MaxValue;
            return left + right;
        }

        private static string GetCandidateId(TranslationPolicyCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.CandidateId)) return candidate.CandidateId;
            candidate.CandidateId = TranslationPolicyIdentity.CreateCandidateId(candidate);
            return candidate.CandidateId;
        }

        private static TranslationPolicyAgentDecisionCache GetCache()
        {
            lock (Gate)
            {
                if (_cache != null) return _cache;
                string path = Path.Combine(
                    AutoTranslatorScanner.GetLocalPackPath(),
                    "Cache",
                    "TranslationPolicyAgentDecisions.v1.json");
                _cache = new TranslationPolicyAgentDecisionCache(path);
                return _cache;
            }
        }
    }
}
