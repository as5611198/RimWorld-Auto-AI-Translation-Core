using System;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorAPI
    {
        internal sealed class TranslationFailureDiagnosticData
        {
            internal string Provider = string.Empty;
            internal string Model = string.Empty;
            internal long HttpCode;
            internal int Attempts;
            internal TranslationRequestFailureKind FailureKind;
            internal string FailureStage = string.Empty;
            internal int ItemCount;
            internal long SourceCharacters;
            internal long EstimatedInputTokens;
            internal int TimeoutSeconds;
            internal long RequestBodyBytes;
            internal long UploadedBytes;
            internal float UploadProgress;
            internal long DownloadedBytes;
            internal string UnityResult = string.Empty;
            internal string ErrorText = string.Empty;
            internal string ResponseSummary = string.Empty;
            internal bool IsConcurrencyLimit;
            internal bool IsQuotaExhausted;
        }

        internal static class TranslationFailureDiagnosticPolicy
        {
            internal static void LogInvalidResponseForDeveloper(
                string context,
                string reason,
                string responseBody)
            {
                string safeContext = string.IsNullOrWhiteSpace(context) ? "translation" : context.Trim();
                string safeReason = string.IsNullOrWhiteSpace(reason) ? "unknown validation failure" : reason.Trim();
                string fullResponse = responseBody ?? string.Empty;
                ATC_Dispatcher.RunOnMainThread(() =>
                    Verse.Log.Error(
                        "[AutoTranslationCore] Provider returned an unusable response [" + safeContext + "]. " +
                        "Reason=" + safeReason + ". The complete response follows for developer diagnosis.\n" +
                        fullResponse));
            }

            internal static string GetCategoryKey(TranslationFailureDiagnosticData failure)
            {
                if (failure == null) return "ATC_ApiFailureCategory_Transport";
                if (failure.IsQuotaExhausted || failure.FailureKind == TranslationRequestFailureKind.QuotaExhausted)
                    return "ATC_ApiFailureCategory_Quota";
                if (failure.IsConcurrencyLimit || failure.FailureKind == TranslationRequestFailureKind.ConcurrencyLimit)
                    return "ATC_ApiFailureCategory_Concurrency";
                if (failure.FailureKind == TranslationRequestFailureKind.LocalDispatch)
                    return "ATC_ApiFailureCategory_LocalDispatch";
                if (failure.FailureKind == TranslationRequestFailureKind.ResponseTimeout)
                {
                    if (failure.RequestBodyBytes > 0L &&
                        failure.UploadedBytes >= failure.RequestBodyBytes)
                        return "ATC_ApiFailureCategory_ResponseTimeoutAfterUpload";
                    if (failure.RequestBodyBytes > 0L)
                        return "ATC_ApiFailureCategory_ResponseTimeoutDuringUpload";
                    return "ATC_ApiFailureCategory_ResponseTimeout";
                }
                if (failure.FailureKind == TranslationRequestFailureKind.UnityTransportStall)
                    return "ATC_ApiFailureCategory_UnityTransportStall";
                if (failure.FailureKind == TranslationRequestFailureKind.Http)
                    return "ATC_ApiFailureCategory_Http";
                if (failure.FailureKind == TranslationRequestFailureKind.InvalidResponse)
                    return "ATC_ApiFailureCategory_InvalidResponse";
                if (failure.FailureKind == TranslationRequestFailureKind.Configuration)
                    return "ATC_ApiFailureCategory_Configuration";
                if (failure.FailureKind == TranslationRequestFailureKind.BudgetDenied)
                    return "ATC_ApiFailureCategory_BudgetDenied";
                if (failure.FailureKind == TranslationRequestFailureKind.Cancelled)
                    return "ATC_ApiFailureCategory_Cancelled";
                return "ATC_ApiFailureCategory_Transport";
            }

            internal static string BuildDeveloperDetail(
                TranslationFailureDiagnosticData failure,
                string noResponseText)
            {
                failure = failure ?? new TranslationFailureDiagnosticData();
                string response = failure.FailureKind == TranslationRequestFailureKind.ResponseTimeout &&
                                  string.IsNullOrWhiteSpace(failure.ResponseSummary)
                    ? EmptyAsUnknown(noResponseText)
                    : EmptyAsUnknown(failure.ResponseSummary);
                return "Provider=" + EmptyAsUnknown(failure.Provider) +
                    "; Model=" + EmptyAsUnknown(failure.Model) +
                    "; HTTP=" + failure.HttpCode +
                    "; Attempts=" + Math.Max(1, failure.Attempts) +
                    "; Stage=" + EmptyAsUnknown(failure.FailureStage) +
                    "; Items=" + Math.Max(0, failure.ItemCount) +
                    "; SourceChars=" + Math.Max(0L, failure.SourceCharacters) +
                    "; EstimatedInputTokens=" + Math.Max(0L, failure.EstimatedInputTokens) +
                    "; TimeoutSeconds=" + Math.Max(0, failure.TimeoutSeconds) +
                    "; RequestBodyBytes=" + Math.Max(0L, failure.RequestBodyBytes) +
                    "; UploadedBytes=" + Math.Max(0L, failure.UploadedBytes) +
                    "; UploadProgress=" + Math.Max(0f, failure.UploadProgress).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                    "; DownloadedBytes=" + Math.Max(0L, failure.DownloadedBytes) +
                    "; UnityResult=" + EmptyAsUnknown(failure.UnityResult) +
                    "; Error=" + EmptyAsUnknown(failure.ErrorText) +
                    "; Response=" + response;
            }

            private static string EmptyAsUnknown(string value)
            {
                return string.IsNullOrWhiteSpace(value) ? "n/a" : value;
            }
        }
    }
}
