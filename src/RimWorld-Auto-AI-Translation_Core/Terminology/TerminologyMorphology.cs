using System;

namespace AutoTranslator_Core.Terminology
{
    internal static class TerminologyMorphology
    {
        // Conservative inflection normalization only. Derivations such as
        // Empire/Imperial intentionally remain unrelated.
        internal static string NormalizeEnglishForm(string value)
        {
            string text = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('’', '\'');
            if (text.EndsWith("'s", StringComparison.Ordinal) && text.Length > 3)
                text = text.Substring(0, text.Length - 2);
            else if (text.EndsWith("s'", StringComparison.Ordinal) && text.Length > 3)
                text = text.Substring(0, text.Length - 1);

            if (text.IndexOf(' ') >= 0) return text;
            if (text.EndsWith("ies", StringComparison.Ordinal) && text.Length > 4)
                return text.Substring(0, text.Length - 3) + "y";
            if ((text.EndsWith("ches", StringComparison.Ordinal) ||
                 text.EndsWith("shes", StringComparison.Ordinal) ||
                 text.EndsWith("xes", StringComparison.Ordinal) ||
                 text.EndsWith("zes", StringComparison.Ordinal) ||
                 text.EndsWith("ses", StringComparison.Ordinal)) && text.Length > 4)
                return text.Substring(0, text.Length - 2);
            if (text.EndsWith("s", StringComparison.Ordinal) &&
                !text.EndsWith("ss", StringComparison.Ordinal) && text.Length > 3)
                return text.Substring(0, text.Length - 1);
            return text;
        }
    }
}
