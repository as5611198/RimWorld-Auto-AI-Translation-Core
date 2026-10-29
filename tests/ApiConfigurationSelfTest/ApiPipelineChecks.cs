using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static JObject lastPipelinePayload;

    // Only the test process is patched. The transport boundary returns synthetic
    // responses; the product's real builders, purpose selection and parsers run.
    public static bool CaptureApiPost<T>(string jsonPayload, ref Task<T> __result)
    {
        lastPipelinePayload = JObject.Parse(jsonPayload);
        bool review = jsonPayload.Contains("fixture-review") || jsonPayload.Contains("constrained policy classifier");
        string answer = review ? "[{\"id\":\"fixture-group\",\"decision\":\"allow\",\"reason\":\"visible text\"}]" : "[\"translated\"]";
        JObject envelope = lastPipelinePayload["contents"] != null
            ? JObject.Parse("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"thought\":true,\"text\":\"internal reasoning\"},{\"text\":\"\"}]}}]}")
            : JObject.Parse("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"reasoning_content\":\"internal reasoning\",\"content\":\"\"}}]}");
        if (lastPipelinePayload["contents"] != null) envelope["candidates"][0]["content"]["parts"][1]["text"] = answer;
        else envelope["choices"][0]["message"]["content"] = answer;
        object response = Activator.CreateInstance(typeof(T));
        typeof(T).GetField("IsSuccess").SetValue(response, true);
        typeof(T).GetField("HttpCode").SetValue(response, 200L);
        typeof(T).GetField("ResponseBody").SetValue(response, envelope.ToString());
        __result = Task.FromResult((T)response);
        return false;
    }

    private static object CompleteApiTask(string name, params object[] arguments)
    {
        Task task = (Task)api.GetMethod(name, Static).Invoke(null, arguments);
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result").GetValue(task);
    }

    private static void TestApiPipelines(Assembly mod, string harmonyPath)
    {
        Assembly harmony = Assembly.LoadFrom(harmonyPath);
        Type harmonyType = harmony.GetType("HarmonyLib.Harmony", true);
        Type harmonyMethod = harmony.GetType("HarmonyLib.HarmonyMethod", true);
        object patcher = Activator.CreateInstance(harmonyType, new object[] { "atc.api.pipeline.tests" });
        MethodInfo transport = api.GetMethod("SendJsonRequestAttemptAsync", Static);
        MethodInfo capture = typeof(Program).GetMethod("CaptureApiPost").MakeGenericMethod(transport.ReturnType.GetGenericArguments()[0]);
        object prefix = Activator.CreateInstance(harmonyMethod, new object[] { capture });
        harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5)
            .Invoke(patcher, new object[] { transport, prefix, null, null, null });
        Type settings = mod.GetType("AutoTranslator_Core.AutoTranslatorSettings", true);
        FieldInfo settingsField = mod.GetType("AutoTranslator_Core.AutoTranslatorMod", true).GetField("Settings", Static);
        object previousSettings = settingsField.GetValue(null);
        object fixtureSettings = Activator.CreateInstance(settings);
        settingsField.SetValue(null, fixtureSettings);
        const string instructions = "Keep fixture-specific names unchanged.";
        settings.GetField("ExtraTranslationInstructions").SetValue(fixtureSettings, instructions);
        settings.GetField("TargetLang").SetValue(fixtureSettings, Enum.Parse(mod.GetType("AutoTranslator_Core.TargetLanguage", true), "English"));
        settings.GetMethod("ResetPipelineCancellation", Static).Invoke(null, null);
        try
        {
            var models = new[] {
                new[] { "MiMo", "mimo-v2.6-pro", "thinking.type", "disabled", "enabled" },
                new[] { "DeepSeek", "deepseek-flash", "thinking.type", "disabled", "enabled" },
                new[] { "Alibaba", "qwen-plus", "enable_thinking", "False", "True" },
                new[] { "Google", "gemini-2.5-flash", "generationConfig.thinkingConfig.thinkingBudget", "0", "1024" }
            };
            foreach (string[] model in models)
            {
                object config = Config(model[0], "", "synthetic-pipeline-key", model[1]);
                var translated = (List<string>)CompleteApiTask("TranslateBatchCoreAsync", new List<string> { "Attack" }, config, true, null, null, false);
                Check(translated.Single() == "translated" && lastPipelinePayload.SelectToken(model[2]).ToString() == model[3] &&
                    lastPipelinePayload.ToString().Contains(instructions), model[0] + " real translation builder applies default off and custom instructions");
                object reviewed = CompleteApiTask("ExecuteStructuredChatAttemptAsync", "fixture-review", "[]", 5120, config, null);
                Check((bool)reviewed.GetType().GetProperty("IsSuccess").GetValue(reviewed) &&
                    lastPipelinePayload.SelectToken(model[2]).ToString() == model[4] && !lastPipelinePayload.ToString().Contains(instructions),
                    model[0] + " real review builder retains thinking and excludes translation instructions");
                SetThinkingMode(config, "Enabled");
                CompleteApiTask("TranslateBatchCoreAsync", new List<string> { "Attack" }, config, true, null, null, false);
                Check(lastPipelinePayload.SelectToken(model[2]).ToString() == model[4], model[0] + " real translation builder honors explicit Enable");
                SetThinkingMode(config, "PreferOff");
                CompleteApiTask("TranslateBatchCoreAsync", new List<string> { "Connection Test" }, config, true, null, null, true);
                Check(lastPipelinePayload.SelectToken(model[2]).ToString() == model[3] && !lastPipelinePayload.ToString().Contains(instructions),
                    model[0] + " review connection-test core honors Prefer off without translation instructions");
            }

            Type groupType = mod.GetType("AutoTranslator_Core.TranslationPolicy.TranslationPolicyAgentRequestGroup", true);
            object group = Activator.CreateInstance(groupType);
            groupType.GetProperty("Id").SetValue(group, "fixture-group");
            groupType.GetProperty("PackageId").SetValue(group, "test.fixture");
            IList groups = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(groupType));
            groups.Add(group);
            object policyConfig = Config("MiMo", "", "synthetic-pipeline-key", "mimo-v2.6-pro");
            long estimate = 0;
            Func<long, bool, long?, Task<bool>> budget = (value, retry, actual) => { estimate = value; return Task.FromResult(true); };
            object classification = CompleteApiTask("ClassifyTranslationPolicyGroupsAsync", groups, policyConfig, 0, budget);
            Check((int)lastPipelinePayload["max_completion_tokens"] == 5120 && estimate >= 5120 &&
                ((IEnumerable)classification.GetType().GetProperty("Decisions").GetValue(classification)).Cast<object>().Count() == 1 &&
                !lastPipelinePayload.ToString().Contains(instructions), "full review pipeline reserves reasoning headroom and preserves its built-in prompt");
            SetThinkingMode(policyConfig, "PreferOff");
            CompleteApiTask("ClassifyTranslationPolicyGroupsAsync", groups, policyConfig, 0, budget);
            Check((int)lastPipelinePayload["max_completion_tokens"] == 1024 &&
                (string)lastPipelinePayload["thinking"]["type"] == "disabled", "full review pipeline removes optional thinking reserve when Prefer off is chosen");
        }
        finally
        {
            settingsField.SetValue(null, previousSettings);
            harmonyType.GetMethod("UnpatchAll").Invoke(patcher, new object[] { "atc.api.pipeline.tests" });
        }
    }
}
