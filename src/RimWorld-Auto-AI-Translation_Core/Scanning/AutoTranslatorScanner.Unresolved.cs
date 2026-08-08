using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        public static void StartUnresolvedRetry(List<TranslationUnresolvedEntry> entries)
        {
            List<TranslationUnresolvedEntry> selected = (entries ?? new List<TranslationUnresolvedEntry>())
                .Where(entry => entry != null &&
                                !string.IsNullOrWhiteSpace(entry.Id) &&
                                !TranslationUnresolvedManager.IsFileLevelFailure(entry))
                .Select(CloneUnresolvedEntry)
                .ToList();
            if (selected.Count == 0 || AutoTranslatorSettings.IsRunning) return;
            AutoTranslatorSettings.ResetPipelineCancellation();
            if (!EntriesMatchCurrentTargetLanguage(selected))
            {
                Messages.Message(
                    "ATC_Unresolved_WrongLanguage".Translate(GetEntryTargetLanguage(selected[0])),
                    MessageTypeDefOf.RejectInput,
                    false);
                return;
            }

            AutoTranslatorSettings.IsRunning = true;
            AutoTranslatorMod.Settings.SessionCharCount = 0;
            ResetValidationStats();
            AutoTranslatorMod.Settings.CurrentProgress = 0f;
            AutoTranslatorMod.Settings.SubProgress = 0f;
            AutoTranslatorMod.Settings.CurrentTaskName = "ATC_Unresolved_RetryAI".Translate();
            AutoTranslatorMod.Settings.SubTaskName = string.Empty;

            Task.Run(async () =>
            {
                try
                {
                    if (!TryValidateCurrentUnresolvedSources(selected, out TranslationUnresolvedEntry staleEntry))
                    {
                        ATC_Dispatcher.RunOnMainThread(() => Messages.Message(
                            "ATC_Unresolved_SourceChanged".Translate(staleEntry != null ? staleEntry.Key : string.Empty),
                            MessageTypeDefOf.RejectInput,
                            false));
                        return;
                    }

                    if (!await AutoTranslatorAPI.TestConnectionAsync())
                    {
                        foreach (TranslationUnresolvedEntry entry in selected)
                        {
                            RecordUnresolvedRetryFailure(
                                entry,
                                TranslationUnresolvedReasons.ApiFailure,
                                "The translation provider connection test failed.");
                        }
                        return;
                    }

                    List<IGrouping<string, TranslationUnresolvedEntry>> groups = selected
                        .GroupBy(entry => entry.TargetFile ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    int completedGroups = 0;
                    int resolvedCount = 0;

                    foreach (IGrouping<string, TranslationUnresolvedEntry> group in groups)
                    {
                        if (AutoTranslatorSettings.IsCancellationRequested) break;

                        List<TranslationUnresolvedEntry> groupEntries = group.ToList();
                        string targetFile = group.Key;
                        AutoTranslatorMod.Settings.SubTaskName = groupEntries[0].ModName + " / " +
                            (string.IsNullOrWhiteSpace(groupEntries[0].DefType)
                                ? groupEntries[0].Bucket
                                : groupEntries[0].DefType);

                        if (string.IsNullOrWhiteSpace(targetFile))
                        {
                            foreach (TranslationUnresolvedEntry entry in groupEntries)
                            {
                                RecordUnresolvedRetryFailure(
                                    entry,
                                    TranslationUnresolvedReasons.SaveFailure,
                                    "The target translation file path is missing.");
                            }
                            completedGroups++;
                            continue;
                        }

                        if (!TryLoadUnresolvedLanguageFile(targetFile, out Dictionary<string, string> finalData, out string loadError))
                        {
                            foreach (TranslationUnresolvedEntry entry in groupEntries)
                            {
                                RecordUnresolvedRetryFailure(
                                    entry,
                                    TranslationUnresolvedReasons.SaveFailure,
                                    loadError);
                            }
                            completedGroups++;
                            continue;
                        }

                        List<string> sourceTexts = groupEntries
                            .Select(entry => entry.SourceText ?? string.Empty)
                            .ToList();
                        List<TranslationBatchItemResult> results = await SafeTranslateBatch(
                            sourceTexts,
                            groupEntries[0].ModName + " / targeted retry");
                        if (AutoTranslatorSettings.IsCancellationRequested) break;

                        string packLangRoot = GetUnresolvedPackLanguageRoot(groupEntries[0]);
                        ModMetaData mod = FindInstalledMod(groupEntries[0].PackageId);
                        Dictionary<string, TranslationProvenanceEntry> provenanceByKey =
                            new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
                        foreach (KeyValuePair<string, string> pair in finalData)
                        {
                            provenanceByKey[pair.Key] = GetFileEntryProvenance(
                                packLangRoot,
                                groupEntries[0].PackageId,
                                targetFile,
                                pair.Key,
                                pair.Value);
                        }

                        List<TranslationUnresolvedEntry> accepted = new List<TranslationUnresolvedEntry>();
                        for (int index = 0; index < groupEntries.Count; index++)
                        {
                            TranslationUnresolvedEntry entry = groupEntries[index];
                            TranslationBatchItemResult result = results != null && index < results.Count
                                ? results[index]
                                : null;
                            if (result == null || !result.IsSuccess)
                            {
                                RecordUnresolvedRetryFailure(
                                    entry,
                                    result != null ? result.FailureReason : TranslationUnresolvedReasons.ApiFailure,
                                    result != null ? result.Detail : "No targeted retry result was produced.");
                                continue;
                            }

                            string translated;
                            string failureReason;
                            string failureDetail;
                            if (!TryAcceptTranslatedValue(
                                    result.Value,
                                    entry.SourceText,
                                    out translated,
                                    out failureReason,
                                    out failureDetail))
                            {
                                RecordUnresolvedRetryFailure(entry, failureReason, failureDetail);
                                continue;
                            }

                            finalData[entry.Key] = translated;
                            provenanceByKey[entry.Key] = CreateProvenance(
                                ProvenanceKindAI,
                                entry.PackageId,
                                entry.ModName,
                                entry.SourceFile,
                                "English",
                                translated);
                            accepted.Add(entry);
                        }

                        if (accepted.Count > 0)
                        {
                            if (SaveGeneratedTranslationFile(
                                    mod,
                                    targetFile,
                                    packLangRoot,
                                    finalData,
                                    provenanceByKey))
                            {
                                TranslationUnresolvedManager.Resolve(accepted.Select(entry => entry.Id));
                                resolvedCount += accepted.Count;
                            }
                            else
                            {
                                foreach (TranslationUnresolvedEntry entry in accepted)
                                {
                                    RecordUnresolvedRetryFailure(
                                        entry,
                                        TranslationUnresolvedReasons.SaveFailure,
                                        "The targeted retry passed validation but could not be saved.");
                                }
                            }
                        }

                        completedGroups++;
                        AutoTranslatorMod.Settings.CurrentProgress =
                            (float)completedGroups / Math.Max(1, groups.Count);
                    }

                    if (!AutoTranslatorSettings.IsCancellationRequested)
                    {
                        MarkResolvedPackagesAsTranslated(selected);
                        AutoTranslatorSettings.AddLog(
                            AutoTranslatorAPI.TranslateText("ATC_Unresolved_RetryDone", resolvedCount, selected.Count));
                        AutoTranslatorMod.Settings.CurrentProgress = 1f;
                        AutoTranslatorMod.Settings.SubProgress = 1f;
                        AutoTranslatorMod.Settings.CurrentTaskName = "ATC_TaskDone".Translate();
                        RequestMemoryDrop();
                        TranslationWorkbenchTab.RequestRefresh();
                        AutoTranslatorSettings.ShowFinishPopup = true;
                    }
                }
                catch (Exception ex)
                {
                    foreach (TranslationUnresolvedEntry entry in selected)
                    {
                        RecordUnresolvedRetryFailure(
                            entry,
                            TranslationUnresolvedReasons.Unknown,
                            "Targeted retry was interrupted: " + ex.Message);
                    }
                    Log.Warning("[AutoTranslationCore] Targeted unresolved retry failed: " + ex);
                }
                finally
                {
                    TranslationUnresolvedManager.CompleteRun();
                    AutoTranslatorSettings.IsRunning = false;
                    if (TranslationUnresolvedManager.HasPending)
                        AutoTranslatorSettings.ShowFinishPopup = true;
                }
            });
        }

        public static void KeepOriginalForUnresolved(List<TranslationUnresolvedEntry> entries)
        {
            List<TranslationUnresolvedEntry> selected = (entries ?? new List<TranslationUnresolvedEntry>())
                .Where(entry => entry != null &&
                                !string.IsNullOrWhiteSpace(entry.Id) &&
                                !TranslationUnresolvedManager.IsFileLevelFailure(entry))
                .Select(CloneUnresolvedEntry)
                .ToList();
            if (selected.Count == 0 || AutoTranslatorSettings.IsRunning) return;
            AutoTranslatorSettings.ResetPipelineCancellation();
            if (!EntriesMatchCurrentTargetLanguage(selected))
            {
                Messages.Message(
                    "ATC_Unresolved_WrongLanguage".Translate(GetEntryTargetLanguage(selected[0])),
                    MessageTypeDefOf.RejectInput,
                    false);
                return;
            }
            if (!TryValidateCurrentUnresolvedSources(selected, out TranslationUnresolvedEntry staleEntry))
            {
                Messages.Message(
                    "ATC_Unresolved_SourceChanged".Translate(staleEntry != null ? staleEntry.Key : string.Empty),
                    MessageTypeDefOf.RejectInput,
                    false);
                return;
            }

            List<string> ignoredIds = new List<string>();
            foreach (IGrouping<string, TranslationUnresolvedEntry> group in selected
                .GroupBy(entry => entry.TargetFile ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                List<TranslationUnresolvedEntry> groupEntries = group.ToList();
                string targetFile = group.Key;
                if (string.IsNullOrWhiteSpace(targetFile))
                {
                    foreach (TranslationUnresolvedEntry entry in groupEntries)
                    {
                        RecordUnresolvedRetryFailure(
                            entry,
                            TranslationUnresolvedReasons.SaveFailure,
                            "The target translation file path is missing.");
                    }
                    continue;
                }

                if (!TryLoadUnresolvedLanguageFile(targetFile, out Dictionary<string, string> finalData, out string loadError))
                {
                    foreach (TranslationUnresolvedEntry entry in groupEntries)
                    {
                        RecordUnresolvedRetryFailure(
                            entry,
                            TranslationUnresolvedReasons.SaveFailure,
                            loadError);
                    }
                    continue;
                }
                string packLangRoot = GetUnresolvedPackLanguageRoot(groupEntries[0]);
                ModMetaData mod = FindInstalledMod(groupEntries[0].PackageId);
                Dictionary<string, TranslationProvenanceEntry> provenanceByKey =
                    new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> pair in finalData)
                {
                    provenanceByKey[pair.Key] = GetFileEntryProvenance(
                        packLangRoot,
                        groupEntries[0].PackageId,
                        targetFile,
                        pair.Key,
                        pair.Value);
                }

                foreach (TranslationUnresolvedEntry entry in groupEntries)
                {
                    finalData[entry.Key] = entry.SourceText ?? string.Empty;
                    provenanceByKey[entry.Key] = CreateProvenance(
                        ProvenanceKindModNativeTarget,
                        entry.PackageId,
                        entry.ModName,
                        entry.SourceFile,
                        "Original",
                        entry.SourceText ?? string.Empty);
                }

                if (SaveGeneratedTranslationFile(mod, targetFile, packLangRoot, finalData, provenanceByKey))
                {
                    ignoredIds.AddRange(groupEntries.Select(entry => entry.Id));
                }
                else
                {
                    foreach (TranslationUnresolvedEntry entry in groupEntries)
                    {
                        RecordUnresolvedRetryFailure(
                            entry,
                            TranslationUnresolvedReasons.SaveFailure,
                            "The original text could not be saved to the translation file.");
                    }
                }
            }

            if (ignoredIds.Count == 0) return;

            if (!TranslationUnresolvedManager.Ignore(ignoredIds))
            {
                Messages.Message(
                    "ATC_Unresolved_IgnoreSaveFailed".Translate(),
                    MessageTypeDefOf.RejectInput,
                    false);
                return;
            }
            MarkResolvedPackagesAsTranslated(selected);
            TranslationWorkbenchTab.RequestRefresh();
            RequestMemoryDrop();
            Messages.Message(
                "ATC_Unresolved_IgnoreDone".Translate(ignoredIds.Count),
                MessageTypeDefOf.NeutralEvent,
                false);
        }

        private static void RecordUnresolvedRetryFailure(
            TranslationUnresolvedEntry entry,
            string reason,
            string detail)
        {
            if (entry == null) return;
            TranslationUnresolvedEntry failure = CloneUnresolvedEntry(entry);
            failure.Reason = string.IsNullOrWhiteSpace(reason)
                ? TranslationUnresolvedReasons.Unknown
                : reason;
            failure.Detail = detail ?? string.Empty;
            failure.State = TranslationUnresolvedStates.Pending;
            TranslationUnresolvedManager.RecordFailure(failure);
        }

        private static TranslationUnresolvedEntry CloneUnresolvedEntry(TranslationUnresolvedEntry source)
        {
            return new TranslationUnresolvedEntry
            {
                Id = source.Id,
                TargetLanguage = source.TargetLanguage,
                PackageId = source.PackageId,
                ModName = source.ModName,
                Bucket = source.Bucket,
                DefType = source.DefType,
                Key = source.Key,
                SourceText = source.SourceText,
                SourceFile = source.SourceFile,
                TargetFile = source.TargetFile,
                Reason = source.Reason,
                Detail = source.Detail,
                Attempts = source.Attempts,
                SourceHash = source.SourceHash,
                State = source.State
            };
        }

        private static bool EntriesMatchCurrentTargetLanguage(IEnumerable<TranslationUnresolvedEntry> entries)
        {
            string current = AutoTranslatorMod.Settings.TargetLang.ToString();
            return (entries ?? Enumerable.Empty<TranslationUnresolvedEntry>()).All(entry =>
                entry == null ||
                string.IsNullOrWhiteSpace(entry.TargetLanguage) ||
                string.Equals(entry.TargetLanguage, current, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetEntryTargetLanguage(TranslationUnresolvedEntry entry)
        {
            return entry != null && !string.IsNullOrWhiteSpace(entry.TargetLanguage)
                ? entry.TargetLanguage
                : AutoTranslatorMod.Settings.TargetLang.ToString();
        }

        internal static bool IsUnresolvedEntryCurrent(TranslationUnresolvedEntry entry)
        {
            if (entry == null || !EntriesMatchCurrentTargetLanguage(new[] { entry })) return false;
            return TryValidateCurrentUnresolvedSources(
                new[] { CloneUnresolvedEntry(entry) },
                out TranslationUnresolvedEntry _);
        }

        private static bool TryValidateCurrentUnresolvedSources(
            IEnumerable<TranslationUnresolvedEntry> entries,
            out TranslationUnresolvedEntry staleEntry)
        {
            staleEntry = null;
            Dictionary<string, Dictionary<string, Dictionary<string, string>>> rawDefsByPackage =
                new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

            foreach (TranslationUnresolvedEntry entry in entries ?? Enumerable.Empty<TranslationUnresolvedEntry>())
            {
                if (entry == null) continue;
                if (!TryGetCurrentUnresolvedSource(entry, rawDefsByPackage, out string currentSource) ||
                    !string.Equals(currentSource ?? string.Empty, entry.SourceText ?? string.Empty, StringComparison.Ordinal))
                {
                    staleEntry = entry;
                    return false;
                }
            }

            return true;
        }

        private static bool TryGetCurrentUnresolvedSource(
            TranslationUnresolvedEntry entry,
            Dictionary<string, Dictionary<string, Dictionary<string, string>>> rawDefsByPackage,
            out string sourceText)
        {
            sourceText = string.Empty;
            if (entry == null || string.IsNullOrWhiteSpace(entry.Key)) return false;

            if (!string.IsNullOrWhiteSpace(entry.SourceFile) && File.Exists(entry.SourceFile) &&
                TryLoadUnresolvedLanguageFile(entry.SourceFile, out Dictionary<string, string> directData, out string _) &&
                directData.TryGetValue(entry.Key, out sourceText))
            {
                return true;
            }

            if (!string.Equals(entry.Bucket, "DefInjected", StringComparison.OrdinalIgnoreCase)) return false;
            ModMetaData mod = FindInstalledMod(entry.PackageId);
            if (mod == null) return false;

            Dictionary<string, Dictionary<string, string>> rawDefs;
            if (!rawDefsByPackage.TryGetValue(entry.PackageId ?? string.Empty, out rawDefs))
            {
                rawDefs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string defsRoot in GetAllEffectiveDefsPaths(mod))
                {
                    Dictionary<string, Dictionary<string, string>> extracted =
                        ExtractEnglishFromRawDefs(defsRoot, includePolicyCandidates: true);
                    foreach (KeyValuePair<string, Dictionary<string, string>> typePair in extracted)
                    {
                        if (!rawDefs.TryGetValue(typePair.Key, out Dictionary<string, string> values))
                        {
                            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            rawDefs[typePair.Key] = values;
                        }
                        foreach (KeyValuePair<string, string> pair in typePair.Value) values[pair.Key] = pair.Value;
                    }
                }
                rawDefsByPackage[entry.PackageId ?? string.Empty] = rawDefs;
            }

            return rawDefs.TryGetValue(entry.DefType ?? string.Empty, out Dictionary<string, string> defValues) &&
                defValues.TryGetValue(entry.Key, out sourceText);
        }

        private static bool TryLoadUnresolvedLanguageFile(
            string path,
            out Dictionary<string, string> data,
            out string error)
        {
            data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "The translation XML path is missing.";
                return false;
            }
            if (!File.Exists(path)) return true;

            try
            {
                XmlReaderSettings settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                    XmlResolver = null
                };
                XmlDocument document = new XmlDocument { XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(path, settings)) document.Load(reader);
                if (document.DocumentElement == null ||
                    !string.Equals(document.DocumentElement.Name, "LanguageData", StringComparison.Ordinal))
                {
                    error = "The existing translation XML does not have a LanguageData root; it was not overwritten.";
                    return false;
                }

                foreach (XmlNode node in document.DocumentElement.ChildNodes)
                {
                    if (node.NodeType != XmlNodeType.Element) continue;
                    string value = node.InnerText ?? string.Empty;
                    data[node.Name] = value.Replace("\\n", "\n").Replace("\\r", "\r").Replace("/n", "\n");
                }
                return true;
            }
            catch (Exception ex)
            {
                data.Clear();
                error = "The existing translation XML could not be read and was not overwritten: " + ex.Message;
                return false;
            }
        }

        private static ModMetaData FindInstalledMod(string packageId)
        {
            return ModLister.AllInstalledMods.FirstOrDefault(mod =>
                mod != null && string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
        }

        private static void MarkResolvedPackagesAsTranslated(IEnumerable<TranslationUnresolvedEntry> entries)
        {
            foreach (string packageId in (entries ?? Enumerable.Empty<TranslationUnresolvedEntry>())
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.PackageId))
                .Select(entry => entry.PackageId)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (TranslationUnresolvedManager.HasPendingForPackage(
                        packageId,
                        AutoTranslatorMod.Settings.TargetLang.ToString())) continue;
                ModMetaData mod = FindInstalledMod(packageId);
                if (mod == null || mod.RootDir == null) continue;
                ModUpdateDetector.MarkModAsTranslated(packageId, mod.RootDir.FullName, false);
            }

            ModUpdateDetector.ClearStatusCache();
        }

        private static string GetUnresolvedPackLanguageRoot(TranslationUnresolvedEntry entry)
        {
            try
            {
                DirectoryInfo directory = Directory.GetParent(entry.TargetFile);
                if (directory == null) throw new InvalidOperationException();

                if (string.Equals(entry.Bucket, "Keyed", StringComparison.OrdinalIgnoreCase))
                    return directory.Parent != null ? directory.Parent.FullName : directory.FullName;

                if (string.Equals(entry.Bucket, "DefInjected", StringComparison.OrdinalIgnoreCase) &&
                    directory.Parent != null && directory.Parent.Parent != null)
                {
                    return directory.Parent.Parent.FullName;
                }
            }
            catch
            {
            }

            return Path.Combine(
                GetLocalPackPath(),
                "Languages",
                GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang));
        }
    }
}
