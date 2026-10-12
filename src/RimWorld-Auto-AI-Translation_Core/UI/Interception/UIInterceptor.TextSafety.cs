using System;
using System.Collections.Concurrent;

namespace AutoTranslator_Core
{
    public static partial class UIInterceptor
    {
        private const int MaxTextDecisionCacheSize = 20000;
        private static readonly ConcurrentDictionary<string, bool> FastBypassDecisionCache =
            new ConcurrentDictionary<string, bool>();

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

        // 這個方法負責處理 ContainsIgnoreCase 相關流程。
        // EN: This method handles contains ignore case.
        private static bool ContainsIgnoreCase(string text, string value)
        {
            return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
