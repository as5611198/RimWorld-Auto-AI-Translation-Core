using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責 DeepL 語言對應。
// EN: This file maps target languages to DeepL language codes.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器API 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorAPI.
    public static partial class AutoTranslatorAPI
    {


        // 這個方法負責對應 ToDeepLLangCode 資料。
        // EN: This method maps to deep l language code.
        private static string MapToDeepLLangCode(TargetLanguage lang)
        {
            switch (lang)
            {
                case TargetLanguage.Traditional: return "ZH-HANT";
                case TargetLanguage.Simplified: return "ZH-HANS";
                case TargetLanguage.Japanese: return "JA";
                case TargetLanguage.Korean: return "KO";
                case TargetLanguage.Russian: return "RU";
                case TargetLanguage.English: return "EN-US";
                case TargetLanguage.Ukrainian: return null;

                case TargetLanguage.French: return "FR";
                case TargetLanguage.German: return "DE";
                case TargetLanguage.Spanish: return "ES";
                case TargetLanguage.Italian: return "IT";
                case TargetLanguage.Polish: return "PL";
                case TargetLanguage.Portuguese: return "PT-BR";
                case TargetLanguage.Turkish: return "TR";
                case TargetLanguage.Thai: return null;
                default: return null;
            }
        }
    }
}
