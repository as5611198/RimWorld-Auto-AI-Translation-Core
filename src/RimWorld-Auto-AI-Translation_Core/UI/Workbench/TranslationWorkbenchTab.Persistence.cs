using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;

namespace AutoTranslator_Core
{
        public static partial class TranslationWorkbenchTab
        {
            private sealed class WorkbenchProvenanceFileSnapshot
            {
                public Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> Entries =
                    new Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
            }

            private static void SaveModifications()
            {
                if (_editingMod == null) return;
                if (_isLoading || !_loadedWorkbenchTargetLanguage.HasValue) return;
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
                TargetLanguage targetLang = _loadedWorkbenchTargetLanguage ?? AutoTranslatorMod.Settings.TargetLang;
                string targetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(targetLang);
                string packPath = AutoTranslatorScanner.GetLocalPackPath();
                string cleanPackageId = _editingMod.PackageId.Replace(".", "_").ToLower();
                WorkbenchSaveSnapshot snapshot = new WorkbenchSaveSnapshot
                {
                    Mod = _editingMod,
                    PackageId = _editingMod.PackageId,
                    RootDir = _editingMod.RootDir != null ? _editingMod.RootDir.FullName : "",
                    TargetLang = targetLang,
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
                string targetLanguageRoot = Path.Combine(snapshot.PackPath, "Languages", snapshot.TargetLangFolder);
                string workspaceLanguageRoot = Path.Combine(workspaceBaseDir, snapshot.PackageId, snapshot.TargetLangFolder);

                try
                {
                    Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> targetProvenanceIndex =
                        LoadWorkbenchProvenanceIndex(targetLanguageRoot, snapshot.PackageId);
                    Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> workspaceProvenanceIndex =
                        LoadWorkbenchProvenanceIndex(workspaceLanguageRoot, snapshot.PackageId);
                    HashSet<string> ownedKeyedFileNames = GetWorkbenchOwnedKeyedFileNames(
                        snapshot.PackageId,
                        snapshot.TargetLang,
                        AutoTranslatorScanner.GetAllEffectiveLangPaths(snapshot.PackageId, snapshot.RootDir));

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

                        string targetFile = Path.Combine(targetDir, $"{snapshot.CleanPackageId}_AutoTranslated.xml");
                        string workspaceFile = Path.Combine(workspaceDir, $"{snapshot.CleanPackageId}_AutoTranslated.xml");
                        List<string> targetSourceFiles = AutoTranslatorScanner
                            .GetXmlFilesForTranslationCache(targetDir, SearchOption.TopDirectoryOnly)
                            .Where(file => IsWorkbenchGeneratedOutputFile(
                                file,
                                snapshot.PackageId,
                                category.Category == "Keyed" ? ownedKeyedFileNames : null))
                            .OrderBy(file => PathsEqual(file, targetFile) ? 1 : 0)
                            .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        List<string> workspaceSourceFiles = AutoTranslatorScanner
                            .GetXmlFilesForTranslationCache(workspaceDir, SearchOption.TopDirectoryOnly)
                            .Where(file => IsWorkbenchGeneratedOutputFile(
                                file,
                                snapshot.PackageId,
                                category.Category == "Keyed" ? ownedKeyedFileNames : null))
                            .OrderBy(file => PathsEqual(file, workspaceFile) ? 1 : 0)
                            .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        Dictionary<string, string> fullDictToSave =
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> existingProvenance =
                            new Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
                        MergeExistingWorkbenchFiles(
                            targetLanguageRoot,
                            snapshot.PackageId,
                            targetSourceFiles,
                            targetProvenanceIndex,
                            fullDictToSave,
                            existingProvenance);
                        MergeExistingWorkbenchFiles(
                            workspaceLanguageRoot,
                            snapshot.PackageId,
                            workspaceSourceFiles,
                            workspaceProvenanceIndex,
                            fullDictToSave,
                            existingProvenance);

                        foreach (WorkbenchSaveItemSnapshot item in category.Items)
                        {
                            if (ShouldSaveWorkbenchSnapshotItem(item))
                            {
                                fullDictToSave[item.Key] = item.TranslatedText;
                            }
                            else
                            {
                                fullDictToSave.Remove(item.Key);
                                existingProvenance.Remove(item.Key);
                            }
                            if (item.IsModified) result.SavedCount++;
                        }

                        EnsureWorkbenchFileWritable(workspaceFile);
                        AutoTranslatorScanner.SaveXml(workspaceFile, fullDictToSave);
                        if (!AutoTranslatorScanner.SaveProvenanceForFile(
                            workspaceLanguageRoot,
                            snapshot.PackageId,
                            workspaceFile,
                            fullDictToSave,
                            BuildManualEditProvenance(
                                snapshot,
                                category,
                                workspaceFile,
                                fullDictToSave,
                                existingProvenance)))
                        {
                            throw new IOException("Could not save workspace translation provenance.");
                        }

                        EnsureWorkbenchFileWritable(targetFile);
                        AutoTranslatorScanner.SaveXml(targetFile, fullDictToSave);
                        if (!AutoTranslatorScanner.SaveProvenanceForFile(
                            targetLanguageRoot,
                            snapshot.PackageId,
                            targetFile,
                            fullDictToSave,
                            BuildManualEditProvenance(
                                snapshot,
                                category,
                                targetFile,
                                fullDictToSave,
                                existingProvenance)))
                        {
                            throw new IOException("Could not save translation provenance.");
                        }

                        DeleteSupersededWorkbenchFiles(
                            workspaceSourceFiles,
                            workspaceFile);
                        DeleteSupersededWorkbenchFiles(
                            targetSourceFiles,
                            targetFile);
                        result.TouchedTranslationFiles = true;
                    }

                    UpdateWorkbenchRestoreBaselines(snapshot);
                    result.HasSavedTranslation = HasAnySavedTranslationForCurrentMod(
                        snapshot.TargetLangFolder,
                        snapshot.PackageId,
                        snapshot.RootDir,
                        snapshot.TargetLang);
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

            private static void MergeExistingWorkbenchFiles(
                string languageRoot,
                string packageId,
                IEnumerable<string> files,
                Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> provenanceIndex,
                Dictionary<string, string> mergedData,
                Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> mergedProvenance)
            {
                if (files == null || mergedData == null || mergedProvenance == null) return;

                foreach (string file in files)
                {
                    if (!AutoTranslatorScanner.TryLoadRawXmlFileToDict(
                            file,
                            out Dictionary<string, string> data))
                    {
                        throw new InvalidDataException(
                            "Could not parse existing translation XML: " + Path.GetFileName(file));
                    }

                    if (data.Any(pair => string.IsNullOrWhiteSpace(pair.Value)))
                    {
                        throw new InvalidDataException(
                            "Existing translation XML contains an empty value that cannot be consolidated safely: " +
                            Path.GetFileName(file));
                    }

                    foreach (KeyValuePair<string, string> pair in data)
                    {
                        AutoTranslatorScanner.TranslationProvenanceEntry source =
                            GetWorkbenchFileEntryProvenance(
                                languageRoot,
                                packageId,
                                file,
                                pair.Key,
                                pair.Value,
                                provenanceIndex);

                        bool sameValue = mergedData.TryGetValue(pair.Key, out string previousValue) &&
                                         SameWorkbenchText(previousValue, pair.Value);
                        bool keepPreviousProvenance = sameValue &&
                                                      HasUsefulWorkbenchProvenance(mergedProvenance.TryGetValue(
                                                          pair.Key,
                                                          out AutoTranslatorScanner.TranslationProvenanceEntry previousSource)
                                                          ? previousSource
                                                          : null) &&
                                                      !HasUsefulWorkbenchProvenance(source);

                        mergedData[pair.Key] = pair.Value;
                        if (!keepPreviousProvenance)
                        {
                            mergedProvenance[pair.Key] = source;
                        }
                    }
                }
            }

            private static Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> LoadWorkbenchProvenanceIndex(
                string languageRoot,
                string packageId)
            {
                Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> empty =
                    new Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(languageRoot) || string.IsNullOrWhiteSpace(packageId)) return empty;

                string path = Path.Combine(
                    languageRoot,
                    "ATC_Provenance",
                    packageId.Replace(".", "_").ToLowerInvariant() + ".json");
                if (!File.Exists(path)) return empty;

                try
                {
                    WorkbenchProvenanceFileSnapshot snapshot =
                        JsonConvert.DeserializeObject<WorkbenchProvenanceFileSnapshot>(File.ReadAllText(path));
                    if (snapshot == null || snapshot.Entries == null)
                    {
                        throw new InvalidDataException("Provenance document has no entries object.");
                    }

                    return new Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry>(
                        snapshot.Entries,
                        StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException(
                        "Could not parse existing translation provenance: " + Path.GetFileName(path),
                        ex);
                }
            }

            private static AutoTranslatorScanner.TranslationProvenanceEntry GetWorkbenchFileEntryProvenance(
                string languageRoot,
                string packageId,
                string translationFile,
                string key,
                string value,
                Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> provenanceIndex)
            {
                string relativePath = GetWorkbenchRelativeTranslationPath(languageRoot, translationFile);
                string entryId = string.IsNullOrEmpty(relativePath) || string.IsNullOrWhiteSpace(key)
                    ? ""
                    : relativePath + "|" + key;

                if (!string.IsNullOrEmpty(entryId) &&
                    provenanceIndex != null &&
                    provenanceIndex.TryGetValue(
                        entryId,
                        out AutoTranslatorScanner.TranslationProvenanceEntry source) &&
                    source != null &&
                    string.Equals(
                        source.ValueHash,
                        ComputeWorkbenchValueHash(value),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return AutoTranslatorScanner.CloneProvenance(source, value);
                }

                return AutoTranslatorScanner.CreateProvenance(
                    AutoTranslatorScanner.ProvenanceKindUnknownLegacy,
                    packageId,
                    "",
                    translationFile,
                    "",
                    value);
            }

            private static string GetWorkbenchRelativeTranslationPath(string languageRoot, string file)
            {
                if (string.IsNullOrWhiteSpace(languageRoot) || string.IsNullOrWhiteSpace(file)) return "";

                string root = Path.GetFullPath(languageRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(file);
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return full.Substring(root.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');
                }

                return Path.GetFileName(file);
            }

            private static string ComputeWorkbenchValueHash(string value)
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                    StringBuilder builder = new StringBuilder(hash.Length * 2);
                    foreach (byte hashByte in hash) builder.Append(hashByte.ToString("x2"));
                    return builder.ToString();
                }
            }

            private static bool HasUsefulWorkbenchProvenance(
                AutoTranslatorScanner.TranslationProvenanceEntry entry)
            {
                return entry != null &&
                       !string.IsNullOrWhiteSpace(entry.SourceKind) &&
                       !string.Equals(
                           entry.SourceKind,
                           AutoTranslatorScanner.ProvenanceKindUnknownLegacy,
                           StringComparison.OrdinalIgnoreCase);
            }

            private static void EnsureWorkbenchFileWritable(string file)
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;
                File.SetAttributes(file, FileAttributes.Normal);
            }

            private static void DeleteSupersededWorkbenchFiles(
                IEnumerable<string> sourceFiles,
                string canonicalFile)
            {
                if (sourceFiles == null) return;

                foreach (string oldFile in sourceFiles)
                {
                    if (PathsEqual(oldFile, canonicalFile) || !File.Exists(oldFile)) continue;

                    File.SetAttributes(oldFile, FileAttributes.Normal);
                    File.Delete(oldFile);
                    AutoTranslatorScanner.NotifyTranslationFileChanged(oldFile);
                }
            }

            private static bool PathsEqual(string left, string right)
            {
                if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
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
                Dictionary<string, string> savedData,
                Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> existingProvenance)
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
                    else if (existingProvenance != null &&
                             existingProvenance.TryGetValue(
                                 pair.Key,
                                 out AutoTranslatorScanner.TranslationProvenanceEntry existingSource))
                    {
                        result[pair.Key] = AutoTranslatorScanner.CloneProvenance(existingSource, pair.Value);
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

                TranslationUnresolvedManager.ResolveMatching(BuildResolvedWorkbenchEntries(snapshot));
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
                if (result.HasSavedTranslation &&
                    !TranslationUnresolvedManager.HasPendingForPackage(
                        snapshot.PackageId,
                        snapshot.TargetLang.ToString()))
                {
                    MarkPackageTranslated(snapshot.PackageId);
                    ModUpdateDetector.MarkModAsTranslatedSnapshot(snapshot.PackageId, result.SourceFingerprint);
                }
            }

            private static List<TranslationUnresolvedEntry> BuildResolvedWorkbenchEntries(
                WorkbenchSaveSnapshot snapshot)
            {
                List<TranslationUnresolvedEntry> entries = new List<TranslationUnresolvedEntry>();
                if (snapshot == null || snapshot.Categories == null) return entries;

                foreach (WorkbenchSaveCategorySnapshot category in snapshot.Categories)
                {
                    if (category == null || category.Items == null) continue;
                    bool keyed = string.Equals(category.Category, "Keyed", StringComparison.OrdinalIgnoreCase);

                    foreach (WorkbenchSaveItemSnapshot item in category.Items)
                    {
                        if (item == null || !item.IsModified || !ShouldSaveWorkbenchSnapshotItem(item)) continue;
                        if (!TranslationResultLanguagePolicy.ShouldAccept(
                                item.TranslatedText,
                                item.OriginalText,
                                snapshot.TargetLang))
                        {
                            continue;
                        }

                        entries.Add(new TranslationUnresolvedEntry
                        {
                            TargetLanguage = snapshot.TargetLang.ToString(),
                            PackageId = snapshot.PackageId,
                            ModName = snapshot.Mod != null ? snapshot.Mod.Name : "",
                            Bucket = keyed ? "Keyed" : "DefInjected",
                            DefType = keyed ? "" : category.Category,
                            Key = item.Key,
                            SourceText = item.OriginalText
                        });
                    }
                }

                return entries;
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

            private static bool HasAnySavedTranslationForCurrentMod(
                string targetLangFolder,
                string packageId,
                string rootDir,
                TargetLanguage targetLang)
            {
                string langRoot = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Languages", targetLangFolder);
                if (!Directory.Exists(langRoot)) return false;
                HashSet<string> ownedKeyedFileNames = GetWorkbenchOwnedKeyedFileNames(
                    packageId,
                    targetLang,
                    AutoTranslatorScanner.GetAllEffectiveLangPaths(packageId, rootDir));

                foreach (var file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(langRoot, SearchOption.AllDirectories))
                {
                    bool keyedFile = string.Equals(
                        Directory.GetParent(file)?.Name,
                        "Keyed",
                        StringComparison.OrdinalIgnoreCase);
                    if (!IsOwnedWorkbenchOutputFile(
                            file,
                            packageId,
                            keyedFile ? ownedKeyedFileNames : null))
                    {
                        continue;
                    }
                    if (AutoTranslatorScanner.LoadXmlFileToDict(file, targetLang).Count > 0) return true;
                }

                return false;
            }
        }
}
