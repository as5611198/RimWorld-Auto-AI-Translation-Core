using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;

namespace AutoTranslator_Core
{
        public static partial class TranslationWorkbenchTab
        {
            private static void SaveModifications()
            {
                if (_editingMod == null) return;
                if (_isSavingModifications) return;

                WorkbenchSaveSnapshot snapshot = CreateSaveSnapshot();
                if (snapshot.Categories.Count == 0)
                {
                    AutoTranslatorSettings.AddLog("? " + "ATC_Log_WorkbenchSaved".Translate(0));
                    Verse.Messages.Message("ATC_Workbench_SaveSuccess".Translate(), MessageTypeDefOf.PositiveEvent, false);
                    return;
                }

                _isSavingModifications = true;
                SetWorkbenchStatus("ATC_Workbench_Saving".Translate().ToString());
                Task.Run(() =>
                {
                    WorkbenchSaveResult result = SaveSnapshot(snapshot);
                    ATC_Dispatcher.RunOnMainThread(() => CompleteSave(snapshot, result));
                });
            }

            private static WorkbenchSaveSnapshot CreateSaveSnapshot()
            {
                string targetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
                string packPath = AutoTranslatorScanner.GetLocalPackPath();
                string cleanPackageId = _editingMod.PackageId.Replace(".", "_").ToLower();
                WorkbenchSaveSnapshot snapshot = new WorkbenchSaveSnapshot
                {
                    Mod = _editingMod,
                    PackageId = _editingMod.PackageId,
                    RootDir = _editingMod.RootDir != null ? _editingMod.RootDir.FullName : "",
                    TargetLang = AutoTranslatorMod.Settings.TargetLang,
                    TargetLangFolder = targetLangFolder,
                    PackPath = packPath,
                    CleanPackageId = cleanPackageId
                };

                foreach (var categoryPair in _categorizedData)
                {
                    bool categoryModified = categoryPair.Value.Any(HasWorkbenchItemUnsavedChanges);
                    if (!categoryModified) continue;

                    WorkbenchSaveCategorySnapshot category = new WorkbenchSaveCategorySnapshot
                    {
                        Category = categoryPair.Key,
                        ClearKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    };

                    foreach (WorkbenchItem item in categoryPair.Value)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.Key)) continue;
                        WorkbenchSaveItemSnapshot snapshotItem = new WorkbenchSaveItemSnapshot
                        {
                            Key = item.Key,
                            OriginalText = item.OriginalText,
                            TranslatedText = item.TranslatedText,
                            OriginalTranslatedText = item.OriginalTranslatedText,
                            OriginalTranslatedTextIsReadOnlyReference = item.OriginalTranslatedTextIsReadOnlyReference,
                            SavedTranslatedText = item.SavedTranslatedText,
                            SavedTranslatedTextIsReadOnlyReference = item.SavedTranslatedTextIsReadOnlyReference,
                            IsModified = HasWorkbenchItemUnsavedChanges(item)
                        };

                        if (snapshotItem.IsModified && !ShouldSaveWorkbenchSnapshotItem(snapshotItem))
                        {
                            category.ClearKeys.Add(snapshotItem.Key);
                        }

                        category.Items.Add(snapshotItem);
                    }

