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
    public sealed class Window_WorkflowDryRunReport : Window
    {
        private readonly DryRunReport _report;
        private readonly Dictionary<string, string> _modNames;
        private Vector2 _scroll = Vector2.zero;

        public Window_WorkflowDryRunReport(
            DryRunReport report,
            WorkflowWorkbenchSnapshot snapshot)
        {
            _report = report ?? new DryRunReport();
            if (_report.AiReview == null) _report.AiReview = new AiStepEstimate();
            if (_report.AiTranslation == null) _report.AiTranslation = new AiStepEstimate();
            if (_report.Range == null) _report.Range = new DryRunRangeSummary();
            if (_report.ModReports == null)
                _report.ModReports = new Dictionary<string, DryRunReport>(StringComparer.Ordinal);
            _modNames = (snapshot?.Mods ?? new List<WorkflowModSummary>())
                .Where(mod => !string.IsNullOrWhiteSpace(mod.ModIdentity))
                .GroupBy(mod => mod.ModIdentity, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.Ordinal);
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            draggable = true;
        }

        public override Vector2 InitialSize => new Vector2(880f, 650f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f),
                AutoTranslatorMod.WfText("试跑汇总报告", "Dry-run summary report"));
            Text.Font = GameFont.Tiny;
            GUI.color = Color.grey;
            Widgets.Label(new Rect(inRect.x, inRect.y + 31f, inRect.width, 22f),
                AutoTranslatorMod.WfText("生成时间：", "Created: ") +
                _report.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
                "　·　" + AutoTranslatorMod.WfText("估算器：", "Estimator: ") + _report.EstimatorVersion);
            GUI.color = Color.white;

            Rect totals = new Rect(inRect.x, inRect.y + 58f, inRect.width, 76f);
            DrawTotalCards(totals);

            Rect range = new Rect(inRect.x, totals.yMax + 8f, inRect.width, 54f);
            Widgets.DrawBoxSolid(range, WorkflowUiStyle.Panel);
            WorkflowUiStyle.DrawBorder(range, new Color(0.28f, 0.31f, 0.33f));
            string rangeText = AutoTranslatorMod.WfText(
                $"AI 翻译数据范围：需要翻译且没有有效译文 {_report.Range.NeedsTranslationWithoutValidTranslation:N0} 条；" +
                $"待判定且没有有效译文 {_report.Range.UndeterminedWithoutValidTranslation:N0} 条，其中按 {_report.UndeterminedTranslationRatio:P0} 纳入估算；" +
                $"已有有效译文 {_report.Range.CoveredByValidTranslation:N0} 条。",
                $"AI translation range: {_report.Range.NeedsTranslationWithoutValidTranslation:N0} needed without valid translation; " +
                $"{_report.Range.UndeterminedWithoutValidTranslation:N0} undetermined without valid translation, {_report.UndeterminedTranslationRatio:P0} sampled; " +
                $"{_report.Range.CoveredByValidTranslation:N0} covered by valid translations.");
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(range.x + 8f, range.y + 6f, range.width - 16f, range.height - 12f), rangeText);

            Rect tableRect = new Rect(inRect.x, range.yMax + 8f, inRect.width,
                inRect.yMax - range.yMax - 8f);
            DrawPerModTable(tableRect);
        }

        private void DrawTotalCards(Rect rect)
        {
            const float gap = 8f;
            float width = (rect.width - gap * 2f) / 3f;
            DrawTotalCard(new Rect(rect.x, rect.y, width, rect.height),
                AutoTranslatorMod.WfText("AI 复核预估", "AI review estimate"),
                _report.AiReview.TotalTokens.ToString("N0") + " Token",
                AutoTranslatorMod.WfText(
                    $"输入 {_report.AiReview.InputTokens:N0}　输出 {_report.AiReview.OutputTokens:N0}",
                    $"Input {_report.AiReview.InputTokens:N0}  Output {_report.AiReview.OutputTokens:N0}"));
            DrawTotalCard(new Rect(rect.x + width + gap, rect.y, width, rect.height),
                AutoTranslatorMod.WfText("AI 翻译预估", "AI translation estimate"),
                _report.AiTranslation.TotalTokens.ToString("N0") + " Token",
                AutoTranslatorMod.WfText(
                    $"输入 {_report.AiTranslation.InputTokens:N0}　输出 {_report.AiTranslation.OutputTokens:N0}",
                    $"Input {_report.AiTranslation.InputTokens:N0}  Output {_report.AiTranslation.OutputTokens:N0}"));
            DrawTotalCard(new Rect(rect.x + (width + gap) * 2f, rect.y, width, rect.height),
                AutoTranslatorMod.WfText("预计总预算", "Estimated budget"),
                _report.ProtectedBudgetTokens.ToString("N0") + " Token",
                AutoTranslatorMod.WfText(
                    $"输入 {_report.ProtectedInputBudgetTokens:N0}　输出 {_report.ProtectedOutputBudgetTokens:N0}　×{DryRunReport.BudgetProtectionRatio:0.0}",
                    $"Input {_report.ProtectedInputBudgetTokens:N0}  Output {_report.ProtectedOutputBudgetTokens:N0}  ×{DryRunReport.BudgetProtectionRatio:0.0}"));
        }

        private static void DrawTotalCard(Rect rect, string title, string value, string detail)
        {
            Widgets.DrawBoxSolid(rect, WorkflowUiStyle.RaisedPanel);
            WorkflowUiStyle.DrawBorder(rect, new Color(0.31f, 0.35f, 0.38f));
            Text.Font = GameFont.Tiny;
            GUI.color = Color.grey;
            Widgets.Label(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, 18f), title);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rect.x + 8f, rect.y + 25f, rect.width - 16f, 23f), value);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.grey;
            Widgets.Label(new Rect(rect.x + 8f, rect.y + 50f, rect.width - 16f, 18f), detail);
            GUI.color = Color.white;
        }

        private void DrawPerModTable(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, WorkflowUiStyle.Panel);
            WorkflowUiStyle.DrawBorder(rect, new Color(0.28f, 0.31f, 0.33f));
            const float headerHeight = 25f;
            const float rowHeight = 44f;
            float[] widths =
            {
                rect.width * 0.31f, rect.width * 0.19f, rect.width * 0.19f,
                rect.width * 0.15f, rect.width * 0.16f - 18f
            };
            string[] headers =
            {
                "Mod", AutoTranslatorMod.WfText("AI 复核", "AI review"),
                AutoTranslatorMod.WfText("AI 翻译", "AI translation"),
                AutoTranslatorMod.WfText("基础合计（输入/输出）", "Base total (input/output)"),
                AutoTranslatorMod.WfText("预算（输入/输出）", "Budget (input/output)")
            };
            Rect header = new Rect(rect.x, rect.y, rect.width, headerHeight);
            Widgets.DrawBoxSolid(header, WorkflowUiStyle.Header);
            Text.Font = GameFont.Tiny;
            float x = header.x + 5f;
            for (int i = 0; i < headers.Length; i++)
            {
                Widgets.Label(new Rect(x, header.y + 4f, widths[i], 18f), headers[i]);
                x += widths[i];
            }

            List<KeyValuePair<string, DryRunReport>> rows = _report.ModReports
                .OrderBy(pair => ResolveModName(pair.Key), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            Rect outRect = new Rect(rect.x + 2f, header.yMax, rect.width - 4f, rect.height - headerHeight - 2f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(outRect.height, rows.Count * rowHeight));
            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                KeyValuePair<string, DryRunReport> pair = rows[rowIndex];
                DryRunReport perMod = pair.Value ?? new DryRunReport();
                AiStepEstimate review = perMod.AiReview ?? new AiStepEstimate();
                AiStepEstimate translation = perMod.AiTranslation ?? new AiStepEstimate();
                Rect row = new Rect(0f, rowIndex * rowHeight, viewRect.width, rowHeight);
                if ((rowIndex & 1) == 1) Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.025f));
                Widgets.DrawHighlightIfMouseover(row);
                Widgets.DrawLineHorizontal(row.x, row.yMax - 1f, row.width);
                string[] values =
                {
                    ResolveModName(pair.Key), FormatTokenPair(review.InputTokens, review.OutputTokens, review.TotalTokens),
                    FormatTokenPair(translation.InputTokens, translation.OutputTokens, translation.TotalTokens),
                    FormatTokenPair(perMod.TotalInputTokens, perMod.TotalOutputTokens, perMod.TotalTokens),
                    FormatTokenPair(perMod.ProtectedInputBudgetTokens, perMod.ProtectedOutputBudgetTokens,
                        perMod.ProtectedBudgetTokens)
                };
                float cellX = row.x + 5f;
                for (int i = 0; i < values.Length; i++)
                {
                    Widgets.Label(new Rect(cellX, row.y + 4f, widths[i], row.height - 6f), values[i]);
                    cellX += widths[i];
                }
                TooltipHandler.TipRegion(row, AutoTranslatorMod.WfText(
                    "点击查看这个 Mod 的试跑详情", "Open this mod's dry-run details"));
                if (Widgets.ButtonInvisible(row))
                    Find.WindowStack.Add(new Window_WorkflowModDryRunReport(
                        ResolveModName(pair.Key), perMod));
            }
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
        }

        private string ResolveModName(string modIdentity)
        {
            return _modNames.TryGetValue(modIdentity ?? string.Empty, out string name)
                ? name
                : modIdentity ?? string.Empty;
        }

        private static string FormatTokenPair(long input, long output, long total)
        {
            return AutoTranslatorMod.WfText(
                $"合计 {total:N0}\n入 {input:N0} · 出 {output:N0}",
                $"Total {total:N0}\nIn {input:N0} · Out {output:N0}");
        }
    }
}
