using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

        // 這個常數定義 翻譯DispatchTimeoutMs 的固定值。
        // EN: This constant defines the fixed value for translation dispatch timeout ms.
        private const int TranslationDispatchTimeoutMs = 5000;
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
            { TranslatorProvider.DeepSeek, new ProviderDef { BaseUrl = "https://api.deepseek.com/v1", ListModelsUrl = "https://api.deepseek.com/models" } },
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
            { TargetLanguage.Turkish, new LangRule { Name = "Turkish (Türkçe)", Specifics = "1. Style: Use natural Turkish suitable for the RimWorld gaming atmosphere.\n" }},
            { TargetLanguage.Thai, new LangRule { Name = "Thai (ภาษาไทย, th-TH)", Specifics = "1. Style: Use natural Thai suitable for the RimWorld gaming atmosphere. Keep established game terminology consistent and avoid transliterating proper nouns unless Thai players commonly do so.\n" }}
        };


        // 這個方法負責取得 Next設定 資料。
        // EN: This method gets next config.
        public static ApiKeyConfig GetNextConfig()
        {
            var validConfigs = AutoTranslatorMod.Settings.ApiConfigs
                .Where(IsConfigReady)
                .ToList();

            if (validConfigs.Count == 0) return null;
            if (validConfigs.Count == 1) return validConfigs[0];

            int idx = System.Threading.Interlocked.Increment(ref currentKeyIndex);
            return validConfigs[Math.Abs(idx) % validConfigs.Count];
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
                   AutoTranslatorMod.Settings.ApiConfigs.Any(IsConfigReady);
        }


        // 這個方法負責處理 DelayWithPipelineCancellationAsync 相關流程。
        // EN: This method handles delay with pipeline cancellation async.
        private static async Task<bool> DelayWithPipelineCancellationAsync(int delayMs)
        {
            int remaining = Math.Max(0, delayMs);
            while (remaining > 0)
            {
                if (AutoTranslatorSettings.IsCancellationRequested) return false;

                int slice = Math.Min(remaining, 100);
                await Task.Delay(slice);
                remaining -= slice;
            }

            return !AutoTranslatorSettings.IsCancellationRequested;
        }

        // 這個方法負責建立 RequestTimeout回應 物件或檔案。
        // EN: This method creates request timeout response.
        private static ATC_WebResponse CreateRequestTimeoutResponse(TranslatorProvider provider, int timeoutSeconds)
        {
            return new ATC_WebResponse
            {
                IsSuccess = false,
                HttpCode = 0,
                ErrorText = $"Request timed out after {timeoutSeconds}s [{provider}]",
                ResponseBody = string.Empty
            };
        }

        // 這個方法負責建立 RequestDispatchTimeout回應 物件或檔案。
        // EN: This method creates request dispatch timeout response.
        private static ATC_WebResponse CreateRequestDispatchTimeoutResponse(TranslatorProvider provider)
        {
            return new ATC_WebResponse
            {
                IsSuccess = false,
                HttpCode = 0,
                ErrorText = $"Request dispatch timed out before UnityWebRequest started [{provider}]",
                ResponseBody = string.Empty
            };
        }

        // 這個方法負責翻譯 BatchAsync 內容。
        // EN: This method translates batch async.
        public static async Task<List<string>> TranslateBatchAsync(List<string> texts, ApiKeyConfig forceConfig = null, bool suppressFinalParseError = false)
        {
            if (AutoTranslatorSettings.IsCancellationRequested) return null;

            ApiKeyConfig targetConfig = forceConfig ?? GetNextConfig();
            if (targetConfig == null) return null;

            string apiKey = CleanInput(targetConfig.Key);
            string model = CleanInput(targetConfig.SelectedModel);
            string baseUrl = GetBaseUrl(targetConfig);

            try
            {
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
                    if (string.IsNullOrEmpty(deepLLang)) return null;
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
                bool reasoningModel = IsReasoningModel(model);
                int maxRetries = isConnectionTestRequest || reasoningModel ? 0 : 1;
                int maxFormatRetries = profile.FormatRetries;
                int formatRetryCount = 0;
                bool hadFormatRetry = false;
                int customTimeout = AutoTranslatorMod.Settings.TimeoutSeconds > 0 ? AutoTranslatorMod.Settings.TimeoutSeconds : 60;
                if (reasoningModel) customTimeout = 300;
                customTimeout = Math.Max(customTimeout, profile.TimeoutFloorSeconds);

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    if (AutoTranslatorSettings.IsCancellationRequested) return null;

                    ATC_WebResponse resHolder = await SendJsonRequestAttemptAsync(
                        url,
                        jsonPayload,
                        apiKey,
                        targetConfig.Provider,
                        customTimeout);

                    if (AutoTranslatorSettings.IsCancellationRequested) return null;
                    if (resHolder == null) return null;

                    if (resHolder.IsSuccess)
                    {
                        bool expectsGoogleFormat = (targetConfig.Provider == TranslatorProvider.Google);

                        if (isConnectionTestRequest)
                        {
                            return new List<string> { "Connection OK" };
                        }

                        List<string> parsed = ParseResponse(resHolder.ResponseBody, targetConfig.Provider, texts.Count, expectsGoogleFormat);
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

                        if (!suppressFinalParseError)
                        {
                            AutoTranslatorSettings.AddErrorLog("⚠️ " + "ATC_Error_ParseFailed".Translate());
                        }
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
                    if (statusCode == 401 || statusCode == 403)
                    {
                        AutoTranslatorSettings.AddErrorLog($"🔒 [{targetConfig.Provider}] " + "ATC_Error_Unauthorized".Translate());
                    }
                    else if (statusCode == 429)
                    {
                        AutoTranslatorSettings.AddErrorLog($"⏱️ [{targetConfig.Provider}] " + "ATC_Error_RateLimit".Translate());
                    }
                    else if (statusCode >= 500)
                    {
                        AutoTranslatorSettings.AddErrorLog($"🔥 [{targetConfig.Provider}] " + "ATC_Error_ServerError".Translate());
                    }
                    else
                    {
                        AutoTranslatorSettings.AddErrorLog($"⚠️ [{targetConfig.Provider}] " + AutoTranslatorAPI.TranslateText("ATC_Error_HttpGeneric", statusCode) + $" ({errText})");
                    }


                    ATC_Dispatcher.RunOnMainThread(() =>
                        Verse.Log.Error($"[AutoTranslationCore] UnityWebRequest Package Lost [{targetConfig.Provider}] (HTTP {statusCode}): {errText}\nBody: {resHolder.ResponseBody}")
                    );

                    return null;
                }
                return null;
            }
            catch (Exception ex)
            {

                ATC_Dispatcher.RunOnMainThread(() =>
                    Verse.Log.Error($"[AutoTranslationCore] Fatal Translation Bridge Error: {ex}")
                );
                return null;
            }
        }
    }

}
