using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Verse;
using Verse.AI;

namespace AutoTranslator_Core
{
    [HarmonyPatch(typeof(Job), nameof(Job.GetReport), new Type[] { typeof(Pawn) })]
    public static class Patch_Job_GetReport_AutoTranslation
    {
        private static readonly Dictionary<string, string> OriginalReportCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        public static void Postfix(Job __instance, Pawn driverPawn, ref string __result)
        {
            if (AutoTranslatorMod.Settings == null || __instance == null || string.IsNullOrWhiteSpace(__result)) return;
            if (LanguageDetector.LooksLikeTargetLanguage(__result, AutoTranslatorMod.Settings.TargetLang)) return;

            if (TryGetTranslatedDefReport(__instance, __result, out string translatedReport))
            {
                __result = translatedReport;
            }
        }

        private static bool TryGetTranslatedDefReport(Job job, string currentReport, out string translatedReport)
        {
            translatedReport = null;
            JobDef def = job.def;
            if (def == null || string.IsNullOrWhiteSpace(def.reportString)) return false;

            string translatedTemplate = def.reportString.Trim();
            if (!LanguageDetector.LooksLikeTargetLanguage(translatedTemplate, AutoTranslatorMod.Settings.TargetLang)) return false;

            string originalTemplate = GetOriginalReportString(def);
            if (string.IsNullOrWhiteSpace(originalTemplate)) return false;
            if (!ReportsLookEquivalent(currentReport, originalTemplate)) return false;

            try
            {
                translatedReport = JobUtility.GetResolvedJobReport(translatedTemplate, job.targetA, job.targetB, job.targetC);
            }
            catch
            {
                translatedReport = translatedTemplate;
            }

            return !string.IsNullOrWhiteSpace(translatedReport)
                && !string.Equals(translatedReport.Trim(), currentReport.Trim(), StringComparison.Ordinal);
        }

        private static string GetOriginalReportString(JobDef def)
        {
            string cacheKey = BuildCacheKey(def);
            lock (CacheLock)
            {
                if (OriginalReportCache.TryGetValue(cacheKey, out string cached))
                {
                    return string.IsNullOrEmpty(cached) ? null : cached;
                }
            }

            string found = FindOriginalReportString(def);
            lock (CacheLock)
            {
                OriginalReportCache[cacheKey] = found ?? string.Empty;
            }
            return found;
        }

        private static string BuildCacheKey(JobDef def)
        {
            string packageId = def.modContentPack?.PackageId ?? string.Empty;
            string root = def.modContentPack?.RootDir ?? string.Empty;
            return packageId + "|" + root + "|" + (def.defName ?? string.Empty);
        }

        private static string FindOriginalReportString(JobDef def)
        {
            string defName = def.defName;
            string rootDir = def.modContentPack?.RootDir;
            if (string.IsNullOrWhiteSpace(defName) || string.IsNullOrWhiteSpace(rootDir)) return null;

            string defsDir = Path.Combine(rootDir, "Defs");
            if (!Directory.Exists(defsDir)) return null;

            IEnumerable<string> files;
            try
            {
                files = Directory.GetFiles(defsDir, "*.xml", SearchOption.AllDirectories);
            }
            catch
            {
                return null;
            }

            foreach (string file in files)
            {
                string report = TryReadJobReportFromFile(file, defName);
                if (!string.IsNullOrWhiteSpace(report)) return report;
            }

            return null;
        }

        private static string TryReadJobReportFromFile(string file, string defName)
        {
            try
            {
                XDocument doc = XDocument.Load(file, LoadOptions.None);
                foreach (XElement jobDef in doc.Descendants("JobDef"))
                {
                    string currentDefName = jobDef.Elements("defName").FirstOrDefault()?.Value?.Trim();
                    if (!string.Equals(currentDefName, defName, StringComparison.OrdinalIgnoreCase)) continue;

                    return jobDef.Elements("reportString").FirstOrDefault()?.Value?.Trim();
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static bool ReportsLookEquivalent(string currentReport, string originalTemplate)
        {
            string current = NormalizeReportForCompare(currentReport);
            string original = NormalizeReportForCompare(originalTemplate);
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(original)) return false;

            return string.Equals(current, original, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeReportForCompare(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            string normalized = text.Trim().TrimEnd('.', '!', '?', ':', ';', '。', '！', '？', '：', '；');
            normalized = Regex.Replace(normalized, @"\s+", " ");
            return normalized.Trim();
        }
    }
}
