using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Verse;
// 這個檔案負責修補舊版翻譯與遺留資料。
// EN: This file repairs legacy translation pack data.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器舊資料修復器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for TranslationFileRepairService.
    public static class TranslationFileRepairService
    {
        private static readonly Regex ProtectedTokenRegex = new Regex(@"(\{[^{}\r\n]+\}|\[[^\[\]\r\n]+\])", RegexOptions.Compiled);
        private static readonly Dictionary<string, LegacySourceCache> SourceCache = new Dictionary<string, LegacySourceCache>(StringComparer.OrdinalIgnoreCase);
        private static readonly object RepairLock = new object();

        // 這個類別負責 RepairSummary 的主要流程與狀態。
        // EN: This class manages the main workflow and state for RepairSummary.
        public class RepairSummary
        {
            // 這個欄位保存 FilesTouched 的執行狀態或快取資料。
            // EN: This field stores files touched runtime state or cached data.
            public int FilesTouched;
            // 這個欄位保存 EntriesFixed 的執行狀態或快取資料。
            // EN: This field stores entries fixed runtime state or cached data.
            public int EntriesFixed;
            // 這個欄位保存 TokenFixes 的執行狀態或快取資料。
            // EN: This field stores token fixes runtime state or cached data.
            public int TokenFixes;
            // 這個欄位保存 規則PrefixFixes 的執行狀態或快取資料。
            // EN: This field stores rule prefix fixes runtime state or cached data.
            public int RulePrefixFixes;
            // 這個欄位保存 StructureWarnings 的執行狀態或快取資料。
            // EN: This field stores structure warnings runtime state or cached data.
            public int StructureWarnings;

            // 這個屬性提供 HasChanges 的讀寫或計算結果。
            // EN: This property exposes has changes.
            public bool HasChanges => FilesTouched > 0 || EntriesFixed > 0 || StructureWarnings > 0;
        }

        // 這個類別負責 舊資料Source快取 的主要流程與狀態。
        // EN: This class manages the main workflow and state for LegacySourceCache.
        private class LegacySourceCache
        {
            public readonly Dictionary<string, Dictionary<string, string>> KeyedByFile = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Dictionary<string, string>> DefByType = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }


        // 這個方法負責處理 RepairCurrent語言翻譯包 相關流程。
        // EN: This method handles repair current language pack.
        public static RepairSummary RepairCurrentLanguagePack(bool requestMemoryDrop = true)
        {
            if (AutoTranslatorMod.Settings == null) return new RepairSummary();
            TargetLanguage targetLang = AutoTranslatorMod.Settings.TargetLang;
            string targetFolder = AutoTranslatorScanner.GetFolderNameByLanguage(targetLang);
            return RepairLanguagePack(targetFolder, null, requestMemoryDrop);
        }

        // 這個方法負責處理 RepairPackage 相關流程。
        // EN: This method handles repair package.
        public static RepairSummary RepairPackage(string packageId, string targetLangFolder, bool requestMemoryDrop = true)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return new RepairSummary();

            if (string.IsNullOrWhiteSpace(targetLangFolder))
            {
                if (AutoTranslatorMod.Settings == null) return new RepairSummary();
                targetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
            }

            return RepairLanguagePack(targetLangFolder, new HashSet<string>(new[] { packageId }, StringComparer.OrdinalIgnoreCase), requestMemoryDrop);
        }

        // 這個方法負責處理 RepairPackages 相關流程。
        // EN: This method handles repair packages.
        public static RepairSummary RepairPackages(IEnumerable<string> packageIds, string targetLangFolder, bool requestMemoryDrop = true)
        {
            var filters = new HashSet<string>((packageIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
            if (filters.Count == 0) return new RepairSummary();

            if (string.IsNullOrWhiteSpace(targetLangFolder))
            {
                if (AutoTranslatorMod.Settings == null) return new RepairSummary();
                targetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
            }

            return RepairLanguagePack(targetLangFolder, filters, requestMemoryDrop);
        }

        // 這個方法負責處理 Repair語言翻譯包 相關流程。
        // EN: This method handles repair language pack.
        private static RepairSummary RepairLanguagePack(string targetFolder, HashSet<string> packageIdFilters, bool requestMemoryDrop)
        {
            var summary = new RepairSummary();

            try
            {
                if (string.IsNullOrWhiteSpace(targetFolder)) return summary;
                string packLangDir = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Languages", targetFolder);
                if (!Directory.Exists(packLangDir)) return summary;

                lock (RepairLock)
                {
                    try
                    {
                        SourceCache.Clear();

                        RepairKeyedDirectory(Path.Combine(packLangDir, "Keyed"), summary, packageIdFilters);
                        RepairKeyedDirectory(Path.Combine(packLangDir, "keyed"), summary, packageIdFilters);
                        RepairDefInjectedDirectory(Path.Combine(packLangDir, "DefInjected"), summary, packageIdFilters);

                        if (summary.HasChanges)
                        {
                            string scope = packageIdFilters == null || packageIdFilters.Count == 0 ? "pack" : $"{packageIdFilters.Count} package(s)";
                            AutoTranslatorSettings.AddLog($"[Legacy Repair] fixed {summary.EntriesFixed} entries in {summary.FilesTouched} files for {scope}. Tokens:{summary.TokenFixes}, rules:{summary.RulePrefixFixes}, warnings:{summary.StructureWarnings}");
                            if (requestMemoryDrop && summary.FilesTouched > 0)
                            {
                                AutoTranslatorScanner.RequestMemoryDrop();
                            }
                        }
                    }
                    finally
                    {
                        SourceCache.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Legacy repair failed: {ex.Message}");
            }

            return summary;
        }

        // 這個方法負責處理 RepairKeyedDirectory 相關流程。
        // EN: This method handles repair Keyed directory.
        private static void RepairKeyedDirectory(string keyedDir, RepairSummary summary, HashSet<string> packageIdFilters = null)
        {
            if (!Directory.Exists(keyedDir)) return;

            foreach (string file in Directory.GetFiles(keyedDir, "*.xml", SearchOption.AllDirectories))
            {
                string packageId = ResolvePackageIdFromGeneratedFile(file);
                if (string.IsNullOrEmpty(packageId)) continue;
                if (!PackageMatchesFilter(packageId, packageIdFilters)) continue;

                LegacySourceCache cache = GetSourceCache(packageId);
                if (cache == null) continue;

                string sourceFileName = GetSourceFileNameFromGeneratedFile(file, packageId);
                if (string.IsNullOrEmpty(sourceFileName)) continue;

                if (!cache.KeyedByFile.TryGetValue(sourceFileName, out Dictionary<string, string> sourceDict))
                {
                    continue;
                }

                RepairLanguageDataFile(file, sourceDict, summary);
            }
        }

        // 這個方法負責處理 RepairDefInjectedDirectory 相關流程。
        // EN: This method handles repair Def Injected directory.
        private static void RepairDefInjectedDirectory(string defInjectedDir, RepairSummary summary, HashSet<string> packageIdFilters = null)
        {
            if (!Directory.Exists(defInjectedDir)) return;

            foreach (string file in Directory.GetFiles(defInjectedDir, "*.xml", SearchOption.AllDirectories))
            {
                string packageId = ResolvePackageIdFromGeneratedFile(file);
                if (string.IsNullOrEmpty(packageId)) continue;
                if (!PackageMatchesFilter(packageId, packageIdFilters)) continue;

                LegacySourceCache cache = GetSourceCache(packageId);
                if (cache == null) continue;

                string defType = GetDefTypeFromPackFile(file, defInjectedDir);
                if (string.IsNullOrEmpty(defType)) defType = "General";

                if (!cache.DefByType.TryGetValue(defType, out Dictionary<string, string> sourceDict))
                {
                    continue;
                }

                RepairLanguageDataFile(file, sourceDict, summary);
            }
        }

        // 這個方法負責處理 PackageMatchesFilter 相關流程。
        // EN: This method handles package matches filter.
        private static bool PackageMatchesFilter(string packageId, HashSet<string> packageIdFilters)
        {
            return packageIdFilters == null || packageIdFilters.Count == 0 || packageIdFilters.Contains(packageId);
        }

        // 這個方法負責處理 Repair語言資料File 相關流程。
        // EN: This method handles repair language data file.
        private static void RepairLanguageDataFile(string file, Dictionary<string, string> sourceDict, RepairSummary summary)
        {
            XmlDocument doc = new XmlDocument();
            try
            {
                doc.Load(file);
            }
            catch
            {
                return;
            }

            if (doc.DocumentElement == null || doc.DocumentElement.Name != "LanguageData") return;

            bool fileChanged = false;

            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (!sourceDict.TryGetValue(node.Name, out string original)) continue;

                string repaired = RepairValue(node.InnerText, original, summary);
                if (repaired != node.InnerText)
                {
                    node.InnerText = repaired;
                    summary.EntriesFixed++;
                    fileChanged = true;
                }
            }

            if (fileChanged)
            {
                doc.Save(file);
                summary.FilesTouched++;
            }
        }

        // 這個方法負責處理 RepairValue 相關流程。
        // EN: This method handles repair value.
        private static string RepairValue(string translated, string original, RepairSummary summary)
        {
            if (string.IsNullOrEmpty(translated) || string.IsNullOrEmpty(original)) return translated;

            string result = translated;
            result = RestoreGrammarRulePrefix(result, original, summary);
            result = RestoreProtectedTokens(result, original, summary);

            if (IsStructureSensitiveText(original) && MissingProtectedToken(result, original))
            {
                summary.StructureWarnings++;
            }

            return result;
        }

        // 這個方法負責處理 RestoreGrammar規則Prefix 相關流程。
        // EN: This method handles restore grammar rule prefix.
        private static string RestoreGrammarRulePrefix(string translated, string original, RepairSummary summary)
        {
            int originalArrow = original.IndexOf("->", StringComparison.Ordinal);
            if (originalArrow < 0) return translated;

            string originalPrefix = original.Substring(0, originalArrow + 2);
            int translatedArrow = translated.IndexOf("->", StringComparison.Ordinal);

            if (translatedArrow >= 0)
            {
                string translatedRight = translated.Substring(translatedArrow + 2).TrimStart();
                string repaired = originalPrefix + translatedRight;
                if (repaired != translated) summary.RulePrefixFixes++;
                return repaired;
            }

            summary.RulePrefixFixes++;
            return originalPrefix + translated.TrimStart();
        }

        // 這個方法負責處理 RestoreProtectedTokens 相關流程。
        // EN: This method handles restore protected tokens.
        private static string RestoreProtectedTokens(string translated, string original, RepairSummary summary)
        {
            string result = translated;
            foreach (string token in GetProtectedTokens(original))
            {
                if (result.Contains(token)) continue;

                string inner = token.Substring(1, token.Length - 2);
                string[] alternatives = token[0] == '{'
                    ? new[] { "[" + inner + "]", "【" + inner + "】", "［" + inner + "］", "(" + inner + ")", "（" + inner + "）" }
                    : new[] { "{" + inner + "}", "【" + inner + "】", "［" + inner + "］", "(" + inner + ")", "（" + inner + "）" };

                foreach (string alt in alternatives)
                {
                    if (!result.Contains(alt)) continue;

                    result = result.Replace(alt, token);
                    summary.TokenFixes++;
                    break;
                }
            }

            return result;
        }

        // 這個方法負責處理 MissingProtectedToken 相關流程。
        // EN: This method handles missing protected token.
        private static bool MissingProtectedToken(string translated, string original)
        {
            foreach (string token in GetProtectedTokens(original))
            {
                if (!translated.Contains(token)) return true;
            }
            return false;
        }

        // 這個方法負責取得 ProtectedTokens 資料。
        // EN: This method gets protected tokens.
        private static List<string> GetProtectedTokens(string original)
        {
            return ProtectedTokenRegex.Matches(original)
                .Cast<Match>()
                .Select(m => m.Value)
                .Distinct()
                .ToList();
        }

        // 這個方法負責判斷 IsStructureSensitiveText 條件是否成立。
        // EN: This method checks is structure sensitive text.
        private static bool IsStructureSensitiveText(string original)
        {
            return original.Contains("->") ||
                   original.IndexOf("[INITIATOR_", StringComparison.Ordinal) >= 0 ||
                   original.IndexOf("[RECIPIENT_", StringComparison.Ordinal) >= 0 ||
                   original.IndexOf("{PAWN", StringComparison.Ordinal) >= 0 ||
                   original.IndexOf("[PAWN_", StringComparison.Ordinal) >= 0;
        }

        // 這個方法負責取得 Source快取 資料。
        // EN: This method gets source cache.
        private static LegacySourceCache GetSourceCache(string packageId)
        {
            if (SourceCache.TryGetValue(packageId, out LegacySourceCache cache)) return cache;

            ModMetaData mod = ModLister.AllInstalledMods.FirstOrDefault(m => string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
            if (mod == null)
            {
                SourceCache[packageId] = null;
                return null;
            }

            cache = new LegacySourceCache();

            foreach (string langRoot in AutoTranslatorScanner.GetAllEffectiveLangPaths(mod))
            {
                string englishKeyed = Path.Combine(langRoot, "English", "Keyed");
                LoadKeyedSources(englishKeyed, cache.KeyedByFile);

                string englishKeyedLower = Path.Combine(langRoot, "English", "keyed");
                LoadKeyedSources(englishKeyedLower, cache.KeyedByFile);

                LoadDefInjectedSources(Path.Combine(langRoot, "English", "DefInjected"), cache.DefByType);
            }

            foreach (string defsRoot in AutoTranslatorScanner.GetAllEffectiveDefsPaths(mod))
            {
                var rawDefs = AutoTranslatorScanner.ExtractEnglishFromRawDefs(defsRoot);
                foreach (var defTypePair in rawDefs)
                {
                    if (!cache.DefByType.TryGetValue(defTypePair.Key, out Dictionary<string, string> dict))
                    {
                        dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        cache.DefByType[defTypePair.Key] = dict;
                    }

                    foreach (var valuePair in defTypePair.Value)
                    {
                        dict[valuePair.Key] = valuePair.Value;
                    }
                }
            }

            SourceCache[packageId] = cache;
            return cache;
        }

        // 這個方法負責讀取 KeyedSources 資料。
        // EN: This method loads Keyed sources.
        private static void LoadKeyedSources(string dir, Dictionary<string, Dictionary<string, string>> keyedByFile)
        {
            if (!Directory.Exists(dir)) return;

            foreach (string file in Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories))
            {
                Dictionary<string, string> dict = AutoTranslatorScanner.LoadXmlFileToDict(file);
                if (dict.Count <= 0) continue;
                keyedByFile[Path.GetFileName(file)] = dict;
            }
        }

        // 這個方法負責讀取 DefInjectedSources 資料。
        // EN: This method loads Def Injected sources.
        private static void LoadDefInjectedSources(string dir, Dictionary<string, Dictionary<string, string>> defByType)
        {
            if (!Directory.Exists(dir)) return;

            foreach (string typeDir in Directory.GetDirectories(dir))
            {
                string defType = Path.GetFileName(typeDir);
                if (!defByType.TryGetValue(defType, out Dictionary<string, string> dict))
                {
                    dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    defByType[defType] = dict;
                }

                foreach (string file in Directory.GetFiles(typeDir, "*.xml", SearchOption.AllDirectories))
                {
                    Dictionary<string, string> fileDict = AutoTranslatorScanner.LoadXmlFileToDict(file);
                    foreach (var pair in fileDict)
                    {
                        dict[pair.Key] = pair.Value;
                    }
                }
            }

            foreach (string file in Directory.GetFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
            {
                if (!defByType.TryGetValue("General", out Dictionary<string, string> dict))
                {
                    dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    defByType["General"] = dict;
                }

                Dictionary<string, string> fileDict = AutoTranslatorScanner.LoadXmlFileToDict(file);
                foreach (var pair in fileDict)
                {
                    dict[pair.Key] = pair.Value;
                }
            }
        }

        // 這個方法負責處理 ResolvePackageIdFromGeneratedFile 相關流程。
        // EN: This method handles resolve package id from generated file.
        private static string ResolvePackageIdFromGeneratedFile(string file)
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(fileName)) return null;

            foreach (ModMetaData mod in ModLister.AllInstalledMods)
            {
                string dotId = mod.PackageId.ToLowerInvariant();
                string underscoreId = dotId.Replace(".", "_");
                string lowerName = fileName.ToLowerInvariant();

                if (lowerName == dotId || lowerName == underscoreId ||
                    lowerName.StartsWith(dotId + ".", StringComparison.Ordinal) ||
                    lowerName.StartsWith(dotId + "_", StringComparison.Ordinal) ||
                    lowerName.StartsWith(underscoreId + "_", StringComparison.Ordinal) ||
                    lowerName.StartsWith(underscoreId + ".", StringComparison.Ordinal))
                {
                    return mod.PackageId;
                }
            }

            return null;
        }

        // 這個方法負責取得 SourceFile名稱FromGeneratedFile 資料。
        // EN: This method gets source file name from generated file.
        private static string GetSourceFileNameFromGeneratedFile(string file, string packageId)
        {
            string fileName = Path.GetFileName(file);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);

            string[] prefixes =
            {
                packageId.ToLowerInvariant() + "_",
                packageId.ToLowerInvariant() + ".",
                packageId.Replace(".", "_").ToLowerInvariant() + "_",
                packageId.Replace(".", "_").ToLowerInvariant() + "."
            };

            foreach (string prefix in prefixes)
            {
                if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return stem.Substring(prefix.Length) + extension;
                }
            }

            if (stem.EndsWith("_AutoTranslated", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return fileName;
        }

        // 這個方法負責取得 DefTypeFrom翻譯包File 資料。
        // EN: This method gets Def type from pack file.
        private static string GetDefTypeFromPackFile(string file, string defInjectedDir)
        {
            string relative = file.Substring(defInjectedDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[0] : "General";
        }
    }
}
