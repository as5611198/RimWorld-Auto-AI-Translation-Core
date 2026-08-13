using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責翻譯結果清理與安全檢查。
// EN: This file sanitizes translated text and validates unsafe output.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器掃描器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorScanner.
    public static partial class AutoTranslatorScanner
    {


        // 這個方法負責清理並標準化 翻譯Result 內容。
        // EN: This method cleans and normalizes translation result.
        private static string SanitizeTranslationResult(string translated, string original)
        {
            if (string.IsNullOrEmpty(translated)) return translated;


            bool originalHasNewline = original.Contains("\\n") || original.Contains("\n");
            bool translatedHasNewline = translated.Contains("\\n") || translated.Contains("\n");

            if (!originalHasNewline && translatedHasNewline)
            {

                translated = translated.Replace("\\n", " ");
                translated = translated.Replace("\n", " ");
                translated = translated.Replace("\r", " ");
                AddValidationStat(s => s.NewlineFixed++);
            }


            if (original.Contains("\\n") && !translated.Contains("\\n"))
            {

                translated = translated.Replace("\r\n", "\\n");
                translated = translated.Replace("\n", "\\n");
                translated = translated.Replace("\r", "\\n");
                AddValidationStat(s => s.NewlineFixed++);
            }


            translated = translated.Trim();


            translated = System.Text.RegularExpressions.Regex.Replace(translated, @"^(?:\\n|\\r|\s)+", "");
            translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?:\\n|\\r|\s)+$", "");


            translated = translated.Replace("\\\\n", "\\n");


            translated = System.Text.RegularExpressions.Regex.Replace(
                translated,
                @" {2,}",
                " "
            );

            translated = RestoreGrammarRulePrefix(translated, original);
            translated = RestoreProtectedTokens(translated, original);
            translated = RestoreUntranslatableGrammarRule(translated, original);
            translated = LanguageDetector.NormalizeChineseVariant(translated, AutoTranslatorMod.Settings.TargetLang);

            if (LanguageDetector.LooksLikePlaceholderTranslation(translated, AutoTranslatorMod.Settings.TargetLang))
            {
                return null;
            }

            return translated;
        }


        // 這個方法負責處理 RestoreGrammar規則Prefix 相關流程。
        // EN: This method handles restore grammar rule prefix.
        private static string RestoreGrammarRulePrefix(string translated, string original)
        {
            if (string.IsNullOrEmpty(translated) || string.IsNullOrEmpty(original)) return translated;

            if (!TrySplitGrammarRule(original, out string originalPrefix, out _, out _)) return translated;
            int translatedArrow = translated.IndexOf("->", StringComparison.Ordinal);

            if (translatedArrow >= 0)
            {
                string translatedRight = translated.Substring(translatedArrow + 2).TrimStart();
                if (!translated.StartsWith(originalPrefix, StringComparison.Ordinal))
                {
                    AddValidationStat(s => s.RulePrefixFixed++);
                }
                return originalPrefix + translatedRight;
            }

            AddValidationStat(s => s.RulePrefixFixed++);
            return originalPrefix + translated.TrimStart();
        }


        private static string RestoreUntranslatableGrammarRule(string translated, string original)
        {
            if (string.IsNullOrEmpty(translated) || string.IsNullOrEmpty(original)) return translated;
            return IsUntranslatableGrammarRule(original) ? original : translated;
        }


        private static bool TrySplitGrammarRule(string text, out string prefix, out string ruleName, out string rightSide)
        {
            prefix = "";
            ruleName = "";
            rightSide = "";

            if (string.IsNullOrEmpty(text)) return false;

            int arrow = text.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) return false;

            prefix = text.Substring(0, arrow + 2);
            rightSide = text.Substring(arrow + 2);

            string leftSide = text.Substring(0, arrow).Trim();
            int metadataStart = leftSide.IndexOf('(');
            ruleName = metadataStart >= 0 ? leftSide.Substring(0, metadataStart).Trim() : leftSide;
            return !string.IsNullOrEmpty(ruleName);
        }


        private static bool IsUntranslatableGrammarRule(string text)
        {
            if (!TrySplitGrammarRule(text, out _, out string ruleName, out string rightSide)) return false;
            return !ShouldTranslateGrammarRuleRightSide(ruleName, rightSide);
        }


        private static bool ShouldTranslateGrammarRuleRightSide(string ruleName, string rightSide)
        {
            return TranslationPolicy.TranslationPolicyClassifier.ShouldTranslateGrammarRuleRightSide(
                ruleName,
                rightSide);
        }


        // 這個方法負責處理 RestoreProtectedTokens 相關流程。
        // EN: This method handles restore protected tokens.
        private static string RestoreProtectedTokens(string translated, string original)
        {
            if (string.IsNullOrEmpty(translated) || string.IsNullOrEmpty(original)) return translated;

            bool missingToken = false;
            string result = translated;
            var tokens = ProtectedTokenRegex.Matches(original)
                .Cast<Match>()
                .Select(m => m.Value)
                .Distinct()
                .ToList();

            foreach (string token in tokens)
            {
                if (result.Contains(token)) continue;

                string inner = token.Substring(1, token.Length - 2);
                string[] alternatives = token[0] == '{'
                    ? new[] { "[" + inner + "]", "【" + inner + "】", "［" + inner + "］", "(" + inner + ")", "（" + inner + "）" }
                    : new[] { "{" + inner + "}", "【" + inner + "】", "［" + inner + "］", "(" + inner + ")", "（" + inner + "）" };

                foreach (string alt in alternatives)
                {
                    if (result.Contains(alt))
                    {
                        result = result.Replace(alt, token);
                        AddValidationStat(s => s.TokenFixed++);
                    }
                }

                if (!result.Contains(token))
                {
                    missingToken = true;
                }
            }

            if (missingToken && RequiresProtectedTokenParity(original))
            {
                AddValidationStat(s => s.StructureFallback++);
                return original;
            }

            return result;
        }

        private static bool HasProtectedTokenMismatch(string translated, string original)
        {
            if (string.IsNullOrEmpty(original)) return false;
            if (string.IsNullOrEmpty(translated)) return ProtectedTokenRegex.IsMatch(original);

            var tokens = ProtectedTokenRegex.Matches(original)
                .Cast<Match>()
                .GroupBy(m => m.Value)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            foreach (var pair in tokens)
            {
                int translatedCount = Regex.Matches(translated, Regex.Escape(pair.Key)).Count;
                if (translatedCount != pair.Value) return true;
            }

            return false;
        }

        private static bool HasFormatArgumentMismatch(string translated, string original)
        {
            if (string.IsNullOrEmpty(original)) return false;
            if (string.IsNullOrEmpty(translated)) return FormatArgumentRegex.IsMatch(original);

            var originalArgs = FormatArgumentRegex.Matches(original)
                .Cast<Match>()
                .GroupBy(m => m.Groups[1].Value)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var translatedArgs = FormatArgumentRegex.Matches(translated)
                .Cast<Match>()
                .GroupBy(m => m.Groups[1].Value)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            if (originalArgs.Count != translatedArgs.Count) return true;

            foreach (var pair in originalArgs)
            {
                if (!translatedArgs.TryGetValue(pair.Key, out int count) || count != pair.Value)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasTranslatableTitleTagMismatch(string translated, string original)
        {
            if (string.IsNullOrEmpty(original)) return false;

            int originalCount = TranslatableTitleTagRegex.Matches(original).Count;
            if (originalCount == 0) return false;

            int translatedCount = string.IsNullOrEmpty(translated)
                ? 0
                : TranslatableTitleTagRegex.Matches(translated).Count;
            return translatedCount != originalCount;
        }

        private static bool RequiresProtectedTokenParity(string original)
        {
            return ProtectedTokenRegex.IsMatch(original ?? "") || FormatArgumentRegex.IsMatch(original ?? "");
        }


        // 這個方法負責判斷 IsStructureSensitiveText 條件是否成立。
        // EN: This method checks is structure sensitive text.
        private static bool IsStructureSensitiveText(string original)
        {
            if (string.IsNullOrEmpty(original)) return false;
            return original.Contains("->") ||
                   original.IndexOf("[INITIATOR_", StringComparison.Ordinal) >= 0 ||
                   original.IndexOf("[RECIPIENT_", StringComparison.Ordinal) >= 0 ||
                   original.IndexOf("{PAWN", StringComparison.Ordinal) >= 0 ||
                   original.IndexOf("[PAWN_", StringComparison.Ordinal) >= 0 ||
                   original.IndexOf('{') >= 0 ||
                   original.IndexOf('[') >= 0;
        }


        // 這個方法負責處理 翻譯HasLikelyEnglishResidual 相關流程。
        // EN: This method handles translation has likely english residual.
        private static bool TranslationHasLikelyEnglishResidual(string translated, string original, bool recordStat)
        {
            if (!HasLikelyEnglishResidual(translated, original)) return false;

            if (recordStat)
            {
                AddValidationStat(s => s.EnglishResidualDetected++);
            }
            return true;
        }


        // 這個方法負責判斷 HasLikelyEnglishResidual 條件是否成立。
        // EN: This method checks has likely english residual.
        private static bool HasLikelyEnglishResidual(string translated, string original)
        {
            return TranslationResultLanguagePolicy.HasLikelyEnglishResidual(
                translated,
                original,
                AutoTranslatorMod.Settings.TargetLang);
        }
    }
}
