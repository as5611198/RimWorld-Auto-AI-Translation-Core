using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AutoTranslator_Core.TranslationPolicy;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責 API 供應商與提示詞規則，並包裝翻譯請求的核心流程。
// EN: This file defines API provider data and drives the core translation request flow.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器API 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorAPI.
    public static partial class AutoTranslatorAPI
    {

        // 這個欄位保存 currentKeyIndex 的執行狀態或快取資料。
        // EN: This field stores current key index runtime state or cached data.
        private static int currentKeyIndex = 0;

        static AutoTranslatorAPI()
        {

            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;


        }
        // 這個欄位保存 供應商登錄 的執行狀態或快取資料。
        // EN: This field stores provider registry runtime state or cached data.
        public static readonly Dictionary<TranslatorProvider, ProviderDef> ProviderRegistry = new Dictionary<TranslatorProvider, ProviderDef>
        {
            { TranslatorProvider.Google, new ProviderDef { BaseUrl = "https://generativelanguage.googleapis.com/v1beta", ListModelsUrl = "https://generativelanguage.googleapis.com/v1beta/models" } },
            { TranslatorProvider.DeepSeek, new ProviderDef { BaseUrl = DeepSeekProviderAdapter.OfficialBaseUrl, ListModelsUrl = "https://api.deepseek.com/models" } },
            { TranslatorProvider.Grok, new ProviderDef { BaseUrl = "https://api.x.ai/v1", ListModelsUrl = "https://api.x.ai/v1/models" } },
            { TranslatorProvider.OpenRouter, new ProviderDef { BaseUrl = "https://openrouter.ai/api/v1", ListModelsUrl = "https://openrouter.ai/api/v1/models" } },
            { TranslatorProvider.GLM, new ProviderDef { BaseUrl = "https://open.bigmodel.cn/api/paas/v4", ListModelsUrl = "https://open.bigmodel.cn/api/paas/v4/models" } },
            { TranslatorProvider.Alibaba, new ProviderDef { BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1", ListModelsUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1/models" } }
        };

        // 這個欄位保存 提示詞規則 的執行狀態或快取資料。
        // EN: This field stores prompt rules runtime state or cached data.
        public static bool IsConfigReady(ApiKeyConfig config)
        {
            return GetWorkflowConfigurationProblem(config) == null;
        }



        // 這個方法負責處理 DelayWithPipelineCancellationAsync 相關流程。
        // EN: This method handles delay with pipeline cancellation async.
        private static async Task<bool> DelayWithPipelineCancellationAsync(
            int delayMs,
            Func<bool> additionalCancellation = null)
        {
            using (TranslationRequestActivity.BeginRetryWait())
            {
                int remaining = Math.Max(0, delayMs);
                while (remaining > 0)
                {
                    if (IsRequestCancellationRequested(additionalCancellation)) return false;

                    int slice = Math.Min(remaining, 100);
                    await Task.Delay(slice);
                    remaining -= slice;
                }

                return !IsRequestCancellationRequested(additionalCancellation);
            }
        }

        // 這個方法負責建立 RequestTimeout回應 物件或檔案。
        // EN: This method creates request timeout response.
        private static ATC_WebResponse CreateRequestTimeoutResponse(TranslatorProvider provider, int timeoutSeconds)
        {
            return new ATC_WebResponse
            {
                IsSuccess = false,
                HttpCode = 0,
                ErrorText = timeoutSeconds > 0
                    ? $"Request timed out after {timeoutSeconds}s [{provider}]"
                    : $"Request cancelled before completion [{provider}]",
                ResponseBody = string.Empty,
                FailureKind = timeoutSeconds > 0
                    ? TranslationRequestFailureKind.ResponseTimeout
                    : TranslationRequestFailureKind.Cancelled,
                FailureStage = timeoutSeconds > 0 ? "WaitingResponse" : "Cancelled",
                TimeoutSeconds = Math.Max(0, timeoutSeconds)
            };
        }

        private static ATC_WebResponse CreateUnityTransportStallResponse(
            TranslatorProvider provider,
            int configuredTimeoutSeconds,
            int cleanupGraceSeconds)
        {
            return new ATC_WebResponse
            {
                IsSuccess = false,
                HttpCode = 0,
                ErrorText =
                    "UnityWebRequest did not finish within " + cleanupGraceSeconds +
                    "s after the configured " + configuredTimeoutSeconds +
                    "s timeout; the request may be stuck inside Unity's network layer and was forcibly aborted [" +
                    provider + "]",
                ResponseBody = string.Empty,
                FailureKind = TranslationRequestFailureKind.UnityTransportStall,
                FailureStage = "UnityWebRequestCleanup",
                TimeoutSeconds = Math.Max(0, configuredTimeoutSeconds)
            };
        }

        // 只在主執行緒或 UnityWebRequest 明確回報無法啟動時使用；等待本身沒有逾時。
        // EN: Used only for a definite local dispatch failure; waiting itself has no timeout.
        private static ATC_WebResponse CreateRequestDispatchFailureResponse(TranslatorProvider provider)
        {
            return new ATC_WebResponse
            {
                IsSuccess = false,
                HttpCode = 0,
                ErrorText = $"UnityWebRequest could not be started [{provider}]",
                ResponseBody = string.Empty,
                FailureKind = TranslationRequestFailureKind.LocalDispatch,
                FailureStage = "Dispatching"
            };
        }




    }

}
