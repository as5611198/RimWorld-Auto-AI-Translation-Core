using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using Verse;

namespace AutoTranslator_Core
{
        public static partial class TranslationWorkbenchTab
        {
            private sealed class WorkbenchRestoreBaselineFile
            {
                public int SchemaVersion = 1;
                public string PackageId;
                public string LanguageFolder;
                public Dictionary<string, WorkbenchRestoreBaselineEntry> Entries =
                    new Dictionary<string, WorkbenchRestoreBaselineEntry>(StringComparer.OrdinalIgnoreCase);
            }

            private sealed class WorkbenchRestoreBaselineEntry
            {
                public string Category;
                public string Key;
                public string OriginalText;
                public string OriginalTranslatedText;
                public bool OriginalTranslatedTextIsReadOnlyReference;
                public string CapturedAtUtc;
            }

            private static bool SameWorkbenchText(string left, string right)
            {
                return string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
            }

            private static bool HasWorkbenchItemUnsavedChanges(WorkbenchItem item)
            {
                return item != null &&
                    (item.IsModified || !SameWorkbenchText(item.TranslatedText, item.SavedTranslatedText));
            }

            private static void RefreshWorkbenchItemModifiedState(WorkbenchItem item)
            {
                if (item == null) return;
                item.IsModified = !SameWorkbenchText(item.TranslatedText, item.SavedTranslatedText);
            }

            private static Dictionary<string, WorkbenchRestoreBaselineEntry> LoadWorkbenchRestoreBaselines(string packageId, string languageFolder)
            {
                Dictionary<string, WorkbenchRestoreBaselineEntry> empty =
                    new Dictionary<string, WorkbenchRestoreBaselineEntry>(StringComparer.OrdinalIgnoreCase);
                string path = GetWorkbenchRestoreBaselinePath(packageId, languageFolder);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return empty;

                try
                {
                    WorkbenchRestoreBaselineFile data = JsonConvert.DeserializeObject<WorkbenchRestoreBaselineFile>(File.ReadAllText(path));
                    if (data == null || data.Entries == null) return empty;

                    Dictionary<string, WorkbenchRestoreBaselineEntry> result =
                        new Dictionary<string, WorkbenchRestoreBaselineEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, WorkbenchRestoreBaselineEntry> pair in data.Entries)
                    {
                        if (pair.Value == null) continue;
                        string category = pair.Value.Category ?? "";
                        string key = pair.Value.Key ?? "";
                        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(key))
                        {
                            TrySplitWorkbenchRestoreBaselineId(pair.Key, out category, out key);
                        }

                        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(key)) continue;

                        pair.Value.Category = category;
                        pair.Value.Key = key;
                        result[BuildWorkbenchRestoreBaselineId(category, key)] = pair.Value;
                    }

                    return result;
                }
                catch
                {
                    return empty;
                }
            }

            private static bool TryGetWorkbenchRestoreBaseline(
                Dictionary<string, WorkbenchRestoreBaselineEntry> baselines,
                string category,
                string key,
                out WorkbenchRestoreBaselineEntry entry)
            {
                entry = null;
                if (baselines == null || string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(key)) return false;
                return baselines.TryGetValue(BuildWorkbenchRestoreBaselineId(category, key), out entry) && entry != null;
            }

            private static void UpdateWorkbenchRestoreBaselines(WorkbenchSaveSnapshot snapshot)
            {
                if (snapshot == null || snapshot.Categories == null) return;

                try
                {
                    Dictionary<string, WorkbenchRestoreBaselineEntry> entries =
                        LoadWorkbenchRestoreBaselines(snapshot.PackageId, snapshot.TargetLangFolder);
                    bool changed = false;

                    foreach (WorkbenchSaveCategorySnapshot category in snapshot.Categories)
                    {
                        if (category == null || category.Items == null) continue;

                        foreach (WorkbenchSaveItemSnapshot item in category.Items)
                        {
                            if (item == null || string.IsNullOrWhiteSpace(item.Key)) continue;

                            string id = BuildWorkbenchRestoreBaselineId(category.Category, item.Key);
                            bool currentEqualsOriginal = SameWorkbenchText(item.TranslatedText, item.OriginalTranslatedText);
                            if (currentEqualsOriginal)
                            {
                                if (entries.Remove(id)) changed = true;
                                continue;
                            }

                            if (entries.ContainsKey(id)) continue;
                            if (!item.IsModified && SameWorkbenchText(item.TranslatedText, item.SavedTranslatedText)) continue;

                            entries[id] = new WorkbenchRestoreBaselineEntry
                            {
                                Category = category.Category ?? "",
                                Key = item.Key ?? "",
                                OriginalText = item.OriginalText ?? "",
                                OriginalTranslatedText = item.OriginalTranslatedText ?? "",
                                OriginalTranslatedTextIsReadOnlyReference = item.OriginalTranslatedTextIsReadOnlyReference,
                                CapturedAtUtc = DateTime.UtcNow.ToString("O")
                            };
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        SaveWorkbenchRestoreBaselines(snapshot.PackageId, snapshot.TargetLangFolder, entries);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[AutoTranslationCore] Failed to update workbench restore baselines: {ex.Message}");
                }
            }

            private static void SaveWorkbenchRestoreBaselines(
                string packageId,
                string languageFolder,
                Dictionary<string, WorkbenchRestoreBaselineEntry> entries)
            {
                string path = GetWorkbenchRestoreBaselinePath(packageId, languageFolder);
                if (string.IsNullOrEmpty(path)) return;

                if (entries == null || entries.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                WorkbenchRestoreBaselineFile data = new WorkbenchRestoreBaselineFile
                {
                    SchemaVersion = 1,
                    PackageId = packageId ?? "",
                    LanguageFolder = languageFolder ?? "",
                    Entries = entries
                };
                File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
            }

            private static string GetWorkbenchRestoreBaselinePath(string packageId, string languageFolder)
            {
                if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(languageFolder)) return null;
                string safePackageId = MakeWorkbenchRestoreBaselineFileName(packageId.Replace(".", "_").ToLowerInvariant());
                return Path.Combine(
                    AutoTranslatorScanner.GetLocalPackPath(),
                    "Workbench_RestoreBaselines",
                    languageFolder,
                    safePackageId + ".json");
            }

            private static string MakeWorkbenchRestoreBaselineFileName(string value)
            {
                string safe = value ?? "unknown";
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    safe = safe.Replace(c, '_');
                }

                return safe;
            }

            private static string BuildWorkbenchRestoreBaselineId(string category, string key)
            {
                return (category ?? "") + "\n" + (key ?? "");
            }

            private static bool TrySplitWorkbenchRestoreBaselineId(string id, out string category, out string key)
            {
                category = "";
                key = "";
                if (string.IsNullOrEmpty(id)) return false;

                int split = id.IndexOf('\n');
                if (split < 0) return false;

                category = id.Substring(0, split);
                key = id.Substring(split + 1);
                return true;
            }
        }
}
