using System.Collections;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static object ThinkingConfig(Assembly mod, string name, string model, string url = "")
    {
        Type type = mod.GetType("AutoTranslator_Core.ApiKeyConfig", true);
        object config = Activator.CreateInstance(type);
        type.GetField("Provider").SetValue(config, Enum.Parse(provider, name));
        type.GetField("SelectedModel").SetValue(config, model);
        type.GetField("CustomBaseUrl").SetValue(config, url);
        type.GetField("Key").SetValue(config, "synthetic-thinking-key");
        type.GetField("Enabled").SetValue(config, true);
        return config;
    }

    private static async Task TestThinkingPolicies(Assembly mod, FixtureServer server)
    {
        Type rules = mod.GetType("AutoTranslator_Core.ApiThinkingPolicy", true);
        MethodInfo prepare = api.GetMethod("PrepareApiRequestPayload", Static);
        JObject input = JObject.Parse("{\"model\":\"preserve-model\",\"messages\":[{\"role\":\"user\",\"content\":\"preserve prompt\"}],\"max_tokens\":4096}");
        JObject Prepared(object config, bool review) => (JObject)prepare.Invoke(null, new[] { input, config, (object)review });
        var cases = new (string Provider, string Model, string Path, object Translation, object Review)[]
        {
            ("MiMo", "mimo-v2.6-flash", "thinking.type", "disabled", "enabled"),
            ("MiMo", "mimo-v2.6-pro", "thinking.type", "disabled", "enabled"),
            ("DeepSeek", "deepseek-flash", "thinking.type", "disabled", "enabled"),
            ("DeepSeek", "deepseek-v4-pro", "thinking.type", "disabled", "enabled"),
            ("GLM", "glm-4.5", "thinking.type", "disabled", "enabled"),
            ("GLM", "glm-4.7", "thinking.type", "disabled", "enabled"),
            ("GLM", "glm-5.2", "thinking.type", "disabled", "enabled"),
            ("GLM", "glm-5.3", "thinking.type", "enabled", "enabled"),
            ("GLM", "glm-5.3-flash", "reasoning_effort", "low", "low"),
            ("Alibaba", "qwen-plus", "enable_thinking", false, true),
            ("Alibaba", "qwen3.8-max", "enable_thinking", false, true),
            ("Alibaba", "qwen3.5-397b-a17b", "enable_thinking", false, true),
            ("Alibaba", "deepseek-v4-pro", "enable_thinking", false, true),
            ("Alibaba", "mimo-v2.5-pro", "enable_thinking", false, true),
            ("Alibaba", "glm-5.2", "enable_thinking", false, true),
            ("OpenAI", "gpt-5.1", "reasoning_effort", "none", "low"),
            ("OpenAI", "gpt-5.2", "reasoning_effort", "none", "low"),
            ("OpenAI", "gpt-5.4-mini", "reasoning_effort", "none", "low"),
            ("OpenAI", "gpt-5.5", "reasoning_effort", "none", "low"),
            ("OpenAI", "gpt-5.6-sol", "reasoning_effort", "none", "low"),
            ("OpenAI", "gpt-5.6-terra", "reasoning_effort", "none", "low"),
            ("OpenAI", "gpt-5.6-luna", "reasoning_effort", "none", "low"),
            ("OpenAI", "gpt-5-mini", "reasoning_effort", "minimal", "low"),
            ("OpenAI", "gpt-6-astra", "reasoning_effort", "low", "low"),
            ("OpenAI", "gpt-6.1-sol", "reasoning_effort", "low", "low"),
            ("OpenAI", "o3-mini", "reasoning_effort", "low", "low"),
            ("Grok", "grok-4.3", "reasoning_effort", "none", "low"),
            ("Grok", "grok-4.7", "reasoning_effort", "low", "low"),
            ("Google", "gemini-2.5-flash", "generationConfig.thinkingConfig.thinkingBudget", 0, 1024),
            ("Google", "gemini-2.5-flash-lite", "generationConfig.thinkingConfig.thinkingBudget", 0, 1024),
            ("Google", "gemini-2.5-pro", "generationConfig.thinkingConfig.thinkingBudget", 128, 1024),
            ("Google", "gemini-3-flash-preview", "generationConfig.thinkingConfig.thinkingLevel", "minimal", "low"),
            ("Google", "gemini-3.6-flash", "generationConfig.thinkingConfig.thinkingLevel", "minimal", "low"),
            ("Google", "gemini-3.8-flash", "generationConfig.thinkingConfig.thinkingLevel", "low", "low"),
            ("Google", "gemini-4-argon", "generationConfig.thinkingConfig.thinkingLevel", "low", "low")
        };
        foreach (var item in cases)
        {
            object config = ThinkingConfig(mod, item.Provider, item.Model);
            JObject translation = Prepared(config, false), review = Prepared(config, true);
            Check(JToken.DeepEquals(translation.SelectToken(item.Path), JToken.FromObject(item.Translation)) &&
                JToken.DeepEquals(review.SelectToken(item.Path), JToken.FromObject(item.Review)),
                item.Provider + "/" + item.Model + " separates translation and review controls");
            Check(JToken.DeepEquals(translation["messages"], input["messages"]) &&
                JToken.DeepEquals(review["messages"], input["messages"]) && (string)review["model"] == "preserve-model",
                item.Model + " preserves prompts, input and selected model");
        }
        Check(input["thinking"] == null && input["reasoning_effort"] == null && input["max_tokens"] != null,
            "thinking preparation never mutates the caller's payload");
        Check((string)Prepared(ThinkingConfig(mod, "GLM", "glm-5.2"), true)["reasoning_effort"] == "high" &&
            Prepared(ThinkingConfig(mod, "GLM", "glm-5.2"), false)["reasoning_effort"] == null,
            "GLM-5.2 uses its lowest actual review effort without conflicting with translation's off switch");
        foreach (var item in new[] { ("DeepSeek", "deepseek-r1"), ("Alibaba", "qwen3-235b-a22b-thinking-2507"),
            ("Alibaba", "qwen3.8-2.4t-a95b"), ("Alibaba", "glm-5.3"), ("OpenAI", "gpt-5.5-pro") })
        {
            JObject output = Prepared(ThinkingConfig(mod, item.Item1, item.Item2), false);
            Check(output["thinking"] == null && output["enable_thinking"] == null && output["reasoning_effort"] == null,
                item.Item2 + " cannot be switched off through an unsupported parameter");
        }
        foreach (var item in new[] { ("OpenAI", "gpt-4o"), ("GLM", "glm-4-flash"), ("GLM", "glm-5.4-future"),
            ("Alibaba", "qwen-plus-2024-11-27"), ("Alibaba", "qwen3-max-2025-09-23"), ("Google", "gemini-2.0-flash"),
            ("Google", "gemini-9-future"), ("OpenAI", "unknown-model") })
        {
            Check(JToken.DeepEquals(Prepared(ThinkingConfig(mod, item.Item1, item.Item2), false), input),
                item.Item2 + " receives no invented thinking controls");
        }
        var official = Prepared(ThinkingConfig(mod, "Custom_OpenAI", "deepseek-flash", "https://api.deepseek.com/v1"), false);
        Check((string)official["thinking"]?["type"] == "disabled", "custom configuration recognizes a known official endpoint");
        foreach (string url in new[] { "http://127.0.0.1:4315", "https://gateway.example/v1", "https://api.deepseek.com.attacker.invalid/v1" })
            Check(JToken.DeepEquals(Prepared(ThinkingConfig(mod, "Custom_OpenAI", "deepseek-flash", url), false), input),
                "unknown custom endpoint is not assigned a guessed vendor protocol: " + url);
        object player2 = ThinkingConfig(mod, "Custom_OpenAI", "deepseek-flash", "http://127.0.0.1:4315");
        player2.GetType().GetField("UsePlayer2Protocol").SetValue(player2, true);
        JObject playerPayload = Prepared(player2, true);
        Check(playerPayload["model"] == null && playerPayload["thinking"] == null && playerPayload["reasoning_effort"] == null,
            "Player2 keeps control of its own model and thinking options");

        object router = ThinkingConfig(mod, "OpenRouter", "fixture/optional");
        string routerUrl = (string)api.GetMethod("GetBaseUrl", Static).Invoke(null, new[] { router });
        string metadata = "{\"data\":[{\"id\":\"fixture/optional\",\"reasoning\":{\"mandatory\":false,\"supported_efforts\":[\"high\",\"low\",\"none\"]}}," +
            "{\"id\":\"fixture/mandatory\",\"reasoning\":{\"mandatory\":true,\"supported_efforts\":[\"high\",\"low\"]}}," +
            "{\"id\":\"fixture/budget\",\"reasoning\":{\"mandatory\":true,\"supports_max_tokens\":true}}," +
            "{\"id\":\"fixture/invalid\",\"reasoning\":{\"mandatory\":\"unexpected\",\"supported_efforts\":[{},null]}},42]}";
        rules.GetMethod("RememberModels", Static).Invoke(null, new[] { router, routerUrl, metadata });
        Check((bool?)Prepared(router, false)["reasoning"]?["enabled"] == false &&
            (string)Prepared(router, true)["reasoning"]?["effort"] == "low", "OpenRouter uses per-model metadata to disable optional thinking only for translation");
        router.GetType().GetField("SelectedModel").SetValue(router, "fixture/mandatory");
        Check((bool?)Prepared(router, false)["reasoning"]?["enabled"] == true &&
            (string)Prepared(router, false)["reasoning"]?["effort"] == "low", "OpenRouter never disables a mandatory reasoning model");
        router.GetType().GetField("SelectedModel").SetValue(router, "fixture/budget");
        Check((int?)Prepared(router, true)["reasoning"]?["max_tokens"] == 1024 &&
            (bool?)Prepared(router, true)["reasoning"]?["enabled"] == true, "OpenRouter review keeps thinking with a supported token budget");
        router.GetType().GetField("SelectedModel").SetValue(router, "fixture/invalid");
        Check(Prepared(router, false)["reasoning"] == null,
            "malformed optional model metadata retains defaults without inventing controls");
        Check(Prepared(ThinkingConfig(mod, "OpenRouter", "unknown/future-model"), false)["reasoning"] == null,
            "OpenRouter models without capability metadata retain their defaults");
        object localRouter = ThinkingConfig(mod, "OpenRouter", "fixture-model", server.Url);
        Type configType = localRouter.GetType();
        api.GetMethod("AutoFetchForConfig", Static, null, new[] { configType, typeof(bool) }, null)
            .Invoke(null, new[] { localRouter, (object)true });
        using (var routerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            while ((bool)configType.GetField("IsFetching").GetValue(localRouter)) await Task.Delay(10, routerTimeout.Token);
        Check((bool?)Prepared(localRouter, false)["reasoning"]?["enabled"] == false &&
            (string)Prepared(localRouter, true)["reasoning"]?["effort"] == "low",
            "actual model-list fetch remembers OpenRouter capabilities for subsequent payloads");

        object settingsObject = Activator.CreateInstance(settings);
        mod.GetType("AutoTranslator_Core.AutoTranslatorMod", true).GetField("Settings", Static).SetValue(null, settingsObject);
        settings.GetField("TargetLang").SetValue(settingsObject, Enum.Parse(mod.GetType("AutoTranslator_Core.TargetLanguage", true), "Traditional"));
        foreach (var item in new[] { ("MiMo", "mimo-v2.6-pro", "thinking.type", "disabled", "enabled"),
            ("DeepSeek", "deepseek-flash", "thinking.type", "disabled", "enabled"),
            ("Alibaba", "qwen3.8-max", "enable_thinking", "False", "True"),
            ("Google", "gemini-2.5-flash", "generationConfig.thinkingConfig.thinkingBudget", "0", "1024") })
        {
            object config = ThinkingConfig(mod, item.Item1, item.Item2, server.Url);
            int before = server.Requests.Count;
            object translated = await Invoke(api, "TranslateBatchCoreAsync", new List<string> { "fixture input" }, config, true, null, null, false);
            Check(translated is List<string> values && values.Count == 1 && values[0] == "測試譯文",
                item.Item1 + " actual translation parses final text without thinking content");
            JObject sent = JObject.Parse(Encoding.UTF8.GetString(server.Requests.Skip(before).Single().Body));
            Check(sent.SelectToken(item.Item3)?.ToString() == item.Item4, item.Item1 + " translation wire payload contains the off control");
            before = server.Requests.Count;
            object reviewed = await Invoke(api, "ExecuteStructuredChatAttemptAsync", "fixture-review", "[]", 6144, config, null);
            JObject reviewSent = JObject.Parse(Encoding.UTF8.GetString(server.Requests.Skip(before).Single().Body));
            Check((bool)reviewed.GetType().GetProperty("IsSuccess").GetValue(reviewed) &&
                reviewSent.SelectToken(item.Item3)?.ToString() == item.Item5,
                item.Item1 + " actual Policy Agent wire payload retains thinking");
            before = server.Requests.Count;
            await Invoke(api, "TranslateBatchCoreAsync", new List<string> { "Connection Test" }, config, true, null, null, true);
            JObject testSent = JObject.Parse(Encoding.UTF8.GetString(server.Requests.Skip(before).Single().Body));
            Check(testSent.SelectToken(item.Item3)?.ToString() == item.Item5,
                item.Item1 + " Policy Agent connection test retains its review thinking intent");
        }

        Type groupType = mod.GetType("AutoTranslator_Core.TranslationPolicy.TranslationPolicyAgentRequestGroup", true);
        object group = Activator.CreateInstance(groupType);
        groupType.GetProperty("Id").SetValue(group, "fixture-group");
        groupType.GetProperty("PackageId").SetValue(group, "test.fixture");
        IList groups = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(groupType)); groups.Add(group);
        object policyConfig = ThinkingConfig(mod, "MiMo", "mimo-v2.6-pro", server.Url);
        long reserved = 0;
        Func<long, bool, long?, Task<bool>> deny = (estimate, _, _) => { reserved = estimate; return Task.FromResult(false); };
        int requestsBefore = server.Requests.Count;
        object denied = await Invoke(api, "ClassifyTranslationPolicyGroupsAsync", groups, policyConfig, 0, deny);
        Check(reserved >= 5120 && (bool)denied.GetType().GetProperty("BudgetDenied").GetValue(denied) && server.Requests.Count == requestsBefore,
            "review reserves reasoning plus answer tokens before sending and respects budget denial");
        Func<long, bool, long?, Task<bool>> allow = (estimate, _, _) => { reserved = estimate; return Task.FromResult(true); };
        object classified = await Invoke(api, "ClassifyTranslationPolicyGroupsAsync", groups, policyConfig, 0, allow);
        JObject classificationPayload = JObject.Parse(Encoding.UTF8.GetString(server.Requests.Skip(requestsBefore).Single().Body));
        Check((int)classificationPayload["max_completion_tokens"] == 5120 &&
            (string)classificationPayload["thinking"]?["type"] == "enabled" &&
            ((IEnumerable)classified.GetType().GetProperty("Decisions").GetValue(classified)).Cast<object>().Count() == 1,
            "full policy classification leaves answer headroom and parses a decision with reasoning enabled");
        Check((string)api.GetField("TranslationPolicyAgentPromptVersion", Static).GetRawConstantValue() == "4",
            "changed review behavior invalidates the previous agent cache generation");
        object truncatedConfig = ThinkingConfig(mod, "MiMo", "mimo-v2.6-pro", server.Url + "/truncated");
        object truncated = await Invoke(api, "ClassifyTranslationPolicyGroupsAsync", groups, truncatedConfig, 0, allow);
        Check((string)truncated.GetType().GetProperty("ErrorCode").GetValue(truncated) == "truncated_response" &&
            !((IEnumerable)truncated.GetType().GetProperty("Decisions").GetValue(truncated)).Cast<object>().Any(),
            "reasoning that exhausts the output budget is reported as truncation and never grants translation permission");
    }

    private static byte[] ThinkingResponse(string path, byte[] body)
    {
        JObject request = JObject.Parse(Encoding.UTF8.GetString(body));
        bool review = request.ToString().Contains("fixture-review") || request.ToString().Contains("constrained policy classifier");
        string text = review ? "[{\"id\":\"fixture-group\",\"decision\":\"allow\",\"reason\":\"visible text\"}]" : "[\"測試譯文\"]";
        JObject response;
        if (path.Contains(":generateContent"))
            response = new JObject { ["candidates"] = new JArray(new JObject { ["finishReason"] = "STOP", ["content"] = new JObject {
                ["parts"] = new JArray(new JObject { ["thought"] = true, ["text"] = "fixture thought must not become a translation" },
                    new JObject { ["text"] = text }) } }) };
        else response = new JObject { ["choices"] = new JArray(new JObject { ["finish_reason"] = "stop", ["message"] = new JObject {
            ["reasoning_content"] = "fixture thought must not become a translation", ["content"] = text } }) };
        if (path.Contains("/truncated"))
        {
            response["choices"][0]["finish_reason"] = "length";
            response["choices"][0]["message"]["content"] = "";
        }
        return Encoding.UTF8.GetBytes(response.ToString(Newtonsoft.Json.Formatting.None));
    }
}
