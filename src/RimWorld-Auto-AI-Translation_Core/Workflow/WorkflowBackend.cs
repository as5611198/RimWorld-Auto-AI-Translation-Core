using AutoTranslator_Core.Workflow.AI;
using AutoTranslator_Core.Workflow.Analysis;
using AutoTranslator_Core.Workflow.DryRun;
using AutoTranslator_Core.Workflow.Output;
using AutoTranslator_Core.Workflow.Persistence;
using AutoTranslator_Core.Workflow.Synchronization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RimWorld;
using Verse;

namespace AutoTranslator_Core.Workflow
{
    public sealed partial class WorkflowBackend
    {
        private readonly WorkflowRepository _repository;
        private readonly string _generatedPackRoot;
        private readonly AnalysisWorkflowService _analysis;
        private readonly AiReviewService _review;
        private readonly AiTranslationService _translation;
        private readonly DryRunWorkflowService _dryRun;
        private readonly TranslationFileSynchronizer _synchronizer;
        private readonly ManualTranslationService _manualTranslation;
        private readonly ITranslationOutputStore _output;
        private readonly WorkflowConfigurationStore _configuration;
        private readonly object _initializationGate = new object();
        private bool _databaseInitialized;

        public WorkflowBackend(
            string coreModRoot,
            string generatedPackRoot,
            ILanguageModelTransport modelTransport)
        {
            WorkflowDatabaseConnectionFactory connections = new WorkflowDatabaseConnectionFactory(
                coreModRoot, generatedPackRoot);
            _repository = new WorkflowRepository(connections);
            _generatedPackRoot = System.IO.Path.GetFullPath(generatedPackRoot);
            _configuration = new WorkflowConfigurationStore(_repository);
            LanguageModelGateway gateway = new LanguageModelGateway(modelTransport);
            CompositeTranslationOutputStore output = new CompositeTranslationOutputStore(generatedPackRoot);
            _output = output;
            _analysis = new AnalysisWorkflowService(
                _repository, new XmlWorkflowAnalyzer(), new DllWorkflowAnalyzer(), _configuration);
            _review = new AiReviewService(_repository, gateway, _configuration);
            _translation = new AiTranslationService(_repository, gateway, output, _configuration);
            _dryRun = new DryRunWorkflowService(
                _review, _translation, _repository, _configuration);
            _synchronizer = new TranslationFileSynchronizer(
                _repository, new TranslationFileIndex(generatedPackRoot, _repository), output);
            _manualTranslation = new ManualTranslationService(_repository, output);
        }

        public static WorkflowBackend CreateDefault(string coreModRoot, string generatedPackRoot)
        {
            return new WorkflowBackend(
                coreModRoot, generatedPackRoot, new AutoTranslatorApiModelTransport());
        }

        public string DatabasePath => _repository.DatabasePath;

        public void InitializeDatabase()
        {
            EnsureDatabaseInitialized();
        }

        public WorkflowConfiguration GetConfiguration()
        {
            EnsureDatabaseInitialized();
            return _configuration.Load();
        }

        public void SaveConfiguration(WorkflowConfiguration configuration)
        {
            EnsureDatabaseInitialized();
            if (!WorkflowTaskCoordinator.Instance.TryBegin(
                    WorkflowTaskKind.ConfigurationEdit,
                    "Save workflow configuration",
                    out WorkflowTaskLease lease))
                throw new InvalidOperationException("Workflow settings cannot be changed while a background task is running.");
            using (lease)
            {
                AutoTranslatorSettings.ResetPipelineCancellation();
                string inputJson = JsonConvert.SerializeObject(configuration ?? new WorkflowConfiguration());
                _repository.StartWorkflowRun(
                    lease.RunId, WorkflowTaskKind.ConfigurationEdit, inputJson);
                try
                {
                    _configuration.Save(configuration);
                    _repository.CompleteWorkflowRun(
                        lease.RunId, WorkflowRunState.Completed, string.Empty, string.Empty);
                    WorkflowTaskCoordinator.Instance.MarkTerminal(lease.RunId, WorkflowRunState.Completed);
                }
                catch (Exception ex)
                {
                    _repository.CompleteWorkflowRun(
                        lease.RunId, WorkflowRunState.Failed, string.Empty, ex.Message);
                    WorkflowTaskCoordinator.Instance.MarkTerminal(lease.RunId, WorkflowRunState.Failed, ex.Message);
                    throw;
                }
                finally
                {
                    // Operations can update summaries even when they finish partially or
                    // fail after persisting a safe subset.  Publishing one revision here
                    // keeps workbench snapshots aligned after persisted settings change.
                    WorkflowTaskCoordinator.Instance.NotifyWorkbenchDataChanged();
                }
            }
        }

