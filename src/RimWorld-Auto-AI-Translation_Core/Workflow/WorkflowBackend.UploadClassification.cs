using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using AutoTranslator_Core.Workflow.Analysis;

namespace AutoTranslator_Core.Workflow
{
    public sealed class UploadClassificationSummary
    {
        public long TotalEntries;
        public long AiEntries;
        internal HashSet<string> AiSourceIdentities = new HashSet<string>(StringComparer.Ordinal);
        internal bool IsAiEntry(string file, string key, string text)
        {
            return AiSourceIdentities.Contains(WorkflowBackend.UploadEntryIdentity(
                file, key, WorkflowIdentity.HashText(text)));
        }
        public double AiPercentage => TotalEntries == 0 ? 0d : 100d * AiEntries / TotalEntries;
        public bool CanLabelManual => AiEntries <= TotalEntries / 2;
        public string Description => AutoTranslatorMod.WfText(
            $"本次上传：{TotalEntries} 条译文，AI 来源 {AiEntries} 条（{AiPercentage:F1}%）。",
            $"This upload: {TotalEntries} translations; {AiEntries} AI entries ({AiPercentage:F1}%).");
        public string ManualBlockedReason => AutoTranslatorMod.WfText(
            Description + " AI 来源超过 50%，不能标记为人工精翻，请选择 AI 翻译。",
            Description + " AI exceeds 50%; choose the AI translation label instead of Human-curated.");
    }

    public sealed partial class WorkflowBackend
    {
        public UploadClassificationSummary EvaluateUploadClassification(
            string sourceFolder, string packageId, string targetLanguage)
        {
            EnsureDatabaseInitialized();
            HashSet<string> aiEntries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in _repository.GetUploadTranslationSources(packageId, targetLanguage))
            {
                if (row.Origin != TranslationOrigin.AiTranslation) continue;
                if (string.IsNullOrWhiteSpace(row.OutputFile)) continue;
                string fullFile = Path.GetFullPath(Path.Combine(_generatedPackRoot,
                    row.OutputFile.Replace('/', Path.DirectorySeparatorChar)));
                if (string.IsNullOrWhiteSpace(row.AiProvider))
                {
                    // Older synchronization could mark an unclassified local file as AI.
                    // Without an AI request record, require its explicit entry provenance.
                    var provenance = AutoTranslatorScanner.GetFileEntryProvenance(
                        Path.Combine(_generatedPackRoot, "Languages", targetLanguage),
                        packageId, fullFile, row.EntryKey, row.TranslationText);
                    if (provenance == null ||
                        (!string.Equals(provenance.SourceKind, AutoTranslatorScanner.ProvenanceKindAI, StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(provenance.SourceKind, AutoTranslatorScanner.ProvenanceKindAIFromSecondary, StringComparison.OrdinalIgnoreCase))) continue;
                }
                aiEntries.Add(UploadEntryIdentity(fullFile, row.EntryKey,
                    WorkflowIdentity.HashText(row.TranslationText)));
            }
            UploadClassificationSummary summary = new UploadClassificationSummary { AiSourceIdentities = aiEntries };
            foreach (string file in AutoTranslatorScanner.GetXmlFilesForTranslationCache(sourceFolder, SearchOption.AllDirectories))
            {
                if (!AutoTranslatorScanner.ShouldIncludeUploadFile(sourceFolder, file, packageId)) continue;
                foreach (var entry in AutoTranslatorScanner.LoadXmlFileToDict(file))
                {
                    if (string.IsNullOrWhiteSpace(entry.Value)) continue;
                    summary.TotalEntries++;
                    if (aiEntries.Contains(UploadEntryIdentity(file, entry.Key,
                            WorkflowIdentity.HashText(entry.Value)))) summary.AiEntries++;
                }
            }
            return summary;
        }

        internal static string UploadEntryIdentity(string file, string key, string hash)
        {
            return JsonConvert.SerializeObject(new[]
            {
                Path.GetFullPath(file).Replace('\\', '/').ToLowerInvariant(), key ?? string.Empty, hash ?? string.Empty
            });
        }
    }
}
