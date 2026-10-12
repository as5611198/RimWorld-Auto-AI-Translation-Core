using System;
using System.Collections.Generic;

namespace AutoTranslator_Core.Workflow
{
    public enum CandidateClassification : byte
    {
        NotAnalyzed = 0,
        Undetermined = 1,
        NeedsTranslation = 2,
        NoTranslationNeeded = 3
    }

    public enum ClassificationLayer : byte
    {
        Xml = 0,
        Dll = 1,
        AiReview = 2,
        Manual = 3
    }

    public enum CandidateTranslationState : byte
    {
        Untranslated = 0,
        Translating = 1,
        Translated = 2,
        Failed = 3
    }

    public enum TranslationOrigin : byte
    {
        None = 0,
        AiTranslation = 1,
        Cloud = 2,
        ModNative = 3,
        ThirdParty = 4,
        Manual = 5
    }

    public enum CandidateSourceDomain : byte
    {
        Xml = 0,
        Dll = 1
    }

    public enum WorkflowTaskKind : byte
    {
        StartupSynchronization = 0,
        ManualSynchronization = 1,
        XmlAnalysis = 2,
        DllAnalysis = 3,
        AnalysisBatch = 4,
        DryRun = 5,
        AiReview = 6,
        AiTranslation = 7,
        OneClickTranslation = 8,
        CloudTranslation = 9,
        ManualTranslationEdit = 10,
        ClassificationEdit = 11,
        ConfigurationEdit = 12,
        ExpiredDataCleanup = 13,
        ResultCleanup = 14,
        RuntimeTranslationReload = 15,
        ModCatalogSynchronization = 16,
        ReferenceDictionaryEdit = 17,
        ApiConnectionTest = 18,
        TranslationFileMaintenance = 19,
        HistoricalDataMigration = 20
    }

    public enum WorkflowRunState : byte
    {
        Running = 0,
        Completed = 1,
        Cancelled = 2,
        Failed = 3
    }

    public enum AnalysisResultFreshness : byte
    {
        NeverAnalyzed = 0,
        Expired = 1,
        Current = 2,
        Failed = 3
    }

    public sealed class AnalysisRunStatusSummary
    {
        public string TargetLanguage { get; set; } = string.Empty;
        public string ModIdentity { get; set; } = string.Empty;
        public CandidateSourceDomain SourceDomain { get; set; }
        public WorkflowRunState State { get; set; }
        public string ErrorText { get; set; } = string.Empty;
        public DateTime StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public AnalysisResultFreshness Freshness { get; set; }
        public string AnalyzerVersion { get; set; } = string.Empty;
        public string ModVersionFingerprint { get; set; } = string.Empty;
        public string CurrentAnalyzerVersion { get; set; } = string.Empty;
        public string CurrentModVersionFingerprint { get; set; } = string.Empty;
    }

    public sealed class CandidatePage
    {
        public int TotalCount { get; set; }
        public List<CandidateRecord> Candidates { get; set; } = new List<CandidateRecord>();
    }

    public sealed class ExpiredWorkflowDataSummary
    {
        public long CandidateCount { get; set; }
        public long TranslationResultCount { get; set; }
        public long TranslationFileEntryCount { get; set; }
        public long PendingFileOperationCount { get; set; }
        public long DatabaseBytesBefore { get; set; }
        public long DatabaseBytesAfter { get; set; }
        public bool DatabaseCompacted { get; set; }

        public bool HasExpiredData => CandidateCount > 0;
    }

    public sealed class WorkflowResultCleanupOptions
    {
        public bool ClearAiReviewResults { get; set; }
        public bool ClearManualClassificationResults { get; set; }
        public bool ClearLocalAiTranslationResults { get; set; }

        public bool HasAnySelection =>
            ClearAiReviewResults ||
            ClearManualClassificationResults ||
            ClearLocalAiTranslationResults;
    }

    public sealed class WorkflowResultCleanupSummary
    {
        public int RequestedModCount { get; set; }
        public int ProtectedModCount { get; set; }
        public long AiReviewCandidateCount { get; set; }
        public long ManualClassificationCandidateCount { get; set; }
        public long LocalAiTranslationCount { get; set; }
    }

