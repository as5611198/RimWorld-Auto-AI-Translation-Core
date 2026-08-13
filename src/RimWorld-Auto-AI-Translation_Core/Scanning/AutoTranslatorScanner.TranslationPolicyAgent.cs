using System;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private static long BeginTranslationPolicyAgentRun()
        {
            return TranslationPolicyAgentCoordinator.BeginRun(AutoTranslatorMod.Settings);
        }

        private static Task EndTranslationPolicyAgentRunAsync(long runId, bool completed)
        {
            return TranslationPolicyAgentCoordinator.EndRunAsync(runId, completed);
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
                Verse.Log.Warning("[AutoTranslationCore] Agent prediction cache clear failed: " + ex.Message);
                AutoTranslatorSettings.AddErrorLog("ATC_PolicyAgent_CacheClearFailed".Translate());
                return false;
            }
        }
    }
}
