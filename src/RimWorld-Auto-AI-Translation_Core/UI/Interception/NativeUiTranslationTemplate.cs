using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core
{
    internal sealed class NativeUiTranslationTemplate
    {
        private static readonly Regex Tokens = new Regex(@"(?<!\{)\{(?<id>\d+|[A-Za-z_][A-Za-z_0-9]*)(?:,[+-]?\d+)?(?::[^{}]+)?\}(?!\})");
        private Regex pattern;
        private string[] literals, groups;
        internal string Bucket;

        internal static bool CompatiblePlaceholders(string source, string target)
        {
            var left = new HashSet<string>(StringComparer.Ordinal);
            var right = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match token in Tokens.Matches(source ?? "")) left.Add(token.Groups["id"].Value);
            foreach (Match token in Tokens.Matches(target ?? "")) right.Add(token.Groups["id"].Value);
            return left.SetEquals(right);
        }

        internal static NativeUiTranslationTemplate Create(string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || source.Length > 2048 || target.Length > 4096) return null;
            var matches = Tokens.Matches(source);
            if (matches.Count == 0 || matches.Count > 16) return null;
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            var regex = new StringBuilder("\\A");
            int start = 0, literalCount = 0;
            string first = null, last = null;
            foreach (Match token in matches)
            {
                string literal = Unescape(source.Substring(start, token.Index - start));
                if (first == null) first = literal;
                literalCount += literal.Length; regex.Append(Regex.Escape(literal));
                string id = token.Groups["id"].Value;
                if (ids.TryGetValue(id, out string group)) regex.Append("\\k<" + group + ">");
                else { group = "g" + ids.Count; ids[id] = group; regex.Append("(?<" + group + ">[\\s\\S]*?)"); }
                start = token.Index + token.Length;
            }
            last = Unescape(source.Substring(start)); literalCount += last.Length;
            regex.Append(Regex.Escape(last)).Append("\\z");
            if (literalCount < 8) return null;
            string bucket = BucketFor(first, false) ?? BucketFor(last, true);
            if (bucket == null) return null;
            var pieces = new List<string>(); var targetGroups = new List<string>(); var used = new HashSet<string>();
            start = 0;
            foreach (Match token in Tokens.Matches(target))
            {
                string id = token.Groups["id"].Value;
                if (!ids.TryGetValue(id, out string group)) return null;
                pieces.Add(Unescape(target.Substring(start, token.Index - start))); targetGroups.Add(group); used.Add(id);
                start = token.Index + token.Length;
            }
            if (used.Count != ids.Count) return null;
            pieces.Add(Unescape(target.Substring(start)));
            return new NativeUiTranslationTemplate { Bucket = bucket, literals = pieces.ToArray(), groups = targetGroups.ToArray(),
                pattern = new Regex(regex.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(1)) };
        }

        private static string Unescape(string text) => text.Replace("{{", "{").Replace("}}", "}");
        private static string BucketFor(string literal, bool suffix)
        {
            int length = literal.Length >= 12 ? 12 : literal.Length >= 8 ? 8 : literal.Length >= 4 ? 4 : 0;
            return length == 0 ? null : (suffix ? "S" : "P") + length + ":" +
                (suffix ? literal.Substring(literal.Length - length) : literal.Substring(0, length));
        }

        internal bool TryMatch(string original, out string translated)
        {
            translated = null;
            Match match = pattern.Match(original);
            if (!match.Success) return false;
            var text = new StringBuilder();
            for (int i = 0; i < groups.Length; i++) text.Append(literals[i]).Append(match.Groups[groups[i]].Value);
            translated = text.Append(literals[literals.Length - 1]).ToString();
            return true;
        }
    }
}
