using AutoTranslator_Core;
using System;

internal static class Program
{
    private static int Main()
    {
        try
        {
            DateTime now = new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc);
            var tracker = new PolicyAnalysisCloudHealthTracker(3, 60);
            PolicyAnalysisCloudHealthTransition missing = tracker.RecordFailure(
                PolicyAnalysisCloudFailureKind.NotFound, now);
            Assert(!missing.ShouldWarn, "404 must not show a user warning");
            Assert(tracker.CanAttempt(now), "single 404 must not immediately open the circuit");

            PolicyAnalysisCloudHealthTransition timeout = tracker.RecordFailure(
                PolicyAnalysisCloudFailureKind.Timeout, now.AddSeconds(1));
            Assert(timeout.ShouldWarn, "first actionable outage must warn once");
            PolicyAnalysisCloudHealthTransition server = tracker.RecordFailure(
                PolicyAnalysisCloudFailureKind.HttpError, now.AddSeconds(2));
            Assert(!server.ShouldWarn && server.CircuitOpened,
                "repeated failures must be deduplicated and open the circuit");
            Assert(!tracker.CanAttempt(now.AddSeconds(30)), "circuit must skip repeated per-Mod waits");
            Assert(tracker.CanAttempt(now.AddSeconds(63)), "circuit must permit a later recovery probe");

            PolicyAnalysisCloudHealthTransition recovered = tracker.RecordSuccess();
            Assert(recovered.ShouldReportRecovery, "outage recovery must be reported once");
            Assert(!tracker.RecordSuccess().ShouldReportRecovery, "recovery notice must be deduplicated");
            Assert(tracker.CanAttempt(now), "success must reset the circuit");

            var outageTracker = new PolicyAnalysisCloudHealthTracker(3, 60);
            int cloudAttempts = 0;
            int warnings = 0;
            int locallyCompletedMods = 0;
            for (int modIndex = 0; modIndex < 50; modIndex++)
            {
                DateTime taskTime = now.AddMilliseconds(modIndex);
                if (outageTracker.CanAttempt(taskTime))
                {
                    cloudAttempts++;
                    PolicyAnalysisCloudHealthTransition failure = outageTracker.RecordFailure(
                        PolicyAnalysisCloudFailureKind.Timeout,
                        taskTime);
                    if (failure.ShouldWarn) warnings++;
                }

                // Production callers treat an unavailable/missing cloud record as
                // a cache miss and continue with local rules or the Agent.
                locallyCompletedMods++;
            }
            Assert(locallyCompletedMods == 50,
                "a total cloud outage must not stop later local Mod work");
            Assert(warnings == 1,
                "a total cloud outage must produce one non-blocking user warning");
            Assert(cloudAttempts == 3,
                "three failures must open the circuit and skip repeated per-Mod waits");

            Console.WriteLine("PASS: policy cloud warning, circuit-breaker, and recovery self-test");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
