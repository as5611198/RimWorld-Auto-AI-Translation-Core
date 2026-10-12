using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責 UnityWebRequest 的實際傳輸。
// EN: This file sends UnityWebRequest traffic for API calls.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器API 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorAPI.
    public static partial class AutoTranslatorAPI
    {
        internal sealed class UnityRequestTransferSnapshot
        {
            internal long RequestBodyBytes;
            internal long UploadedBytes;
            internal float UploadProgress;
            internal long DownloadedBytes;
            internal long HttpCode;
            internal string NativeError = string.Empty;
            internal string UnityResult = string.Empty;
        }

        private static void ApplyUnityTransferSnapshot(
            ATC_WebResponse response,
            UnityRequestTransferSnapshot snapshot)
        {
            if (response == null || snapshot == null) return;
            response.RequestBodyBytes = Math.Max(0L, snapshot.RequestBodyBytes);
            response.UploadedBytes = Math.Max(0L, snapshot.UploadedBytes);
            response.UploadProgress = Math.Max(0f, snapshot.UploadProgress);
            response.DownloadedBytes = Math.Max(0L, snapshot.DownloadedBytes);
            response.UnityResult = snapshot.UnityResult ?? string.Empty;
            if (response.HttpCode == 0L && snapshot.HttpCode > 0L)
                response.HttpCode = snapshot.HttpCode;
            if (string.IsNullOrWhiteSpace(response.ErrorText) &&
                !string.IsNullOrWhiteSpace(snapshot.NativeError))
                response.ErrorText = snapshot.NativeError;
            else if (!string.IsNullOrWhiteSpace(snapshot.NativeError) &&
                     (response.ErrorText ?? string.Empty).IndexOf(
                         snapshot.NativeError,
                         StringComparison.OrdinalIgnoreCase) < 0)
                response.ErrorText += " | UnityNativeError=" + snapshot.NativeError;
        }

        // 這個類別負責 ATCWebRequestEngine 的主要流程與狀態。
        // EN: This class manages the main workflow and state for ATC_WebRequestEngine.
        public class ATC_WebRequestEngine : MonoBehaviour
        {
            // 這個欄位保存 instance 的執行狀態或快取資料。
            // EN: This field stores instance runtime state or cached data.
            private static ATC_WebRequestEngine _instance;
            private static readonly object _instanceLock = new object();

            private readonly Dictionary<int, ActiveTranslationRequest> activeRequests = new Dictionary<int, ActiveTranslationRequest>();
            private readonly ConcurrentDictionary<int, UnityRequestTransferSnapshot> requestDiagnostics =
                new ConcurrentDictionary<int, UnityRequestTransferSnapshot>();
            // 這個欄位保存 nextRequestId 的執行狀態或快取資料。
            // EN: This field stores next request id runtime state or cached data.
            private int nextRequestId;

            public static ATC_WebRequestEngine Instance
            {
                get
                {
                    if (_instance == null)
                    {
                        lock (_instanceLock)
                        {
                            if (_instance == null)
                            {
                                GameObject go = new GameObject("ATC_WebRequestEngine_Unkillable");
                                UnityEngine.Object.DontDestroyOnLoad(go);
                                _instance = go.AddComponent<ATC_WebRequestEngine>();
                            }
                        }
                    }
                    return _instance;
                }
            }

            // 這個方法負責處理 FireRequest 相關流程。
            // EN: This method handles fire request.
            public int FireRequest(string url, string jsonBody, string apiKey, TranslatorProvider provider, int timeoutSeconds, TaskCompletionSource<ATC_WebResponse> tcs, TaskCompletionSource<bool> sendStarted)
            {
                if (tcs == null) return -1;

                int requestId = ++nextRequestId;
                ActiveTranslationRequest active = new ActiveTranslationRequest
                {
                    Id = requestId,
                    Completion = tcs,
                    SendStarted = sendStarted
                };

                activeRequests[requestId] = active;
                requestDiagnostics[requestId] = new UnityRequestTransferSnapshot
                {
                    RequestBodyBytes = Encoding.UTF8.GetByteCount(jsonBody ?? string.Empty),
                    UnityResult = "Created"
                };
                try
                {
                    active.Coroutine = StartCoroutine(ExecuteGuardedRequestCoroutine(
                        active, url, jsonBody, apiKey, provider, timeoutSeconds));
                    if (active.Coroutine == null && !active.IsCompleted)
                        FailRequestExecution(active);
                }
                catch (Exception error)
                {
                    FailRequestExecution(active, error);
                }
                return requestId;
            }

            // Unity does not propagate iterator exceptions to the caller of StartCoroutine.
            // Drive the iterator here so every failure completes both request signals.
            private IEnumerator ExecuteGuardedRequestCoroutine(
                ActiveTranslationRequest active, string url, string jsonBody,
                string apiKey, TranslatorProvider provider, int timeoutSeconds)
            {
                IEnumerator execution = ExecuteRequestCoroutine(
                    active, url, jsonBody, apiKey, provider, timeoutSeconds);
                try
                {
                    while (!active.IsCompleted)
                    {
                        bool moved = false;
                        object yielded = null;
                        Exception failure = null;
                        try
                        {
                            moved = execution.MoveNext();
                            if (moved) yielded = execution.Current;
                        }
                        catch (Exception error)
                        {
                            failure = error;
                        }
                        if (failure != null || (!moved && !active.IsCompleted))
                            FailRequestExecution(active, failure);
                        if (failure != null || !moved || active.IsCompleted) yield break;
                        yield return yielded;
                    }
                }
                finally
                {
                    try { (execution as IDisposable)?.Dispose(); } catch { }
                    if (!active.IsCompleted) FailRequestExecution(active);
                }
            }

            private void FailRequestExecution(ActiveTranslationRequest active, Exception error = null)
            {
                if (active == null || active.IsCompleted) return;
                // Native exception messages may contain the credential-bearing request URL.
                FinishRequest(active, new ATC_WebResponse
                {
                    IsSuccess = false,
                    HttpCode = 0,
                    ErrorText = (active.NetworkStarted
                        ? "Unity transport failed while processing the response."
                        : "Unity transport failed before the network request started.") +
                        " ExceptionType=" + (error?.GetType().Name ?? "UnexpectedCoroutineTermination"),
                    ResponseBody = string.Empty,
                    FailureKind = active.NetworkStarted
                        ? TranslationRequestFailureKind.Transport
                        : TranslationRequestFailureKind.LocalDispatch,
                    FailureStage = active.NetworkStarted ? "Transport" : "Dispatching"
                }, true, false);
            }

            internal bool TryGetRequestDiagnostics(int requestId, out UnityRequestTransferSnapshot snapshot)
            {
                return requestDiagnostics.TryGetValue(requestId, out snapshot);
            }

            // 這個方法負責中止 Request 流程。
            // EN: This method aborts request.
            public void AbortRequest(int requestId, string reason)
            {
                if (requestId <= 0) return;

                ActiveTranslationRequest active;
                if (!activeRequests.TryGetValue(requestId, out active)) return;

                FinishRequest(active, CreateCancelledResponse(reason), true);
            }

            // 這個方法負責中止 AllRequests 流程。
            // EN: This method aborts all requests.
            public void AbortAllRequests(string reason)
            {
                List<ActiveTranslationRequest> requests = activeRequests.Values.ToList();
                for (int i = 0; i < requests.Count; i++)
                {
                    FinishRequest(requests[i], CreateCancelledResponse(reason), true);
                }
            }

            // 這個方法負責執行 RequestCoroutine 動作。
            // EN: This method executes request coroutine.
            private IEnumerator ExecuteRequestCoroutine(ActiveTranslationRequest active, string url, string jsonBody, string apiKey, TranslatorProvider provider, int timeoutSeconds)
            {
                // FinishRequest owns disposal on every terminal path, including an external stop.
                UnityWebRequest webRequest = new UnityWebRequest(url, "POST");
                active.WebRequest = webRequest;

                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.useHttpContinue = false;
                webRequest.SetRequestHeader("Content-Type", "application/json");

                string trimmedApiKey = apiKey != null ? apiKey.Trim() : string.Empty;
                if (!string.IsNullOrEmpty(trimmedApiKey))
                {
                    if (provider == TranslatorProvider.DeepL)
                    {
                        webRequest.SetRequestHeader("Authorization", "DeepL-Auth-Key " + trimmedApiKey);
                    }
                    else if (provider != TranslatorProvider.Google)
                    {
                        webRequest.SetRequestHeader("Authorization", "Bearer " + trimmedApiKey);
                    }
                }

                webRequest.timeout = timeoutSeconds > 0 ? timeoutSeconds : 60;
                UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
                active.NetworkStarted = true;
                active.SendStarted?.TrySetResult(true);
                CaptureRequestDiagnostics(active, webRequest, "InProgress");
                while (!operation.isDone)
                {
                    if (active.IsCompleted) yield break;
                    CaptureRequestDiagnostics(active, webRequest, "InProgress");
                    if (AutoTranslatorSettings.IsCancellationRequested)
                    {
                        FinishRequest(active, CreateCancelledResponse("Pipeline cancellation requested"), true, false);
                        yield break;
                    }
                    yield return null;
                }

                if (active.IsCompleted) yield break;
                CaptureRequestDiagnostics(active, webRequest, webRequest.result.ToString());
                string safeText = string.Empty;
                if (webRequest.downloadHandler != null)
                {
                    byte[] rawData = webRequest.downloadHandler.data;
                    if (rawData != null && rawData.Length > 0)
                    {
                        try
                        {
                            Encoding tolerantUtf8 = new UTF8Encoding(false, false);
                            safeText = tolerantUtf8.GetString(rawData);
                        }
                        catch
                        {
                            safeText = webRequest.downloadHandler.text ?? string.Empty;
                        }
                    }
                }

                ATC_WebResponse response = new ATC_WebResponse
                {
                    HttpCode = webRequest.responseCode,
                    ErrorText = webRequest.error ?? string.Empty,
                    ResponseBody = safeText,
                    IsSuccess = UnityWebRequestCompat.IsSuccess(webRequest)
                };
                FinishRequest(active, response, false, false);
            }

            private void CaptureRequestDiagnostics(
                ActiveTranslationRequest active,
                UnityWebRequest webRequest,
                string unityResult)
            {
                if (active == null || webRequest == null) return;
                UnityRequestTransferSnapshot current;
                requestDiagnostics.TryGetValue(active.Id, out current);
                requestDiagnostics[active.Id] = new UnityRequestTransferSnapshot
                {
                    RequestBodyBytes = current != null ? current.RequestBodyBytes : 0L,
                    UploadedBytes = (long)webRequest.uploadedBytes,
                    UploadProgress = webRequest.uploadProgress,
                    DownloadedBytes = (long)webRequest.downloadedBytes,
                    HttpCode = webRequest.responseCode,
                    NativeError = webRequest.error ?? string.Empty,
                    UnityResult = unityResult ?? string.Empty
                };
            }

            // 這個方法負責處理 FinishRequest 相關流程。
            // EN: This method handles finish request.
            private void FinishRequest(ActiveTranslationRequest active, ATC_WebResponse response,
                bool abortWebRequest, bool stopCoroutine = true)
            {
                if (active == null || active.IsCompleted) return;
                UnityRequestTransferSnapshot finalSnapshot;
                if (requestDiagnostics.TryGetValue(active.Id, out finalSnapshot))
                    ApplyUnityTransferSnapshot(response, finalSnapshot);
                active.IsCompleted = true;

                if (activeRequests.ContainsKey(active.Id))
                {
                    activeRequests.Remove(active.Id);
                }

                UnityWebRequest webRequest = active.WebRequest;
                active.WebRequest = null;
                if (abortWebRequest && webRequest != null)
                {
                    try { webRequest.Abort(); } catch { }
                }

                if (stopCoroutine && active.Coroutine != null)
                {
                    try { StopCoroutine(active.Coroutine); } catch { }
                }
                try { webRequest?.Dispose(); } catch { }
                requestDiagnostics.TryRemove(active.Id, out finalSnapshot);
                // A false send signal must never wake the caller before the real failure exists.
                active.Completion.TrySetResult(response);
                active.SendStarted?.TrySetResult(active.NetworkStarted);
            }

            // 這個方法負責建立 Cancelled回應 物件或檔案。
            // EN: This method creates cancelled response.
            private static ATC_WebResponse CreateCancelledResponse(string reason)
            {
                return new ATC_WebResponse
                {
                    IsSuccess = false,
                    HttpCode = 0,
                    ErrorText = reason ?? "Cancelled",
                    ResponseBody = string.Empty,
                    FailureKind = TranslationRequestFailureKind.Cancelled,
                    FailureStage = "Cancelled"
                };
            }

            // 這個類別負責 Active翻譯Request 的主要流程與狀態。
            // EN: This class manages the main workflow and state for ActiveTranslationRequest.
            private class ActiveTranslationRequest
            {
                // 這個欄位保存 Id 的執行狀態或快取資料。
                // EN: This field stores id runtime state or cached data.
                public int Id;
                // 這個欄位保存 WebRequest 的執行狀態或快取資料。
                // EN: This field stores web request runtime state or cached data.
                public UnityWebRequest WebRequest;
                // 這個欄位保存 Coroutine 的執行狀態或快取資料。
                // EN: This field stores coroutine runtime state or cached data.
                public Coroutine Coroutine;
                // 這個欄位保存 Completion 的執行狀態或快取資料。
                // EN: This field stores completion runtime state or cached data.
                public TaskCompletionSource<ATC_WebResponse> Completion;
                public TaskCompletionSource<bool> SendStarted;
                // 這個欄位保存 IsCompleted 的執行狀態或快取資料。
                // EN: This field stores is completed runtime state or cached data.
                public bool IsCompleted;
                public bool NetworkStarted;
            }
        }

        // 這個方法負責中止 Active翻譯Requests 流程。
        // EN: This method aborts active translation requests.
        public static void AbortActiveTranslationRequests(string reason)
        {
            if (UnityData.IsInMainThread)
            {
                ATC_WebRequestEngine.Instance.AbortAllRequests(reason);
                return;
            }

            ATC_Dispatcher.RunOnMainThread(() =>
            {
                ATC_WebRequestEngine.Instance.AbortAllRequests(reason);
            });
        }

        // 這個方法負責中止 翻譯Request 流程。
        // EN: This method aborts translation request.
        private static void AbortTranslationRequest(int requestId, string reason)
        {
            AbortTranslationRequestAsync(requestId, reason);
        }

        private static Task AbortTranslationRequestAsync(int requestId, string reason)
        {
            if (requestId <= 0) return Task.CompletedTask;

            if (UnityData.IsInMainThread)
            {
                ATC_WebRequestEngine.Instance.AbortRequest(requestId, reason);
                return Task.CompletedTask;
            }

            TaskCompletionSource<bool> cleanupCompleted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ATC_Dispatcher.RunOnMainThread(() =>
            {
                try
                {
                    ATC_WebRequestEngine.Instance.AbortRequest(requestId, reason);
                }
                finally
                {
                    cleanupCompleted.TrySetResult(true);
                }
            });
            return cleanupCompleted.Task;
        }
    }
}
