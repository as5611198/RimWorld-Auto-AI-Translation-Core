using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace AutoTranslator_Core.Workflow
{
    public sealed class ReferenceDictionaryEntry
    {
        public string EntryId { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = string.Empty;
        public string ScopeModIdentity { get; set; } = string.Empty;
        public string SourceForm { get; set; } = string.Empty;
        public string TargetForm { get; set; } = string.Empty;
        public string PartOfSpeech { get; set; } = string.Empty;
        public string ContextHint { get; set; } = string.Empty;
        public string ExampleText { get; set; } = string.Empty;
        public string SourceReference { get; set; } = string.Empty;
        public bool IsBuiltIn { get; set; }
        public bool Enabled { get; set; } = true;
    }

    internal static class ReferenceDictionaryPrompt
    {
        internal static string Build(IList<ReferenceDictionaryEntry> entries, IList<CandidateRecord> candidates)
        {
            var relevant = (entries ?? new List<ReferenceDictionaryEntry>())
                .Where(entry => entry.Enabled && !string.IsNullOrWhiteSpace(entry.SourceForm) &&
                    !string.IsNullOrWhiteSpace(entry.TargetForm) && candidates.Any(candidate =>
                        (string.IsNullOrEmpty(entry.ScopeModIdentity) || entry.ScopeModIdentity == candidate.ModIdentity) &&
                        Appears(candidate.SourceText, entry.SourceForm)))
                .OrderByDescending(entry => !string.IsNullOrEmpty(entry.ScopeModIdentity))
                .ThenBy(entry => entry.IsBuiltIn)
                .ThenByDescending(entry => entry.SourceForm.Length)
                .ThenBy(entry => entry.EntryId, StringComparer.Ordinal).ToList();
            if (relevant.Count == 0) return string.Empty;
            var body = new StringBuilder();
            body.AppendLine("Reference dictionary (contextual guidance, not mandatory substitutions):");
            body.AppendLine("Select the appropriate sense and part of speech using each item's source and context. " +
                "The same spelling may have several meanings; do not apply a mapping to an unrelated meaning. " +
                "Prefer user-maintained Mod entries over built-in suggestions when their meaning applies. " +
                "Adapt inflection and sentence structure. Never modify protected numbered markers. " +
                "Keep the normal output schema; do not report dictionary usage.");
            int length = body.Length;
            int count = 0;
            foreach (var senses in relevant.GroupBy(entry => entry.SourceForm.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                // Keep all matching senses of a spelling together; never select only half of a homonym group.
                var lines = senses.Select(entry => JsonConvert.SerializeObject(new
                {
                    source = entry.SourceForm, suggestedTranslation = entry.TargetForm,
                    partOfSpeech = entry.PartOfSpeech, context = entry.ContextHint, example = entry.ExampleText,
                    scope = string.IsNullOrEmpty(entry.ScopeModIdentity) ? "shared" : "current Mod",
                    origin = entry.IsBuiltIn ? "built-in" : "user"
                })).ToList();
                int groupLength = lines.Sum(line => line.Length + Environment.NewLine.Length);
                if (length + groupLength > 3200 || count + lines.Count > 24) continue;
                foreach (string line in lines) body.AppendLine(line);
                length += groupLength; count += lines.Count;
            }
            return count == 0 ? string.Empty : body.ToString();
        }

        private static bool Appears(string text, string term)
        {
            text = text ?? string.Empty;
            term = term.Trim();
            int start = 0;
            while (start < text.Length)
            {
                int found = text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) return false;
                int end = found + term.Length;
                if ((found == 0 || !IsWordCharacter(text[found-1])) &&
                    (end == text.Length || !IsWordCharacter(text[end]))) return true;
                start = found + 1;
            }
            return false;
        }

        private static bool IsWordCharacter(char value) => value == '_' || value >= 'A' && value <= 'Z' ||
            value >= 'a' && value <= 'z' || value >= '0' && value <= '9';
    }
}
