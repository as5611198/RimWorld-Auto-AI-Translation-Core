using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core.Terminology
{
    internal sealed class TerminologyTrustedAnchor
    {
        internal string TermId { get; set; } = string.Empty;
        internal string Source { get; set; } = string.Empty;
        internal string Target { get; set; } = string.Empty;
    }

    internal sealed class TerminologyAlignedSentencePair
    {
        internal string PairId { get; set; } = string.Empty;
        internal string PackageId { get; set; } = string.Empty;
        internal string Source { get; set; } = string.Empty;
        internal string Target { get; set; } = string.Empty;
    }

    internal static class AlignedTranslationMiner
    {
        private sealed class MappingEvidence
        {
            internal string Source;
            internal string Target;
            internal readonly HashSet<string> PairIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> PackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Contexts = new List<string>();
        }

        private static readonly Regex ProtectedTokenRegex = new Regex(
            @"(\{[^{}\r\n]+\}|\[[^\[\]\r\n]+\]|\$[A-Za-z0-9_]+|%[A-Za-z])",
            RegexOptions.Compiled);

        internal static List<TerminologyCandidate> Mine(
            IEnumerable<TerminologyAlignedSentencePair> pairs,
            IEnumerable<TerminologyTrustedAnchor> trustedAnchors,
            string scopeKind,
            string scopeId)
        {
            List<TerminologyTrustedAnchor> anchors = (trustedAnchors ?? Enumerable.Empty<TerminologyTrustedAnchor>())
                .Where(anchor => anchor != null && !string.IsNullOrWhiteSpace(anchor.Source) && !string.IsNullOrWhiteSpace(anchor.Target))
                .ToList();
            var evidence = new Dictionary<string, MappingEvidence>(StringComparer.OrdinalIgnoreCase);

            foreach (TerminologyAlignedSentencePair pair in pairs ?? Enumerable.Empty<TerminologyAlignedSentencePair>())
            {
                if (pair == null || !ProtectedTokensMatch(pair.Source, pair.Target)) continue;
                List<TerminologyTrustedAnchor> matching = anchors
                    .Where(anchor => CountOccurrences(pair.Source, anchor.Source) == 1 &&
                                     CountOccurrences(pair.Target, anchor.Target) == 1)
                    .ToList();
                foreach (TerminologyTrustedAnchor anchor in matching)
                {
                    if (!TryRemoveUniqueAnchor(pair.Source, anchor.Source, out string sourceRemainder) ||
                        !TryRemoveUniqueAnchor(pair.Target, anchor.Target, out string targetRemainder))
                        continue;
                    if (!IsValidRemainder(sourceRemainder) || !IsValidRemainder(targetRemainder)) continue;
                    string normalizedSource = TerminologyMorphology.NormalizeEnglishForm(sourceRemainder);
                    string key = normalizedSource + "\n" + targetRemainder.ToLowerInvariant();
                    if (!evidence.TryGetValue(key, out MappingEvidence item))
                    {
                        item = new MappingEvidence { Source = sourceRemainder, Target = targetRemainder };
                        evidence[key] = item;
                    }
                    item.PairIds.Add(string.IsNullOrWhiteSpace(pair.PairId) ? pair.Source + "\n" + pair.Target : pair.PairId);
                    if (!string.IsNullOrWhiteSpace(pair.PackageId)) item.PackageIds.Add(pair.PackageId);
                    if (item.Contexts.Count < 3) item.Contexts.Add(pair.Source + " => " + pair.Target);
                }
            }

            var targetCountBySource = evidence.Values
                .GroupBy(item => TerminologyMorphology.NormalizeEnglishForm(item.Source), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count(), StringComparer.OrdinalIgnoreCase);

            return evidence.Values.Select(item =>
            {
                string normalized = TerminologyMorphology.NormalizeEnglishForm(item.Source);
                bool supported = item.PairIds.Count >= 2 && targetCountBySource[normalized] == 1;
                return new TerminologyCandidate
                {
                    TermId = CreateTermId(scopeKind, scopeId, normalized, item.Target),
                    SourceForm = item.Source,
                    NormalizedForm = normalized,
                    Target = item.Target,
                    ScopeKind = scopeKind,
                    ScopeId = scopeId,
                    SourceScopeKind = scopeKind,
                    SourceScopeId = scopeId,
                    Status = supported ? TerminologyStatus.SessionActive : TerminologyStatus.Candidate,
                    EvidenceKind = "aligned_difference",
                    Frequency = item.PairIds.Count,
                    PackageCount = item.PackageIds.Count,
                    Score = supported ? 20f + item.PairIds.Count : item.PairIds.Count,
                    PackageIds = item.PackageIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                    Contexts = item.Contexts,
                    UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
            }).OrderByDescending(item => item.Score).ToList();
        }

        private static bool TryRemoveUniqueAnchor(string text, string anchor, out string remainder)
        {
            remainder = string.Empty;
            if (CountOccurrences(text, anchor) != 1) return false;
            int index = text.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
            string before = NormalizeRemainder(text.Substring(0, index));
            string after = NormalizeRemainder(text.Substring(index + anchor.Length));
            if (before.Length > 0 && after.Length > 0) return false;
            remainder = before.Length > 0 ? before : after;
            return remainder.Length > 0;
        }

        private static string NormalizeRemainder(string text)
        {
            return Regex.Replace((text ?? string.Empty).Trim(' ', '\t', '\r', '\n', '-', ':', ';', ',', '.', '(', ')'), @"\s+", " ");
        }

        private static bool IsValidRemainder(string text)
        {
            return !string.IsNullOrWhiteSpace(text) && text.Length <= 80 && text.Any(char.IsLetter);
        }

        private static int CountOccurrences(string text, string value)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value)) return 0;
            int count = 0;
            int offset = 0;
            while ((offset = text.IndexOf(value, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                offset += value.Length;
            }
            return count;
        }

        private static bool ProtectedTokensMatch(string source, string target)
        {
            string[] left = ProtectedTokenRegex.Matches(source ?? string.Empty).Cast<Match>().Select(match => match.Value).OrderBy(value => value).ToArray();
            string[] right = ProtectedTokenRegex.Matches(target ?? string.Empty).Cast<Match>().Select(match => match.Value).OrderBy(value => value).ToArray();
            return left.SequenceEqual(right, StringComparer.Ordinal);
        }

        private static string CreateTermId(string scopeKind, string scopeId, string source, string target)
        {
            string material = (scopeKind ?? string.Empty) + "|" + (scopeId ?? string.Empty) + "|" + source + "|" + target;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
                return "term:" + string.Concat(hash.Take(12).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }
    }
}
