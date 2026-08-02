using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class TranslationWorkbenchTab
    {
        private sealed class WorkbenchManualXmlCategory
        {
            public string Category;
            public Dictionary<string, string> Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class WorkbenchManualXmlImportResult
        {
            public Dictionary<string, Dictionary<string, string>> DataByCategory =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            public string Error;
        }

        private static void ExportCurrentWorkbenchOriginalXml()
        {
            if (_editingMod == null || _isManualXmlBusy) return;

            List<WorkbenchManualXmlCategory> categories = CaptureManualXmlCategories(useOriginalText: true);
            if (categories.Count == 0)
            {
                SetWorkbenchStatus("ATC_Workbench_ManualXmlNoEntries".Translate().ToString());
                return;
            }

            string packageId = _editingMod.PackageId;
            string languageFolder = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
            _isManualXmlBusy = true;
            SetWorkbenchStatus("ATC_Workbench_ManualXmlWorking".Translate().ToString());

            Task.Run(() =>
            {
                int exported = 0;
                string error = null;
                try
                {
                    foreach (WorkbenchManualXmlCategory category in categories)
                    {
                        string path = GetWorkbenchManualXmlPath(packageId, languageFolder, category.Category);
                        AutoTranslatorScanner.SaveXml(path, category.Data);
                        exported += category.Data.Count;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _isManualXmlBusy = false;
                    if (!string.IsNullOrEmpty(error))
                    {
                        SetWorkbenchStatus(error);
                        Messages.Message(error, MessageTypeDefOf.RejectInput, false);
                        return;
                    }

                    SetWorkbenchStatus("ATC_Workbench_ManualXmlExported".Translate(exported).ToString());
                    Messages.Message("ATC_Workbench_ManualXmlExported".Translate(exported), MessageTypeDefOf.PositiveEvent, false);
                    OpenCurrentWorkbenchWorkspace();
                });
            });
        }

        private static void ImportCurrentWorkbenchManualXml()
        {
            if (_editingMod == null || _isManualXmlBusy) return;

            List<string> categories = GetSelectedManualXmlCategoryNames();
            string packageId = _editingMod.PackageId;
            string languageFolder = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
            _isManualXmlBusy = true;
            SetWorkbenchStatus("ATC_Workbench_ManualXmlWorking".Translate().ToString());

            Task.Run(() =>
            {
                WorkbenchManualXmlImportResult result = new WorkbenchManualXmlImportResult();
                try
                {
                    foreach (string category in categories)
                    {
                        string path = GetWorkbenchManualXmlPath(packageId, languageFolder, category);
                        if (!File.Exists(path)) continue;
                        result.DataByCategory[category] = AutoTranslatorScanner.LoadXmlFileToDict(path);
                    }
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                }

                ATC_Dispatcher.RunOnMainThread(() => CompleteManualXmlImport(result));
            });
        }

        private static void CompleteManualXmlImport(WorkbenchManualXmlImportResult result)
        {
            _isManualXmlBusy = false;
            if (result == null || !string.IsNullOrEmpty(result.Error))
            {
                string error = result != null ? result.Error : "ATC_Workbench_ManualXmlImportFailed".Translate().ToString();
                SetWorkbenchStatus(error);
                Messages.Message(error, MessageTypeDefOf.RejectInput, false);
                return;
            }

            int imported = 0;
            foreach (KeyValuePair<string, Dictionary<string, string>> categoryPair in result.DataByCategory)
            {
                if (!_categorizedData.TryGetValue(categoryPair.Key, out List<WorkbenchItem> items) || items == null) continue;
                Dictionary<string, WorkbenchItem> itemsByKey = items
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key))
                    .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, string> pair in categoryPair.Value)
                {
                    if (string.IsNullOrWhiteSpace(pair.Value) || !itemsByKey.TryGetValue(pair.Key, out WorkbenchItem item)) continue;
                    if (SameWorkbenchText(item.TranslatedText, pair.Value)) continue;
                    item.TranslatedText = pair.Value;
                    RefreshWorkbenchItemModifiedState(item);
                    imported++;
                }
            }

            if (imported > 0)
            {
                _categorizedDataVersion++;
                InvalidateVisibleItemCache();
                SetWorkbenchStatus("ATC_Workbench_ManualXmlImported".Translate(imported).ToString());
                Messages.Message("ATC_Workbench_ManualXmlImported".Translate(imported), MessageTypeDefOf.PositiveEvent, false);
            }
            else
            {
                SetWorkbenchStatus("ATC_Workbench_ManualXmlNoChanges".Translate().ToString());
            }
        }

        private static List<WorkbenchManualXmlCategory> CaptureManualXmlCategories(bool useOriginalText)
        {
            List<WorkbenchManualXmlCategory> result = new List<WorkbenchManualXmlCategory>();
            foreach (string categoryName in GetSelectedManualXmlCategoryNames())
            {
                if (!_categorizedData.TryGetValue(categoryName, out List<WorkbenchItem> items) || items == null) continue;
                WorkbenchManualXmlCategory category = new WorkbenchManualXmlCategory { Category = categoryName };
                foreach (WorkbenchItem item in items)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Key)) continue;
                    string value = useOriginalText ? item.OriginalText : item.TranslatedText;
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    category.Data[item.Key] = value;
                }

                if (category.Data.Count > 0) result.Add(category);
            }
            return result;
        }

        private static List<string> GetSelectedManualXmlCategoryNames()
        {
            if (IsAllWorkbenchCategoriesSelected())
            {
                return _categorizedData.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();
            }

            return !string.IsNullOrWhiteSpace(_selectedCategory) && _categorizedData.ContainsKey(_selectedCategory)
                ? new List<string> { _selectedCategory }
                : new List<string>();
        }

        private static string GetWorkbenchManualXmlPath(string packageId, string languageFolder, string category)
        {
            string root = GetWorkbenchWorkspaceRoot(packageId);
            string cleanPackageId = MakeWorkbenchManualXmlPathSegment((packageId ?? "unknown").Replace('.', '_').ToLowerInvariant());
            string safeCategory = MakeWorkbenchManualXmlPathSegment(category);
            string directory = string.Equals(category, "Keyed", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(root, languageFolder, "Manual_Translation", "Keyed")
                : Path.Combine(root, languageFolder, "Manual_Translation", "DefInjected", safeCategory);
            return Path.Combine(directory, cleanPackageId + "_ManualTranslation.xml");
        }

        private static string GetWorkbenchWorkspaceRoot(string packageId)
        {
            return Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Upload_Workspace", packageId ?? "unknown");
        }

        private static string MakeWorkbenchManualXmlPathSegment(string value)
        {
            string safe = value ?? "General";
            foreach (char invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(safe) ? "General" : safe;
        }

        private static void OpenCurrentWorkbenchWorkspace()
        {
            if (_editingMod == null) return;
            string root = GetWorkbenchWorkspaceRoot(_editingMod.PackageId);
            try
            {
                Directory.CreateDirectory(root);
                Application.OpenURL("file://" + root.Replace('\\', '/'));
            }
            catch (Exception ex)
            {
                SetWorkbenchStatus(ex.Message);
                Messages.Message(ex.Message, MessageTypeDefOf.RejectInput, false);
            }
        }
    }
}
