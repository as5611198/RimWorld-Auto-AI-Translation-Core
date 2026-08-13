using Newtonsoft.Json.Linq;
using System;
using System.Linq;
// 這個檔案負責 UI 文字安全檢查。
// EN: This file checks whether UI text is safe to translate.

namespace AutoTranslator_Core
{
    // 這個類別負責 UIInterceptor 的主要流程與狀態。
    // EN: This class manages the main workflow and state for UIInterceptor.
    public static partial class UIInterceptor
    {
        // 這個結構保存 UITranslationTextContext 所需的資料欄位。
        // EN: This struct stores data used by UITranslationTextContext.
        private struct UITranslationTextContext
        {
            // 這個欄位保存 OriginalText 的執行狀態或快取資料。
            // EN: This field stores original text runtime state or cached data.
            public string OriginalText;
            // 這個欄位保存 翻譯Text 的執行狀態或快取資料。
            // EN: This field stores translation text runtime state or cached data.
            public string TranslationText;
            // 這個欄位保存 Prefix 的執行狀態或快取資料。
            // EN: This field stores prefix runtime state or cached data.
            public string Prefix;
            // 這個欄位保存 HasLogTimestamp 的執行狀態或快取資料。
            // EN: This field stores has log timestamp runtime state or cached data.
            public bool HasLogTimestamp;
        }

        // 這個方法負責建立 TextContext 所需資料。
        // EN: This method builds text context.
        private static UITranslationTextContext BuildTextContext(string text)
        {
            UITranslationTextContext context = new UITranslationTextContext
            {
                OriginalText = text,
                TranslationText = text ?? string.Empty,
                Prefix = string.Empty,
                HasLogTimestamp = false
            };

            if (string.IsNullOrEmpty(text)) return context;

            var match = LogTimestampRegex.Match(text);
            if (match.Success)
            {
                context.Prefix = match.Groups["prefix"].Value;
                context.TranslationText = match.Groups["body"].Value ?? string.Empty;
                context.HasLogTimestamp = true;
            }

            return context;
        }

        // 這個方法負責判斷 ShouldInterceptText 條件是否成立。
        // EN: This method checks should intercept text.
        private static void RememberTextDecision(string decisionKey, bool shouldIntercept)
        {
            if (string.IsNullOrEmpty(decisionKey)) return;
            if (TextDecisionCache.Count >= MaxTextDecisionCacheSize) TextDecisionCache.Clear();
            TextDecisionCache[decisionKey] = shouldIntercept;
        }

        private static string NormalizeDynamicNumberTemplate(string text)
        {
            return UIDynamicNumberTemplate.Normalize(text);
        }

        private static string RestoreDynamicNumbers(string original, string translated)
        {
            return UIDynamicNumberTemplate.Restore(original, translated);
        }

        internal static bool ShouldInterceptText(string text)
        {
            return ShouldInterceptText(text, true);
        }

        internal static bool ShouldBypassUIPatchText(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            if (text[0] == '\u200B') return true;
            if (AutoTranslatorMod.Settings.EnableUIErrorLogInterception) return false;

            string cacheKey = text.Length <= 256 ? text : text.Substring(0, 256);
            if (FastBypassDecisionCache.TryGetValue(cacheKey, out bool cachedDecision))
            {
                return cachedDecision;
            }

            bool shouldBypass = LooksLikeFastLogOrStackText(text);
            if (FastBypassDecisionCache.Count >= MaxTextDecisionCacheSize) FastBypassDecisionCache.Clear();
            FastBypassDecisionCache[cacheKey] = shouldBypass;
            return shouldBypass;
        }

        // 這個方法負責判斷 ShouldInterceptText 條件是否成立。
        // EN: This method checks should intercept text.
        private static bool ShouldInterceptText(string text, bool rememberSkipped)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (ShouldBypassUIPatchText(text)) return false;

