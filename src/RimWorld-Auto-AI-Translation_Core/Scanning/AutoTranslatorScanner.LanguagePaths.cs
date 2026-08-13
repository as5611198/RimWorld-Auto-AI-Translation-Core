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

            string version = candidate.Substring(0, end).TrimEnd('.');
            Version parsed;
            if (!Version.TryParse(version, out parsed) || parsed.Major != 1 || parsed.Minor < 0 || parsed.Minor > 6)
                return null;
            return version;
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
            bool selectedManifestBranch;
            if (TryParseLoadFolders(modRoot, out activeFolders, out selectedManifestBranch) &&
                activeFolders.Count > 0)
            {
                return activeFolders;
            }

            activeFolders.Clear();
            if (Directory.Exists(modRoot))
            {
                activeFolders.Add(modRoot);
            }

            return activeFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool TryParseLoadFolders(
            string modRoot,
            out List<string> activeFolders,
            out bool selectedManifestBranch)
        {
            activeFolders = new List<string>();
            selectedManifestBranch = false;
            if (string.IsNullOrWhiteSpace(modRoot)) return false;

            string loadFolderXml = Path.Combine(modRoot, "LoadFolders.xml");
            if (!File.Exists(loadFolderXml)) return false;

            if (TryResolveNativeLoadFolders(modRoot, out activeFolders, out selectedManifestBranch))
            {
                return true;
            }

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(loadFolderXml);
                XmlNode rootNode = doc.DocumentElement;
                if (rootNode == null || !rootNode.Name.Equals("loadFolders", StringComparison.OrdinalIgnoreCase))
                    return false;

                XmlNode selectedNode = SelectLoadFoldersVersionNode(rootNode);
                if (selectedNode == null) return false;
                selectedManifestBranch = true;

                foreach (XmlNode li in selectedNode.ChildNodes.Cast<XmlNode>().Reverse())
                {
                    if (li.NodeType != XmlNodeType.Element ||
                        !li.Name.Equals("li", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(li.InnerText) ||
                        !ShouldUseLoadFolderEntry(li))
                    {
                        continue;
                    }

                    string relativePath = li.InnerText.Trim().Replace('/', Path.DirectorySeparatorChar);
                    string folderPath = relativePath == Path.DirectorySeparatorChar.ToString() || relativePath == ""
                        ? modRoot
                        : Path.Combine(modRoot, relativePath);
                    if (Directory.Exists(folderPath)) activeFolders.Add(NormalizeCachePath(folderPath));
                }

                activeFolders = activeFolders
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return true;
            }
            catch
            {
                activeFolders.Clear();
                selectedManifestBranch = false;
                return false;
            }
        }

        private static bool TryResolveNativeLoadFolders(
            string modRoot,
            out List<string> activeFolders,
            out bool selectedManifestBranch)
        {
            activeFolders = new List<string>();
            selectedManifestBranch = false;
            try
            {
                ModMetaData metadata = ModLister.AllInstalledMods.FirstOrDefault(mod =>
                    mod != null && mod.RootDir != null && PathsEqual(mod.RootDir.FullName, modRoot));
                if (metadata == null || metadata.loadFolders == null) return false;

                List<string> definedVersions = metadata.loadFolders.DefinedVersions() ?? new List<string>();
                string currentVersionText = VersionControl.CurrentVersionString;
                bool selectedBranch = HasExactLoadFolderVersion(definedVersions, currentVersionText);
                List<LoadFolder> selected = selectedBranch
                    ? metadata.LoadFoldersForVersion(currentVersionText)
                    : null;
                if (!selectedBranch)
                {
                    Version current = VersionControl.CurrentVersion;
                    string nearest = definedVersions
                        .Where(version => !string.IsNullOrWhiteSpace(version) &&
                                          !version.Equals("default", StringComparison.OrdinalIgnoreCase))
                        .Select(version => new { Text = version, Parsed = TryParseLoadFolderVersion(version) })
                        .Where(item => item.Parsed != null && item.Parsed <= current)
                        .OrderByDescending(item => item.Parsed)
                        .Select(item => item.Text)
                        .FirstOrDefault();
                    if (!string.IsNullOrEmpty(nearest))
                    {
                        selected = metadata.LoadFoldersForVersion(nearest);
                        selectedBranch = true;
                    }
                }
                if (!selectedBranch && definedVersions.Any(
                    version => version.Equals("default", StringComparison.OrdinalIgnoreCase)))
                {
                    selected = metadata.LoadFoldersForVersion("default");
                    selectedBranch = true;
                }
                if (!selectedBranch) return false;

                selectedManifestBranch = true;
                foreach (LoadFolder loadFolder in (selected ?? new List<LoadFolder>()).AsEnumerable().Reverse())
                {
                    if (loadFolder == null || !loadFolder.ShouldLoad) continue;

                    string relative = (loadFolder.folderName ?? "").Trim().Replace('/', Path.DirectorySeparatorChar);
                    string fullPath = relative == Path.DirectorySeparatorChar.ToString() || relative == ""
                        ? modRoot
                        : Path.Combine(modRoot, relative);
                    if (Directory.Exists(fullPath)) activeFolders.Add(NormalizeCachePath(fullPath));
                }

                activeFolders = activeFolders
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return true;
            }
            catch
            {
                activeFolders.Clear();
                selectedManifestBranch = false;
                return false;
            }
        }

        private static bool HasExactLoadFolderVersion(
            IEnumerable<string> definedVersions,
            string currentVersion)
        {
            string currentText = GetStrictLoadFolderVersionText(currentVersion);
            if (string.IsNullOrEmpty(currentText) || TryParseLoadFolderVersion(currentText) == null)
                return false;

            return (definedVersions ?? Enumerable.Empty<string>()).Any(version =>
            {
                string definedText = GetStrictLoadFolderVersionText(version);
                return !string.IsNullOrEmpty(definedText) &&
                       TryParseLoadFolderVersion(definedText) != null &&
                       string.Equals(definedText, currentText, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static Version TryParseLoadFolderVersion(string value)
        {
            string candidate = GetStrictLoadFolderVersionText(value);
            if (string.IsNullOrEmpty(candidate)) return null;
            Version parsed;
            return Version.TryParse(candidate, out parsed) ? parsed : null;
        }

        private static string GetStrictLoadFolderVersionText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string candidate = value.Trim();
            if (candidate.StartsWith("v", StringComparison.OrdinalIgnoreCase)) candidate = candidate.Substring(1);
            return candidate;
        }

        private static XmlNode SelectLoadFoldersVersionNode(XmlNode rootNode)
        {
            Version current;
            try
            {
                current = VersionControl.CurrentVersion;
            }
            catch
            {
                if (!Version.TryParse(CurrentRimWorldVersion, out current)) return null;
            }
            string exactVersion;
            try
            {
                exactVersion = VersionControl.CurrentVersionString;
            }
            catch
            {
                exactVersion = CurrentRimWorldVersion;
            }

            XmlNode exact = null;
            XmlNode nearest = null;
            Version nearestVersion = null;
            XmlNode defaultNode = null;

            foreach (XmlNode child in rootNode.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                if (child.Name.Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    defaultNode = child;
                    continue;
                }

                string versionText = GetStrictLoadFolderVersionText(child.Name);
                Version candidate = TryParseLoadFolderVersion(child.Name);
                if (candidate == null) continue;
                if (string.Equals(versionText, exactVersion, StringComparison.OrdinalIgnoreCase))
                {
                    exact = child;
                    break;
                }
                if (candidate > current || (nearestVersion != null && candidate <= nearestVersion)) continue;
                nearest = child;
                nearestVersion = candidate;
            }

            return exact ?? nearest ?? defaultNode;
        }

        private static bool ShouldUseLoadFolderEntry(XmlNode li)
        {
            try
            {
                string anyActive = GetXmlAttribute(li, "IfModActive");
                if (!string.IsNullOrWhiteSpace(anyActive) && !IsAnyListedModActive(anyActive)) return false;

#if !RIMWORLD_1_5
                string allActive = GetXmlAttribute(li, "IfModActiveAll");
                if (!string.IsNullOrWhiteSpace(allActive) && !AreAllListedModsActive(allActive)) return false;
#endif

                string notActive = GetXmlAttribute(li, "IfModNotActive");
                if (!string.IsNullOrWhiteSpace(notActive) && IsAnyListedModActive(notActive)) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetXmlAttribute(XmlNode node, string name)
        {
            if (node == null || node.Attributes == null) return "";
            XmlAttribute attribute = node.Attributes.Cast<XmlAttribute>()
                .FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return attribute != null ? attribute.Value ?? "" : "";
        }

        private static bool IsAnyListedModActive(string packageIds)
        {
            if (string.IsNullOrWhiteSpace(packageIds)) return false;
            List<string> ids = packageIds
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .ToList();
#if RIMWORLD_1_5
            return ModLister.AnyFromListActive(ids);
#else
            return ModLister.AnyModActiveNoSuffix(ids);
#endif
        }

#if !RIMWORLD_1_5
        private static bool AreAllListedModsActive(string packageIds)
        {
            if (string.IsNullOrWhiteSpace(packageIds)) return true;
            List<string> ids = packageIds
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .ToList();
            return ModLister.AllModsActiveNoSuffix(ids);
        }
#endif

        private static List<string> ResolveContentRootsForScanning(
            string packageId,
            string rootDir,
            out bool usedRunningModRoots)
        {
            usedRunningModRoots = false;
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(rootDir)) return result;

            string modRoot = NormalizeCachePath(rootDir);
            try
            {
                bool matchedRunningMod = false;
                foreach (ModContentPack content in LoadedModManager.RunningModsListForReading)
                {
                    if (content == null) continue;

                    bool rootMatches = PathsEqual(content.RootDir, modRoot);
                    if (!rootMatches) continue;
                    matchedRunningMod = true;

                    if (content.foldersToLoadDescendingOrder != null)
                    {
                        foreach (string folder in content.foldersToLoadDescendingOrder)
                        {
                            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                            result.Add(NormalizeCachePath(folder));
                        }
                    }

                    usedRunningModRoots = true;
                    break;
                }

                if (matchedRunningMod) usedRunningModRoots = true;
            }
            catch
            {
                result.Clear();
                usedRunningModRoots = false;
            }

            if (!usedRunningModRoots)
            {
                result = ResolveFallbackContentRoots(modRoot);
            }

            return result
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> ResolveFallbackContentRoots(string modRoot)
        {
            List<string> parsed;
            bool selectedManifestBranch;
            if (TryParseLoadFolders(modRoot, out parsed, out selectedManifestBranch) &&
                selectedManifestBranch)
            {
                return parsed;
            }

            List<string> result = new List<string>();
            string fallbackVersionRoot = FindBestFallbackVersionRoot(modRoot);
            if (!string.IsNullOrEmpty(fallbackVersionRoot)) result.Add(fallbackVersionRoot);

            string commonRoot = Path.Combine(modRoot, "Common");
            if (Directory.Exists(commonRoot)) result.Add(commonRoot);
            if (Directory.Exists(modRoot)) result.Add(modRoot);

            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FindBestFallbackVersionRoot(string modRoot)
        {
            if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot)) return null;

            Version current;
            try
            {
                current = VersionControl.CurrentVersion;
            }
            catch
            {
                if (!Version.TryParse(CurrentRimWorldVersion, out current)) return null;
            }

            string bestPath = null;
            Version bestVersion = null;
            try
            {
                foreach (string directory in Directory.GetDirectories(modRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    Version candidate = TryParseLoadFolderVersion(Path.GetFileName(directory));
                    if (candidate == null) continue;
                    if (candidate > current || (bestVersion != null && candidate <= bestVersion)) continue;

                    bestVersion = candidate;
                    bestPath = NormalizeCachePath(directory);
                }
            }
            catch { }

            return bestPath;
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                return string.Equals(
                    NormalizeCachePath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    NormalizeCachePath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
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
            return HasUsableTranslationSources(GetModPathIndex(mod));
        }

        public static bool HasScannableTranslationSources(string packageId, string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir)) return false;
            return HasUsableTranslationSources(GetModPathIndex(packageId, rootDir));
        }

        internal static bool HasScannableTranslationSourcesNormally(string packageId, string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir)) return false;
            ModPathIndexCacheEntry index = GetModPathIndex(packageId, rootDir, false);
            return HasUsableTranslationSources(index);
        }

        private static bool HasUsableTranslationSources(ModPathIndexCacheEntry index)
        {
            if (index == null) return false;
            return index.EffectiveDefsPaths.Any(ContainsXmlFiles) ||
                   index.EffectiveLangPaths.Any(ContainsAnyTranslationLanguageSource);
        }

        internal static List<string> GetForceTranslationCandidatePaths(string rootDir)
        {
            List<string> result = new List<string>();
            result.AddRange(DiscoverForceTranslationCandidateDefsPaths(rootDir));
            result.AddRange(DiscoverForceTranslationCandidateLangPaths(rootDir));
            return FilterEligibleForceTranslationCandidatePaths(rootDir, result);
        }

        private static List<string> GetForceTranslationCandidateDefsPaths(string rootDir)
        {
            return FilterEligibleForceTranslationCandidatePaths(
                rootDir,
                DiscoverForceTranslationCandidateDefsPaths(rootDir));
        }

        private static List<string> DiscoverForceTranslationCandidateDefsPaths(string rootDir)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return result;

            AddDefsRootsFrom(rootDir, rootDir, result, true);
            return result
                .Where(path => ContainsXmlFiles(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> GetForceTranslationCandidateLangPaths(string rootDir)
        {
            return FilterEligibleForceTranslationCandidatePaths(
                rootDir,
                DiscoverForceTranslationCandidateLangPaths(rootDir));
        }

        private static List<string> DiscoverForceTranslationCandidateLangPaths(string rootDir)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return result;

            AddLanguageRootsFrom(rootDir, rootDir, result, true);
            return result
                .Where(ContainsAnyTranslationLanguageSource)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> FilterEligibleForceTranslationCandidatePaths(
            string rootDir,
            IEnumerable<string> candidates)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(rootDir) || candidates == null || !Directory.Exists(rootDir))
                return result;

            string modRoot = NormalizeCachePath(rootDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool usedRunningModRoots;
            List<string> activeContentRoots = ResolveContentRootsForScanning(
                "",
                modRoot,
                out usedRunningModRoots);
            List<string> inactiveManifestRoots = GetInactiveManifestContentRoots(
                modRoot,
                activeContentRoots);

            foreach (string candidate in candidates)
            {
                if (!IsEligibleForceTranslationCandidate(
                    modRoot,
                    candidate,
                    activeContentRoots,
                    inactiveManifestRoots))
                {
                    continue;
                }

                result.Add(NormalizeCachePath(candidate));
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsEligibleForceTranslationCandidate(
            string modRoot,
            string candidate,
            List<string> activeContentRoots,
            List<string> inactiveManifestRoots)
        {
            if (string.IsNullOrWhiteSpace(modRoot) || string.IsNullOrWhiteSpace(candidate)) return false;

            string normalizedCandidate = NormalizeCachePath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsSameOrAncestorPath(modRoot, normalizedCandidate)) return false;

            if ((inactiveManifestRoots ?? new List<string>()).Any(
                inactiveRoot => IsSameOrAncestorPath(inactiveRoot, normalizedCandidate)))
            {
                return false;
            }

            string relative = normalizedCandidate.Substring(modRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relative.Length == 0) return true;

            int separator = relative.IndexOfAny(new[]
            {
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            });
            string topFolder = separator >= 0 ? relative.Substring(0, separator) : relative;
            bool isReservedContentBranch =
                topFolder.Equals("Common", StringComparison.OrdinalIgnoreCase) ||
                TryParseLoadFolderVersion(topFolder) != null;
            if (!isReservedContentBranch) return true;

            string reservedRoot = NormalizeCachePath(Path.Combine(modRoot, topFolder));
            return (activeContentRoots ?? new List<string>()).Any(
                activeRoot => PathsEqual(activeRoot, reservedRoot));
        }

        private static List<string> GetInactiveManifestContentRoots(
            string modRoot,
            List<string> activeContentRoots)
        {
            List<string> result = new List<string>();
            string manifestPath = Path.Combine(modRoot ?? "", "LoadFolders.xml");
            if (!File.Exists(manifestPath)) return result;

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(manifestPath);
                foreach (XmlNode li in doc.GetElementsByTagName("li"))
                {
                    if (li == null || string.IsNullOrWhiteSpace(li.InnerText)) continue;

                    string relative = li.InnerText.Trim().Replace('/', Path.DirectorySeparatorChar);
                    string declaredRoot = relative == Path.DirectorySeparatorChar.ToString() || relative == ""
                        ? modRoot
                        : NormalizeCachePath(Path.Combine(modRoot, relative));
                    bool containsActiveRoot = (activeContentRoots ?? new List<string>()).Any(
                        activeRoot => IsSameOrAncestorPath(declaredRoot, activeRoot));
                    if (!containsActiveRoot) result.Add(declaredRoot);
                }
            }
            catch
            {
                return new List<string>();
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool ContainsAnyTranslationLanguageSource(string languageRoot)
        {
            if (string.IsNullOrWhiteSpace(languageRoot) || !Directory.Exists(languageRoot)) return false;
            try
            {
                return Directory.GetDirectories(languageRoot).Any(ContainsTranslationXmlFiles);
            }
            catch
            {
                return false;
            }
        }

        private static void AddDirectLanguageRoot(string contentRoot, List<string> result)
        {
            if (string.IsNullOrWhiteSpace(contentRoot) || result == null) return;
            string direct = Path.Combine(contentRoot, "Languages");
            if (Directory.Exists(direct)) result.Add(direct);
        }

        private static void AddDirectDefsRoot(string contentRoot, List<string> result)
        {
            if (string.IsNullOrWhiteSpace(contentRoot) || result == null) return;
            string direct = Path.Combine(contentRoot, "Defs");
            if (Directory.Exists(direct)) result.Add(direct);
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
            AddDefsRootsFrom(root, modRoot, result, false);
        }

        private static void AddDefsRootsFrom(
            string root,
            string modRoot,
            List<string> result,
            bool includeOldVersionPaths)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(modRoot) || result == null || !Directory.Exists(root)) return;

            try
            {
                string direct = Path.Combine(root, "Defs");
                if (Directory.Exists(direct) && (includeOldVersionPaths || !IsOldVersionPath(modRoot, direct)))
                {
                    result.Add(direct);
                }

                var dirs = Directory.GetDirectories(root, "Defs", SearchOption.AllDirectories);
                foreach (var dir in dirs)
                {
                    if (includeOldVersionPaths || !IsOldVersionPath(modRoot, dir)) result.Add(dir);
                }
            }
            catch { }
        }

    }
}
