using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責翻譯供應商與網址規則。
// EN: This file resolves translation providers, base URLs, and runtime profiles.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器API 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorAPI.
    public static partial class AutoTranslatorAPI
    {
        // 這個結構保存 供應商Def 所需的資料欄位。
        // EN: This struct stores data used by ProviderDef.
        public struct ProviderDef
        {
            // 這個欄位保存 Base網址 的執行狀態或快取資料。
            // EN: This field stores base URL runtime state or cached data.
            public string BaseUrl;
            // 這個欄位保存 ListModels網址 的執行狀態或快取資料。
            // EN: This field stores list models URL runtime state or cached data.
            public string ListModelsUrl;
        }


        // 這個結構保存 供應商執行期Profile 所需的資料欄位。
        // EN: This struct stores data used by ProviderRuntimeProfile.
        public struct ProviderRuntimeProfile
        {
            // 這個欄位保存 BatchSize 的執行狀態或快取資料。
            // EN: This field stores batch size runtime state or cached data.
            public int BatchSize;
            // 這個欄位保存 FormatRetries 的執行狀態或快取資料。
            // EN: This field stores format retries runtime state or cached data.
            public int FormatRetries;
            // 這個欄位保存 TimeoutFloorSeconds 的執行狀態或快取資料。
            // EN: This field stores timeout floor seconds runtime state or cached data.
            public int TimeoutFloorSeconds;
            // 這個欄位保存 QualityHintKey 的執行狀態或快取資料。
            // EN: This field stores quality hint key runtime state or cached data.
            public string QualityHintKey;
        }


        // 這個方法負責取得 執行期Profile 資料。
        // EN: This method gets runtime profile.
        public static ProviderRuntimeProfile GetRuntimeProfile(TranslatorProvider provider, string model = null)
        {
            bool reasoning = !string.IsNullOrEmpty(model) && IsReasoningModel(model);

            switch (provider)
            {
                case TranslatorProvider.DeepSeek:
                    return new ProviderRuntimeProfile { BatchSize = reasoning ? 6 : 12, FormatRetries = 1, TimeoutFloorSeconds = reasoning ? 300 : 90, QualityHintKey = "ATC_Profile_DeepSeek" };
                case TranslatorProvider.Google:
                    return new ProviderRuntimeProfile { BatchSize = 40, FormatRetries = 1, TimeoutFloorSeconds = 60, QualityHintKey = "ATC_Profile_Google" };
                case TranslatorProvider.DeepL:
                    return new ProviderRuntimeProfile { BatchSize = 50, FormatRetries = 0, TimeoutFloorSeconds = 60, QualityHintKey = "ATC_Profile_DeepL" };
                case TranslatorProvider.Custom_OpenAI:
                    return new ProviderRuntimeProfile { BatchSize = reasoning ? 12 : 24, FormatRetries = 1, TimeoutFloorSeconds = reasoning ? 300 : 90, QualityHintKey = "ATC_Profile_Custom" };
                case TranslatorProvider.MiMo:
                    return new ProviderRuntimeProfile { BatchSize = 24, FormatRetries = 1, TimeoutFloorSeconds = 90, QualityHintKey = "ATC_Advanced_MiMoProfile" };
                case TranslatorProvider.OpenRouter:
                    return new ProviderRuntimeProfile { BatchSize = reasoning ? 12 : 28, FormatRetries = 1, TimeoutFloorSeconds = reasoning ? 300 : 90, QualityHintKey = "ATC_Profile_OpenRouter" };
                default:
                    return new ProviderRuntimeProfile { BatchSize = reasoning ? 16 : 32, FormatRetries = 1, TimeoutFloorSeconds = reasoning ? 300 : 90, QualityHintKey = "ATC_Profile_Default" };
            }
        }


        // 這個方法負責取得 Current執行期Profile 資料。
        // EN: This method gets current runtime profile.
        public static ProviderRuntimeProfile GetCurrentRuntimeProfile()
        {
            ApiKeyConfig config = AutoTranslatorMod.Settings.ApiConfigs
                .FirstOrDefault(IsConfigReady);
            if (config == null)
            {
                return new ProviderRuntimeProfile { BatchSize = 32, FormatRetries = 1, TimeoutFloorSeconds = 90, QualityHintKey = "ATC_Profile_Default" };
            }
            return GetRuntimeProfile(config.Provider, config.SelectedModel);
        }

        // 這個方法負責取得 Base網址 資料。
        // EN: This method gets base URL.
        private static string GetBaseUrl(ApiKeyConfig config)
        {
            string custom;
            string errorKey;
            if (!ApiEndpointRules.TryNormalize(config.CustomBaseUrl, CleanInput(config.Key), out custom, out errorKey))
                throw new InvalidOperationException(TranslateText(errorKey));
            string fingerprint = AutoTranslatorAPI.GetModelFetchFingerprint(config);
            if (!string.IsNullOrEmpty(config.ResolvedBaseUrl) && config.ResolvedBaseUrlFingerprint == fingerprint)
                return config.ResolvedBaseUrl;
            if (config.Provider == TranslatorProvider.Custom_OpenAI && config.UsePlayer2Protocol)
                return Player2LocalApi.NormalizeBaseUrl(custom);
            if (!string.IsNullOrEmpty(custom)) return AppendCustomV1(custom,
                config.Provider == TranslatorProvider.Custom_OpenAI || config.Provider == TranslatorProvider.MiMo);

            if (config.Provider == TranslatorProvider.DeepL)
            {
                return (!string.IsNullOrEmpty(config.Key) && config.Key.Trim().EndsWith(":fx"))
                    ? "https://api-free.deepl.com/v2"
                    : "https://api.deepl.com/v2";
            }


            if (ProviderRegistry.TryGetValue(config.Provider, out var def))
            {
                return def.BaseUrl;
            }
            return "https://api.openai.com/v1";
        }

        private static string AppendCustomV1(string baseUrl, bool customOpenAi)
        {
            if (!customOpenAi || baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return baseUrl;
            return baseUrl.TrimEnd('/') + "/v1";
        }

        public static string GetAlternateCustomBaseUrl(ApiKeyConfig config, string currentBaseUrl)
        {
            if (config == null || config.Provider != TranslatorProvider.Custom_OpenAI || config.UsePlayer2Protocol)
                return string.Empty;
            string normalized = (currentBaseUrl ?? string.Empty).TrimEnd('/');
            if (!normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            return normalized.Substring(0, normalized.Length - 3).TrimEnd('/');
        }

        public static void RememberResolvedBaseUrl(ApiKeyConfig config, string baseUrl)
        {
            if (config == null || string.IsNullOrEmpty(baseUrl)) return;
            config.ResolvedBaseUrl = baseUrl.TrimEnd('/');
            config.ResolvedBaseUrlFingerprint = AutoTranslatorAPI.GetModelFetchFingerprint(config);
        }

        public static string GetApiEndpointError(ApiKeyConfig config)
        {
            if (config == null) return ApiEndpointRules.InvalidUrl;
            string normalized;
            string errorKey;
            ApiEndpointRules.TryNormalize(config.CustomBaseUrl, CleanInput(config.Key), out normalized, out errorKey);
            if (!string.IsNullOrEmpty(errorKey)) return errorKey;
            if (config.Provider == TranslatorProvider.MiMo && !string.IsNullOrEmpty(normalized) &&
                (new Uri(normalized).AbsolutePath.IndexOf("/anthropic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 normalized.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)))
                return "ATC_MiMo_OpenAiRequired";
            if (config.Provider == TranslatorProvider.Custom_OpenAI && config.UsePlayer2Protocol)
            {
                Uri endpoint = new Uri(Player2LocalApi.NormalizeBaseUrl(normalized));
                string clientId;
                if (!Player2LocalApi.TryGetGameClientId(config.Player2GameClientId, out clientId))
                    return "ATC_Player2_ClientIdRequired";
            }
            return errorKey;
        }

        public static bool HasApiCredential(ApiKeyConfig config)
        {
            if (config == null || !string.IsNullOrEmpty(GetApiEndpointError(config))) return false;
            if (config.Provider == TranslatorProvider.Custom_OpenAI && config.UsePlayer2Protocol) return true;
            return !string.IsNullOrEmpty(CleanInput(config.Key)) ||
                   (config.Provider == TranslatorProvider.Custom_OpenAI &&
                    !string.IsNullOrWhiteSpace(config.CustomBaseUrl));
        }

        private static bool ValidateApiEndpointForRequest(ApiKeyConfig config)
        {
            string errorKey = GetApiEndpointError(config);
            if (string.IsNullOrEmpty(errorKey)) return true;
            AutoTranslatorSettings.AddErrorLog(TranslateText(errorKey));
            return false;
        }

        // 這個方法負責清理並標準化 Input 內容。
        // EN: This method cleans and normalizes input.
        public static string CleanInput(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";


            var builder = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (!char.IsWhiteSpace(c) && c >= 32 && c <= 126)
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }


        // 這個方法負責判斷 IsReasoning模型 條件是否成立。
        // EN: This method checks is reasoning model.
        private static bool IsReasoningModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            string lower = modelName.ToLower();
            return lower.Contains("reasoner") ||
                   lower.Contains("o1-") ||
                   lower.StartsWith("o1") ||
                   lower.Contains("-thinking") ||
                   lower.Contains("qwq") ||
                   lower.Contains("r1");
        }

    }
}
