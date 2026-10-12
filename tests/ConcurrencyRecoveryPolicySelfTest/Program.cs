using AutoTranslator_Core;
using System;

internal static class Program
{
    private static int Main()
    {
        try
        {
            Assert(ConcurrencyRecoveryPolicy.MaximumRetries == 3, "retry count");
            Assert(ConcurrencyRecoveryPolicy.GetDelayMilliseconds(1) == 3000, "first delay");
            Assert(ConcurrencyRecoveryPolicy.GetDelayMilliseconds(2) == 8000, "second delay");
            Assert(ConcurrencyRecoveryPolicy.GetDelayMilliseconds(3) == 20000, "third delay");
            Assert(ConcurrencyRecoveryPolicy.IsConcurrencyLimit(429, "too many requests", ""),
                "ordinary 429 must enter concurrency recovery");
            Assert(!ConcurrencyRecoveryPolicy.IsConcurrencyLimit(429, "insufficient_quota", ""),
                "quota failure must not be misreported as concurrency");
            Assert(!ConcurrencyRecoveryPolicy.IsConcurrencyLimit(500, "server error", ""),
                "non-429 must not enter concurrency recovery");
            bool threw = false;
            try { ConcurrencyRecoveryPolicy.GetDelayMilliseconds(4); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            Assert(threw, "a fourth retry must not be silently scheduled");
            Console.WriteLine("PASS: 429 classification and 3-step backoff self-test");
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
