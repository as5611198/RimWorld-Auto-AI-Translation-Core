namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorAPI
    {
        public enum TranslationRequestFailureKind
        {
            None,
            Cancelled,
            LocalDispatch,
            ResponseTimeout,
            Http,
            ConcurrencyLimit,
            QuotaExhausted,
            InvalidResponse,
            Configuration,
            Transport
        }
    }
}
