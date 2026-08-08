using System;
using System.Collections.Generic;

namespace AutoTranslator_Core.TranslationPolicy
{
    public enum TranslationPolicyAgentConsentDecision
    {
        ContinueWithAgent = 0,
        LocalOnly = 1,
        Cancel = 2
    }

    public sealed class TranslationPolicyAgentRunBudget
    {
        public const int EmergencyMaximumCalls = 200;
        public const long EmergencyMaximumEstimatedTokens = 2000000L;

        private readonly object _gate = new object();
        private readonly int _maximumCalls;
        private readonly long _maximumEstimatedTokens;
        private readonly int _maximumCallsPerMod;
        private readonly Dictionary<string, int> _callsByMod =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _callsUsed;
        private int _retryCallsUsed;
        private long _estimatedTokensReserved;
        private bool _unlimitedGranted;
        private bool _agentDisabled;
        private bool _emergencyLimitReached;

        public TranslationPolicyAgentRunBudget(
            int maximumCalls,
            long maximumEstimatedTokens,
            int maximumCallsPerMod)
        {
            _maximumCalls = Math.Min(20, Math.Max(0, maximumCalls));
            _maximumEstimatedTokens = Math.Min(200000L, Math.Max(0L, maximumEstimatedTokens));
            // This is a soft per-mod threshold. Explicit consent bypasses it for the run.
            _maximumCallsPerMod = Math.Min(20, Math.Max(0, maximumCallsPerMod));
        }

        public bool TryReserveAttempt(string packageId, long estimatedTokens, bool isRetry)
        {
            string normalizedPackageId = (packageId ?? string.Empty).Trim().ToLowerInvariant();
            long safeEstimatedTokens = Math.Max(0L, estimatedTokens);

            lock (_gate)
            {
                int modCalls;
                if (!_callsByMod.TryGetValue(normalizedPackageId, out modCalls)) modCalls = 0;

                if (_agentDisabled) return false;
                if (_callsUsed >= EmergencyMaximumCalls ||
                    safeEstimatedTokens > EmergencyMaximumEstimatedTokens - _estimatedTokensReserved)
                {
                    _emergencyLimitReached = true;
                    _agentDisabled = true;
                    return false;
                }
                if (!_unlimitedGranted &&
                    (_callsUsed >= _maximumCalls ||
                     modCalls >= _maximumCallsPerMod ||
                     safeEstimatedTokens > _maximumEstimatedTokens - _estimatedTokensReserved))
                {
                    return false;
                }

                _callsUsed++;
                if (isRetry) _retryCallsUsed++;
                _estimatedTokensReserved += safeEstimatedTokens;
                _callsByMod[normalizedPackageId] = modCalls + 1;
                return true;
            }
        }

        public bool IsUnlimitedGranted
        {
            get
            {
                lock (_gate) return _unlimitedGranted;
            }
        }

        public bool IsAgentDisabled
        {
            get
            {
                lock (_gate) return _agentDisabled;
            }
        }

        public bool IsEmergencyLimitReached
        {
            get
            {
                lock (_gate) return _emergencyLimitReached;
            }
        }

        public void GrantUnlimited()
        {
            lock (_gate)
            {
                if (!_agentDisabled) _unlimitedGranted = true;
            }
        }

        public void DisableAgent()
        {
            lock (_gate)
            {
                _agentDisabled = true;
            }
        }

        public TranslationPolicyAgentBudgetSnapshot GetSnapshot()
        {
            return GetSnapshot(null);
        }

        public TranslationPolicyAgentBudgetSnapshot GetSnapshot(string packageId)
        {
            lock (_gate)
            {
                int callsUsedForMod = 0;
                string normalizedPackageId = (packageId ?? string.Empty).Trim().ToLowerInvariant();
                _callsByMod.TryGetValue(normalizedPackageId, out callsUsedForMod);

                return new TranslationPolicyAgentBudgetSnapshot
                {
                    CallsUsed = _callsUsed,
                    CallsUsedForMod = callsUsedForMod,
                    RetryCallsUsed = _retryCallsUsed,
                    EstimatedTokensReserved = _estimatedTokensReserved,
                    MaximumCalls = _maximumCalls,
                    MaximumEstimatedTokens = _maximumEstimatedTokens,
                    MaximumCallsPerMod = _maximumCallsPerMod,
                    UnlimitedGranted = _unlimitedGranted,
                    AgentDisabled = _agentDisabled,
                    EmergencyLimitReached = _emergencyLimitReached
                };
            }
        }
    }
}
