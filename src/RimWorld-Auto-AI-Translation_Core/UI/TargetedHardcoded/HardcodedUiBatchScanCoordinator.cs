using RimWorld;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal sealed class HardcodedUiBatchScanSummary
    {
        internal string PackageId = string.Empty;
        internal int AssemblyCount;
        internal int MethodCount;
        internal int CandidateCount;
        internal int DiagnosticCount;
        internal int AnalyzerVersion;
        internal int TranslateCount;
        internal int DoNotTranslateCount;
        internal int UncertainCount;
        internal int UserOverrideCount;
        internal string Error = string.Empty;
        internal HardcodedUiScanResult Result;
    }

    internal static class HardcodedUiBatchScanCoordinator
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, HardcodedUiBatchScanSummary> Results =
            new Dictionary<string, HardcodedUiBatchScanSummary>(StringComparer.OrdinalIgnoreCase);

        internal static bool TryGet(string packageId, out HardcodedUiBatchScanSummary summary)
        {
            lock (Gate)
                return Results.TryGetValue(packageId ?? string.Empty, out summary);
        }

        internal static void RefreshDecisionCounts(HardcodedUiBatchScanSummary summary)
        {
            if (summary?.Result == null) return;
            summary.TranslateCount = summary.Result.Decisions.Values.Count(record =>
                record.EffectiveDecision == HardcodedUiAutomaticDecision.Translate);
            summary.DoNotTranslateCount = summary.Result.Decisions.Values.Count(record =>
                record.EffectiveDecision == HardcodedUiAutomaticDecision.DoNotTranslate);
            summary.UncertainCount = summary.Result.Decisions.Values.Count(record =>
                record.EffectiveDecision == HardcodedUiAutomaticDecision.Uncertain);
            summary.UserOverrideCount = summary.Result.Decisions.Values.Count(record =>
                record.UserOverride != HardcodedUiUserOverride.None);
        }

        internal static async Task<List<HardcodedUiBatchScanSummary>> ScanActiveModsAsync(
            IEnumerable<ModMetaData> mods,
            Action<int, int, string> progress = null,
            Func<bool> cancellationRequested = null)
        {
            List<ModMetaData> targets = (mods ?? Enumerable.Empty<ModMetaData>())
                .Where(mod => mod != null && mod.Active && mod.RootDir != null &&
                              !string.IsNullOrWhiteSpace(mod.PackageId) &&
                              !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(mod.PackageId) &&
                              !string.Equals(mod.PackageId, "Auto.AITranslation.Core", StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(mod.PackageId, "Auto.AITranslation.Core.dev", StringComparison.OrdinalIgnoreCase))
                .GroupBy(mod => mod.PackageId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return await Task.Run(() =>
            {
                var completed = new List<HardcodedUiBatchScanSummary>();
                Dictionary<string, HardcodedUiPatchEntry> savedEntries = LoadSavedEntries();
                for (int index = 0; index < targets.Count; index++)
                {
                    if (AutoTranslatorSettings.IsCancellationRequested ||
                        (cancellationRequested != null && cancellationRequested())) break;
                    ModMetaData mod = targets[index];
                    progress?.Invoke(index, targets.Count, mod.Name ?? mod.PackageId);
                    HardcodedUiBatchScanSummary summary;
                    try
                    {
                        HardcodedUiScanResult result = HardcodedUiRuntimeScanner.Scan(mod);
                        ApplySavedTranslations(result, savedEntries);
                        summary = new HardcodedUiBatchScanSummary
                        {
                            PackageId = mod.PackageId,
                            AssemblyCount = result.AssemblyCount,
                            MethodCount = result.MethodCount,
                            CandidateCount = result.Entries.Count,
                            DiagnosticCount = result.Diagnostics.Count,
                            AnalyzerVersion = HardcodedUiIlDataflowAnalyzer.AnalyzerVersion,
                            TranslateCount = result.Decisions.Values.Count(record =>
                                record.EffectiveDecision == HardcodedUiAutomaticDecision.Translate),
                            DoNotTranslateCount = result.Decisions.Values.Count(record =>
                                record.EffectiveDecision == HardcodedUiAutomaticDecision.DoNotTranslate),
                            UncertainCount = result.Decisions.Values.Count(record =>
                                record.EffectiveDecision == HardcodedUiAutomaticDecision.Uncertain),
                            UserOverrideCount = result.Decisions.Values.Count(record =>
                                record.UserOverride != HardcodedUiUserOverride.None),
                            Result = result
                        };
                    }
                    catch (Exception ex)
                    {
                        summary = new HardcodedUiBatchScanSummary
                        {
                            PackageId = mod.PackageId,
                            Error = ex.Message
                        };
                        Verse.Log.Warning("[AutoTranslationCore] DLL batch scan failed for " +
                            mod.PackageId + ": " + ex);
                    }

                    lock (Gate) Results[mod.PackageId] = summary;
                    completed.Add(summary);
                    progress?.Invoke(index + 1, targets.Count, mod.Name ?? mod.PackageId);
                }
                return completed;
            });
        }

        private static Dictionary<string, HardcodedUiPatchEntry> LoadSavedEntries()
        {
            try
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
            catch (Exception ex)
            {
                Verse.Log.Warning("[AutoTranslationCore] DLL saved-translation index could not be loaded: " + ex.Message);
                return new Dictionary<string, HardcodedUiPatchEntry>(StringComparer.Ordinal);
            }
        }

        private static void ApplySavedTranslations(
            HardcodedUiScanResult result,
            IDictionary<string, HardcodedUiPatchEntry> savedEntries)
        {
            if (result?.Entries == null || savedEntries == null) return;
            foreach (HardcodedUiPatchEntry entry in result.Entries)
            {
                if (entry == null || !savedEntries.TryGetValue(entry.EntryId, out HardcodedUiPatchEntry saved))
                    continue;
                entry.Translations = saved.Translations != null
                    ? new Dictionary<string, string>(saved.Translations, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>();
            }
        }
    }
}
