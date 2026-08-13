using System;

namespace AutoTranslator_Core
{
    internal enum PolicyAnalysisCloudFailureKind
    {
        None,
        NotFound,
        Timeout,
        HttpError,
        InvalidJson,
        InvalidSchema,
        Transport
    }

    internal sealed class PolicyAnalysisCloudHealthTransition
    {
        internal bool ShouldWarn { get; set; }
        internal bool ShouldReportRecovery { get; set; }
        internal bool CircuitOpened { get; set; }
        internal int ConsecutiveFailures { get; set; }
        internal PolicyAnalysisCloudFailureKind FailureKind { get; set; }
    }

    internal sealed class PolicyAnalysisCloudHealthTracker
    {
        private readonly object _gate = new object();
        private readonly int _failureThreshold;
        private readonly TimeSpan _circuitDuration;
        private int _consecutiveFailures;
        private DateTime _circuitOpenUntilUtc;
        private bool _warningIssued;

        internal PolicyAnalysisCloudHealthTracker(int failureThreshold = 3, int circuitSeconds = 60)
        {
            _failureThreshold = Math.Max(1, failureThreshold);
            _circuitDuration = TimeSpan.FromSeconds(Math.Max(1, circuitSeconds));
        }

        internal bool CanAttempt(DateTime utcNow)
        {
            lock (_gate) return utcNow >= _circuitOpenUntilUtc;
        }

        internal PolicyAnalysisCloudHealthTransition RecordFailure(
            PolicyAnalysisCloudFailureKind kind,
            DateTime utcNow)
        {
            lock (_gate)
            {
                _consecutiveFailures++;
                bool opened = _consecutiveFailures >= _failureThreshold;
                if (opened) _circuitOpenUntilUtc = utcNow.Add(_circuitDuration);
                bool warn = kind != PolicyAnalysisCloudFailureKind.NotFound && !_warningIssued;
                if (warn) _warningIssued = true;
                return new PolicyAnalysisCloudHealthTransition
                {
                    ShouldWarn = warn,
                    CircuitOpened = opened,
                    ConsecutiveFailures = _consecutiveFailures,
                    FailureKind = kind
                };
            }
        }

        internal PolicyAnalysisCloudHealthTransition RecordSuccess()
        {
            lock (_gate)
            {
                bool recovered = _warningIssued;
                _consecutiveFailures = 0;
                _circuitOpenUntilUtc = DateTime.MinValue;
                _warningIssued = false;
                return new PolicyAnalysisCloudHealthTransition
                {
                    ShouldReportRecovery = recovered,
                    FailureKind = PolicyAnalysisCloudFailureKind.None
                };
            }
        }
    }
}
