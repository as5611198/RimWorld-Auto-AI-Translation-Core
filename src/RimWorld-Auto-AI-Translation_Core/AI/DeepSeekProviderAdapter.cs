using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using AutoTranslator_Core.Terminology;

namespace AutoTranslator_Core
{
    internal enum DeepSeekResponseMode
    {
        JsonObject,
        StrictFunctionCall
    }

    internal sealed class DeepSeekPreparedRequest
    {
        public string Url { get; set; }
        public string JsonPayload { get; set; }
        public DeepSeekResponseMode ResponseMode { get; set; }
    }

    internal sealed class DeepSeekUsage
    {
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public long ReasoningTokens { get; set; }
        public long TotalTokens { get; set; }
        public long CacheHitTokens { get; set; }
        public long CacheMissTokens { get; set; }
    }

    internal sealed class DeepSeekTranslationResponse
    {
        public List<string> Translations { get; set; }
        public string FinishReason { get; set; }
        public DeepSeekUsage Usage { get; set; }
        public List<TerminologyApplication> TermApplications { get; set; }
    }

    /// <summary>
    /// Owns all DeepSeek-specific URL, request-shape and response-shape details.
    /// The translation pipeline only consumes the normalized request and result.
    /// </summary>
    internal static class DeepSeekProviderAdapter
    {
        internal const string OfficialBaseUrl = "https://api.deepseek.com";
        internal const string OfficialBetaBaseUrl = "https://api.deepseek.com/beta";
        internal const string TranslationFunctionName = "submit_translations";