            string decisionKey = BuildCacheKey(GetTranslationLookupText(text));
            if (TextDecisionCache.TryGetValue(decisionKey, out bool cachedDecision))
            {
                return cachedDecision;
            }

            UITranslationTextContext context = BuildTextContext(text);
            bool shouldIntercept;
            if (context.HasLogTimestamp && !AutoTranslatorMod.Settings.EnableUIErrorLogInterception)
            {
                if (rememberSkipped) RememberIgnored(text);
                shouldIntercept = false;
            }
            else
            {
                shouldIntercept = !ShouldSkipUITranslationSourceText(context.TranslationText);
            }

            RememberTextDecision(decisionKey, shouldIntercept);
            return shouldIntercept;
        }

        private static bool LooksLikeFastLogOrStackText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (HasFastLogTimestampPrefix(text)) return true;

            bool isMultiline = text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0;
            int length = text.Length;

            if (ContainsIgnoreCase(text, "[Ref ")) return true;
            if (ContainsIgnoreCase(text, "UnityEngine.StackTraceUtility")) return true;
            if (ContainsIgnoreCase(text, "Verse.Log:")) return true;
            if (ContainsIgnoreCase(text, "(wrapper ")) return true;
            if (ContainsIgnoreCase(text, "HarmonyLib.")) return true;
            if (ContainsIgnoreCase(text, "MonoMod.")) return true;
            if (ContainsIgnoreCase(text, "RimWorld_Auto_AI_Translation_Core")) return true;
            if (ContainsIgnoreCase(text, "[AutoTranslationCore]")) return true;

            if (isMultiline)
            {
                if (ContainsIgnoreCase(text, "\nat ")) return true;
                if (ContainsIgnoreCase(text, "\r at ")) return true;
                if (ContainsIgnoreCase(text, "\n  at ")) return true;
                if (ContainsIgnoreCase(text, "\r  at ")) return true;
                if (ContainsIgnoreCase(text, "stack trace")) return true;
            }

            if (ContainsIgnoreCase(text, "Exception getting types in assembly")) return true;
            if (ContainsIgnoreCase(text, "Error in static constructor")) return true;
            if (ContainsIgnoreCase(text, "Could not resolve cross-reference")) return true;
            if (ContainsIgnoreCase(text, "PatchOperation")) return true;

            bool hasException = ContainsIgnoreCase(text, "Exception");
            if (hasException && (isMultiline || length > 40 || ContainsIgnoreCase(text, "System.") || ContainsIgnoreCase(text, " ---> ")))
            {
                return true;
            }

            if (length > 80 && (
                ContainsIgnoreCase(text, "System.Reflection.") ||
                ContainsIgnoreCase(text, "System.Runtime") ||
                ContainsIgnoreCase(text, "System.NullReferenceException") ||
                ContainsIgnoreCase(text, "System.MissingMethodException") ||
                ContainsIgnoreCase(text, "System.TypeInitializationException")))
            {
                return true;
            }

