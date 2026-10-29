using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static void SetThinkingMode(object config, string mode)
    {
        FieldInfo field = configType.GetField("ThinkingMode");
        field.SetValue(config, Enum.Parse(field.FieldType, mode));
    }

    private static void TestSharedApiPreferences(Assembly mod)
    {
        MethodInfo prepare = api.GetMethod("PrepareApiRequestPayload", Static);
        MethodInfo status = api.GetMethod("GetThinkingStatusKey", Static);
        Type thinking = mod.GetType("AutoTranslator_Core.ApiThinkingPolicy", true);
        MethodInfo reserve = thinking.GetMethod("PolicyReasoningReserve", Static);
        JObject input = JObject.Parse("{\"model\":\"fixture\",\"messages\":[{\"role\":\"user\",\"content\":\"preserve\"}],\"max_tokens\":4096}");
        JObject Prepared(object config, bool review) => (JObject)prepare.Invoke(null, new object[] { input, config, review });
        var models = new[] {
            new[] { "MiMo", "mimo-v2.6-pro", "thinking.type", "disabled", "enabled" },
            new[] { "DeepSeek", "deepseek-flash", "thinking.type", "disabled", "enabled" },
            new[] { "GLM", "glm-4.7", "thinking.type", "disabled", "enabled" },
            new[] { "Alibaba", "qwen-plus", "enable_thinking", "False", "True" },
            new[] { "Google", "gemini-2.5-flash", "generationConfig.thinkingConfig.thinkingBudget", "0", "1024" },
            new[] { "OpenAI", "gpt-5.6", "reasoning_effort", "none", "low" },
            new[] { "Grok", "grok-4.3", "reasoning_effort", "none", "low" }
        };
        foreach (string[] model in models)
        {
            object config = Config(model[0], "", "synthetic-shared-key", model[1]);
            foreach (string mode in new[] { "Auto", "PreferOff", "Enabled" })
            {
                SetThinkingMode(config, mode);
                JObject translation = Prepared(config, false);
                JObject review = Prepared(config, true);
                Check(translation.SelectToken(model[2]).ToString() == model[mode == "Enabled" ? 4 : 3] &&
                    review.SelectToken(model[2]).ToString() == model[mode == "PreferOff" ? 3 : 4],
                    model[0] + " " + mode + " applies correctly to translation and review on every game version");
                Check(JToken.DeepEquals(translation["messages"], input["messages"]) &&
                    JToken.DeepEquals(review["messages"], input["messages"]), model[0] + " " + mode + " preserves input messages");
            }
            SetThinkingMode(config, "PreferOff");
            string endpoint = (string)api.GetMethod("GetBaseUrl", Static).Invoke(null, new[] { config });
            Check((int)reserve.Invoke(null, new[] { config, endpoint }) == 0, model[0] + " optional review thinking off avoids extra budget reservation");
        }
        Check(input["thinking"] == null && input["reasoning_effort"] == null && input["max_tokens"] != null,
            "shared thinking logic never mutates the caller's payload");

        object mandatory = Config("GLM", "", "synthetic-key", "glm-5.3");
        SetThinkingMode(mandatory, "PreferOff");
        Check((string)Prepared(mandatory, true)["thinking"]["type"] == "enabled" &&
            (string)status.Invoke(null, new object[] { mandatory, true }) == "ATC_Advanced_ThinkingMinimum",
            "mandatory thinking is retained with an honest UI limitation");
        Check((int)reserve.Invoke(null, new object[] { mandatory, "https://open.bigmodel.cn/api/paas/v4" }) == 4096,
            "mandatory review thinking retains answer headroom");
        object custom = Config("Custom_OpenAI", "https://gateway.example/v1", "synthetic-key", "deepseek-flash");
        SetThinkingMode(custom, "Enabled");
        Check(JToken.DeepEquals(Prepared(custom, false), input), "unknown gateways are never assigned guessed thinking fields");
        configType.GetField("CustomBaseUrl").SetValue(custom, "https://api.deepseek.com/v1");
        Check((string)Prepared(custom, false)["thinking"]["type"] == "enabled", "known custom official hosts honor explicit thinking preferences");
        configType.GetField("UsePlayer2Protocol").SetValue(custom, true);
        Check(Prepared(custom, false)["thinking"] == null &&
            (string)status.Invoke(null, new object[] { custom, false }) == "ATC_Advanced_ThinkingPlayer2", "Player2 keeps application control");

        object router = Config("OpenRouter", "", "synthetic-key", "fixture/optional");
        string routerUrl = (string)api.GetMethod("GetBaseUrl", Static).Invoke(null, new[] { router });
        thinking.GetMethod("RememberModels", Static).Invoke(null, new object[] { router, routerUrl,
            "{\"data\":[{\"id\":\"fixture/optional\",\"reasoning\":{\"mandatory\":false,\"supported_efforts\":[\"none\",\"low\"]}},{\"id\":\"fixture/mandatory\",\"reasoning\":{\"mandatory\":true,\"supported_efforts\":[\"low\"]}}]}" });
        SetThinkingMode(router, "Enabled");
        Check((bool)Prepared(router, false)["reasoning"]["enabled"], "OpenRouter translation Enable uses model capabilities");
        SetThinkingMode(router, "PreferOff");
        Check(!(bool)Prepared(router, true)["reasoning"]["enabled"], "OpenRouter optional review thinking can be disabled");
        configType.GetField("SelectedModel").SetValue(router, "fixture/mandatory");
        Check((bool)Prepared(router, true)["reasoning"]["enabled"], "OpenRouter mandatory review thinking cannot be disabled");

        Type settings = mod.GetType("AutoTranslator_Core.AutoTranslatorSettings", true);
        FieldInfo settingsField = mod.GetType("AutoTranslator_Core.AutoTranslatorMod", true).GetField("Settings", Static);
        object previous = settingsField.GetValue(null);
        object settingsObject = Activator.CreateInstance(settings);
        settingsField.SetValue(null, settingsObject);
        try
        {
            Type language = mod.GetType("AutoTranslator_Core.TargetLanguage", true);
            object target = Enum.Parse(language, "Traditional");
            settings.GetField("TargetLang").SetValue(settingsObject, target);
            MethodInfo preview = api.GetMethod("GetSystemPrompt", Static, null, new[] { language, typeof(string) }, null);
            MethodInfo actualPrompt = api.GetMethod("GetSystemPrompt", Static, null, new[] { language }, null);
            const string extra = "Use Taiwan wording; keep 測試人 {0} [PAWN_nameDef] <color=red>.";
            string basePrompt = (string)preview.Invoke(null, new object[] { target, "" });
            string extended = (string)preview.Invoke(null, new object[] { target, extra });
            settings.GetField("ExtraTranslationInstructions").SetValue(settingsObject, extra);
            Check((string)actualPrompt.Invoke(null, new[] { target }) == extended && extended.Contains(extra),
                "translation uses exactly the previewed custom prompt");
            Check(extended.Contains("EXACT SAME number") && extended.Contains("[VARIABLES & SPECIAL CHARACTERS PROTECTION") &&
                extended.EndsWith("Absolutely NO other text is allowed!", StringComparison.Ordinal), "custom preferences retain built-in format and variable safeguards");
            Check(basePrompt == (string)preview.Invoke(null, new object[] { target, " \r\n\t" }), "empty preferences restore the original prompt");
            MethodInfo normalizeInstructions = api.GetMethod("NormalizeTranslationInstructions", Static);
            Check(((string)normalizeInstructions.Invoke(null, new[] { new string('a', 5000) })).Length == 4000 &&
                (string)normalizeInstructions.Invoke(null, new[] { "a\r\nb\rc" }) == "a\nb\nc", "instructions are bounded and normalize saved line endings");
            Check(((string)normalizeInstructions.Invoke(null, new[] { new string('a', 3999) + "\U0001F600" })).Length == 3999,
                "instruction limit preserves whole emoji surrogate pairs");

            object identityConfig = Config("MiMo", "", "synthetic-key", "mimo-v2.6-pro");
            MethodInfo policyIdentity = api.GetMethod("GetPolicyAgentEvaluatorFingerprint", Static);
            MethodInfo testIdentity = api.GetMethod("GetConnectionTestFingerprint", Static);
            string originalPolicy = (string)policyIdentity.Invoke(null, new[] { identityConfig });
            string originalTest = (string)testIdentity.Invoke(null, new[] { identityConfig });
            SetThinkingMode(identityConfig, "PreferOff");
            Check(originalPolicy != (string)policyIdentity.Invoke(null, new[] { identityConfig }) &&
                originalTest != (string)testIdentity.Invoke(null, new[] { identityConfig }), "thinking changes invalidate review cache identity and connection diagnostics");
            SetThinkingMode(identityConfig, "Auto");
            settings.GetField("ExtraTranslationInstructions").SetValue(settingsObject, "different wording");
            Check(originalPolicy == (string)policyIdentity.Invoke(null, new[] { identityConfig }) &&
                originalTest != (string)testIdentity.Invoke(null, new[] { identityConfig }), "translation preferences change translation diagnostics without changing review identity");
            Check((string)api.GetField("TranslationPolicyAgentPromptVersion", Static).GetRawConstantValue() == "4",
                "all versions use the new review cache generation");
        }
        finally { settingsField.SetValue(null, previous); }

        object manual = Config("Custom_OpenAI", "http://127.0.0.1:4315", "", "fixture");
        api.GetMethod("AutoFetchForConfig", Static, null, new[] { configType, typeof(bool) }, null).Invoke(null, new object[] { manual, false });
        Check(!(bool)configType.GetField("IsFetching").GetValue(manual), "editing a custom endpoint never starts fetching or dispatches Unity work");
        configType.GetField("FetchGeneration").SetValue(manual, 9);
        configType.GetField("PendingFetchFingerprint").SetValue(manual, "pending");
        api.GetMethod("ScheduleModelFetchRetry", Static).Invoke(null, new object[] { manual, 9 });
        Check((string)configType.GetField("PendingFetchFingerprint").GetValue(manual) == "" &&
            (long)configType.GetField("NextModelFetchRetryUtcTicks").GetValue(manual) == 0,
            "custom manual fetching does not schedule unsolicited background retries");

        JObject googleResponse = JObject.Parse("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"[\\\"final\\\"]\"},{\"thought\":true,\"text\":\"[\\\"reasoning\\\"]\"}]}}]}");
        var parsed = (List<string>)api.GetMethod("ParseResponse", Static).Invoke(null, new object[] {
            googleResponse.ToString(), Enum.Parse(providerType, "Google"), 1, true });
        Check(parsed != null && parsed.Single() == "final", "Google final translation excludes thought parts even when they occur last");
        Check(mod.GetType("AutoTranslator_Core.Window_TranslationPreferences", true) != null,
            "custom translation editor is included in the product DLL");
        Type guide = mod.GetType("AutoTranslator_Core.TutorialWindow", true);
        Type sectionType = mod.GetType("AutoTranslator_Core.TutorialSection", true);
        int[] destinations = { 3, 3, 1, 2 };
        int sectionIndex = 0;
        foreach (object section in Enum.GetValues(sectionType))
        {
            Check((int)guide.GetMethod("DestinationTab", Static).Invoke(null, new[] { section }) == destinations[sectionIndex++] &&
                (string)guide.GetMethod("SectionContentKey", Static).Invoke(null, new[] { section }) == "ATC_Guide_Content_" + section,
                "guide topic " + section + " targets its matching operation page and content");
        }
    }
}