        internal static bool SupportsStrictFunctionCalling(string baseUrl)
        {
            if (!Uri.TryCreate(NormalizeBaseUrl(baseUrl), UriKind.Absolute, out Uri uri))
                return false;

            return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(uri.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase) &&
                   (uri.AbsolutePath == "/" ||
                    uri.AbsolutePath.Equals("/v1", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsolutePath.Equals("/beta", StringComparison.OrdinalIgnoreCase));
        }

        internal static DeepSeekPreparedRequest BuildTranslationRequest(
            string baseUrl,
            string model,
            string systemPrompt,
            IReadOnlyList<string> texts,
            int maxTokens,
            bool useStrictFunctionCalling,
            IReadOnlyList<TerminologyCandidate> terminologyTerms = null)
        {
            if (texts == null) throw new ArgumentNullException(nameof(texts));
            if (texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));

            string normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
            bool strict = useStrictFunctionCalling && SupportsStrictFunctionCalling(normalizedBaseUrl);
            string endpointBase = strict ? OfficialBetaBaseUrl : normalizedBaseUrl;
            JObject payload = BuildCommonPayload(model, systemPrompt, texts, maxTokens, strict, terminologyTerms);

            if (strict)
            {
                JObject parameters = BuildTranslationSchema(texts.Count, terminologyTerms);
                payload["tools"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = TranslationFunctionName,
                            ["description"] = "Return all translated RimWorld strings with their original stable IDs.",
                            ["strict"] = true,
                            ["parameters"] = parameters
                        }
                    }
                };
                payload["tool_choice"] = new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject { ["name"] = TranslationFunctionName }
                };
            }
            else
            {
                payload["response_format"] = new JObject { ["type"] = "json_object" };
            }

            return new DeepSeekPreparedRequest
            {
                Url = endpointBase.TrimEnd('/') + "/chat/completions",
                JsonPayload = payload.ToString(Formatting.None),
                ResponseMode = strict ? DeepSeekResponseMode.StrictFunctionCall : DeepSeekResponseMode.JsonObject
            };
        }

        internal static bool TryParseTranslationResponse(
            string json,
            int expectedCount,
            DeepSeekResponseMode responseMode,
            out DeepSeekTranslationResponse response,
            out string error)
        {
            response = null;
            error = null;

            try
            {
                JObject root = JObject.Parse(json ?? string.Empty);
                JToken choice = root["choices"]?[0];
                if (choice == null)
                    return Fail("DeepSeek response does not contain choices[0].", out error);

                string finishReason = choice["finish_reason"]?.ToString();
                if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                    return Fail("DeepSeek response was truncated because the output token limit was reached.", out error);

                string structuredJson;
                if (responseMode == DeepSeekResponseMode.StrictFunctionCall)
                {
                    JToken toolCall = choice["message"]?["tool_calls"]?
                        .FirstOrDefault(x => string.Equals(
                            x?["function"]?["name"]?.ToString(),
                            TranslationFunctionName,
                            StringComparison.Ordinal));
                    structuredJson = toolCall?["function"]?["arguments"]?.ToString();
                    if (string.IsNullOrWhiteSpace(structuredJson))
                        return Fail("DeepSeek strict response does not contain submit_translations arguments.", out error);
                }
                else
                {
                    structuredJson = choice["message"]?["content"]?.ToString();
                    if (string.IsNullOrWhiteSpace(structuredJson))
                        return Fail("DeepSeek JSON response content is empty.", out error);
                }

                if (!TryParseTranslations(structuredJson, expectedCount, out List<string> translations, out List<TerminologyApplication> applications, out error))
                    return false;

                response = new DeepSeekTranslationResponse
                {
                    Translations = translations,
                    FinishReason = finishReason,
                    Usage = ParseUsage(root["usage"]),
                    TermApplications = applications
                };
                return true;
            }
            catch (Exception ex)
            {
                error = "Invalid DeepSeek response: " + ex.Message;
                return false;
            }
        }

        private static JObject BuildCommonPayload(
            string model,
            string systemPrompt,
            IReadOnlyList<string> texts,
            int maxTokens,
            bool strict,
            IReadOnlyList<TerminologyCandidate> terminologyTerms)
        {
            JArray items = new JArray();
            for (int i = 0; i < texts.Count; i++)
            {
                items.Add(new JObject
                {
                    ["id"] = i.ToString(),
                    ["source"] = texts[i] ?? string.Empty
                });
            }

            List<TerminologyCandidate> terms = (terminologyTerms ?? Array.Empty<TerminologyCandidate>()).Where(term => term != null).ToList();
            bool includeTermApplications = terms.Count > 0;
            string contract = strict
                ? "DeepSeek provider contract: translate every source value and call submit_translations exactly once. Preserve every id exactly. The function arguments are the only output."
                : "DeepSeek provider contract: return only one JSON object containing the required schema fields. Translate every source value, preserve every id exactly, and include no prose.";
            if (includeTermApplications)
                contract += " Also return termApplications for every supplied terminology mapping actually used; otherwise return an empty termApplications array.";

            JObject input = new JObject { ["items"] = items };
            if (includeTermApplications)
                input["terminology"] = new JArray(terms.Select(term => new JObject
                {
                    ["termId"] = term.TermId,
                    ["sourceForm"] = term.SourceForm,
                    ["target"] = term.Target,
                    ["semanticRole"] = term.SemanticRole ?? string.Empty
                }));

            return new JObject
            {
                ["model"] = string.IsNullOrWhiteSpace(model) ? "deepseek-v4-flash" : model,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = (systemPrompt ?? string.Empty) + "\n\n" + contract
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = input.ToString(Formatting.None)
                    }
                },
                ["max_tokens"] = Math.Max(1, maxTokens),
                ["temperature"] = 0.2,
                ["thinking"] = new JObject { ["type"] = "disabled" }
            };
        }

        private static JObject BuildTranslationSchema(int count, IReadOnlyList<TerminologyCandidate> terminologyTerms)
        {
            JArray allowedIds = new JArray();
            for (int i = 0; i < count; i++) allowedIds.Add(i.ToString());

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
                            ["id"] = new JObject { ["type"] = "string", ["enum"] = allowedIds },
                            ["text"] = new JObject { ["type"] = "string" }
                        },
                        ["required"] = new JArray("id", "text"),
                        ["additionalProperties"] = false
                    }
                }
            };
            var required = new JArray("translations");
            List<string> termIds = (terminologyTerms ?? Array.Empty<TerminologyCandidate>())
                .Where(term => term != null).Select(term => term.TermId).Distinct().ToList();
            if (termIds.Count > 0)
            {
                properties["termApplications"] = BuildTermApplicationsSchema(termIds);
                required.Add("termApplications");
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            };
        }

        private static JObject BuildTermApplicationsSchema(List<string> ids)
        {
            return new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["termId"] = new JObject { ["type"] = "string", ["enum"] = new JArray(ids) },
                        ["sourceForm"] = new JObject { ["type"] = "string" },
                        ["semanticRole"] = new JObject { ["type"] = "string" },
                        ["target"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray("termId", "sourceForm", "semanticRole", "target"),
                    ["additionalProperties"] = false
                }
            };
        }

        private static bool TryParseTranslations(
            string structuredJson,
            int expectedCount,
            out List<string> translations,
            out List<TerminologyApplication> termApplications,
            out string error)
        {
            translations = null;
            termApplications = new List<TerminologyApplication>();
            error = null;
            JObject payload = JObject.Parse(structuredJson);
            JArray items = payload["translations"] as JArray;
            if (items == null)
                return Fail("DeepSeek output does not contain a translations array.", out error);
            if (items.Count != expectedCount)
                return Fail($"DeepSeek returned {items.Count} items; expected {expectedCount}.", out error);

            var byId = new Dictionary<int, string>();
            foreach (JToken item in items)
            {
                if (!(item is JObject obj))
                    return Fail("DeepSeek returned a non-object translation item.", out error);

                if (!int.TryParse(obj["id"]?.ToString(), out int id) || id < 0 || id >= expectedCount)
                    return Fail("DeepSeek returned an unknown translation id.", out error);
                if (byId.ContainsKey(id))
                    return Fail("DeepSeek returned a duplicate translation id: " + id, out error);
                JToken textToken = obj["text"];
                if (textToken is JObject textObject)
                {
                    textToken = textObject["text"] ??
                                textObject["translation"] ??
                                textObject["value"];
                }
                if (textToken == null || textToken.Type == JTokenType.Null ||
                    textToken.Type == JTokenType.Object || textToken.Type == JTokenType.Array)
                    return Fail("DeepSeek returned a translation without string text: " + id, out error);

                // Some OpenAI-compatible gateways serialize a scalar translation as a
                // number or boolean despite the strict schema. It is still an unambiguous
                // textual value, so normalize that scalar instead of discarding the whole
                // otherwise complete batch.
                byId.Add(id, textToken.ToString());
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
                        return Fail("DeepSeek returned an invalid termApplications item.", out error);
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

        private static DeepSeekUsage ParseUsage(JToken usage)
        {
            return new DeepSeekUsage
            {
                PromptTokens = ReadLong(usage?["prompt_tokens"]),
                CompletionTokens = ReadLong(usage?["completion_tokens"]),
                ReasoningTokens = ReadLong(usage?["completion_tokens_details"]?["reasoning_tokens"]),
                TotalTokens = ReadLong(usage?["total_tokens"]),
                CacheHitTokens = ReadLong(usage?["prompt_cache_hit_tokens"]),
                CacheMissTokens = ReadLong(usage?["prompt_cache_miss_tokens"])
            };
        }

        private static long ReadLong(JToken token)
        {
            return token != null && long.TryParse(token.ToString(), out long value) ? Math.Max(0L, value) : 0L;
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            string value = string.IsNullOrWhiteSpace(baseUrl) ? OfficialBaseUrl : baseUrl.Trim();
            return value.TrimEnd('/');
        }

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