            return false;
        }

        private static bool HasFastLogTimestampPrefix(string text)
        {
            int index = 0;
            int length = text.Length;
            while (index < length && char.IsWhiteSpace(text[index])) index++;

            if (index < length && text[index] == '(')
            {
                int closeIndex = index + 1;
                bool hasDigit = false;
                while (closeIndex < length && char.IsDigit(text[closeIndex]))
                {
                    hasDigit = true;
                    closeIndex++;
                }

                if (hasDigit && closeIndex < length && text[closeIndex] == ')')
                {
                    index = closeIndex + 1;
                    while (index < length && char.IsWhiteSpace(text[index])) index++;
                }
            }

            if (index >= length || text[index] != '[') return false;
            index++;

            int hourStart = index;
            while (index < length && char.IsDigit(text[index])) index++;
            int hourDigits = index - hourStart;
            if (hourDigits < 1 || hourDigits > 2) return false;
            if (index >= length || text[index] != ':') return false;
            index++;

            if (!HasTwoDigitsAt(text, index)) return false;
            index += 2;
            if (index >= length || text[index] != ':') return false;
            index++;

            if (!HasTwoDigitsAt(text, index)) return false;
            index += 2;
            return index < length && text[index] == ']';
        }

        private static bool HasTwoDigitsAt(string text, int index)
        {
            return index + 1 < text.Length
                && char.IsDigit(text[index])
                && char.IsDigit(text[index + 1]);
        }

        // 這個方法負責判斷 ShouldLoadCachedText 條件是否成立。
        // EN: This method checks should load cached text.
        private static bool ShouldLoadCachedText(string text)
        {
            UITranslationTextContext context = BuildTextContext(text);
            return !ShouldSkipUITranslationSourceText(context.TranslationText);
        }

        // 這個方法負責取得 翻譯LookupText 資料。
        // EN: This method gets translation lookup text.
        internal static string GetTranslationLookupText(string text)
        {
            return NormalizeDynamicNumberTemplate(BuildTextContext(text).TranslationText);
        }

        // 這個方法負責處理 Restore翻譯DisplayText 相關流程。
        // EN: This method handles restore translation display text.
        internal static string RestoreTranslationDisplayText(string original, string translated)
        {
            UITranslationTextContext context = BuildTextContext(original);
            string restored = RestoreDynamicNumbers(context.TranslationText, translated);
            return context.HasLogTimestamp ? context.Prefix + restored : restored;
        }

        // 這個方法負責判斷 ShouldSkipUITranslationText 條件是否成立。
        // EN: This method checks should skip UI translation text.
        private static bool ShouldSkipUITranslationText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;

            string trimmed = text.Trim();
            if (trimmed.Length < 2) return true;
            if (trimmed.StartsWith("\u200B", StringComparison.Ordinal)) return true;
            if (!LetterRegex.IsMatch(trimmed)) return true;
            if (LooksLikeVolatileReadout(trimmed)) return true;
            if (LooksLikeStructuredData(trimmed)) return true;
            if (LooksLikeRimWorldRichText(trimmed)) return true;
            if (LooksLikeModListCompositeText(trimmed)) return true;
            if (LooksLikeStackCountReadout(trimmed)) return true;
            if (LooksLikeInternalKey(trimmed)) return true;
            if (LooksLikeNumericStatusText(trimmed)) return true;
            if (LooksLikeDeveloperLogText(trimmed)) return true;

            return false;
        }

        // 這個方法負責清理並標準化 UITranslationResult 內容。
        // EN: This method cleans and normalizes UI translation result.
        private static string SanitizeUITranslationResult(string original, string translated)
        {
            if (!HasRenderableUICharacters(translated)) return null;

            string cleaned = GetTranslationLookupText(translated).Trim().Trim('\u200B');
            cleaned = TryExtractWrappedText(cleaned) ?? cleaned;
            cleaned = cleaned.Trim();

            if (!HasRenderableUICharacters(cleaned)) return null;
            if (LanguageDetector.LooksLikePlaceholderTranslation(cleaned, AutoTranslatorMod.Settings.TargetLang)) return null;
            if (!HasDynamicNumberTemplate(original) && HasDynamicNumberTemplate(cleaned)) return null;
            if (HasDynamicNumberTemplate(original) && HasDynamicNumberTemplateLoss(original, cleaned)) return null;
            if (LooksLikeStructuredData(cleaned))
            {
                string unwrapped = TryExtractKeyValueText(cleaned);
                if (string.IsNullOrWhiteSpace(unwrapped) || LooksLikeStructuredData(unwrapped.Trim())) return null;
                cleaned = unwrapped.Trim();
            }

            string normalized = LanguageDetector.NormalizeChineseVariant(cleaned, AutoTranslatorMod.Settings.TargetLang);
            return HasRenderableUICharacters(normalized) ? normalized : null;
        }

        private static bool HasRenderableUICharacters(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.Any(char.IsLetterOrDigit);
        }

        private static bool HasDynamicNumberTemplate(string text)
        {
            return !string.IsNullOrEmpty(text) && UIDynamicNumberTemplate.PlaceholderRegex.IsMatch(text);
        }

        private static bool HasDynamicNumberTemplateLoss(string original, string translated)
        {
            int originalCount = UIDynamicNumberTemplate.PlaceholderRegex.Matches(original ?? "").Count;
            if (originalCount == 0) return false;
            int translatedCount = UIDynamicNumberTemplate.PlaceholderRegex.Matches(translated ?? "").Count;
            return translatedCount < originalCount;
        }

        private static bool ShouldSkipUITranslationSourceText(string text)
        {
            if (ShouldSkipUITranslationText(text)) return true;

            string sample = GetLanguageDetectionSample(text);
            if (string.IsNullOrWhiteSpace(sample)) return true;
            if (!LetterRegex.IsMatch(sample)) return true;
            if (LooksLikeTargetLanguageSourceSample(sample)) return true;

            return false;
        }

        private static string GetLanguageDetectionSample(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            string sample = UIDynamicNumberTemplate.PlaceholderRegex.Replace(text, " ");
            sample = LanguageDetectionNumericMarkerRegex.Replace(sample, " ");
            sample = UIDynamicNumberTemplate.NumberRegex.Replace(sample, " ");
            sample = LanguageDetectionNumericMarkerRegex.Replace(sample, " ");
            sample = UIDynamicNumberTemplate.NumberRegex.Replace(sample, " ");
            sample = System.Text.RegularExpressions.Regex.Replace(sample, @"\s+", " ");
            return sample.Trim();
        }

        private static bool LooksLikeTargetLanguageSourceSample(string sample)
        {
            if (string.IsNullOrWhiteSpace(sample)) return false;
            if (LanguageDetector.LooksLikeTargetLanguage(sample, AutoTranslatorMod.Settings.TargetLang)) return true;

            bool hasEnglish = EnglishRegex.IsMatch(sample);
            bool hasCyrillic = CyrillicRegex.IsMatch(sample);
            bool hasKana = KanaRegex.IsMatch(sample);
            bool hasHangul = HangulRegex.IsMatch(sample);
            bool hasCJK = CJKRegex.IsMatch(sample);

            switch (AutoTranslatorMod.Settings.TargetLang)
            {
                case TargetLanguage.Traditional:
                    return hasCJK
                        && !hasEnglish
                        && !hasCyrillic
                        && !hasKana
                        && !hasHangul
                        && !LanguageDetector.LooksLikeSimplified(sample);

                case TargetLanguage.Simplified:
                    return hasCJK
                        && !hasEnglish
                        && !hasCyrillic
                        && !hasKana
                        && !hasHangul
                        && !LanguageDetector.LooksLikeTraditional(sample);

                default:
                    return false;
            }
        }

        // 這個方法負責嘗試執行 清理UIReplacementText 並回報是否成功。
        // EN: This method tries to sanitize UI replacement text and reports whether it succeeded.
        internal static bool TrySanitizeUIReplacementText(string original, string translated, out string sanitized)
        {
            sanitized = null;

            string lookupOriginal = GetTranslationLookupText(original);
            string cleanTranslation = SanitizeUITranslationResult(lookupOriginal, translated);
            if (string.IsNullOrWhiteSpace(cleanTranslation)) return false;
            if (string.Equals(lookupOriginal, cleanTranslation, StringComparison.Ordinal)) return false;
            if (ShouldSkipUITranslationText(cleanTranslation)) return false;

            sanitized = RestoreTranslationDisplayText(original, cleanTranslation);
            return true;
        }

        // 這個方法負責處理 LooksLikeStructured資料 相關流程。
        // EN: This method handles looks like structured data.
        private static bool LooksLikeStructuredData(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string trimmed = text.Trim();
            if (trimmed.Length == 0) return false;

            if ((trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
                || (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)))
            {
                return true;
            }

            if ((trimmed.StartsWith("<", StringComparison.Ordinal) && trimmed.EndsWith(">", StringComparison.Ordinal))
                && trimmed.IndexOf(' ', 1) > 0)
            {
                return true;
            }

            if (DataKeyValueRegex.IsMatch(trimmed)) return true;
            if (trimmed.Contains("\":") || trimmed.Contains("':")) return true;

            return false;
        }

        // 這個方法負責處理 LooksLikeVolatileReadout 相關流程。
        // EN: This method handles looks like volatile readout.
        private static bool LooksLikeVolatileReadout(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string trimmed = text.Trim();
            return VolatileMetricRegex.IsMatch(trimmed)
                || TemperatureReadoutRegex.IsMatch(trimmed);
        }

        // 這個方法負責處理 LooksLikeStackCountReadout 相關流程。
        // EN: This method handles looks like stack count readout.
        private static bool LooksLikeStackCountReadout(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!StackCountRegex.IsMatch(text)) return false;

            string withoutCount = System.Text.RegularExpressions.Regex.Replace(text, @"[xX×]\s*\d{1,6}\s*$", "").Trim();
            if (LanguageDetector.LooksLikeTargetLanguage(withoutCount, AutoTranslatorMod.Settings.TargetLang)) return true;

            bool hasEnglish = EnglishRegex.IsMatch(withoutCount);
            switch (AutoTranslatorMod.Settings.TargetLang)
            {
                case TargetLanguage.Traditional:
                case TargetLanguage.Simplified:
                    return CJKRegex.IsMatch(withoutCount) && !hasEnglish;
                case TargetLanguage.Japanese:
                    return (CJKRegex.IsMatch(withoutCount) || KanaRegex.IsMatch(withoutCount)) && !hasEnglish;
                case TargetLanguage.Korean:
                    return HangulRegex.IsMatch(withoutCount) && !hasEnglish;
                case TargetLanguage.Russian:
                case TargetLanguage.Ukrainian:
                    return CyrillicRegex.IsMatch(withoutCount) && !hasEnglish;
                default:
                    return false;
            }
        }

        // 這個方法負責處理 LooksLikeInternalKey 相關流程。
        // EN: This method handles looks like internal key.
        private static bool LooksLikeInternalKey(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string trimmed = text.Trim();
            if (trimmed.Contains(" ")) return false;
            if (trimmed.Length > 80) return false;
            return InternalKeyRegex.IsMatch(trimmed);
        }

        // 這個方法負責處理 LooksLikeNumericStatusText 相關流程。
        // EN: This method handles looks like numeric status text.
        private static bool LooksLikeNumericStatusText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string trimmed = text.Trim();
            if (trimmed.Length < 8) return false;
            int numericMatches = NumericStatusRegex.Matches(trimmed).Count;
            if (trimmed.IndexOf('\n') >= 0 && numericMatches >= 2) return true;
            if (numericMatches >= 3) return true;
            if (numericMatches >= 2 && trimmed.IndexOf('=') >= 0) return true;
            if (trimmed.IndexOf("Stack amount", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (trimmed.IndexOf("Processed ", StringComparison.OrdinalIgnoreCase) >= 0 && trimmed.IndexOf(" amount", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        // 這個方法負責處理 LooksLikeDeveloperLogText 相關流程。
        // EN: This method handles looks like developer log text.
        private static bool LooksLikeDeveloperLogText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string trimmed = text.Trim();
            if (trimmed.Length > 220 && (
                trimmed.IndexOf("System.", StringComparison.Ordinal) >= 0 ||
                trimmed.IndexOf("Verse.", StringComparison.Ordinal) >= 0 ||
                trimmed.IndexOf("Harmony", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trimmed.IndexOf("stack trace", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trimmed.IndexOf("Source file:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trimmed.IndexOf(".xml", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            if (trimmed.IndexOf("\\steamapps\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trimmed.IndexOf("/steamapps/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (trimmed.IndexOf("PatchOperation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trimmed.IndexOf("Could not resolve cross-reference", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        // 這個方法負責處理 LooksLikeRimWorldRichText 相關流程。
        // EN: This method handles looks like rim world rich text.
        private static bool LooksLikeRimWorldRichText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            return ContainsIgnoreCase(text, "<color=")
                || ContainsIgnoreCase(text, "</color>")
                || ContainsIgnoreCase(text, "<size=")
                || ContainsIgnoreCase(text, "</size>")
                || ContainsIgnoreCase(text, "<b>")
                || ContainsIgnoreCase(text, "</b>")
                || ContainsIgnoreCase(text, "<i>")
                || ContainsIgnoreCase(text, "</i>");
        }

        // 這個方法負責處理 LooksLike模組ListCompositeText 相關流程。
        // EN: This method handles looks like mod list composite text.
        private static bool LooksLikeModListCompositeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.IndexOf('\n') < 0) return false;

            string[] lines = text
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();

            return lines.Any(IsPackageIdLike);
        }

        // 這個方法負責判斷 IsPackageIdLike 條件是否成立。
        // EN: This method checks is package id like.
        private static bool IsPackageIdLike(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;

            string trimmed = line.Trim();
            if (trimmed.Length < 4 || trimmed.Length > 140) return false;
            if (!trimmed.Contains(".")) return false;
            if (trimmed.Any(char.IsWhiteSpace)) return false;
            if (!trimmed.Any(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z')) return false;

            return trimmed.All(c =>
                c >= 'A' && c <= 'Z' ||
                c >= 'a' && c <= 'z' ||
                c >= '0' && c <= '9' ||
                c == '.' ||
                c == '_' ||
                c == '-');
        }

        // 這個方法負責處理 ContainsIgnoreCase 相關流程。
        // EN: This method handles contains ignore case.
        private static bool ContainsIgnoreCase(string text, string value)
        {
            return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 這個方法負責嘗試執行 ExtractWrappedText 並回報是否成功。
        // EN: This method tries to extract wrapped text and reports whether it succeeded.
        private static string TryExtractWrappedText(string text)
        {
            string fromJson = TryExtractJsonObjectText(text);
            if (!string.IsNullOrWhiteSpace(fromJson)) return fromJson;

            string fromKeyValue = TryExtractKeyValueText(text);
            if (!string.IsNullOrWhiteSpace(fromKeyValue)) return fromKeyValue;

            return null;
        }

        // 這個方法負責嘗試執行 ExtractJSONObjectText 並回報是否成功。
        // EN: This method tries to extract json object text and reports whether it succeeded.
        private static string TryExtractJsonObjectText(string text)
        {
            try
            {
                if (!text.TrimStart().StartsWith("{", StringComparison.Ordinal)) return null;

                JObject obj = JObject.Parse(text);
                foreach (string key in new[] { "text", "translation", "translated", "result", "value" })
                {
                    JToken token;
                    if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out token)
                        && token.Type == JTokenType.String)
                    {
                        return token.Value<string>();
                    }
                }

                if (obj.Count == 1)
                {
                    JProperty property = obj.Properties().FirstOrDefault();
                    if (property != null && property.Value.Type == JTokenType.String)
                    {
                        return property.Value.Value<string>();
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        // 這個方法負責嘗試執行 ExtractKeyValueText 並回報是否成功。
        // EN: This method tries to extract key value text and reports whether it succeeded.
        private static string TryExtractKeyValueText(string text)
        {
            var match = DataKeyValueRegex.Match(text);
            if (!match.Success) return null;

            string value = text.Substring(match.Length).Trim();
            if (value.Length == 0) return null;

            char quote = value[0];
            if (quote == '"' || quote == '\'')
            {
                value = value.Substring(1);
                int endQuote = value.IndexOf(quote);
                if (endQuote >= 0) value = value.Substring(0, endQuote);
            }

            return value.Trim();
        }
    }
}
