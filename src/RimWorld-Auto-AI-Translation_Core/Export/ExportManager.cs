using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimWorld;
// 這個檔案負責實際導出檔案的組裝與輸出。
// EN: This file assembles and writes exported translation files.

namespace AutoTranslator_Core
{
    // 這個類別負責 導出管理器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for ExportManager.
    public static class ExportManager
    {
        // 這個方法負責執行 導出 動作。
        // EN: This method executes export.
        public static void ExecuteExport(List<ExportableModInfo> mods)
        {
            List<ExportableModInfo> exportMods = mods != null
                ? mods.Select(CloneExportableModInfo).ToList()
                : new List<ExportableModInfo>();
            string targetFolder = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
            string sourceLangsRoot = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Languages");
            string eulaAcceptedTimestamp = AutoTranslatorMod.Settings.EulaAcceptedTimestamp;
            string eulaAcceptedVersion = AutoTranslatorMod.Settings.EulaAcceptedVersion;
            int eulaAcceptCount = AutoTranslatorMod.Settings.EulaAcceptCount;
            string readmeContent = ExportTemplates.GetReadme(exportMods);
            string consentContent = ExportTemplates.GetConsentRecord(
                eulaAcceptedTimestamp,
                eulaAcceptedVersion,
                eulaAcceptCount);

            AutoTranslatorSettings.AddLog("ATC_Log_ExportStart".Translate(exportMods.Count));

            Task.Run(() =>
            {
                ExportWorkerResult result = new ExportWorkerResult();

                try
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    result.ExportRoot = Path.Combine(desktopPath, $"RimWorld_Translations_{timestamp}");

                    Directory.CreateDirectory(result.ExportRoot);
                    WriteReadme(result.ExportRoot, readmeContent);
                    WriteConsentRecord(result.ExportRoot, consentContent);

                    foreach (var mod in exportMods)
                    {
                        result.TotalFiles += ExportSingleMod(mod, result.ExportRoot, targetFolder, sourceLangsRoot);
                    }
                }
                catch (Exception ex)
                {
                    result.Error = ex;
                }

                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    if (result.Error != null)
                    {
                        AutoTranslatorSettings.AddErrorLog("ATC_Log_ExportFailed".Translate(result.Error.Message));
                        Messages.Message("ATC_Export_Failed_Message".Translate(result.Error.Message),
                            MessageTypeDefOf.RejectInput, false);
                        Log.Error($"[AutoTranslationCore] Export failed: {result.Error}");
                        return;
                    }

                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = result.ExportRoot,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception openEx)
                    {
                        Log.Warning($"[AutoTranslationCore] Cannot open folder: {openEx.Message}");
                    }

                    AutoTranslatorSettings.AddLog("ATC_Log_ExportComplete".Translate(exportMods.Count, result.TotalFiles));


                    ExportCooldownManager.RecordExport();


                    ShowSuccessDialogWithContactOption(exportMods, result.TotalFiles, result.ExportRoot);
                });
            });
        }

        private class ExportWorkerResult
        {
            public string ExportRoot;
            public int TotalFiles;
            public Exception Error;
        }

        private static ExportableModInfo CloneExportableModInfo(ExportableModInfo source)
        {
            if (source == null) return new ExportableModInfo();
            return new ExportableModInfo
            {
                ModName = source.ModName,
                PackageId = source.PackageId,
                PackageIdWithUnderscore = source.PackageIdWithUnderscore,
                ModRootDir = source.ModRootDir,
                DefInjectedCount = source.DefInjectedCount,
                KeyedCount = source.KeyedCount
            };
        }


        private static void ShowSuccessDialogWithContactOption(
            List<ExportableModInfo> mods, int totalFiles, string exportPath)
        {
            string message = "ATC_Export_Success_Message".Translate(mods.Count, totalFiles, exportPath);
            string title = "ATC_Export_Success_Title".Translate();

            Find.WindowStack.Add(new Window_AtcDialog(
                text: message,
                buttonAText: "ATC_Export_Success_ContactAuthorBtn".Translate(),
                buttonAAction: () =>
                {
                    Find.WindowStack.Add(new Dialog_ContactAuthor(mods));
                },
                buttonBText: "ATC_Export_Success_CloseBtn".Translate(),
                buttonBAction: null,
                title: title
            ));
        }


        // 這個方法負責處理 導出Single模組 相關流程。
        // EN: This method handles export single mod.
        private static int ExportSingleMod(ExportableModInfo mod, string exportRoot, string targetFolder, string sourceLangsRoot)
        {
            int fileCount = 0;


            string safeModName = MakeSafeFolderName(mod.ModName);
            string modExportRoot = Path.Combine(exportRoot, $"AutoTrans_{safeModName}");
            string targetLangPath = Path.Combine(modExportRoot, "1.6", "Languages", targetFolder);

            Directory.CreateDirectory(targetLangPath);


            foreach (var langDir in Directory.GetDirectories(sourceLangsRoot))
            {
                string defInjectedDir = Path.Combine(langDir, "DefInjected");
                if (!Directory.Exists(defInjectedDir)) continue;

                foreach (var typeDir in Directory.GetDirectories(defInjectedDir))
                {
                    string defType = Path.GetFileName(typeDir);
                    foreach (var file in Directory.GetFiles(typeDir, "*.xml"))
                    {
                        if (!IsFileForThisMod(file, mod)) continue;

                        string targetTypeDir = Path.Combine(targetLangPath, "DefInjected", defType);
                        Directory.CreateDirectory(targetTypeDir);
                        string targetFile = Path.Combine(targetTypeDir, Path.GetFileName(file));
                        WriteFileWithWatermark(file, targetFile, mod);
                        fileCount++;
                    }
                }


                string keyedDir = Path.Combine(langDir, "Keyed");
                if (Directory.Exists(keyedDir))
                {
                    foreach (var file in Directory.GetFiles(keyedDir, "*.xml"))
                    {
                        if (!IsFileForThisMod(file, mod)) continue;

                        string targetKeyedDir = Path.Combine(targetLangPath, "Keyed");
                        Directory.CreateDirectory(targetKeyedDir);
                        string targetFile = Path.Combine(targetKeyedDir, Path.GetFileName(file));
                        WriteFileWithWatermark(file, targetFile, mod);
                        fileCount++;
                    }
                }
            }


            WriteAboutXml(modExportRoot, mod);
            fileCount++;


            WriteLoadFoldersXml(modExportRoot);
            fileCount++;

            return fileCount;
        }


        // 這個方法負責判斷 IsFileForThis模組 條件是否成立。
        // EN: This method checks is file for this mod.
        private static bool IsFileForThisMod(string filePath, ExportableModInfo mod)
        {
            string fileName = Path.GetFileName(filePath).ToLower();
            string id1 = mod.PackageId.ToLower();
            string id2 = mod.PackageIdWithUnderscore.ToLower();
            return fileName.StartsWith(id1 + "_") || fileName.StartsWith(id1 + ".") ||
                   fileName.StartsWith(id2 + "_") || fileName.StartsWith(id2 + ".");
        }


        // 這個方法負責保存 FileWithWatermark 資料。
        // EN: This method saves file with watermark.
        private static void WriteFileWithWatermark(string sourceFile, string targetFile, ExportableModInfo mod)
        {
            string content = File.ReadAllText(sourceFile, Encoding.UTF8);
            string watermark = ExportTemplates.GetXmlWatermark(mod);


            int declarationEnd = content.IndexOf("?>");
            string result;
            if (declarationEnd > 0)
            {
                int insertPos = declarationEnd + 2;
                result = content.Substring(0, insertPos) + "\n" + watermark + content.Substring(insertPos);
            }
            else
            {

                result = watermark + content;
            }

            File.WriteAllText(targetFile, result, Encoding.UTF8);
        }

        // 這個方法負責保存 AboutXml 資料。
        // EN: This method saves about XML.
        private static void WriteAboutXml(string modExportRoot, ExportableModInfo mod)
        {
            string aboutDir = Path.Combine(modExportRoot, "About");
            Directory.CreateDirectory(aboutDir);
            string aboutPath = Path.Combine(aboutDir, "About.xml");

            string content = ExportTemplates.GetAboutXml(mod);
            File.WriteAllText(aboutPath, content, Encoding.UTF8);
        }

        // 這個方法負責保存 LoadFoldersXml 資料。
        // EN: This method saves load folders XML.
        private static void WriteLoadFoldersXml(string modExportRoot)
        {
            string path = Path.Combine(modExportRoot, "LoadFolders.xml");
            string content = @"<?xml version=""1.0"" encoding=""utf-8""?>
<loadFolders>
  <v1.6>
    <li>1.6</li>
  </v1.6>
</loadFolders>";
            File.WriteAllText(path, content, Encoding.UTF8);
        }

        // 這個方法負責保存 Readme 資料。
        // EN: This method saves readme.
        private static void WriteReadme(string exportRoot, string content)
        {
            string path = Path.Combine(exportRoot, "README_IMPORTANT.txt");
            File.WriteAllText(path, content, Encoding.UTF8);
        }

        // 這個方法負責保存 ConsentRecord 資料。
        // EN: This method saves consent record.
        private static void WriteConsentRecord(string exportRoot, string content)
        {
            string path = Path.Combine(exportRoot, "EULA_Consent_Record.txt");
            File.WriteAllText(path, content, Encoding.UTF8);
        }


        // 這個方法負責處理 MakeSafeFolder名稱 相關流程。
        // EN: This method handles make safe folder name.
        private static string MakeSafeFolderName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                sb.Append(invalid.Contains(c) ? '_' : c);
            }
            string result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "UnnamedMod" : result;
        }
    }
}
