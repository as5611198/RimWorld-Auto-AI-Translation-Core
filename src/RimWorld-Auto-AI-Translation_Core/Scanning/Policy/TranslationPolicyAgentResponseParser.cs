using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core.TranslationPolicy
{
    public static class TranslationPolicyAgentResponseParser
    {
        private static readonly HashSet<string> RequiredProperties =
            new HashSet<string>(new[] { "id", "decision", "reason" }, StringComparer.Ordinal);

        public static bool TryParse(
            string raw,
            IEnumerable<string> expectedIds,
            out List<TranslationPolicyAgentGroupDecision> decisions)
        {
            decisions = null;
            HashSet<string> expected = new HashSet<string>(
                (expectedIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);
            if (expected.Count == 0 || string.IsNullOrWhiteSpace(raw)) return false;
            if (raw.Length > 262144) return false;

            try
            {
                JArray array = JArray.Parse(
                    NormalizeJsonArray(raw),
                    new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                if (array.Count != expected.Count) return false;

                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                List<TranslationPolicyAgentGroupDecision> parsed =
                    new List<TranslationPolicyAgentGroupDecision>(array.Count);
                foreach (JToken token in array)
                {
                    JObject item = token as JObject;
                    if (item == null) return false;

                    List<JProperty> properties = item.Properties().ToList();
                    if (properties.Count != RequiredProperties.Count ||
                        properties.Any(property => !RequiredProperties.Contains(property.Name)))
                    {
                        return false;
                    }

                    JToken idToken = item["id"];
                    JToken decisionToken = item["decision"];
                    JToken reasonToken = item["reason"];
                    if (idToken == null || idToken.Type != JTokenType.String ||
                        decisionToken == null || decisionToken.Type != JTokenType.String ||
                        reasonToken == null || reasonToken.Type != JTokenType.String)
                    {
                        return false;
                    }

                    string id = idToken.Value<string>();
                    string rawDecision = decisionToken.Value<string>();
                    string reason = (reasonToken.Value<string>() ?? string.Empty).Trim();
                    if (!expected.Contains(id) || !seen.Add(id) || reason.Length == 0 || reason.Length > 240)
                        return false;

                    TranslationPolicyAgentDecision decision;
                    if (string.Equals(rawDecision, "allow", StringComparison.Ordinal))
                        decision = TranslationPolicyAgentDecision.Allow;
                    else if (string.Equals(rawDecision, "deny", StringComparison.Ordinal))
                        decision = TranslationPolicyAgentDecision.Deny;
                    else if (string.Equals(rawDecision, "review", StringComparison.Ordinal))
                        decision = TranslationPolicyAgentDecision.Review;
                    else
                        return false;

                    parsed.Add(new TranslationPolicyAgentGroupDecision
                    {
                        Id = id,
                        Decision = decision,
                        Reason = reason
                    });
                }

                decisions = parsed.OrderBy(decision => decision.Id, StringComparer.Ordinal).ToList();
                return true;
            }
            catch
            {
                decisions = null;
                return false;
            }
        }

        private static string NormalizeJsonArray(string raw)
        {
            string normalized = (raw ?? string.Empty).Trim().TrimStart('\uFEFF').Trim();
            if (!normalized.StartsWith("```", StringComparison.Ordinal)) return normalized;

            int firstLineBreak = normalized.IndexOf('\n');
            if (firstLineBreak < 0) return normalized;

            normalized = normalized.Substring(firstLineBreak + 1).Trim();
            if (normalized.EndsWith("```", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - 3).Trim();
            }

            return normalized;
        }
    }
}
