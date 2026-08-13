using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutoTranslator_Core.Terminology;
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
        private static readonly Dictionary<TargetLanguage, LangRule> PromptRules = new Dictionary<TargetLanguage, LangRule>
        {
            { TargetLanguage.Traditional, new LangRule { Name = "台灣繁體中文 (Traditional Chinese, zh-TW)", Specifics = "1. 術語轉換：若原文為另一種語系，必須強制轉換（例如：質量->品質、信息->訊息、激活->啟動、菜單->選單、程序->程式）。\n" }},
            { TargetLanguage.Simplified, new LangRule { Name = "大陆简体中文 (Simplified Chinese, zh-CN)", Specifics = "1. 术语转换：若原文为另一种语系，必须强制转换（例如：品質->質量、訊息->信息、啟動->激活、選單->菜單、程式->程序）。\n" }},
            { TargetLanguage.Japanese, new LangRule { Name = "Japanese (日本語)", Specifics = "1. Style: Use natural Japanese suitable for the RimWorld gaming atmosphere. Use appropriate Katakana for sci-fi terms.\n" }},
            { TargetLanguage.Korean, new LangRule { Name = "Korean (한국어)", Specifics = "1. Style: Use natural Korean suitable for the RimWorld gaming atmosphere.\n" }},
            { TargetLanguage.Russian, new LangRule { Name = "Russian (Русский)", Specifics = "1. Style: Use natural Russian suitable for the RimWorld gaming atmosphere.\n" }},
            { TargetLanguage.Ukrainian, new LangRule { Name = "Ukrainian (Українська)", Specifics = "1. Style: Use natural Ukrainian suitable for the RimWorld gaming atmosphere.\n" }},
            { TargetLanguage.English, new LangRule { Name = "English (US/UK)", Specifics = "1. Style: Translate foreign text into natural English suitable for the RimWorld gaming atmosphere.\n" }},

            { TargetLanguage.French, new LangRule { Name = "French (Français)", Specifics = "1. Style: Use natural French suitable for the RimWorld gaming atmosphere.\n" }},
            { TargetLanguage.German, new LangRule { Name = "German (Deutsch)", Specifics = "1. Style: Use natural German suitable for the RimWorld gaming atmosphere.\n" }},
            { TargetLanguage.Spanish, new LangRule { Name = "Spanish (Español)", Specifics = "1. Style: Use natural Spanish suitable for the RimWorld gaming atmosphere.\n" }},
            { TargetLanguage.Italian, new LangRule { Name = "Italian (Italiano)", Specifics = "1. Style: Use natural Italian suitable for the RimWorld gaming atmosphere.\n" }},
            { TargetLanguage.Polish, new LangRule { Name = "Polish (Polski)", Specifics = "1. Style: Use natural Polish suitable for the RimWorld gaming atmosphere.\n" }},
            { TargetLanguage.Portuguese, new LangRule { Name = "Brazilian Portuguese (Português do Brasil)", Specifics = "1. Style: Use natural Brazilian Portuguese suitable for the RimWorld gaming atmosphere.\n" }},
            { TargetLanguage.Turkish, new LangRule { Name = "Turkish (Türkçe)", Specifics = "1. Style: Use natural Turkish suitable for the RimWorld gaming atmosphere.\n" }}
        };


        // 這個方法負責取得 Next設定 資料。
        // EN: This method gets next config.
        public static ApiKeyConfig GetNextConfig(TranslationTaskTier requestedTier = TranslationTaskTier.Bulk)
        {
            var validConfigs = AutoTranslatorMod.Settings.ApiConfigs
                .Where(IsConfigReady)
                .ToList();

            if (validConfigs.Count == 0) return null;
            List<ApiKeyConfig> eligible =
                TranslationTaskTierRouter.SelectEligible(validConfigs, requestedTier);
            if (eligible.Count == 0) return null;
            if (eligible.Count == 1) return eligible[0];

            int idx = System.Threading.Interlocked.Increment(ref currentKeyIndex);
            return eligible[Math.Abs(idx) % eligible.Count];
        }

        // 這個方法負責判斷 Is設定Ready 條件是否成立。
        // EN: This method checks is config ready.
        public static bool IsConfigReady(ApiKeyConfig config)
        {
            return config != null &&
                   config.Enabled &&
                   !string.IsNullOrEmpty(config.Key) &&
                   !string.IsNullOrEmpty(config.SelectedModel);
        }

        // 這個方法負責判斷 HasAnyReady設定 條件是否成立。
        // EN: This method checks has any ready config.
        public static bool HasAnyReadyConfig()
        {
            return AutoTranslatorMod.Settings.ApiConfigs != null &&
                   AutoTranslatorMod.Settings.ApiConfigs.Any(config =>
                       IsConfigReady(config) && config.TaskTier == TranslationTaskTier.Bulk);
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

        // 這個方法負責翻譯 BatchAsync 內容。
        // EN: This method translates batch async.
        public static async Task<List<string>> TranslateBatchAsync(
            List<string> texts,
            ApiKeyConfig forceConfig = null,
            bool suppressFinalParseError = false,
            string packageId = null,
            string requestScope = null,
            string requestPurpose = "translation",
            bool reportFailureToUser = true)
        {
            ClearLastTranslationFailure();
            long sourceCharacters = (texts ?? new List<string>())
                .Sum(text => (long)(text ?? string.Empty).Length);
            bool connectionTest = texts != null && texts.Count == 1 && texts[0] == "Connection Test";
            string previousTerminologyContext = TerminologyPromptContext.Value;
            IReadOnlyList<TerminologyCandidate> previousTerminologyTerms = TerminologyRequestTerms.Value;
            List<TerminologyCandidate> requestTerms = connectionTest
                ? new List<TerminologyCandidate>()
                : TerminologyRuntime.GetRelevantTerms(packageId, texts);
            TerminologyRequestTerms.Value = requestTerms;
            TerminologyPromptContext.Value = connectionTest
                ? string.Empty
                : TerminologyPromptContextBuilder.Build(requestTerms, texts, 20, 2000);
            try
            {
                using (TranslationUsageCoordinator.PushRequestContext(
                    packageId,
                    connectionTest ? "connection_test" : requestPurpose,
                    requestScope,
                    sourceCharacters,
                    exempt: connectionTest,
                    itemCount: texts?.Count ?? 0))
                {
                    List<string> result = await TranslateBatchCoreAsync(
                        texts,
                        forceConfig,
                        suppressFinalParseError,
                        ResolveTaskTier(requestPurpose, connectionTest));
                    if (result == null)
                    {
                        PublishLastTranslationFailure(requestScope);
                        if (reportFailureToUser &&
                            !connectionTest &&
                            !AutoTranslatorSettings.IsCancellationRequested)
                        {
                            string contextInfo = !string.IsNullOrWhiteSpace(requestScope)
                                ? requestScope
                                : !string.IsNullOrWhiteSpace(packageId)
                                    ? packageId
                                    : requestPurpose ?? "translation";
                            string failureDetail = DescribeLastTranslationFailure(
                                contextInfo,
                                requestScope,
                                out string aggregationKey,
                                out int affectedItems);
                            AddAggregatedTranslationFailure(
                                aggregationKey,
                                failureDetail,
                                affectedItems > 0 ? affectedItems : texts?.Count ?? 0);
                        }
                    }
                    return result;
                }
            }
            finally
            {
                TerminologyPromptContext.Value = previousTerminologyContext;
                TerminologyRequestTerms.Value = previousTerminologyTerms;
            }
        }

        private static async Task<List<string>> TranslateBatchCoreAsync(
            List<string> texts,
            ApiKeyConfig forceConfig,
            bool suppressFinalParseError,
            TranslationTaskTier taskTier)
        {
            if (AutoTranslatorSettings.IsCancellationRequested) return null;

            ApiKeyConfig targetConfig = forceConfig ?? GetNextConfig(taskTier);
            if (targetConfig == null)
            {
                RecordLastTranslationFailure(null, new ATC_WebResponse
                {
                    HttpCode = 0,
                    ErrorText = "No enabled API configuration is available for this task tier.",
                    ResponseBody = string.Empty,
                    FailureKind = TranslationRequestFailureKind.Configuration,
                    FailureStage = "Configuration",
                    ItemCount = texts != null ? texts.Count : 0,
                    SourceCharacters = texts != null
                        ? texts.Sum(text => (long)(text ?? string.Empty).Length)
                        : 0L
                }, false, false, 1);
                return null;
            }

            try
            {
                if (targetConfig.Provider == TranslatorProvider.DeepSeek)
                    return await TranslateDeepSeekBatchAsync(texts, targetConfig, suppressFinalParseError);
                if (targetConfig.Provider != TranslatorProvider.DeepL)
                    return await TranslateStructuredProviderBatchAsync(texts, targetConfig, suppressFinalParseError);

                string apiKey = CleanInput(targetConfig.Key);
                string model = CleanInput(targetConfig.SelectedModel);
                string baseUrl = GetBaseUrl(targetConfig);
                string url;
                object payload;
                TargetLanguage requestTargetLang = AutoTranslatorMod.Settings.TargetLang;
                string prompt = GetSystemPrompt(requestTargetLang);
                string inputJson = JsonConvert.SerializeObject(texts);

                if (targetConfig.Provider == TranslatorProvider.Google)
                {
                    url = $"{baseUrl}/models/{model}:generateContent?key={apiKey}";
                    payload = new { contents = new[] { new { parts = new[] { new { text = $"{prompt}\n\nInput JSON:\n{inputJson}" } } } } };
                }
                else if (targetConfig.Provider == TranslatorProvider.DeepL)
                {
                    url = $"{baseUrl}/translate";
                    string deepLLang = MapToDeepLLangCode(requestTargetLang);
                    if (string.IsNullOrEmpty(deepLLang))
                    {
                        RecordLastTranslationFailure(targetConfig, new ATC_WebResponse
                        {
                            HttpCode = 0,
                            ErrorText = "The selected target language is not supported by the DeepL route.",
                            ResponseBody = string.Empty,
                            FailureKind = TranslationRequestFailureKind.Configuration,
                            FailureStage = "Configuration",
                            ItemCount = texts != null ? texts.Count : 0,
                            SourceCharacters = texts != null
                                ? texts.Sum(text => (long)(text ?? string.Empty).Length)
                                : 0L
                        }, false, false, 1);
                        return null;
                    }
                    payload = new { text = texts.ToArray(), target_lang = deepLLang, preserve_formatting = true, tag_handling = "xml" };
                }
                else
                {
                    url = $"{baseUrl}/chat/completions";
                    bool isReasoningModel = IsReasoningModel(model);
                    int safeMaxTokens = 4096;

                    if (isReasoningModel || targetConfig.Provider == TranslatorProvider.Custom_OpenAI || targetConfig.Provider == TranslatorProvider.DeepSeek)
                    {
                        payload = new { model = string.IsNullOrEmpty(model) ? "local-model" : model, messages = new[] { new { role = "system", content = prompt }, new { role = "user", content = inputJson } }, max_tokens = safeMaxTokens };
                    }
                    else
                    {
                        payload = new { model = string.IsNullOrEmpty(model) ? "local-model" : model, messages = new[] { new { role = "system", content = prompt }, new { role = "user", content = inputJson } }, max_tokens = safeMaxTokens };
                    }
                }
                string jsonPayload = JsonConvert.SerializeObject(payload);
                var profile = GetRuntimeProfile(targetConfig.Provider, model);
                bool isConnectionTestRequest = texts.Count == 1 && texts[0] == "Connection Test";
                // A retry is another potentially billable request. Failed batches are
                // persisted as unresolved work and only retried after explicit user action.
                int maxRetries = 0;
                int maxFormatRetries = 0;
                int formatRetryCount = 0;
                bool hadFormatRetry = false;
                int customTimeout = AutoTranslatorMod.Settings.TimeoutSeconds > 0 ? AutoTranslatorMod.Settings.TimeoutSeconds : 60;
                customTimeout = TranslationPolicyAgentTimeout.Resolve(customTimeout, 0);

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    if (AutoTranslatorSettings.IsCancellationRequested) return null;

                    ATC_WebResponse resHolder = await SendTranslationRequestWithConcurrencyRecoveryAsync(
                        () => SendJsonRequestAttemptAsync(
                            url,
                            jsonPayload,
                            apiKey,
                            targetConfig.Provider,
                            customTimeout),
                        targetConfig);

                    if (resHolder == null) return null;
                    if (AutoTranslatorSettings.IsCancellationRequested && !resHolder.IsSuccess) return null;
                    if (resHolder.BudgetDenied) return null;

                    if (resHolder.IsSuccess)
                    {
                        bool expectsGoogleFormat = (targetConfig.Provider == TranslatorProvider.Google);

                        if (isConnectionTestRequest)
                        {
                            return new List<string> { "Connection OK" };
                        }

                        List<string> parsed = ParseResponse(
                            resHolder.ResponseBody,
                            targetConfig.Provider,
                            texts.Count,
                            expectsGoogleFormat);
                        if (parsed != null && parsed.Count == texts.Count)
                        {
                            parsed = NormalizeBatchForTargetLanguage(parsed, requestTargetLang);
                            int charCount = texts.Sum(t => t.Length);
                            AutoTranslatorMod.Settings.SessionCharCount += charCount;
                            AutoTranslatorMod.Settings.TotalCharCount += charCount;

                            if (hadFormatRetry)
                            {
                                AutoTranslatorSettings.AddLog("✅ " + "ATC_Log_AIFormatRecovered".Translate());
                            }

                            return parsed;
                        }

                        if (formatRetryCount < maxFormatRetries && attempt < maxRetries)
                        {
                            hadFormatRetry = true;
                            formatRetryCount++;
                            int baseDelay = 1000 * (int)Math.Pow(2, attempt);
                            int jitter = new System.Random().Next(100, 800);
                            int delayMs = baseDelay + jitter;

                            AutoTranslatorSettings.AddLog("⚠️ " + AutoTranslatorAPI.TranslateText("ATC_Log_AIFormatRetry", formatRetryCount));
                            ATC_Dispatcher.RunOnMainThread(() =>
                                Verse.Log.Warning($"[AutoTranslationCore] " + AutoTranslatorAPI.TranslateText("ATC_Log_AIFormatRetry", formatRetryCount))
                            );

                            if (!await DelayWithPipelineCancellationAsync(delayMs)) return null;
                            continue;
                        }

                        TranslationFailureDiagnosticPolicy.LogInvalidResponseForDeveloper(
                            targetConfig != null
                                ? targetConfig.Provider + "/translation"
                                : "translation",
                            "The response could not be parsed into the expected translation batch.",
                            resHolder.ResponseBody);
                        RecordLastTranslationFailure(targetConfig, new ATC_WebResponse
                        {
                            HttpCode = resHolder.HttpCode,
                            ErrorText = "The provider response could not be parsed into the expected translation batch.",
                            ResponseBody = resHolder.ResponseBody,
                            FailureKind = TranslationRequestFailureKind.InvalidResponse,
                            FailureStage = "ResponseParsing",
                            ItemCount = resHolder.ItemCount,
                            SourceCharacters = resHolder.SourceCharacters,
                            EstimatedInputTokens = resHolder.EstimatedInputTokens,
                            TimeoutSeconds = resHolder.TimeoutSeconds
                        }, false, false, 1);
                        return null;
                    }

                    int statusCode = (int)resHolder.HttpCode;

                    if ((statusCode == 429 || statusCode >= 500 || resHolder.HttpCode == 0) && attempt < maxRetries)
                    {
                        int baseDelay = 1000 * (int)Math.Pow(2, attempt);
                        int jitter = new System.Random().Next(100, 800);
                        int delayMs = baseDelay + jitter;


                        ATC_Dispatcher.RunOnMainThread(() =>
                            Verse.Log.Warning($"[AutoTranslationCore] " + AutoTranslatorAPI.TranslateText("ATC_Log_RetryAttempt", statusCode, attempt + 1, delayMs))
                        );

                        if (!await DelayWithPipelineCancellationAsync(delayMs)) return null;
                        continue;
                    }

                    string errText = resHolder.ErrorText;


                    ATC_Dispatcher.RunOnMainThread(() =>
                        Verse.Log.Error($"[AutoTranslationCore] UnityWebRequest Package Lost [{targetConfig.Provider}] (HTTP {statusCode}): {errText}\nBody: {resHolder.ResponseBody}")
                    );

                    return null;
                }
                return null;
            }
            catch (Exception ex)
            {
                RecordLastTranslationFailure(targetConfig, new ATC_WebResponse
                {
                    HttpCode = 0,
                    ErrorText = ex.Message,
                    ResponseBody = string.Empty,
                    FailureKind = TranslationRequestFailureKind.Transport,
                    FailureStage = "TranslationProcessing",
                    ItemCount = texts != null ? texts.Count : 0,
                    SourceCharacters = texts != null
                        ? texts.Sum(text => (long)(text ?? string.Empty).Length)
                        : 0L
                }, false, false, 1);

                ATC_Dispatcher.RunOnMainThread(() =>
                    Verse.Log.Error($"[AutoTranslationCore] Fatal Translation Bridge Error: {ex}")
                );
                return null;
            }
        }

        private static void AddAggregatedTranslationFailure(
            string aggregationKey,
            string failureDetail,
            int affectedItems)
        {
            AutoTranslatorSettings.AddAggregatedErrorLog(
                aggregationKey,
                failureDetail,
                failureDetail,
                affectedItems);
        }

        private static TranslationTaskTier ResolveTaskTier(string requestPurpose, bool connectionTest)
        {
            if (connectionTest) return TranslationTaskTier.Bulk;
            string purpose = (requestPurpose ?? string.Empty).Trim().ToLowerInvariant();
            if (purpose == "manual_retry" || purpose == "unresolved_retry" || purpose == "validation_retry")
                return TranslationTaskTier.Precision;
            if (purpose == "analysis" || purpose == "review")
                return TranslationTaskTier.Standard;
            return TranslationTaskTier.Bulk;
        }
    }

}
