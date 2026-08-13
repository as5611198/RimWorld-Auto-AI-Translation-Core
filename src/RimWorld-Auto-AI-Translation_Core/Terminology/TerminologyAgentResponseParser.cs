using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core.Terminology
{
    internal static class TerminologyAgentResponseParser
    {
        internal static bool TryParse(
            string raw,
            IEnumerable<string> expectedTermIds,
            out List<TerminologyAgentDecision> decisions)
        {
            decisions = new List<TerminologyAgentDecision>();
            var expected = new HashSet<string>(
                expectedTermIds ?? Array.Empty<string>(),
                StringComparer.Ordinal);
            if (expected.Count == 0 || string.IsNullOrWhiteSpace(raw)) return false;
            try
            {
                JToken root = JToken.Parse(raw.Trim());
                JArray array = root as JArray ?? (root as JObject)?["decisions"] as JArray;
                if (array == null || array.Count != expected.Count) return false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JObject item in array.OfType<JObject>())
                {
                    string id = item["termId"]?.ToString() ?? string.Empty;
                    string decision = (item["decision"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
                    string target = (item["target"]?.ToString() ?? string.Empty).Trim();
                    string role = (item["semanticRole"]?.ToString() ?? string.Empty).Trim();
                    string reason = (item["reason"]?.ToString() ?? string.Empty).Trim();
                    if (!expected.Contains(id) || !seen.Add(id) ||
                        (decision != "accept" && decision != "review" && decision != "reject") ||
                        reason.Length > 160 || role.Length > 40 || target.Length > 160 ||
                        (decision == "accept" && target.Length == 0))
                    {
                        decisions.Clear();
                        return false;
                    }
                    decisions.Add(new TerminologyAgentDecision
                    {
                        TermId = id,
                        Decision = decision,
                        Target = target,
                        SemanticRole = role,
                        Reason = reason
                    });
                }
                return decisions.Count == expected.Count;
            }
            catch
            {
                decisions.Clear();
                return false;
            }
        }
    }
}
