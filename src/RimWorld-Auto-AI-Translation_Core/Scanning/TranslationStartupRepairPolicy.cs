namespace AutoTranslator_Core
{
    internal static class TranslationStartupRepairPolicy
    {
        internal static string DecideStartup(bool baselineExists, bool ownedPackChanged)
        {
            if (!baselineExists) return "repair-owned-pack";
            return ownedPackChanged ? "repair-delta" : "fast-inject";
        }

        internal static string DecidePipeline(bool producedOutput)
        {
            return producedOutput ? "repair-delta" : "no-op";
        }

        internal static bool ShouldAutoRepairObservation(bool automatic, bool owned, string sourceKind)
        {
            if (!automatic) return true;
            if (!owned) return false;
            return sourceKind != "third-party-read-overlay" &&
                   sourceKind != "native-duplicate-list" &&
                   sourceKind != "native-manual-list" &&
                   sourceKind != "native-config-list" &&
                   sourceKind != "native-general-error";
        }
    }
}
