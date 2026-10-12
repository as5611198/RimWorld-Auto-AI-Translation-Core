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
            UnityTransportStall,
            Http,
            ConcurrencyLimit,
            QuotaExhausted,
            InvalidResponse,
            Configuration,
            BudgetDenied,
            Transport
        }

        public class ATC_WebResponse
        {
            // 這個欄位保存 IsSuccess 的執行狀態或快取資料。
            // EN: This field stores is success runtime state or cached data.
            public bool IsSuccess;
            // 這個欄位保存 HTTPCode 的執行狀態或快取資料。
            // EN: This field stores http code runtime state or cached data.
            public long HttpCode;
            // 這個欄位保存 ErrorText 的執行狀態或快取資料。
            // EN: This field stores error text runtime state or cached data.
            public string ErrorText;
            // 這個欄位保存 回應Body 的執行狀態或快取資料。
            // EN: This field stores response body runtime state or cached data.
            public string ResponseBody;
            public bool BudgetDenied;
            public string BudgetDenialReason;
            public TranslationRequestFailureKind FailureKind;
            public string FailureStage;
            public int ItemCount;
            public long SourceCharacters;
            public long EstimatedInputTokens;
            public int TimeoutSeconds;
            public long RequestBodyBytes;
            public long UploadedBytes;
            public float UploadProgress;
            public long DownloadedBytes;
            public string UnityResult;
        }

    }
}
