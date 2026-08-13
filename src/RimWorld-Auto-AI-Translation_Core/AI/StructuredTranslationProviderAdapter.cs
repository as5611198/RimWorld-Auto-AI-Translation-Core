using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using AutoTranslator_Core.Terminology;

namespace AutoTranslator_Core
{
    internal enum StructuredTranslationMode
    {
        PromptOnly,
        JsonObject,
        JsonSchema,
        GeminiSchema
    }

    internal sealed class StructuredTranslationPreparedRequest
    {
        public string Url { get; set; }
        public string JsonPayload { get; set; }
        public StructuredTranslationMode Mode { get; set; }
        public bool UsesGoogleEnvelope { get; set; }
    }

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
                    return StructuredTranslationMode.JsonObject;
                case TranslatorProvider.Custom_OpenAI:
                default:
                    return StructuredTranslationMode.PromptOnly;
            }
        }

        internal static StructuredTranslationPreparedRequest BuildRequest(
            ApiKeyConfig config,
            string baseUrl,
            string apiKey,
            string systemPrompt,
            IReadOnlyList<string> texts,
            int maxTokens,
            IReadOnlyList<TerminologyCandidate> terminologyTerms = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (texts == null || texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));

            StructuredTranslationMode mode = ResolveMode(config);
            string model = string.IsNullOrWhiteSpace(config.SelectedModel) ? "local-model" : config.SelectedModel.Trim();
            JArray inputItems = BuildInputItems(texts);
            List<TerminologyCandidate> terms = (terminologyTerms ?? Array.Empty<TerminologyCandidate>()).Where(term => term != null).ToList();
            bool includeTermApplications = terms.Count > 0;
            string contract = "Return exactly one translations item for every input ID. " +
                              "Preserve IDs exactly and return only the structured response; no Markdown or prose." +
                              (includeTermApplications ? " Also return termApplications for every supplied terminology mapping actually used; otherwise return an empty termApplications array." : string.Empty);
            JObject input = new JObject { ["items"] = inputItems };
            if (includeTermApplications)
                input["terminology"] = BuildTerminologyInput(terms);

            if (config.Provider == TranslatorProvider.Google)
            {
                JObject generationConfig = new JObject
                {
                    ["maxOutputTokens"] = Math.Max(1, maxTokens),
                    ["responseMimeType"] = "application/json"
                };
                if (mode == StructuredTranslationMode.GeminiSchema)
                    generationConfig["responseSchema"] = BuildGeminiSchema(texts.Count, terms);

                JObject googlePayload = new JObject
                {
                    ["contents"] = new JArray
                    {
                        new JObject
                        {
                            ["parts"] = new JArray
                            {
                                new JObject
                                {
                                    ["text"] = (systemPrompt ?? string.Empty) + "\n\n" + contract +
                                               "\n\nInput JSON:\n" + input.ToString(Formatting.None)
                                }
                            }
                        }
                    },
                    ["generationConfig"] = generationConfig
                };
                return new StructuredTranslationPreparedRequest
                {
                    Url = baseUrl.TrimEnd('/') + "/models/" + model + ":generateContent?key=" + apiKey,
                    JsonPayload = googlePayload.ToString(Formatting.None),
                    Mode = mode,
                    UsesGoogleEnvelope = true
                };
            }

            JObject payload = new JObject
            {
                ["model"] = model,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = (systemPrompt ?? string.Empty) + "\n\n" + contract },
                    new JObject { ["role"] = "user", ["content"] = input.ToString(Formatting.None) }
                },
                ["max_tokens"] = Math.Max(1, maxTokens)
            };

            if (mode == StructuredTranslationMode.JsonSchema)
            {
                payload["response_format"] = new JObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JObject
                    {
                        ["name"] = "rimworld_translations",
                        ["strict"] = true,
                        ["schema"] = BuildJsonSchema(texts.Count, terms)
                    }
                };
                if (config.Provider == TranslatorProvider.OpenRouter)
                    payload["provider"] = new JObject { ["require_parameters"] = true };
            }
            else if (mode == StructuredTranslationMode.JsonObject)
            {
                payload["response_format"] = new JObject { ["type"] = "json_object" };
            }

            return new StructuredTranslationPreparedRequest
            {
                Url = baseUrl.TrimEnd('/') + "/chat/completions",
                JsonPayload = payload.ToString(Formatting.None),
                Mode = mode,
                UsesGoogleEnvelope = false
            };
        }

        internal static bool TryParseResponse(
            string responseJson,
            int expectedCount,
            bool googleEnvelope,
            out List<string> translations,
            out string error)
        {
            return TryParseResponse(responseJson, expectedCount, googleEnvelope, out translations, out _, out error);
        }

        internal static bool TryParseResponse(
            string responseJson,
            int expectedCount,
            bool googleEnvelope,
            out List<string> translations,
            out List<TerminologyApplication> termApplications,
            out string error)
        {
            translations = null;
            termApplications = new List<TerminologyApplication>();
            error = string.Empty;
            try
            {
                JObject envelope = JObject.Parse(responseJson ?? string.Empty);
                string finishReason = googleEnvelope
                    ? envelope["candidates"]?[0]?["finishReason"]?.ToString()
                    : envelope["choices"]?[0]?["finish_reason"]?.ToString();
                if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Provider response was truncated.";
                    return false;
                }

                string raw = googleEnvelope
                    ? envelope["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString()
                    : envelope["choices"]?[0]?["message"]?["content"]?.ToString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    error = "Provider response content is empty.";
                    return false;
                }

                JObject payload = JObject.Parse(raw);
                JArray items = payload["translations"] as JArray;
                if (items == null || items.Count != expectedCount)
                {
                    error = "Provider returned an incomplete translations collection.";
                    return false;
                }

                Dictionary<int, string> byId = new Dictionary<int, string>();
                foreach (JToken item in items)
                {
                    if (!(item is JObject obj) ||
                        !int.TryParse(obj["id"]?.ToString(), out int id) ||
                        id < 0 || id >= expectedCount ||
                        byId.ContainsKey(id) ||
                        obj["text"]?.Type != JTokenType.String)
                    {
                        error = "Provider returned an invalid, unknown, or duplicate translation ID.";
                        return false;
                    }
                    byId[id] = obj["text"].ToString()
                        .Replace("\\n", "\n")
                        .Replace("\\r", "\r")
                        .Replace("/n", "\n");
                }

                translations = Enumerable.Range(0, expectedCount).Select(id => byId[id]).ToList();
                JArray applications = payload["termApplications"] as JArray;
                if (applications != null)
                {
                    foreach (JToken token in applications)
                    {
                        if (!(token is JObject application) ||
                            application["termId"]?.Type != JTokenType.String ||
                            application["sourceForm"]?.Type != JTokenType.String ||
                            application["semanticRole"]?.Type != JTokenType.String ||
                            application["target"]?.Type != JTokenType.String)
                        {
                            error = "Provider returned an invalid termApplications item.";
                            translations = null;
                            termApplications.Clear();
                            return false;
                        }
                        termApplications.Add(new TerminologyApplication
                        {
                            TermId = application["termId"].ToString(),
                            SourceForm = application["sourceForm"].ToString(),
                            SemanticRole = application["semanticRole"].ToString(),
                            Target = application["target"].ToString()
                        });
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Invalid structured provider response: " + ex.Message;
                return false;
            }
        }

        internal static JObject BuildJsonSchema(int count, IReadOnlyList<TerminologyCandidate> terminologyTerms = null)
        {
            JArray ids = new JArray(Enumerable.Range(0, count).Select(index => index.ToString()));
            var properties = new JObject
            {
                ["translations"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["id"] = new JObject { ["type"] = "string", ["enum"] = ids },
                            ["text"] = new JObject { ["type"] = "string" }
                        },
                        ["required"] = new JArray("id", "text"),
                        ["additionalProperties"] = false
                    }
                }
            };
            var required = new JArray("translations");
            AddJsonTerminologySchema(properties, required, terminologyTerms);
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            };
        }

        private static JObject BuildGeminiSchema(int count, IReadOnlyList<TerminologyCandidate> terminologyTerms)
        {
            JArray ids = new JArray(Enumerable.Range(0, count).Select(index => index.ToString()));
            var properties = new JObject
            {
                ["translations"] = new JObject
                {
                    ["type"] = "ARRAY",
                    ["items"] = new JObject
                    {
                        ["type"] = "OBJECT",
                        ["properties"] = new JObject
                        {
                            ["id"] = new JObject { ["type"] = "STRING", ["enum"] = ids },
                            ["text"] = new JObject { ["type"] = "STRING" }
                        },
                        ["required"] = new JArray("id", "text"),
                        ["propertyOrdering"] = new JArray("id", "text")
                    }
                }
            };
            var required = new JArray("translations");
            AddGeminiTerminologySchema(properties, required, terminologyTerms);
            return new JObject
            {
                ["type"] = "OBJECT",
                ["properties"] = properties,
                ["required"] = required,
                ["propertyOrdering"] = required.DeepClone()
            };
        }

        private static JArray BuildTerminologyInput(IEnumerable<TerminologyCandidate> terms)
        {
            return new JArray(terms.Select(term => new JObject
            {
                ["termId"] = term.TermId,
                ["sourceForm"] = term.SourceForm,
                ["target"] = term.Target,
                ["semanticRole"] = term.SemanticRole ?? string.Empty
            }));
        }

        private static void AddJsonTerminologySchema(JObject properties, JArray required, IReadOnlyList<TerminologyCandidate> terms)
        {
            List<string> ids = (terms ?? Array.Empty<TerminologyCandidate>()).Where(term => term != null).Select(term => term.TermId).Distinct().ToList();
            if (ids.Count == 0) return;
            properties["termApplications"] = BuildTermApplicationsSchema(ids, false);
            required.Add("termApplications");
        }

        private static void AddGeminiTerminologySchema(JObject properties, JArray required, IReadOnlyList<TerminologyCandidate> terms)
        {
            List<string> ids = (terms ?? Array.Empty<TerminologyCandidate>()).Where(term => term != null).Select(term => term.TermId).Distinct().ToList();
            if (ids.Count == 0) return;
            properties["termApplications"] = BuildTermApplicationsSchema(ids, true);
            required.Add("termApplications");
        }

        private static JObject BuildTermApplicationsSchema(List<string> ids, bool gemini)
        {
            string objectType = gemini ? "OBJECT" : "object";
            string arrayType = gemini ? "ARRAY" : "array";
            string stringType = gemini ? "STRING" : "string";
            var itemProperties = new JObject
            {
                ["termId"] = new JObject { ["type"] = stringType, ["enum"] = new JArray(ids) },
                ["sourceForm"] = new JObject { ["type"] = stringType },
                ["semanticRole"] = new JObject { ["type"] = stringType },
                ["target"] = new JObject { ["type"] = stringType }
            };
            var item = new JObject
            {
                ["type"] = objectType,
                ["properties"] = itemProperties,
                ["required"] = new JArray("termId", "sourceForm", "semanticRole", "target")
            };
            if (gemini) item["propertyOrdering"] = new JArray("termId", "sourceForm", "semanticRole", "target");
            else item["additionalProperties"] = false;
            return new JObject { ["type"] = arrayType, ["items"] = item };
        }

        private static JArray BuildInputItems(IReadOnlyList<string> texts)
        {
            JArray items = new JArray();
            for (int i = 0; i < texts.Count; i++)
                items.Add(new JObject { ["id"] = i.ToString(), ["source"] = texts[i] ?? string.Empty });
            return items;
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