        public Task RunAnalysisButtonAsync(
            IList<ModMetaData> mods,
            bool forceAnalysis,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            List<ModMetaData> targets = WorkflowStepSelection.FilterTranslationTargets(mods);
            return RunExclusiveAsync(
                WorkflowTaskKind.AnalysisBatch,
                "Local analysis",
                token => RunLocalAnalysisAsync(
                    () => _analysis.RunAnalysisButtonAsync(targets, forceAnalysis, token), token),
                cancellationToken,
                CreateSelectionInput(targets, forceAnalysis, null));
        }

        public Task RunXmlAnalysisAsync(
            IList<ModMetaData> mods,
            bool forceAnalysis,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            List<ModMetaData> targets = WorkflowStepSelection.FilterTranslationTargets(mods);
            return RunExclusiveAsync(
                WorkflowTaskKind.XmlAnalysis,
                "XML analysis",
                token => RunLocalAnalysisAsync(
                    () => _analysis.RunXmlOnlyAsync(targets, forceAnalysis, token), token),
                cancellationToken,
                CreateSelectionInput(targets, forceAnalysis, null));
        }

        public Task RunDllAnalysisAsync(
            IList<ModMetaData> mods,
            bool forceAnalysis,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            List<ModMetaData> targets = WorkflowStepSelection.FilterTranslationTargets(mods);
            return RunExclusiveAsync(
                WorkflowTaskKind.DllAnalysis,
                "DLL analysis",
                token => RunLocalAnalysisAsync(
                    () => _analysis.RunDllOnlyAsync(targets, forceAnalysis, token), token),
                cancellationToken,
                CreateSelectionInput(targets, forceAnalysis, null));
        }

        public Task<DryRunReport> RunDryRunAsync(
            IList<string> modIdentities,
            WorkflowExecutionOptions options = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            List<string> targets = (modIdentities ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return RunExclusiveAsync(
                WorkflowTaskKind.DryRun,
                "Token dry run",
                token => Task.Run(
                    () => _dryRun.ExecuteAsync(targets, options, token), token),
                cancellationToken,
                CreateIdentitySelectionInput(targets, options));
        }

        public Task<AiStepEstimate> RunAiReviewAsync(
            IList<ModMetaData> mods,
            WorkflowExecutionOptions options = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            List<ModMetaData> targets = WorkflowStepSelection.FilterTranslationTargets(mods);
            List<string> modIdentities = WorkflowStepSelection.ResolveModIdentities(targets);
            return RunExclusiveAsync(
                WorkflowTaskKind.AiReview,
                "AI classification review",
                token => Task.Run(
                    () => _review.ExecuteAsync(modIdentities, options, false, token), token),
                cancellationToken,
                CreateSelectionInput(targets, null, options));
        }

        public Task<AiStepEstimate> RunAiTranslationAsync(
            IList<ModMetaData> mods,
            WorkflowExecutionOptions options = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            List<ModMetaData> targets = WorkflowStepSelection.FilterTranslationTargets(mods);
            List<string> modIdentities = WorkflowStepSelection.ResolveModIdentities(targets);
            return RunExclusiveAsync(
                WorkflowTaskKind.AiTranslation,
                "AI translation",
                token => ExecuteAiTranslationWithInitialSynchronizationAsync(
                    modIdentities, options, token),
                cancellationToken,
                CreateSelectionInput(targets, null, options));
        }

        public Task<AiStepEstimate> RunCandidateAiTranslationAsync(
            ModMetaData mod,
            ICollection<string> candidateIds,
            WorkflowExecutionOptions options = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (mod == null) throw new ArgumentNullException(nameof(mod));
            List<ModMetaData> targets = WorkflowStepSelection.FilterTranslationTargets(
                new[] { mod });
            if (targets.Count != 1)
                throw new InvalidOperationException("The selected Mod is not an eligible translation target.");
            string modIdentity = ModAnalysisTargetFactory.CreateModIdentity(targets[0]);
            List<string> requested = (candidateIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).ToList();
            if (requested.Count == 0)
                throw new InvalidOperationException("No translation entries were selected.");

            return RunExclusiveAsync(
                WorkflowTaskKind.AiTranslation,
                "Selected-entry AI translation",
                token => Task.Run(async () =>
                {
                    string targetLanguage = WorkflowRuntimeSettings.GetTargetLanguageFolder();
                    List<CandidateRecord> scopedCandidates = requested
                        .Select(candidateId => _repository.GetCandidate(candidateId, targetLanguage))
                        .Where(candidate => candidate != null && string.Equals(
                            candidate.ModIdentity, modIdentity, StringComparison.Ordinal))
                        .ToList();
                    token.ThrowIfCancellationRequested();
                    if (scopedCandidates.Count == 0)
                        throw new InvalidOperationException(
                            "None of the selected entries belong to the current Mod.");

                    List<string> scoped = scopedCandidates
                        .Select(candidate => candidate.CandidateId).ToList();
                    List<string> requiresManualClassification = scopedCandidates
                        .Where(candidate => candidate.EffectiveClassification !=
                                            CandidateClassification.NeedsTranslation)
                        .Select(candidate => candidate.CandidateId).ToList();
                    if (requiresManualClassification.Count > 0)
                    {
                        _repository.SetClassification(
                            requiresManualClassification,
                            ClassificationLayer.Manual,
                            CandidateClassification.NeedsTranslation);
                    }
                    WorkflowExecutionOptions targetedOptions = new WorkflowExecutionOptions
                    {
                        TemporaryAiReviewScope = options?.TemporaryAiReviewScope,
                        TemporaryUndeterminedTranslationRatio =
                            options?.TemporaryUndeterminedTranslationRatio,
                        CandidateIds = scoped
                    };
                    return await ExecuteAiTranslationWithInitialSynchronizationAsync(
                        new[] { modIdentity }, targetedOptions, token).ConfigureAwait(false);
                }, token),
                cancellationToken,
                JsonConvert.SerializeObject(new
                {
                    modIdentity,
                    operation = "selected-entry-ai-translation",
                    candidateSelection = JsonConvert.DeserializeObject(
                        CreateCandidateSelectionInput(requested, "ai-translation"))
                }));
        }

        public Task<TranslationSynchronizationResult> RunManualTranslationStateRefreshAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunExclusiveAsync(
                WorkflowTaskKind.ManualSynchronization,
                "Translation status synchronization",
                async token =>
                {
                    TranslationSynchronizationResult result = await Task.Run(
                        () => _synchronizer.Synchronize(token), token);
                    bool reloadSucceeded = await AutoTranslatorScanner.RequestMemoryDropAsync();
                    if (!reloadSucceeded) throw new InvalidOperationException("Translation memory reload failed.");
                    token.ThrowIfCancellationRequested();
                    return result;
                },
                cancellationToken,
                JsonConvert.SerializeObject(new { manualRefresh = true }));
        }

        public Task RunRuntimeTranslationReloadAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunExclusiveAsync(
                WorkflowTaskKind.RuntimeTranslationReload,
                "Translation hot reload",
                async token =>
                {
                    string targetLanguage = WorkflowRuntimeSettings.GetTargetLanguageFolder();
                    RuntimeTranslationMaterializationSummary restored = await Task.Run(
                        () => RebuildManagedTranslationOutputs(targetLanguage, token), token);
                    token.ThrowIfCancellationRequested();
                    bool refreshed = await RuntimeTranslationRefresher.ForceReloadAllAsync();
                    if (!refreshed)
                        throw new InvalidOperationException(
                            "One or more runtime translation reload requests failed.");
                    token.ThrowIfCancellationRequested();
                    string completed = "已从数据库重建并热重载全部已保存译文：共 " +
                        restored.Total + " 条（XML " + restored.Xml + "，DLL " + restored.Dll + "）";
                    WorkflowTaskCoordinator.Instance.ReportStage(
                        string.Empty, completed, "✓ " + completed,
                        "workflow.runtime-reload complete total=" + restored.Total +
                        " xml=" + restored.Xml + " dll=" + restored.Dll);
                },
                cancellationToken,
                JsonConvert.SerializeObject(new
                {
                    rebuildManagedOutputs = true,
                    forceXmlReload = true,
                    reloadDllManifest = true
                }));
        }

        private RuntimeTranslationMaterializationSummary RebuildManagedTranslationOutputs(
            string targetLanguage,
            CancellationToken cancellationToken)
        {
            const int pageSize = 500;
            int total = _repository.CountCurrentManagedTranslations(targetLanguage);
            int completed = 0;
            int xml = 0;
            int dll = 0;
            string afterCandidateId = string.Empty;
            WorkflowTaskCoordinator.Instance.ReportProgress(
                0, total, string.Empty,
                "从数据库重建已保存译文 0/" + total,
                subCompletedUnits: 0, subTotalUnits: total,
                writeRuntimeLog: false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<CandidateRecord> page = _repository.GetCurrentManagedTranslationsPage(
                    targetLanguage, afterCandidateId, pageSize);
                if (page.Count == 0) break;
                List<TranslationOutputWrite> writes = new List<TranslationOutputWrite>(page.Count);
                foreach (CandidateRecord candidate in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writes.Add(new TranslationOutputWrite
                    {
                        Candidate = candidate,
                        TranslatedText = candidate.TranslationText,
                        Origin = candidate.TranslationOrigin,
                        Target = new TranslationWriteTarget
                        {
                            RelativePath = candidate.TranslationFileRelativePath,
                            EntryKey = candidate.TranslationEntryKey
                        }
                    });
                    if (candidate.SourceDomain == CandidateSourceDomain.Dll) dll++;
                    else xml++;
                }
                _output.WriteBatch(writes, targetLanguage);
                completed += page.Count;
                afterCandidateId = page[page.Count - 1].CandidateId;
                WorkflowTaskCoordinator.Instance.ReportProgress(
                    completed, total, string.Empty,
                    "从数据库重建已保存译文 " + completed + "/" + total,
                    subCompletedUnits: completed, subTotalUnits: total,
                    writeRuntimeLog: false);
            }
            if (completed != total)
                throw new InvalidOperationException(
                    "Saved translation paging ended early: " + completed + "/" + total + ".");
            AutoTranslatorSettings.AddLog(
                "已从数据库重建已保存译文：共 " + completed +
                " 条（XML " + xml + "，DLL " + dll + "）");
            return new RuntimeTranslationMaterializationSummary
            {
                Total = completed,
                Xml = xml,
                Dll = dll
            };
        }

