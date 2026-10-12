using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AutoTranslator_Core.Workflow.AI
{
    internal static class TranslationReusePolicy
    {
        private enum CountMode { Unknown, Chinese, Japanese, Thai, Words }

        // A source-language hint takes precedence; never infer from the target language.
        internal static bool CanReuse(CandidateRecord candidate)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.SourceText)) return false;
            string language = null;
            try
            {
                var context = JObject.Parse(candidate.ContextJson ?? "{}");
                language = (context["sourceLanguage"] ?? context["source_language"])?.ToString();
            }
            catch { /* Missing or malformed context still permits script-based recognition. */ }
            return CanReuse(candidate.SourceText, language);
        }

        internal static bool CanReuse(string source, string sourceLanguage)
        {
            if (string.IsNullOrEmpty(source)) return false;
            CountMode mode = string.IsNullOrWhiteSpace(sourceLanguage)
                ? RecognizeScript(source) : FromLanguage(sourceLanguage);
            if (mode == CountMode.Unknown) return false;
            if (mode == CountMode.Words)
            {
                int words = 0;
                foreach (string word in Regex.Split(source, @"\s+"))
                    if (word.Length > 0) words++;
                return words > 3;
            }
            // Preserve punctuation and whitespace, and count complete Unicode text elements.
            int threshold = mode == CountMode.Japanese ? 6 : mode == CountMode.Thai ? 8 : 3;
            return new StringInfo(source).LengthInTextElements > threshold;
        }

        private static CountMode FromLanguage(string sourceLanguage)
        {
            string language = sourceLanguage.Trim().Replace('_', '-').ToLowerInvariant();
            switch (language.Split('-')[0])
            {
                case "zh": case "chinesesimplified": case "chinesetraditional":
                case "simplified": case "traditional": return CountMode.Chinese;
                case "ja": case "japanese": return CountMode.Japanese;
                case "th": case "thai": return CountMode.Thai;
                case "en": case "english": case "fr": case "french":
                case "de": case "german": case "es": case "spanish":
                case "pt": case "portuguese": case "it": case "italian":
                case "pl": case "polish": case "tr": case "turkish":
                case "ru": case "russian": case "uk": case "ukrainian":
                case "ko": case "korean": case "ar": case "arabic":
                case "he": case "hebrew": return CountMode.Words;
                default: return CountMode.Unknown;
            }
        }

        private static CountMode RecognizeScript(string source)
        {
            bool han = false, kana = false, thai = false, words = false;
            for (int i = 0; i < source.Length; i++)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(source, i);
                int code = source[i];
                if (char.IsHighSurrogate(source[i]) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                {
                    code = char.ConvertToUtf32(source, i);
                    i++;
                }
                if (InRange(code, 0x3400, 0x4DBF) || InRange(code, 0x4E00, 0x9FFF) ||
                    InRange(code, 0xF900, 0xFAFF) || InRange(code, 0x20000, 0x323AF)) han = true;
                else if (InRange(code, 0x3040, 0x30FF) || InRange(code, 0x31F0, 0x31FF) ||
                    InRange(code, 0xFF66, 0xFF9D)) kana = true;
                else if (InRange(code, 0x0E01, 0x0E5B)) thai = true;
                else if (category == UnicodeCategory.UppercaseLetter || category == UnicodeCategory.LowercaseLetter ||
                    category == UnicodeCategory.TitlecaseLetter || category == UnicodeCategory.ModifierLetter ||
                    category == UnicodeCategory.OtherLetter)
                {
                    if (InRange(code, 0x0041, 0x024F) || InRange(code, 0x1E00, 0x1EFF) ||
                        InRange(code, 0x0400, 0x052F) || InRange(code, 0x2DE0, 0x2DFF) ||
                        InRange(code, 0xA640, 0xA69F) || InRange(code, 0x0590, 0x06FF) ||
                        InRange(code, 0x0750, 0x077F) || InRange(code, 0x08A0, 0x08FF) ||
                        InRange(code, 0xFB1D, 0xFDFF) || InRange(code, 0xFE70, 0xFEFF) ||
                        InRange(code, 0x1100, 0x11FF) || InRange(code, 0x3130, 0x318F) ||
                        InRange(code, 0xA960, 0xA97F) || InRange(code, 0xAC00, 0xD7FF)) words = true;
                    else return CountMode.Unknown;
                }
            }
            if ((words && (han || kana || thai)) || (thai && (han || kana))) return CountMode.Unknown;
            if (kana) return CountMode.Japanese;
            if (han) return CountMode.Chinese;
            if (thai) return CountMode.Thai;
            return words ? CountMode.Words : CountMode.Unknown;
        }

        private static bool InRange(int code, int first, int last) => code >= first && code <= last;
    }
}
