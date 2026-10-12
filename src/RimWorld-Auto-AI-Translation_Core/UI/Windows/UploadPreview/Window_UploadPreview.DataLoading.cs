using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責上傳預覽資料載入。
// EN: This file loads upload preview data.

namespace AutoTranslator_Core
{
        // 這個類別負責 視窗上傳Preview 的主要流程與狀態。
        // EN: This class manages the main workflow and state for Window_UploadPreview.
        public partial class Window_UploadPreview : Window
        {

            // 這個方法負責讀取 Preview資料 資料。
            // EN: This method loads preview data.
            private void LoadPreviewData()
            {
                var resultData = new Dictionary<string, List<PreviewItem>>();
                Workflow.UploadClassificationSummary classification = null;
                string classificationError = null;
                try
                {
                    classification = Workflow.WorkflowBackendRuntime.GetOrCreate()
                        .EvaluateUploadClassification(_sourceDir, _mod.PackageId, _targetLangFolder);
                }
                catch (Exception ex) { classificationError = ex.Message; }


                string id1 = _mod.PackageId.ToLower();
                string id2 = _mod.PackageId.Replace(".", "_").ToLower();

                bool isWorkspace = _sourceDir.Contains("Upload_Workspace");

                if (Directory.Exists(_sourceDir))
                {

                    string keyedDir = Path.Combine(_sourceDir, "Keyed");
                    if (Directory.Exists(keyedDir))
                    {
                        var list = new List<PreviewItem>();
                        foreach (var file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(keyedDir, SearchOption.AllDirectories))
                        {
                            if (!AutoTranslatorScanner.ShouldIncludeUploadFile(_sourceDir, file, _mod.PackageId)) continue;
                            string fileName = Path.GetFileName(file).ToLower();
                            bool isValid = isWorkspace || fileName.StartsWith(id1 + "_") || fileName.StartsWith(id1 + ".") || fileName.StartsWith(id2 + "_") || fileName.StartsWith(id2 + ".");

                            if (isValid)
                            {
                                var dict = AutoTranslatorScanner.LoadXmlFileToDict(file);
                                foreach (var kv in dict)
                                    list.Add(new PreviewItem { Key = kv.Key, SourceFile = file, OriginalText = "ATC_Preview_ClickToSee".Translate(), TranslatedText = kv.Value });
                            }
                        }
                        if (list.Count > 0) resultData["Keyed"] = list;
                    }


                    string defBaseDir = Path.Combine(_sourceDir, "DefInjected");
                    if (Directory.Exists(defBaseDir))
                    {
                        foreach (var typeDir in Directory.GetDirectories(defBaseDir))
                        {
                            string defType = Path.GetFileName(typeDir);
                            var list = new List<PreviewItem>();


                            foreach (var file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(typeDir, SearchOption.AllDirectories))
                            {
                                if (!AutoTranslatorScanner.ShouldIncludeUploadFile(_sourceDir, file, _mod.PackageId)) continue;
                                string fileName = Path.GetFileName(file).ToLower();
                                bool isValid = isWorkspace || fileName.StartsWith(id1 + "_") || fileName.StartsWith(id1 + ".") || fileName.StartsWith(id2 + "_") || fileName.StartsWith(id2 + ".");

                                if (isValid)
                                {
                                    var dict = AutoTranslatorScanner.LoadXmlFileToDict(file);
                                    foreach (var kv in dict)
                                        list.Add(new PreviewItem { Key = kv.Key, SourceFile = file, OriginalText = "ATC_Preview_ClickToSee".Translate(), TranslatedText = kv.Value });
                                }
                            }
                            if (list.Count > 0) resultData[defType] = list;
                        }
                    }
                }

                ATC_Dispatcher.RunOnMainThread(() => {
                    _uploadClassification = classification;
                    _uploadClassificationError = classificationError;
                    _categorizedData = resultData;
                    _selectedCategory = _categorizedData.Keys.FirstOrDefault() ?? "";
                    _isLoading = false;
                });
            }
        }
}