    public sealed class LocalAiTranslationPage
    {
        public List<CandidateRecord> Candidates { get; set; } = new List<CandidateRecord>();
        public string NextCandidateId { get; set; } = string.Empty;
        public bool HasMore { get; set; }
    }

    public sealed class AiCandidateCursor
    {
        public string SourceFileRelativePath { get; set; } = string.Empty;
        public int SourceLineNumber { get; set; }
        public string CandidateId { get; set; } = string.Empty;
    }

    public sealed class AiCandidatePage
    {
        public List<CandidateRecord> Candidates { get; set; } = new List<CandidateRecord>();
        public AiCandidateCursor NextCursor { get; set; }
        public bool HasMore { get; set; }
    }

    public sealed class WorkbenchModAggregate
    {
        public string ModIdentity { get; set; } = string.Empty;
        public CandidateSourceDomain SourceDomain { get; set; }
        public int CandidateCount { get; set; }
        public int LocalNeedsTranslation { get; set; }
        public int LocalUndetermined { get; set; }
        public int LocalNoTranslationNeeded { get; set; }
        public int EffectiveNeedsTranslation { get; set; }
        public int EffectiveUndetermined { get; set; }
        public int EffectiveNoTranslationNeeded { get; set; }
        public int Untranslated { get; set; }
        public int Translating { get; set; }
        public int Translated { get; set; }
        public int Failed { get; set; }
    }

    public sealed class WorkflowPartialFailureException : AggregateException
    {
        public WorkflowPartialFailureException(
            string message,
            IEnumerable<Exception> innerExceptions,
            object partialResult)
            : base(message, innerExceptions)
        {
            PartialResult = partialResult;
            UserSummary = message ?? string.Empty;
        }

        public object PartialResult { get; }
        public string UserSummary { get; }
    }

    public sealed class CandidateRecord
    {
        public string CandidateId { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string ModIdentity { get; set; } = string.Empty;
        public string ModVersionFingerprint { get; set; } = string.Empty;
        public string SnapshotId { get; set; } = string.Empty;
        public CandidateSourceDomain SourceDomain { get; set; }
        public string EntryKind { get; set; } = string.Empty;
        public string LogicalLocator { get; set; } = string.Empty;
        public string ContextJson { get; set; } = string.Empty;
        public string DefaultOutputFileRelativePath { get; set; } = string.Empty;
        public string SourceFileRelativePath { get; set; } = string.Empty;
        public int SourceLineNumber { get; set; }
        public int SourceLineEnd { get; set; }
        public string SourceText { get; set; } = string.Empty;
        public string SourceTextHash { get; set; } = string.Empty;
        public string ContentFingerprint { get; set; } = string.Empty;
        public string IdentitySchemaVersion { get; set; } = WorkflowIdentity.CandidateIdentitySchemaVersion;
        public string EntryIdentity { get; set; } = string.Empty;
        public long EstimatedTokens { get; set; }
        public byte ClassificationFlags { get; set; }
        public string XmlAnalyzerVersion { get; set; } = string.Empty;
        public string XmlAnalysisFingerprint { get; set; } = string.Empty;
        public string DllAnalyzerVersion { get; set; } = string.Empty;
        public string DllAnalysisFingerprint { get; set; } = string.Empty;
        public string AiReviewVersion { get; set; } = string.Empty;
        public string AiReviewPromptVersion { get; set; } = string.Empty;
        public string AiReviewFingerprint { get; set; } = string.Empty;
        public string XmlReasonCode { get; set; } = string.Empty;
        public string DllReasonCode { get; set; } = string.Empty;
        public string AiReviewReason { get; set; } = string.Empty;
        public DateTime? ManualUpdatedUtc { get; set; }
        public bool IsPresent { get; set; } = true;
        public CandidateTranslationState TranslationState { get; set; }
        public TranslationOrigin TranslationOrigin { get; set; }
        public string TranslationText { get; set; } = string.Empty;
        public string TranslationHash { get; set; } = string.Empty;
        public string SourceTextHashAtTranslation { get; set; } = string.Empty;
        public string TranslationFileRelativePath { get; set; } = string.Empty;
        public string TranslationEntryKey { get; set; } = string.Empty;
        public string TranslationSourcePackageId { get; set; } = string.Empty;
        public string TranslationSourceFileRelativePath { get; set; } = string.Empty;
        public string TranslationSourceEntryKey { get; set; } = string.Empty;
        public string ValidationStatus { get; set; } = string.Empty;
        public string TranslationError { get; set; } = string.Empty;
        public string LastSyncStatus { get; set; } = string.Empty;
        public string LastSyncError { get; set; } = string.Empty;
        public DateTime? LastSyncedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public CandidateClassification EffectiveClassification =>
            ClassificationFlagsCodec.GetEffective(this);
    }

