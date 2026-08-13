using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案保存單一 API 金鑰設定與背景請求狀態。
// EN: This file stores one API key configuration and its background request state.

namespace AutoTranslator_Core
{
    // 這個類別負責 ApiKey設定 的主要流程與狀態。
    // EN: This class manages the main workflow and state for ApiKeyConfig.
    public class ApiKeyConfig : IExposable
    {
        // 這個欄位保存 供應商 的執行狀態或快取資料。
        // EN: This field stores provider runtime state or cached data.
        public TranslatorProvider Provider = TranslatorProvider.Google;
        // 這個欄位保存 Label 的執行狀態或快取資料。
        // EN: This field stores label runtime state or cached data.
        public string Label = "";
        // 這個欄位保存 Enabled 的執行狀態或快取資料。
        // EN: This field stores enabled runtime state or cached data.
        public bool Enabled = true;
        // 這個欄位保存 Key 的執行狀態或快取資料。
        // EN: This field stores key runtime state or cached data.
        public string Key = "";
        // 這個欄位保存 CustomBase網址 的執行狀態或快取資料。
        // EN: This field stores custom base URL runtime state or cached data.
        public string CustomBaseUrl = "";
        // 這個欄位保存 Selected模型 的執行狀態或快取資料。
        // EN: This field stores selected model runtime state or cached data.
        public string SelectedModel = "";
        public StructuredOutputPreference StructuredOutput = StructuredOutputPreference.Auto;
        public TranslationTaskTier TaskTier = TranslationTaskTier.Bulk;

        public List<string> FetchedModels = new List<string>();
        [NonSerialized] public Dictionary<string, List<string>> FetchedModelSupportedParameters =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // 這個欄位保存 IsFetching 的執行狀態或快取資料。
        // EN: This method handles expose data.
        [NonSerialized] public bool IsFetching = false;
        // 這個欄位保存 lastFetchedKey 的執行狀態或快取資料。
        // EN: This method handles expose data.
        [NonSerialized] public string lastFetchedKey = "";
        // 這個欄位保存 Pending取得Fingerprint 的執行狀態或快取資料。
        // EN: This method handles expose data.
        [NonSerialized] public string PendingFetchFingerprint = "";
        // 這個欄位保存 取得RetryCount 的執行狀態或快取資料。
        // EN: This method handles expose data.
        [NonSerialized] public int FetchRetryCount = 0;
        // 這個欄位保存 Next模型取得RetryUtcTicks 的執行狀態或快取資料。
        // EN: This method handles expose data.
        [NonSerialized] public long NextModelFetchRetryUtcTicks = 0L;
        // 這個欄位保存 取得StartedUtcTicks 的執行狀態或快取資料。
        // EN: This method handles expose data.
        [NonSerialized] public long FetchStartedUtcTicks = 0L;
        // 這個欄位保存 取得Generation 的執行狀態或快取資料。
        // EN: This method handles expose data.
        [NonSerialized] public int FetchGeneration = 0;


        // 這個欄位保存 IsTesting 的執行狀態或快取資料。
        // EN: This method handles expose data.
        [NonSerialized] public bool IsTesting = false;
        // 這個欄位保存 測試StartedUtcTicks 的執行狀態或快取資料。
        // EN: This method handles expose data.
        [NonSerialized] public long TestStartedUtcTicks = 0L;
        // 這個欄位保存 測試Generation 的執行狀態或快取資料。
        // EN: This method handles expose data.
        [NonSerialized] public int TestGeneration = 0;
        // 這個方法負責處理 Expose資料 相關流程。
        // EN: This method handles expose data.
        public void ExposeData()
        {
            Scribe_Values.Look(ref Provider, "Provider", TranslatorProvider.Google);
            Scribe_Values.Look(ref Label, "Label", "");
            Scribe_Values.Look(ref Enabled, "Enabled", true);
            Scribe_Values.Look(ref Key, "Key", "");
            Scribe_Values.Look(ref CustomBaseUrl, "CustomBaseUrl", "");
            Scribe_Values.Look(ref SelectedModel, "SelectedModel", "");
            Scribe_Values.Look(ref StructuredOutput, "StructuredOutput", StructuredOutputPreference.Auto);
            Scribe_Values.Look(ref TaskTier, "TaskTier", TranslationTaskTier.Bulk);
            Scribe_Collections.Look(ref FetchedModels, "FetchedModels", LookMode.Value);

            if (FetchedModels == null) FetchedModels = new List<string>();
            if (FetchedModelSupportedParameters == null)
                FetchedModelSupportedParameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        public bool ModelSupportsParameter(string model, string parameter)
        {
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(parameter)) return false;
            return FetchedModelSupportedParameters != null &&
                   FetchedModelSupportedParameters.TryGetValue(model, out List<string> parameters) &&
                   parameters != null &&
                   parameters.Any(value => string.Equals(value, parameter, StringComparison.OrdinalIgnoreCase));
        }
    }
}
