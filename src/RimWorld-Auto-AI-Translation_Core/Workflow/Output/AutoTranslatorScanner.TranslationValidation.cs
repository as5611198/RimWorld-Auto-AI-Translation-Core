using System;
namespace AutoTranslator_Core
{
    // Shared mechanical validation used by the workflow, imports and manual edits.
    public static partial class AutoTranslatorScanner
    {

        // 這個方法負責嘗試執行 AcceptTranslatedValue 並回報是否成功。
        // EN: This method tries to accept translated value and reports whether it succeeded.
        internal static bool TryAcceptTranslatedValue(
            string translated,
            string sourceText,
            out string sanitized,
            out string failureReason,
            out string failureDetail)
        {
            failureReason = string.Empty;
            failureDetail = string.Empty;
            sanitized = SanitizeTranslationResult(translated, sourceText);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                failureReason = TranslationValidationReasons.EmptyResponse;
                failureDetail = "The provider returned an empty or unusable translation.";
                return false;
            }

            if (HasTranslatableTitleTagMismatch(sanitized, sourceText))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                failureReason = TranslationValidationReasons.TitleTagMismatch;
                failureDetail = "A [title:] tag was changed or lost.";
                return false;
            }

            if (HasProtectedTokenMismatch(sanitized, sourceText))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                failureReason = TranslationValidationReasons.ProtectedTokenMismatch;
                failureDetail = "A protected token was changed or lost.";
                return false;
            }

            if (HasFormatArgumentMismatch(sanitized, sourceText))
            {
                AddValidationStat(s => s.ProtectedTokenMismatchFallback++);
                failureReason = TranslationValidationReasons.FormatArgumentMismatch;
                failureDetail = "A format argument such as {0} was changed or lost.";
                return false;
            }

            return true;
        }
    }
}
