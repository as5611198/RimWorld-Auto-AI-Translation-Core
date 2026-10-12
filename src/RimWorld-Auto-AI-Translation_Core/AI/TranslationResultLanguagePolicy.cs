using AutoTranslator_Core.TranslationPolicy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core
{
    // Final language-quality checks shared by batch retries and every write path.
    internal static class TranslationResultLanguagePolicy
    {
        private static readonly Regex ProtectedTokenRegex = new Regex(
            @"(\{[^{}\r\n]+\}|\[(?!title:)[^\[\]\r\n]+\])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EnglishSignalRegex = new Regex(
            @"(?<!\p{L})(the|and|for|with|from|this|that|your|you|not|can|will|when|while|after|before|into|has|have|are|was|were|is|of|to|in|on|a|an)(?!\p{L})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ModelDesignationRegex = new Regex(
            @"^(?:[IVXLCDM]{1,8}|[A-Z-]*\d[A-Z0-9-]*)$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly HashSet<string> KnownTranslatableEnglishTokens =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "none", "yes", "no", "on", "off", "enabled", "disabled", "enable", "disable",
                "default", "random", "unknown", "empty", "cancel", "confirm", "apply", "reset",
                "close", "open", "back", "next", "previous", "save", "load", "delete", "remove",
                "add", "edit", "error", "warning", "column"
            };

        internal static bool ShouldAccept(string translated, string original, TargetLanguage targetLang)
        {
            if (string.IsNullOrWhiteSpace(translated)) return false;
            if (LanguageDetector.HasWrongChineseVariant(translated, targetLang)) return false;
            if (HasUnexpectedScriptResidual(translated, original, targetLang)) return false;
            return !HasLikelyEnglishResidual(translated, original, targetLang);
        }

        internal static bool TryNormalizePersistedGeneratedValue(
            string translated,
            TargetLanguage targetLang,
            out string normalized)
        {
            normalized = translated;

            // Without the source text, Latin target languages cannot be distinguished safely from English.
            // The fake-language detector currently applies only to the two Chinese targets.
            if (targetLang != TargetLanguage.Traditional && targetLang != TargetLanguage.Simplified)
                return true;

            normalized = LanguageDetector.NormalizeChineseVariant(translated, targetLang);
            return ShouldAccept(normalized, normalized, targetLang);
        }

        internal static bool HasUnexpectedScriptResidual(
            string translated,
            string original,
            TargetLanguage targetLang)
        {
            if (targetLang != TargetLanguage.Traditional && targetLang != TargetLanguage.Simplified)
                return false;

            string sample = NormalizeResidualLanguageSample(translated);
            string sourceSample = NormalizeResidualLanguageSample(original);
            CountResidualScripts(
                sample,
                out int hanCount,
                out int kanaCount,
                out int hangulCount,
                out int cyrillicCount,
                out _,
                out int letterCount);

            int foreignScriptCount = kanaCount + hangulCount + cyrillicCount;
            if (foreignScriptCount == 0) return false;

            for (int index = 0; index < sample.Length; index++)
            {
                char c = sample[index];
                if (!(IsKana(c) || IsHangul(c) || IsCyrillic(c)) || sourceSample.IndexOf(c) >= 0)
                    continue;

                bool adjacentLetter = (index > 0 && char.IsLetter(sample[index - 1])) ||
                                       (index + 1 < sample.Length && char.IsLetter(sample[index + 1]));
                if (adjacentLetter || foreignScriptCount >= 2)
                    return true;
            }

            bool unchanged = string.Equals(sample, sourceSample, StringComparison.Ordinal);
            bool shortProperName = unchanged &&
                letterCount <= 8 &&
                !Regex.IsMatch(sample, @"\s");
            if (shortProperName) return false;

            return foreignScriptCount >= 4 && hanCount == 0;
        }

        internal static bool HasLikelyEnglishResidual(
            string translated,
            string original,
            TargetLanguage targetLang)
        {
            if (targetLang == TargetLanguage.English) return false;

            string sample = NormalizeResidualLanguageSample(translated);
            string sourceSample = NormalizeResidualLanguageSample(original);
            if (sample.Length < 2) return false;

            CountResidualScripts(
                sample,
                out int hanCount,
                out int kanaCount,
                out int hangulCount,
                out int cyrillicCount,
                out int latinCount,
                out int letterCount);
            if (letterCount < 3 || latinCount < 3) return false;
            if (IsShortUppercaseToken(sample)) return false;

            bool unchanged = string.Equals(sample, sourceSample, StringComparison.OrdinalIgnoreCase);
            bool targetPresent = LanguageDetector.LooksLikeTargetLanguage(sample, targetLang);
            int latinPercent = Percent(latinCount, letterCount);
            bool sentenceLike = IsLikelyEnglishSentence(sample);

            if (!IsLatinTargetLanguage(targetLang) &&
                !targetPresent &&
                latinPercent >= 80 &&
                sentenceLike)
            {
                return true;
            }

            if (sourceSample.Length < 2 || !HasTranslatableLatinSource(sourceSample)) return false;

            if (IsLatinTargetLanguage(targetLang))
            {
                return unchanged && sentenceLike;
            }

            if (targetPresent && latinPercent < 45)
            {
                return false;
            }

            if (!targetPresent && latinPercent >= 80)
            {
                return sentenceLike;
            }

            return latinPercent >= 65 && sentenceLike;
        }

        private static bool IsLikelyEnglishSentence(string sample)
        {
            if (string.IsNullOrWhiteSpace(sample)) return false;
            string trimmedToken = sample.Trim().Trim('.', ',', ':', ';', '!', '?');
            if (KnownTranslatableEnglishTokens.Contains(trimmedToken)) return true;
            if (EnglishSignalRegex.IsMatch(sample)) return true;

            string[] words = Regex.Split(sample.Trim(), @"\s+")
                .Where(word => word.Length > 0)
                .ToArray();
            if (words.Length >= 3) return true;
            if (words.Length == 1)
            {
                return Regex.IsMatch(
                    trimmedToken,
                    @"^[a-z][a-z'\-]{2,}$",
                    RegexOptions.CultureInvariant);
            }
            if (words.Length != 2) return false;

            string designator = words[1].Trim('.', ',', ':', ';', '!', '?', '(', ')', '[', ']');
            return !ModelDesignationRegex.IsMatch(designator);
        }

        private static string NormalizeResidualLanguageSample(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            if (TrySplitGrammarRule(text, out string ruleName, out string rightSide))
            {
                if (!TranslationPolicyClassifier.ShouldTranslateGrammarRuleRightSide(ruleName, rightSide))
                    return string.Empty;
                text = rightSide;
            }

            string sample = text
                .Replace("\\n", " ")
                .Replace("\\r", " ")
                .Replace("\\t", " ")
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Replace("\t", " ");
            sample = Regex.Replace(sample, @"<[^>]+>", " ");
            sample = ProtectedTokenRegex.Replace(sample, " ");
            sample = Regex.Replace(sample, @"\$[A-Za-z0-9_]+|%[A-Za-z]", " ");
            sample = Regex.Replace(
                sample,
                @"https?://\S+|[A-Za-z]:[\\/]\S+|[A-Za-z0-9_\-./\\]+\.(?:png|jpg|jpeg|dds|tex|wav|mp3|ogg|xml|txt|dll)\b",
                " ");
            sample = Regex.Replace(sample, @"[_/\\]+", " ");
            sample = Regex.Replace(sample, @"\s+", " ");
            return sample.Trim();
        }

        private static bool TrySplitGrammarRule(string text, out string ruleName, out string rightSide)
        {
            ruleName = string.Empty;
            rightSide = string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return false;

            int arrow = text.IndexOf("->", StringComparison.Ordinal);
            if (arrow <= 0) return false;
            string leftSide = text.Substring(0, arrow).Trim();
            int metadataStart = leftSide.IndexOf('(');
            ruleName = metadataStart >= 0
                ? leftSide.Substring(0, metadataStart).Trim()
                : leftSide;
            rightSide = text.Substring(arrow + 2);
            return ruleName.Length > 0;
        }

        private static bool HasTranslatableLatinSource(string sample)
        {
            CountResidualScripts(sample, out _, out _, out _, out _, out int latinCount, out int letterCount);
            if (letterCount < 3 || latinCount < 3) return HasEnglishSignal(sample);
            if (Regex.IsMatch(sample, @"^[A-Z0-9 .'\-]{2,6}$") && sample.ToUpperInvariant() == sample)
                return false;
            return true;
        }

        private static bool HasEnglishSignal(string sample)
        {
            if (string.IsNullOrWhiteSpace(sample)) return false;
            return EnglishSignalRegex.IsMatch(sample) ||
                   Regex.IsMatch(sample, @"^[A-Za-z][A-Za-z '\-]{2,}$");
        }

        private static bool IsLatinTargetLanguage(TargetLanguage targetLang)
        {
            return targetLang == TargetLanguage.French ||
                   targetLang == TargetLanguage.German ||
                   targetLang == TargetLanguage.Spanish ||
                   targetLang == TargetLanguage.Italian ||
                   targetLang == TargetLanguage.Polish ||
                   targetLang == TargetLanguage.Portuguese ||
                   targetLang == TargetLanguage.Turkish;
        }

        private static bool IsShortUppercaseToken(string sample)
        {
            return Regex.IsMatch(sample, @"^[A-Z0-9]{2,6}$") && sample.ToUpperInvariant() == sample;
        }

        private static void CountResidualScripts(
            string text,
            out int hanCount,
            out int kanaCount,
            out int hangulCount,
            out int cyrillicCount,
            out int latinCount,
            out int letterCount)
        {
            hanCount = 0;
            kanaCount = 0;
            hangulCount = 0;
            cyrillicCount = 0;
            latinCount = 0;
            letterCount = 0;

            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text)
            {
                if (!char.IsLetter(c)) continue;
                letterCount++;
                if (IsHan(c)) hanCount++;
                else if (IsKana(c)) kanaCount++;
                else if (IsHangul(c)) hangulCount++;
                else if (IsCyrillic(c)) cyrillicCount++;
                else if (IsLatin(c)) latinCount++;
            }
        }

        private static int Percent(int part, int total)
        {
            return total <= 0 ? 0 : (int)((part * 100.0) / total);
        }

        private static bool IsHan(char c)
        {
            return (c >= '\u3400' && c <= '\u4DBF') ||
                   (c >= '\u4E00' && c <= '\u9FFF') ||
                   (c >= '\uF900' && c <= '\uFAFF');
        }

        private static bool IsKana(char c)
        {
            return (c >= '\u3040' && c <= '\u30FF') ||
                   (c >= '\u31F0' && c <= '\u31FF') ||
                   (c >= '\uFF66' && c <= '\uFF9F');
        }

        private static bool IsHangul(char c)
        {
            return (c >= '\u1100' && c <= '\u11FF') ||
                   (c >= '\u3130' && c <= '\u318F') ||
                   (c >= '\uAC00' && c <= '\uD7AF');
        }

        private static bool IsCyrillic(char c)
        {
            return (c >= '\u0400' && c <= '\u04FF') ||
                   (c >= '\u0500' && c <= '\u052F');
        }

        private static bool IsLatin(char c)
        {
            return (c >= 'A' && c <= 'Z') ||
                   (c >= 'a' && c <= 'z') ||
                   (c >= '\u00C0' && c <= '\u024F');
        }
    }
}
