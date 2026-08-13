using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core.Terminology
{
    internal sealed class TerminologyApplication
    {
        internal string TermId { get; set; } = string.Empty;
        internal string SourceForm { get; set; } = string.Empty;
        internal string SemanticRole { get; set; } = string.Empty;
        internal string Target { get; set; } = string.Empty;
    }

    internal sealed class TerminologyApplicationValidationResult
    {
        internal bool IsValid { get; set; }
        internal string ErrorCode { get; set; } = string.Empty;
    }

    internal static class TerminologyApplicationValidator
    {
        private static readonly Regex ProtectedTokenRegex = new Regex(
            @"(\{[^{}\r\n]+\}|\[[^\[\]\r\n]+\]|\$[A-Za-z0-9_]+|%[A-Za-z]|<[^<>\r\n]+>)",
            RegexOptions.Compiled);

        internal static TerminologyApplicationValidationResult Validate(
            IReadOnlyList<TerminologyCandidate> requestTerms,
            IReadOnlyList<string> sourceTexts,
            IReadOnlyList<string> translatedTexts,
            IReadOnlyList<TerminologyApplication> applications)
        {
            if (sourceTexts == null || translatedTexts == null || sourceTexts.Count != translatedTexts.Count)
                return Fail("batch_shape_mismatch");
            for (int index = 0; index < sourceTexts.Count; index++)
            {
                if (!ProtectedTokensMatch(sourceTexts[index], translatedTexts[index]))
                    return Fail("placeholder_mismatch");
            }

            var termsById = (requestTerms ?? Array.Empty<TerminologyCandidate>())
                .Where(term => term != null && !string.IsNullOrWhiteSpace(term.TermId))
                .GroupBy(term => term.TermId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            string sourceCorpus = string.Join("\n", sourceTexts);
            string targetCorpus = string.Join("\n", translatedTexts);
            foreach (TerminologyApplication application in applications ?? Array.Empty<TerminologyApplication>())
            {
                if (application == null || !termsById.TryGetValue(application.TermId ?? string.Empty, out TerminologyCandidate term))
                    return Fail("unknown_term_id");
                if (!ContainsForm(sourceCorpus, application.SourceForm) ||
                    !ContainsForm(sourceCorpus, term.SourceForm))
                    return Fail("source_not_present");
                if (!ContainsForm(targetCorpus, application.Target))
                    return Fail("target_not_present");
                if (!string.Equals((term.Target ?? string.Empty).Trim(), (application.Target ?? string.Empty).Trim(), StringComparison.Ordinal))
                    return Fail("target_not_authorized");
                if (seen.TryGetValue(application.TermId, out string prior) &&
                    !string.Equals(prior, application.Target, StringComparison.Ordinal))
                    return Fail("conflicting_application");
                seen[application.TermId] = application.Target;
            }

            foreach (TerminologyCandidate term in termsById.Values)
            {
                bool sourceWasRelevant = ContainsForm(sourceCorpus, term.SourceForm);
                bool authorizedTargetWasUsed = ContainsForm(targetCorpus, term.Target);
                if (sourceWasRelevant && authorizedTargetWasUsed && !seen.ContainsKey(term.TermId))
                    return Fail("missing_term_application");
            }
            return new TerminologyApplicationValidationResult { IsValid = true };
        }

        private static bool ContainsForm(string corpus, string form)
        {
            if (string.IsNullOrWhiteSpace(corpus) || string.IsNullOrWhiteSpace(form)) return false;
            return corpus.IndexOf(form.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ProtectedTokensMatch(string source, string target)
        {
            string[] left = ProtectedTokenRegex.Matches(source ?? string.Empty).Cast<Match>().Select(match => match.Value).OrderBy(value => value).ToArray();
            string[] right = ProtectedTokenRegex.Matches(target ?? string.Empty).Cast<Match>().Select(match => match.Value).OrderBy(value => value).ToArray();
            return left.SequenceEqual(right, StringComparer.Ordinal);
        }

        private static TerminologyApplicationValidationResult Fail(string errorCode)
        {
            return new TerminologyApplicationValidationResult { IsValid = false, ErrorCode = errorCode };
        }
    }
}
