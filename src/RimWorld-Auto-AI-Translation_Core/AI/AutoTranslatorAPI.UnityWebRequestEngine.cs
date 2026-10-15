using System.Collections;
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
        // 這個類別負責 ATCWebRequestEngine 的主要流程與狀態。
        // EN: This class manages the main workflow and state for ATC_WebRequestEngine.
        public class ATC_WebRequestEngine : MonoBehaviour
        {
            // 這個欄位保存 instance 的執行狀態或快取資料。
            // EN: This field stores instance runtime state or cached data.
            private static ATC_WebRequestEngine _instance;
            private static readonly object _instanceLock = new object();

            private readonly Dictionary<int, ActiveTranslationRequest> activeRequests = new Dictionary<int, ActiveTranslationRequest>();
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
                                Object.DontDestroyOnLoad(go);
                                _instance = go.AddComponent<ATC_WebRequestEngine>();
                            }
                        }
                    }
                    return _instance;
                }
            }

            // 這個方法負責處理 FireRequest 相關流程。
            // EN: This method handles fire request.
            public int FireRequest(string url, string jsonBody, string apiKey, TranslatorProvider provider, int timeoutSeconds, TaskCompletionSource<ATC_WebResponse> tcs)
            {
                return FireRequest(url, jsonBody, apiKey, provider, timeoutSeconds, tcs, null, false);
            }

            public int FireRequest(string url, string jsonBody, string apiKey, TranslatorProvider provider, int timeoutSeconds, TaskCompletionSource<ATC_WebResponse> tcs, string player2GameClientId, bool usePlayer2Protocol)
            {
                if (tcs == null) return -1;

                int requestId = ++nextRequestId;
                ActiveTranslationRequest active = new ActiveTranslationRequest
                {
                    Id = requestId,
                    Completion = tcs
                };

                activeRequests[requestId] = active;
                active.Coroutine = StartCoroutine(ExecuteRequestCoroutine(active, url, jsonBody, apiKey, provider, timeoutSeconds, player2GameClientId, usePlayer2Protocol));
                return requestId;
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
            private IEnumerator ExecuteRequestCoroutine(ActiveTranslationRequest active, string url, string jsonBody, string apiKey, TranslatorProvider provider, int timeoutSeconds, string player2GameClientId, bool usePlayer2Protocol)
            {
                using (UnityWebRequest webRequest = new UnityWebRequest(url, "POST"))
                {
                    active.WebRequest = webRequest;

                    byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                    webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    webRequest.downloadHandler = new DownloadHandlerBuffer();
                    foreach (var header in ApiRequestHeaders.Build(provider, apiKey, player2GameClientId, usePlayer2Protocol))
                        webRequest.SetRequestHeader(header.Key, header.Value);

                    webRequest.timeout = timeoutSeconds > 0 ? timeoutSeconds : 60;

                    UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
                    while (!operation.isDone)
                    {
                        if (active.IsCompleted)
                        {
                            yield break;
                        }

                        if (AutoTranslatorSettings.IsCancellationRequested)
                        {
                            FinishRequest(active, CreateCancelledResponse("Pipeline cancellation requested"), true);
                            yield break;
                        }

                        yield return null;
                    }

                    if (active.IsCompleted)
                    {
                        yield break;
                    }

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
                        ResponseBody = safeText
                    };
                    response.IsSuccess = UnityWebRequestCompat.IsSuccess(webRequest);

                    FinishRequest(active, response, false);
                }
            }

            // 這個方法負責處理 FinishRequest 相關流程。
            // EN: This method handles finish request.
            private void FinishRequest(ActiveTranslationRequest active, ATC_WebResponse response, bool abortWebRequest)
            {
                if (active == null || active.IsCompleted) return;
                active.IsCompleted = true;

                if (activeRequests.ContainsKey(active.Id))
                {
                    activeRequests.Remove(active.Id);
                }

                if (abortWebRequest && active.WebRequest != null)
                {
                    try { active.WebRequest.Abort(); } catch { }
                }

                if (abortWebRequest && active.Coroutine != null)
                {
                    try { StopCoroutine(active.Coroutine); } catch { }
                }

                active.Completion.TrySetResult(response);
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
                    ResponseBody = string.Empty
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
                // 這個欄位保存 IsCompleted 的執行狀態或快取資料。
                // EN: This field stores is completed runtime state or cached data.
                public bool IsCompleted;
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
            if (requestId <= 0) return;

            if (UnityData.IsInMainThread)
            {
                ATC_WebRequestEngine.Instance.AbortRequest(requestId, reason);
                return;
            }

            ATC_Dispatcher.RunOnMainThread(() =>
            {
                ATC_WebRequestEngine.Instance.AbortRequest(requestId, reason);
            });
        }
    }
}
