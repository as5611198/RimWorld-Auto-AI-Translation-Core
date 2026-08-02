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
// 這個檔案負責翻譯包維護與舊資料整理。
// EN: This file maintains the local translation pack and legacy data layout.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器掃描器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorScanner.
    public static partial class AutoTranslatorScanner
    {
        private static readonly object PackMaintenanceLock = new object();
        // 這個欄位保存 packMaintenanceQueued 的執行狀態或快取資料。
        // EN: This field stores pack maintenance queued runtime state or cached data.
        private static int _packMaintenanceQueued;
        // 這個欄位保存 packMaintenanceRunning 的執行狀態或快取資料。
        // EN: This field stores pack maintenance running runtime state or cached data.
        private static int _packMaintenanceRunning;

        // 這個方法負責處理 MigrateOldTranslations 相關流程。
        // EN: This method handles migrate old translations.
        public static void MigrateOldTranslations()
        {
            try
            {
                string packPath = GetLocalPackPath();
                string langsPath = Path.Combine(packPath, "Languages");
                if (!Directory.Exists(langsPath)) return;

                foreach (var langDir in Directory.GetDirectories(langsPath))
                {
                    string defInjectedDir = Path.Combine(langDir, "DefInjected");
                    if (!Directory.Exists(defInjectedDir)) continue;

                    foreach (var maybePackageDir in Directory.GetDirectories(defInjectedDir))
                    {
                        string packageName = Path.GetFileName(maybePackageDir);
                        bool isOldPackageStructure = false;

                        foreach (var defTypeDir in Directory.GetDirectories(maybePackageDir))
                        {
                            string defType = Path.GetFileName(defTypeDir);
                            string oldFile = Path.Combine(defTypeDir, "AutoTranslated_Defs.xml");

                            if (File.Exists(oldFile))
                            {
                                isOldPackageStructure = true;
                                string newTargetDir = Path.Combine(defInjectedDir, defType);
                                Directory.CreateDirectory(newTargetDir);

                                string cleanPackageName = packageName.Replace(".", "_");
                                string newFile = Path.Combine(newTargetDir, $"{cleanPackageName}_AutoTranslated.xml");

                                if (File.Exists(newFile))
                                {
                                    var oldDict = LoadXmlFileToDict(oldFile);
                                    var newDict = LoadXmlFileToDict(newFile);
                                    foreach (var kv in oldDict) newDict[kv.Key] = kv.Value;
                                    SaveXml(newFile, newDict);
                                    File.Delete(oldFile);
                                    NotifyTranslationFileChanged(oldFile);
                                }
                                else
                                {
                                    File.Move(oldFile, newFile);
                                    NotifyTranslationFileChanged(oldFile);
                                    NotifyTranslationFileChanged(newFile);
                                }
                                AutoTranslatorSettings.AddLog("ATC_Log_MigrateSuccess".Translate(packageName, defType));
                            }
                        }

                        if (isOldPackageStructure)
                        {
                            try
                            {
                                if (Directory.GetFiles(maybePackageDir, "*", SearchOption.AllDirectories).Length == 0)
                                {
                                    Directory.Delete(maybePackageDir, true);
                                    NotifyTranslationFilesChanged(maybePackageDir);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Migration system error: {ex.Message}");
            }
        }


        // 這個方法負責清理並標準化 upSelfTranslations 內容。
        // EN: This method cleans and normalizes up self translations.
        private static void CleanupSelfTranslations()
        {
            try
            {
                string packPath = GetLocalPackPath();
                string langsPath = Path.Combine(packPath, "Languages");
                if (!Directory.Exists(langsPath)) return;

                string[] forbiddenPrefixes = { "auto_aitranslation_core", "aitranslation_pack" };

                var allXmls = GetXmlFilesCached(langsPath, SearchOption.AllDirectories);
                foreach (var file in allXmls)
                {
                    string fileName = Path.GetFileName(file).ToLower();
                    foreach (var prefix in forbiddenPrefixes)
                    {
                        if (fileName.StartsWith(prefix))
                        {
                            File.Delete(file);
                            NotifyTranslationFileChanged(file);
                            AutoTranslatorSettings.AddLog($"🗑️ [System] 發現並刪除誤翻譯檔案 / Deleted rogue file: {Path.GetFileName(file)}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] 清理自我翻譯檔時發生錯誤 / Self-cleanup error: {ex.Message}");
            }
        }


        // 這個方法負責確保 翻譯包Initialized 已準備完成。
        // EN: This method ensures pack initialized is ready.
        public static void EnsurePackInitialized(bool runFullMaintenance = false)
        {
            EnsurePackSkeleton();

            if (runFullMaintenance)
            {
                RunPackMaintenance(waitForExisting: true, repairPlaceholders: true);
            }
            else
            {
                QueueBackgroundPackMaintenanceOnce();
            }
        }

        // 這個方法負責確保 翻譯包Skeleton 已準備完成。
        // EN: This method ensures pack skeleton is ready.
        private static void EnsurePackSkeleton()
        {
            string packPath = GetLocalPackPath();
            string aboutPath = Path.Combine(packPath, "About/About.xml");
            if (!File.Exists(aboutPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aboutPath));
                File.WriteAllText(aboutPath, "<?xml version=\"1.0\" encoding=\"utf-8\"?><ModMetaData><name>! AutoTranslation AI Pack</name><author>Auto Translator Core</author><packageId>AITranslation.Pack</packageId><supportedVersions><li>1.6</li></supportedVersions></ModMetaData>");
            }
        }

        // 這個方法負責排入 Background翻譯包MaintenanceOnce 佇列。
        // EN: This method queues background pack maintenance once.
        private static void QueueBackgroundPackMaintenanceOnce(int delayMs = 750)
        {
            if (Interlocked.Exchange(ref _packMaintenanceQueued, 1) == 1) return;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Math.Max(0, delayMs));
                    RunPackMaintenance();
                }
                catch (Exception ex)
                {
                    Log.Warning($"[AutoTranslationCore] Background pack maintenance failed: {ex.Message}");
                }
            });
        }

        // 這個方法負責處理 Run翻譯包Maintenance 相關流程。
        // EN: This method handles run pack maintenance.
        private static void RunPackMaintenance(bool waitForExisting = false, bool repairPlaceholders = false)
        {
            if (Interlocked.CompareExchange(ref _packMaintenanceRunning, 1, 0) != 0)
            {
                if (!waitForExisting) return;

                System.Threading.SpinWait.SpinUntil(
                    () => Interlocked.CompareExchange(ref _packMaintenanceRunning, 0, 0) == 0,
                    TimeSpan.FromSeconds(30));

                if (Interlocked.CompareExchange(ref _packMaintenanceRunning, 1, 0) != 0) return;
            }

            try
            {
                lock (PackMaintenanceLock)
                {
                    CleanupSelfTranslations();
                    MigrateOldTranslations();
                    ApplyEmergencyHotfix();
                    ApplyOfficialDlcKeyedHotfix();
                    ApplyBuiltInKeyedHotfix();
                    RemovePlaceholderTranslationsFromGeneratedPack();
                    if (repairPlaceholders)
                    {
                        RepairGeneratedPlaceholderTokens();
                    }
                    RunDetoxScanner();
                    RunAdvancedDetoxScanner();
                    RunNewlineDetoxScanner();
                    AutoTranslatorLegacyRepairer.QueueBackgroundRepairOnce();
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Pack maintenance failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _packMaintenanceRunning, 0);
            }
        }

        private static void RemovePlaceholderTranslationsFromGeneratedPack()
        {
            try
            {
                if (AutoTranslatorMod.Settings == null) return;

                string packPath = GetLocalPackPath();
                string targetFolder = GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
                string languageRoot = Path.Combine(packPath, "Languages", targetFolder);
                if (!Directory.Exists(languageRoot)) return;

                int removedEntries = 0;
                int fixedFiles = 0;
                foreach (string file in GetXmlFilesCached(languageRoot, SearchOption.AllDirectories))
                {
                    XmlDocument doc = new XmlDocument();
                    try
                    {
                        doc.Load(file);
                    }
                    catch
                    {
                        continue;
                    }

                    if (doc.DocumentElement == null || doc.DocumentElement.Name != "LanguageData") continue;

                    bool changed = false;
                    foreach (XmlNode node in doc.DocumentElement.ChildNodes.Cast<XmlNode>().ToList())
                    {
                        if (node.NodeType != XmlNodeType.Element) continue;
                        if (!LanguageDetector.LooksLikePlaceholderTranslation(node.InnerText, AutoTranslatorMod.Settings.TargetLang)) continue;

                        doc.DocumentElement.RemoveChild(node);
                        removedEntries++;
                        changed = true;
                    }

                    if (changed)
                    {
                        SaveLanguageXmlDocumentAtomic(file, doc);
                        NotifyTranslationFileChanged(file);
                        fixedFiles++;
                    }
                }

                if (removedEntries > 0)
                {
                    AutoTranslatorSettings.AddLog($"🧹 [System] 已移除 {removedEntries} 個 TODO/佔位翻譯，影響 {fixedFiles} 個檔案。下次翻譯會重新補齊。");
                    Log.Message($"[AutoTranslationCore] Removed placeholder translations: {removedEntries} entries across {fixedFiles} files.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Placeholder translation cleanup failed: {ex.Message}");
            }
        }

        private static void RepairGeneratedPlaceholderTokens()
        {
            try
            {
                string packPath = GetLocalPackPath();
                string targetFolder = GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
                string languageRoot = Path.Combine(packPath, "Languages", targetFolder);
                if (!Directory.Exists(languageRoot)) return;

                int fixedFiles = 0;
                int fixedEntries = 0;
                List<string> activePackagePrefixes = ModLister.AllInstalledMods
                    .Where(m => m != null && m.Active && !string.IsNullOrWhiteSpace(m.PackageId))
                    .Select(m => m.PackageId.Replace(".", "_"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(p => p.Length)
                    .ToList();

                HashSet<string> candidatePackages = FindPlaceholderRepairCandidatePackages(languageRoot, activePackagePrefixes);
                if (candidatePackages.Count == 0) return;

                foreach (ModMetaData mod in ModLister.AllInstalledMods.Where(m => m != null && m.Active))
                {
                if (string.IsNullOrWhiteSpace(mod.PackageId)) continue;

                string packagePrefix = mod.PackageId.Replace(".", "_");
                if (!candidatePackages.Contains(packagePrefix)) continue;

                    var keyedSources = BuildModKeyedSources(mod);
                    fixedEntries += RepairGeneratedKeyedPlaceholders(languageRoot, packagePrefix, keyedSources, ref fixedFiles);

                    var defSources = BuildModDefSources(mod);
                    fixedEntries += RepairGeneratedDefPlaceholders(languageRoot, packagePrefix, defSources, ref fixedFiles);
                }

                if (fixedEntries > 0)
                {
                    AutoTranslatorSettings.AddLog("🧩 " +
                        AutoTranslatorAPI.TranslateText("ATC_Log_PlaceholderRepairSummary", fixedEntries, fixedFiles));
                    Log.Message($"[AutoTranslationCore] Placeholder token repair complete: {fixedEntries} entries across {fixedFiles} files.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Placeholder token repair failed: {ex.Message}");
            }
        }

        private static HashSet<string> FindPlaceholderRepairCandidatePackages(string languageRoot, List<string> activePackagePrefixes)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(languageRoot) || !Directory.Exists(languageRoot)) return result;
            if (activePackagePrefixes == null || activePackagePrefixes.Count == 0) return result;

            Regex suspicious = new Regex(@"（[A-Za-z0-9_]+）|【[A-Za-z0-9_]+】|［[A-Za-z0-9_]+］", RegexOptions.Compiled);

            foreach (string file in GetXmlFilesCached(languageRoot, SearchOption.AllDirectories))
            {
                try
                {
                    string content = File.ReadAllText(file);
                    if (!suspicious.IsMatch(content)) continue;

                    string packagePrefix = ExtractPackagePrefixFromGeneratedFile(languageRoot, file, activePackagePrefixes);
                    if (!string.IsNullOrWhiteSpace(packagePrefix))
                    {
                        result.Add(packagePrefix);
                    }
                }
                catch { }
            }

            return result;
        }

        private static string ExtractPackagePrefixFromGeneratedFile(string languageRoot, string file, List<string> activePackagePrefixes)
        {
            if (string.IsNullOrEmpty(languageRoot) || string.IsNullOrEmpty(file)) return "";

            string fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.EndsWith("_AutoTranslated", StringComparison.OrdinalIgnoreCase))
            {
                return fileName.Substring(0, fileName.Length - "_AutoTranslated".Length);
            }

            try
            {
                string keyedRoot = Path.Combine(languageRoot, "Keyed");
                string fullKeyedRoot = Path.GetFullPath(keyedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fullFile = Path.GetFullPath(file);
                if (!fullFile.StartsWith(fullKeyedRoot, StringComparison.OrdinalIgnoreCase)) return "";

                string relative = fullFile.Substring(fullKeyedRoot.Length).Replace('\\', '/');
                string[] parts = relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return "";

                string keyedFileName = Path.GetFileNameWithoutExtension(parts[parts.Length - 1]);
                foreach (string packagePrefix in activePackagePrefixes)
                {
                    if (keyedFileName.StartsWith(packagePrefix + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        return packagePrefix;
                    }
                }

                return "";
            }
            catch
            {
                return "";
            }
        }

        private static Dictionary<string, string> BuildModKeyedSources(ModMetaData mod)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string langRoot in GetAllEffectiveLangPaths(mod))
            {
                foreach (string keyedSourcePath in GetTranslatableLanguageBucketPaths(langRoot, AutoTranslatorMod.Settings.TargetLang, "Keyed", true))
                {
                    AddKeyedSource(keyedSourcePath, result);
                }
            }

            return result;
        }

        private static void AddKeyedSource(string path, Dictionary<string, string> result)
        {
            if (string.IsNullOrEmpty(path) || result == null || !Directory.Exists(path)) return;

            foreach (string file in GetXmlFilesCached(path, SearchOption.AllDirectories))
            {
                foreach (var pair in LoadXmlFileToDict(file))
                {
                    if (!result.ContainsKey(pair.Key))
                    {
                        result[pair.Key] = pair.Value;
                    }
                }
            }
        }

        private static Dictionary<string, Dictionary<string, string>> BuildModDefSources(ModMetaData mod)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string defsRoot in GetAllEffectiveDefsPaths(mod))
            {
                MergeDefSources(result, ExtractEnglishFromRawDefs(defsRoot));
            }

            foreach (string langRoot in GetAllEffectiveLangPaths(mod))
            {
                foreach (string defSourcePath in GetTranslatableLanguageBucketPaths(langRoot, AutoTranslatorMod.Settings.TargetLang, "DefInjected", true))
                {
                    AddDefInjectedSource(defSourcePath, result);
                }
            }

            return result;
        }

        private static void AddDefInjectedSource(string path, Dictionary<string, Dictionary<string, string>> result)
        {
            if (string.IsNullOrEmpty(path) || result == null || !Directory.Exists(path)) return;

            foreach (string typeDir in Directory.GetDirectories(path))
            {
                string defType = Path.GetFileName(typeDir);
                if (string.IsNullOrWhiteSpace(defType)) continue;
                if (!result.TryGetValue(defType, out Dictionary<string, string> typeDict))
                {
                    typeDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[defType] = typeDict;
                }

                foreach (string file in GetXmlFilesCached(typeDir, SearchOption.AllDirectories))
                {
                    foreach (var pair in LoadXmlFileToDict(file))
                    {
                        if (!typeDict.ContainsKey(pair.Key))
                        {
                            typeDict[pair.Key] = pair.Value;
                        }
                    }
                }
            }

            if (!result.TryGetValue("General", out Dictionary<string, string> generalDict))
            {
                generalDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result["General"] = generalDict;
            }

            foreach (string file in GetXmlFilesCached(path, SearchOption.TopDirectoryOnly))
            {
                foreach (var pair in LoadXmlFileToDict(file))
                {
                    if (!generalDict.ContainsKey(pair.Key))
                    {
                        generalDict[pair.Key] = pair.Value;
                    }
                }
            }
        }

        private static void MergeDefSources(
            Dictionary<string, Dictionary<string, string>> target,
            Dictionary<string, Dictionary<string, string>> source)
        {
            if (target == null || source == null) return;

            foreach (var typePair in source)
            {
                if (!target.TryGetValue(typePair.Key, out Dictionary<string, string> targetType))
                {
                    targetType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    target[typePair.Key] = targetType;
                }

                foreach (var pair in typePair.Value)
                {
                    if (!targetType.ContainsKey(pair.Key))
                    {
                        targetType[pair.Key] = pair.Value;
                    }
                }
            }
        }

        private static int RepairGeneratedKeyedPlaceholders(
            string languageRoot,
            string packagePrefix,
            Dictionary<string, string> keyedSources,
            ref int fixedFiles)
        {
            if (keyedSources == null || keyedSources.Count == 0) return 0;

            string keyedDir = Path.Combine(languageRoot, "Keyed");
            if (!Directory.Exists(keyedDir)) return 0;

            int fixedEntries = 0;
            foreach (string file in GetXmlFilesCached(keyedDir, SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).StartsWith(packagePrefix + "_", StringComparison.OrdinalIgnoreCase)))
            {
                fixedEntries += RepairPlaceholderFile(file, key => keyedSources.TryGetValue(key, out string source) ? source : null, ref fixedFiles);
            }

            return fixedEntries;
        }

        private static int RepairGeneratedDefPlaceholders(
            string languageRoot,
            string packagePrefix,
            Dictionary<string, Dictionary<string, string>> defSources,
            ref int fixedFiles)
        {
            if (defSources == null || defSources.Count == 0) return 0;

            string defInjectedDir = Path.Combine(languageRoot, "DefInjected");
            if (!Directory.Exists(defInjectedDir)) return 0;

            int fixedEntries = 0;
            foreach (string typeDir in Directory.GetDirectories(defInjectedDir))
            {
                string defType = Path.GetFileName(typeDir);
                if (!defSources.TryGetValue(defType, out Dictionary<string, string> sourcesForType)) continue;

                string file = Path.Combine(typeDir, packagePrefix + "_AutoTranslated.xml");
                if (!File.Exists(file)) continue;

                fixedEntries += RepairPlaceholderFile(file, key => sourcesForType.TryGetValue(key, out string source) ? source : null, ref fixedFiles);
            }

            return fixedEntries;
        }

        private static int RepairPlaceholderFile(string file, Func<string, string> getSourceText, ref int fixedFiles)
        {
            if (string.IsNullOrEmpty(file) || getSourceText == null || !File.Exists(file)) return 0;

            var data = LoadXmlFileToDict(file);
            if (data.Count == 0) return 0;

            bool changed = false;
            int fixedEntries = 0;
            var repaired = new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);

            foreach (var pair in data)
            {
                string sourceText = getSourceText(pair.Key);
                if (string.IsNullOrWhiteSpace(sourceText) || !RequiresProtectedTokenParity(sourceText)) continue;

                string fixedValue = SanitizeTranslationResult(pair.Value, sourceText);
                if (string.Equals(fixedValue, pair.Value, StringComparison.Ordinal)) continue;
                if (HasProtectedTokenMismatch(fixedValue, sourceText)) continue;

                repaired[pair.Key] = fixedValue;
                changed = true;
                fixedEntries++;
            }

            if (changed)
            {
                SaveXml(file, repaired);
                fixedFiles++;
            }

            return fixedEntries;
        }
    }
}
