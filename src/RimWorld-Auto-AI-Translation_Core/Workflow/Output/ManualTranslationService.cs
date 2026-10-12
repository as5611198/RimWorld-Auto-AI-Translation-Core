using AutoTranslator_Core.Workflow.Persistence;
using System;

namespace AutoTranslator_Core.Workflow.Output
{
    internal sealed class ManualTranslationService
    {
        private readonly WorkflowRepository _repository;
        private readonly ITranslationOutputStore _output;

        public ManualTranslationService(WorkflowRepository repository, ITranslationOutputStore output)
        {
            _repository = repository;
            _output = output;
        }

        public CandidateSourceDomain Save(string candidateId, string translatedText)
        {
            return Save(candidateId, translatedText, WorkflowRuntimeSettings.GetTargetLanguageFolder());
        }

        internal CandidateSourceDomain Save(
            string candidateId, string translatedText, string targetLanguage,
            TranslationWriteTarget writeTarget = null)
        {
            AutoTranslatorSettings.AddDebugLog(
                "workflow.manual-translation save start candidate=" + candidateId + " target=" + targetLanguage);
            CandidateRecord candidate = RequireCandidate(candidateId, targetLanguage);
            if (!AutoTranslatorScanner.TryAcceptTranslatedValue(
                    translatedText, candidate.SourceText,
                    out string sanitized, out string failureReason, out string failureDetail))
                throw new InvalidOperationException(
                    "Manual translation validation failed (" + failureReason + "): " + failureDetail);
            TranslationWriteTarget target = writeTarget ?? _output.Resolve(candidate, targetLanguage);
            Guid operationId = _repository.CreatePendingFileOperation(
                candidateId, targetLanguage, 1, TranslationOrigin.Manual,
                target.RelativePath, target.EntryKey, sanitized);
            try
            {
                _output.Write(candidate, targetLanguage, sanitized, TranslationOrigin.Manual, target);
                _repository.CompletePendingTranslation(
                    operationId, candidateId, targetLanguage, sanitized,
                    TranslationOrigin.Manual, target.RelativePath, target.EntryKey,
                    markManualClassification: true);
                AutoTranslatorSettings.AddDebugLog(
                    "workflow.manual-translation save complete candidate=" + candidateId);
            }
            catch (Exception ex)
            {
                _repository.FailPendingFileOperation(operationId, ex);
                throw;
            }
            return candidate.SourceDomain;
        }

        public CandidateSourceDomain Delete(string candidateId)
        {
            string targetLanguage = WorkflowRuntimeSettings.GetTargetLanguageFolder();
            AutoTranslatorSettings.AddDebugLog(
                "workflow.manual-translation delete start candidate=" + candidateId + " target=" + targetLanguage);
            CandidateRecord candidate = RequireCandidate(candidateId, targetLanguage);
            if (!string.IsNullOrWhiteSpace(candidate.TranslationFileRelativePath))
            {
                TranslationWriteTarget target = new TranslationWriteTarget
                {
                    RelativePath = candidate.TranslationFileRelativePath,
                    EntryKey = candidate.TranslationEntryKey
                };
                Guid operationId = _repository.CreatePendingFileOperation(
                    candidateId, targetLanguage, 2, TranslationOrigin.None,
                    target.RelativePath, target.EntryKey, string.Empty);
                try
                {
                    _output.Delete(candidate, targetLanguage, target);
                    _repository.CompletePendingDeletion(operationId, candidateId, targetLanguage);
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.manual-translation delete complete candidate=" + candidateId + " managedOutput=true");
                }
                catch (Exception ex)
                {
                    _repository.FailPendingFileOperation(operationId, ex);
                    throw;
                }
                return candidate.SourceDomain;
            }
            _repository.ClearTranslation(candidateId, targetLanguage);
            AutoTranslatorSettings.AddDebugLog(
                "workflow.manual-translation delete complete candidate=" + candidateId + " managedOutput=false");
            return candidate.SourceDomain;
        }

        private CandidateRecord RequireCandidate(string candidateId, string targetLanguage)
        {
            CandidateRecord candidate = _repository.GetCandidate(candidateId, targetLanguage);
            if (candidate == null) throw new InvalidOperationException("Translation candidate was not found: " + candidateId);
            if (!candidate.IsPresent)
                throw new InvalidOperationException("Translation candidate is no longer present in the current mod version: " + candidateId);
            return candidate;
        }
    }
}
