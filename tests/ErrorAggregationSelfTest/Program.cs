using AutoTranslator_Core;
using System;

namespace ErrorAggregationSelfTest
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                var tracker = new TranslationErrorAggregationTracker();
                TranslationErrorAggregateSnapshot first = tracker.Record("timeout|provider-a", 20);
                Assert(first.IsFirstOccurrence, "first occurrence must be marked for user notification");
                Assert(first.Occurrences == 1, "first occurrence count");
                Assert(first.AffectedItems == 20, "first affected-item count");

                TranslationErrorAggregateSnapshot second = tracker.Record("timeout|provider-a", 15);
                Assert(!second.IsFirstOccurrence, "same root cause must not trigger another notification");
                Assert(second.Occurrences == 2, "same root cause should aggregate batches");
                Assert(second.AffectedItems == 35, "same root cause should aggregate affected entries");

                TranslationErrorAggregateSnapshot other = tracker.Record("http-500|provider-a", 4);
                Assert(other.IsFirstOccurrence, "different root cause should notify independently");
                Assert(other.Occurrences == 1 && other.AffectedItems == 4,
                    "different root cause should keep independent totals");

                TranslationErrorAggregateSnapshot negative = tracker.Record("timeout|provider-a", -100);
                Assert(negative.AffectedItems == 35, "negative affected count must not reduce totals");

                tracker.Reset();
                TranslationErrorAggregateSnapshot reset = tracker.Record("timeout|provider-a", 1);
                Assert(reset.IsFirstOccurrence && reset.Occurrences == 1 && reset.AffectedItems == 1,
                    "new translation task should start a fresh aggregation window");

                Console.WriteLine("PASS: error aggregation self-test");
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
}
