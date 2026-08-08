using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責翻譯 XML 的讀寫與輸出驗證。
// EN: This file reads, writes, and validates translation XML.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器掃描器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorScanner.
    public static partial class AutoTranslatorScanner
    {
        // 這個方法負責讀取 XmlFilesToDict 資料。
        // EN: This method loads XML files to dict.
        public static Dictionary<string, string> LoadXmlFilesToDict(string path, TargetLanguage? expectedLang = null)
        {
            var dict = new Dictionary<string, string>();
            if (!Directory.Exists(path)) return dict;
            foreach (var f in GetXmlFilesCached(path, SearchOption.AllDirectories))
            {
                var d = LoadXmlFileToDict(f, ShouldCheckFakeLanguageForFile(f, expectedLang) ? expectedLang : null);
                foreach (var p in d) dict[p.Key] = p.Value;
            }
            return dict;
        }

        // 這個方法負責讀取 XmlFileToDict 資料。
        // EN: This method loads XML file to dict.
        public static Dictionary<string, string> LoadXmlFileToDict(string filePath, TargetLanguage? expectedLang = null)
        {
            var dict = LoadRawXmlFileToDictCached(filePath);
            TargetLanguage placeholderLang = expectedLang ?? (AutoTranslatorMod.Settings != null ? AutoTranslatorMod.Settings.TargetLang : TargetLanguage.Traditional);

            var placeholderKeys = dict
                .Where(pair => LanguageDetector.LooksLikePlaceholderTranslation(pair.Value, placeholderLang))
                .Select(pair => pair.Key)
                .ToList();
            foreach (string key in placeholderKeys)
            {
                dict.Remove(key);
            }

            bool shouldCheckLanguage = ShouldCheckFakeLanguageForFile(filePath, expectedLang);
            if (shouldCheckLanguage && IsGeneratedTranslationOutputFile(filePath))
            {
                foreach (string key in dict.Keys.ToList())
                {
                    if (TranslationResultLanguagePolicy.TryNormalizePersistedGeneratedValue(
                            dict[key],
                            expectedLang.Value,
                            out string normalized))
                    {
                        dict[key] = normalized;
                    }
                    else
                    {
                        dict.Remove(key);
                    }
                }
            }
            else if (shouldCheckLanguage && LanguageDetector.IsFakeLanguage(dict, expectedLang.Value))
            {
                AutoTranslatorSettings.AddLog($"🕵️ [System] " + AutoTranslatorAPI.TranslateText("ATC_Log_FakeLanguageDetected", Path.GetFileName(filePath)));
                return new Dictionary<string, string>();
            }

            return dict;
        }

        // 這個方法負責保存 Xml 資料。
        // EN: This method saves XML.
        public static void SaveXml(string path, Dictionary<string, string> data)
        {
            XmlDocument d = new XmlDocument();
            XmlDeclaration dec = d.CreateXmlDeclaration("1.0", "utf-8", null);
            d.AppendChild(dec);
            XmlElement r = d.CreateElement("LanguageData");

            foreach (var p in data ?? new Dictionary<string, string>())
            {

                if (Regex.IsMatch(p.Value ?? "", @"^[\s\u200B\u200C\u200D\uFEFF\u00A0\x00-\x1F]*$"))
                    continue;


                if (string.IsNullOrWhiteSpace(p.Key) || !ValidXmlNameRegex.IsMatch(p.Key))
                {
                    AddValidationStat(s => s.XmlKeySkipped++);
                    AutoTranslatorSettings.AddErrorLog("⚠️ " + AutoTranslatorAPI.TranslateText("ATC_LogError_InvalidXmlKey", p.Key ?? "<null>"));
                    continue;
                }

                try
                {
                    XmlElement n = d.CreateElement(p.Key);
                    n.InnerText = p.Value;
                    r.AppendChild(n);
                }
                catch (XmlException ex)
                {

                    AddValidationStat(s => s.XmlKeySkipped++);
                    AutoTranslatorSettings.AddErrorLog("⚠️ " + AutoTranslatorAPI.TranslateText("ATC_LogError_InvalidXmlKey", $"{p.Key} ({ex.Message})"));
                    continue;
                }
            }

            d.AppendChild(r);
            SaveLanguageXmlDocumentAtomic(path, d);
            NotifyTranslationFileChanged(path);
        }

        private static void SaveLanguageXmlDocumentAtomic(string path, XmlDocument document)
        {
            if (document?.DocumentElement == null ||
                !document.DocumentElement.Name.Equals("LanguageData", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Translation XML must contain a LanguageData root element.");
            }

            TranslationXmlAtomicFileStore.Save(path, stream => document.Save(stream));
        }
        // 這個方法負責取得 Short路徑 資料。
        // EN: This method gets short path.
        private static string GetShortPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return "";
            string normalized = fullPath.Replace('\\', '/');
            int idx = normalized.IndexOf("294100/");
            if (idx == -1) idx = normalized.IndexOf("Mods/");
            return idx != -1 ? normalized.Substring(idx) : Path.GetFileName(fullPath);
        }

        private static bool ShouldCheckFakeLanguageForFile(string filePath, TargetLanguage? expectedLang)
        {
            if (!expectedLang.HasValue || string.IsNullOrEmpty(filePath)) return false;

            string targetFolder = GetFolderNameByLanguage(expectedLang.Value);
            string normalized = filePath.Replace('\\', '/');
            string[] parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i <= parts.Length - 4; i++)
            {
                if (!parts[i].Equals("Languages", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsLanguageFolderMatch(parts[i + 1], targetFolder)) continue;

                string bucket = parts[i + 2];
                if (bucket.Equals("Keyed", StringComparison.OrdinalIgnoreCase) ||
                    bucket.Equals("DefInjected", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsGeneratedTranslationOutputFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;

            string fileName = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
            return fileName.EndsWith("_AutoTranslated", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("AutoTranslated_Defs", StringComparison.OrdinalIgnoreCase);
        }
    }
}
