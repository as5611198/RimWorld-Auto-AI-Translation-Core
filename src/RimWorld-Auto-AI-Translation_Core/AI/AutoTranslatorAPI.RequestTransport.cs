using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorAPI
    {
        private const int UnityTransportCleanupGraceSeconds = 15;

        private static async Task<ATC_WebResponse> SendJsonRequestAttemptAsync(
            string url,
            string jsonPayload,
            string apiKey,
            TranslatorProvider provider,
            int timeoutSeconds,
            Func<bool> additionalCancellation = null)
        {
            int safeTimeoutSeconds = Math.Max(1, timeoutSeconds);
            TranslationUsageRequestContext requestContext =
                TranslationUsageCoordinator.GetCurrentRequestContext();
            long estimatedInputTokens = TranslationUsageCoordinator.EstimateInputTokens(jsonPayload);
            if (IsRequestCancellationRequested(additionalCancellation))
            {
                AutoTranslatorSettings.AddDebugLog(
                    "Request cancelled before budget reservation. Provider=" + provider + ".");
                return CreateRequestTimeoutResponse(provider, 0);
            }

            if (!TranslationUsageCoordinator.TryReserve(
                    provider,
                    jsonPayload,
                    out TranslationUsageReservationHandle usageReservation,
                    out string budgetDenialReason))
            {
                AutoTranslatorSettings.AddDebugLog(
                    "Request denied by local usage budget. Provider=" + provider +
                    ", Items=" + (requestContext != null ? requestContext.ItemCount : 0) + ".");
                return new ATC_WebResponse
                {
                    IsSuccess = false,
                    HttpCode = 0,
                    ErrorText = "Translation budget paused: " + budgetDenialReason,
                    ResponseBody = string.Empty,
                    BudgetDenied = true,
                    BudgetDenialReason = budgetDenialReason,
                    FailureKind = TranslationRequestFailureKind.BudgetDenied,
                    FailureStage = "LocalUsageBudget"
                };
            }

            TaskCompletionSource<ATC_WebResponse> completion =
                new TaskCompletionSource<ATC_WebResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> dispatchStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> networkSendStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TranslationRequestActivity.RequestLease activity = TranslationRequestActivity.CreateRequest();
            int requestId = -1;
            bool networkRequestStarted = false;
            Stopwatch requestTimer = null;
            bool perfRequestStarted = false;
            ATC_WebResponse response = null;
            IDisposable concurrencySlot = null;

            try
            {
                activity.MarkQueued();
                int maximumConcurrency = Math.Max(
                    1,
                    AutoTranslatorMod.Settings != null
                        ? AutoTranslatorMod.Settings.MaxThreads
                        : 1);
                maximumConcurrency = GetEffectiveTranslationConcurrency(maximumConcurrency);
                AutoTranslatorSettings.AddDebugLog(
                    "Request queued. Provider=" + provider +
                    ", PayloadBytes=" + System.Text.Encoding.UTF8.GetByteCount(jsonPayload ?? string.Empty) +
                    ", Items=" + (requestContext != null ? requestContext.ItemCount : 0) +
                    ", Concurrency=" + maximumConcurrency + ".");
                concurrencySlot = await TranslationRequestConcurrencyGate.AcquireAsync(
                    maximumConcurrency,
                    () => IsRequestCancellationRequested(additionalCancellation));
                if (concurrencySlot == null)
                {
                    activity.MarkCancelled();
                    response = CreateRequestTimeoutResponse(provider, 0);
                    return response;
                }
                activity.MarkDispatching();
                AutoTranslatorSettings.AddDebugLog(
                    "Request acquired concurrency slot. Provider=" + provider + ".");
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    if (completion.Task.IsCompleted)
                    {
                        dispatchStarted.TrySetResult(false);
                        return;
                    }

                    if (IsRequestCancellationRequested(additionalCancellation))
                    {
                        dispatchStarted.TrySetResult(false);
                        completion.TrySetResult(CreateRequestTimeoutResponse(provider, 0));
                        return;
                    }

                    try
                    {
                        activity.MarkDispatching();
                        requestId = ATC_WebRequestEngine.Instance.FireRequest(
                            url,
                            jsonPayload,
                            apiKey,
                            provider,
                            safeTimeoutSeconds,
                            completion,
                            networkSendStarted);
                        AutoTranslatorSettings.AddDebugLog(
                            "Request dispatched to Unity transport. Provider=" + provider +
                            ", RequestId=" + requestId + ", TimeoutSeconds=" + safeTimeoutSeconds + ".");
                        if (requestId > 0 &&
                            (completion.Task.IsCompleted || IsRequestCancellationRequested(additionalCancellation)))
                        {
                            dispatchStarted.TrySetResult(false);
                            AbortTranslationRequest(requestId, "Request completed or cancelled during dispatch");
                            return;
                        }
                        dispatchStarted.TrySetResult(requestId > 0);
                    }
                    catch (Exception ex)
                    {
                        dispatchStarted.TrySetResult(false);
                        completion.TrySetResult(new ATC_WebResponse
                        {
                            IsSuccess = false,
                            HttpCode = 0,
                            ErrorText = ex.Message,
                            ResponseBody = string.Empty,
                            FailureKind = TranslationRequestFailureKind.LocalDispatch,
                            FailureStage = "Dispatching"
                        });
                    }
                });

                if (!await TranslationRequestSignalWaiter.WaitAsync(
                        dispatchStarted.Task,
                        () => IsRequestCancellationRequested(additionalCancellation)))
                {
                    activity.MarkCancelled();
                    response = CreateRequestTimeoutResponse(provider, 0);
                    completion.TrySetResult(response);
                    if (requestId > 0)
                        await AbortTranslationRequestAsync(
                            requestId,
                            "Request cancelled while dispatch signal was completing");
                    return response;
                }

                if (!await dispatchStarted.Task)
                {
                    response = completion.Task.IsCompleted
                        ? await completion.Task
                        : CreateRequestDispatchFailureResponse(provider);
                    return response;
                }

                if (!await TranslationRequestSignalWaiter.WaitAsync(
                        networkSendStarted.Task,
                        () => IsRequestCancellationRequested(additionalCancellation)))
                {
                    activity.MarkCancelled();
                    response = CreateRequestTimeoutResponse(provider, 0);
                    completion.TrySetResult(response);
                    await AbortTranslationRequestAsync(requestId, "Request cancelled before network send");
                    return response;
                }

                if (!await networkSendStarted.Task)
                {
                    activity.MarkFailed();
                    response = completion.Task.IsCompleted
                        ? await completion.Task
                        : CreateRequestDispatchFailureResponse(provider);
                    return response;
                }

                networkRequestStarted = true;
                activity.MarkSent();
                AutoTranslatorSettings.AddDebugLog(
                    "Request started on network transport. Provider=" + provider +
                    ", RequestId=" + requestId + ".");
                requestTimer = Stopwatch.StartNew();
                perfRequestStarted = true;
                AutoTranslatorPerf.BeginApiRequest();
                activity.MarkWaitingResponse();
                int hardTimeoutSeconds = safeTimeoutSeconds + UnityTransportCleanupGraceSeconds;
                Stopwatch hardTimer = Stopwatch.StartNew();
                while (!completion.Task.IsCompleted)
                {
                    if (IsRequestCancellationRequested(additionalCancellation))
                    {
                        response = CreateRequestTimeoutResponse(provider, 0);
                        activity.MarkCancelled();
                        completion.TrySetResult(response);
                        await AbortTranslationRequestAsync(requestId, "Request cancelled");
                        return response;
                    }

                    if (hardTimer.ElapsedMilliseconds >= hardTimeoutSeconds * 1000L)
                    {
                        response = CreateUnityTransportStallResponse(
                            provider,
                            safeTimeoutSeconds,
                            UnityTransportCleanupGraceSeconds);
                        UnityRequestTransferSnapshot transferSnapshot;
                        if (requestId > 0 &&
                            ATC_WebRequestEngine.Instance.TryGetRequestDiagnostics(
                                requestId,
                                out transferSnapshot))
                        {
                            ApplyUnityTransferSnapshot(response, transferSnapshot);
                        }
                        activity.MarkFailed();
                        completion.TrySetResult(response);
                        await AbortTranslationRequestAsync(
                            requestId,
                            "UnityWebRequest did not finish during the post-timeout cleanup grace period");
                        return response;
                    }

                    await Task.Delay(100);
                }

                        response = await completion.Task;
                PopulateRequestDiagnostics(
                    response,
                    requestContext,
                    estimatedInputTokens,
                    safeTimeoutSeconds);
                if (response != null && !response.IsSuccess && response.FailureKind == TranslationRequestFailureKind.None)
                {
                    if (response.HttpCode >= 400) response.FailureKind = TranslationRequestFailureKind.Http;
                    else if ((response.ErrorText ?? string.Empty).IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                        response.FailureKind = TranslationRequestFailureKind.ResponseTimeout;
                    else response.FailureKind = TranslationRequestFailureKind.Transport;
                    response.FailureStage = response.FailureKind == TranslationRequestFailureKind.ResponseTimeout
                        ? "WaitingResponse"
                        : "Transport";
                }
                if (response != null && response.IsSuccess) activity.MarkCompleted();
                else if (IsRequestCancellationRequested(additionalCancellation)) activity.MarkCancelled();
                else activity.MarkFailed();
                AutoTranslatorSettings.AddDebugLog(
                    "Request completed. Provider=" + provider +
                    ", RequestId=" + requestId +
                    ", Success=" + (response != null && response.IsSuccess) +
                    ", Http=" + (response != null ? response.HttpCode : 0L) +
                    ", FailureKind=" + (response != null ? response.FailureKind.ToString() : "NoResponse") +
                    ", Stage=" + (response != null ? response.FailureStage : "Unknown") +
                    ", Upload=" + (response != null ? response.UploadedBytes : 0L) +
                    "/" + (response != null ? response.RequestBodyBytes : 0L) +
                    ", Download=" + (response != null ? response.DownloadedBytes : 0L) +
                    ", UnityResult=" + (response != null ? response.UnityResult : "Unknown") + ".");
                return response;
            }
            finally
            {
                PopulateRequestDiagnostics(
                    response,
                    requestContext,
                    estimatedInputTokens,
                    safeTimeoutSeconds);
                activity.Dispose();
                concurrencySlot?.Dispose();
                TranslationUsageCoordinator.Reconcile(
                    usageReservation,
                    networkRequestStarted,
                    response != null ? response.HttpCode : 0L,
                    response != null && response.IsSuccess,
                    response != null ? response.ResponseBody : string.Empty);
                if (perfRequestStarted && requestTimer != null)
                {
                    requestTimer.Stop();
                    AutoTranslatorPerf.EndApiRequest(
                        requestTimer.ElapsedMilliseconds,
                        response != null && response.IsSuccess);
                }
                if (requestTimer != null)
                    AutoTranslatorSettings.AddDebugLog(
                        "Request finalized. Provider=" + provider +
                        ", RequestId=" + requestId +
                        ", ElapsedMs=" + requestTimer.ElapsedMilliseconds + ".");
            }
        }

        private static void PopulateRequestDiagnostics(
            ATC_WebResponse response,
            TranslationUsageRequestContext context,
            long estimatedInputTokens,
            int timeoutSeconds)
        {
            if (response == null) return;
            response.ItemCount = context != null ? context.ItemCount : 0;
            response.SourceCharacters = context != null ? context.SourceCharacters : 0L;
            response.EstimatedInputTokens = Math.Max(0L, estimatedInputTokens);
            if (response.TimeoutSeconds <= 0 &&
                response.FailureKind == TranslationRequestFailureKind.ResponseTimeout)
                response.TimeoutSeconds = Math.Max(0, timeoutSeconds);
        }

        private static bool IsRequestCancellationRequested(Func<bool> additionalCancellation)
        {
            if (AutoTranslatorSettings.IsCancellationRequested) return true;
            if (additionalCancellation == null) return false;

            try
            {
                return additionalCancellation();
            }
            catch
            {
                return true;
            }
        }
    }
}
