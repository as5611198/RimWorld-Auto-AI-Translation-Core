using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Verse;
using AutoTranslator_Core.Terminology;
using AutoTranslator_Core.TranslationPolicy;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorAPI
    {
        /// <summary>
        /// Executes one DeepSeek translation request and normalizes the provider-specific
        /// protocol back to the same List&lt;string&gt; contract used by the outer pipeline.
        /// This method deliberately does not retry a submitted request: if the provider
        /// processed it but the response was lost, an automatic retry could charge twice.
        /// </summary>
        private static async Task<List<string>> TranslateDeepSeekBatchAsync(
            List<string> texts,
            ApiKeyConfig config,
            bool suppressFinalParseError)
        {
            string apiKey = CleanInput(config.Key);
            string model = CleanInput(config.SelectedModel);
            string baseUrl = GetBaseUrl(config);
            bool wantsStrictFunctionCalling =
                config.StructuredOutput == StructuredOutputPreference.Auto ||
                config.StructuredOutput == StructuredOutputPreference.JsonSchema;
            bool useStrictFunctionCalling = wantsStrictFunctionCalling &&
                DeepSeekProviderAdapter.SupportsStrictFunctionCalling(baseUrl);
            DeepSeekPreparedRequest request = DeepSeekProviderAdapter.BuildTranslationRequest(
                baseUrl,
                model,
                GetSystemPrompt(AutoTranslatorMod.Settings.TargetLang),
                texts,
                4096,
                useStrictFunctionCalling,
                TerminologyRequestTerms.Value);

            int configuredTimeout = AutoTranslatorMod.Settings.TimeoutSeconds > 0
                ? AutoTranslatorMod.Settings.TimeoutSeconds
                : 60;
            int timeoutSeconds = TranslationPolicyAgentTimeout.Resolve(configuredTimeout, 0);

            ATC_WebResponse response = await SendTranslationRequestWithConcurrencyRecoveryAsync(
                () => SendJsonRequestAttemptAsync(
                    request.Url,
                    request.JsonPayload,
                    apiKey,
                    TranslatorProvider.DeepSeek,
                    timeoutSeconds),
                config);

            if (response == null) return null;
            if (AutoTranslatorSettings.IsCancellationRequested && !response.IsSuccess) return null;
            if (response.BudgetDenied) return null;
            if (!response.IsSuccess)
            {
                LogDeepSeekRequestFailure(response);
                return null;
            }

            if (texts.Count == 1 && texts[0] == "Connection Test")
                return new List<string> { "Connection OK" };

            if (!DeepSeekProviderAdapter.TryParseTranslationResponse(
                    response.ResponseBody,
                    texts.Count,
                    request.ResponseMode,
                    out DeepSeekTranslationResponse parsed,
                    out string error))
            {
                TranslationFailureDiagnosticPolicy.LogInvalidResponseForDeveloper(
                    "DeepSeek/" + request.ResponseMode,
                    error,
                    response.ResponseBody);
                RecordLastTranslationFailure(config, new ATC_WebResponse
                {
                    HttpCode = response.HttpCode,
                    ErrorText = "DeepSeek response validation failed: " + error,
                    ResponseBody = response.ResponseBody,
                    FailureKind = TranslationRequestFailureKind.InvalidResponse,
                    FailureStage = "ResponseParsing",
                    ItemCount = response.ItemCount,
                    SourceCharacters = response.SourceCharacters,
                    EstimatedInputTokens = response.EstimatedInputTokens,
                    TimeoutSeconds = response.TimeoutSeconds
                }, false, false, 1);
                Log.Warning("[AutoTranslationCore] DeepSeek response validation failed: " + error);
                return null;
            }

            List<string> translations = NormalizeBatchForTargetLanguage(
                parsed.Translations,
                AutoTranslatorMod.Settings.TargetLang);
            TerminologyApplicationValidationResult termValidation = TerminologyApplicationValidator.Validate(
                TerminologyRequestTerms.Value,
                texts,
                translations,
                parsed.TermApplications ?? new List<TerminologyApplication>());
            if (!termValidation.IsValid)
            {
                TranslationFailureDiagnosticPolicy.LogInvalidResponseForDeveloper(
                    "DeepSeek/" + request.ResponseMode,
                    "Terminology application validation failed: " + termValidation.ErrorCode,
                    response.ResponseBody);
                RecordLastTranslationFailure(config, new ATC_WebResponse
                {
                    HttpCode = response.HttpCode,
                    ErrorText = "Terminology application validation failed: " + termValidation.ErrorCode,
                    ResponseBody = response.ResponseBody,
                    FailureKind = TranslationRequestFailureKind.InvalidResponse,
                    FailureStage = "TerminologyValidation",
                    ItemCount = response.ItemCount,
                    SourceCharacters = response.SourceCharacters,
                    EstimatedInputTokens = response.EstimatedInputTokens,
                    TimeoutSeconds = response.TimeoutSeconds
                }, false, false, 1);
                return null;
            }
            int charCount = texts.Sum(text => (text ?? string.Empty).Length);
            AutoTranslatorMod.Settings.SessionCharCount += charCount;
            AutoTranslatorMod.Settings.TotalCharCount += charCount;
            return translations;
        }

        private static void LogDeepSeekRequestFailure(ATC_WebResponse response)
        {
            int statusCode = (int)response.HttpCode;
            string errorText = response.ErrorText ?? string.Empty;
            ATC_Dispatcher.RunOnMainThread(() =>
                Log.Error("[AutoTranslationCore] DeepSeek request failed (HTTP " + statusCode + "): " +
                          errorText + "\nBody: " + response.ResponseBody));
        }
    }
}
