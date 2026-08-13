using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core.Terminology
{
    internal static class TerminologyPromptContextBuilder
    {
        internal static string Build(
            IEnumerable<TerminologyCandidate> availableTerms,
            IEnumerable<string> batchTexts,
            int maxTerms = 20,
            int maxCharacters = 2000)
        {
            string corpus = string.Join("\n", batchTexts ?? Enumerable.Empty<string>());
            if (string.IsNullOrWhiteSpace(corpus)) return string.Empty;

            List<TerminologyCandidate> relevant = SelectRelevant(availableTerms, batchTexts, maxTerms);
            if (relevant.Count == 0) return string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine("[RELEVANT TERMINOLOGY FOR THIS BATCH ONLY]");
            builder.AppendLine("Use these mappings when the matching source term has the same meaning. Preserve protected variables and grammar tokens.");
            builder.AppendLine("When this terminology block is present, return a top-level termApplications array. For every mapping actually used, add {termId,sourceForm,semanticRole,target}; otherwise return an empty array.");
            foreach (TerminologyCandidate term in relevant)
            {
                string line = "- " + term.SourceForm.Trim() + " => " + term.Target.Trim() +
                              " (termId=" + term.TermId.Trim() + ")";
                if (builder.Length + line.Length + 1 > Math.Max(256, maxCharacters)) break;
                builder.AppendLine(line);
            }
            return builder.ToString().TrimEnd();
        }

        internal static List<TerminologyCandidate> SelectRelevant(
            IEnumerable<TerminologyCandidate> availableTerms,
            IEnumerable<string> batchTexts,
            int maxTerms = 20)
        {
            string corpus = string.Join("\n", batchTexts ?? Enumerable.Empty<string>());
            if (string.IsNullOrWhiteSpace(corpus)) return new List<TerminologyCandidate>();
            return (availableTerms ?? Enumerable.Empty<TerminologyCandidate>())
                .Where(term => term != null &&
                    !string.IsNullOrWhiteSpace(term.TermId) &&
                    !string.IsNullOrWhiteSpace(term.SourceForm) &&
                    !string.IsNullOrWhiteSpace(term.Target) &&
                    SourceAppears(corpus, term.SourceForm))
                .OrderByDescending(GetPriority)
                .ThenByDescending(term => term.Score)
                .GroupBy(term => term.NormalizedForm ?? term.SourceForm, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(Math.Max(1, maxTerms))
                .ToList();
        }

        private static bool SourceAppears(string corpus, string sourceForm)
        {
            string source = (sourceForm ?? string.Empty).Trim();
            if (source.Length == 0) return false;
            return Regex.IsMatch(
                corpus,
                @"(?<![A-Za-z0-9])" + Regex.Escape(source) + @"(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static int GetPriority(TerminologyCandidate term)
        {
            int status = string.Equals(term.Status, TerminologyStatus.UserApproved, StringComparison.OrdinalIgnoreCase) ? 600 :
                string.Equals(term.Status, TerminologyStatus.ModPersistent, StringComparison.OrdinalIgnoreCase) ? 400 :
                string.Equals(term.Status, TerminologyStatus.GroupPersistent, StringComparison.OrdinalIgnoreCase) ? 350 :
                string.Equals(term.Status, TerminologyStatus.SessionActive, StringComparison.OrdinalIgnoreCase) ? 200 : 0;
            int scope = string.Equals(term.ScopeKind, TerminologyScope.Mod, StringComparison.OrdinalIgnoreCase) ? 40 :
                string.Equals(term.ScopeKind, TerminologyScope.ModGroup, StringComparison.OrdinalIgnoreCase) ? 30 :
                string.Equals(term.ScopeKind, TerminologyScope.Session, StringComparison.OrdinalIgnoreCase) ? 20 : 10;
            return status + scope;
        }
    }
}
