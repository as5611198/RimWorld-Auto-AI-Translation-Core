using AutoTranslator_Core.Workflow.Analysis;
using AutoTranslator_Core.Workflow.DryRun;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Verse;

namespace AutoTranslator_Core.Workflow
{
    public sealed class WorkflowModMetadataSnapshot
    {
        public string ModIdentity { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public int InstallationCount { get; set; } = 1;
        public bool IsProtectedSystemPackage { get; set; }
        public int ProtectedSystemPackageOrder { get; set; } = -1;
    }

    public sealed class WorkflowWorkbenchSnapshot
    {
        public DateTime CreatedUtc { get; set; }
        public List<WorkflowModSummary> Mods { get; set; } = new List<WorkflowModSummary>();
        public DryRunReport LatestDryRun { get; set; }
    }

    public sealed class WorkflowModSummary
    {
        public string ModIdentity { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public int InstallationCount { get; set; } = 1;
        public bool IsProtectedSystemPackage { get; set; }
        public int ProtectedSystemPackageOrder { get; set; } = -1;
        public bool HasXmlAnalysis { get; set; }
        public bool HasDllAnalysis { get; set; }
        public int NeedsTranslation { get; set; }
        public int Undetermined { get; set; }
        public int NoTranslationNeeded { get; set; }
        public int Untranslated { get; set; }
        public int Translating { get; set; }
        public int Translated { get; set; }
        public int Failed { get; set; }
        public int CandidateCount { get; set; }
        public WorkflowAnalysisSourceSummary XmlAnalysis { get; set; } =
            new WorkflowAnalysisSourceSummary { SourceDomain = CandidateSourceDomain.Xml };
        public WorkflowAnalysisSourceSummary DllAnalysis { get; set; } =
            new WorkflowAnalysisSourceSummary { SourceDomain = CandidateSourceDomain.Dll };
        public WorkflowSourceStateSummary XmlState { get; set; } =
            new WorkflowSourceStateSummary { SourceDomain = CandidateSourceDomain.Xml };
        public WorkflowSourceStateSummary DllState { get; set; } =
            new WorkflowSourceStateSummary { SourceDomain = CandidateSourceDomain.Dll };
    }

    public sealed class WorkflowAnalysisSourceSummary
    {
        public CandidateSourceDomain SourceDomain { get; set; }
        public bool HasRun { get; set; }
        public WorkflowRunState State { get; set; }
        public AnalysisResultFreshness Freshness { get; set; } = AnalysisResultFreshness.NeverAnalyzed;
        public string ErrorText { get; set; } = string.Empty;
        public string AnalyzerVersion { get; set; } = string.Empty;
        public string CurrentAnalyzerVersion { get; set; } = string.Empty;
        public string ModVersionFingerprint { get; set; } = string.Empty;
        public string CurrentModVersionFingerprint { get; set; } = string.Empty;
        public int NeedsTranslation { get; set; }
        public int Undetermined { get; set; }
        public int NoTranslationNeeded { get; set; }
    }

    public sealed class WorkflowSourceStateSummary
    {
        public CandidateSourceDomain SourceDomain { get; set; }
        public int NeedsTranslation { get; set; }
        public int Undetermined { get; set; }
        public int NoTranslationNeeded { get; set; }
        public int Untranslated { get; set; }
        public int Translating { get; set; }
        public int Translated { get; set; }
        public int Failed { get; set; }
    }

    public sealed class WorkflowEditorSnapshot
    {
        public string ModIdentity { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<WorkflowCandidateEditorItem> Candidates { get; set; } =
            new List<WorkflowCandidateEditorItem>();
        public int TotalCount { get; set; }
        public int PageIndex { get; set; }
        public int PageSize { get; set; } = 100;
        public bool HasMore { get; set; }
        public string QueryKey { get; set; } = string.Empty;
    }

