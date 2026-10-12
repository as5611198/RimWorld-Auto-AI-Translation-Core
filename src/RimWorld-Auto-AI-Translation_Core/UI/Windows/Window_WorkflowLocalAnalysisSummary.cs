using AutoTranslator_Core.Workflow;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class Window_WorkflowLocalAnalysisSummary : Window
    {
        private readonly WorkflowModSummary _mod;

        public Window_WorkflowLocalAnalysisSummary(WorkflowModSummary mod)
        {
            _mod = mod ?? new WorkflowModSummary();
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            draggable = true;
        }

        public override Vector2 InitialSize => new Vector2(650f, 330f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 30f),
                AutoTranslatorMod.WfText("本地分析结果", "Local analysis results"));
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inRect.x, inRect.y + 34f, inRect.width, 36f),
                _mod.DisplayName + "\n" + _mod.PackageId);

            float y = inRect.y + 82f;
            DrawSource(new Rect(inRect.x, y, inRect.width, 72f), "XML", _mod.XmlAnalysis);
            DrawSource(new Rect(inRect.x, y + 78f, inRect.width, 72f), "DLL", _mod.DllAnalysis);
            Text.Font = GameFont.Small;
        }

        private static void DrawSource(Rect rect, string name, WorkflowAnalysisSourceSummary summary)
        {
            summary = summary ?? new WorkflowAnalysisSourceSummary();
            Widgets.DrawBoxSolid(rect, new Color(0.08f, 0.09f, 0.1f, 0.9f));
            Widgets.DrawBox(rect, 1);
            string status;
            if (summary.Freshness == AnalysisResultFreshness.NeverAnalyzed)
                status = AutoTranslatorMod.WfText("未分析", "Not analyzed");
            else if (summary.Freshness == AnalysisResultFreshness.Expired)
                status = BuildExpiredStatus(summary);
            else if (summary.Freshness == AnalysisResultFreshness.Failed)
                status = AutoTranslatorMod.WfText("任务失败", "Failed");
            else
                status = AutoTranslatorMod.WfText("当前有效", "Current");
            Widgets.Label(new Rect(rect.x + 9f, rect.y + 6f, rect.width - 18f, 19f),
                name + "　" + status);
            if (summary.Freshness == AnalysisResultFreshness.Current)
            {
                string counts = AutoTranslatorMod.WfText(
                    $"需要翻译 {summary.NeedsTranslation}　　待判定 {summary.Undetermined}　　无需翻译 {summary.NoTranslationNeeded}",
                    $"Needs {summary.NeedsTranslation}    Undetermined {summary.Undetermined}    No translation {summary.NoTranslationNeeded}");
                Widgets.Label(new Rect(rect.x + 9f, rect.y + 28f, rect.width - 18f, 18f), counts);
            }
            if (summary.Freshness == AnalysisResultFreshness.Failed && !string.IsNullOrWhiteSpace(summary.ErrorText))
            {
                GUI.color = new Color(1f, 0.45f, 0.45f);
                Widgets.Label(new Rect(rect.x + 9f, rect.y + 49f, rect.width - 18f, 18f),
                    AutoTranslatorMod.WfText("失败原因：", "Error: ") + summary.ErrorText);
                GUI.color = Color.white;
            }
        }

        private static string BuildExpiredStatus(WorkflowAnalysisSourceSummary summary)
        {
            List<string> reasons = new List<string>();
            if (!string.Equals(summary.AnalyzerVersion, summary.CurrentAnalyzerVersion, StringComparison.Ordinal))
                reasons.Add((summary.AnalyzerVersion ?? string.Empty) + " → " +
                            (summary.CurrentAnalyzerVersion ?? string.Empty));
            if (!string.Equals(
                    summary.ModVersionFingerprint,
                    summary.CurrentModVersionFingerprint,
                    StringComparison.Ordinal))
                reasons.Add(AutoTranslatorMod.WfText("Mod 内容已变化", "Mod content changed"));
            return AutoTranslatorMod.WfText("已过期", "Expired") +
                   (reasons.Count == 0 ? string.Empty : "　" + string.Join("；", reasons));
        }
    }
}
