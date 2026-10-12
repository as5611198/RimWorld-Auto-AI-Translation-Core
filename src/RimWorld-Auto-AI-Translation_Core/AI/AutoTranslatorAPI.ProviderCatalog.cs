using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責翻譯供應商與網址規則。
// EN: This file resolves translation providers, base URLs, and runtime profiles.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器API 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorAPI.
    public static partial class AutoTranslatorAPI
    {
        // 這個結構保存 供應商Def 所需的資料欄位。
        // EN: This struct stores data used by ProviderDef.
        public struct ProviderDef
        {
            // 這個欄位保存 Base網址 的執行狀態或快取資料。
            // EN: This field stores base URL runtime state or cached data.
            public string BaseUrl;
            // 這個欄位保存 ListModels網址 的執行狀態或快取資料。
            // EN: This field stores list models URL runtime state or cached data.
            public string ListModelsUrl;
        }


        // 這個結構保存 供應商執行期Profile 所需的資料欄位。
        // EN: This struct stores data used by ProviderRuntimeProfile.



        // 這個方法負責取得 Base網址 資料。
        // EN: This method gets base URL.
        private static string GetBaseUrl(ApiKeyConfig config)
        {
            string custom = CleanInput(config.CustomBaseUrl);
            if (!string.IsNullOrEmpty(custom))
            {

                if (!custom.StartsWith("http://") && !custom.StartsWith("https://"))
                    custom = "http://" + custom;

                if (Uri.TryCreate(custom, UriKind.Absolute, out Uri validUri))
                {

                    string cleanUrl = validUri.AbsoluteUri.TrimEnd('/');


                    if (cleanUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanUrl = cleanUrl.Substring(0, cleanUrl.Length - 17);
                    }
                    else if (cleanUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanUrl = cleanUrl.Substring(0, cleanUrl.Length - 7);
                    }


                    return cleanUrl;
                }
                else
                {

                    Log.Warning($"[AutoTranslationCore] " + TranslateText("ATC_Warning_InvalidUrlFallback", custom));
                    return custom;
                }
            }

            if (config.Provider == TranslatorProvider.DeepL)
            {
                return (!string.IsNullOrEmpty(config.Key) && config.Key.Trim().EndsWith(":fx"))
                    ? "https://api-free.deepl.com/v2"
                    : "https://api.deepl.com/v2";
            }


            if (ProviderRegistry.TryGetValue(config.Provider, out var def))
            {
                return def.BaseUrl;
            }
            return "https://api.openai.com/v1";
        }

        // 這個方法負責清理並標準化 Input 內容。
        // EN: This method cleans and normalizes input.
        public static string CleanInput(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";


            var builder = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (!char.IsWhiteSpace(c) && c >= 32 && c <= 126)
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }


    }
}
