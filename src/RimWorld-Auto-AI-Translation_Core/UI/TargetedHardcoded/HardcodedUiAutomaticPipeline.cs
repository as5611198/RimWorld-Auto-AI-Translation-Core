using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal static class HardcodedUiAutomaticPipeline
    {
        private const int TranslationBatchSize = 20;
        private static readonly object ManifestGate = new object();

        internal static async Task RunAsync(
            IEnumerable<ModMetaData> mods,
            bool enableAgent)
        {
            if (AutoTranslatorMod.Settings == null ||
                !AutoTranslatorMod.Settings.EnableHardcodedUiPrototype ||
                AutoTranslatorSettings.IsCancellationRequested)
                return;

            List<ModMetaData> selected = (mods ?? Enumerable.Empty<ModMetaData>())
                .Where(mod => mod != null && mod.Active && mod.RootDir != null &&
                              !string.IsNullOrWhiteSpace(mod.PackageId) &&
                              !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(mod.PackageId) &&
                              !string.Equals(mod.PackageId, "Auto.AITranslation.Core", StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(mod.PackageId, "Auto.AITranslation.Core.dev", StringComparison.OrdinalIgnoreCase))
                .GroupBy(mod => mod.PackageId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (selected.Count == 0) return;

            AutoTranslatorMod.Settings.SubTaskName = "ATC_HardcodedUi_AutoAnalyzing".Translate();
            List<HardcodedUiBatchScanSummary> summaries =
                await HardcodedUiBatchScanCoordinator.ScanActiveModsAsync(selected);
            bool enableCloudCache = AutoTranslatorSettings.IsPolicyAnalysisCloudCacheAvailable &&
                                    AutoTranslatorMod.Settings.EnablePolicyAnalysisCloudCache;
            bool ownsPolicyRun = false;
            long agentRunId = 0L;
            bool agentCompleted = false;
            try
            {
                if ((enableCloudCache || enableAgent) &&
                    !TranslationPolicyAgentCoordinator.IsEnabledForCurrentRun)
                {
                    agentRunId = TranslationPolicyAgentCoordinator.BeginRun(
                        AutoTranslatorMod.Settings,
                        enableCloudCache,
                        enableAgent);
                    ownsPolicyRun = agentRunId != 0L;
                }

                foreach (HardcodedUiBatchScanSummary summary in summaries)
                {
                    if (AutoTranslatorSettings.IsCancellationRequested) break;
                    if (summary?.Result == null) continue;
                    if (enableCloudCache || enableAgent)
                    {
                        List<HardcodedUiPatchEntry> pending =
                            HardcodedUiPolicyBridge.GetAgentCandidates(summary.Result);
                        if (pending.Count > 0)
                        {
                            string modName = selected.FirstOrDefault(mod => string.Equals(
                                mod.PackageId,
                                summary.PackageId,
                                StringComparison.OrdinalIgnoreCase))?.Name ?? summary.PackageId;
                            Dictionary<string, TranslationPolicy.TranslationPolicyAgentCandidateOutcome> outcomes =
                                await TranslationPolicyAgentCoordinator.ResolveCandidatesAsync(
                                    summary.PackageId,
                                    pending.Select(entry =>
                                        HardcodedUiPolicyBridge.CreateCandidate(entry, modName)),
                                    true,
                                    PolicyAnalysisCandidateDomain.Dll);
                            HardcodedUiPolicyBridge.ApplyAgentOutcomes(
                                summary.Result,
                                pending,
                                outcomes);
                        }
                    }
                    HardcodedUiBatchScanCoordinator.RefreshDecisionCounts(summary);
                }
                agentCompleted = !AutoTranslatorSettings.IsCancellationRequested;
            }
            finally
            {
                if (ownsPolicyRun)
                    await TranslationPolicyAgentCoordinator.EndRunAsync(agentRunId, agentCompleted);
            }

            if (AutoTranslatorSettings.IsCancellationRequested) return;
            AutoTranslatorMod.Settings.SubTaskName = "ATC_HardcodedUi_AutoTranslating".Translate();
            string languageFolder = AutoTranslatorScanner.GetFolderNameByLanguage(
                AutoTranslatorMod.Settings.TargetLang);
            HardcodedUiPatchManifest existingManifest = LoadManifest();
            Dictionary<string, HardcodedUiPatchEntry> existingById =
                (existingManifest.Entries ?? new List<HardcodedUiPatchEntry>())
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.EntryId))
                .GroupBy(entry => entry.EntryId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var replacementsByPackage = new Dictionary<string, List<HardcodedUiPatchEntry>>(
                StringComparer.OrdinalIgnoreCase);
            int translatedCount = 0;

            foreach (HardcodedUiBatchScanSummary summary in summaries)
            {
                if (AutoTranslatorSettings.IsCancellationRequested ||
                    TranslationUsageCoordinator.IsPausedByBudget) break;
                if (summary?.Result == null) continue;
                List<HardcodedUiPatchEntry> targets = summary.Result.Entries
                    .Where(entry => summary.Result.Decisions.TryGetValue(
                            entry.EntryId,
                            out HardcodedUiDecisionRecord decision) &&
                        decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate)
                    .ToList();
                foreach (HardcodedUiPatchEntry entry in targets)
                {
                    if (existingById.TryGetValue(entry.EntryId, out HardcodedUiPatchEntry saved) &&
                        saved.Translations != null)
                        entry.Translations = new Dictionary<string, string>(
                            saved.Translations,
                            StringComparer.OrdinalIgnoreCase);

                    bool needsTranslation = !entry.Translations.TryGetValue(
                            languageFolder,
                            out string currentValue) ||
                        string.IsNullOrWhiteSpace(currentValue) ||
                        string.Equals(currentValue.Trim(), entry.Literal, StringComparison.Ordinal);
                    if (needsTranslation &&
                        summary.Result.Decisions.TryGetValue(entry.EntryId, out HardcodedUiDecisionRecord decision) &&
                        decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate &&
                        HardcodedUiBuiltInDictionary.TryTranslate(
                            entry.Literal,
                            decision.SemanticRole,
                            AutoTranslatorMod.Settings.TargetLang,
                            out string dictionaryValue))
                    {
                        entry.Translations[languageFolder] = dictionaryValue;
                        translatedCount++;
                    }
                }

                CheckpointPackageTranslations(
                    existingManifest,
                    replacementsByPackage,
                    summary.PackageId,
                    targets);

                List<HardcodedUiPatchEntry> pending = targets
                    .Where(entry => !entry.Translations.TryGetValue(
                            languageFolder,
                            out string value) ||
                        string.IsNullOrWhiteSpace(value) ||
                        string.Equals(value.Trim(), entry.Literal, StringComparison.Ordinal))
                    .ToList();
                for (int offset = 0; offset < pending.Count; offset += TranslationBatchSize)
                {
                    if (AutoTranslatorSettings.IsCancellationRequested ||
                        TranslationUsageCoordinator.IsPausedByBudget) break;
                    List<HardcodedUiPatchEntry> batch = pending
                        .Skip(offset)
                        .Take(TranslationBatchSize)
                        .ToList();
                    List<string> translated = await AutoTranslatorAPI.TranslateBatchAsync(
                        batch.Select(entry => entry.Literal).ToList(),
                        packageId: summary.PackageId,
                        requestScope: "hardcoded-ui-auto/" + summary.PackageId + "/" + offset,
                        requestPurpose: "hardcoded-ui-auto");
                    if (translated == null || translated.Count != batch.Count)
                    {
                        if (AutoTranslatorSettings.IsCancellationRequested) break;
                        AutoTranslatorSettings.AddLog(
                            "ATC_HardcodedUi_AutoBatchFailed".Translate(summary.PackageId, batch.Count));
                        continue;
                    }
                    for (int index = 0; index < batch.Count; index++)
                    {
                        if (!AutoTranslatorScanner.TryAcceptTranslatedValue(
                                translated[index],
                                batch[index].Literal,
                                out string value,
                                out _,
                                out _))
                            continue;
                        batch[index].Translations[languageFolder] = value;
                        translatedCount++;
                    }

                    CheckpointPackageTranslations(
                        existingManifest,
                        replacementsByPackage,
                        summary.PackageId,
                        targets);
                }

                CheckpointPackageTranslations(
                    existingManifest,
                    replacementsByPackage,
                    summary.PackageId,
                    targets,
                    includeEmpty: true);
            }

            if (replacementsByPackage.Count == 0) return;
            SaveManifest(existingManifest, replacementsByPackage);
            if (AutoTranslatorSettings.IsCancellationRequested ||
                TranslationUsageCoordinator.IsPausedByBudget)
            {
                HardcodedUiTargetedPatchManager.RequestReload();
                return;
            }
            AutoTranslatorMod.Settings.EnableUIInterceptor = false;
            LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
            HardcodedUiTargetedPatchManager.RequestReload();
            AutoTranslatorSettings.AddLog(
                "ATC_HardcodedUi_AutoDone".Translate(
                    replacementsByPackage.Count,
                    translatedCount));
        }

        private static void CheckpointPackageTranslations(
            HardcodedUiPatchManifest manifest,
            IDictionary<string, List<HardcodedUiPatchEntry>> replacementsByPackage,
            string packageId,
            IEnumerable<HardcodedUiPatchEntry> targets,
            bool includeEmpty = false)
        {
            if (string.IsNullOrWhiteSpace(packageId) || replacementsByPackage == null)
                return;

            List<HardcodedUiPatchEntry> completed = (targets ??
                    Enumerable.Empty<HardcodedUiPatchEntry>())
                .Where(entry => entry != null &&
                                entry.Translations != null &&
                                entry.Translations.Count > 0)
                .Select(entry =>
                {
                    entry.Enabled = true;
                    return entry;
                })
                .ToList();
            if (completed.Count == 0 && !includeEmpty) return;

            replacementsByPackage[packageId] = completed;
            SaveManifest(
                manifest,
                new Dictionary<string, List<HardcodedUiPatchEntry>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [packageId] = completed
                });
        }

        private static HardcodedUiPatchManifest LoadManifest()
        {
            lock (ManifestGate)
            {
                string path = HardcodedUiTargetedPatchManager.ManifestPath;
                if (!File.Exists(path)) return new HardcodedUiPatchManifest();
                return JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(
                           File.ReadAllText(path)) ??
                       new HardcodedUiPatchManifest();
            }
        }

        private static void SaveManifest(
            HardcodedUiPatchManifest manifest,
            IDictionary<string, List<HardcodedUiPatchEntry>> replacementsByPackage)
        {
            lock (ManifestGate)
            {
                manifest = manifest ?? new HardcodedUiPatchManifest();
                manifest.Approved = true;
                HashSet<string> replaced = new HashSet<string>(
                    replacementsByPackage.Keys,
                    StringComparer.OrdinalIgnoreCase);
                manifest.Entries = (manifest.Entries ?? new List<HardcodedUiPatchEntry>())
                    .Where(entry => entry != null && !replaced.Contains(entry.PackageId ?? string.Empty))
                    .Concat(replacementsByPackage.Values.SelectMany(entries => entries))
                    .GroupBy(entry => entry.EntryId, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(entry => entry.EntryId, StringComparer.Ordinal)
                    .ToList();
                if (manifest.Entries.Count > 10000)
                    throw new InvalidOperationException("Approved DLL UI manifest exceeds 10000 entries.");
                string path = HardcodedUiTargetedPatchManager.ManifestPath;
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(manifest, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                else File.Move(temporary, path);
            }
        }
    }
}
