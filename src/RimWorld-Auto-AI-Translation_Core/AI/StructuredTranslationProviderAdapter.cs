using System;
namespace AutoTranslator_Core
{
    internal enum StructuredTranslationMode { PromptOnly, JsonObject, JsonSchema, GeminiSchema }
    internal static class StructuredTranslationProviderAdapter
    {
        internal static StructuredTranslationMode ResolveMode(ApiKeyConfig config)
        {
            if (config == null) return StructuredTranslationMode.PromptOnly;
            if (config.StructuredOutput != StructuredOutputPreference.Auto)
            {
                if (config.Provider == TranslatorProvider.Google &&
                    config.StructuredOutput == StructuredOutputPreference.JsonSchema)
                    return StructuredTranslationMode.GeminiSchema;
                if (config.StructuredOutput == StructuredOutputPreference.JsonSchema)
                    return StructuredTranslationMode.JsonSchema;
                if (config.StructuredOutput == StructuredOutputPreference.JsonObject)
                    return StructuredTranslationMode.JsonObject;
                return StructuredTranslationMode.PromptOnly;
            }

            switch (config.Provider)
            {
                case TranslatorProvider.Google:
                    return StructuredTranslationMode.GeminiSchema;
                case TranslatorProvider.Grok:
                    return StructuredTranslationMode.JsonSchema;
                case TranslatorProvider.OpenAI:
                    return OpenAiModelSupportsJsonSchema(config.SelectedModel)
                        ? StructuredTranslationMode.JsonSchema
                        : StructuredTranslationMode.JsonObject;
                case TranslatorProvider.OpenRouter:
                    if (config.ModelSupportsParameter(config.SelectedModel, "structured_outputs"))
                        return StructuredTranslationMode.JsonSchema;
                    if (config.ModelSupportsParameter(config.SelectedModel, "response_format"))
                        return StructuredTranslationMode.JsonObject;
                    return StructuredTranslationMode.PromptOnly;
                case TranslatorProvider.GLM:
                case TranslatorProvider.Alibaba:
                case TranslatorProvider.DeepSeek:
                    return StructuredTranslationMode.JsonObject;
                case TranslatorProvider.Custom_OpenAI:
                default:
                    return StructuredTranslationMode.PromptOnly;
            }
        }

        private static bool OpenAiModelSupportsJsonSchema(string model)
        {
            string value = (model ?? string.Empty).Trim().ToLowerInvariant();
            return value.StartsWith("gpt-4o", StringComparison.Ordinal) ||
                   value.StartsWith("gpt-4.1", StringComparison.Ordinal) ||
                   value.StartsWith("gpt-5", StringComparison.Ordinal) ||
                   value.StartsWith("o1", StringComparison.Ordinal) ||
                   value.StartsWith("o3", StringComparison.Ordinal) ||
                   value.StartsWith("o4", StringComparison.Ordinal);
        }
    }
}
