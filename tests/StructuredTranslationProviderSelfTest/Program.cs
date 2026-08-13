using AutoTranslator_Core;
using AutoTranslator_Core.Terminology;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace StructuredTranslationProviderSelfTest
{
    internal static class Program
    {
        private static int _passed;

        private static int Main()
        {
            try
            {
                Run("Gemini uses native response schema", TestGeminiSchema);
                Run("modern OpenAI uses strict JSON schema", TestOpenAiSchema);
                Run("legacy OpenAI uses JSON object", TestLegacyOpenAiJsonObject);
                Run("xAI uses strict JSON schema", TestGrokSchema);
                Run("Alibaba and GLM use JSON object", TestJsonObjectProviders);
                Run("custom endpoint is prompt-only by default", TestCustomPromptOnly);
                Run("custom endpoint permits explicit override", TestCustomOverride);
                Run("OpenRouter negotiates model capabilities", TestOpenRouterCapabilities);
                Run("stable IDs reorder provider output", TestResponseReordering);
                Run("duplicate and truncated output is rejected", TestResponseRejection);
                Run("terminology schema and applications are round-tripped", TestTerminologyContract);
                Run("Policy Agent uses strict provider schemas", TestPolicySchemas);
                Run("DeepSeek Policy Agent uses official strict function calling", TestDeepSeekPolicyFunction);
                Run("Policy wrapper is normalized to the validated decision array", TestPolicyResponseExtraction);
                Console.WriteLine("PASS: " + _passed + " structured provider self-tests");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
        }

        private static void TestGeminiSchema()
        {
            ApiKeyConfig config = Config(TranslatorProvider.Google, "gemini-2.5-flash");
            StructuredTranslationPreparedRequest request = Build(config, "https://google.example/v1beta");
            JObject payload = JObject.Parse(request.JsonPayload);
            AssertEqual(StructuredTranslationMode.GeminiSchema, request.Mode, "mode");
            AssertEqual("application/json", payload["generationConfig"]?["responseMimeType"]?.ToString(), "mime type");
            AssertEqual("OBJECT", payload["generationConfig"]?["responseSchema"]?["type"]?.ToString(), "schema type");
            AssertTrue(request.UsesGoogleEnvelope, "Google envelope");
        }

        private static void TestOpenAiSchema()
        {
            StructuredTranslationPreparedRequest request = Build(
                Config(TranslatorProvider.OpenAI, "gpt-5-mini"),
                "https://api.openai.com/v1");
            JObject payload = JObject.Parse(request.JsonPayload);
            AssertEqual("json_schema", payload["response_format"]?["type"]?.ToString(), "schema mode");
            AssertEqual("True", payload["response_format"]?["json_schema"]?["strict"]?.ToString(), "strict");
        }

        private static void TestLegacyOpenAiJsonObject()
        {
            StructuredTranslationPreparedRequest request = Build(
                Config(TranslatorProvider.OpenAI, "gpt-3.5-turbo"),
                "https://api.openai.com/v1");
            AssertEqual("json_object", JObject.Parse(request.JsonPayload)["response_format"]?["type"]?.ToString(), "JSON mode");
        }

        private static void TestGrokSchema()
        {
            StructuredTranslationPreparedRequest request = Build(
                Config(TranslatorProvider.Grok, "grok-4.5"),
                "https://api.x.ai/v1");
            AssertEqual(StructuredTranslationMode.JsonSchema, request.Mode, "xAI mode");
        }

        private static void TestJsonObjectProviders()
        {
            foreach (TranslatorProvider provider in new[] { TranslatorProvider.Alibaba, TranslatorProvider.GLM })
            {
                StructuredTranslationPreparedRequest request = Build(Config(provider, "model"), "https://provider.example/v1");
                AssertEqual(StructuredTranslationMode.JsonObject, request.Mode, provider + " mode");
            }
        }

        private static void TestCustomPromptOnly()
        {
            StructuredTranslationPreparedRequest request = Build(
                Config(TranslatorProvider.Custom_OpenAI, "local"),
                "http://localhost:1234/v1");
            AssertEqual(StructuredTranslationMode.PromptOnly, request.Mode, "custom mode");
            AssertTrue(JObject.Parse(request.JsonPayload)["response_format"] == null, "no unsupported parameter");
        }

        private static void TestCustomOverride()
        {
            ApiKeyConfig config = Config(TranslatorProvider.Custom_OpenAI, "local");
            config.StructuredOutput = StructuredOutputPreference.JsonSchema;
            AssertEqual(StructuredTranslationMode.JsonSchema,
                Build(config, "http://localhost:1234/v1").Mode,
                "manual schema override");
        }

        private static void TestOpenRouterCapabilities()
        {
            ApiKeyConfig config = Config(TranslatorProvider.OpenRouter, "vendor/model");
            AssertEqual(StructuredTranslationMode.PromptOnly,
                StructuredTranslationProviderAdapter.ResolveMode(config), "unknown model");
            config.Parameters[config.SelectedModel] = new List<string> { "response_format" };
            AssertEqual(StructuredTranslationMode.JsonObject,
                StructuredTranslationProviderAdapter.ResolveMode(config), "JSON mode model");
            config.Parameters[config.SelectedModel].Add("structured_outputs");
            StructuredTranslationPreparedRequest request = Build(config, "https://openrouter.ai/api/v1");
            AssertEqual(StructuredTranslationMode.JsonSchema, request.Mode, "schema model");
            AssertEqual("True", JObject.Parse(request.JsonPayload)["provider"]?["require_parameters"]?.ToString(), "required routing");
        }

        private static void TestResponseReordering()
        {
            string content = "{\"translations\":[{\"id\":\"1\",\"text\":\"乙\"},{\"id\":\"0\",\"text\":\"甲\"}]}";
            string response = new JObject
            {
                ["choices"] = new JArray
                {
                    new JObject
                    {
                        ["finish_reason"] = "stop",
                        ["message"] = new JObject { ["content"] = content }
                    }
                }
            }.ToString();
            AssertTrue(StructuredTranslationProviderAdapter.TryParseResponse(
                response, 2, false, out List<string> translations, out string error), error);
            AssertEqual("甲", translations[0], "id 0");
            AssertEqual("乙", translations[1], "id 1");
        }

        private static void TestResponseRejection()
        {
            const string duplicate = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{\\\"translations\\\":[{\\\"id\\\":\\\"0\\\",\\\"text\\\":\\\"A\\\"},{\\\"id\\\":\\\"0\\\",\\\"text\\\":\\\"B\\\"}]}\"}}]}";
            AssertFalse(StructuredTranslationProviderAdapter.TryParseResponse(
                duplicate, 2, false, out _, out _), "duplicate IDs");
            const string truncated = "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"{}\"}}]}";
            AssertFalse(StructuredTranslationProviderAdapter.TryParseResponse(
                truncated, 1, false, out _, out _), "truncated output");
        }

        private static void TestTerminologyContract()
        {
            var term = new TerminologyCandidate
            {
                TermId = "term-empire",
                SourceForm = "Empire",
                Target = "帝国",
                SemanticRole = "faction"
            };
            StructuredTranslationPreparedRequest request = StructuredTranslationProviderAdapter.BuildRequest(
                Config(TranslatorProvider.OpenAI, "gpt-5-mini"),
                "https://api.openai.com/v1", "key", "Translate safely.",
                new[] { "Empire" }, 1000, new[] { term });
            JObject payload = JObject.Parse(request.JsonPayload);
            AssertEqual("term-empire",
                payload["response_format"]?["json_schema"]?["schema"]?["properties"]?["termApplications"]?["items"]?["properties"]?["termId"]?["enum"]?[0]?.ToString(),
                "allowed terminology ID");

            string content = "{\"translations\":[{\"id\":\"0\",\"text\":\"帝国\"}]," +
                             "\"termApplications\":[{\"termId\":\"term-empire\",\"sourceForm\":\"Empire\",\"semanticRole\":\"faction\",\"target\":\"帝国\"}]}";
            string response = new JObject
            {
                ["choices"] = new JArray(new JObject
                {
                    ["finish_reason"] = "stop",
                    ["message"] = new JObject { ["content"] = content }
                })
            }.ToString();
            AssertTrue(StructuredTranslationProviderAdapter.TryParseResponse(
                response, 1, false, out List<string> translations,
                out List<TerminologyApplication> applications, out string error), error);
            AssertEqual("帝国", translations[0], "translation");
            AssertEqual("term-empire", applications[0].TermId, "application ID");
            AssertTrue(TerminologyApplicationValidator.Validate(
                new[] { term }, new[] { "Empire" }, translations, applications).IsValid,
                "terminology validation");
            AssertEqual("missing_term_application", TerminologyApplicationValidator.Validate(
                new[] { term }, new[] { "Empire" }, translations,
                new List<TerminologyApplication>()).ErrorCode, "missing application");
        }

        private static void TestPolicySchemas()
        {
            ApiKeyConfig config = Config(TranslatorProvider.OpenAI, "gpt-5-mini");
            PolicyStructuredPreparedRequest openAi = BuildPolicy(config, "https://api.openai.com/v1");
            JObject openAiPayload = JObject.Parse(openAi.JsonPayload);
            AssertEqual(PolicyStructuredMode.JsonSchema, openAi.Mode, "OpenAI policy mode");
            AssertEqual("object", openAiPayload["response_format"]?["json_schema"]?["schema"]?["type"]?.ToString(), "policy schema root");

            PolicyStructuredPreparedRequest gemini = BuildPolicy(
                Config(TranslatorProvider.Google, "gemini-2.5-flash"),
                "https://generativelanguage.googleapis.com/v1beta");
            AssertEqual("OBJECT", JObject.Parse(gemini.JsonPayload)["generationConfig"]?["responseSchema"]?["type"]?.ToString(), "Gemini policy schema");
        }

        private static void TestDeepSeekPolicyFunction()
        {
            PolicyStructuredPreparedRequest request = BuildPolicy(
                Config(TranslatorProvider.DeepSeek, "deepseek-chat"),
                DeepSeekProviderAdapter.OfficialBaseUrl);
            JObject payload = JObject.Parse(request.JsonPayload);
            AssertEqual(PolicyStructuredMode.DeepSeekFunction, request.Mode, "DeepSeek policy mode");
            AssertTrue(request.Url.StartsWith(DeepSeekProviderAdapter.OfficialBetaBaseUrl, StringComparison.Ordinal), "beta endpoint");
            AssertEqual("True", payload["tools"]?[0]?["function"]?["strict"]?.ToString(), "strict function");
            AssertEqual(PolicyStructuredProviderAdapter.FunctionName, payload["tool_choice"]?["function"]?["name"]?.ToString(), "forced function");
        }

        private static void TestPolicyResponseExtraction()
        {
            string arguments = "{\"decisions\":[{\"id\":\"group-a\",\"decision\":\"allow\",\"reason\":\"visible text\"}]}";
            JObject envelope = new JObject
            {
                ["choices"] = new JArray
                {
                    new JObject
                    {
                        ["finish_reason"] = "tool_calls",
                        ["message"] = new JObject
                        {
                            ["tool_calls"] = new JArray
                            {
                                new JObject
                                {
                                    ["function"] = new JObject
                                    {
                                        ["name"] = PolicyStructuredProviderAdapter.FunctionName,
                                        ["arguments"] = arguments
                                    }
                                }
                            }
                        }
                    }
                }
            };
            AssertTrue(PolicyStructuredProviderAdapter.TryExtractDecisionArray(
                envelope,
                TranslatorProvider.DeepSeek,
                PolicyStructuredMode.DeepSeekFunction,
                out string array,
                out string finishReason), "extract strict policy result");
            AssertEqual("tool_calls", finishReason, "finish reason");
            AssertEqual(1, JArray.Parse(array).Count, "decision count");
        }

        private static PolicyStructuredPreparedRequest BuildPolicy(ApiKeyConfig config, string baseUrl)
        {
            return PolicyStructuredProviderAdapter.BuildRequest(
                config,
                baseUrl,
                "key",
                "Classify safely.",
                "{\"groups\":[]}",
                new[] { "group-a", "group-b" },
                1000);
        }

        private static StructuredTranslationPreparedRequest Build(ApiKeyConfig config, string baseUrl)
        {
            return StructuredTranslationProviderAdapter.BuildRequest(
                config, baseUrl, "key", "Translate safely.", new[] { "A", "B" }, 1000);
        }

        private static ApiKeyConfig Config(TranslatorProvider provider, string model)
        {
            return new ApiKeyConfig { Provider = provider, SelectedModel = model };
        }

        private static void Run(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS: " + name);
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertFalse(bool condition, string message)
        {
            if (condition) throw new InvalidOperationException(message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(message + ": expected " + expected + ", actual " + actual);
        }
    }
}
