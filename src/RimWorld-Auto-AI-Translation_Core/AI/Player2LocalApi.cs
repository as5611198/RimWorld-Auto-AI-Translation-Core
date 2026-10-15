using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AutoTranslator_Core
{
    internal static class Player2LocalApi
    {
        internal const string DefaultBaseUrl = "http://127.0.0.1:4315/v1";

        internal static bool TryGetGameClientId(string value, out string clientId)
        {
            Guid parsed;
            bool valid = Guid.TryParse(value, out parsed) && parsed != Guid.Empty;
            clientId = valid ? parsed.ToString("D") : string.Empty;
            return valid;
        }

        internal static string NormalizeBaseUrl(string normalizedUrl)
        {
            if (string.IsNullOrEmpty(normalizedUrl)) return DefaultBaseUrl;
            return new Uri(normalizedUrl).AbsolutePath.Trim('/') == ""
                ? normalizedUrl.TrimEnd('/') + "/v1"
                : normalizedUrl;
        }

        internal static JObject PrepareChatPayload(object payload)
        {
            JObject result = JObject.FromObject(payload);
            // Player2 selects the model in its desktop app; do not invent a model ID.
            result.Remove("model");
            result["stream"] = false;
            return result;
        }
    }

    internal static class ApiRequestHeaders
    {
        internal static Dictionary<string, string> Build(TranslatorProvider provider, string apiKey, string gameClientId, bool usePlayer2Protocol)
        {
            var headers = new Dictionary<string, string> { { "Content-Type", "application/json" } };
            if (usePlayer2Protocol)
            {
                string id;
                if (!Player2LocalApi.TryGetGameClientId(gameClientId, out id))
                    throw new InvalidOperationException("Player2 requires this mod's registered Game Client ID.");
                headers.Add("player2-game-key", id);
                return headers;
            }
            string key = (apiKey ?? string.Empty).Trim();
            if (key.Length > 0 && provider != TranslatorProvider.Google)
                headers.Add("Authorization", (provider == TranslatorProvider.DeepL ? "DeepL-Auth-Key " : "Bearer ") + key);
            return headers;
        }
    }
}
