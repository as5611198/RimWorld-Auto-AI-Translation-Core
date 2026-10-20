using Newtonsoft.Json.Linq;

namespace AutoTranslator_Core
{
    internal static class MiMoApi
    {
        internal const string BaseUrl = "https://api.xiaomimimo.com/v1";
        internal const string DefaultModel = "mimo-v2.6-flash";

        // Suggestions from the official model list, not a claim about this key's model access.
        internal static string[] SuggestedModels()
        {
            return new[] { DefaultModel, "mimo-v2.6-pro" };
        }

        internal static JObject PrepareChatPayload(object payload)
        {
            JObject result = payload is JObject json ? (JObject)json.DeepClone() : JObject.FromObject(payload);
            if (result["max_completion_tokens"] == null && result["max_tokens"] != null)
                result["max_completion_tokens"] = result["max_tokens"].DeepClone();
            result.Remove("max_tokens");
            result["stream"] = false;
            result["thinking"] = new JObject { ["type"] = "disabled" };
            return result;
        }
    }

    public static partial class AutoTranslatorAPI
    {
        internal static object PrepareProviderChatPayload(object payload, TranslatorProvider provider, bool usePlayer2Protocol)
        {
            if (provider == TranslatorProvider.MiMo) return MiMoApi.PrepareChatPayload(payload);
            if (provider == TranslatorProvider.Custom_OpenAI && usePlayer2Protocol)
                return Player2LocalApi.PrepareChatPayload(payload);
            return payload;
        }
    }
}
