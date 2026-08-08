using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorAPI
    {
        private static async Task<ATC_WebResponse> SendJsonRequestAttemptAsync(
            string url,
            string jsonPayload,
            string apiKey,
            TranslatorProvider provider,
            int timeoutSeconds,
            Func<bool> additionalCancellation = null)
        {
            int safeTimeoutSeconds = Math.Max(1, timeoutSeconds);
            if (IsRequestCancellationRequested(additionalCancellation))
                return CreateRequestTimeoutResponse(provider, 0);

            TaskCompletionSource<ATC_WebResponse> completion =
                new TaskCompletionSource<ATC_WebResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> dispatchStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int requestId = -1;
            Stopwatch requestTimer = null;
            bool perfRequestStarted = false;
            ATC_WebResponse response = null;

            try
            {
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
                        requestId = ATC_WebRequestEngine.Instance.FireRequest(
                            url,
                            jsonPayload,
                            apiKey,
                            provider,
                            safeTimeoutSeconds,
                            completion);
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
                            ResponseBody = string.Empty
                        });
                    }
                });

                if (!await WaitForRequestDispatchAsync(
                        dispatchStarted.Task,
                        additionalCancellation,
                        Math.Max(300000L, safeTimeoutSeconds * 2000L)))
                {
                    if (IsRequestCancellationRequested(additionalCancellation))
                    {
                        response = CreateRequestTimeoutResponse(provider, 0);
                        completion.TrySetResult(response);
                        return response;
                    }

                    dispatchStarted.TrySetResult(false);
                    response = CreateRequestDispatchTimeoutResponse(provider);
                    completion.TrySetResult(response);
                    AbortTranslationRequest(requestId, "Request dispatch timed out");
                    return response;
                }

                if (!await dispatchStarted.Task)
                {
                    if (completion.Task.IsCompleted) return await completion.Task;
                    return CreateRequestDispatchTimeoutResponse(provider);
                }

                requestTimer = Stopwatch.StartNew();
                perfRequestStarted = true;
                AutoTranslatorPerf.BeginApiRequest();
                int hardTimeoutSeconds = Math.Max(safeTimeoutSeconds + 10, 30);
                Stopwatch hardTimer = Stopwatch.StartNew();
                while (!completion.Task.IsCompleted)
                {
                    if (IsRequestCancellationRequested(additionalCancellation))
                    {
                        response = CreateRequestTimeoutResponse(provider, 0);
                        completion.TrySetResult(response);
                        AbortTranslationRequest(requestId, "Request cancelled");
                        return response;
                    }

                    if (hardTimer.ElapsedMilliseconds >= hardTimeoutSeconds * 1000L)
                    {
                        response = CreateRequestTimeoutResponse(provider, hardTimeoutSeconds);
                        completion.TrySetResult(response);
                        AbortTranslationRequest(requestId, "Request timed out");
                        return response;
                    }

                    await Task.Delay(100);
                }

                response = await completion.Task;
                return response;
            }
            finally
            {
                if (perfRequestStarted && requestTimer != null)
                {
                    requestTimer.Stop();
                    AutoTranslatorPerf.EndApiRequest(
                        requestTimer.ElapsedMilliseconds,
                        response != null && response.IsSuccess);
                }
            }
        }

        private static async Task<bool> WaitForRequestDispatchAsync(
            Task dispatchStarted,
            Func<bool> additionalCancellation,
            long absoluteTimeoutMilliseconds)
        {
            if (dispatchStarted == null) return false;

            Stopwatch timer = Stopwatch.StartNew();
            long previousElapsed = 0L;
            long responsivePumpWait = 0L;

            while (!dispatchStarted.IsCompleted)
            {
                if (IsRequestCancellationRequested(additionalCancellation)) return false;

                await Task.Delay(100);

                // A healthy pump should consume this request quickly. When RimWorld is
                // temporarily blocking the main thread, pause the dispatch timeout budget
                // instead of misreporting a local stall as an HTTP 0 provider failure.
                long elapsed = timer.ElapsedMilliseconds;
                if (elapsed >= Math.Max(TranslationDispatchTimeoutMs, absoluteTimeoutMilliseconds))
                    return false;

                long interval = Math.Max(0L, elapsed - previousElapsed);
                previousElapsed = elapsed;
                if (ATC_Dispatcher.HasRecentPump(2000)) responsivePumpWait += interval;
                if (responsivePumpWait >= TranslationDispatchTimeoutMs)
                    return false;
            }

            return true;
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
