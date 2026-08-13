using AutoTranslator_Core;
using System;
using System.Collections.Generic;
using static AutoTranslator_Core.AutoTranslatorAPI;

internal static class Program
{
    private static int Main()
    {
        try
        {
            var expected = new Dictionary<TranslationRequestFailureKind, string>
            {
                [TranslationRequestFailureKind.LocalDispatch] = "ATC_ApiFailureCategory_LocalDispatch",
                [TranslationRequestFailureKind.ResponseTimeout] = "ATC_ApiFailureCategory_ResponseTimeout",
                [TranslationRequestFailureKind.Http] = "ATC_ApiFailureCategory_Http",
                [TranslationRequestFailureKind.ConcurrencyLimit] = "ATC_ApiFailureCategory_Concurrency",
                [TranslationRequestFailureKind.QuotaExhausted] = "ATC_ApiFailureCategory_Quota",
                [TranslationRequestFailureKind.InvalidResponse] = "ATC_ApiFailureCategory_InvalidResponse",
                [TranslationRequestFailureKind.Configuration] = "ATC_ApiFailureCategory_Configuration",
                [TranslationRequestFailureKind.Cancelled] = "ATC_ApiFailureCategory_Cancelled",
                [TranslationRequestFailureKind.Transport] = "ATC_ApiFailureCategory_Transport"
            };
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<TranslationRequestFailureKind, string> item in expected)
            {
                string actual = TranslationFailureDiagnosticPolicy.GetCategoryKey(
                    new TranslationFailureDiagnosticData { FailureKind = item.Key });
                Assert(actual == item.Value, item.Key + " category");
                Assert(unique.Add(actual), item.Key + " must have a distinct user category");
            }

            var timeout = new TranslationFailureDiagnosticData
            {
                Provider = "DeepSeek",
                Model = "deepseek-v4-flash",
                FailureKind = TranslationRequestFailureKind.ResponseTimeout,
                FailureStage = "WaitingResponse",
                Attempts = 1,
                ItemCount = 20,
                SourceCharacters = 6400,
                EstimatedInputTokens = 1800,
                TimeoutSeconds = 100,
                ErrorText = "Request timed out"
            };
            string timeoutDetail = TranslationFailureDiagnosticPolicy.BuildDeveloperDetail(timeout, "NO_RESPONSE");
            AssertContains(timeoutDetail, "Stage=WaitingResponse", "timeout stage");
            AssertContains(timeoutDetail, "Items=20", "timeout item count");
            AssertContains(timeoutDetail, "SourceChars=6400", "timeout source characters");
            AssertContains(timeoutDetail, "EstimatedInputTokens=1800", "timeout token estimate");
            AssertContains(timeoutDetail, "TimeoutSeconds=100", "timeout limit");
            AssertContains(timeoutDetail, "Response=NO_RESPONSE", "timeout must explicitly state no response");

            var malformed = new TranslationFailureDiagnosticData
            {
                FailureKind = TranslationRequestFailureKind.InvalidResponse,
                FailureStage = "ResponseParsing",
                HttpCode = 200,
                ResponseSummary = "{bad-json}",
                ErrorText = "missing translations"
            };
            string malformedDetail = TranslationFailureDiagnosticPolicy.BuildDeveloperDetail(malformed, "NO_RESPONSE");
            AssertContains(malformedDetail, "HTTP=200", "malformed HTTP status");
            AssertContains(malformedDetail, "Stage=ResponseParsing", "malformed stage");
            AssertContains(malformedDetail, "Response={bad-json}", "malformed response summary");
            Assert(!malformedDetail.Contains("Response=NO_RESPONSE"), "returned malformed content must not look like a timeout");

            Assert(TranslationFailureDiagnosticPolicy.GetCategoryKey(new TranslationFailureDiagnosticData
            {
                FailureKind = TranslationRequestFailureKind.Http,
                IsQuotaExhausted = true
            }) == "ATC_ApiFailureCategory_Quota", "quota signal overrides generic HTTP");
            Assert(TranslationFailureDiagnosticPolicy.GetCategoryKey(new TranslationFailureDiagnosticData
            {
                FailureKind = TranslationRequestFailureKind.Http,
                IsConcurrencyLimit = true
            }) == "ATC_ApiFailureCategory_Concurrency", "429 signal overrides generic HTTP");

            Console.WriteLine("PASS: nine API failure categories and diagnostic details");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static void AssertContains(string value, string expected, string name)
    {
        Assert((value ?? string.Empty).Contains(expected), name + ": missing " + expected);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
