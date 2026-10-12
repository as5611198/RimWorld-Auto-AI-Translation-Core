using AutoTranslator_Core.Workflow.AI;
using AutoTranslator_Core.Workflow.Persistence;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTranslator_Core.Workflow.DryRun
{
    public sealed class DryRunReport
    {
        public const double BudgetProtectionRatio = ApproximateTokenEstimator.BudgetProtectionRatio;
        public DateTime CreatedUtc { get; set; }
        public string EstimatorVersion { get; set; } = ApproximateTokenEstimator.Version;
        public AiStepEstimate AiReview { get; set; } = new AiStepEstimate();
        public AiStepEstimate AiTranslation { get; set; } = new AiStepEstimate();
        public long TotalInputTokens => AiReview.InputTokens + AiTranslation.InputTokens;
        public long TotalOutputTokens => AiReview.OutputTokens + AiTranslation.OutputTokens;
        public long TotalTokens => TotalInputTokens + TotalOutputTokens;
        public long ProtectedInputBudgetTokens => ApproximateTokenEstimator.WithBudgetProtection(TotalInputTokens);
        public long ProtectedOutputBudgetTokens => ApproximateTokenEstimator.WithBudgetProtection(TotalOutputTokens);
        public long ProtectedBudgetTokens => ProtectedInputBudgetTokens + ProtectedOutputBudgetTokens;
        public string ScopeJson { get; set; } = string.Empty;
        public Dictionary<string, DryRunReport> ModReports { get; set; } =
            new Dictionary<string, DryRunReport>(StringComparer.Ordinal);
        public DryRunRangeSummary Range { get; set; } = new DryRunRangeSummary();
        public bool MissingLocalAnalysis { get; set; }
        public string SkippedReason { get; set; } = string.Empty;
        public double UndeterminedTranslationRatio { get; set; } = 0.5d;
    }

    public sealed class DryRunRangeSummary
    {
        public long NeedsTranslationWithoutValidTranslation { get; set; }
        public long UndeterminedWithoutValidTranslation { get; set; }
        public long CoveredByValidTranslation { get; set; }
        public Dictionary<string, Dictionary<string, long>> AiReviewRangeByClassificationAndSource { get; set; } =
            new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
    }

    internal sealed class DryRunWorkflowService
    {
        private readonly AiReviewService _review;
        private readonly AiTranslationService _translation;
        private readonly WorkflowRepository _repository;
        private readonly WorkflowConfigurationStore _configuration;

        public DryRunWorkflowService(
            AiReviewService review,
            AiTranslationService translation,
            WorkflowRepository repository,
            WorkflowConfigurationStore configuration)
        {
            _review = review;
            _translation = translation;
            _repository = repository;
            _configuration = configuration;
        }

        public async Task<DryRunReport> ExecuteAsync(
            IList<string> modIdentities,
            WorkflowExecutionOptions options = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            List<string> selectedMods = (modIdentities ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).ToList();
            AiReviewScope reviewScope = _configuration.ResolveAiReviewScope(options);
            double undeterminedRatio = _configuration.ResolveDryRunUndeterminedRatio(options);
            Guid runId = Guid.NewGuid();
            DryRunReport aggregate = new DryRunReport
            {
                CreatedUtc = DateTime.UtcNow,
                ScopeJson = JsonConvert.SerializeObject(reviewScope),
                UndeterminedTranslationRatio = undeterminedRatio
            };

            // The orchestrator owns no candidate query or estimate algorithm. Simulation uses
            // the exact paged selection, batching, prompt and gateway paths of the real steps.
            AiWorkflowStageReporter reporter = new AiWorkflowStageReporter("dry_run", "试跑", true);
            Stopwatch reviewTimer = reporter.Start(
                "ai_review_estimate", "切换到 AI复核估算",
                debugFields: "selected_mods=" + selectedMods.Count);
            aggregate.AiReview = await _review.ExecuteAsync(
                selectedMods, options, true, cancellationToken);
            reporter.Complete(
                "ai_review_estimate", "AI复核估算", reviewTimer,
                runtimeFields: "候选 " + aggregate.AiReview.CandidateCount + " 条",
                debugFields: "candidates=" + aggregate.AiReview.CandidateCount +
                             " tokens=" + aggregate.AiReview.TotalTokens);
            Stopwatch translationTimer = reporter.Start(
                "ai_translation_estimate", "切换到 AI翻译估算",
                debugFields: "selected_mods=" + selectedMods.Count);
            aggregate.AiTranslation = await _translation.ExecuteAsync(
                selectedMods, true, options, cancellationToken);
            reporter.Complete(
                "ai_translation_estimate", "AI翻译估算", translationTimer,
                runtimeFields: "候选 " + aggregate.AiTranslation.CandidateCount + " 条",
                debugFields: "candidates=" + aggregate.AiTranslation.CandidateCount +
                             " tokens=" + aggregate.AiTranslation.TotalTokens);

            Stopwatch reportTimer = reporter.Start(
                "report_save", "切换到试跑报告保存",
                debugFields: "selected_mods=" + selectedMods.Count);
            foreach (string modIdentity in selectedMods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DryRunReport perMod = new DryRunReport
                {
                    CreatedUtc = aggregate.CreatedUtc,
                    ScopeJson = aggregate.ScopeJson,
                    UndeterminedTranslationRatio = undeterminedRatio,
                    AiReview = SliceEstimate(aggregate.AiReview, modIdentity),
                    AiTranslation = SliceEstimate(aggregate.AiTranslation, modIdentity)
                };
                string skippedReason = GetSkippedReason(
                    aggregate.AiReview, aggregate.AiTranslation, modIdentity);
                perMod.SkippedReason = skippedReason;
                perMod.MissingLocalAnalysis = skippedReason.IndexOf(
                    "未完成本地分析", StringComparison.Ordinal) >= 0;
                perMod.Range = BuildRange(perMod, modIdentity);
                aggregate.ModReports[modIdentity] = perMod;
                aggregate.MissingLocalAnalysis |= perMod.MissingLocalAnalysis;
                MergeRange(aggregate.Range, perMod.Range);
                _repository.SaveDryRunReport(runId, modIdentity, JsonConvert.SerializeObject(perMod));
            }
            _repository.SaveDryRunReport(runId, "__aggregate__", JsonConvert.SerializeObject(aggregate));
            reporter.Complete(
                "report_save", "试跑报告保存", reportTimer,
                runtimeFields: "逐 Mod 报告 " + aggregate.ModReports.Count + " 份",
                debugFields: "mod_reports=" + aggregate.ModReports.Count +
                             " protected_budget_tokens=" + aggregate.ProtectedBudgetTokens);
            return aggregate;
        }

        private static AiStepEstimate SliceEstimate(AiStepEstimate source, string modIdentity)
        {
            AiStepEstimate result = new AiStepEstimate();
            if (source == null) return result;
            Copy(source.CountsByMod, result.CountsByMod, modIdentity);
            Copy(source.InputTokensByMod, result.InputTokensByMod, modIdentity);
            Copy(source.OutputTokensByMod, result.OutputTokensByMod, modIdentity);
            Copy(source.CompletedByMod, result.CompletedByMod, modIdentity);
            Copy(source.FailedByMod, result.FailedByMod, modIdentity);
            Copy(source.ProducedDecisionsByMod, result.ProducedDecisionsByMod, modIdentity);
            Copy(source.EffectiveClassificationChangesByMod,
                result.EffectiveClassificationChangesByMod, modIdentity);
            Copy(source.ProtectedByManualByMod, result.ProtectedByManualByMod, modIdentity);
            result.CountsByMod.TryGetValue(modIdentity, out long candidates);
            result.InputTokensByMod.TryGetValue(modIdentity, out long inputTokens);
            result.OutputTokensByMod.TryGetValue(modIdentity, out long outputTokens);
            result.CompletedByMod.TryGetValue(modIdentity, out long completed);
            result.FailedByMod.TryGetValue(modIdentity, out long failed);
            result.ProducedDecisionsByMod.TryGetValue(modIdentity, out long decisions);
            result.EffectiveClassificationChangesByMod.TryGetValue(modIdentity, out long changed);
            result.ProtectedByManualByMod.TryGetValue(modIdentity, out long manual);
            result.CandidateCount = candidates;
            result.InputTokens = inputTokens;
            result.OutputTokens = outputTokens;
            result.CompletedCount = completed;
            result.FailedCount = failed;
            result.ProducedDecisionCount = decisions;
            result.EffectiveClassificationChangedCount = changed;
            result.ProtectedByManualCount = manual;
            if (source.CountsByModAndClassification.TryGetValue(
                    modIdentity, out Dictionary<string, long> classifications))
            {
                result.CountsByModAndClassification[modIdentity] =
                    new Dictionary<string, long>(classifications, StringComparer.Ordinal);
                foreach (KeyValuePair<string, long> pair in classifications)
                    result.CountsByClassification[pair.Key] = pair.Value;
            }
            if (source.CandidatePoolCountsByModAndClassification.TryGetValue(
                    modIdentity, out Dictionary<string, long> poolClassifications))
                result.CandidatePoolCountsByModAndClassification[modIdentity] =
                    new Dictionary<string, long>(poolClassifications, StringComparer.Ordinal);
            Copy(source.CoveredByCurrentTranslationByMod,
                result.CoveredByCurrentTranslationByMod, modIdentity);
            if (source.SkippedMods.TryGetValue(modIdentity, out string skipped))
                result.SkippedMods[modIdentity] = skipped;
            if (source.ErrorsByMod.TryGetValue(modIdentity, out List<string> errors))
                result.ErrorsByMod[modIdentity] = errors.ToList();
            return result;
        }

        private static void Copy(
            IDictionary<string, long> source,
            IDictionary<string, long> target,
            string modIdentity)
        {
            if (source != null && source.TryGetValue(modIdentity, out long value))
                target[modIdentity] = value;
        }

        private static string GetSkippedReason(
            AiStepEstimate review, AiStepEstimate translation, string modIdentity)
        {
            if (review != null && review.SkippedMods.TryGetValue(modIdentity, out string reason)) return reason;
            if (translation != null && translation.SkippedMods.TryGetValue(modIdentity, out reason)) return reason;
            return string.Empty;
        }

        private static DryRunRangeSummary BuildRange(DryRunReport report, string modIdentity)
        {
            DryRunRangeSummary range = new DryRunRangeSummary();
            report.AiTranslation.CandidatePoolCountsByModAndClassification.TryGetValue(
                modIdentity ?? string.Empty,
                out Dictionary<string, long> translationPool);
            translationPool = translationPool ?? new Dictionary<string, long>(StringComparer.Ordinal);
            translationPool.TryGetValue(
                CandidateClassification.NeedsTranslation.ToString(), out long needs);
            translationPool.TryGetValue(
                CandidateClassification.Undetermined.ToString(), out long undetermined);
            range.NeedsTranslationWithoutValidTranslation = needs;
            range.UndeterminedWithoutValidTranslation = undetermined;
            range.CoveredByValidTranslation = report.AiTranslation
                .CoveredByCurrentTranslationByMod.Values.Sum();
            foreach (KeyValuePair<string, long> classification in report.AiReview.CountsByClassification)
            {
                range.AiReviewRangeByClassificationAndSource[classification.Key] =
                    new Dictionary<string, long>(StringComparer.Ordinal)
                    {
                        ["Effective"] = classification.Value
                    };
            }
            return range;
        }

        private static void MergeRange(DryRunRangeSummary target, DryRunRangeSummary source)
        {
            target.NeedsTranslationWithoutValidTranslation += source.NeedsTranslationWithoutValidTranslation;
            target.UndeterminedWithoutValidTranslation += source.UndeterminedWithoutValidTranslation;
            target.CoveredByValidTranslation += source.CoveredByValidTranslation;
            foreach (KeyValuePair<string, Dictionary<string, long>> classification in
                     source.AiReviewRangeByClassificationAndSource)
            {
                if (!target.AiReviewRangeByClassificationAndSource.TryGetValue(
                        classification.Key, out Dictionary<string, long> targetSources))
                {
                    targetSources = new Dictionary<string, long>(StringComparer.Ordinal);
                    target.AiReviewRangeByClassificationAndSource[classification.Key] = targetSources;
                }
                foreach (KeyValuePair<string, long> sourceCount in classification.Value)
                {
                    if (!targetSources.ContainsKey(sourceCount.Key)) targetSources[sourceCount.Key] = 0;
                    targetSources[sourceCount.Key] += sourceCount.Value;
                }
            }
        }
    }
}
