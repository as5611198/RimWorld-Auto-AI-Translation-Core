using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AutoTranslator_Core
{
    // Capability rules deliberately leave unknown models/gateways at their own defaults.
    // Official references and exceptions are recorded in docs/API_THINKING_POLICY.md.
    internal static class ApiThinkingPolicy
    {
        private sealed class Plan
        {
            internal JObject Fields = new JObject();
            internal bool Thinking;
            internal bool CompletionBudget;
        }

        private sealed class RouterModels
        {
            internal DateTime FetchedUtc;
            internal Dictionary<string, JObject> Reasoning;
        }
        private static readonly ConcurrentDictionary<string, RouterModels> RouterCache = new ConcurrentDictionary<string, RouterModels>();

        internal static JObject Prepare(object payload, ApiKeyConfig config, string endpoint, bool policyReview)
        {
            JObject result = payload is JObject json ? (JObject)json.DeepClone() : JObject.FromObject(payload);
            if (config.Provider == TranslatorProvider.Custom_OpenAI && config.UsePlayer2Protocol)
                return Player2LocalApi.PrepareChatPayload(result);
            TranslatorProvider provider = ResolveProvider(config.Provider, endpoint);
            string model = (config.SelectedModel ?? string.Empty).Trim().ToLowerInvariant();
            Plan plan = Resolve(provider, model, endpoint, ThinkingIntent(config, policyReview));
            if (provider == TranslatorProvider.MiMo)
            {
                // Keep MiMo's protocol normalization separate from the request's thinking intent.
                result.Remove("thinking");
                result["stream"] = false;
                plan.CompletionBudget = true;
            }
            if (plan.CompletionBudget && result["max_tokens"] != null)
            {
                if (result["max_completion_tokens"] == null) result["max_completion_tokens"] = result["max_tokens"].DeepClone();
                result.Remove("max_tokens");
            }
            if (provider == TranslatorProvider.Google)
            {
                if (plan.Fields.Count > 0)
                {
                    JObject generation = result["generationConfig"] as JObject ?? new JObject();
                    generation["thinkingConfig"] = plan.Fields;
                    result["generationConfig"] = generation;
                }
            }
            else
                foreach (var field in plan.Fields.Properties()) result[field.Name] = field.Value.DeepClone();
            return result;
        }

        internal static int PolicyReasoningReserve(ApiKeyConfig config, string endpoint)
        {
            if (config == null || (config.Provider == TranslatorProvider.Custom_OpenAI && config.UsePlayer2Protocol)) return 0;
            return Resolve(ResolveProvider(config.Provider, endpoint),
                (config.SelectedModel ?? string.Empty).Trim().ToLowerInvariant(), endpoint, ThinkingIntent(config, true)).Thinking ? 4096 : 0;
        }

        private static bool ThinkingIntent(ApiKeyConfig config, bool policyReview)
        {
            return config.ThinkingMode == ApiThinkingMode.Enabled ||
                config.ThinkingMode != ApiThinkingMode.PreferOff && policyReview;
        }

        internal static string StatusKey(ApiKeyConfig config, string endpoint, bool policyReview)
        {
            if (config.Provider == TranslatorProvider.DeepL) return "ATC_Advanced_ThinkingNotApplicable";
            if (config.Provider == TranslatorProvider.Custom_OpenAI && config.UsePlayer2Protocol)
                return "ATC_Advanced_ThinkingPlayer2";
            var provider = ResolveProvider(config.Provider, endpoint);
            string model = (config.SelectedModel ?? "").Trim().ToLowerInvariant();
            bool intent = ThinkingIntent(config, policyReview);
            Plan plan = Resolve(provider, model, endpoint, intent);
            if (plan.Fields.Count == 0 && !plan.Thinking) return "ATC_Advanced_ThinkingUnknown";
            if (!intent && plan.Thinking) return "ATC_Advanced_ThinkingMinimum";
            return plan.Thinking ? "ATC_Advanced_ThinkingOn" : "ATC_Advanced_ThinkingOff";
        }

        private static Plan Resolve(TranslatorProvider provider, string model, string endpoint, bool review)
        {
            switch (provider)
            {
                case TranslatorProvider.MiMo:
                    if (new[] { "mimo-v2.5", "mimo-v2.5-pro", "mimo-v2.6-flash", "mimo-v2.6-pro", "mimo-v2.6-pro-ultraspeed" }.Contains(model))
                        return ThinkingSwitch(review);
                    break;
                case TranslatorProvider.DeepSeek:
                    if (DeepSeekHybrid(model))
                    {
                        Plan plan = ThinkingSwitch(review);
                        if (review && (Family(model, "deepseek-flash") || model.StartsWith("deepseek-v4", StringComparison.Ordinal)))
                            plan.Fields["reasoning_effort"] = "low";
                        return plan;
                    }
                    if (Family(model, "deepseek-reasoner") || Family(model, "deepseek-r1")) return new Plan { Thinking = true };
                    break;
                case TranslatorProvider.GLM:
                    if (Family(model, "glm-5.3"))
                    {
                        Plan plan = ThinkingSwitch(true);
                        plan.Fields["reasoning_effort"] = "low";
                        return plan;
                    }
                    if (new[] { "glm-4.5", "glm-4.5v", "glm-4.6", "glm-4.6v", "glm-4.7", "glm-5", "glm-5v", "glm-5.1", "glm-5.2" }.Any(f => Family(model, f)))
                    {
                        Plan plan = ThinkingSwitch(review);
                        // GLM-5.2 maps low/medium to high; high is its lowest actual thinking effort.
                        if (review && Family(model, "glm-5.2")) plan.Fields["reasoning_effort"] = "high";
                        return plan;
                    }
                    break;
                case TranslatorProvider.Alibaba:
                    if (AlibabaMandatory(model)) return new Plan { Thinking = true };
                    if (AlibabaHybrid(model))
                    {
                        var plan = new Plan { Thinking = review, Fields = new JObject { ["enable_thinking"] = review } };
                        // Budget support varies by vendor even within the same gateway.
                        if (review && model.StartsWith("qwen", StringComparison.Ordinal)) plan.Fields["thinking_budget"] = 1024;
                        return plan;
                    }
                    break;
                case TranslatorProvider.OpenAI:
                    return OpenAiPlan(model, review);
                case TranslatorProvider.Grok:
                    if (Family(model, "grok-4.3")) return Effort(review ? "low" : "none");
                    if (new[] { "grok-4.5", "grok-4.6", "grok-4.7", "grok-3-mini" }.Any(f => Family(model, f))) return Effort("low");
                    break;
                case TranslatorProvider.Google:
                    return GooglePlan(model, review);
                case TranslatorProvider.OpenRouter:
                    return RouterPlan(model, endpoint, review);
            }
            return new Plan();
        }

        private static Plan OpenAiPlan(string model, bool review)
        {
            // Chat-only and Codex-only aliases have different parameter/API support.
            if (model.Contains("chat") || model.Contains("codex")) return new Plan();
            Plan plan;
            // Pro models are Responses-only: this Chat Completions client must not invent a toggle.
            if (new[] { "gpt-5-pro", "gpt-5.2-pro", "gpt-5.4-pro", "gpt-5.5-pro", "o1-pro", "o3-pro" }.Any(f => Family(model, f)))
                plan = new Plan { Thinking = true };
            else if (model.Contains("-pro")) return new Plan();
            else if (new[] { "gpt-5.1", "gpt-5.2", "gpt-5.4", "gpt-5.5", "gpt-5.6" }.Any(f => Family(model, f))) plan = Effort(review ? "low" : "none");
            else if (Family(model, "gpt-5")) plan = Effort(review ? "low" : "minimal");
            else if (new[] { "o1", "o3", "o4-mini", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-6.1-sol" }.Any(f => Family(model, f))) plan = Effort("low");
            else return new Plan();
            plan.CompletionBudget = true;
            return plan;
        }

        private static Plan GooglePlan(string model, bool review)
        {
            if (Family(model, "gemini-2.5-flash") || Family(model, "gemini-2.5-flash-lite"))
                return new Plan { Thinking = review, Fields = new JObject { ["thinkingBudget"] = review ? 1024 : 0 } };
            if (Family(model, "gemini-2.5-pro"))
                return new Plan { Thinking = true, Fields = new JObject { ["thinkingBudget"] = review ? 1024 : 128 } };
            if (Family(model, "gemini-3.1-flash-lite-image"))
                return new Plan { Thinking = true, Fields = new JObject { ["thinkingLevel"] = review ? "high" : "minimal" } };
            if (new[] { "gemini-3-flash", "gemini-3.1-flash-lite", "gemini-3.5-flash-lite", "gemini-3.6-flash" }.Any(f => Family(model, f)))
                return new Plan { Thinking = true, Fields = new JObject { ["thinkingLevel"] = review ? "low" : "minimal" } };
            if (new[] { "gemini-3-pro", "gemini-3.1-pro", "gemini-3.8-flash", "gemini-4-argon" }.Any(f => Family(model, f)))
                return new Plan { Thinking = true, Fields = new JObject { ["thinkingLevel"] = "low" } };
            return new Plan();
        }

        private static bool DeepSeekHybrid(string model)
        {
            return new[] { "deepseek-chat", "deepseek-v3.1", "deepseek-v3.2", "deepseek-v4-flash", "deepseek-v4-pro", "deepseek-v4.1-flash", "deepseek-flash" }
                .Any(f => Family(model, f));
        }
        private static bool AlibabaMandatory(string model)
        {
            return model.Contains("-thinking") || Family(model, "qwq") || Family(model, "deepseek-r1") ||
                Family(model, "glm-5.3") || Family(model, "qwen3.8-2.4t-a95b") ||
                Family(model, "qwen3.7-max-preview") || model == "qwen3.7-max-2026-05-17";
        }
        private static bool AlibabaHybrid(string model)
        {
            // Old snapshots predate the hybrid switch; don't retroactively add new parameters.
            if (DateTime.TryParseExact(model.Length >= 10 ? model.Substring(model.Length - 10) : "",
                "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime snapshot))
            {
                if (Family(model, "qwen-plus") && snapshot < new DateTime(2025, 4, 28)) return false;
                if (Family(model, "qwen-flash") && snapshot < new DateTime(2025, 7, 28)) return false;
                if (Family(model, "qwen3-max") && snapshot < new DateTime(2026, 1, 23)) return false;
            }
            return (new[] { "qwen-plus", "qwen-flash", "qwen3-max", "qwen3.5-plus", "qwen3.5-flash", "qwen3.5-397b-a17b", "qwen3.5-122b-a10b", "qwen3.5-27b", "qwen3.5-35b-a3b", "qwen3.6-plus", "qwen3.6-flash", "qwen3.6-35b-a3b", "qwen3.7-max", "qwen3.7-plus", "qwen3.7-flash", "qwen3.8-max", "qwen3.8-flash", "qwen3.8-27b", "qwen3-235b-a22b", "qwen3-30b-a3b", "qwen3-32b", "qwen3-14b", "qwen3-8b", "qwen3-4b", "qwen3-1.7b", "qwen3-0.6b", "mimo-v2.5-pro", "kimi-k2.5", "kimi-k2.6", "glm-5", "glm-5.1", "glm-5.2" }
                .Any(f => Family(model, f)) && !model.Contains("instruct")) || DeepSeekHybrid(model);
        }
        private static Plan ThinkingSwitch(bool enabled)
        {
            return new Plan { Thinking = enabled, Fields = new JObject { ["thinking"] = new JObject { ["type"] = enabled ? "enabled" : "disabled" } } };
        }
        private static Plan Effort(string effort)
        {
            return new Plan { Thinking = effort != "none", Fields = new JObject { ["reasoning_effort"] = effort } };
        }
        private static bool Family(string model, string family)
        {
            return model == family || model.StartsWith(family + "-", StringComparison.Ordinal);
        }

        private static TranslatorProvider ResolveProvider(TranslatorProvider provider, string endpoint)
        {
            if (provider != TranslatorProvider.Custom_OpenAI || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return provider;
            string host = uri.Host.ToLowerInvariant();
            if (host == "api.deepseek.com") return TranslatorProvider.DeepSeek;
            if (host == "api.xiaomimimo.com" || host.EndsWith(".xiaomimimo.com", StringComparison.Ordinal)) return TranslatorProvider.MiMo;
            if (host == "open.bigmodel.cn" || host == "api.z.ai") return TranslatorProvider.GLM;
            if (host == "dashscope.aliyuncs.com" || (host.StartsWith("dashscope-", StringComparison.Ordinal) && host.EndsWith(".aliyuncs.com", StringComparison.Ordinal)) || host.EndsWith(".maas.aliyuncs.com", StringComparison.Ordinal)) return TranslatorProvider.Alibaba;
            if (host == "api.openai.com") return TranslatorProvider.OpenAI;
            if (host == "openrouter.ai") return TranslatorProvider.OpenRouter;
            if (host == "api.x.ai" || host == "us.api.x.ai") return TranslatorProvider.Grok;
            return provider;
        }

        internal static void RememberModels(ApiKeyConfig config, string endpoint, string body)
        {
            if (ResolveProvider(config.Provider, endpoint) != TranslatorProvider.OpenRouter) return;
            try
            {
                var models = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                foreach (JObject model in (JObject.Parse(body)["data"] as JArray ?? new JArray()).OfType<JObject>())
                    if (model["id"]?.Type == JTokenType.String && model["reasoning"] is JObject reasoning)
                        models[model["id"].ToString()] = (JObject)reasoning.DeepClone();
                RouterCache[endpoint.TrimEnd('/')] = new RouterModels { FetchedUtc = DateTime.UtcNow, Reasoning = models };
            }
            catch (Newtonsoft.Json.JsonException) { } // Optional capability metadata cannot invalidate a model list.
        }

        private static Plan RouterPlan(string model, string endpoint, bool review)
        {
            if (!RouterCache.TryGetValue(endpoint.TrimEnd('/'), out var cache) || DateTime.UtcNow - cache.FetchedUtc > TimeSpan.FromHours(1) ||
                !cache.Reasoning.TryGetValue(model, out JObject metadata)) return new Plan();
            bool? mandatory = metadata["mandatory"]?.Type == JTokenType.Boolean ? (bool?)metadata["mandatory"] : null;
            string[] efforts = (metadata["supported_efforts"] as JArray)?.Where(t => t.Type == JTokenType.String).Values<string>().ToArray();
            bool anyEffort = metadata["supported_efforts"]?.Type == JTokenType.Null;
            bool supportsBudget = metadata["supports_max_tokens"]?.Type == JTokenType.Boolean && (bool)metadata["supports_max_tokens"];
            if (mandatory == null && (efforts == null || efforts.Length == 0) && !anyEffort && !supportsBudget) return new Plan();
            bool canDisable = mandatory != true && (mandatory == false || efforts != null && efforts.Contains("none"));
            if (!review && canDisable)
                return new Plan { Fields = new JObject { ["reasoning"] = new JObject { ["enabled"] = false } } };
            var fields = new JObject { ["enabled"] = true, ["exclude"] = true };
            string effort = anyEffort ? "low" : new[] { "low", "minimal", "medium", "high", "xhigh", "max" }.FirstOrDefault(e => efforts != null && efforts.Contains(e));
            if (effort != null) fields["effort"] = effort;
            else if (supportsBudget) fields["max_tokens"] = 1024;
            return new Plan { Thinking = true, Fields = new JObject { ["reasoning"] = fields } };
        }
    }

    public static partial class AutoTranslatorAPI
    {
        internal static object PrepareApiRequestPayload(object payload, ApiKeyConfig config, bool policyReview)
        {
            return ApiThinkingPolicy.Prepare(payload, config, GetBaseUrl(config), policyReview);
        }

        internal static string GetThinkingStatusKey(ApiKeyConfig config, bool policyReview)
        {
            return ApiThinkingPolicy.StatusKey(config, GetBaseUrl(config), policyReview);
        }
    }
}
