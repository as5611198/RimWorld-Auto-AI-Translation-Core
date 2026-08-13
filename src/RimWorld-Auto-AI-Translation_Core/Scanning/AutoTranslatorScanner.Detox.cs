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
            try
            {
                string packPath = GetLocalPackPath();
                string langsPath = Path.Combine(packPath, "Languages");
                string markerFile = Path.Combine(packPath, "V4.7_Cleaned.marker");


                if (File.Exists(markerFile)) return;

                if (Directory.Exists(langsPath))
                {
                    List<string> allXmlFiles = GetXmlFilesCached(langsPath, SearchOption.AllDirectories);
                    if (allXmlFiles.Count > 0)
                    {

                        Directory.Delete(langsPath, true);
                        NotifyTranslationFilesChanged(langsPath);


                        AutoTranslatorSettings.AddErrorLog("🚨 " + "ATC_LogError_OldToxicFiles".Translate());
                        AutoTranslatorSettings.AddLog("🗑️ " + "ATC_Log_OldFilesDeleted".Translate());
                        AutoTranslatorSettings.AddLog("🚀 " + "ATC_Log_AutoRebuildStart".Translate());
                    }
                }


                Directory.CreateDirectory(packPath);
                File.WriteAllText(markerFile, "This marker prevents V4.7 from deleting the cleaned translation pack.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Delete failed (請手動刪除 !Translation_AI_Pack/Languages): {ex.Message}");
            }
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
            try
            {
                string packPath = GetLocalPackPath();
                string langsPath = Path.Combine(packPath, "Languages");
                if (!Directory.Exists(langsPath)) return;

                var xmlFiles = GetXmlFilesCached(langsPath, SearchOption.AllDirectories);
                int fixedFiles = 0;
                int removedTags = 0;


                Regex badTagRegex = new Regex(@"(?m)^\s*<([a-zA-Z0-9_\-\.]+)>[\s\u200B\u200C\u200D\uFEFF\u00A0\x00-\x1F]*<\/\1>\s*$|(?m)^\s*<[a-zA-Z0-9_\-\.]+\s*\/>\s*$", RegexOptions.Multiline);

                foreach (var file in xmlFiles)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(file) || !File.Exists(file))
                        {
                            NotifyTranslationFileChanged(file);
                            continue;
                        }

                        string content = File.ReadAllText(file);
                        if (badTagRegex.IsMatch(content))
                        {

                            MatchCollection matches = badTagRegex.Matches(content);
                            foreach (Match match in matches)
                            {
                                Log.Warning($"[AutoTranslationCore] ⚠️ 抓到劇毒標籤，準備物理切除：{match.Value.Trim()} (來自檔案: {Path.GetFileName(file)})");
                            }

                            int matchCount = matches.Count;

                            string cleanContent = badTagRegex.Replace(content, "");

                            cleanContent = Regex.Replace(cleanContent, @"^\s+$[\r\n]*", "", RegexOptions.Multiline);

                            File.WriteAllText(file, cleanContent);
                            NotifyTranslationFileChanged(file);
                            fixedFiles++;
                            removedTags += matchCount;
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        NotifyTranslationFileChanged(file);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        NotifyTranslationFileChanged(file);
                    }
                }
                if (fixedFiles > 0)
                {


                    string logMsg = "ATC_Log_DetoxSuccess".CanTranslate()
                        ? AutoTranslatorAPI.TranslateText("ATC_Log_DetoxSuccess", fixedFiles, removedTags)
                        : $"[排毒系統] 修復了 {fixedFiles} 個檔案，切除 {removedTags} 個劇毒標籤！";

                    AutoTranslatorSettings.AddLog($"🛡️ {logMsg}");
                    Log.Message($"[AutoTranslationCore] Detox complete: {fixedFiles} files fixed, {removedTags} tags removed.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] 排毒系統異常: {ex.Message}");
            }

        }


        // 這個方法負責處理 RunAdvancedDetox掃描器 相關流程。
        // EN: This method handles run advanced detox scanner.
        public static void RunAdvancedDetoxScanner()
        {
            try
            {
                string packPath = GetLocalPackPath();
                string langsPath = Path.Combine(packPath, "Languages");
                if (!Directory.Exists(langsPath)) return;

                var xmlFiles = GetXmlFilesCached(langsPath, SearchOption.AllDirectories);
                int fixedFiles = 0;
                int removedTags = 0;

                foreach (var file in xmlFiles)
                {
                    if (string.IsNullOrEmpty(file) || !File.Exists(file))
                    {
                        NotifyTranslationFileChanged(file);
                        continue;
                    }


                    string defInjectedMarker = Path.DirectorySeparatorChar + "DefInjected" + Path.DirectorySeparatorChar;
                    bool isDefInjectedFile = file.IndexOf(defInjectedMarker, StringComparison.OrdinalIgnoreCase) >= 0;
                    string dirName = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(file)).Name.ToLower();
                    if (isDefInjectedFile &&
                        (dirName.Contains("facedef") || dirName.Contains("eyedef") || dirName.Contains("browdef") ||
                        dirName.Contains("liddef") || dirName.Contains("lashdef") || dirName.Contains("mouthdef") ||
                        dirName.Contains("nosedef") || dirName.Contains("eardef") || dirName.Contains("skindef") ||
                        dirName.Contains("facialanimation")))
                    {

                        System.IO.File.SetAttributes(file, System.IO.FileAttributes.Normal);
                        System.IO.File.Delete(file);
                        NotifyTranslationFileChanged(file);
                        fixedFiles++;
                        continue;
                    }

                    XmlDocument doc = new XmlDocument();
                    try { doc.Load(file); } catch { continue; }

                    if (doc.DocumentElement == null || doc.DocumentElement.Name != "LanguageData") continue;

                    List<XmlNode> nodesToKill = new List<XmlNode>();


                    foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                    {
                        if (node.NodeType != XmlNodeType.Element) continue;

                        string tagName = node.Name;
                        bool shouldKill = false;

                        if (isDefInjectedFile && IsUnsafeRuntimeDefInjectionPath(tagName))
                        {
                            shouldKill = true;
                        }

                        if (!shouldKill)
                        {
                            foreach (var badField in BlacklistedFields)
                            {

                                if (tagName.EndsWith("." + badField, StringComparison.OrdinalIgnoreCase) ||
                                    tagName.Equals(badField, StringComparison.OrdinalIgnoreCase))
                                {
                                    shouldKill = true;
                                    break;
                                }
                            }
                        }

                        if (shouldKill)
                        {
                            nodesToKill.Add(node);
                        }
                    }


                    if (nodesToKill.Count > 0)
                    {
                        foreach (var node in nodesToKill)
                        {
                            doc.DocumentElement.RemoveChild(node);
                            removedTags++;
                        }
                        SaveLanguageXmlDocumentAtomic(file, doc);
                        NotifyTranslationFileChanged(file);
                        fixedFiles++;
                    }
                }

                if (fixedFiles > 0)
                {

                    AutoTranslatorSettings.AddLog($"🩺 [ATC System] " + AutoTranslatorAPI.TranslateText("ATC_Log_AdvancedDetoxSuccess", removedTags, fixedFiles));
                    Log.Message($"[AutoTranslationCore] Advanced Detox complete: Removed {removedTags} bad tags across {fixedFiles} files, saving player's API costs!");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] 高級排毒系統異常: {ex.Message}");
            }
        }


        // 這個方法負責處理 RunNewlineDetox掃描器 相關流程。
        // EN: This method handles run newline detox scanner.
        public static void RunNewlineDetoxScanner()
        {
            try
            {
                string packPath = GetLocalPackPath();
                string langsPath = Path.Combine(packPath, "Languages");
                if (!Directory.Exists(langsPath)) return;

                var xmlFiles = GetXmlFilesCached(langsPath, SearchOption.AllDirectories);
                int fixedFiles = 0;

                foreach (var file in xmlFiles)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(file) || !File.Exists(file))
                        {
                            NotifyTranslationFileChanged(file);
                            continue;
                        }

                        string content = File.ReadAllText(file);


                        bool changed = false;
                        string repairedContent = RepairBrokenXmlClosingTags(content);
                        if (!string.Equals(repairedContent, content, StringComparison.Ordinal))
                        {
                            content = repairedContent;
                            changed = true;
                        }

                        if (content.Contains("\\n") || content.Contains("\\r"))
                        {

                            content = content.Replace("\\n", "\n").Replace("\\r", "\r");
                            changed = true;
                        }

                        if (changed)
                        {
                            File.WriteAllText(file, content);
                            NotifyTranslationFileChanged(file);
                            fixedFiles++;
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        NotifyTranslationFileChanged(file);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        NotifyTranslationFileChanged(file);
                    }
                }

                if (fixedFiles > 0)
                {

                    NotifyTranslationFilesChanged(langsPath);
                    RequestKeyedMemoryDrop();
                    AutoTranslatorSettings.AddLog($"✨ [ATC System] 歷史業障超渡成功！共修復了 {fixedFiles} 個帶有換行 Bug 的舊檔案！");
                    Log.Message($"[AutoTranslationCore] Newline Detox complete: Fixed {fixedFiles} legacy files.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] 換行排毒系統異常: {ex.Message}");
            }
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
