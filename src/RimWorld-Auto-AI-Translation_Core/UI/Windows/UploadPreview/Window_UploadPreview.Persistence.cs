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
using AutoTranslator_Core.Workflow;
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
            public string TargetLanguage;
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
            public string SourceFile;
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
            Task.Run(async () =>
            {
                UploadPreviewSaveResult result = await SaveSnapshotAsync(snapshot);
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _isSavingChanges = false;
                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        Messages.Message(result.Error, MessageTypeDefOf.RejectInput, false);
                        return;
                    }

                    MarkSavedSnapshotItems(snapshot);
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
                PackageId = _mod != null ? _mod.PackageId : "",
                TargetLanguage = _targetLangFolder
            };

            foreach (var pair in _categorizedData)
            {
                List<UploadPreviewSaveItemSnapshot> modified = pair.Value
                    .Where(i => i != null && i.IsModified)
                    .Select(i => new UploadPreviewSaveItemSnapshot
                    {
                        Key = i.Key,
                        SourceFile = i.SourceFile,
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

        private static async Task<UploadPreviewSaveResult> SaveSnapshotAsync(UploadPreviewSaveSnapshot snapshot)
        {
            UploadPreviewSaveResult result = new UploadPreviewSaveResult();
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.SourceDir) || string.IsNullOrWhiteSpace(snapshot.PackageId)) return result;

            try
            {
                List<UploadPreviewTranslationEdit> edits = snapshot.Categories
                    .SelectMany(category => category.Items)
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key))
                    .Select(item => new UploadPreviewTranslationEdit
                    {
                        File = item.SourceFile, Key = item.Key, Text = item.TranslatedText
                    }).ToList();
                if (edits.Any(item => string.IsNullOrWhiteSpace(item.File)))
                    throw new InvalidOperationException("Upload preview source file is missing.");
                await WorkflowBackendRuntime.GetOrCreate().SaveUploadPreviewEditsAsync(
                    snapshot.SourceDir, snapshot.PackageId, snapshot.TargetLanguage, edits);
                result.SaveCount = edits.Count;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Log.Warning($"[AutoTranslationCore] Upload preview background save failed: {ex}");
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
