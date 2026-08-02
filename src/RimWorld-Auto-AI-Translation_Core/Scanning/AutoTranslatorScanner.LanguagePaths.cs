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
// 這個檔案負責解析各版本 RimWorld 的語言路徑。
// EN: This file resolves language paths across supported RimWorld versions.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器掃描器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorScanner.
    public static partial class AutoTranslatorScanner
    {

        // 這個方法負責判斷 Is翻譯補丁模組 條件是否成立。
        // EN: This method checks is translation patch mod.
        public static bool IsTranslationPatchMod(ModMetaData mod)
        {
            if (mod == null || string.IsNullOrEmpty(mod.PackageId)) return false;
            string pid = mod.PackageId.ToLowerInvariant();
            string name = (mod.Name ?? "").ToLowerInvariant();

            if (pid.Contains("chinesepack") ||
                pid.Contains("chinese-pack") ||
                pid.StartsWith("rwzh.") ||
                name.Contains("zh-pack") ||
                name.Contains("chinese pack") ||
                name.Contains("chinesepack"))
            {
                return true;
            }


            string[] patchKeywords = { "漢化", "汉化", "翻譯", "翻译", "translation", "language", "l10n", "中文", "zh-tw", "zh-cn", "簡繁", "简繁", "繁簡", "繁简" };
            foreach (var kw in patchKeywords)
            {
                if (name.Contains(kw)) return true;
            }


            string[] pidSuffixes = { ".zh", "_zh", "-zh", "zh-pack", ".zhtc", "_zhtc", "-zhtc", ".zhcn", "_zhcn", "-zhcn", ".cn", "_cn", "-cn", ".tw", "_tw", "-tw", "l10n" };
            foreach (var suf in pidSuffixes)
            {

                if (pid.EndsWith(suf) || pid.Contains(suf + ".") || pid.Contains(suf + "_")) return true;
            }


            if (pid.EndsWith("zh")) return true;

            return false;
        }

        // 這個方法負責取得 Local翻譯包路徑 資料。
        // EN: This method gets local pack path.
        public static string GetLocalPackPath()
        {
            string rimWorldRoot = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            return Path.Combine(rimWorldRoot, "Mods/!Translation_AI_Pack");
        }


        // 這個方法負責取得 Folder名稱By語言 資料。
        // EN: This method gets folder name by language.
        public static string GetFolderNameByLanguage(TargetLanguage lang)
        {
            switch (lang)
            {
                case TargetLanguage.Traditional: return "ChineseTraditional";
                case TargetLanguage.Simplified: return "ChineseSimplified";
                case TargetLanguage.Japanese: return "Japanese";
                case TargetLanguage.Korean: return "Korean";
                case TargetLanguage.Russian: return "Russian";
                case TargetLanguage.Ukrainian: return "Ukrainian";
                case TargetLanguage.English: return "English";

                case TargetLanguage.French: return "French";
                case TargetLanguage.German: return "German";
                case TargetLanguage.Spanish: return "Spanish";
                case TargetLanguage.Italian: return "Italian";
                case TargetLanguage.Polish: return "Polish";
                case TargetLanguage.Portuguese: return "PortugueseBrazilian";
                case TargetLanguage.Turkish: return "Turkish";
                default: return "English";
            }
        }


        // 這個方法負責取得 SecondaryFolder名稱By語言 資料。
        // EN: This method gets secondary folder name by language.
        public static string GetSecondaryFolderNameByLanguage(TargetLanguage lang)
        {
            switch (lang)
            {
                case TargetLanguage.Traditional: return "ChineseSimplified";
                case TargetLanguage.Simplified: return "ChineseTraditional";
                default: return null;
            }
        }

        private class TranslationLanguageSource
        {
            public string LanguageRoot;
            public string LanguageFolderPath;
            public string FolderName;
            public TargetLanguage? Language;
            public int Priority;
        }

        private static bool TryGetLanguageFromFolderName(string folderName, out TargetLanguage language)
        {
            foreach (TargetLanguage candidate in Enum.GetValues(typeof(TargetLanguage)))
            {
                if (IsLanguageFolderMatch(folderName, GetFolderNameByLanguage(candidate)))
                {
                    language = candidate;
                    return true;
                }
            }

            language = TargetLanguage.English;
            return false;
        }

        private static int GetLanguageSourcePriority(TargetLanguage? sourceLang, TargetLanguage targetLang)
        {
            if (sourceLang.HasValue && sourceLang.Value == targetLang) return 0;

            if (targetLang == TargetLanguage.Traditional && sourceLang == TargetLanguage.Simplified) return 10;
            if (targetLang == TargetLanguage.Simplified && sourceLang == TargetLanguage.Traditional) return 10;
            if (sourceLang == TargetLanguage.English) return 20;

            if (sourceLang == TargetLanguage.Japanese) return 30;
            if (sourceLang == TargetLanguage.Korean) return 35;
            if (sourceLang == TargetLanguage.Russian) return 40;
            if (sourceLang == TargetLanguage.Ukrainian) return 45;

            return sourceLang.HasValue ? 70 : 100;
        }

        private static List<TranslationLanguageSource> GetTranslationLanguageSources(string langRoot, TargetLanguage targetLang, bool includeTarget)
        {
            List<TranslationLanguageSource> result = new List<TranslationLanguageSource>();
            if (string.IsNullOrEmpty(langRoot) || !Directory.Exists(langRoot)) return result;

            try
            {
                foreach (string dir in Directory.GetDirectories(langRoot))
                {
                    string folderName = Path.GetFileName(dir);
                    TargetLanguage detected;
                    TargetLanguage? language = TryGetLanguageFromFolderName(folderName, out detected)
                        ? (TargetLanguage?)detected
                        : null;

                    if (!includeTarget && language.HasValue && language.Value == targetLang) continue;
                    if (!ContainsTranslationXmlFiles(dir)) continue;

                    result.Add(new TranslationLanguageSource
                    {
                        LanguageRoot = langRoot,
                        LanguageFolderPath = dir,
                        FolderName = folderName,
                        Language = language,
                        Priority = GetLanguageSourcePriority(language, targetLang)
                    });
                }
            }
            catch { }

            return result
                .OrderBy(s => s.Priority)
                .ThenBy(s => s.FolderName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> GetTranslatableLanguageBucketPaths(string langRoot, TargetLanguage targetLang, string bucketName, bool includeTarget)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(bucketName)) return result;

            foreach (TranslationLanguageSource source in GetTranslationLanguageSources(langRoot, targetLang, includeTarget))
            {
                AddLanguageBucketPath(result, source.LanguageFolderPath, bucketName);
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> GetTargetLanguageBucketPaths(string langRoot, TargetLanguage targetLang, string bucketName)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(bucketName)) return result;

            string targetFolder = GetFolderNameByLanguage(targetLang);
            foreach (string targetRoot in ResolveLanguageFolders(langRoot, targetFolder))
            {
                AddLanguageBucketPath(result, targetRoot, bucketName);
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> GetLanguageBucketPaths(string languageFolderPath, string bucketName)
        {
            List<string> result = new List<string>();
            AddLanguageBucketPath(result, languageFolderPath, bucketName);
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddLanguageBucketPath(List<string> result, string languageFolderPath, string bucketName)
        {
            if (result == null || string.IsNullOrEmpty(languageFolderPath) || string.IsNullOrWhiteSpace(bucketName)) return;

            string direct = Path.Combine(languageFolderPath, bucketName);
            if (Directory.Exists(direct)) result.Add(direct);

            string lower = Path.Combine(languageFolderPath, bucketName.ToLowerInvariant());
            if (Directory.Exists(lower)) result.Add(lower);
        }


        // 這個方法負責判斷 Is語言FolderMatch 條件是否成立。
        // EN: This method checks is language folder match.
        private static bool IsLanguageFolderMatch(string folderName, string expectedFolder)
        {
            if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrWhiteSpace(expectedFolder)) return false;

            string compact = NormalizeLanguageFolderName(folderName);
            string expectedCompact = NormalizeLanguageFolderName(expectedFolder);
            if (compact.Equals(expectedCompact, StringComparison.OrdinalIgnoreCase)) return true;
            if (compact.StartsWith(expectedCompact, StringComparison.OrdinalIgnoreCase)) return true;

            if (expectedFolder.Equals("ChineseSimplified", StringComparison.OrdinalIgnoreCase))
            {
                return compact.StartsWith("SimplifiedChinese", StringComparison.OrdinalIgnoreCase)
                    || compact.IndexOf("ChineseSimplified", StringComparison.OrdinalIgnoreCase) >= 0
                    || compact.IndexOf("SimplifiedChinese", StringComparison.OrdinalIgnoreCase) >= 0
                    || compact.Contains("\u7b80\u4f53")
                    || compact.Contains("\u7c21\u9ad4")
                    || compact.Contains("\u7b80\u4f53\u4e2d\u6587")
                    || compact.Contains("\u7c21\u9ad4\u4e2d\u6587");
            }

            if (expectedFolder.Equals("ChineseTraditional", StringComparison.OrdinalIgnoreCase))
            {
                return compact.StartsWith("TraditionalChinese", StringComparison.OrdinalIgnoreCase)
                    || compact.IndexOf("ChineseTraditional", StringComparison.OrdinalIgnoreCase) >= 0
                    || compact.IndexOf("TraditionalChinese", StringComparison.OrdinalIgnoreCase) >= 0
                    || compact.Contains("\u7e41\u4f53")
                    || compact.Contains("\u7e41\u9ad4")
                    || compact.Contains("\u7e41\u4f53\u4e2d\u6587")
                    || compact.Contains("\u7e41\u9ad4\u4e2d\u6587");
            }

            return false;
        }

        // 這個方法負責清理並標準化 語言Folder名稱 內容。
        // EN: This method cleans and normalizes language folder name.
        private static string NormalizeLanguageFolderName(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return "";
            return new string(folderName.Where(char.IsLetterOrDigit).ToArray());
        }


        // 這個方法負責處理 Resolve語言Folders 相關流程。
        // EN: This method handles resolve language folders.
        private static List<string> ResolveLanguageFolders(string langRoot, string folderName)
        {
            return ResolveLanguageFoldersCached(langRoot, folderName);
        }


        // 這個方法負責判斷 IsOld版本路徑 條件是否成立。
        // EN: This method checks is old version path.
        private static bool IsOldVersionPath(string modRoot, string fullPath)
        {
            string relative = fullPath.Substring(modRoot.Length).Replace('\\', '/');
            string[] parts = relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                string version = NormalizeVersionFolder(part);
                if (!string.IsNullOrEmpty(version) &&
                    !string.Equals(version, CurrentRimWorldVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string CurrentRimWorldVersion
        {
            get
            {
#if RIMWORLD_1_5
                return "1.5";
#else
                return "1.6";
#endif
            }
        }

        // 這個方法負責取得 CurrentLoadFolderVersions 資料。
        // EN: This method gets current load folder versions.
        private static string[] GetCurrentLoadFolderVersions()
        {
#if RIMWORLD_1_5
            return new[] { "v1.5", "1.5" };
#else
            return new[] { "v1.6", "1.6" };
#endif
        }

        // 這個方法負責清理並標準化 版本Folder 內容。
        // EN: This method cleans and normalizes version folder.
        private static string NormalizeVersionFolder(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return null;

            string candidate = folderName.Trim();
            if (candidate.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring(1);
            }

            if (candidate.Length < 3) return null;
            if (!char.IsDigit(candidate[0]) || candidate[1] != '.' || !char.IsDigit(candidate[2])) return null;

            int end = 3;
            while (end < candidate.Length && (char.IsDigit(candidate[end]) || candidate[end] == '.'))
            {
                end++;
            }

            string version = candidate.Substring(0, end);
            return version == "1.0" ||
                   version == "1.1" ||
                   version == "1.2" ||
                   version == "1.3" ||
                   version == "1.4" ||
                   version == "1.5" ||
                   version == "1.6"
                ? version
                : null;
        }


        // 這個方法負責解析 LoadFolders 內容。
        // EN: This method parses load folders.
        private static List<string> ParseLoadFolders(ModMetaData mod)
        {
            return mod == null || mod.RootDir == null
                ? new List<string>()
                : ParseLoadFolders(mod.RootDir.FullName);
        }

        private static List<string> ParseLoadFolders(string rootDir)
        {
            List<string> activeFolders = new List<string>();
            if (string.IsNullOrWhiteSpace(rootDir)) return activeFolders;

            string modRoot = Path.GetFullPath(rootDir);
            string loadFolderXml = Path.Combine(modRoot, "LoadFolders.xml");

            if (File.Exists(loadFolderXml))
            {
                try
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(loadFolderXml);

                    string[] versionsToCheck = GetCurrentLoadFolderVersions();
                    foreach (string ver in versionsToCheck)
                    {
                        XmlNode verNode = doc.SelectSingleNode($"//loadFolders/{ver}");
                        if (verNode != null)
                        {
                            foreach (XmlNode li in verNode.ChildNodes)
                            {
                                if (li.Name == "li" && !string.IsNullOrWhiteSpace(li.InnerText))
                                {
                                    string relativePath = li.InnerText.Trim().Replace('/', '\\');
                                    string folderPath = relativePath == "\\" || relativePath == ""
                                        ? modRoot
                                        : Path.Combine(modRoot, relativePath);

                                    if (Directory.Exists(folderPath)) activeFolders.Add(folderPath);
                                }
                            }
                            if (activeFolders.Count > 0) break;
                        }
                    }
                }
                catch { }
            }

            if (activeFolders.Count == 0)
            {
                activeFolders.Add(modRoot);
            }

            return activeFolders.Distinct().ToList();
        }


        // 這個方法負責取得 AllEffective語言路徑 資料。
        // EN: This method gets all effective language paths.
        public static List<string> GetAllEffectiveLangPaths(ModMetaData mod)
        {
            return GetModPathIndex(mod).EffectiveLangPaths;
        }

        public static List<string> GetAllEffectiveLangPaths(string packageId, string rootDir)
        {
            return GetModPathIndex(packageId, rootDir).EffectiveLangPaths;
        }

        public static List<string> GetAllTranslationPatchLangPaths(ModMetaData mod)
        {
            return GetModPathIndex(mod).TranslationPatchLangPaths;
        }

        public static List<string> GetAllTranslationPatchLangPaths(string packageId, string rootDir)
        {
            return GetModPathIndex(packageId, rootDir).TranslationPatchLangPaths;
        }

        public static bool HasScannableTranslationSources(ModMetaData mod)
        {
            if (mod == null) return false;
            return GetAllEffectiveLangPaths(mod).Count > 0 ||
                   GetAllEffectiveDefsPaths(mod).Count > 0;
        }

        public static bool HasScannableTranslationSources(string packageId, string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir)) return false;
            return GetAllEffectiveLangPaths(packageId, rootDir).Count > 0 ||
                   GetAllEffectiveDefsPaths(packageId, rootDir).Count > 0;
        }

        private static void AddLanguageRootsFrom(string root, string modRoot, List<string> result)
        {
            AddLanguageRootsFrom(root, modRoot, result, false);
        }

        private static void AddLanguageRootsFrom(string root, string modRoot, List<string> result, bool includeOldVersionPaths)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(modRoot) || result == null || !Directory.Exists(root)) return;

            try
            {
                string direct = Path.Combine(root, "Languages");
                if (Directory.Exists(direct) && (includeOldVersionPaths || !IsOldVersionPath(modRoot, direct)))
                {
                    result.Add(direct);
                }

                var dirs = Directory.GetDirectories(root, "Languages", SearchOption.AllDirectories);
                foreach (var dir in dirs)
                {
                    if (includeOldVersionPaths || !IsOldVersionPath(modRoot, dir)) result.Add(dir);
                }
            }
            catch { }
        }

        // 這個方法負責判斷 HasNative目標語言 條件是否成立。
        // EN: This method checks has native target language.
        public static bool HasNativeTargetLanguage(ModMetaData mod, TargetLanguage targetLang)
        {
            if (mod == null) return false;
            return HasNativeTargetLanguage(mod.PackageId, mod.RootDir != null ? mod.RootDir.FullName : "", targetLang);
        }

        public static bool HasNativeTargetLanguage(string packageId, string rootDir, TargetLanguage targetLang)
        {
            if (string.IsNullOrWhiteSpace(rootDir)) return false;
            string targetFolder = GetFolderNameByLanguage(targetLang);
            foreach (string langRoot in GetAllEffectiveLangPaths(packageId, rootDir))
            {
                try
                {
                    foreach (string targetRoot in ResolveLanguageFolders(langRoot, targetFolder))
                    {
                        if (ContainsTranslationXmlFiles(targetRoot))
                        {
                            return true;
                        }
                    }
                }
                catch { }
            }

            return false;
        }


        // 這個方法負責取得 AllEffectiveDefs路徑 資料。
        // EN: This method gets all effective defs paths.
        public static bool HasCompleteNativeTargetLanguage(ModMetaData mod, TargetLanguage targetLang)
        {
            if (mod == null) return false;

            string targetFolder = GetFolderNameByLanguage(targetLang);
            List<string> langRoots = GetAllEffectiveLangPaths(mod);
            List<string> defsRoots = GetAllEffectiveDefsPaths(mod);
            bool hasSource = false;
            bool hasTarget = false;

            var sourceKeyed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targetKeyed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string langRoot in langRoots)
            {
                foreach (string sourceKeyedDir in GetTranslatableLanguageBucketPaths(langRoot, targetLang, "Keyed", false))
                {
                    AddKeyedKeys(sourceKeyedDir, sourceKeyed);
                }

                foreach (string targetRoot in ResolveLanguageFolders(langRoot, targetFolder))
                {
                    if (ContainsTranslationXmlFiles(targetRoot)) hasTarget = true;
                    AddKeyedKeys(Path.Combine(targetRoot, "Keyed"), targetKeyed);
                    AddKeyedKeys(Path.Combine(targetRoot, "keyed"), targetKeyed);
                }
            }

            if (sourceKeyed.Count > 0)
            {
                hasSource = true;
                foreach (string key in sourceKeyed)
                {
                    if (!targetKeyed.Contains(key)) return false;
                }
            }

            var sourceDefs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var targetDefs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string defsRoot in defsRoots)
            {
                foreach (var typePair in ExtractEnglishFromRawDefs(defsRoot))
                {
                    AddDefKeys(sourceDefs, typePair.Key, typePair.Value.Keys);
                }
            }

            foreach (string langRoot in langRoots)
            {
                foreach (string sourceDefDir in GetTranslatableLanguageBucketPaths(langRoot, targetLang, "DefInjected", false))
                {
                    AddDefKeysFromDir(sourceDefDir, sourceDefs);
                }

                foreach (string targetRoot in ResolveLanguageFolders(langRoot, targetFolder))
                {
                    AddDefKeysFromDir(Path.Combine(targetRoot, "DefInjected"), targetDefs);
                    AddDefKeysFromDir(Path.Combine(targetRoot, "defInjected"), targetDefs);
                }
            }

            if (sourceDefs.Count > 0)
            {
                hasSource = true;
                foreach (var typePair in sourceDefs)
                {
                    if (!targetDefs.TryGetValue(typePair.Key, out HashSet<string> targetKeys))
                    {
                        return false;
                    }

                    foreach (string key in typePair.Value)
                    {
                        if (!targetKeys.Contains(key)) return false;
                    }
                }
            }

            return hasSource && hasTarget;
        }

        private static void AddKeyedKeys(string path, HashSet<string> keys)
        {
            if (keys == null || string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            foreach (string file in GetXmlFilesCached(path, SearchOption.AllDirectories))
            {
                foreach (string key in LoadXmlFileToDict(file).Keys)
                {
                    keys.Add(key);
                }
            }
        }

        private static void AddDefKeysFromDir(string path, Dictionary<string, HashSet<string>> target)
        {
            if (target == null || string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            foreach (string typeDir in Directory.GetDirectories(path))
            {
                string defType = Path.GetFileName(typeDir);
                foreach (string file in GetXmlFilesCached(typeDir, SearchOption.AllDirectories))
                {
                    AddDefKeys(target, defType, LoadXmlFileToDict(file).Keys);
                }
            }

            foreach (string file in GetXmlFilesCached(path, SearchOption.TopDirectoryOnly))
            {
                AddDefKeys(target, "General", LoadXmlFileToDict(file).Keys);
            }
        }

        private static void AddDefKeys(Dictionary<string, HashSet<string>> target, string defType, IEnumerable<string> keys)
        {
            if (target == null || string.IsNullOrEmpty(defType) || keys == null) return;

            if (!target.TryGetValue(defType, out HashSet<string> typeKeys))
            {
                typeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                target[defType] = typeKeys;
            }

            foreach (string key in keys)
            {
                if (!string.IsNullOrEmpty(key)) typeKeys.Add(key);
            }
        }

        private static bool ContainsTranslationXmlFiles(string languageFolderPath)
        {
            if (string.IsNullOrEmpty(languageFolderPath) || !Directory.Exists(languageFolderPath)) return false;
            try
            {
                return ContainsXmlFiles(Path.Combine(languageFolderPath, "Keyed")) ||
                       ContainsXmlFiles(Path.Combine(languageFolderPath, "keyed")) ||
                       ContainsXmlFiles(Path.Combine(languageFolderPath, "DefInjected")) ||
                       ContainsXmlFiles(Path.Combine(languageFolderPath, "defInjected"));
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsXmlFiles(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
            try
            {
                return GetXmlFilesCached(path, SearchOption.AllDirectories).Count > 0;
            }
            catch
            {
                return false;
            }
        }

        public static List<string> GetAllEffectiveDefsPaths(ModMetaData mod)
        {
            return GetModPathIndex(mod).EffectiveDefsPaths;
        }

        public static List<string> GetAllEffectiveDefsPaths(string packageId, string rootDir)
        {
            return GetModPathIndex(packageId, rootDir).EffectiveDefsPaths;
        }

        private static void AddDefsRootsFrom(string root, string modRoot, List<string> result)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(modRoot) || result == null || !Directory.Exists(root)) return;

            try
            {
                string direct = Path.Combine(root, "Defs");
                if (Directory.Exists(direct) && !IsOldVersionPath(modRoot, direct))
                {
                    result.Add(direct);
                }

                var dirs = Directory.GetDirectories(root, "Defs", SearchOption.AllDirectories);
                foreach (var dir in dirs)
                {
                    if (!IsOldVersionPath(modRoot, dir)) result.Add(dir);
                }
            }
            catch { }
        }

    }
}
