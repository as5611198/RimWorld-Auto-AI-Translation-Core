using System;
using System.IO;
using System.Reflection;

internal static class Program
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
            foreach (string directory in new[] { Path.GetDirectoryName(modPath), Path.GetDirectoryName(harmonyPath), gameDirectory })
            {
                string path = Path.Combine(directory, file);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try
        {
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
            Check(InvokeBool("ShouldAutoFetchModels", custom), "keyless custom service passes auto-fetch gate");
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
            Console.WriteLine("PASS: " + passed + " API configuration checks against " + modPath);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
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
