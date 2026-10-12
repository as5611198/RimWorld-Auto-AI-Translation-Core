using System;

namespace AutoTranslator_Core
{
    internal static class ConcurrencyRecoveryPolicy
    {
        internal const int MaximumRetries = 3;
        private static readonly int[] DelaysMilliseconds = { 3000, 8000, 20000 };

        internal static int GetDelayMilliseconds(int retryNumber)
        {
            if (retryNumber < 1 || retryNumber > DelaysMilliseconds.Length)
                throw new ArgumentOutOfRangeException(nameof(retryNumber));
            return DelaysMilliseconds[retryNumber - 1];
        }

        internal static bool IsQuotaExhaustion(string errorText, string responseBody)
        {
            string value = ((errorText ?? string.Empty) + " " + (responseBody ?? string.Empty)).ToLowerInvariant();
            return value.Contains("insufficient_quota") ||
                   value.Contains("quota exhausted") ||
                   value.Contains("billing hard limit") ||
                   value.Contains("余额不足") ||
                   value.Contains("配额已用尽");
        }

        internal static bool IsConcurrencyLimit(long httpCode, string errorText, string responseBody)
        {
            return httpCode == 429 && !IsQuotaExhaustion(errorText, responseBody);
        }
    }
}
