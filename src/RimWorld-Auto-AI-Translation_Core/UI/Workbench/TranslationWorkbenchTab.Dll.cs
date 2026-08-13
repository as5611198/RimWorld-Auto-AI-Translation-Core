using AutoTranslator_Core.TargetedHardcodedUi;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class TranslationWorkbenchTab
    {
        private sealed class WorkbenchDllLoadResult
        {
            internal HardcodedUiScanResult ScanResult;
            internal Dictionary<string, List<WorkbenchItem>> Categories =
                new Dictionary<string, List<WorkbenchItem>>(StringComparer.OrdinalIgnoreCase);
            internal int StaleSavedEntryCount;
            internal string Error = string.Empty;
        }

        private static WorkbenchDllLoadResult LoadDllWorkbenchData(
            ModMetaData mod,
            TargetLanguage targetLanguage)
        {
            var output = new WorkbenchDllLoadResult();
            if (mod == null || !mod.Active || mod.RootDir == null) return output;

            try
            {
                HardcodedUiScanResult scan;
                if (HardcodedUiBatchScanCoordinator.TryGet(mod.PackageId, out HardcodedUiBatchScanSummary cached) &&
                    cached?.Result != null && string.IsNullOrWhiteSpace(cached.Error))
                {
                    scan = cached.Result;
                }
                else
                {
                    scan = HardcodedUiRuntimeScanner.Scan(mod);
                }
                output.ScanResult = scan;
                string languageFolder = AutoTranslatorScanner.GetFolderNameByLanguage(targetLanguage);
                Dictionary<string, HardcodedUiPatchEntry> savedById = LoadDllManifestEntries();
                var currentEntryIds = new HashSet<string>(
                    scan.Entries
                        .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.EntryId))
                        .Select(entry => entry.EntryId),
                    StringComparer.Ordinal);
                output.StaleSavedEntryCount = savedById.Values.Count(entry =>
                    entry != null &&
                    string.Equals(entry.PackageId, mod.PackageId, StringComparison.OrdinalIgnoreCase) &&
                    !currentEntryIds.Contains(entry.EntryId));

                foreach (HardcodedUiPatchEntry entry in scan.Entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.EntryId)) continue;
                    if (!scan.Decisions.TryGetValue(entry.EntryId, out HardcodedUiDecisionRecord decision))
                    {
                        decision = HardcodedUiBaselineDecisionAnalyzer.Analyze(entry);
                        scan.Decisions[entry.EntryId] = decision;
                    }

                    string translated = string.Empty;
                    if (savedById.TryGetValue(entry.EntryId, out HardcodedUiPatchEntry saved))
                    {
                        entry.Translations = saved.Translations != null
                            ? new Dictionary<string, string>(saved.Translations, StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        entry.Translations.TryGetValue(languageFolder, out translated);
                    }

                    string category = string.IsNullOrWhiteSpace(entry.AssemblyRelativePath)
                        ? "DLL"
                        : entry.AssemblyRelativePath;
                    if (!output.Categories.TryGetValue(category, out List<WorkbenchItem> items))
                    {
                        items = new List<WorkbenchItem>();
                        output.Categories[category] = items;
                    }

                    items.Add(new WorkbenchItem
                    {
                        Source = WorkbenchSourceKind.Dll,
                        Category = category,
                        Key = BuildDllWorkbenchKey(entry),
                        OriginalText = entry.Literal ?? string.Empty,
                        TranslatedText = translated ?? string.Empty,
                        OriginalTranslatedText = translated ?? string.Empty,
                        SavedTranslatedText = translated ?? string.Empty,
                        DllEntry = entry,
                        DllDecision = decision
                    });
                }
            }
            catch (Exception ex)
            {
                output.Error = ex.Message;
                Verse.Log.Warning("[AutoTranslationCore] Workbench DLL load failed for " +
                    (mod.PackageId ?? "unknown") + ": " + ex);
            }

            return output;
        }

        private static string BuildDllWorkbenchKey(HardcodedUiPatchEntry entry)
        {
            string method = !string.IsNullOrWhiteSpace(entry.MethodSignature)
                ? entry.MethodSignature
                : (entry.DeclaringType ?? string.Empty) + "." + (entry.MethodName ?? string.Empty);
            return method + " #" + entry.LiteralOrdinal;
        }

        private static Dictionary<string, HardcodedUiPatchEntry> LoadDllManifestEntries()
        {
            string path = HardcodedUiTargetedPatchManager.ManifestPath;
            if (!File.Exists(path))
                return new Dictionary<string, HardcodedUiPatchEntry>(StringComparer.Ordinal);
            HardcodedUiPatchManifest manifest = JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(
                File.ReadAllText(path));
            return (manifest?.Entries ?? new List<HardcodedUiPatchEntry>())
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.EntryId))
                .GroupBy(entry => entry.EntryId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }

        private static bool HasUnsavedDllWorkbenchChanges()
        {
            return _dllStaleSavedEntryCount > 0 || _dllCategorizedData.Values
                .Where(items => items != null)
                .SelectMany(items => items)
                .Any(item => item != null && HasWorkbenchItemUnsavedChanges(item));
        }

        private static bool HasUnsavedXmlWorkbenchChanges()
        {
            return _categorizedData.Values
                .Where(items => items != null)
                .SelectMany(items => items)
                .Any(item => item != null && HasWorkbenchItemUnsavedChanges(item));
        }

        private static void SaveWorkbenchChanges()
        {
            bool xmlChanged = HasUnsavedXmlWorkbenchChanges();
            bool dllChanged = HasUnsavedDllWorkbenchChanges();
            if (!xmlChanged && !dllChanged) return;

            if (dllChanged && !SaveDllWorkbenchChanges()) return;
            if (xmlChanged) SaveModifications();
            else
            {
                SetWorkbenchStatus("ATC_Workbench_DllSaved".Translate().ToString());
                Messages.Message("ATC_Workbench_SaveSuccess".Translate(), MessageTypeDefOf.PositiveEvent, false);
            }
        }

        private static bool SaveDllWorkbenchChanges()
        {
            if (_editingMod == null || _dllScanResult == null) return true;
            try
            {
                string folder = AutoTranslatorScanner.GetFolderNameByLanguage(
                    _loadedWorkbenchTargetLanguage ?? AutoTranslatorMod.Settings.TargetLang);
                List<WorkbenchItem> items = _dllCategorizedData.Values
                    .Where(list => list != null)
                    .SelectMany(list => list)
                    .Where(item => item != null && item.DllEntry != null && item.DllDecision != null)
                    .ToList();

                foreach (WorkbenchItem item in items)
                {
                    string value = (item.TranslatedText ?? string.Empty).Trim();
                    if (value.Length > 0 && !string.Equals(value, item.OriginalText ?? string.Empty, StringComparison.Ordinal))
                    {
                        if (item.DllDecision.EffectiveDecision != HardcodedUiAutomaticDecision.Translate)
                            item.DllDecision.SetUserOverride(HardcodedUiUserOverride.Translate);
                        item.DllEntry.Translations[folder] = value;
                    }
                    else
                    {
                        item.DllEntry.Translations.Remove(folder);
                    }
                    item.DllEntry.Enabled =
                        item.DllDecision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate;
                }

                HardcodedUiDecisionState.Persist(_dllScanResult.Decisions.Values);
                string path = HardcodedUiTargetedPatchManager.ManifestPath;
                HardcodedUiPatchManifest manifest = File.Exists(path)
                    ? JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(File.ReadAllText(path))
                    : null;
                manifest = manifest ?? new HardcodedUiPatchManifest();
                manifest.Approved = true;
                manifest.Entries = (manifest.Entries ?? new List<HardcodedUiPatchEntry>())
                    .Where(entry => entry != null && !string.Equals(
                        entry.PackageId,
                        _editingMod.PackageId,
                        StringComparison.OrdinalIgnoreCase))
                    .Concat(items
                        .Where(item => item.DllDecision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate &&
                                       item.DllEntry.Translations.Count > 0)
                        .Select(item => item.DllEntry))
                    .GroupBy(entry => entry.EntryId, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(entry => entry.EntryId, StringComparer.Ordinal)
                    .ToList();
                if (manifest.Entries.Count > 10000)
                    throw new InvalidOperationException("Approved DLL UI manifest exceeds 10000 entries.");

                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(manifest, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                else File.Move(temporary, path);

                foreach (WorkbenchItem item in items)
                {
                    item.IsModified = false;
                    item.SavedTranslatedText = item.TranslatedText ?? string.Empty;
                    item.SavedTranslatedTextIsReadOnlyReference = false;
                }
                AutoTranslatorMod.Settings.EnableUIInterceptor = false;
                AutoTranslatorMod.Settings.EnableHardcodedUiPrototype = true;
                LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
                HardcodedUiTargetedPatchManager.RequestReload();
                _dllStaleSavedEntryCount = 0;
                _categorizedDataVersion++;
                InvalidateVisibleItemCache();
                return true;
            }
            catch (Exception ex)
            {
                Verse.Log.Error("[AutoTranslationCore] Unified workbench DLL save failed:\n" + ex);
                SetWorkbenchStatus("ATC_HardcodedUi_SaveFailed".Translate(ex.Message).ToString());
                Messages.Message("ATC_HardcodedUi_SaveFailed".Translate(ex.Message), MessageTypeDefOf.RejectInput, false);
                return false;
            }
        }

        private static void MarkDllWorkbenchItemAsTranslated(WorkbenchItem item)
        {
            if (item?.Source != WorkbenchSourceKind.Dll || item.DllDecision == null) return;
            string value = (item.TranslatedText ?? string.Empty).Trim();
            if (value.Length == 0 || string.Equals(value, item.OriginalText ?? string.Empty, StringComparison.Ordinal)) return;
            if (item.DllDecision.EffectiveDecision != HardcodedUiAutomaticDecision.Translate)
                item.DllDecision.SetUserOverride(HardcodedUiUserOverride.Translate);
            if (item.DllEntry != null) item.DllEntry.Enabled = true;
        }

        private static string GetWorkbenchDllDecisionLabel(HardcodedUiDecisionRecord decision)
        {
            if (decision == null) return "ATC_PolicyPreflight_DecisionUncertain".Translate();
            return (decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate
                ? "ATC_PolicyPreflight_DecisionAllow"
                : decision.EffectiveDecision == HardcodedUiAutomaticDecision.DoNotTranslate
                    ? "ATC_PolicyPreflight_DecisionDeny"
                    : "ATC_PolicyPreflight_DecisionUncertain").Translate();
        }

        private static void OpenDllWorkbenchDecisionMenu(WorkbenchItem item)
        {
            if (item?.DllDecision == null || _isSavingModifications) return;
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new FloatMenuOption(
                    "ATC_PolicyPreflight_DecisionAllow".Translate(),
                    () => ApplyDllWorkbenchDecision(item, HardcodedUiUserOverride.Translate)),
                new FloatMenuOption(
                    "ATC_PolicyPreflight_DecisionDeny".Translate(),
                    () => ApplyDllWorkbenchDecision(item, HardcodedUiUserOverride.DoNotTranslate)),
                new FloatMenuOption(
                    "ATC_HardcodedUi_RestoreAutomatic".Translate(),
                    () => ApplyDllWorkbenchDecision(item, HardcodedUiUserOverride.None))
            }));
        }

        private static void ApplyDllWorkbenchDecision(
            WorkbenchItem item,
            HardcodedUiUserOverride decision)
        {
            if (item?.DllDecision == null) return;
            if (decision == HardcodedUiUserOverride.None) item.DllDecision.RestoreAutomaticDecision();
            else item.DllDecision.SetUserOverride(decision);
            if (item.DllEntry != null)
                item.DllEntry.Enabled = item.DllDecision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate;
            item.IsModified = true;
            _categorizedDataVersion++;
            InvalidateVisibleItemCache();
        }

        private static bool CanTranslateWorkbenchRangeItem(WorkbenchItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.OriginalText)) return false;
            string translated = (item.TranslatedText ?? string.Empty).Trim();
            if (translated.Length > 0 && !string.Equals(translated, (item.OriginalText ?? string.Empty).Trim(), StringComparison.Ordinal))
                return false;
            return item.Source != WorkbenchSourceKind.Dll ||
                   (item.DllDecision != null &&
                    item.DllDecision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate);
        }

        private static List<WorkbenchItem> GetCurrentRangeTranslationTargets()
        {
            return GetVisibleItemsForCurrentCategory(GetCurrentWorkbenchSourceItems())
                .Where(CanTranslateWorkbenchRangeItem)
                .ToList();
        }

        private static void StartTranslateCurrentWorkbenchRange()
        {
            if (_isTranslatingCurrentRange || _isSavingModifications || _editingMod == null) return;
            if (AutoTranslatorSettings.IsRunning ||
                AutoTranslatorAPI.HasOutstandingTranslationWork)
            {
                Messages.Message("ATC_Workbench_TranslateOriginalBusyGlobal".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            List<WorkbenchItem> targets = GetCurrentRangeTranslationTargets();
            if (targets.Count == 0)
            {
                SetWorkbenchStatus("ATC_Workbench_CurrentRangeNothing".Translate().ToString());
                return;
            }

            TargetLanguage targetLanguage = AutoTranslatorMod.Settings.TargetLang;
            int dictionaryTranslated = 0;
            foreach (WorkbenchItem item in targets.ToList())
            {
                if (item.Source != WorkbenchSourceKind.Dll ||
                    item.DllDecision?.EffectiveDecision != HardcodedUiAutomaticDecision.Translate ||
                    !HardcodedUiBuiltInDictionary.TryTranslate(
                        item.OriginalText,
                        item.DllDecision.SemanticRole,
                        targetLanguage,
                        out string dictionaryValue))
                    continue;
                item.TranslatedText = dictionaryValue;
                MarkDllWorkbenchItemAsTranslated(item);
                RefreshWorkbenchItemModifiedState(item);
                targets.Remove(item);
                dictionaryTranslated++;
            }
            if (dictionaryTranslated > 0)
            {
                _categorizedDataVersion++;
                InvalidateVisibleItemCache();
            }
            if (targets.Count == 0)
            {
                SetWorkbenchStatus("ATC_Workbench_CurrentRangeDone".Translate(dictionaryTranslated, dictionaryTranslated).ToString());
                return;
            }
            if (!AutoTranslatorAPI.HasAnyReadyConfig())
            {
                Messages.Message("ATC_EmptyConfigWarning".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            _isTranslatingCurrentRange = true;
            _currentRangeTranslated = dictionaryTranslated;
            _currentRangeTotal = targets.Count + dictionaryTranslated;
            string packageId = _editingMod.PackageId;
            AutoTranslatorSettings.ResetPipelineCancellation();
            SetWorkbenchStatus("ATC_Workbench_CurrentRangeStarted".Translate(_currentRangeTotal).ToString());

            Task.Run(async () =>
            {
                var completed = new Dictionary<WorkbenchItem, string>();
                string error = string.Empty;
                bool cancelled = false;
                try
                {
                    const int batchSize = 20;
                    for (int offset = 0; offset < targets.Count; offset += batchSize)
                    {
                        if (AutoTranslatorSettings.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }
                        List<WorkbenchItem> batch = targets.Skip(offset).Take(batchSize).ToList();
                        List<string> translated = await AutoTranslatorAPI.TranslateBatchAsync(
                            batch.Select(item => item.OriginalText).ToList(),
                            suppressFinalParseError: true,
                            packageId: packageId,
                            requestScope: "workbench-range/" + packageId + "/" + offset,
                            requestPurpose: "translation");
                        bool interruptedAfterBatch = AutoTranslatorSettings.IsCancellationRequested;
                        if (translated == null || translated.Count != batch.Count)
                        {
                            if (interruptedAfterBatch)
                            {
                                cancelled = true;
                                break;
                            }
                            error = "batch " + (offset / batchSize + 1) + " returned no usable result";
                            continue;
                        }
                        for (int i = 0; i < batch.Count; i++)
                        {
                            if (!AutoTranslatorScanner.TryAcceptTranslatedValue(
                                    translated[i],
                                    batch[i].OriginalText,
                                    out string value,
                                    out string failureReason,
                                    out string failureDetail))
                            {
                                error = "batch " + (offset / batchSize + 1) +
                                        " rejected an item (" + failureReason + "): " + failureDetail;
                                continue;
                            }
                            completed[batch[i]] = value;
                        }
                        _currentRangeTranslated = dictionaryTranslated + completed.Count;
                        if (interruptedAfterBatch)
                        {
                            cancelled = true;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    try
                    {
                        if (_editingMod != null &&
                            string.Equals(_editingMod.PackageId, packageId, StringComparison.OrdinalIgnoreCase) &&
                            AutoTranslatorMod.Settings.TargetLang == targetLanguage)
                        {
                            foreach (KeyValuePair<WorkbenchItem, string> pair in completed)
                            {
                                pair.Key.TranslatedText = pair.Value;
                                MarkDllWorkbenchItemAsTranslated(pair.Key);
                                RefreshWorkbenchItemModifiedState(pair.Key);
                            }
                            _currentRangeTranslated = dictionaryTranslated + completed.Count;
                            _categorizedDataVersion++;
                            InvalidateVisibleItemCache();
                            SetWorkbenchStatus(cancelled
                                ? "ATC_Workbench_CurrentRangeStopped".Translate(dictionaryTranslated + completed.Count, targets.Count + dictionaryTranslated).ToString()
                                : string.IsNullOrWhiteSpace(error)
                                ? "ATC_Workbench_CurrentRangeDone".Translate(dictionaryTranslated + completed.Count, targets.Count + dictionaryTranslated).ToString()
                                : "ATC_Workbench_CurrentRangePartial".Translate(dictionaryTranslated + completed.Count, targets.Count + dictionaryTranslated, error).ToString());
                        }
                    }
                    finally
                    {
                        _isTranslatingCurrentRange = false;
                    }
                });
            });
        }
    }
}
