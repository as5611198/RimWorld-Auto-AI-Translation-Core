using System;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private static long BeginTranslationPolicyAgentRun()
        {
            return TranslationPolicyAgentCoordinator.BeginRun(AutoTranslatorMod.Settings);
        }

        private static void EndTranslationPolicyAgentRun(long runId)
        {
            TranslationPolicyAgentCoordinator.EndRun(runId);
        }

        public static bool ClearTranslationPolicyAgentCache()
        {
            try
            {
                TranslationPolicyAgentCoordinator.ClearCache();
                return true;
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[AutoTranslationCore] Policy Agent cache clear failed: " + ex.Message);
                AutoTranslatorSettings.AddErrorLog("ATC_PolicyAgent_CacheClearFailed".Translate());
                return false;
            }
        }
    }
}
