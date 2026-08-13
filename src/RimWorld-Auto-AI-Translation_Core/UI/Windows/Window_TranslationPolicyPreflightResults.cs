using AutoTranslator_Core.TargetedHardcodedUi;
using AutoTranslator_Core.TranslationPolicy;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal sealed class Window_TranslationPolicyPreflightResults : Window
    {
        private const float RowHeight = 88f;
        private readonly string _packageId;
        private Vector2 _scrollPosition;

        internal Window_TranslationPolicyPreflightResults(string packageId = null)
        {
            _packageId = packageId ?? string.Empty;
            doCloseX = true;
            doCloseButton = true;
            absorbInputAroundWindow = true;
            forcePause = true;
        }

        public override Vector2 InitialSize => new Vector2(980f, 700f);

        public override void DoWindowContents(Rect inRect)
        {
            if (!TranslationPolicyPreflightResultCache.TryGetLatest(out TranslationPolicyPreflightSnapshot snapshot))
            {
                Widgets.Label(inRect, "ATC_PolicyPreflight_NoResult".Translate());
                return;
            }

            if (!string.IsNullOrWhiteSpace(_packageId))
            {
                DrawSingleMod(inRect, snapshot);
                return;
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "ATC_PolicyPreflight_ResultTitle".Translate());
            Text.Font = GameFont.Small;
            string time = snapshot.GeneratedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            Widgets.Label(
                new Rect(0f, 38f, inRect.width, 54f),
                "ATC_PolicyPreflight_OverallSummary".Translate(
                    snapshot.Mods.Count,
                    snapshot.TotalCandidates,
                    snapshot.LocalAllows,
                    snapshot.LocalDenies,
                    snapshot.Ambiguous,
                    snapshot.ScannedXmlFiles,
                    snapshot.ScanErrors,
                    time));

            List<TranslationPolicyPreflightModResult> mods = snapshot.Mods.Values
                .Where(item => item != null)
                .OrderBy(item => item.ModName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Rect outRect = new Rect(0f, 100f, inRect.width, inRect.height - 100f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 18f, Math.Max(outRect.height, mods.Count * RowHeight));
            Widgets.BeginScrollView(outRect, ref _scrollPosition, viewRect);
            for (int index = 0; index < mods.Count; index++)
                DrawModRow(mods[index], new Rect(0f, index * RowHeight, viewRect.width, RowHeight));
            Widgets.EndScrollView();
        }

        private void DrawSingleMod(Rect inRect, TranslationPolicyPreflightSnapshot snapshot)
        {
            if (!snapshot.Mods.TryGetValue(_packageId, out TranslationPolicyPreflightModResult mod) || mod == null)
            {
                Widgets.Label(inRect, "ATC_PolicyPreflight_ModNoResult".Translate(_packageId));
                return;
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f),
                string.IsNullOrWhiteSpace(mod.ModName) ? mod.PackageId : mod.ModName);
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 38f, inRect.width, 92f), BuildModSummary(mod));

            float dllTop = 134f;
            if (HardcodedUiBatchScanCoordinator.TryGet(mod.PackageId, out HardcodedUiBatchScanSummary dll))
            {
                string dllText = string.IsNullOrWhiteSpace(dll.Error)
                    ? "ATC_PolicyPreflight_DllSummary".Translate(
                        dll.AssemblyCount,
                        dll.MethodCount,
                        dll.CandidateCount,
                        dll.DiagnosticCount,
                        dll.TranslateCount,
                        dll.DoNotTranslateCount,
                        dll.UncertainCount,
                        dll.UserOverrideCount,
                        dll.AnalyzerVersion).ToString()
                    : "ATC_PolicyPreflight_DllError".Translate(dll.Error).ToString();
                if (string.IsNullOrWhiteSpace(dll.Error) && dll.CandidateCount == 0)
                    dllText += "\n" + "ATC_HardcodedUi_NoCandidates".Translate();
                Widgets.Label(new Rect(0f, dllTop, inRect.width - 250f, 74f), dllText);
                if (Widgets.ButtonText(new Rect(inRect.width - 240f, dllTop + 12f, 240f, 34f), "ATC_HardcodedUi_BatchDetails".Translate()))
                {
                    ModMetaData target = ModLister.AllInstalledMods.FirstOrDefault(item =>
                        item != null && string.Equals(item.PackageId, mod.PackageId, StringComparison.OrdinalIgnoreCase));
                    if (target != null) Find.WindowStack.Add(new Window_HardcodedUiWorkbench(target));
                }
            }
            else
            {
                Widgets.Label(new Rect(0f, dllTop, inRect.width, 60f), "ATC_PolicyPreflight_DllNotScanned".Translate());
            }

            float diagnosticsTop = 218f;
            Widgets.DrawLineHorizontal(0f, diagnosticsTop - 8f, inRect.width);
            Widgets.Label(new Rect(0f, diagnosticsTop, inRect.width, 28f),
                "ATC_PolicyPreflight_DiagnosticSamples".Translate(mod.DiagnosticSamples.Count));
            List<TranslationPolicyCandidateResult> samples = mod.DiagnosticSamples
                .Where(item => item != null)
                .ToList();
            Rect outRect = new Rect(0f, diagnosticsTop + 30f, inRect.width, inRect.height - diagnosticsTop - 30f);
            const float sampleHeight = 72f;
            Rect viewRect = new Rect(0f, 0f, outRect.width - 18f,
                Math.Max(outRect.height, samples.Count * sampleHeight));
            Widgets.BeginScrollView(outRect, ref _scrollPosition, viewRect);
            for (int index = 0; index < samples.Count; index++)
                DrawDiagnosticSample(samples[index], new Rect(0f, index * sampleHeight, viewRect.width, sampleHeight));
            Widgets.EndScrollView();
        }

        private static void DrawDiagnosticSample(TranslationPolicyCandidateResult sample, Rect rect)
        {
            Widgets.DrawHighlightIfMouseover(rect);
            string decision = sample.Decision == TranslationPolicyDecision.HardAllow
                ? "ATC_PolicyPreflight_DecisionAllow".Translate().ToString()
                : sample.Decision == TranslationPolicyDecision.HardDeny
                    ? "ATC_PolicyPreflight_DecisionDeny".Translate().ToString()
                    : "ATC_PolicyPreflight_DecisionUncertain".Translate().ToString();
            string source = string.IsNullOrWhiteSpace(sample.KeyOrPath)
                ? sample.SourceFile
                : sample.KeyOrPath;
            Widgets.Label(new Rect(rect.x + 4f, rect.y + 2f, rect.width - 8f, rect.height - 4f),
                decision + " | " + sample.ReasonCode + " | " + source +
                "\n" + (sample.SourceText ?? string.Empty));
            TooltipHandler.TipRegion(rect,
                (sample.SourceFile ?? string.Empty) + "\n" +
                (sample.NormalizedPath ?? string.Empty) + "\n" +
                (sample.CandidateId ?? string.Empty));
        }

        private static void DrawModRow(TranslationPolicyPreflightModResult mod, Rect rect)
        {
            Widgets.DrawHighlightIfMouseover(rect);
            string title = string.IsNullOrWhiteSpace(mod.ModName) ? mod.PackageId : mod.ModName;
            Widgets.Label(new Rect(rect.x + 4f, rect.y + 4f, rect.width - 134f, rect.height - 8f),
                title + "\n<size=10><color=#888888>" + mod.PackageId + "</color></size>\n" + BuildModSummary(mod));
            if (Widgets.ButtonText(new Rect(rect.xMax - 124f, rect.y + 24f, 120f, 34f),
                    "ATC_PolicyPreflight_Details".Translate()))
            {
                Find.WindowStack.Add(new Window_TranslationPolicyPreflightResults(mod.PackageId));
            }
        }

        private static string BuildModSummary(TranslationPolicyPreflightModResult mod)
        {
            return "ATC_PolicyPreflight_ModSummary".Translate(
                mod.XmlCandidates,
                mod.LocalAllows,
                mod.LocalDenies,
                mod.Ambiguous,
                mod.CloudAllows,
                mod.CloudDenies,
                mod.AgentAllows,
                mod.AgentDenies,
                mod.AgentReviews,
                mod.Unresolved,
                mod.FinalTranslationCandidates).ToString();
        }
    }
}
