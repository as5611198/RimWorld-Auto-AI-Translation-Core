using AutoTranslator_Core.TranslationPolicy;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private static bool BeginTranslationUsageRun(
            string runKind,
            IEnumerable<ModMetaData> mods)
        {
            AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
            if (settings == null || !settings.EnableTranslationUsageBudget)
            {
                TranslationUsageCoordinator.EndRun(false);
                return false;
            }

            List<string> packageIds = (mods ?? Enumerable.Empty<ModMetaData>())
                .Where(mod => mod != null && !string.IsNullOrWhiteSpace(mod.PackageId))
                .Select(mod => mod.PackageId.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            string canonical = string.Join("\n", new[]
            {
                runKind ?? string.Empty,
                settings.TargetLang.ToString(),
                string.Join("\n", packageIds)
            });
            string runKey = "translation_run_" + TranslationPolicyIdentity.ComputeSha256(canonical);
            string journalPath = Path.Combine(
                GetLocalPackPath(),
                "Cache",
                "TranslationUsageRun.v1.json");
            TranslationUsageCoordinator.BeginRun(
                journalPath,
                runKey,
                settings.TranslationBudgetSourceCharactersPerRun,
                settings.TranslationBudgetEstimatedTokensPerRun);
            return true;
        }

        private static void EndTranslationUsageRun(bool started, bool completed)
        {
            if (started) TranslationUsageCoordinator.EndRun(completed);
        }

        private static bool PauseScanIfTranslationBudgetReached(AutoTranslatorSettings settings)
        {
            if (!TranslationUsageCoordinator.IsPausedByBudget) return false;

            TranslationUsageSnapshot snapshot = TranslationUsageCoordinator.GetSnapshot();
            if (settings != null)
            {
                settings.CurrentTaskName = "Translation paused: usage budget reached";
                settings.SubTaskName = string.Empty;
            }
            AutoTranslatorSettings.AddLog(
                "⏸ Translation paused after safely retaining completed data. " +
                "Source characters: " + (snapshot?.CommittedSourceCharacters ?? 0L) +
                ", accounted tokens: " + (snapshot?.AccountedTokens ?? 0L) + ".");
            return true;
        }
    }
}
