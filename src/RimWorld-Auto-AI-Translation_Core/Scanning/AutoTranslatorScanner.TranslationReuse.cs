using System;
using System.Collections.Generic;
using System.IO;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private static readonly object TranslationResultCacheGate = new object();
        private static TranslationResultCache _translationResultCache;

        private static TranslationResultCache GetTranslationResultCache()
        {
            lock (TranslationResultCacheGate)
            {
                if (_translationResultCache != null) return _translationResultCache;
                _translationResultCache = new TranslationResultCache(Path.Combine(
                    GetLocalPackPath(),
                    "Cache",
                    "ValidatedTranslationResults.v1.json"));
                return _translationResultCache;
            }
        }

        private static bool TryUseCachedTranslation(
            string packageId,
            string source,
            out TranslationBatchItemResult result)
        {
            result = null;
            try
            {
                string targetLanguage = AutoTranslatorMod.Settings.TargetLang.ToString();
                if (!GetTranslationResultCache().TryGet(
                        packageId,
                        targetLanguage,
                        source,
                        out string cached))
                {
                    return false;
                }

                if (!TryAcceptTranslatedValue(
                        cached,
                        source,
                        out string sanitized,
                        out _,
                        out _))
                {
                    return false;
                }

                result = new TranslationBatchItemResult { Value = sanitized };
                return true;
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[AutoTranslationCore] Translation result cache read failed: " + ex.Message);
                return false;
            }
        }

        private static void CacheValidatedTranslations(
            string packageId,
            IEnumerable<KeyValuePair<string, string>> translations)
        {
            try
            {
                GetTranslationResultCache().PutRange(
                    packageId,
                    AutoTranslatorMod.Settings.TargetLang.ToString(),
                    translations);
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[AutoTranslationCore] Translation result cache write failed: " + ex.Message);
            }
        }

        public static bool ClearValidatedTranslationResultCache()
        {
            try
            {
                GetTranslationResultCache().Clear();
                return true;
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[AutoTranslationCore] Translation result cache clear failed: " + ex.Message);
                return false;
            }
        }
    }
}