        private sealed class RuntimeTranslationMaterializationSummary
        {
            public int Total;
            public int Xml;
            public int Dll;
        }

        public Task<TranslationSynchronizationResult> RunCloudTranslationStateRefreshAsync(
            string targetLanguage,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(targetLanguage))
                throw new ArgumentException("Cloud target language is required.", nameof(targetLanguage));
            return RunExclusiveAsync(
                WorkflowTaskKind.CloudTranslation,
                "Cloud translation synchronization",
                async token =>
                {
                    TranslationSynchronizationResult result = await Task.Run(
                        () => _synchronizer.Synchronize(token, targetLanguage), token);
                    bool reloadSucceeded = await AutoTranslatorScanner.RequestMemoryDropAsync();
                    if (!reloadSucceeded) throw new InvalidOperationException("Translation memory reload failed.");
                    token.ThrowIfCancellationRequested();
                    return result;
                },
                cancellationToken,
                JsonConvert.SerializeObject(new { cloudDownload = true, targetLanguage }));
        }

        public ExpiredWorkflowDataSummary GetExpiredDataSummary()
        {
            EnsureDatabaseInitialized();
            return _repository.GetExpiredDataSummary();
        }

        public Task<ExpiredWorkflowDataSummary> RunExpiredDataCleanupAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunExclusiveAsync(
                WorkflowTaskKind.ExpiredDataCleanup,
                "Expired data cleanup",
                token => Task.Run(() => _repository.CleanupExpiredData(
                    token,
                    (completed, total, detail) => WorkflowTaskCoordinator.Instance.ReportProgress(
                        completed, total, string.Empty, detail, 0, 0, completed, total, false)), token),
                cancellationToken,
                JsonConvert.SerializeObject(new { expiredDataCleanup = true }));
        }

        public Task RunOneClickTranslationAsync(
            IList<ModMetaData> mods,
            bool forceAnalysis,
            WorkflowExecutionOptions options = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            List<ModMetaData> targets = WorkflowStepSelection.FilterTranslationTargets(mods);
            return RunExclusiveAsync(
                WorkflowTaskKind.OneClickTranslation,
                "One-click translation",
                token => RunOneClickTranslationCoreAsync(
                    targets, forceAnalysis, options, token),
                cancellationToken,
                CreateSelectionInput(targets, forceAnalysis, options));
        }

        public Task SaveManualTranslationAsync(
            string candidateId,
            string translatedText,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunExclusiveAsync(
                WorkflowTaskKind.ManualTranslationEdit,
                "Save manual translation",
                async token =>
                {
                    CandidateSourceDomain sourceDomain = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        return _manualTranslation.Save(candidateId, translatedText);
                    }, token);
                    if (!await RefreshEditedTranslationAsync(sourceDomain))
                        throw new InvalidOperationException("Translation was saved, but the runtime refresh failed.");
                },
                cancellationToken,
                JsonConvert.SerializeObject(new
                {
                    candidateId = candidateId ?? string.Empty,
                    operation = "save",
                    translatedTextLength = translatedText?.Length ?? 0
                }));
        }

        public Task DeleteTranslationAsync(
            string candidateId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunExclusiveAsync(
                WorkflowTaskKind.ManualTranslationEdit,
                "Delete translation",
                async token =>
                {
                    CandidateSourceDomain sourceDomain = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        return _manualTranslation.Delete(candidateId);
                    }, token);
                    if (!await RefreshEditedTranslationAsync(sourceDomain))
                        throw new InvalidOperationException("Translation was deleted, but the runtime refresh failed.");
                },
                cancellationToken,
                JsonConvert.SerializeObject(new
                {
                    candidateId = candidateId ?? string.Empty,
                    operation = "delete"
                }));
        }

        private static Task<bool> RefreshEditedTranslationAsync(CandidateSourceDomain sourceDomain)
        {
            return sourceDomain == CandidateSourceDomain.Dll
                ? RuntimeTranslationRefresher.EnableAndRequestDllReloadAsync()
                : AutoTranslatorScanner.RequestMemoryDropAsync();
        }

        public Task SetManualClassificationAsync(
            ICollection<string> candidateIds,
            CandidateClassification classification,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunExclusiveAsync(
                WorkflowTaskKind.ClassificationEdit,
                "Set manual classification",
                token => Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    _repository.SetClassification(
                        candidateIds, ClassificationLayer.Manual, classification);
                }, token),
                cancellationToken,
                CreateCandidateSelectionInput(candidateIds, classification.ToString()));
        }

        public Task ClearManualClassificationAsync(
            ICollection<string> candidateIds,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return SetManualClassificationAsync(
                candidateIds, CandidateClassification.NotAnalyzed, cancellationToken);
        }

        public Task ClearAiReviewAsync(
            ICollection<string> candidateIds,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunExclusiveAsync(
                WorkflowTaskKind.ClassificationEdit,
                "Clear AI review",
                token => Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    _repository.SetClassification(
                        candidateIds, ClassificationLayer.AiReview, CandidateClassification.NotAnalyzed);
                }, token),
                cancellationToken,
                CreateCandidateSelectionInput(candidateIds, "ClearAiReview"));
        }

        public Task<WorkflowResultCleanupSummary> ClearModResultsAsync(
            ICollection<string> modIdentities,
            WorkflowResultCleanupOptions options,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            List<string> requested = (modIdentities ?? Array.Empty<string>())
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            WorkflowResultCleanupOptions selected = options ?? new WorkflowResultCleanupOptions();
            if (requested.Count == 0)
                throw new InvalidOperationException("No mods were selected for result cleanup.");
            if (!selected.HasAnySelection)
                throw new InvalidOperationException("No result type was selected for cleanup.");

            return RunExclusiveAsync(
                WorkflowTaskKind.ResultCleanup,
                "Clear selected mod results",
                async token =>
                {
                    HashSet<string> protectedTargets =
                        _repository.GetProtectedTranslationTargetIdentities(requested);
                    List<string> targets = requested
                        .Where(identity => !protectedTargets.Contains(identity))
                        .ToList();
                    WorkflowResultCleanupSummary summary = new WorkflowResultCleanupSummary
                    {
                        RequestedModCount = requested.Count,
                        ProtectedModCount = protectedTargets.Count
                    };
                    if (targets.Count == 0) return summary;

                    WorkflowTaskCoordinator.Instance.ReportProgress(
                        0, targets.Count, string.Empty,
                        AutoTranslatorMod.WfText("正在清除分类结果", "Clearing classification results"));
                    await Task.Run(() => _repository.ClearClassificationResultsForMods(
                        targets,
                        selected.ClearAiReviewResults,
                        selected.ClearManualClassificationResults,
                        summary,
                        token), token);

                    if (selected.ClearLocalAiTranslationResults)
                    {
                        string targetLanguage = WorkflowRuntimeSettings.GetTargetLanguageFolder();
                        long total = _repository.CountLocalAiTranslations(targets, targetLanguage);
                        long completed = 0;
                        string cursor = string.Empty;
                        do
                        {
                            token.ThrowIfCancellationRequested();
                            LocalAiTranslationPage page = _repository.GetLocalAiTranslationPage(
                                targets, targetLanguage, cursor, 250);
                            foreach (CandidateRecord candidate in page.Candidates)
                            {
                                token.ThrowIfCancellationRequested();
                                ClearLocalAiTranslation(candidate, targetLanguage);
                                completed++;
                                summary.LocalAiTranslationCount++;
                                WorkflowTaskCoordinator.Instance.ReportProgress(
                                    (int)Math.Min(completed, int.MaxValue),
                                    (int)Math.Min(Math.Max(1L, total), int.MaxValue),
                                    candidate.ModIdentity,
                                    AutoTranslatorMod.WfText(
                                        "正在清除 AI 翻译结果 " + completed + "/" + total,
                                        "Clearing AI translation results " + completed + "/" + total));
                            }
                            cursor = page.NextCandidateId;
                            if (!page.HasMore) break;
                        }
                        while (!string.IsNullOrWhiteSpace(cursor));

                        if (summary.LocalAiTranslationCount > 0 &&
                            !await AutoTranslatorScanner.RequestMemoryDropAsync())
                            throw new InvalidOperationException(
                                "Results were cleared, but the runtime translation refresh failed.");
                    }
                    return summary;
                },
                cancellationToken,
                JsonConvert.SerializeObject(new
                {
                    modIdentities = requested,
                    clearAiReview = selected.ClearAiReviewResults,
                    clearManualClassification = selected.ClearManualClassificationResults,
                    clearLocalAiTranslation = selected.ClearLocalAiTranslationResults
                }));
        }

        private void ClearLocalAiTranslation(CandidateRecord candidate, string targetLanguage)
        {
            if (candidate == null || candidate.TranslationOrigin != TranslationOrigin.AiTranslation) return;
            if (string.IsNullOrWhiteSpace(candidate.TranslationFileRelativePath))
            {
                _repository.ClearTranslationIfOrigin(
                    candidate.CandidateId, targetLanguage, TranslationOrigin.AiTranslation);
                return;
            }

            TranslationWriteTarget target = new TranslationWriteTarget
            {
                RelativePath = candidate.TranslationFileRelativePath,
                EntryKey = candidate.TranslationEntryKey
            };
            Guid operationId = _repository.CreatePendingFileOperation(
                candidate.CandidateId, targetLanguage, 2, TranslationOrigin.AiTranslation,
                target.RelativePath, target.EntryKey, string.Empty);
            try
            {
                _output.Delete(candidate, targetLanguage, target);
                _repository.CompletePendingDeletion(
                    operationId, candidate.CandidateId, targetLanguage);
            }
            catch (Exception ex)
            {
                _repository.FailPendingFileOperation(operationId, ex);
                throw;
            }
        }

        public Task RestoreLocalClassificationAsync(
            ICollection<string> candidateIds,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunExclusiveAsync(
                WorkflowTaskKind.ClassificationEdit,
                "Restore local classification",
                token => Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    _repository.RestoreLocalClassification(candidateIds);
                }, token),
                cancellationToken,
                CreateCandidateSelectionInput(candidateIds, "RestoreLocal"));
        }

        private async Task RunExclusiveAsync(
            WorkflowTaskKind kind,
            string displayName,
            Func<CancellationToken, Task> operation,
            CancellationToken externalCancellation,
            string inputJson = "")
        {
            if (!WorkflowTaskCoordinator.Instance.TryBegin(kind, displayName, out WorkflowTaskLease lease))
                throw new InvalidOperationException("Another background workflow is already running.");
            // Acquire the lease synchronously, then keep ALL database work and async
            // continuations off Unity's synchronization context, not just the inner loop.
            await Task.Run(async () =>
            {
            using (lease)
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                       lease.CancellationToken, externalCancellation))
            {
                AutoTranslatorSettings.ResetPipelineCancellation();
                bool recorded = false;
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    EnsureDatabaseInitialized();
                    linked.Token.ThrowIfCancellationRequested();
                    _repository.StartWorkflowRun(lease.RunId, kind, inputJson);
                    recorded = true;
                    await operation(linked.Token);
                    _repository.CompleteWorkflowRun(
                        lease.RunId, WorkflowRunState.Completed, string.Empty, string.Empty);
                    WorkflowTaskCoordinator.Instance.MarkTerminal(lease.RunId, WorkflowRunState.Completed);
                }
                catch (OperationCanceledException ex)
                {
                    RecordWorkflowFailure(lease, recorded, WorkflowRunState.Cancelled, ex.Message);
                    throw;
                }
                catch (WorkflowPartialFailureException ex)
                {
                    RecordWorkflowFailure(lease, recorded, WorkflowRunState.Failed, ex.UserSummary,
                        () => PersistPerModResult(lease.RunId, kind, ex.PartialResult), ex.PartialResult);
                    throw;
                }
                catch (Exception ex)
                {
                    RecordWorkflowFailure(lease, recorded, WorkflowRunState.Failed, ex.Message);
                    throw;
                }
                finally
                {
                    WorkflowTaskCoordinator.Instance.NotifyWorkbenchDataChanged();
                }
            }
            }).ConfigureAwait(false);
        }

        private async Task<T> RunExclusiveAsync<T>(
            WorkflowTaskKind kind,
            string displayName,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken externalCancellation,
            string inputJson = "")
        {
            if (!WorkflowTaskCoordinator.Instance.TryBegin(kind, displayName, out WorkflowTaskLease lease))
                throw new InvalidOperationException("Another background workflow is already running.");
            return await Task.Run(async () =>
            {
            using (lease)
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                       lease.CancellationToken, externalCancellation))
            {
                AutoTranslatorSettings.ResetPipelineCancellation();
                bool recorded = false;
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    EnsureDatabaseInitialized();
                    linked.Token.ThrowIfCancellationRequested();
                    _repository.StartWorkflowRun(lease.RunId, kind, inputJson);
                    recorded = true;
                    T result = await operation(linked.Token);
                    PersistPerModResult(lease.RunId, kind, result);
                    _repository.CompleteWorkflowRun(
                        lease.RunId, WorkflowRunState.Completed,
                        JsonConvert.SerializeObject(result), string.Empty);
                    WorkflowTaskCoordinator.Instance.MarkTerminal(lease.RunId, WorkflowRunState.Completed);
                    return result;
                }
                catch (OperationCanceledException ex)
                {
                    RecordWorkflowFailure(lease, recorded, WorkflowRunState.Cancelled, ex.Message);
                    throw;
                }
                catch (WorkflowPartialFailureException ex)
                {
                    RecordWorkflowFailure(lease, recorded, WorkflowRunState.Failed, ex.UserSummary,
                        () => PersistPerModResult(lease.RunId, kind, ex.PartialResult), ex.PartialResult);
                    throw;
                }
                catch (Exception ex)
                {
                    RecordWorkflowFailure(lease, recorded, WorkflowRunState.Failed, ex.Message);
                    throw;
                }
                finally
                {
                    WorkflowTaskCoordinator.Instance.NotifyWorkbenchDataChanged();
                }
            }
            }).ConfigureAwait(false);
        }

        private void RecordWorkflowFailure(WorkflowTaskLease lease, bool recorded,
            WorkflowRunState state, string error, Action persistPartial = null, object partialResult = null)
        {
            try
            {
                if (recorded)
                {
                    persistPartial?.Invoke();
                    _repository.CompleteWorkflowRun(lease.RunId, state,
                        partialResult == null ? string.Empty : JsonConvert.SerializeObject(partialResult), error);
                }
            }
            catch (Exception persistenceError)
            {
                AutoTranslatorSettings.AddWarningLog("任务失败状态写入失败：" + persistenceError.Message);
            }
            finally
            {
                // A database failure must not leave the in-memory task marked Completed
                // on lease disposal, or replace the original error shown to the user.
                WorkflowTaskCoordinator.Instance.MarkTerminal(lease.RunId, state, error);
            }
        }

        private async Task RunLocalAnalysisAsync(Func<Task> analyze, CancellationToken token)
        {
            string language = WorkflowRuntimeSettings.GetTargetLanguageFolder();
            try
            {
                await analyze();
            }
            finally
            {
                // Partial analysis may already have accepted native translations. Commit
                // those source changes to existing managed outputs before runtime reload.
                if (!token.IsCancellationRequested)
                {
                    int changed = await Task.Run(
                        () => _synchronizer.WriteChangedSourceTranslations(language, token), token);
                    if (changed > 0)
                    {
                        AutoTranslatorScanner.NotifyTranslationFilesChanged(
                            System.IO.Path.Combine(_generatedPackRoot, "Languages", language));
                        if (!await AutoTranslatorScanner.RequestMemoryDropAsync())
                            throw new InvalidOperationException(
                                "原生译文已更新到本地文件，但游戏内译文刷新失败，请重试热重载。");
                    }
                }
            }
        }

        private async Task RunOneClickTranslationCoreAsync(
            IList<ModMetaData> mods,
            bool forceAnalysis,
            WorkflowExecutionOptions options,
            CancellationToken cancellationToken)
        {
            List<Exception> failures = new List<Exception>();
            List<string> modIdentities = WorkflowStepSelection.ResolveModIdentities(mods);
            try
            {
                await RunLocalAnalysisAsync(
                    () => _analysis.RunAnalysisButtonAsync(mods, forceAnalysis, cancellationToken),
                    cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Native translation read failures are handled inside the catalog and
                // remain non-fatal. An actual candidate-analysis failure is different:
                // do not send a previous run's candidates to AI as if this run succeeded.
                throw new InvalidOperationException(
                    "本地候选分析未全部完成，已完成结果已保留；本次尚未调用 AI，请处理分析错误后重试。", ex);
            }

            try
            {
                AiStepEstimate reviewResult = await Task.Run(
                    () => _review.ExecuteAsync(
                        modIdentities, options, false, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                PersistPerModResult(Guid.NewGuid(), WorkflowTaskKind.AiReview, reviewResult);
            }
            catch (OperationCanceledException) { throw; }
            catch (WorkflowPartialFailureException ex)
            {
                PersistPerModResult(
                    Guid.NewGuid(), WorkflowTaskKind.AiReview, ex.PartialResult);
                failures.Add(new InvalidOperationException(
                    "AI 复核步骤失败。", ex));
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException("AI 复核步骤失败。", ex));
            }

            try
            {
                AiStepEstimate translationResult =
                    await ExecuteAiTranslationWithInitialSynchronizationAsync(
                        modIdentities, options, cancellationToken).ConfigureAwait(false);
                PersistPerModResult(
                    Guid.NewGuid(), WorkflowTaskKind.AiTranslation, translationResult);
            }
            catch (OperationCanceledException) { throw; }
            catch (WorkflowPartialFailureException ex)
            {
                PersistPerModResult(
                    Guid.NewGuid(), WorkflowTaskKind.AiTranslation, ex.PartialResult);
                failures.Add(new InvalidOperationException(
                    "AI 翻译步骤失败。", ex));
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException("AI 翻译步骤失败。", ex));
            }

            if (failures.Count > 0)
                throw new InvalidOperationException(
                    "一键翻译未全部完成；已完成并保存的结果会保留，请查看错误日志中的具体步骤。",
                    failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }

        private async Task<AiStepEstimate> ExecuteAiTranslationWithInitialSynchronizationAsync(
            IList<string> modIdentities,
            WorkflowExecutionOptions options,
            CancellationToken cancellationToken)
        {
            string targetLanguage = WorkflowRuntimeSettings.GetTargetLanguageFolder();
            if (!_repository.HasCompletedSynchronization(targetLanguage))
            {
                WorkflowTaskCoordinator.Instance.ReportStage(
                    string.Empty,
                    "首次翻译前自动同步译文状态",
                    "⏳ AI 翻译：首次运行，正在自动同步译文状态",
                    "workflow.ai-translation initial-sync start target_language=" + targetLanguage);
                await Task.Run(
                    () => _synchronizer.Synchronize(cancellationToken, targetLanguage),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!_repository.HasCompletedSynchronization(targetLanguage))
                    throw new InvalidOperationException(
                        "首次译文状态同步未能完成，AI 翻译尚未开始；请查看同步错误日志。");
                WorkflowTaskCoordinator.Instance.ReportStage(
                    string.Empty,
                    "首次译文状态同步完成，继续 AI 翻译",
                    "✓ AI 翻译：首次译文状态同步完成",
                    "workflow.ai-translation initial-sync complete target_language=" + targetLanguage);
            }
            return await Task.Run(
                () => _translation.ExecuteAsync(
                    modIdentities, false, options, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        private void PersistPerModResult<T>(Guid runId, WorkflowTaskKind kind, T result)
        {
            Dictionary<string, string> reports = new Dictionary<string, string>(StringComparer.Ordinal);
            if (result is DryRunReport dryRun)
            {
                foreach (KeyValuePair<string, DryRunReport> report in dryRun.ModReports)
                    reports[report.Key] = JsonConvert.SerializeObject(report.Value);
            }
            else if (result is AiStepEstimate estimate)
            {
                HashSet<string> modIdentities = new HashSet<string>(
                    estimate.CountsByMod.Keys, StringComparer.Ordinal);
                modIdentities.UnionWith(estimate.SkippedMods.Keys);
                modIdentities.UnionWith(estimate.ErrorsByMod.Keys);
                foreach (string modIdentity in modIdentities)
                {
                    reports[modIdentity] = JsonConvert.SerializeObject(new
                    {
                        candidateCount = GetCount(estimate.CountsByMod, modIdentity),
                        inputTokens = GetCount(estimate.InputTokensByMod, modIdentity),
                        outputTokens = GetCount(estimate.OutputTokensByMod, modIdentity),
                        completedCount = GetCount(estimate.CompletedByMod, modIdentity),
                        failedCount = GetCount(estimate.FailedByMod, modIdentity),
                        producedDecisionCount = GetCount(estimate.ProducedDecisionsByMod, modIdentity),
                        effectiveClassificationChangedCount = GetCount(
                            estimate.EffectiveClassificationChangesByMod, modIdentity),
                        protectedByManualCount = GetCount(
                            estimate.ProtectedByManualByMod, modIdentity),
                        skippedReason = estimate.SkippedMods.TryGetValue(
                            modIdentity, out string skipped) ? skipped : string.Empty,
                        errors = estimate.ErrorsByMod.TryGetValue(
                            modIdentity, out List<string> errors) ? errors : new List<string>()
                    });
                }
            }
            _repository.SaveWorkflowModReports(runId, kind, reports);
        }

        private static long GetCount(IDictionary<string, long> counts, string modIdentity)
        {
            return counts != null && counts.TryGetValue(modIdentity, out long value) ? value : 0L;
        }

        private void EnsureDatabaseInitialized()
        {
            lock (_initializationGate)
            {
                if (_databaseInitialized) return;
                _repository.Initialize();
                _databaseInitialized = true;
            }
        }

        private static string CreateSelectionInput(
            IEnumerable<ModMetaData> mods,
            bool? forceAnalysis,
            WorkflowExecutionOptions options)
        {
            return JsonConvert.SerializeObject(new
            {
                selectedMods = (mods ?? Enumerable.Empty<ModMetaData>())
                    .Where(mod => mod != null)
                    .Select(mod => new
                    {
                        modIdentity = ModAnalysisTargetFactory.CreateModIdentity(mod),
                        packageId = mod.PackageId ?? string.Empty,
                        displayName = mod.Name ?? string.Empty
                    })
                    .ToList(),
                forceAnalysis,
                temporaryOptions = options
            });
        }

        private static string CreateIdentitySelectionInput(
            IEnumerable<string> modIdentities,
            WorkflowExecutionOptions options)
        {
            return JsonConvert.SerializeObject(new
            {
                selectedModIdentities = (modIdentities ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                temporaryOptions = options
            });
        }

        private static string CreateCandidateSelectionInput(
            IEnumerable<string> candidateIds,
            string operation)
        {
            List<string> normalized = (candidateIds ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            return JsonConvert.SerializeObject(new
            {
                operation = operation ?? string.Empty,
                candidateCount = normalized.Count,
                candidateSelectionFingerprint = WorkflowIdentity.HashText(
                    string.Join("\n", normalized)),
                candidateIdSample = normalized.Take(100).ToList(),
                sampleTruncated = normalized.Count > 100
            });
        }

    }
}
