using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core
{
    internal enum TranslatorProvider
    {
        Google,
        OpenAI,
        DeepSeek,
        Grok,
        GLM,
        Alibaba,
        OpenRouter,
        DeepL,
        Custom_OpenAI
    }

    internal sealed class ApiKeyConfig
    {
        public TranslatorProvider Provider;
        public string SelectedModel = string.Empty;
        public StructuredOutputPreference StructuredOutput = StructuredOutputPreference.Auto;
        public Dictionary<string, List<string>> Parameters =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public bool ModelSupportsParameter(string model, string parameter)
        {
            return Parameters.TryGetValue(model ?? string.Empty, out List<string> values) &&
                   values.Any(value => string.Equals(value, parameter, StringComparison.OrdinalIgnoreCase));
        }
    }
}
