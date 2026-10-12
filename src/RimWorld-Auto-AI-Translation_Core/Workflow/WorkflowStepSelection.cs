using AutoTranslator_Core.Workflow.Analysis;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AutoTranslator_Core.Workflow
{
    internal static class WorkflowStepSelection
    {
        public static bool IsTranslationTarget(ModMetaData mod)
        {
            if (mod?.RootDir == null) return false;
            string packageId = (mod.PackageId ?? string.Empty).Trim();
            return !AutoTranslatorScanner.IsNonTranslatableSystemPackage(packageId);
        }

        public static List<ModMetaData> FilterTranslationTargets(IEnumerable<ModMetaData> mods)
        {
            List<ModMetaData> result = new List<ModMetaData>();
            foreach (ModMetaData mod in mods ?? Enumerable.Empty<ModMetaData>())
            {
                if (IsTranslationTarget(mod)) result.Add(mod);
                else if (mod != null && AutoTranslatorScanner.IsNonTranslatableSystemPackage(mod.PackageId))
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.target skipped package_id=" + (mod.PackageId ?? string.Empty) +
                        " reason=protected-system-package");
            }
            return result;
        }

        public static List<string> ResolveModIdentities(IEnumerable<ModMetaData> mods)
        {
            return FilterTranslationTargets(mods)
                .Select(ModAnalysisTargetFactory.CreateModIdentity)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }
}
