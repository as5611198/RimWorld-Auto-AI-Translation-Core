using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static int passed;
    private static Type api;
    private static Type configType;
    private static Type providerType;
    private static MethodInfo normalize;

    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: ApiConfigurationSelfTest <mod.dll> <game assemblies directory> <harmony.dll>");
            return 2;
        }
        string modPath = Path.GetFullPath(args[0]);
        string gameDirectory = Path.GetFullPath(args[1]);
        string harmonyPath = Path.GetFullPath(args[2]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            string file = new AssemblyName(request.Name).Name + ".dll";
            foreach (string directory in new[] { gameDirectory, Path.GetDirectoryName(harmonyPath), Path.GetDirectoryName(modPath) })
            {
                string path = Path.Combine(directory, file);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try
        {
            Assembly game = Assembly.LoadFrom(Path.Combine(gameDirectory, "Assembly-CSharp.dll"));
            Console.WriteLine("Game dependency: " + game.FullName + "; " + game.Location);
            // Legacy build output can contain compile-only Unity reference assemblies.
            // Prefer explicitly supplied runtime dependencies before loading the product.
            foreach (string path in Directory.GetFiles(gameDirectory, "Unity*.dll")) Assembly.LoadFrom(path);
            Assembly mod = Assembly.LoadFrom(modPath);
            api = mod.GetType("AutoTranslator_Core.AutoTranslatorAPI", true);
            configType = mod.GetType("AutoTranslator_Core.ApiKeyConfig", true);
            providerType = mod.GetType("AutoTranslator_Core.TranslatorProvider", true);
            normalize = mod.GetType("AutoTranslator_Core.ApiEndpointRules", true).GetMethod("TryNormalize", Static);

            Accept("", "", "", "provider default");
            Accept("http://127.0.0.1:4315", "", "http://127.0.0.1:4315", "Player2 local base");
            Accept("http://127.0.0.1:4315/models", "", "http://127.0.0.1:4315", "models suffix");
            Accept("http://127.0.0.1:4315/v1/chat/completions/", "", "http://127.0.0.1:4315/v1", "chat suffix");
            Accept("  HTTPS://api.example.com/v1/  ", "", "https://api.example.com/v1", "trim surrounding spaces and scheme casing");
            Accept("http://localhost:11434/v1", "", "http://localhost:11434/v1", "Ollama local base");
            Accept("http://[::1]:4315/v1", "", "http://[::1]:4315/v1", "IPv6 local base");
            Accept("https://sk-proxy.example.com/v1", "", "https://sk-proxy.example.com/v1", "ordinary domain is not mistaken for a key");

            // Synthetic credentials only. Never contact any address in this suite.
            const string key = "aq.syntheticCredentialForTests123456789";
            Reject(key, key, "ATC_Error_ApiKeyInUrl", "key pasted into URL");
            Reject(key, "different-test-key", "ATC_Error_InvalidApiBaseUrl", "old key still in URL after replacing key");
            Reject("http://" + key + "/models", key, "ATC_Error_ApiKeyInUrl", "key previously wrapped as host");
            Reject("https://api.example.com/" + key, key, "ATC_Error_ApiKeyInUrl", "key in URL path");
            Reject("https://api.example.com/%61q.syntheticCredentialForTests123456789", key, "ATC_Error_ApiKeyInUrl", "encoded key in URL path");
            Reject("sk-syntheticKeyForTests123", "", "ATC_Error_ApiKeyInUrl", "recognizable key without key field");
            Reject("AIzaSyntheticKeyForTests123", "", "ATC_Error_ApiKeyInUrl", "Google key without key field");
            Reject("127.0.0.1:4315", "", "ATC_Error_InvalidApiBaseUrl", "missing protocol");
            Reject("ftp://api.example.com", "", "ATC_Error_InvalidApiBaseUrl", "unsupported protocol");
            Reject("https://user:password@api.example.com", "", "ATC_Error_InvalidApiBaseUrl", "URL credentials");
            Reject("https://api.example.com/v1?key=test", "", "ATC_Error_InvalidApiBaseUrl", "query cannot be appended as base path");
            Reject("https://api.example.com/v1#fragment", "", "ATC_Error_InvalidApiBaseUrl", "fragment cannot be appended as base path");
            Reject("https://api.example.com/ v1", "", "ATC_Error_InvalidApiBaseUrl", "internal space");
            Reject("https://", "", "ATC_Error_InvalidApiBaseUrl", "incomplete URL");

            object custom = Config("Custom_OpenAI", "http://127.0.0.1:4315", "", "test-model");
            Check(InvokeBool("IsConfigReady", custom), "keyless custom service can translate");
            Check(InvokeBool("IsPolicyAgentConfigReady", custom), "keyless custom service can serve policy agent");
            Check(InvokeBool("HasModelFetchCredential", custom), "keyless custom service can fetch models");
            Check(!InvokeBool("ShouldAutoFetchModels", custom),
                "keyless custom service waits for explicit model refresh on all versions");
            Check(!InvokeBool("ShouldAutoFetchModels", Config("Custom_OpenAI", "http://127.0.0.1:4315", "synthetic-key", "test-model")),
                "custom model auto-fetch remains manual even when a key is present");
            Check(InvokeBool("ShouldAutoFetchModels", Config("DeepSeek", "", "synthetic-key", "test-model")),
                "built-in providers retain automatic model fetching");
            Check((string)api.GetMethod("GetBaseUrl", Static).Invoke(null, new[] { custom }) == "http://127.0.0.1:4315/v1", "custom base URL defaults to v1");
            Check((string)api.GetMethod("BuildModelsUrl", Static).Invoke(null,
                new object[] { custom, "http://127.0.0.1:4315/v1", "", false }) == "http://127.0.0.1:4315/v1/models", "production model list URL");
            Check((string)api.GetMethod("GetAlternateCustomBaseUrl", Static).Invoke(null,
                new object[] { custom, "http://127.0.0.1:4315/v1" }) == "http://127.0.0.1:4315", "custom model fallback removes v1");
            object player2 = Config("Custom_OpenAI", "http://127.0.0.1:49152", "", "", true, "019a8368-b00b-72bc-b367-2825079dc6fb");
            Check(InvokeBool("IsConfigReady", player2), "Player2 protocol can translate without an API key");
            Check(InvokeBool("IsPolicyAgentConfigReady", player2), "Player2 protocol can serve policy agent");
            Check(InvokeBool("HasModelFetchCredential", player2), "Player2 protocol has an endpoint credential");
            Check(!InvokeBool("ShouldAutoFetchModels", player2), "Player2 protocol skips generic model fetch");
            Check((string)api.GetMethod("GetBaseUrl", Static).Invoke(null, new[] { player2 }) == "http://127.0.0.1:49152/v1", "Player2 appends v1 without changing a custom port");
            object player2WithV1 = Config("Custom_OpenAI", "http://127.0.0.1:4315/v1", "", "", true, "019a8368-b00b-72bc-b367-2825079dc6fb");
            Check((string)api.GetMethod("GetBaseUrl", Static).Invoke(null, new[] { player2WithV1 }) == "http://127.0.0.1:4315/v1", "Player2 does not duplicate v1");
            Check(!InvokeBool("IsConfigReady", Config("Custom_OpenAI", "http://127.0.0.1:4315", "", "", true, "")), "Player2 protocol requires a Game Client ID");
            Check(InvokeBool("IsConfigReady", Config("Custom_OpenAI", "https://api.example.com/custom", "", "test-model")), "ordinary custom endpoint still uses its own path");
            Check(!InvokeBool("IsConfigReady", Config("Custom_OpenAI", "", "", "test-model")), "keyless custom service needs explicit endpoint");
            Check(!InvokeBool("IsConfigReady", Config("Custom_OpenAI", "http://localhost:4315", "", "")), "model name remains required");
            Check(!InvokeBool("IsConfigReady", Config("Custom_OpenAI", key, "different-test-key", "test-model")), "invalid URL excluded from translation pool");
            Check(!InvokeBool("ShouldAutoFetchModels", Config("Custom_OpenAI", key, "different-test-key", "test-model")), "invalid URL never auto-fetches");
            configType.GetField("Enabled").SetValue(custom, false);
            Check(!InvokeBool("IsConfigReady", custom), "disabled service remains disabled");
            foreach (string provider in new[] { "Google", "OpenAI", "DeepSeek", "Grok", "GLM", "Alibaba", "OpenRouter", "DeepL" })
            {
                Check(!InvokeBool("IsConfigReady", Config(provider, "", "", "test-model")), provider + " still requires a key");
                Check(InvokeBool("IsConfigReady", Config(provider, "", "valid-synthetic-test-key", "test-model")), provider + " default endpoint still works");
            }
            Check(!InvokeBool("IsPolicyAgentConfigReady", Config("DeepL", "", "valid-synthetic-test-key", "test-model")), "policy agent still excludes DeepL");
            TestConnectionDiagnostics(mod);
            TestMiMo(mod);
            TestSharedApiPreferences(mod);
            TestApiPipelines(mod, harmonyPath);
            Console.WriteLine("PASS: " + passed + " API configuration checks against " + modPath);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void TestMiMo(Assembly mod)
    {
        object provider = Enum.Parse(providerType, "MiMo");
        Check(Convert.ToInt32(Enum.Parse(providerType, "Custom_OpenAI")) == 8 && Convert.ToInt32(provider) == 9,
            "MiMo is appended without renumbering saved providers");
        object config = Config("MiMo", "", "sk-synthetic-mimo-test", "mimo-v2.6-flash");
        Check(InvokeBool("IsConfigReady", config) && InvokeBool("IsPolicyAgentConfigReady", config), "MiMo supports translation and Policy Agent");
        Check(!InvokeBool("IsConfigReady", Config("MiMo", "", "", "mimo-v2.6-flash")), "MiMo requires its own API key");
        Check((string)api.GetMethod("GetBaseUrl", Static).Invoke(null, new[] { config }) == "https://api.xiaomimimo.com/v1",
            "MiMo defaults to the official pay-as-you-go endpoint");
        object plan = Config("MiMo", "https://token-plan-cn.xiaomimimo.com", "tp-synthetic-plan-key", "mimo-v2.6-pro");
        Check((string)api.GetMethod("GetBaseUrl", Static).Invoke(null, new[] { plan }) == "https://token-plan-cn.xiaomimimo.com/v1",
            "MiMo accepts a separate Token Plan URL and appends v1 once");
        object planWithV1 = Config("MiMo", "https://token-plan-sgp.xiaomimimo.com/v1", "tp-synthetic-plan-key", "mimo-v2.6-pro");
        Check((string)api.GetMethod("GetBaseUrl", Static).Invoke(null, new[] { planWithV1 }) == "https://token-plan-sgp.xiaomimimo.com/v1",
            "MiMo preserves the selected Token Plan region without duplicating v1");
        Check((string)api.GetMethod("GetApiEndpointError", Static).Invoke(null,
            new[] { Config("MiMo", "https://api.xiaomimimo.com/anthropic", "synthetic-key", "model") }) == "ATC_MiMo_OpenAiRequired",
            "MiMo explains when an Anthropic URL is used in the OpenAI route");

        Type headersType = mod.GetType("AutoTranslator_Core.ApiRequestHeaders", true);
        MethodInfo headers = headersType.GetMethod("Build", Static);
        var mimoHeaders = (Dictionary<string, string>)headers.Invoke(null, new object[] { provider, " tp-synthetic-plan-key ", null, false });
        Check(mimoHeaders["api-key"] == "tp-synthetic-plan-key" && !mimoHeaders.ContainsKey("Authorization") &&
            !mimoHeaders.ContainsKey("player2-game-key"), "MiMo emits its documented api-key header without unrelated credentials");
        var genericHeaders = (Dictionary<string, string>)headers.Invoke(null,
            new object[] { Enum.Parse(providerType, "Custom_OpenAI"), "synthetic-bearer", null, false });
        Check(genericHeaders["Authorization"] == "Bearer synthetic-bearer" && !genericHeaders.ContainsKey("api-key"),
            "adding MiMo preserves custom OpenAI Bearer authentication");

        MethodInfo prepare = api.GetMethod("PrepareProviderChatPayload", Static);
        JObject payload = JObject.Parse("{\"model\":\"mimo-v2.6-flash\",\"messages\":[{\"role\":\"system\",\"content\":\"Translate JSON\"},{\"role\":\"user\",\"content\":\"[\\\"hello\\\"]\"}],\"max_tokens\":4096}");
        JObject result = (JObject)prepare.Invoke(null, new object[] { payload, provider, false });
        Check(result["max_tokens"] == null && (int)result["max_completion_tokens"] == 4096, "MiMo converts the output budget to max_completion_tokens");
        Check((string)result["thinking"]["type"] == "disabled" && !(bool)result["stream"], "MiMo translation uses non-streaming output with thinking disabled");
        Check(JToken.DeepEquals(result["messages"], payload["messages"]), "MiMo preserves the mod's translation instructions and input JSON");
        Check(payload["max_tokens"] != null && payload["thinking"] == null, "MiMo payload preparation does not mutate the caller's data");
        JObject policyPayload = JObject.Parse("{\"model\":\"mimo-v2.6-pro\",\"messages\":[],\"max_tokens\":8192}");
        Check((int)((JObject)prepare.Invoke(null, new object[] { policyPayload, provider, false }))["max_completion_tokens"] == 8192,
            "MiMo preserves the independent Policy Agent output budget");
        object unchanged = prepare.Invoke(null, new object[] { payload, Enum.Parse(providerType, "Custom_OpenAI"), false });
        Check(ReferenceEquals(unchanged, payload), "other OpenAI providers keep their original payload");
        JObject player2 = (JObject)prepare.Invoke(null, new object[] { payload, Enum.Parse(providerType, "Custom_OpenAI"), true });
        Check(player2["model"] == null && player2["max_tokens"] != null && player2["thinking"] == null,
            "Player2 model handling remains separate from MiMo");

        MethodInfo fallback = api.GetMethod("TryUseMiMoModelSuggestions", Static);
        object fallbackConfig = Config("MiMo", "", "synthetic-key", "");
        Check((bool)fallback.Invoke(null, new object[] { fallbackConfig, 404, "test-fingerprint" }) &&
            (string)configType.GetField("SelectedModel").GetValue(fallbackConfig) == "mimo-v2.6-flash",
            "missing MiMo model-list endpoint offers current documented text models");
        Check((bool)fallback.Invoke(null, new object[] { config, 405, "test-fingerprint" }) &&
            (string)configType.GetField("SelectedModel").GetValue(config) == "mimo-v2.6-flash", "model suggestions preserve the configured model");
        foreach (int code in new[] { 0, 401, 403, 429, 500 })
            Check(!(bool)fallback.Invoke(null, new object[] { config, code, "test-fingerprint" }),
                "MiMo suggestions do not hide HTTP " + code + " failures");
        Check(!(bool)fallback.Invoke(null, new object[] { Config("Custom_OpenAI", "https://example.com/v1", "key", "model"), 404, "fp" }),
            "MiMo model fallback does not affect generic providers");
    }

    private static object Config(string provider, string url, string key, string model, bool player2 = false, string player2GameClientId = "")
    {
        object config = Activator.CreateInstance(configType);
        configType.GetField("Provider").SetValue(config, Enum.Parse(providerType, provider));
        configType.GetField("CustomBaseUrl").SetValue(config, url);
        configType.GetField("Key").SetValue(config, key);
        configType.GetField("SelectedModel").SetValue(config, model);
        configType.GetField("UsePlayer2Protocol").SetValue(config, player2);
        configType.GetField("Player2GameClientId").SetValue(config, player2GameClientId);
        return config;
    }

    private static void TestConnectionDiagnostics(Assembly mod)
    {
        Type diagnosticType = mod.GetType("AutoTranslator_Core.ApiConnectionDiagnostic", true);
        MethodInfo create = diagnosticType.GetMethod("Create", Static);
        MethodInfo redact = diagnosticType.GetMethod("Redact", Static);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        const string secret = "synthetic/private+credential123";
        Func<object, string, string> read = (value, name) => (string)diagnosticType.GetField(name, fields).GetValue(value);
        Func<bool, long, string, string, object> report = (success, code, error, body) => create.Invoke(null,
            new object[] { "Custom_OpenAI", "test-model", "https://api.example.com/v1/chat/completions?key=" + secret,
                success, code, error, body, new[] { secret } });

        object auth = report(false, 401, "HTTP/1.1 401 Unauthorized", "{\"error\":{\"message\":\"Invalid key " + secret + "\"}}");
        Check(read(auth, "HintKey") == "ATC_ConnectionTest_Unauthorized", "401 has an authentication explanation");
        Check(read(auth, "ServiceMessage") == "Invalid key [redacted]", "service error is preserved with echoed key hidden");
        MethodInfo notificationDetail = diagnosticType.GetMethod("GetNotificationDetail", BindingFlags.Instance | BindingFlags.NonPublic);
        Func<object, string> brief = value => (string)notificationDetail.Invoke(value, null);
        Check(brief(auth) == "Invalid key [redacted]", "notification prefers the redacted service reason over generic transport status");
        Check(brief(report(false, 0, "Unable to resolve\n\thost", "")) == "Unable to resolve host", "notification retains DNS errors on one line");
        Check(brief(report(false, 400, "", new string('a', 2000))).Length == 181, "long errors stay bounded in the corner notification");
        Check(brief(report(false, 400, "", new string('a', 179) + "😀tail")) == new string('a', 179) + "…", "notification truncation preserves Unicode character boundaries");
        Check(read(auth, "Endpoint") == "https://api.example.com/v1/chat/completions", "request URL excludes credential query");
        foreach (var item in new[] {
            new { Code = 403L, Hint = "Forbidden" }, new { Code = 404L, Hint = "NotFound" },
            new { Code = 429L, Hint = "RateLimit" }, new { Code = 503L, Hint = "ServerError" },
            new { Code = 400L, Hint = "Rejected" } })
            Check(read(report(false, item.Code, "", ""), "HintKey") == "ATC_ConnectionTest_" + item.Hint,
                item.Code + " has a distinct actionable explanation");
        Check(read(report(false, 0, "Request timed out", ""), "HintKey") == "ATC_ConnectionTest_Timeout", "transport timeout is visible without an HTTP code");
        Check(read(report(false, 0, "Unable to resolve host", ""), "TransportError").Contains("resolve host"), "DNS failure retains transport details");
        Check(read(report(false, 0, "", null), "HintKey") == "ATC_ConnectionTest_NoResponse", "missing response does not imply an invalid key");
        Check(read(report(false, 403, "", "<html><script>secret</script>Sign in</html>"), "ServiceMessage") == "", "gateway HTML is not rendered as an error");
        Check(read(report(false, 400, "", "{\"api_key\":\"unexpected-secret\",\"data\":{}}"), "ServiceMessage") == "", "unrecognized JSON fields are not copied into diagnostics");
        Check(read(report(false, 400, "", "{\"detail\":\"Model not available\"}"), "ServiceMessage") == "Model not available", "top-level detail error is retained");
        Check(read(report(false, 400, "", "Invalid request"), "ServiceMessage") == "Invalid request", "plain-text errors remain readable");
        object successReport = report(true, 200, "", "{\"choices\":[{\"message\":{\"content\":\"not diagnostic data\"}}]}");
        Check(brief(successReport) == "", "success notification does not expose response content");
        Check(read(successReport, "HintKey") == "ATC_ConnectionTest_Success" && read(successReport, "ServiceMessage") == "", "successful response content is not exported");
        object timeout = diagnosticType.GetMethod("TimedOut", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(successReport, null);
        Check(!(bool)diagnosticType.GetField("Succeeded", fields).GetValue(timeout) &&
            (long)diagnosticType.GetField("HttpCode", fields).GetValue(timeout) == 0 && read(timeout, "HintKey") == "ATC_ConnectionTest_Timeout",
            "watchdog timeout discards an earlier success status");

        Func<string, string> clean = value => (string)redact.Invoke(null, new object[] { value, new[] { secret } });
        Check(!clean("echo " + secret).Contains(secret), "literal configured credential is redacted");
        Check(!clean("echo " + Uri.EscapeDataString(secret)).Contains("credential123"), "percent-encoded credential is redacted");
        Check(!clean("synthetic\\u002fprivate+credential123").Contains("credential123"), "JSON unicode-escaped credential is redacted");
        Check(!((string)redact.Invoke(null, new object[] { "echo opaque%2Fcredential456", new[] { "opaque%2Fcredential456" } })).Contains("credential456"), "credential containing percent escapes is redacted after decoding");
        Check(!clean("Bearer unknownCredential987").Contains("unknownCredential987"), "unconfigured bearer credentials are redacted");
        Check(!clean("{\"x-api-key\":\"unknownCredential987\"}").Contains("unknownCredential987"), "echoed x-api-key field is redacted");
        Check(!clean("api_key=unknownCredential987&message=denied").Contains("unknownCredential987"), "query-style key field is redacted");
        Check(!clean("sk-syntheticSecret987654321").Contains("syntheticSecret"), "recognizable key without a field label is redacted");
        Check(clean("<color=red>denied</color>\0").Equals("denied"), "server rich text and control characters are removed");
        Check(clean(new string('a', 2000)).Length <= 1501, "display and clipboard data have bounded size");
        Check(!clean(new string('a', 1490) + secret).Contains("synthetic"), "key crossing the display truncation boundary is removed before truncation");

        object config = Config("Custom_OpenAI", "https://api.example.com/v1", secret, "model-a");
        MethodInfo fingerprint = api.GetMethod("GetConnectionTestFingerprint", Static);
        string before = (string)fingerprint.Invoke(null, new[] { config });
        configType.GetField("SelectedModel").SetValue(config, "model-b");
        Check(before != (string)fingerprint.Invoke(null, new[] { config }), "changing the model hides the previous test result");
        Check(configType.GetField("LastConnectionTest", fields).IsDefined(typeof(NonSerializedAttribute), false), "diagnostic results are not serialized into player settings");

        // Neither stale callbacks nor edited configurations may show a result for the wrong request.
        configType.GetField("TestGeneration").SetValue(config, 4);
        configType.GetField("IsTesting").SetValue(config, true);
        MethodInfo complete = api.GetMethod("CompleteConnectionTest", Static);
        complete.Invoke(null, new object[] { config, 3, before, auth });
        Check((bool)configType.GetField("IsTesting").GetValue(config) &&
            configType.GetField("LastConnectionTest", fields).GetValue(config) == null, "late callback cannot finish a newer test");
        complete.Invoke(null, new object[] { config, 4, before, auth });
        Check(!(bool)configType.GetField("IsTesting").GetValue(config) &&
            (string)configType.GetField("ConnectionTestFingerprint", fields).GetValue(config) == before,
            "edited configuration releases testing state while retaining the original result fingerprint");
    }

    private static bool InvokeBool(string method, object config)
    {
        return (bool)api.GetMethod(method, Static, null, new[] { configType }, null).Invoke(null, new[] { config });
    }

    private static void Accept(string input, string key, string expected, string name)
    {
        object[] args = { input, key, null, null };
        bool valid = (bool)normalize.Invoke(null, args);
        Check(valid && (string)args[2] == expected && (string)args[3] == "", name);
    }

    private static void Reject(string input, string key, string expectedError, string name)
    {
        object[] args = { input, key, null, null };
        bool valid = (bool)normalize.Invoke(null, args);
        Check(!valid && (string)args[2] == "" && (string)args[3] == expectedError, name);
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        passed++;
        Console.WriteLine("PASS: " + name);
    }
}
