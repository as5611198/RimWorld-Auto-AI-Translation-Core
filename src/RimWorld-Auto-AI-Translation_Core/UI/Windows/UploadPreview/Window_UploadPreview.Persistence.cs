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
// 這個檔案負責上傳預覽的保存與還原。
// EN: This file saves and restores upload preview edits.

namespace AutoTranslator_Core
{
        // 這個類別負責 視窗上傳Preview 的主要流程與狀態。
        // EN: This class manages the main workflow and state for Window_UploadPreview.
        public partial class Window_UploadPreview : Window
        {

        // 這個方法負責保存 ChangesIfAny 資料。
        // EN: This method saves changes if any.
        private sealed class UploadPreviewSaveSnapshot
        {
            public string SourceDir;
            public string PackageId;
            public List<UploadPreviewSaveCategorySnapshot> Categories = new List<UploadPreviewSaveCategorySnapshot>();
        }

        private sealed class UploadPreviewSaveCategorySnapshot
        {
            public string Category;
            public List<UploadPreviewSaveItemSnapshot> Items = new List<UploadPreviewSaveItemSnapshot>();
        }

        private sealed class UploadPreviewSaveItemSnapshot
        {
            public string Key;
            public string TranslatedText;
        }

        private sealed class UploadPreviewSaveResult
        {
            public int SaveCount;
            public string Error;
        }

        private void SaveChangesThenUpload()
        {
            if (_isSavingChanges) return;

            UploadPreviewSaveSnapshot snapshot = CreateSaveSnapshot();
            if (snapshot.Categories.Count == 0)
            {
                ExecuteActualUpload();
                Close();
                return;
            }

            _isSavingChanges = true;
            Task.Run(() =>
            {
                UploadPreviewSaveResult result = SaveSnapshot(snapshot);
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _isSavingChanges = false;
                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        Messages.Message(result.Error, MessageTypeDefOf.RejectInput, false);
                        return;
                    }

                    MarkSavedSnapshotItems(snapshot);
                    if (result.SaveCount > 0)
                    {
                        AutoTranslatorScanner.RequestMemoryDrop();
                        UIInterceptor.ClearUICache();
                    }

                    ExecuteActualUpload();
                    Close();
                });
            });
        }

        private UploadPreviewSaveSnapshot CreateSaveSnapshot()
        {
            UploadPreviewSaveSnapshot snapshot = new UploadPreviewSaveSnapshot
            {
                SourceDir = _sourceDir,
                PackageId = _mod != null ? _mod.PackageId : ""
            };

            foreach (var pair in _categorizedData)
            {
                List<UploadPreviewSaveItemSnapshot> modified = pair.Value
                    .Where(i => i != null && i.IsModified)
                    .Select(i => new UploadPreviewSaveItemSnapshot
                    {
                        Key = i.Key,
                        TranslatedText = i.TranslatedText
                    })
                    .ToList();

                if (modified.Count == 0) continue;
                snapshot.Categories.Add(new UploadPreviewSaveCategorySnapshot
                {
                    Category = pair.Key,
                    Items = modified
                });
            }

            return snapshot;
        }

        private static UploadPreviewSaveResult SaveSnapshot(UploadPreviewSaveSnapshot snapshot)
        {
            UploadPreviewSaveResult result = new UploadPreviewSaveResult();
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.SourceDir) || string.IsNullOrWhiteSpace(snapshot.PackageId)) return result;

            try
            {
                string cleanPackageId = snapshot.PackageId.Replace(".", "_").ToLower();
                foreach (UploadPreviewSaveCategorySnapshot category in snapshot.Categories)
                {
                    if (category == null || category.Items == null || category.Items.Count == 0) continue;

                    string fileDir = category.Category == "Keyed"
                        ? Path.Combine(snapshot.SourceDir, "Keyed")
                        : Path.Combine(snapshot.SourceDir, "DefInjected", category.Category);

                    Directory.CreateDirectory(fileDir);
                    string targetFile = Path.Combine(fileDir, $"{cleanPackageId}_AutoTranslated.xml");
                    Dictionary<string, string> existing = AutoTranslatorScanner.LoadXmlFileToDict(targetFile);

                    foreach (UploadPreviewSaveItemSnapshot item in category.Items)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.Key)) continue;
                        existing[item.Key] = item.TranslatedText;
                        result.SaveCount++;
                    }

                    AutoTranslatorScanner.SaveXml(targetFile, existing);
                    AutoTranslatorScanner.SaveProvenanceForFile(
                        snapshot.SourceDir,
                        snapshot.PackageId,
                        targetFile,
                        existing,
                        BuildManualEditProvenance(snapshot, category, targetFile, existing));
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Log.Warning($"[AutoTranslationCore] Upload preview background save failed: {ex}");
            }

            return result;
        }

        private static Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> BuildManualEditProvenance(
            UploadPreviewSaveSnapshot snapshot,
            UploadPreviewSaveCategorySnapshot category,
            string targetFile,
            Dictionary<string, string> savedData)
        {
            Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> result =
                new Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
            if (snapshot == null || category == null || category.Items == null || savedData == null) return result;

            HashSet<string> modifiedKeys = new HashSet<string>(
                category.Items
                    .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Key))
                    .Select(i => i.Key),
                StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> pair in savedData)
            {
                if (!modifiedKeys.Contains(pair.Key)) continue;

                result[pair.Key] = AutoTranslatorScanner.CreateProvenance(
                    AutoTranslatorScanner.ProvenanceKindManualEdit,
                    snapshot.PackageId,
                    "",
                    targetFile,
                    "",
                    pair.Value);
            }

            return result;
        }

        private void MarkSavedSnapshotItems(UploadPreviewSaveSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Categories == null) return;

            foreach (UploadPreviewSaveCategorySnapshot category in snapshot.Categories)
            {
                if (category == null || !_categorizedData.TryGetValue(category.Category, out List<PreviewItem> currentItems)) continue;
                HashSet<string> savedKeys = new HashSet<string>(
                    category.Items.Where(i => i != null && !string.IsNullOrWhiteSpace(i.Key)).Select(i => i.Key),
                    StringComparer.OrdinalIgnoreCase);

                foreach (PreviewItem item in currentItems)
                {
                    if (item != null && savedKeys.Contains(item.Key ?? ""))
                    {
                        item.IsModified = false;
                    }
                }
            }
        }

        }
}