    public sealed class ModSnapshotRecord
    {
        public string SnapshotId { get; set; } = string.Empty;
        public string ModIdentity { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string NormalizedPackageId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
        public string InstallationSource { get; set; } = string.Empty;
        public string InstallationSourceId { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public string GameVersion { get; set; } = string.Empty;
        public string LoadFoldersJson { get; set; } = string.Empty;
        public string VersionLabel { get; set; } = string.Empty;
        public string VersionFingerprint { get; set; } = string.Empty;
        public DateTime LastObservedUtc { get; set; }
    }

    public sealed class ModFileRecord
    {
        public string ModIdentity { get; set; } = string.Empty;
        public string ModVersionFingerprint { get; set; } = string.Empty;
        public string SnapshotId { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string FileHash { get; set; } = string.Empty;
        public long FileLength { get; set; }
        public int LineCount { get; set; }
        public long EstimatedTokens { get; set; }
        public string FileType { get; set; } = string.Empty;
        public DateTime LastWriteUtc { get; set; }
        public bool IsPresent { get; set; } = true;
    }

    public sealed class DryRunEstimate
    {
        public string ModIdentity { get; set; } = string.Empty;
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens => InputTokens + OutputTokens;
        public Dictionary<string, long> ItemCounts { get; } = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    public sealed class PendingFileOperationRecord
    {
        public string ReuseProvenanceJson { get; set; } = string.Empty;
        public Guid OperationId { get; set; }
        public string CandidateId { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = string.Empty;
        public int OperationKind { get; set; }
        public TranslationOrigin TranslationOrigin { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public string EntryKey { get; set; } = string.Empty;
        public string DesiredText { get; set; } = string.Empty;
        public string DesiredHash { get; set; } = string.Empty;
        public string SourceTextHash { get; set; } = string.Empty;
        public string AiProvider { get; set; } = string.Empty;
        public string AiModel { get; set; } = string.Empty;
        public string AiPromptVersion { get; set; } = string.Empty;
        public string AiRunId { get; set; } = string.Empty;
        public int AiBatchIndex { get; set; }
    }

    public sealed class PendingTranslationCompletion
    {
        public Guid OperationId { get; set; }
        public string CandidateId { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = string.Empty;
        public string TranslationText { get; set; } = string.Empty;
        public TranslationOrigin Origin { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public string EntryKey { get; set; } = string.Empty;
        public bool MarkManualClassification { get; set; }
        public string AiProvider { get; set; } = string.Empty;
        public string AiModel { get; set; } = string.Empty;
        public string AiPromptVersion { get; set; } = string.Empty;
        public string AiRunId { get; set; } = string.Empty;
        public int AiBatchIndex { get; set; }
    }

    public sealed class AiClassificationUpdate
    {
        public string CandidateId { get; set; } = string.Empty;
        public CandidateClassification Classification { get; set; }
        public string ReviewVersion { get; set; } = string.Empty;
        public string PromptVersion { get; set; } = string.Empty;
        public string ReviewFingerprint { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    internal sealed class WorkflowModEligibility
    {
        public List<string> EligibleModIdentities { get; } = new List<string>();
        public HashSet<string> MissingLocalAnalysisMods { get; } =
            new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> UnreadyTranslationStateMods { get; } =
            new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, string> SkippedMods { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
