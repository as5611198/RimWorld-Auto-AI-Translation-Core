namespace AutoTranslator_Core
{
    internal static class TranslationValidationReasons
    {
        public const string EmptyResponse = "empty_response";
        public const string EnglishResidual = "english_residual";
        public const string WrongChineseVariant = "wrong_chinese_variant";
        public const string WrongTargetLanguage = "wrong_target_language";
        public const string ProtectedTokenMismatch = "protected_token_mismatch";
        public const string FormatArgumentMismatch = "format_argument_mismatch";
        public const string TitleTagMismatch = "title_tag_mismatch";
        public const string Unknown = "unknown";
    }
}
