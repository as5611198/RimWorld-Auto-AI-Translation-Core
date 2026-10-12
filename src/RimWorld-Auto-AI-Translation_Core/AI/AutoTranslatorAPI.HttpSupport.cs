using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責翻譯 API 的 HTTP 共用支援。
// EN: This file provides shared HTTP support for translation APIs.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器API 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorAPI.
    public static partial class AutoTranslatorAPI
    {
    }

    // 這個類別負責 UnityWebRequestCompat 的主要流程與狀態。
    // EN: This class manages the main workflow and state for UnityWebRequestCompat.
    internal static class UnityWebRequestCompat
    {
        // 這個方法負責判斷 IsSuccess 條件是否成立。
        // EN: This method checks is success.
        public static bool IsSuccess(UnityEngine.Networking.UnityWebRequest request)
        {
            if (request == null) return false;
            return request.result == UnityEngine.Networking.UnityWebRequest.Result.Success;
        }
    }
}
