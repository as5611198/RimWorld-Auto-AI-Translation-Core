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
        private static async Task<List<string>> TranslateStructuredProviderBatchAsync(
            List<string> texts,
            ApiKeyConfig config,
            bool suppressFinalParseError)
        {
            string apiKey = CleanInput(config.Key);
            string model = CleanInput(config.SelectedModel);
            string baseUrl = GetBaseUrl(config);
            StructuredTranslationPreparedRequest request = StructuredTranslationProviderAdapter.BuildRequest(
                config,
                baseUrl,
                apiKey,
                GetSystemPrompt(AutoTranslatorMod.Settings.TargetLang),
                texts,
                4096,
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
                    config.Provider,
                    timeoutSeconds),
                config);
            if (response == null) return null;
            if (AutoTranslatorSettings.IsCancellationRequested && !response.IsSuccess) return null;
            if (response.BudgetDenied) return null;
            if (!response.IsSuccess)
            {
                LogStructuredProviderRequestFailure(config.Provider, response);
                return null;
            }

            if (texts.Count == 1 && texts[0] == "Connection Test")
                return new List<string> { "Connection OK" };

            if (!StructuredTranslationProviderAdapter.TryParseResponse(
                    response.ResponseBody,
                    texts.Count,
                    request.UsesGoogleEnvelope,
                    out List<string> parsed,
                    out List<TerminologyApplication> termApplications,
                    out string error))
            {
                TranslationFailureDiagnosticPolicy.LogInvalidResponseForDeveloper(
                    config.Provider + "/" + request.Mode,
                    error,
                    response.ResponseBody);
                RecordLastTranslationFailure(config, new ATC_WebResponse
                {
                    HttpCode = response.HttpCode,
                    ErrorText = "Structured response validation failed: " + error,
                    ResponseBody = response.ResponseBody,
                    FailureKind = TranslationRequestFailureKind.InvalidResponse,
                    FailureStage = "ResponseParsing",
                    ItemCount = response.ItemCount,
                    SourceCharacters = response.SourceCharacters,
                    EstimatedInputTokens = response.EstimatedInputTokens,
                    TimeoutSeconds = response.TimeoutSeconds
                }, false, false, 1);
                Log.Warning("[AutoTranslationCore] Structured response validation failed [" +
                            config.Provider + "/" + request.Mode + "]: " + error);
                return null;
            }

            List<string> translations = NormalizeBatchForTargetLanguage(
                parsed,
                AutoTranslatorMod.Settings.TargetLang);
            TerminologyApplicationValidationResult termValidation = TerminologyApplicationValidator.Validate(
                TerminologyRequestTerms.Value,
                texts,
                translations,
                termApplications);
            if (!termValidation.IsValid)
            {
                TranslationFailureDiagnosticPolicy.LogInvalidResponseForDeveloper(
                    config.Provider + "/" + request.Mode,
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

        private static void LogStructuredProviderRequestFailure(
            TranslatorProvider provider,
            ATC_WebResponse response)
        {
            int statusCode = (int)response.HttpCode;
            string errorText = response.ErrorText ?? string.Empty;
            ATC_Dispatcher.RunOnMainThread(() =>
                Log.Error("[AutoTranslationCore] Structured provider request failed [" + provider +
                          "] (HTTP " + statusCode + "): " + errorText + "\nBody: " + response.ResponseBody));
        }
    }
}
