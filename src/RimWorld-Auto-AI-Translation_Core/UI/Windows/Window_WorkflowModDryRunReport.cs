using AutoTranslator_Core.Workflow;
using AutoTranslator_Core.Workflow.AI;
using AutoTranslator_Core.Workflow.DryRun;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class Window_WorkflowModDryRunReport : Window
    {
        private readonly string _modName;
        private readonly DryRunReport _report;
        private Vector2 _scroll = Vector2.zero;

        public Window_WorkflowModDryRunReport(string modName, DryRunReport report)
        {
            _modName = string.IsNullOrWhiteSpace(modName) ? "Mod" : modName;
            _report = report ?? new DryRunReport();
            if (_report.AiReview == null) _report.AiReview = new AiStepEstimate();
            if (_report.AiTranslation == null) _report.AiTranslation = new AiStepEstimate();
            if (_report.Range == null) _report.Range = new DryRunRangeSummary();
            if (_report.Range.AiReviewRangeByClassificationAndSource == null)
                _report.Range.AiReviewRangeByClassificationAndSource =
                    new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            draggable = true;
        }

        public override Vector2 InitialSize => new Vector2(780f, 610f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f),
                _modName + AutoTranslatorMod.WfText(" · 试跑详情", " · Dry-run details"));
            Text.Font = GameFont.Tiny;
            GUI.color = Color.grey;
            Widgets.Label(new Rect(inRect.x, inRect.y + 31f, inRect.width, 22f),
                AutoTranslatorMod.WfText("生成时间：", "Created: ") +
                _report.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
                "　·　" + AutoTranslatorMod.WfText("估算器：", "Estimator: ") + _report.EstimatorVersion);
            GUI.color = Color.white;

            Rect outRect = new Rect(inRect.x, inRect.y + 58f, inRect.width, inRect.height - 62f);
            bool hasWarning = _report.MissingLocalAnalysis || !string.IsNullOrWhiteSpace(_report.SkippedReason);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 18f, hasWarning ? 524f : 468f);
            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
            float y = 0f;

            if (hasWarning)
            {
                Rect warning = new Rect(0f, y, viewRect.width, 48f);
                Widgets.DrawBoxSolid(warning, new Color(0.22f, 0.14f, 0.06f, 0.9f));
                WorkflowUiStyle.DrawBorder(warning, WorkflowUiStyle.WarningText);
                GUI.color = WorkflowUiStyle.WarningText;
                string warningText = _report.MissingLocalAnalysis
                    ? AutoTranslatorMod.WfText("本次试跑缺少有效的本地分析结果。", "This dry run was missing valid local analysis.")
                    : AutoTranslatorMod.WfText("本次试跑已跳过：", "Skipped: ") + _report.SkippedReason;
                Widgets.Label(new Rect(warning.x + 9f, warning.y + 8f, warning.width - 18f, 32f), warningText);
                GUI.color = Color.white;
                y += 56f;
            }

            DrawTotalCards(new Rect(0f, y, viewRect.width, 76f));
            y += 84f;
            DrawTranslationRange(new Rect(0f, y, viewRect.width, 82f));
            y += 90f;
            DrawAiReviewDetail(new Rect(0f, y, viewRect.width, 142f));
            y += 150f;
            DrawAiTranslationDetail(new Rect(0f, y, viewRect.width, 126f));
            Widgets.EndScrollView();
        }

        private void DrawTotalCards(Rect rect)
        {
            const float gap = 8f;
            float width = (rect.width - gap * 2f) / 3f;
            DrawTotalCard(new Rect(rect.x, rect.y, width, rect.height),
                AutoTranslatorMod.WfText("AI 复核", "AI review"), _report.AiReview.TotalTokens,
                _report.AiReview.InputTokens, _report.AiReview.OutputTokens);
            DrawTotalCard(new Rect(rect.x + width + gap, rect.y, width, rect.height),
                AutoTranslatorMod.WfText("AI 翻译", "AI translation"), _report.AiTranslation.TotalTokens,
                _report.AiTranslation.InputTokens, _report.AiTranslation.OutputTokens);
            Rect budget = new Rect(rect.x + (width + gap) * 2f, rect.y, width, rect.height);
            Widgets.DrawBoxSolid(budget, WorkflowUiStyle.RaisedPanel);
            WorkflowUiStyle.DrawBorder(budget, new Color(0.31f, 0.35f, 0.38f));
            GUI.color = Color.grey;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(budget.x + 8f, budget.y + 6f, budget.width - 16f, 18f),
                AutoTranslatorMod.WfText("预计总预算", "Estimated budget"));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(budget.x + 8f, budget.y + 25f, budget.width - 16f, 23f),
                _report.ProtectedBudgetTokens.ToString("N0") + " Token");
            Text.Font = GameFont.Tiny;
            GUI.color = Color.grey;
            Widgets.Label(new Rect(budget.x + 8f, budget.y + 50f, budget.width - 16f, 18f),
                AutoTranslatorMod.WfText(
                    $"输入 {_report.ProtectedInputBudgetTokens:N0} · 输出 {_report.ProtectedOutputBudgetTokens:N0} · ×{DryRunReport.BudgetProtectionRatio:0.0}",
                    $"Input {_report.ProtectedInputBudgetTokens:N0} · Output {_report.ProtectedOutputBudgetTokens:N0} · ×{DryRunReport.BudgetProtectionRatio:0.0}"));
            GUI.color = Color.white;
        }

        private static void DrawTotalCard(Rect rect, string title, long total, long input, long output)
        {
            Widgets.DrawBoxSolid(rect, WorkflowUiStyle.RaisedPanel);
            WorkflowUiStyle.DrawBorder(rect, new Color(0.31f, 0.35f, 0.38f));
            GUI.color = Color.grey;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, 18f), title);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rect.x + 8f, rect.y + 25f, rect.width - 16f, 23f),
                total.ToString("N0") + " Token");
            Text.Font = GameFont.Tiny;
            GUI.color = Color.grey;
            Widgets.Label(new Rect(rect.x + 8f, rect.y + 50f, rect.width - 16f, 18f),
                AutoTranslatorMod.WfText($"输入 {input:N0} · 输出 {output:N0}", $"Input {input:N0} · Output {output:N0}"));
            GUI.color = Color.white;
        }

        private void DrawTranslationRange(Rect rect)
        {
            DrawPanel(rect, AutoTranslatorMod.WfText("AI 翻译数据范围", "AI translation range"));
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 29f, rect.width - 20f, 20f),
                AutoTranslatorMod.WfText(
                    $"需要翻译且没有有效译文：{_report.Range.NeedsTranslationWithoutValidTranslation:N0} 条",
                    $"Needs translation without a valid translation: {_report.Range.NeedsTranslationWithoutValidTranslation:N0}"));
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 49f, rect.width - 20f, 20f),
                AutoTranslatorMod.WfText(
                    $"待判定且没有有效译文：{_report.Range.UndeterminedWithoutValidTranslation:N0} 条 · 按 {_report.UndeterminedTranslationRatio:P0} 纳入；已有有效译文：{_report.Range.CoveredByValidTranslation:N0} 条",
                    $"Undetermined without valid translation: {_report.Range.UndeterminedWithoutValidTranslation:N0} · {_report.UndeterminedTranslationRatio:P0} included; valid translations: {_report.Range.CoveredByValidTranslation:N0}"));
        }

        private void DrawAiReviewDetail(Rect rect)
        {
            DrawPanel(rect, AutoTranslatorMod.WfText("AI 复核明细", "AI review details"));
            Text.Font = GameFont.Tiny;
            float y = rect.y + 29f;
            string[] classifications = { "NeedsTranslation", "Undetermined", "NoTranslationNeeded" };
            foreach (string classification in classifications)
            {
                Widgets.Label(new Rect(rect.x + 10f, y, rect.width - 20f, 19f),
                    FormatReviewRange(classification));
                y += 19f;
            }
            GUI.color = Color.grey;
            Widgets.Label(new Rect(rect.x + 10f, rect.yMax - 28f, rect.width - 20f, 20f),
                AutoTranslatorMod.WfText(
                    $"进入复核 {_report.AiReview.CandidateCount:N0} 条 · 输入 {_report.AiReview.InputTokens:N0} · 输出 {_report.AiReview.OutputTokens:N0} · 合计 {_report.AiReview.TotalTokens:N0} Token",
                    $"Review candidates {_report.AiReview.CandidateCount:N0} · input {_report.AiReview.InputTokens:N0} · output {_report.AiReview.OutputTokens:N0} · total {_report.AiReview.TotalTokens:N0} Token"));
            GUI.color = Color.white;
        }

        private void DrawAiTranslationDetail(Rect rect)
        {
            DrawPanel(rect, AutoTranslatorMod.WfText("AI 翻译明细", "AI translation details"));
            Text.Font = GameFont.Tiny;
            long needs = GetCount(_report.AiTranslation.CountsByClassification, "NeedsTranslation");
            long undetermined = GetCount(_report.AiTranslation.CountsByClassification, "Undetermined");
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 30f, rect.width - 20f, 20f),
                AutoTranslatorMod.WfText($"需要翻译：{needs:N0} 条", $"Needs translation: {needs:N0}"));
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 50f, rect.width - 20f, 20f),
                AutoTranslatorMod.WfText(
                    $"待判定抽样：{undetermined:N0} 条（按 {_report.UndeterminedTranslationRatio:P0} 纳入）",
                    $"Undetermined sample: {undetermined:N0} ({_report.UndeterminedTranslationRatio:P0} included)"));
            GUI.color = Color.grey;
            Widgets.Label(new Rect(rect.x + 10f, rect.yMax - 30f, rect.width - 20f, 20f),
                AutoTranslatorMod.WfText(
                    $"进入翻译 {_report.AiTranslation.CandidateCount:N0} 条 · 输入 {_report.AiTranslation.InputTokens:N0} · 输出 {_report.AiTranslation.OutputTokens:N0} · 合计 {_report.AiTranslation.TotalTokens:N0} Token",
                    $"Translation candidates {_report.AiTranslation.CandidateCount:N0} · input {_report.AiTranslation.InputTokens:N0} · output {_report.AiTranslation.OutputTokens:N0} · total {_report.AiTranslation.TotalTokens:N0} Token"));
            GUI.color = Color.white;
        }

        private string FormatReviewRange(string classification)
        {
            string label = classification == "NeedsTranslation"
                ? AutoTranslatorMod.WfText("需要翻译", "Needs translation")
                : classification == "NoTranslationNeeded"
                    ? AutoTranslatorMod.WfText("无需翻译", "No translation needed")
                    : AutoTranslatorMod.WfText("待判定", "Undetermined");
            if (!_report.Range.AiReviewRangeByClassificationAndSource.TryGetValue(
                    classification, out Dictionary<string, long> sources) || sources == null)
                return label + AutoTranslatorMod.WfText("：0 条", ": 0");
            long total = sources.Values.Sum();
            string detail = string.Join("｜", sources.Where(pair => pair.Value > 0)
                .OrderBy(pair => SourceOrder(pair.Key))
                .Select(pair => SourceLabel(pair.Key) + " " + pair.Value.ToString("N0")));
            return label + AutoTranslatorMod.WfText($"：{total:N0} 条", $": {total:N0}") +
                   (string.IsNullOrEmpty(detail) ? string.Empty : "（" + detail + "）");
        }

        private static int SourceOrder(string source)
        {
            if (source == "Manual") return 0;
            if (source == "AiReview") return 1;
            if (source == "Dll") return 2;
            if (source == "Xml") return 3;
            return 4;
        }

        private static string SourceLabel(string source)
        {
            if (source == "Manual") return AutoTranslatorMod.WfText("手动", "Manual");
            if (source == "AiReview") return AutoTranslatorMod.WfText("AI 复核", "AI review");
            if (source == "Dll") return AutoTranslatorMod.WfText("DLL 分析", "DLL analysis");
            if (source == "Xml") return AutoTranslatorMod.WfText("XML 分析", "XML analysis");
            return AutoTranslatorMod.WfText("系统默认", "System fallback");
        }

        private static long GetCount(IDictionary<string, long> values, string key)
        {
            return values != null && values.TryGetValue(key, out long count) ? count : 0L;
        }

        private static void DrawPanel(Rect rect, string title)
        {
            Widgets.DrawBoxSolid(rect, WorkflowUiStyle.Panel);
            WorkflowUiStyle.DrawBorder(rect, new Color(0.28f, 0.31f, 0.33f));
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rect.x + 9f, rect.y + 5f, rect.width - 18f, 23f), title);
        }
    }
}
