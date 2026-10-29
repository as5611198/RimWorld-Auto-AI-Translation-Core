using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core
{
    internal static class UIDynamicNumberTemplate
    {
        private const string UnitPattern =
            @"(?:TiB|GiB|MiB|KiB|TB|GB|MB|kB|GW|MW|kW|XP|kg|mg|km|cm|mm|mL|\u00B5s|us|ms|W|L|g|m|s|h|d|%)";

        internal static readonly Regex NumberRegex = new Regex(
            @"(?<![A-Za-z0-9_\.,])[-+]?\d+(?:[\.,]\d+)*(?:\s*(?:(?:°\s*)?[CF]|" + UnitPattern + @"))?(?![A-Za-z0-9_]|[\.,]\d)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static readonly Regex PlaceholderRegex = new Regex(
            @"\{num\d+\}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LegacyPartialPlaceholderRegex = new Regex(
            @"(?<placeholder>\{num\d+\})(?:[\.,]\d+)+\s*(?:(?:°\s*)?[CF]|" + UnitPattern + @")(?![A-Za-z0-9_])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static bool IsNumericDisplayText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            // Formatting does not turn a stat value into translatable text.
            for (int depth = 0; depth < 4 && value.StartsWith("<", StringComparison.Ordinal); depth++)
            {
                int end = value.IndexOf('>');
                if (end < 0) break;
                string opening = value.Substring(0, end + 1), closing;
                if (opening.Equals("<b>", StringComparison.OrdinalIgnoreCase)) closing = "</b>";
                else if (opening.Equals("<i>", StringComparison.OrdinalIgnoreCase)) closing = "</i>";
                else if (opening.StartsWith("<color=", StringComparison.OrdinalIgnoreCase)) closing = "</color>";
                else if (opening.StartsWith("<size=", StringComparison.OrdinalIgnoreCase)) closing = "</size>";
                else break;
                if (value.Length < opening.Length + closing.Length || !value.EndsWith(closing, StringComparison.OrdinalIgnoreCase)) break;
                value = value.Substring(opening.Length, value.Length - opening.Length - closing.Length).Trim();
            }
            bool hasDigit = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsDigit(c)) { hasDigit = true; continue; }
                if (char.IsWhiteSpace(c) || "+-−.,:/\\%()[]×÷*=xX~～＋－．，％".IndexOf(c) >= 0) continue;
                // A bare number plus a recognized unit is still a readout.
                // Labels such as "Model 3D" or "3rd day" remain ordinary text.
                if (!hasDigit) return false;
                Match match = NumberRegex.Match(value);
                return match.Success && match.Index == 0 && match.Length == value.Length;
            }
            return hasDigit;
        }

        internal static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            if (!ContainsDigit(text)) return text;

            // Earlier versions could normalize 123.87MB as {num1}.87MB.
            // Collapse that persisted shape before assigning any new slots.
            string canonical = CollapseLegacyPartialPlaceholder(text);

            int nextIndex = GetHighestPlaceholderIndex(canonical);
            int replacements = 0;
            string normalized = NumberRegex.Replace(canonical, match =>
            {
                replacements++;
                nextIndex++;
                return "{num" + nextIndex.ToString() + "}";
            });

            return replacements > 0 ? normalized : canonical;
        }

        internal static bool HasMixedPersistedTemplate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string canonical = CollapseLegacyPartialPlaceholder(text);
            return PlaceholderRegex.IsMatch(canonical) && NumberRegex.IsMatch(canonical);
        }

        internal static string Restore(string original, string translated)
        {
            if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(translated)) return translated;
            if (translated.IndexOf("{num", StringComparison.OrdinalIgnoreCase) < 0) return translated;

            MatchCollection matches = NumberRegex.Matches(original);
            if (matches.Count == 0) return translated;

            var numbers = new List<string>(matches.Count);
            foreach (Match match in matches) numbers.Add(match.Value);

            return PlaceholderRegex.Replace(translated, match =>
            {
                string rawIndex = match.Value.Substring(4, match.Value.Length - 5);
                if (!int.TryParse(rawIndex, out int numberIndex)) return match.Value;

                int arrayIndex = numberIndex - 1;
                return arrayIndex >= 0 && arrayIndex < numbers.Count ? numbers[arrayIndex] : match.Value;
            });
        }

        private static int GetHighestPlaceholderIndex(string text)
        {
            int highest = 0;
            foreach (Match match in PlaceholderRegex.Matches(text ?? string.Empty))
            {
                string rawIndex = match.Value.Substring(4, match.Value.Length - 5);
                if (int.TryParse(rawIndex, out int index) && index > highest) highest = index;
            }
            return highest;
        }

        private static string CollapseLegacyPartialPlaceholder(string text)
        {
            return LegacyPartialPlaceholderRegex.Replace(
                text,
                match => match.Groups["placeholder"].Value);
        }

        private static bool ContainsDigit(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsDigit(text[i])) return true;
            }
            return false;
        }
    }
}
