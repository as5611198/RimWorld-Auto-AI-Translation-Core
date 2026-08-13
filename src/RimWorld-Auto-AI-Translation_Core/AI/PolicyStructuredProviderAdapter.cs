using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core
{
    internal enum PolicyStructuredMode
    {
        PromptOnly,
        JsonObject,
        JsonSchema,
        GeminiSchema,
        DeepSeekFunction
    }

    internal sealed class PolicyStructuredPreparedRequest
    {
        public string Url { get; set; }
        public string JsonPayload { get; set; }
        public PolicyStructuredMode Mode { get; set; }
    }

    internal static class PolicyStructuredProviderAdapter
    {
        internal const string FunctionName = "submit_policy_decisions";

        internal static PolicyStructuredMode ResolveMode(ApiKeyConfig config, string baseUrl)
        {
            if (config == null) return PolicyStructuredMode.PromptOnly;

            if (config.Provider == TranslatorProvider.DeepSeek)
            {
                if (config.StructuredOutput == StructuredOutputPreference.PromptOnly)
                    return PolicyStructuredMode.PromptOnly;
                if (config.StructuredOutput == StructuredOutputPreference.JsonObject)
                    return PolicyStructuredMode.JsonObject;
                if (DeepSeekProviderAdapter.SupportsStrictFunctionCalling(baseUrl))
                    return PolicyStructuredMode.DeepSeekFunction;
                return config.StructuredOutput == StructuredOutputPreference.JsonSchema
                    ? PolicyStructuredMode.JsonSchema
                    : PolicyStructuredMode.JsonObject;
            }

            StructuredTranslationMode mode = StructuredTranslationProviderAdapter.ResolveMode(config);
            switch (mode)
            {
                case StructuredTranslationMode.JsonObject:
                    return PolicyStructuredMode.JsonObject;
                case StructuredTranslationMode.JsonSchema:
                    return PolicyStructuredMode.JsonSchema;
                case StructuredTranslationMode.GeminiSchema:
                    return PolicyStructuredMode.GeminiSchema;
                default:
                    return PolicyStructuredMode.PromptOnly;
            }
        }

        internal static PolicyStructuredPreparedRequest BuildRequest(
            ApiKeyConfig config,
            string baseUrl,
            string apiKey,
            string systemPrompt,
            string userJson,
            IReadOnlyCollection<string> expectedIds,
            int maximumOutputTokens)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            List<string> ids = (expectedIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            if (ids.Count == 0) throw new ArgumentException("At least one policy group ID is required.", nameof(expectedIds));

            string normalizedBase = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            string model = string.IsNullOrWhiteSpace(config.SelectedModel) ? "local-model" : config.SelectedModel.Trim();
            PolicyStructuredMode mode = ResolveMode(config, normalizedBase);

            if (config.Provider == TranslatorProvider.Google)
            {
                JObject generationConfig = new JObject
                {
                    ["maxOutputTokens"] = Math.Max(1, maximumOutputTokens),
                    ["responseMimeType"] = "application/json"
                };
                if (mode == PolicyStructuredMode.GeminiSchema)
                    generationConfig["responseSchema"] = BuildGeminiSchema(ids);

                return new PolicyStructuredPreparedRequest
                {
                    Url = normalizedBase + "/models/" + model + ":generateContent?key=" + apiKey,
                    JsonPayload = new JObject
                    {
                        ["contents"] = new JArray
                        {
                            new JObject
                            {
                                ["parts"] = new JArray
                                {
                                    new JObject { ["text"] = (systemPrompt ?? string.Empty) + "\n\nInput JSON:\n" + (userJson ?? string.Empty) }
                                }
                            }
                        },
                        ["generationConfig"] = generationConfig
                    }.ToString(Formatting.None),
                    Mode = mode
                };
            }

            JObject payload = new JObject
            {
                ["model"] = model,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = systemPrompt ?? string.Empty },
                    new JObject { ["role"] = "user", ["content"] = userJson ?? string.Empty }
                },
                ["max_tokens"] = Math.Max(1, maximumOutputTokens)
            };

            string endpointBase = normalizedBase;
            if (mode == PolicyStructuredMode.DeepSeekFunction)
            {
                endpointBase = DeepSeekProviderAdapter.OfficialBetaBaseUrl;
                payload["thinking"] = new JObject { ["type"] = "disabled" };
                payload["tools"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = FunctionName,
                            ["description"] = "Return one policy decision for every supplied stable group ID.",
                            ["strict"] = true,
                            ["parameters"] = BuildJsonSchema(ids)
                        }
                    }
                };
                payload["tool_choice"] = new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject { ["name"] = FunctionName }
                };
            }
            else if (mode == PolicyStructuredMode.JsonSchema)
            {
                payload["response_format"] = new JObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JObject
                    {
                        ["name"] = "rimworld_translation_policy",
                        ["strict"] = true,
                        ["schema"] = BuildJsonSchema(ids)
                    }
                };
                if (config.Provider == TranslatorProvider.OpenRouter)
                    payload["provider"] = new JObject { ["require_parameters"] = true };
            }
            else if (mode == PolicyStructuredMode.JsonObject)
            {
                payload["response_format"] = new JObject { ["type"] = "json_object" };
            }

            return new PolicyStructuredPreparedRequest
            {
                Url = endpointBase + "/chat/completions",
                JsonPayload = payload.ToString(Formatting.None),
                Mode = mode
            };
        }

        internal static bool TryExtractDecisionArray(
            JObject envelope,
            TranslatorProvider provider,
            PolicyStructuredMode mode,
            out string rawDecisionArray,
            out string finishReason)
        {
            rawDecisionArray = string.Empty;
            finishReason = string.Empty;
            if (envelope == null) return false;

            string raw;
            if (provider == TranslatorProvider.Google)
            {
                finishReason = envelope["candidates"]?[0]?["finishReason"]?.ToString() ?? string.Empty;
                JArray parts = envelope["candidates"]?[0]?["content"]?["parts"] as JArray;
                raw = parts?
                    .OfType<JObject>()
                    .Where(part => part["thought"]?.Type != JTokenType.Boolean || !part["thought"].Value<bool>())
                    .Select(part => part["text"]?.ToString())
                    .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));
            }
            else
            {
                JToken choice = envelope["choices"]?[0];
                finishReason = choice?["finish_reason"]?.ToString() ?? string.Empty;
                if (mode == PolicyStructuredMode.DeepSeekFunction)
                {
                    raw = choice?["message"]?["tool_calls"]?
                        .FirstOrDefault(call => string.Equals(
                            call?["function"]?["name"]?.ToString(),
                            FunctionName,
                            StringComparison.Ordinal))?["function"]?["arguments"]?.ToString();
                }
                else
                {
                    raw = choice?["message"]?["content"]?.ToString();
                }
            }

            if (string.IsNullOrWhiteSpace(raw)) return false;
            try
            {
                JToken root = JToken.Parse(raw.Trim());
                JArray decisions = root as JArray ?? (root as JObject)?["decisions"] as JArray;
                if (decisions == null) return false;
                rawDecisionArray = decisions.ToString(Formatting.None);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static JObject BuildJsonSchema(IReadOnlyCollection<string> expectedIds)
        {
            JArray ids = new JArray(expectedIds.OrderBy(id => id, StringComparer.Ordinal));
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["decisions"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = BuildDecisionItemSchema("string", ids, false)
                    }
                },
                ["required"] = new JArray("decisions"),
                ["additionalProperties"] = false
            };
        }

        private static JObject BuildGeminiSchema(IReadOnlyCollection<string> expectedIds)
        {
            JArray ids = new JArray(expectedIds.OrderBy(id => id, StringComparer.Ordinal));
            return new JObject
            {
                ["type"] = "OBJECT",
                ["properties"] = new JObject
                {
                    ["decisions"] = new JObject
                    {
                        ["type"] = "ARRAY",
                        ["items"] = BuildDecisionItemSchema("STRING", ids, true)
                    }
                },
                ["required"] = new JArray("decisions"),
                ["propertyOrdering"] = new JArray("decisions")
            };
        }

        private static JObject BuildDecisionItemSchema(string stringType, JArray ids, bool gemini)
        {
            JObject schema = new JObject
            {
                ["type"] = gemini ? "OBJECT" : "object",
                ["properties"] = new JObject
                {
                    ["id"] = new JObject { ["type"] = stringType, ["enum"] = ids.DeepClone() },
                    ["decision"] = new JObject
                    {
                        ["type"] = stringType,
                        ["enum"] = new JArray("allow", "deny", "review")
                    },
                    ["reason"] = new JObject { ["type"] = stringType }
                },
                ["required"] = new JArray("id", "decision", "reason")
            };
            if (gemini)
                schema["propertyOrdering"] = new JArray("id", "decision", "reason");
            else
                schema["additionalProperties"] = false;
            return schema;
        }
    }
}
