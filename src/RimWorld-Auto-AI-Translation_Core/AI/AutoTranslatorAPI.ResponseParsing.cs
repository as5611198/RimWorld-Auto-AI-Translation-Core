using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責翻譯回應的解析與清理。
// EN: This file parses and cleans translation API responses.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器API 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorAPI.
    public static partial class AutoTranslatorAPI
    {

        // 這個方法負責處理 Extract清理JSON 相關流程。
        // EN: This method handles extract clean json.
        private static string ExtractCleanJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;


            int start = text.IndexOfAny(new char[] { '[', '{' });
            if (start == -1) return text;

            char openChar = text[start];
            char closeChar = openChar == '[' ? ']' : '}';
            int depth = 0;
            bool inString = false;
            bool escape = false;


            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == openChar) depth++;
                else if (c == closeChar)
                {
                    depth--;
                    if (depth == 0)
                    {

                        return text.Substring(start, i - start + 1);
                    }
                }
            }
            return text.Substring(start);
        }


        // 這個方法負責解析 翻譯Payload 內容。
        // EN: This method parses translation payload.
        private static List<string> ParseTranslationPayload(string raw, int count)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            raw = raw.Trim();
            raw = System.Text.RegularExpressions.Regex.Replace(raw, @"^\s*```(?:json)?\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\s*```\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            raw = ExtractCleanJson(raw).Trim();

            JToken token = JToken.Parse(raw, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (token is JArray directArray) return ReadStringArray(directArray, count);
            if (token is JObject obj)
            {
                string[] commonKeys = { "translations", "translation", "result", "results", "data", "items", "output" };
                foreach (string key in commonKeys)
                    if (obj[key] is JArray childArray) return ReadStringArray(childArray, count);
                // Numeric-object protocol is exactly 0..count-1, without aliases or unknown keys.
                if (obj.Count != count) return null;
                var values = new List<string>(count);
                for (int i = 0; i < count; i++)
                {
                    JToken value = obj[i.ToString(System.Globalization.CultureInfo.InvariantCulture)];
                    if (value == null || value.Type != JTokenType.String) return null;
                    values.Add((string)value);
                }
                return values;
            }

            return null;
        }


        private static List<string> ReadStringArray(JArray array, int count)
        {
            if (array.Count != count || array.Any(x => x.Type != JTokenType.String)) return null;
            return array.Select(x => (string)x).ToList();
        }

        // 這個方法負責解析 回應 內容。
        // EN: This method parses response.
        private static List<string> ParseResponse(string json, TranslatorProvider p, int count, bool expectsGoogleFormat)
        {
            try
            {
                var obj = JObject.Parse(json);


                if (p == TranslatorProvider.DeepL)
                {
                    var translations = obj["translations"];
                    if (translations == null) return null;

                    var result = new List<string>();
                    foreach (var item in translations)
                    {
                        result.Add(item["text"]?.ToString() ?? "");
                    }
                    return (result.Count == count) ? result : null;
                }

                string raw = expectsGoogleFormat
                                    ? ExtractGoogleAssistantContent(obj)
                                    : obj["choices"]?[0]?["message"]?["content"]?.ToString();


                if (string.IsNullOrWhiteSpace(raw))
                {
                    Log.Warning($"[AutoTranslationCore] 解析到空內容 (API 可能觸發了空包彈 Bug)，準備觸發重試機制...");
                    return null;
                }

                List<string> list = ParseTranslationPayload(raw, count);


                if (list != null && list.Count == count)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(list[i]))
                        {
                            list[i] = list[i].Replace("\\n", "\n").Replace("\\r", "\r");
                        }
                    }
                    return list;
                }
                return null;
            }

            catch (Exception e)
            {

                string preview = string.IsNullOrEmpty(json) ? "NULL" : (json.Length > 200 ? json.Substring(0, 200) + "..." : json);
                Log.Warning($"[AutoTranslationCore] JSON 解析失敗 (AI 回傳格式異常): {e.Message}\n異常 Payload: {preview}");

                return null;
            }
        }

        // 這個類別負責 ATCWeb回應 的主要流程與狀態。
        // EN: This class manages the main workflow and state for ATC_WebResponse.
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
        }

    }
}
