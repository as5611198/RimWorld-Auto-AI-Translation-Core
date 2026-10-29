using System.Reflection;
using System.Text;
using System.Collections;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static void SetThinking(object config, string mode)
    {
        FieldInfo field = config.GetType().GetField("ThinkingMode");
        field.SetValue(config, Enum.Parse(field.FieldType, mode));
    }

    private static async Task TestPlayerPreferences(Assembly mod, FixtureServer server)
    {
        MethodInfo prepare = api.GetMethod("PrepareApiRequestPayload", Static);
        MethodInfo status = api.GetMethod("GetThinkingStatusKey", Static);
        MethodInfo reserve = mod.GetType("AutoTranslator_Core.ApiThinkingPolicy", true).GetMethod("PolicyReasoningReserve", Static);
        JObject input = JObject.Parse("{\"model\":\"fixture\",\"messages\":[{\"role\":\"user\",\"content\":\"preserved\"}],\"max_tokens\":4096}");
        JObject Prepared(object config, bool review) => (JObject)prepare.Invoke(null, new[] { input, config, (object)review });
        string Status(object config, bool review) => (string)status.Invoke(null, new[] { config, (object)review });
        var matrix = new[] {
            ("MiMo", "mimo-v2.6-pro", "thinking.type", "disabled", "enabled"),
            ("DeepSeek", "deepseek-flash", "thinking.type", "disabled", "enabled"),
            ("GLM", "glm-4.7", "thinking.type", "disabled", "enabled"),
            ("Alibaba", "qwen-plus", "enable_thinking", "False", "True"),
            ("Google", "gemini-2.5-flash", "generationConfig.thinkingConfig.thinkingBudget", "0", "1024"),
            ("OpenAI", "gpt-5.6", "reasoning_effort", "none", "low"),
            ("Grok", "grok-4.3", "reasoning_effort", "none", "low")
        };
        foreach (var item in matrix)
        {
            object config = ThinkingConfig(mod, item.Item1, item.Item2);
            Check(config.GetType().GetField("ThinkingMode").GetValue(config).ToString() == "Auto",
                item.Item1 + " defaults to Automatic for existing configurations");
            SetThinking(config, "Enabled");
            Check(Prepared(config, false).SelectToken(item.Item3)?.ToString() == item.Item5 &&
                Prepared(config, true).SelectToken(item.Item3)?.ToString() == item.Item5,
                item.Item1 + " explicit Enable applies to translation and review");
            Check(Status(config, false) == "ATC_Advanced_ThinkingOn", item.Item1 + " Enable status matches request");
            SetThinking(config, "PreferOff");
            Check(Prepared(config, false).SelectToken(item.Item3)?.ToString() == item.Item4 &&
                Prepared(config, true).SelectToken(item.Item3)?.ToString() == item.Item4,
                item.Item1 + " Prefer off overrides review default without touching translation model or messages");
            string endpoint = (string)api.GetMethod("GetBaseUrl", Static).Invoke(null, new[] { config });
            Check((int)reserve.Invoke(null, new[] { config, endpoint }) == 0 && Status(config, true) == "ATC_Advanced_ThinkingOff",
                item.Item1 + " review off uses no unnecessary reasoning reserve");
        }

        object mandatory = ThinkingConfig(mod, "GLM", "glm-5.3");
        SetThinking(mandatory, "PreferOff");
        Check((string)Prepared(mandatory, true)["thinking"]?["type"] == "enabled" &&
            Status(mandatory, true) == "ATC_Advanced_ThinkingMinimum" &&
            (int)reserve.Invoke(null, new[] { mandatory, "https://open.bigmodel.cn/api/paas/v4" }) == 4096,
            "mandatory models retain thinking, budget headroom and an honest limitation notice");
        object unknown = ThinkingConfig(mod, "Custom_OpenAI", "deepseek-flash", "https://gateway.example/v1");
        SetThinking(unknown, "Enabled");
        Check(JToken.DeepEquals(Prepared(unknown, false), input) && Status(unknown, false) == "ATC_Advanced_ThinkingUnknown",
            "explicit preference never injects guessed parameters into an unknown gateway");
        unknown.GetType().GetField("UsePlayer2Protocol").SetValue(unknown, true);
        Check(Prepared(unknown, false)["thinking"] == null && Status(unknown, false) == "ATC_Advanced_ThinkingPlayer2",
            "Player2 retains control under explicit Enable");
        Check(Status(ThinkingConfig(mod, "DeepL", ""), false) == "ATC_Advanced_ThinkingNotApplicable",
            "DeepL is described as not applicable");
        object router = ThinkingConfig(mod, "OpenRouter", "fixture/optional");
        SetThinking(router, "Enabled");
        Check((bool?)Prepared(router, false)["reasoning"]?["enabled"] == true,
            "OpenRouter Enable uses the fetched per-model capability for translation");
        SetThinking(router, "PreferOff");
        Check((bool?)Prepared(router, true)["reasoning"]?["enabled"] == false,
            "OpenRouter Prefer off can disable optional review reasoning");
        router.GetType().GetField("SelectedModel").SetValue(router, "fixture/mandatory");
        Check((bool?)Prepared(router, true)["reasoning"]?["enabled"] == true &&
            Status(router, true) == "ATC_Advanced_ThinkingMinimum",
            "OpenRouter mandatory metadata takes precedence over Prefer off");

        object configForIdentity = ThinkingConfig(mod, "MiMo", "mimo-v2.6-pro");
        MethodInfo policyIdentity = api.GetMethod("GetPolicyAgentEvaluatorFingerprint", Static);
        MethodInfo testIdentity = api.GetMethod("GetConnectionTestFingerprint", Static);
        string initialPolicyIdentity = (string)policyIdentity.Invoke(null, new[] { configForIdentity });
        string initialTestIdentity = (string)testIdentity.Invoke(null, new[] { configForIdentity });
        SetThinking(configForIdentity, "PreferOff");
        Check(initialPolicyIdentity != (string)policyIdentity.Invoke(null, new[] { configForIdentity }) &&
            initialTestIdentity != (string)testIdentity.Invoke(null, new[] { configForIdentity }),
            "changing thinking invalidates review cache identity and old connection diagnostics");
        SetThinking(configForIdentity, "Auto");
        Check(initialPolicyIdentity == (string)policyIdentity.Invoke(null, new[] { configForIdentity }),
            "returning to Automatic restores the matching review identity");

        Type language = mod.GetType("AutoTranslator_Core.TargetLanguage", true);
        MethodInfo prompt = api.GetMethod("GetSystemPrompt", Static, null, new[] { language, typeof(string) }, null);
        MethodInfo normalize = api.GetMethod("NormalizeTranslationInstructions", Static);
        object traditional = Enum.Parse(language, "Traditional");
        string Build(string value) => (string)prompt.Invoke(null, new[] { traditional, value });
        string originalPrompt = Build(null);
        const string preferences = "Use Taiwan wording; keep name 測試人 unchanged. Keep {0}, [PAWN_nameDef], <color=red>.";
        string customPrompt = Build(preferences);
        Check(originalPrompt == Build("") && originalPrompt == Build(" \r\n\t"),
            "empty or whitespace preferences preserve the original prompt");
        Check(customPrompt.Contains(preferences) && customPrompt.Contains("JSON array") &&
            customPrompt.Contains("EXACT SAME number") && customPrompt.Contains("[VARIABLES & SPECIAL CHARACTERS PROTECTION"),
            "custom translation wording retains format, array count and variable safeguards");
        Check(customPrompt.IndexOf(preferences, StringComparison.Ordinal) < customPrompt.IndexOf("[RIMWORLD XML / DEF RULES]", StringComparison.Ordinal) &&
            customPrompt.EndsWith("Absolutely NO other text is allowed!"),
            "built-in protections and final format instruction follow player preferences");
        Check((string)normalize.Invoke(null, new object[] { null }) == "" &&
            ((string)normalize.Invoke(null, new[] { new string('a', 5000) })).Length == 4000,
            "instructions are null safe and bounded before use or storage");
        string surrogateBoundary = (string)normalize.Invoke(null, new[] { new string('a', 3999) + "😀" });
        Check(surrogateBoundary.Length == 3999 && !char.IsHighSurrogate(surrogateBoundary.Last()),
            "instruction limit does not cut an emoji surrogate pair");

        FieldInfo settingsField = mod.GetType("AutoTranslator_Core.AutoTranslatorMod", true).GetField("Settings", Static);
        object playerSettings = settingsField.GetValue(null);
        object previousInstructions = settings.GetField("ExtraTranslationInstructions").GetValue(playerSettings);
        settings.GetField("ExtraTranslationInstructions").SetValue(playerSettings, preferences);
        try
        {
            Check(initialTestIdentity != (string)testIdentity.Invoke(null, new[] { configForIdentity }) &&
                initialPolicyIdentity == (string)policyIdentity.Invoke(null, new[] { configForIdentity }),
                "translation preference edits invalidate translation diagnostics without invalidating unrelated review cache");
            object wireConfig = ThinkingConfig(mod, "MiMo", "mimo-v2.6-pro", server.Url + "/thinking-mimo");
            SetThinking(wireConfig, "Enabled");
            int before = server.Requests.Count;
            await Invoke(api, "TranslateBatchCoreAsync", new List<string> { "Attack" }, wireConfig, true, null, null, false);
            JObject sent = JObject.Parse(Encoding.UTF8.GetString(server.Requests.Skip(before).Single().Body));
            Check((string)sent["thinking"]?["type"] == "enabled" &&
                (string)sent["messages"]?[0]?["content"] == customPrompt,
                "real translation route sends Enable and exactly the previewed custom prompt");
            SetThinking(wireConfig, "PreferOff");
            before = server.Requests.Count;
            await Invoke(api, "ExecuteStructuredChatAttemptAsync", "fixture-review", "[]", 1024, wireConfig, null);
            sent = JObject.Parse(Encoding.UTF8.GetString(server.Requests.Skip(before).Single().Body));
            Check((string)sent["thinking"]?["type"] == "disabled" && !sent.ToString().Contains(preferences),
                "real review route honors Prefer off and excludes translation preferences");
            before = server.Requests.Count;
            await Invoke(api, "TranslateBatchCoreAsync", new List<string> { "Connection Test" }, wireConfig, true, null, null, true);
            sent = JObject.Parse(Encoding.UTF8.GetString(server.Requests.Skip(before).Single().Body));
            Check((string)sent["thinking"]?["type"] == "disabled" && !sent.ToString().Contains(preferences),
                "review connection test honors review mode without translation preferences");
        }
        finally { settings.GetField("ExtraTranslationInstructions").SetValue(playerSettings, previousInstructions); }
        TestPreferencePersistence(mod);
    }

    private static object CallWithDefaults(object instance, string name, params object[] supplied)
    {
        MethodInfo method = instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == name && m.GetParameters().Length >= supplied.Length &&
                m.GetParameters().Skip(supplied.Length).All(p => p.HasDefaultValue))
            .OrderBy(m => m.GetParameters().Length).First();
        object[] arguments = supplied.Concat(method.GetParameters().Skip(supplied.Length).Select(p => p.DefaultValue)).ToArray();
        return method.Invoke(instance, arguments);
    }

    private static void TestPreferencePersistence(Assembly mod)
    {
        Type gameScribe = settings.BaseType.Assembly.GetType("Verse.Scribe", true);
        // The isolated host has no running game's preferences singleton. Seed only
        // this process so Scribe's profiler can read LogVerbose during finalization.
        Type prefs = gameScribe.Assembly.GetType("Verse.Prefs", true);
        FieldInfo prefsData = prefs.GetFields(Static).Single(f => f.FieldType.Name == "PrefsData");
        if (prefsData.GetValue(null) == null) prefsData.SetValue(null, Activator.CreateInstance(prefsData.FieldType));
        object saver = gameScribe.GetField("saver", Static).GetValue(null);
        object loader = gameScribe.GetField("loader", Static).GetValue(null);
        string path = Path.Combine(Path.GetTempPath(), "atc-preferences-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            object saved = Activator.CreateInstance(settings);
            const string instructions = "台灣用語 😀 & <name> {0}\nKeep names.";
            settings.GetField("ExtraTranslationInstructions").SetValue(saved, instructions);
            settings.GetField("HasSeenUsageGuide").SetValue(saved, true);
            object translation = ThinkingConfig(mod, "MiMo", "mimo-v2.6-pro");
            SetThinking(translation, "Enabled");
            ((IList)settings.GetField("ApiConfigs").GetValue(saved)).Add(translation);
            object review = ThinkingConfig(mod, "DeepSeek", "deepseek-flash");
            SetThinking(review, "PreferOff");
            settings.GetField("PolicyAgentApiConfig").SetValue(saved, review);
            CallWithDefaults(saver, "InitSaving", path, "preferences");
            settings.GetMethod("ExposeData").Invoke(saved, null);
            CallWithDefaults(saver, "FinalizeSaving");

            object Load()
            {
                object loaded = Activator.CreateInstance(settings);
                CallWithDefaults(loader, "InitLoading", path);
                settings.GetMethod("ExposeData").Invoke(loaded, null);
                CallWithDefaults(loader, "FinalizeLoading");
                return loaded;
            }
            object reloaded = Load();
            object loadedTranslation = ((IList)settings.GetField("ApiConfigs").GetValue(reloaded))[0];
            object loadedReview = settings.GetField("PolicyAgentApiConfig").GetValue(reloaded);
            Check((bool)settings.GetField("HasSeenUsageGuide").GetValue(reloaded),
                "first-use guide acknowledgement survives real Scribe save and reload");
            Check((string)settings.GetField("ExtraTranslationInstructions").GetValue(reloaded) == instructions &&
                loadedTranslation.GetType().GetField("ThinkingMode").GetValue(loadedTranslation).ToString() == "Enabled" &&
                loadedReview.GetType().GetField("ThinkingMode").GetValue(loadedReview).ToString() == "PreferOff",
                "real game Scribe round-trip preserves Unicode preferences and independent translation/review modes");

            XDocument document = XDocument.Load(path);
            document.Descendants("ThinkingMode").Remove();
            document.Descendants("ExtraTranslationInstructions").Remove();
            document.Descendants("HasSeenUsageGuide").Remove();
            document.Save(path);
            object oldSettings = Load();
            Check(!(bool)settings.GetField("HasSeenUsageGuide").GetValue(oldSettings),
                "old settings offer the new guide once on their first settings visit");
            loadedTranslation = ((IList)settings.GetField("ApiConfigs").GetValue(oldSettings))[0];
            Check((string)settings.GetField("ExtraTranslationInstructions").GetValue(oldSettings) == "" &&
                loadedTranslation.GetType().GetField("ThinkingMode").GetValue(loadedTranslation).ToString() == "Auto",
                "old Scribe settings without new fields load with Automatic and no added prompt");

            document.Descendants("ApiConfigs").Elements("li").Single().Add(new XElement("ThinkingMode", "999"));
            document.Save(path);
            object invalidSettings = Load();
            loadedTranslation = ((IList)settings.GetField("ApiConfigs").GetValue(invalidSettings))[0];
            Check(loadedTranslation.GetType().GetField("ThinkingMode").GetValue(loadedTranslation).ToString() == "Auto",
                "invalid persisted mode recovers to Automatic instead of sending unknown parameters");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