    public sealed class WorkflowCandidateEditorItem
    {
        public string CandidateId { get; set; } = string.Empty;
        public CandidateSourceDomain SourceDomain { get; set; }
        public string EntryKind { get; set; } = string.Empty;
        public string LogicalLocator { get; set; } = string.Empty;
        public string SourceFileRelativePath { get; set; } = string.Empty;
        public int SourceLineNumber { get; set; }
        public string SourceText { get; set; } = string.Empty;
        public string TranslationText { get; set; } = string.Empty;
        public CandidateClassification XmlClassification { get; set; }
        public CandidateClassification DllClassification { get; set; }
        public CandidateClassification AiClassification { get; set; }
        public bool AiClassificationIsCurrent { get; set; }
        public CandidateClassification ManualClassification { get; set; }
        public CandidateClassification EffectiveClassification { get; set; }
        public ClassificationLayer? EffectiveLayer { get; set; }
        public CandidateTranslationState TranslationState { get; set; }
        public TranslationOrigin TranslationOrigin { get; set; }
        public bool TranslationIsCurrent { get; set; }
        public string LastSyncStatus { get; set; } = string.Empty;
        public string LastSyncError { get; set; } = string.Empty;
        public string TranslationError { get; set; } = string.Empty;
    }

    public sealed partial class WorkflowBackend
    {
        public WorkflowWorkbenchSnapshot GetWorkbenchSnapshot(
            IList<WorkflowModMetadataSnapshot> mods)
        {
            EnsureDatabaseInitialized();
            string targetLanguage = WorkflowRuntimeSettings.GetTargetLanguageFolder();
            WorkflowWorkbenchSnapshot snapshot = new WorkflowWorkbenchSnapshot
            {
                CreatedUtc = DateTime.UtcNow
            };

            List<WorkflowModMetadataSnapshot> installed = (mods ??
                    new List<WorkflowModMetadataSnapshot>())
                .Where(mod => mod != null && !string.IsNullOrWhiteSpace(mod.ModIdentity))
                .GroupBy(mod => mod.ModIdentity, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(mod => mod.IsActive)
                    .ThenBy(mod => mod.RootPath, WorkflowPath.Comparer).First())
                .ToList();
            List<string> identities = installed
                .Where(item => !item.IsProtectedSystemPackage)
                .Select(item => item.ModIdentity)
                .Distinct(StringComparer.Ordinal).ToList();
            Stopwatch databaseTimer = Stopwatch.StartNew();
            Dictionary<string, Dictionary<CandidateSourceDomain, WorkbenchModAggregate>> aggregatesByMod =
                _repository.GetWorkbenchModAggregates(identities, targetLanguage)
                    .GroupBy(item => item.ModIdentity, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.ToDictionary(item => item.SourceDomain),
                        StringComparer.Ordinal);
            Dictionary<string, HashSet<CandidateSourceDomain>> analysisByMod =
                _repository.GetCompletedAnalysisDomains(identities);
            Dictionary<string, Dictionary<CandidateSourceDomain, AnalysisRunStatusSummary>>
                analysisStatusesByMod = _repository.GetLatestAnalysisRunStatuses(identities);
            databaseTimer.Stop();
            AutoTranslatorSettings.AddDebugLog(
                "workflow.workbench.phase database-summary elapsed_ms=" +
                databaseTimer.ElapsedMilliseconds + " mods=" + identities.Count);

            foreach (WorkflowModMetadataSnapshot mod in installed)
            {
                string modIdentity = mod.ModIdentity;
                aggregatesByMod.TryGetValue(
                    modIdentity,
                    out Dictionary<CandidateSourceDomain, WorkbenchModAggregate> aggregates);
                analysisByMod.TryGetValue(
                    modIdentity, out HashSet<CandidateSourceDomain> completedDomains);
                analysisStatusesByMod.TryGetValue(modIdentity,
                    out Dictionary<CandidateSourceDomain, AnalysisRunStatusSummary> analysisStatuses);
                WorkflowModSummary summary = new WorkflowModSummary
                {
                    ModIdentity = modIdentity,
                    PackageId = mod.PackageId ?? string.Empty,
                    DisplayName = mod.DisplayName ?? mod.PackageId ?? string.Empty,
                    IsActive = mod.IsActive,
                    InstallationCount = Math.Max(1, mod.InstallationCount),
                    IsProtectedSystemPackage = mod.IsProtectedSystemPackage,
                    ProtectedSystemPackageOrder = mod.ProtectedSystemPackageOrder,
                    HasXmlAnalysis = completedDomains != null &&
                                     completedDomains.Contains(CandidateSourceDomain.Xml),
                    HasDllAnalysis = completedDomains != null &&
                                     completedDomains.Contains(CandidateSourceDomain.Dll)
                };
                ApplyAnalysisStatus(summary.XmlAnalysis, analysisStatuses, CandidateSourceDomain.Xml);
                ApplyAnalysisStatus(summary.DllAnalysis, analysisStatuses, CandidateSourceDomain.Dll);
                ApplyAggregate(summary, aggregates, CandidateSourceDomain.Xml);
                ApplyAggregate(summary, aggregates, CandidateSourceDomain.Dll);
                snapshot.Mods.Add(summary);
            }

            string latestDryRunJson = _repository.GetLatestAggregateDryRunReportJson();
            if (!string.IsNullOrWhiteSpace(latestDryRunJson))
            {
                try { snapshot.LatestDryRun = JsonConvert.DeserializeObject<DryRunReport>(latestDryRunJson); }
                catch (JsonException ex)
                {
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.read-model invalid dry-run report error=" + ex.Message);
                }
            }
            snapshot.Mods = snapshot.Mods
                .OrderBy(mod => mod.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return snapshot;
        }

        private static void ApplyAggregate(
            WorkflowModSummary summary,
            IDictionary<CandidateSourceDomain, WorkbenchModAggregate> aggregates,
            CandidateSourceDomain domain)
        {
            if (summary == null || aggregates == null ||
                !aggregates.TryGetValue(domain, out WorkbenchModAggregate aggregate)) return;
            WorkflowAnalysisSourceSummary analysis = domain == CandidateSourceDomain.Xml
                ? summary.XmlAnalysis : summary.DllAnalysis;
            WorkflowSourceStateSummary state = domain == CandidateSourceDomain.Xml
                ? summary.XmlState : summary.DllState;
            analysis.NeedsTranslation = aggregate.LocalNeedsTranslation;
            analysis.Undetermined = aggregate.LocalUndetermined;
            analysis.NoTranslationNeeded = aggregate.LocalNoTranslationNeeded;
            state.NeedsTranslation = aggregate.EffectiveNeedsTranslation;
            state.Undetermined = aggregate.EffectiveUndetermined;
            state.NoTranslationNeeded = aggregate.EffectiveNoTranslationNeeded;
            state.Untranslated = aggregate.Untranslated;
            state.Translating = aggregate.Translating;
            state.Translated = aggregate.Translated;
            state.Failed = aggregate.Failed;
            summary.CandidateCount += aggregate.CandidateCount;
            summary.NeedsTranslation += aggregate.EffectiveNeedsTranslation;
            summary.Undetermined += aggregate.EffectiveUndetermined;
            summary.NoTranslationNeeded += aggregate.EffectiveNoTranslationNeeded;
            summary.Untranslated += aggregate.Untranslated;
            summary.Translating += aggregate.Translating;
            summary.Translated += aggregate.Translated;
            summary.Failed += aggregate.Failed;
        }

        private static void ApplyAnalysisStatus(
            WorkflowAnalysisSourceSummary target,
            IDictionary<CandidateSourceDomain, AnalysisRunStatusSummary> statuses,
            CandidateSourceDomain domain)
        {
            if (target == null || statuses == null || !statuses.TryGetValue(domain, out AnalysisRunStatusSummary status))
                return;
            target.HasRun = true;
            target.State = status.State;
            target.Freshness = status.Freshness;
            target.ErrorText = status.ErrorText ?? string.Empty;
            target.AnalyzerVersion = status.AnalyzerVersion ?? string.Empty;
            target.CurrentAnalyzerVersion = status.CurrentAnalyzerVersion ?? string.Empty;
            target.ModVersionFingerprint = status.ModVersionFingerprint ?? string.Empty;
            target.CurrentModVersionFingerprint = status.CurrentModVersionFingerprint ?? string.Empty;
        }

        public WorkflowEditorSnapshot GetEditorSnapshot(ModMetaData mod)
        {
            if (mod == null) throw new ArgumentNullException(nameof(mod));
            string modIdentity = ModAnalysisTargetFactory.CreateModIdentity(mod);
            return GetEditorSnapshot(
                modIdentity,
                mod.PackageId ?? string.Empty,
                mod.Name ?? mod.PackageId ?? string.Empty);
        }

        public WorkflowEditorSnapshot GetEditorSnapshot(
            string modIdentity,
            string packageId,
            string displayName,
            string search = "",
            CandidateClassification? classification = null,
            int translationFilter = 0,
            int pageIndex = 0,
            int pageSize = 100,
            bool requireManualClassification = false)
        {
            if (string.IsNullOrWhiteSpace(modIdentity))
                throw new ArgumentException("Mod identity is required.", nameof(modIdentity));
            EnsureDatabaseInitialized();
            string targetLanguage = WorkflowRuntimeSettings.GetTargetLanguageFolder();
            WorkflowEditorSnapshot snapshot = new WorkflowEditorSnapshot
            {
                ModIdentity = modIdentity,
                PackageId = packageId ?? string.Empty,
                DisplayName = displayName ?? packageId ?? string.Empty
            };
            snapshot.QueryKey = string.Join("\n", modIdentity, search ?? string.Empty,
                classification?.ToString() ?? string.Empty, translationFilter.ToString(), pageIndex.ToString(),
                requireManualClassification ? "manual" : string.Empty);
            CandidatePage page = _repository.GetEditorCandidatePage(
                modIdentity, targetLanguage, search, classification, translationFilter,
                checked(Math.Max(0, pageIndex) * Math.Max(1, Math.Min(500, pageSize))),
                Math.Max(1, Math.Min(500, pageSize)) * 2,
                requireManualClassification);
            snapshot.TotalCount = page.TotalCount;
            snapshot.PageIndex = Math.Max(0, pageIndex);
            snapshot.PageSize = Math.Max(1, Math.Min(500, pageSize));
            snapshot.HasMore = (snapshot.PageIndex + 1) * snapshot.PageSize < snapshot.TotalCount;
            snapshot.Candidates = page.Candidates
                .Select(candidate => new WorkflowCandidateEditorItem
                {
                    CandidateId = candidate.CandidateId,
                    SourceDomain = candidate.SourceDomain,
                    EntryKind = candidate.EntryKind,
                    LogicalLocator = candidate.LogicalLocator,
                    SourceFileRelativePath = candidate.SourceFileRelativePath,
                    SourceLineNumber = candidate.SourceLineNumber,
                    SourceText = candidate.SourceText,
                    TranslationText = candidate.TranslationText,
                    XmlClassification = ClassificationFlagsCodec.Get(
                        candidate.ClassificationFlags, ClassificationLayer.Xml),
                    DllClassification = ClassificationFlagsCodec.Get(
                        candidate.ClassificationFlags, ClassificationLayer.Dll),
                    AiClassification = ClassificationFlagsCodec.Get(
                        candidate.ClassificationFlags, ClassificationLayer.AiReview),
                    AiClassificationIsCurrent = WorkflowIdentity.IsAiReviewCurrent(candidate),
                    ManualClassification = ClassificationFlagsCodec.Get(
                        candidate.ClassificationFlags, ClassificationLayer.Manual),
                    EffectiveClassification = candidate.EffectiveClassification,
                    EffectiveLayer = ClassificationFlagsCodec.GetEffectiveLayer(candidate),
                    TranslationState = candidate.TranslationState,
                    TranslationOrigin = candidate.TranslationOrigin,
                    TranslationIsCurrent = WorkflowIdentity.IsTranslationCurrent(candidate),
                    LastSyncStatus = candidate.LastSyncStatus,
                    LastSyncError = candidate.LastSyncError,
                    TranslationError = candidate.TranslationError
                })
                .ToList();
            return snapshot;
        }
    }
}
