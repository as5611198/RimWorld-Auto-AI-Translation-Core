using AutoTranslator_Core.Workflow.AI;
using AutoTranslator_Core.Workflow.Persistence;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;

namespace AutoTranslator_Core.Workflow
{
    internal static class WorkflowRuntimeSettings
    {
        public static TargetLanguage GetTargetLanguage()
        {
            if (AutoTranslatorMod.Settings == null)
                throw new InvalidOperationException("Translation settings are unavailable.");
            return AutoTranslatorMod.Settings.TargetLang;
        }

        public static string GetTargetLanguageFolder()
        {
            return AutoTranslatorScanner.GetFolderNameByLanguage(GetTargetLanguage());
        }

    }

    public sealed class WorkflowConfiguration
    {
        public bool EnableDllAnalysis { get; set; }
        public AiReviewScope AiReviewScope { get; set; } = new AiReviewScope();
        public double DryRunUndeterminedTranslationRatio { get; set; } = 0.5d;
    }

    public sealed class WorkflowExecutionOptions
    {
        public AiReviewScope TemporaryAiReviewScope { get; set; }
        public double? TemporaryUndeterminedTranslationRatio { get; set; }
        public ICollection<string> CandidateIds { get; set; }
    }

    internal sealed class WorkflowConfigurationStore
    {
        private const string ConfigurationKey = "workflow.execution.v1";
        private readonly WorkflowRepository _repository;

        public WorkflowConfigurationStore(WorkflowRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public WorkflowConfiguration Load()
        {
            string json = _repository.GetWorkflowSettingJson(ConfigurationKey);
            WorkflowConfiguration configuration;
            try
            {
                configuration = string.IsNullOrWhiteSpace(json)
                    ? new WorkflowConfiguration()
                    : JsonConvert.DeserializeObject<WorkflowConfiguration>(json) ?? new WorkflowConfiguration();
            }
            catch (JsonException ex)
            {
                AutoTranslatorSettings.AddDebugLog(
                    "workflow.configuration invalid-json; defaults-used error=" + ex.Message);
                configuration = new WorkflowConfiguration();
            }
            Normalize(configuration);
            return configuration;
        }

        public void Save(WorkflowConfiguration configuration)
        {
            configuration = configuration ?? new WorkflowConfiguration();
            Normalize(configuration);
            _repository.SaveWorkflowSettingJson(
                ConfigurationKey, JsonConvert.SerializeObject(configuration));
        }

        public AiReviewScope ResolveAiReviewScope(WorkflowExecutionOptions options)
        {
            return CloneScope(options?.TemporaryAiReviewScope ?? Load().AiReviewScope);
        }

        public double ResolveDryRunUndeterminedRatio(WorkflowExecutionOptions options)
        {
            double value = options?.TemporaryUndeterminedTranslationRatio ??
                           Load().DryRunUndeterminedTranslationRatio;
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.5d;
            return Math.Max(0d, Math.Min(1d, value));
        }

        private static void Normalize(WorkflowConfiguration configuration)
        {
            if (configuration.AiReviewScope == null) configuration.AiReviewScope = new AiReviewScope();
            if (double.IsNaN(configuration.DryRunUndeterminedTranslationRatio) ||
                double.IsInfinity(configuration.DryRunUndeterminedTranslationRatio))
                configuration.DryRunUndeterminedTranslationRatio = 0.5d;
            configuration.DryRunUndeterminedTranslationRatio = Math.Max(
                0d, Math.Min(1d, configuration.DryRunUndeterminedTranslationRatio));
        }

        private static AiReviewScope CloneScope(AiReviewScope source)
        {
            source = source ?? new AiReviewScope();
            return new AiReviewScope
            {
                IncludeNeedsTranslation = source.IncludeNeedsTranslation,
                IncludeUndetermined = source.IncludeUndetermined,
                IncludeNoTranslationNeeded = source.IncludeNoTranslationNeeded
            };
        }
    }
}
