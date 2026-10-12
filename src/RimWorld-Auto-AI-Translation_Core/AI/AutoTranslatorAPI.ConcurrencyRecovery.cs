using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorAPI
    {
        private const int ConcurrencyRecoveryRetryCount = ConcurrencyRecoveryPolicy.MaximumRetries;
        private static readonly SemaphoreSlim ConcurrencyRecoveryGate = new SemaphoreSlim(1, 1);
        private static readonly AsyncLocal<TranslationFailureContext> LastTranslationFailure =
            new AsyncLocal<TranslationFailureContext>();
        private static readonly ConcurrentDictionary<string, TranslationRequestFailureInfo> TranslationFailuresByScope =
            new ConcurrentDictionary<string, TranslationRequestFailureInfo>(StringComparer.Ordinal);
        private static long _degradedConcurrencyUntilUtcTicks;
        private static long _concurrencyRecoveryCooldownStartedUtcTicks;
        private static int _concurrencyRecoveryNotificationPending;
        private static readonly TimeSpan ConcurrencyRecoveryCooldownStage = TimeSpan.FromSeconds(20);

        internal sealed class TranslationRequestFailureInfo
        {
            internal TranslatorProvider Provider;
            internal string Model = string.Empty;
            internal long HttpCode;
            internal string ErrorText = string.Empty;
            internal string ResponseSummary = string.Empty;
            internal bool IsConcurrencyLimit;
            internal bool IsQuotaExhausted;
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
        }

        private sealed class TranslationFailureContext
        {
            internal TranslationRequestFailureInfo Failure;
        }

        private static void ClearLastTranslationFailure()
        {
            LastTranslationFailure.Value = new TranslationFailureContext();
        }

        internal static string DescribeLastTranslationFailure(string contextInfo, string requestScope = null)
        {
            return DescribeLastTranslationFailure(
                contextInfo,
                requestScope,
                out _,
                out _);
        }

        internal static string DescribeLastTranslationFailure(
            string contextInfo,
            string requestScope,
            out string aggregationKey,
            out int affectedItems)
        {
            aggregationKey = "translation_failure|unknown";
            affectedItems = 0;
            TranslationRequestFailureInfo failure = null;
            if (!string.IsNullOrWhiteSpace(requestScope))
                TranslationFailuresByScope.TryRemove(requestScope, out failure);
            failure = failure ?? LastTranslationFailure.Value?.Failure;
            if (failure == null)
                return TranslateText("ATC_LogError_ApiFailureUnknown", contextInfo ?? string.Empty);

            affectedItems = Math.Max(0, failure.ItemCount);
            aggregationKey = string.Join(
                "|",
                "translation_failure",
                failure.FailureKind.ToString(),
                failure.Provider.ToString(),
                EmptyAsUnknown(failure.Model),
                failure.HttpCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EmptyAsUnknown(failure.FailureStage));

            var diagnostic = new TranslationFailureDiagnosticData
            {
                Provider = failure.Provider.ToString(),
                Model = failure.Model,
                HttpCode = failure.HttpCode,
                Attempts = failure.Attempts,
                FailureKind = failure.FailureKind,
                FailureStage = failure.FailureStage,
                ItemCount = failure.ItemCount,
                SourceCharacters = failure.SourceCharacters,
                EstimatedInputTokens = failure.EstimatedInputTokens,
                TimeoutSeconds = failure.TimeoutSeconds,
                RequestBodyBytes = failure.RequestBodyBytes,
                UploadedBytes = failure.UploadedBytes,
                UploadProgress = failure.UploadProgress,
                DownloadedBytes = failure.DownloadedBytes,
                UnityResult = failure.UnityResult,
                ErrorText = failure.ErrorText,
                ResponseSummary = failure.ResponseSummary,
                IsConcurrencyLimit = failure.IsConcurrencyLimit,
                IsQuotaExhausted = failure.IsQuotaExhausted
            };
            string category = failure.IsConcurrencyLimit &&
                              failure.Attempts >= ConcurrencyRecoveryRetryCount + 1
                ? TranslateText("ATC_LogError_ApiConcurrencyExhausted", ConcurrencyRecoveryRetryCount)
                : TranslateText(TranslationFailureDiagnosticPolicy.GetCategoryKey(diagnostic));
            string developerDetail = TranslationFailureDiagnosticPolicy.BuildDeveloperDetail(
                diagnostic,
                TranslateText("ATC_ApiResponse_None"));
            return TranslateText(
                "ATC_LogError_ApiFailureDetailed",
                contextInfo ?? string.Empty,
                category,
                developerDetail);
        }

        private static void PublishLastTranslationFailure(string requestScope)
        {
            TranslationRequestFailureInfo failure = LastTranslationFailure.Value?.Failure;
            if (failure == null || string.IsNullOrWhiteSpace(requestScope)) return;
            if (TranslationFailuresByScope.Count >= 256) TranslationFailuresByScope.Clear();
            TranslationFailuresByScope[requestScope] = failure;
        }

        private static async Task<ATC_WebResponse> SendTranslationRequestWithConcurrencyRecoveryAsync(
            Func<Task<ATC_WebResponse>> sendAttempt,
            ApiKeyConfig config,
            Func<bool> additionalCancellation = null)
        {
            bool ownsRecoveryGate = false;
            try
            {
                if (IsConcurrencyRecoveryActive())
                {
                    if (!await WaitForConcurrencyRecoveryGateAsync(additionalCancellation)) return null;
                    ownsRecoveryGate = true;
                    if (!IsConcurrencyRecoveryActive())
                    {
                        ConcurrencyRecoveryGate.Release();
                        ownsRecoveryGate = false;
                    }
                }

                ATC_WebResponse response = null;
                for (int attempt = 0; attempt <= ConcurrencyRecoveryRetryCount; attempt++)
                {
                    if (IsRequestCancellationRequested(additionalCancellation)) return null;
                    if (attempt > 0)
                    {
                        int delay = ConcurrencyRecoveryPolicy.GetDelayMilliseconds(attempt);
                        AutoTranslatorSettings.AddLog(TranslateText(
                            "ATC_Log_ApiConcurrencyRetry",
                            attempt,
                            ConcurrencyRecoveryRetryCount,
                            delay / 1000));
                        if (!await DelayWithPipelineCancellationAsync(delay, additionalCancellation)) return null;
                    }

                    response = await sendAttempt();
                    if (response == null || response.IsSuccess || response.BudgetDenied) break;

                    bool concurrencyLimit = IsConcurrencyLimit(response);
                    if (!concurrencyLimit) break;

                    RecordLastTranslationFailure(config, response, true, false, attempt + 1);
                    ActivateConcurrencyRecovery();
                    if (!ownsRecoveryGate)
                    {
                        if (!await WaitForConcurrencyRecoveryGateAsync(additionalCancellation)) return null;
                        ownsRecoveryGate = true;
                    }

                    if (attempt == ConcurrencyRecoveryRetryCount)
                    {
                        break;
                    }
                }

                if (response != null && response.IsSuccess)
                {
                    if (ownsRecoveryGate)
                    {
                        if (BeginConcurrencyRecoveryCooldown())
                            AutoTranslatorSettings.AddLog(TranslateText("ATC_Log_ApiConcurrencyRecovered"));
                    }
                    return response;
                }

                if (response != null)
                {
                    bool quota = IsQuotaExhaustion(response);
                    RecordLastTranslationFailure(
                        config,
                        response,
                        IsConcurrencyLimit(response),
                        quota,
                        IsConcurrencyLimit(response) ? ConcurrencyRecoveryRetryCount + 1 : 1);
                }
                return response;
            }
            finally
            {
                if (ownsRecoveryGate) ConcurrencyRecoveryGate.Release();
            }
        }

        private static async Task<bool> WaitForConcurrencyRecoveryGateAsync(
            Func<bool> additionalCancellation = null)
        {
            using (TranslationRequestActivity.BeginRetryWait())
            {
                while (!IsRequestCancellationRequested(additionalCancellation))
                {
                    if (await ConcurrencyRecoveryGate.WaitAsync(100)) return true;
                }

                return false;
            }
        }

        private static void ActivateConcurrencyRecovery()
        {
            Interlocked.Exchange(ref _degradedConcurrencyUntilUtcTicks, DateTime.UtcNow.AddMinutes(2).Ticks);
            Interlocked.Exchange(ref _concurrencyRecoveryCooldownStartedUtcTicks, 0L);
            Interlocked.Exchange(ref _concurrencyRecoveryNotificationPending, 1);
        }

        private static bool BeginConcurrencyRecoveryCooldown()
        {
            Interlocked.Exchange(ref _degradedConcurrencyUntilUtcTicks, 0L);
            Interlocked.Exchange(ref _concurrencyRecoveryCooldownStartedUtcTicks, DateTime.UtcNow.Ticks);
            return Interlocked.Exchange(ref _concurrencyRecoveryNotificationPending, 0) != 0;
        }

        private static int GetEffectiveTranslationConcurrency(int configuredMaximum)
        {
            int safeConfiguredMaximum = Math.Max(1, configuredMaximum);
            long nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks < Interlocked.Read(ref _degradedConcurrencyUntilUtcTicks)) return 1;

            long cooldownStarted = Interlocked.Read(ref _concurrencyRecoveryCooldownStartedUtcTicks);
            if (cooldownStarted <= 0L) return safeConfiguredMaximum;

            long elapsedTicks = Math.Max(0L, nowTicks - cooldownStarted);
            long stageTicks = ConcurrencyRecoveryCooldownStage.Ticks;
            if (elapsedTicks < stageTicks) return 1;
            if (elapsedTicks < stageTicks * 2L)
                return Math.Max(1, (safeConfiguredMaximum + 1) / 2);
            if (elapsedTicks < stageTicks * 3L)
                return Math.Max(1, (safeConfiguredMaximum * 3) / 4);

            Interlocked.CompareExchange(ref _concurrencyRecoveryCooldownStartedUtcTicks, 0L, cooldownStarted);
            return safeConfiguredMaximum;
        }

        private static bool IsConcurrencyRecoveryActive()
        {
            return DateTime.UtcNow.Ticks < Interlocked.Read(ref _degradedConcurrencyUntilUtcTicks);
        }

        private static bool IsQuotaExhaustion(ATC_WebResponse response)
        {
            return ConcurrencyRecoveryPolicy.IsQuotaExhaustion(
                response?.ErrorText,
                response?.ResponseBody);
        }

        private static bool IsConcurrencyLimit(ATC_WebResponse response)
        {
            return ConcurrencyRecoveryPolicy.IsConcurrencyLimit(
                response != null ? response.HttpCode : 0L,
                response?.ErrorText,
                response?.ResponseBody);
        }

        private static void ReportConcurrencyRecoveryExhausted(
            ApiKeyConfig config,
            ATC_WebResponse response,
            string context,
            int affectedItems)
        {
            if (!IsConcurrencyLimit(response)) return;

            var diagnostic = new TranslationFailureDiagnosticData
            {
                Provider = config != null ? config.Provider.ToString() : string.Empty,
                Model = config != null ? CleanInput(config.SelectedModel) : string.Empty,
                HttpCode = response != null ? response.HttpCode : 0L,
                Attempts = ConcurrencyRecoveryRetryCount + 1,
                FailureKind = TranslationRequestFailureKind.ConcurrencyLimit,
                FailureStage = response != null ? response.FailureStage : string.Empty,
                ItemCount = response != null ? response.ItemCount : 0,
                SourceCharacters = response != null ? response.SourceCharacters : 0L,
                EstimatedInputTokens = response != null ? response.EstimatedInputTokens : 0L,
                TimeoutSeconds = response != null ? response.TimeoutSeconds : 0,
                RequestBodyBytes = response != null ? response.RequestBodyBytes : 0L,
                UploadedBytes = response != null ? response.UploadedBytes : 0L,
                UploadProgress = response != null ? response.UploadProgress : 0f,
                DownloadedBytes = response != null ? response.DownloadedBytes : 0L,
                UnityResult = response != null ? response.UnityResult : string.Empty,
                ErrorText = response != null ? TruncateDiagnostic(response.ErrorText, 240) : string.Empty,
                ResponseSummary = response != null ? TruncateDiagnostic(response.ResponseBody, 320) : string.Empty,
                IsConcurrencyLimit = true
            };
            string userMessage = TranslateText(
                "ATC_LogError_ApiConcurrencyExhausted",
                ConcurrencyRecoveryRetryCount);
            string developerDetail = (context ?? string.Empty) + "; " +
                TranslationFailureDiagnosticPolicy.BuildDeveloperDetail(
                    diagnostic,
                    TranslateText("ATC_ApiResponse_None"));
            string rootCauseKey = string.Join(
                "|",
                "api_concurrency_exhausted",
                diagnostic.Provider,
                EmptyAsUnknown(diagnostic.Model));
            AutoTranslatorSettings.AddAggregatedErrorLog(
                rootCauseKey,
                userMessage,
                developerDetail,
                Math.Max(affectedItems, diagnostic.ItemCount));
        }

        private static void ReportApiRequestFailure(
            ApiKeyConfig config,
            ATC_WebResponse response,
            string context,
            int affectedItems,
            TranslationRequestFailureKind? failureKindOverride = null,
            string failureStageOverride = null,
            string errorTextOverride = null)
        {
            if (response == null || response.BudgetDenied) return;

            TranslationRequestFailureKind failureKind = failureKindOverride ?? response.FailureKind;
            if (!failureKindOverride.HasValue)
            {
                if (IsQuotaExhaustion(response)) failureKind = TranslationRequestFailureKind.QuotaExhausted;
                else if (IsConcurrencyLimit(response)) failureKind = TranslationRequestFailureKind.ConcurrencyLimit;
                else if (failureKind == TranslationRequestFailureKind.None)
                    failureKind = response.HttpCode > 0L
                        ? TranslationRequestFailureKind.Http
                        : TranslationRequestFailureKind.Transport;
            }
            if (failureKind == TranslationRequestFailureKind.Cancelled ||
                failureKind == TranslationRequestFailureKind.BudgetDenied)
                return;

            var diagnostic = new TranslationFailureDiagnosticData
            {
                Provider = config != null ? config.Provider.ToString() : string.Empty,
                Model = config != null ? CleanInput(config.SelectedModel) : string.Empty,
                HttpCode = response.HttpCode,
                Attempts = 1,
                FailureKind = failureKind,
                FailureStage = string.IsNullOrWhiteSpace(failureStageOverride)
                    ? response.FailureStage
                    : failureStageOverride,
                ItemCount = response.ItemCount,
                SourceCharacters = response.SourceCharacters,
                EstimatedInputTokens = response.EstimatedInputTokens,
                TimeoutSeconds = response.TimeoutSeconds,
                RequestBodyBytes = response.RequestBodyBytes,
                UploadedBytes = response.UploadedBytes,
                UploadProgress = response.UploadProgress,
                DownloadedBytes = response.DownloadedBytes,
                UnityResult = response.UnityResult,
                ErrorText = TruncateDiagnostic(
                    string.IsNullOrWhiteSpace(errorTextOverride) ? response.ErrorText : errorTextOverride,
                    240),
                ResponseSummary = TruncateDiagnostic(response.ResponseBody, 320),
                IsConcurrencyLimit = failureKind == TranslationRequestFailureKind.ConcurrencyLimit,
                IsQuotaExhausted = failureKind == TranslationRequestFailureKind.QuotaExhausted
            };
            string category = TranslateText(TranslationFailureDiagnosticPolicy.GetCategoryKey(diagnostic));
            string developerDetail = TranslationFailureDiagnosticPolicy.BuildDeveloperDetail(
                diagnostic,
                TranslateText("ATC_ApiResponse_None"));
            string rootCauseKey = string.Join(
                "|",
                "api_request_failure",
                failureKind.ToString(),
                diagnostic.Provider,
                EmptyAsUnknown(diagnostic.Model),
                diagnostic.HttpCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EmptyAsUnknown(diagnostic.FailureStage));
            string userMessage = TranslateText(
                "ATC_LogError_ApiFailureDetailed",
                context ?? string.Empty,
                category,
                developerDetail);
            AutoTranslatorSettings.AddAggregatedErrorLog(
                rootCauseKey,
                userMessage,
                developerDetail,
                Math.Max(affectedItems, diagnostic.ItemCount));
        }

        private static void RecordLastTranslationFailure(
            ApiKeyConfig config,
            ATC_WebResponse response,
            bool concurrencyLimit,
            bool quotaExhausted,
            int attempts)
        {
            TranslationFailureContext context = LastTranslationFailure.Value;
            if (context == null)
            {
                context = new TranslationFailureContext();
                LastTranslationFailure.Value = context;
            }
            context.Failure = new TranslationRequestFailureInfo
            {
                Provider = config != null ? config.Provider : TranslatorProvider.Custom_OpenAI,
                Model = config != null ? CleanInput(config.SelectedModel) : string.Empty,
                HttpCode = response != null ? response.HttpCode : 0,
                ErrorText = TruncateDiagnostic(response?.ErrorText, 240),
                ResponseSummary = TruncateDiagnostic(response?.ResponseBody, 320),
                IsConcurrencyLimit = concurrencyLimit,
                IsQuotaExhausted = quotaExhausted,
                Attempts = attempts,
                FailureKind = quotaExhausted
                    ? TranslationRequestFailureKind.QuotaExhausted
                    : concurrencyLimit
                        ? TranslationRequestFailureKind.ConcurrencyLimit
                        : response != null ? response.FailureKind : TranslationRequestFailureKind.Transport,
                FailureStage = response != null ? response.FailureStage : string.Empty,
                ItemCount = response != null ? response.ItemCount : 0,
                SourceCharacters = response != null ? response.SourceCharacters : 0L,
                EstimatedInputTokens = response != null ? response.EstimatedInputTokens : 0L,
                TimeoutSeconds = response != null ? response.TimeoutSeconds : 0,
                RequestBodyBytes = response != null ? response.RequestBodyBytes : 0L,
                UploadedBytes = response != null ? response.UploadedBytes : 0L,
                UploadProgress = response != null ? response.UploadProgress : 0f,
                DownloadedBytes = response != null ? response.DownloadedBytes : 0L,
                UnityResult = response != null ? response.UnityResult : string.Empty
            };
        }

        private static string TruncateDiagnostic(string value, int maximumLength)
        {
            string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= maximumLength
                ? normalized
                : normalized.Substring(0, maximumLength) + "...";
        }

        private static string EmptyAsUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "n/a" : value;
        }
    }
}
