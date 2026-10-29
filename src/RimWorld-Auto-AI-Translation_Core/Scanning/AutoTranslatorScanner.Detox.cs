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
// 這個檔案負責危險標籤清理與舊檔排毒。
// EN: This file cleans dangerous XML tags and repairs legacy translation files.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器掃描器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorScanner.
    public static partial class AutoTranslatorScanner
    {


        // 這個方法負責套用 EmergencyHotfix 設定。
        // EN: This method applies emergency hotfix.
        public static void ApplyEmergencyHotfix()
        {
            // Legacy destructive detox is replaced by reversible structural repair.
            // Retain every paid value and original XML; no blanket delete/rebuild.
            RepairOwnedTranslationDirectory(Path.Combine(GetLocalPackPath(), "Languages",
                AutoTranslatorMod.Settings == null ? "ChineseTraditional" : GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang)));
        }

        // 這個方法負責處理 RunDetox掃描器 相關流程。
        // EN: This method handles run detox scanner.
        private static void ApplyOfficialDlcKeyedHotfix()
        {
            try
            {
                string packPath = GetLocalPackPath();
                string languagesPath = Path.Combine(packPath, "Languages");
                Directory.CreateDirectory(languagesPath);

                ApplyOfficialDlcKeyedHotfixForLanguage(
                    Path.Combine(languagesPath, "ChineseTraditional", "Keyed", "auto_translation_core_official_dlc_hotfix.xml"),
                    new Dictionary<string, string>
                    {
                        { "MessageTransmutedStuff", "{PAWN_nameDef}\u5DF2\u5C07{1}\u8F49\u5316\u70BA{TRANSMUTED_labelShortIndef}\u3002" },
                        { "MessageTransmutedStuffPlural", "{PAWN_nameDef}\u5DF2\u5C07{1}\u8F49\u5316\u70BA{TRANSMUTED_labelPluralIndef}\u3002" },
                        { "ConfirmSealHatch", "\u5C01\u9589\u53E4\u8001\u5132\u85CF\u5340\u5165\u53E3\u5F8C\u5C07\u7121\u6CD5\u5FA9\u539F\u3002\u4EFB\u4F55\u7559\u5728\u4E0B\u65B9\u7684\u7269\u54C1\u6216\u4EBA\u90FD\u6703\u6C38\u9060\u5931\u53BB\u3002{0}\\n\\n\u4F60\u78BA\u5B9A\u8981\u7E7C\u7E8C\u55CE\uFF1F" }
                    });

                ApplyOfficialDlcKeyedHotfixForLanguage(
                    Path.Combine(languagesPath, "ChineseSimplified", "Keyed", "auto_translation_core_official_dlc_hotfix.xml"),
                    new Dictionary<string, string>
                    {
                        { "MessageTransmutedStuff", "{PAWN_nameDef}\u5DF2\u5C06{1}\u8F6C\u5316\u4E3A{TRANSMUTED_labelShortIndef}\u3002" },
                        { "MessageTransmutedStuffPlural", "{PAWN_nameDef}\u5DF2\u5C06{1}\u8F6C\u5316\u4E3A{TRANSMUTED_labelPluralIndef}\u3002" },
                        { "ConfirmSealHatch", "\u5C01\u95ED\u53E4\u8001\u50A8\u85CF\u533A\u5165\u53E3\u540E\u5C06\u65E0\u6CD5\u590D\u539F\u3002\u4EFB\u4F55\u7559\u5728\u4E0B\u65B9\u7684\u7269\u54C1\u6216\u4EBA\u90FD\u4F1A\u6C38\u8FDC\u5931\u53BB\u3002{0}\\n\\n\u4F60\u786E\u5B9A\u8981\u7EE7\u7EED\u5417\uFF1F" }
                    });
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Official DLC keyed hotfix failed: {ex.Message}");
            }
        }

        private static void ApplyBuiltInKeyedHotfix()
        {
            try
            {
                string packPath = GetLocalPackPath();
                string languagesPath = Path.Combine(packPath, "Languages");
                Directory.CreateDirectory(languagesPath);

                ApplyOfficialDlcKeyedHotfixForLanguage(
                    Path.Combine(languagesPath, "ChineseTraditional", "Keyed", "auto_translation_core_builtin_keyed_hotfix.xml"),
                    new Dictionary<string, string>
                    {
                        { "RS_Mod", "Rimstro" },
                        { "RS_TestLog", "測試記錄" },
                        { "RS_ResetAllSetting", "重置所有設定" }
                    });

                ApplyOfficialDlcKeyedHotfixForLanguage(
                    Path.Combine(languagesPath, "ChineseSimplified", "Keyed", "auto_translation_core_builtin_keyed_hotfix.xml"),
                    new Dictionary<string, string>
                    {
                        { "RS_Mod", "Rimstro" },
                        { "RS_TestLog", "测试日志" },
                        { "RS_ResetAllSetting", "重置所有设置" }
                    });
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Built-in keyed hotfix failed: {ex.Message}");
            }
        }

        public static void ApplyStartupKeyedHotfixes()
        {
            try
            {
                EnsurePackSkeleton();
                ApplyBuiltInKeyedHotfix();
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Startup keyed hotfix failed: {ex.Message}");
            }
        }

        private static void ApplyOfficialDlcKeyedHotfixForLanguage(string filePath, Dictionary<string, string> entries)
        {
            if (string.IsNullOrEmpty(filePath) || entries == null || entries.Count == 0) return;

            var data = LoadXmlFileToDict(filePath);
            bool changed = false;

            foreach (var pair in entries)
            {
                if (data.TryGetValue(pair.Key, out string existing) && string.Equals(existing, pair.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                data[pair.Key] = pair.Value;
                changed = true;
            }

            if (changed) SaveXml(filePath, data);
        }

        public static void RunDetoxScanner()
            {
            // Legacy destructive detox is replaced by reversible structural repair.
            // Retain every paid value and original XML; no blanket delete/rebuild.
            RepairOwnedTranslationDirectory(Path.Combine(GetLocalPackPath(), "Languages",
                AutoTranslatorMod.Settings == null ? "ChineseTraditional" : GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang)));
        }


        // 這個方法負責處理 RunAdvancedDetox掃描器 相關流程。
        // EN: This method handles run advanced detox scanner.
        public static void RunAdvancedDetoxScanner()
        {
            // Legacy destructive detox is replaced by reversible structural repair.
            // Retain every paid value and original XML; no blanket delete/rebuild.
            RepairOwnedTranslationDirectory(Path.Combine(GetLocalPackPath(), "Languages",
                AutoTranslatorMod.Settings == null ? "ChineseTraditional" : GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang)));
        }


        // 這個方法負責處理 RunNewlineDetox掃描器 相關流程。
        // EN: This method handles run newline detox scanner.
        public static void RunNewlineDetoxScanner()
        {
            // Legacy destructive detox is replaced by reversible structural repair.
            // Retain every paid value and original XML; no blanket delete/rebuild.
            RepairOwnedTranslationDirectory(Path.Combine(GetLocalPackPath(), "Languages",
                AutoTranslatorMod.Settings == null ? "ChineseTraditional" : GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang)));
        }

        private static string RepairBrokenXmlClosingTags(string content)
        {
            if (string.IsNullOrEmpty(content) ||
                (content.IndexOf("<\n", StringComparison.Ordinal) < 0 &&
                 content.IndexOf("<\r", StringComparison.Ordinal) < 0))
            {
                return content;
            }

            return Regex.Replace(
                content,
                @"<(?<key>[A-Za-z_][A-Za-z0-9_.\-]*)>(?<value>[^<]*[^\s<][^<]*)<\s*(?<close>[A-Za-z_][A-Za-z0-9_.\-]*)>",
                match =>
                {
                    string key = match.Groups["key"].Value;
                    string close = match.Groups["close"].Value;
                    bool sameKey = string.Equals(key, close, StringComparison.Ordinal);
                    bool slashNCorruptedKey =
                        key.Length > 1 &&
                        string.Equals(key.Substring(1), close, StringComparison.Ordinal);
                    if (!sameKey && !slashNCorruptedKey) return match.Value;

                    return $"<{key}>{match.Groups["value"].Value}</{key}>";
                },
                RegexOptions.Singleline);
        }

    }
}
