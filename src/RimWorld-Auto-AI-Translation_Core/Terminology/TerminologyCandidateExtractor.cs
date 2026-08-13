using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core.Terminology
{
    internal static class TerminologyCandidateExtractor
    {
        private sealed class Evidence
        {
            internal string DisplayForm;
            internal string NormalizedForm;
            internal int Frequency;
            internal int WeightedFrequency;
            internal bool IsTitleCase;
            internal readonly HashSet<string> Packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> DefTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Contexts = new List<string>();
        }

        private static readonly Regex NoiseRegex = new Regex(
            @"<[^>]+>|\{[^{}\r\n]+\}|\[[^\[\]\r\n]+\]|https?://\S+|\b\S+[\\/]\S+\b|\b\w+\.(?:dll|xml|png|jpg|dds|wav|ogg)\b|\b\d+(?:\.\d+)?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WordRegex = new Regex(
            @"[A-Za-z][A-Za-z'’\-]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "from",
            "has", "have", "he", "her", "his", "i", "in", "is", "it", "its", "not",
            "of", "on", "or", "she", "that", "the", "their", "this", "to", "was", "we",
            "were", "will", "with", "you", "your"
        };

        internal static List<TerminologyCandidate> Extract(
            IEnumerable<TerminologyCorpusEntry> corpus,
            string scopeKind,
            string scopeId,
            int maxCandidates = 200)
        {
            List<TerminologyCorpusEntry> entries = (corpus ?? Enumerable.Empty<TerminologyCorpusEntry>())
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Text))
                .ToList();
            var evidenceByKey = new Dictionary<string, Evidence>(StringComparer.OrdinalIgnoreCase);
            var globalCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (TerminologyCorpusEntry entry in entries)
            {
                List<Match> words = WordRegex.Matches(NoiseRegex.Replace(entry.Text, " ")).Cast<Match>().ToList();
                for (int size = 1; size <= 4; size++)
                {
                    for (int index = 0; index + size <= words.Count; index++)
                    {
                        List<Match> slice = words.Skip(index).Take(size).ToList();
                        if (!IsUseful(slice)) continue;
                        string form = string.Join(" ", slice.Select(match => match.Value));
                        string normalized = NormalizePhrase(form);
                        if (normalized.Length < 3) continue;
                        bool titleCase = slice.All(match => char.IsUpper(match.Value[0]));
                        string key = normalized;
                        globalCounts[key] = globalCounts.TryGetValue(key, out int count) ? count + 1 : 1;
                        if (!evidenceByKey.TryGetValue(key, out Evidence evidence))
                        {
                            evidence = new Evidence
                            {
                                DisplayForm = form,
                                NormalizedForm = normalized
                            };
                            evidenceByKey[key] = evidence;
                        }
                        evidence.Frequency++;
                        evidence.WeightedFrequency += GetContextWeight(entry);
                        evidence.IsTitleCase |= titleCase;
                        if (!string.IsNullOrWhiteSpace(entry.PackageId)) evidence.Packages.Add(entry.PackageId.Trim());
                        if (!string.IsNullOrWhiteSpace(entry.DefType)) evidence.DefTypes.Add(entry.DefType.Trim());
                        if (!string.IsNullOrWhiteSpace(entry.Field)) evidence.Fields.Add(entry.Field.Trim());
                        if (evidence.Contexts.Count < 3 && !evidence.Contexts.Contains(entry.Text))
                            evidence.Contexts.Add(entry.Text.Trim());
                    }
                }
            }

            return evidenceByKey.Values
                .Where(evidence => evidence.Frequency >= 2 || evidence.IsTitleCase)
                .Select(evidence => ToCandidate(evidence, scopeKind, scopeId, globalCounts[evidence.NormalizedForm]))
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.Frequency)
                .ThenBy(candidate => candidate.SourceForm, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxCandidates))
                .ToList();
        }

        private static TerminologyCandidate ToCandidate(
            Evidence evidence,
            string scopeKind,
            string scopeId,
            int globalFrequency)
        {
            float commonPenalty = globalFrequency > 12 ? Math.Min(4f, (globalFrequency - 12) * 0.15f) : 0f;
            float score = evidence.WeightedFrequency +
                          (evidence.IsTitleCase ? 3f : 0f) +
                          Math.Min(4, evidence.Packages.Count) - commonPenalty;
            return new TerminologyCandidate
            {
                TermId = CreateTermId(scopeKind, scopeId, evidence.NormalizedForm),
                SourceForm = evidence.DisplayForm,
                NormalizedForm = evidence.NormalizedForm,
                ScopeKind = NormalizeScope(scopeKind),
                ScopeId = (scopeId ?? string.Empty).Trim(),
                SourceScopeKind = NormalizeScope(scopeKind),
                SourceScopeId = (scopeId ?? string.Empty).Trim(),
                Frequency = evidence.Frequency,
                PackageCount = evidence.Packages.Count,
                GlobalFrequency = globalFrequency,
                Score = score,
                PackageIds = evidence.Packages.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                DefTypes = evidence.DefTypes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                Fields = evidence.Fields.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                Contexts = evidence.Contexts.ToList(),
                UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
        }

        private static bool IsUseful(List<Match> words)
        {
            if (words == null || words.Count == 0) return false;
            if (StopWords.Contains(words[0].Value) || StopWords.Contains(words[words.Count - 1].Value)) return false;
            if (words.Count == 1 && (words[0].Value.Length < 4 || StopWords.Contains(words[0].Value))) return false;
            return words.Any(match => match.Value.Any(char.IsLetter));
        }

        private static int GetContextWeight(TerminologyCorpusEntry entry)
        {
            string field = (entry.Field ?? string.Empty).Trim();
            if (field.Equals("label", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("name", StringComparison.OrdinalIgnoreCase)) return 3;
            if (!string.IsNullOrWhiteSpace(entry.DefType) || !string.IsNullOrWhiteSpace(entry.Key)) return 2;
            return 1;
        }

        private static string NormalizePhrase(string form)
        {
            string[] words = Regex.Split((form ?? string.Empty).Trim(), @"\s+");
            for (int index = 0; index < words.Length; index++)
                words[index] = TerminologyMorphology.NormalizeEnglishForm(words[index]);
            return string.Join(" ", words).Trim();
        }

        private static string NormalizeScope(string scopeKind)
        {
            string value = (scopeKind ?? string.Empty).Trim().ToLowerInvariant();
            return value == TerminologyScope.Global || value == TerminologyScope.ModGroup ||
                   value == TerminologyScope.Mod || value == TerminologyScope.Session
                ? value
                : TerminologyScope.Session;
        }

        private static string CreateTermId(string scopeKind, string scopeId, string normalized)
        {
            string material = NormalizeScope(scopeKind) + "|" + (scopeId ?? string.Empty).Trim().ToLowerInvariant() + "|" + normalized;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
                return "term:" + string.Concat(hash.Take(12).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }
    }
}
