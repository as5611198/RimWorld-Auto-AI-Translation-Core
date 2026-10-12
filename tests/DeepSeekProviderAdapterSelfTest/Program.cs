using AutoTranslator_Core;
using AutoTranslator_Core.Terminology;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace DeepSeekProviderAdapterSelfTest
{
    internal static class Program
    {
        private static int _passed;

        private static int Main()
        {
            try
            {
                Run("official endpoint uses strict beta", TestOfficialStrictRequest);
                Run("custom endpoint uses JSON mode", TestCustomJsonRequest);
                Run("strict response is reordered by stable id", TestStrictResponseParsing);
                Run("JSON response is reordered by stable id", TestJsonResponseParsing);
                Run("duplicate ids are rejected", TestDuplicateIdRejected);
                Run("truncated responses are rejected", TestTruncatedResponseRejected);
                Run("terminology schema and applications are round-tripped", TestTerminologyContract);
                Console.WriteLine("PASS: " + _passed + " DeepSeek provider adapter self-tests");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
        }

        private static void TestOfficialStrictRequest()
        {
            DeepSeekPreparedRequest request = DeepSeekProviderAdapter.BuildTranslationRequest(
                DeepSeekProviderAdapter.OfficialBaseUrl,
                "deepseek-v4-flash",
                "Translate the input.",
                new[] { "Alpha", "Beta" },
                2048,
                true);

            AssertEqual("https://api.deepseek.com/beta/chat/completions", request.Url, "strict URL");
            AssertEqual(DeepSeekResponseMode.StrictFunctionCall, request.ResponseMode, "response mode");

            JObject payload = JObject.Parse(request.JsonPayload);
            AssertEqual("disabled", payload["thinking"]?["type"]?.ToString(), "thinking must be disabled");
            AssertEqual(DeepSeekProviderAdapter.TranslationFunctionName,
                payload["tool_choice"]?["function"]?["name"]?.ToString(), "forced tool name");
            AssertEqual("True", payload["tools"]?[0]?["function"]?["strict"]?.ToString(), "strict flag");
            AssertEqual("False",
                payload["tools"]?[0]?["function"]?["parameters"]?["additionalProperties"]?.ToString(),
                "root additionalProperties");
            AssertTrue(payload["response_format"] == null, "strict request must not mix response_format");
        }

        private static void TestCustomJsonRequest()
        {
            DeepSeekPreparedRequest request = DeepSeekProviderAdapter.BuildTranslationRequest(
                "https://proxy.example/v1",
                "deepseek-v4-pro",
                "Translate the input.",
                new[] { "Alpha" },
                1024,
                true);

            AssertEqual("https://proxy.example/v1/chat/completions", request.Url, "custom URL");
            AssertEqual(DeepSeekResponseMode.JsonObject, request.ResponseMode, "custom response mode");
            JObject payload = JObject.Parse(request.JsonPayload);
            AssertEqual("json_object", payload["response_format"]?["type"]?.ToString(), "JSON mode");
            AssertTrue(payload["tools"] == null, "custom endpoint must not assume strict tools");
        }

        private static void TestStrictResponseParsing()
        {
            string arguments = new JObject
            {
                ["translations"] = new JArray
                {
                    new JObject { ["id"] = "1", ["text"] = "乙" },
                    new JObject { ["id"] = "0", ["text"] = "甲" }
                }
            }.ToString(Newtonsoft.Json.Formatting.None);
            string responseJson = new JObject
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
                                        ["name"] = DeepSeekProviderAdapter.TranslationFunctionName,
                                        ["arguments"] = arguments
                                    }
                                }
                            }
                        }
                    }
                },
                ["usage"] = new JObject
                {
                    ["prompt_tokens"] = 100,
                    ["completion_tokens"] = 20,
                    ["total_tokens"] = 120
                }
            }.ToString(Newtonsoft.Json.Formatting.None);

            AssertTrue(DeepSeekProviderAdapter.TryParseTranslationResponse(
                responseJson, 2, DeepSeekResponseMode.StrictFunctionCall,
                out DeepSeekTranslationResponse response, out string error), error);
            AssertEqual("甲", response.Translations[0], "translation 0");
            AssertEqual("乙", response.Translations[1], "translation 1");
            AssertEqual(120L, response.Usage.TotalTokens, "usage total");
        }

        private static void TestJsonResponseParsing()
        {
            string content = "{\"translations\":[{\"id\":\"1\",\"text\":\"B\"},{\"id\":\"0\",\"text\":\"A\"}]}";
            string json = new JObject
            {
                ["choices"] = new JArray
                {
                    new JObject
                    {
                        ["finish_reason"] = "stop",
                        ["message"] = new JObject { ["content"] = content }
                    }
                }
            }.ToString(Newtonsoft.Json.Formatting.None);

            AssertTrue(DeepSeekProviderAdapter.TryParseTranslationResponse(
                json, 2, DeepSeekResponseMode.JsonObject,
                out DeepSeekTranslationResponse response, out string error), error);
            AssertEqual("A", response.Translations[0], "translation 0");
            AssertEqual("B", response.Translations[1], "translation 1");
        }

        private static void TestDuplicateIdRejected()
        {
            string content = "{\"translations\":[{\"id\":\"0\",\"text\":\"A\"},{\"id\":\"0\",\"text\":\"B\"}]}";
            string json = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":" +
                Newtonsoft.Json.JsonConvert.ToString(content) + "}}]}";
            AssertFalse(DeepSeekProviderAdapter.TryParseTranslationResponse(
                json, 2, DeepSeekResponseMode.JsonObject, out _, out string error), "duplicate ids");
            AssertTrue(error != null && error.Contains("duplicate"), "duplicate error message");
        }

        private static void TestTruncatedResponseRejected()
        {
            const string json = "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"{}\"}}]}";
            AssertFalse(DeepSeekProviderAdapter.TryParseTranslationResponse(
                json, 1, DeepSeekResponseMode.JsonObject, out _, out string error), "truncated response");
            AssertTrue(error != null && error.Contains("truncated"), "truncated error message");
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
            DeepSeekPreparedRequest request = DeepSeekProviderAdapter.BuildTranslationRequest(
                DeepSeekProviderAdapter.OfficialBaseUrl, "deepseek-v4-flash", "Translate.",
                new[] { "Empire" }, 1024, true, new[] { term });
            JObject payload = JObject.Parse(request.JsonPayload);
            AssertEqual("term-empire",
                payload["tools"]?[0]?["function"]?["parameters"]?["properties"]?["termApplications"]?["items"]?["properties"]?["termId"]?["enum"]?[0]?.ToString(),
                "allowed terminology ID");

            string arguments = "{\"translations\":[{\"id\":\"0\",\"text\":\"帝国\"}]," +
                               "\"termApplications\":[{\"termId\":\"term-empire\",\"sourceForm\":\"Empire\",\"semanticRole\":\"faction\",\"target\":\"帝国\"}]}";
            string responseJson = new JObject
            {
                ["choices"] = new JArray(new JObject
                {
                    ["finish_reason"] = "tool_calls",
                    ["message"] = new JObject
                    {
                        ["tool_calls"] = new JArray(new JObject
                        {
                            ["function"] = new JObject
                            {
                                ["name"] = DeepSeekProviderAdapter.TranslationFunctionName,
                                ["arguments"] = arguments
                            }
                        })
                    }
                })
            }.ToString();
            AssertTrue(DeepSeekProviderAdapter.TryParseTranslationResponse(
                responseJson, 1, DeepSeekResponseMode.StrictFunctionCall,
                out DeepSeekTranslationResponse response, out string error), error);
            AssertEqual("term-empire", response.TermApplications[0].TermId, "application ID");
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