                    snapshot.Categories.Add(category);
                }

                return snapshot;
            }

            private static WorkbenchSaveResult SaveSnapshot(WorkbenchSaveSnapshot snapshot)
            {
                WorkbenchSaveResult result = new WorkbenchSaveResult();
                if (snapshot == null) return result;

                string workspaceBaseDir = Path.Combine(snapshot.PackPath, "Upload_Workspace");

                try
                {
                    foreach (WorkbenchSaveCategorySnapshot category in snapshot.Categories)
                    {
                        if (category == null) continue;
                        result.ClearKeysByDefType[category.Category] =
                            new HashSet<string>(category.ClearKeys, StringComparer.OrdinalIgnoreCase);

                        string targetDir = category.Category == "Keyed"
                            ? Path.Combine(snapshot.PackPath, "Languages", snapshot.TargetLangFolder, "Keyed")
                            : Path.Combine(snapshot.PackPath, "Languages", snapshot.TargetLangFolder, "DefInjected", category.Category);
                        string workspaceDir = category.Category == "Keyed"
                            ? Path.Combine(workspaceBaseDir, snapshot.PackageId, snapshot.TargetLangFolder, "Keyed")
                            : Path.Combine(workspaceBaseDir, snapshot.PackageId, snapshot.TargetLangFolder, "DefInjected", category.Category);

                        Directory.CreateDirectory(targetDir);
                        Directory.CreateDirectory(workspaceDir);

                        foreach (string oldFile in AutoTranslatorScanner.GetXmlFilesForTranslationCache(workspaceDir, SearchOption.TopDirectoryOnly))
                        {
                            File.Delete(oldFile);
                            AutoTranslatorScanner.NotifyTranslationFileChanged(oldFile);
                            result.TouchedTranslationFiles = true;
                        }

                        foreach (string oldFile in AutoTranslatorScanner.GetXmlFilesForTranslationCache(targetDir, SearchOption.TopDirectoryOnly))
                        {
                            if (Path.GetFileName(oldFile).ToLower().Contains(snapshot.CleanPackageId))
                            {
                                File.SetAttributes(oldFile, FileAttributes.Normal);
                                File.Delete(oldFile);
                                AutoTranslatorScanner.NotifyTranslationFileChanged(oldFile);
                                result.TouchedTranslationFiles = true;
                            }
                        }

                        string targetFile = Path.Combine(targetDir, $"{snapshot.CleanPackageId}_AutoTranslated.xml");
                        string workspaceFile = Path.Combine(workspaceDir, $"{snapshot.CleanPackageId}_AutoTranslated.xml");

                        Dictionary<string, string> fullDictToSave = new Dictionary<string, string>();
                        foreach (WorkbenchSaveItemSnapshot item in category.Items)
                        {
                            if (ShouldSaveWorkbenchSnapshotItem(item))
                            {
                                fullDictToSave[item.Key] = item.TranslatedText;
                            }
                            if (item.IsModified) result.SavedCount++;
                        }

                        if (fullDictToSave.Count > 0)
                        {
                            AutoTranslatorScanner.SaveXml(targetFile, fullDictToSave);
                            AutoTranslatorScanner.SaveProvenanceForFile(
                                Path.Combine(snapshot.PackPath, "Languages", snapshot.TargetLangFolder),
                                snapshot.PackageId,
                                targetFile,
                                fullDictToSave,
                                BuildManualEditProvenance(snapshot, category, targetFile, fullDictToSave));
                            AutoTranslatorScanner.SaveXml(workspaceFile, fullDictToSave);
                            AutoTranslatorScanner.SaveProvenanceForFile(
                                Path.Combine(workspaceBaseDir, snapshot.PackageId, snapshot.TargetLangFolder),
                                snapshot.PackageId,
                                workspaceFile,
                                fullDictToSave,
                                BuildManualEditProvenance(snapshot, category, workspaceFile, fullDictToSave));
                            result.TouchedTranslationFiles = true;
                        }
                    }

                    UpdateWorkbenchRestoreBaselines(snapshot);
                    result.HasSavedTranslation = HasAnySavedTranslationForCurrentMod(snapshot.TargetLangFolder, snapshot.CleanPackageId);
                    result.SourceFingerprint = ModUpdateDetector.BuildSourceFingerprintSnapshot(
                        snapshot.PackageId,
                        snapshot.RootDir,
                        snapshot.TargetLang);
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                    Verse.Log.Warning($"[AutoTranslationCore] Workbench background save failed: {ex}");
                }

                return result;
            }

            private static bool ShouldSaveWorkbenchSnapshotItem(WorkbenchSaveItemSnapshot item)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.TranslatedText)) return false;

                bool revertedToReadOnlyOriginal =
                    item.OriginalTranslatedTextIsReadOnlyReference &&
                    SameWorkbenchText(item.TranslatedText, item.OriginalTranslatedText);
                if (revertedToReadOnlyOriginal) return false;

                bool unchangedReadOnlyReference =
                    item.SavedTranslatedTextIsReadOnlyReference &&
                    !item.IsModified &&
                    SameWorkbenchText(item.TranslatedText, item.SavedTranslatedText);
                return !unchangedReadOnlyReference;
            }

            private static Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> BuildManualEditProvenance(
                WorkbenchSaveSnapshot snapshot,
                WorkbenchSaveCategorySnapshot category,
                string targetFile,
                Dictionary<string, string> savedData)
            {
                Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> result =
                    new Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
                if (snapshot == null || category == null || category.Items == null || savedData == null) return result;

                HashSet<string> modifiedKeys = new HashSet<string>(
                    category.Items
                        .Where(i => i != null && i.IsModified && !string.IsNullOrWhiteSpace(i.Key))
                        .Select(i => i.Key),
                    StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, string> pair in savedData)
                {
                    if (modifiedKeys.Contains(pair.Key))
                    {
                        result[pair.Key] = AutoTranslatorScanner.CreateProvenance(
                            AutoTranslatorScanner.ProvenanceKindManualEdit,
                            snapshot.PackageId,
                            snapshot.Mod != null ? snapshot.Mod.Name : "",
                            targetFile,
                            snapshot.TargetLangFolder,
                            pair.Value);
                    }
                }

                return result;
            }

            private static void CompleteSave(WorkbenchSaveSnapshot snapshot, WorkbenchSaveResult result)
            {
                _isSavingModifications = false;
                if (result == null) result = new WorkbenchSaveResult();

                if (!string.IsNullOrEmpty(result.Error))
                {
                    SetWorkbenchStatus(result.Error);
                    Verse.Messages.Message(result.Error, MessageTypeDefOf.RejectInput, false);
                    return;
                }

                MarkSavedSnapshotItems(snapshot);
                SetWorkbenchStatus("ATC_Workbench_SavedInline".Translate().ToString());
                AutoTranslatorSettings.AddLog("? " + "ATC_Log_WorkbenchSaved".Translate(result.SavedCount));
                Verse.Messages.Message("ATC_Workbench_SaveSuccess".Translate(), MessageTypeDefOf.PositiveEvent, false);

                if (result.TouchedTranslationFiles)
                {
                    AutoTranslatorScanner.RequestMemoryDropForPackage(snapshot.PackageId, result.ClearKeysByDefType);
                    UIInterceptor.RefreshRuntimeUICache();
                }

                InitTranslatedModsCache();
                if (result.HasSavedTranslation)
                {
                    MarkPackageTranslated(snapshot.PackageId);
                    ModUpdateDetector.MarkModAsTranslatedSnapshot(snapshot.PackageId, result.SourceFingerprint);
                }
            }

            private static void MarkSavedSnapshotItems(WorkbenchSaveSnapshot snapshot)
            {
                if (snapshot == null || snapshot.Categories == null) return;

                foreach (WorkbenchSaveCategorySnapshot category in snapshot.Categories)
                {
                    if (category == null || !_categorizedData.TryGetValue(category.Category, out List<WorkbenchItem> currentItems)) continue;
                    Dictionary<string, WorkbenchSaveItemSnapshot> snapshotItems = category.Items
                        .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Key))
                        .GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                    foreach (WorkbenchItem item in currentItems)
                    {
                        if (item == null || !snapshotItems.TryGetValue(item.Key ?? "", out WorkbenchSaveItemSnapshot savedItem)) continue;
                        item.IsModified = false;
                        item.SavedTranslatedText = item.TranslatedText;
                        item.SavedTranslatedTextIsReadOnlyReference =
                            savedItem.OriginalTranslatedTextIsReadOnlyReference &&
                            SameWorkbenchText(savedItem.TranslatedText, savedItem.OriginalTranslatedText);
                    }
                }

                _categorizedDataVersion++;
                InvalidateVisibleItemCache();
            }

            private static bool HasAnySavedTranslationForCurrentMod(string targetLangFolder, string cleanPackageId)
            {
                string langRoot = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Languages", targetLangFolder);
                if (!Directory.Exists(langRoot)) return false;

                foreach (var file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(langRoot, SearchOption.AllDirectories))
                {
                    if (!Path.GetFileName(file).ToLower().Contains(cleanPackageId)) continue;
                    if (AutoTranslatorScanner.LoadXmlFileToDict(file).Count > 0) return true;
                }

                return false;
            }
        }
}
