using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責雲端 API 的共用基底設定。
// EN: This file stores shared cloud API endpoint settings.

namespace AutoTranslator_Core
{


    // 這個類別負責 自動翻譯器雲端Client 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorCloudClient.
    public static partial class AutoTranslatorCloudClient
    {
        // 這個常數定義 PrimaryApiBase網址 的固定值。
        // EN: This constant defines the fixed value for primary API base URL.
        public const string PrimaryApiBaseUrl = "https://api.anln666-nas.xyz/api/v1";
        // 這個常數定義 備份ApiBase網址 的固定值。
        // EN: This constant defines the fixed value for backup API base URL.
        public const string BackupApiBaseUrl = "https://cn-api.anln666-nas.xyz/api/v1";
        // 這個欄位保存 雲端ApiBase網址 的執行狀態或快取資料。
        // EN: This field stores cloud API base URL runtime state or cached data.
        public static string CloudApiBaseUrl = PrimaryApiBaseUrl;

        static AutoTranslatorCloudClient()
        {
#if !RIMWORLD_1_7
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
#endif
        }
        // 這個方法負責處理 Log雲端TranslatedWarning 相關流程。
        // EN: This method handles log cloud translated warning.
        private static void LogCloudTranslatedWarning(string key)
        {
            ATC_Dispatcher.RunOnMainThread(() => Verse.Log.Warning("[ATC Cloud] " + key.Translate()));
        }
    }

}
