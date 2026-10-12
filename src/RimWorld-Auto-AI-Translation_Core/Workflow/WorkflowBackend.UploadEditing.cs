using AutoTranslator_Core.Workflow.Output;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTranslator_Core.Workflow
{
    internal sealed class UploadPreviewTranslationEdit
    {
        public string File;
        public string Key;
        public string Text;
    }

    public sealed partial class WorkflowBackend
    {
        internal Task SaveUploadPreviewEditsAsync(
            string sourceDirectory, string packageId, string language,
            IList<UploadPreviewTranslationEdit> edits)
        {
            return RunExclusiveAsync(
                WorkflowTaskKind.ManualTranslationEdit, "Save upload preview edits",
                async token =>
                {
                    await Task.Run(() =>
                    {
                        // Only exact generated-file/key matches belong to this database.
                        // Editing a cloud workspace must not overwrite a local candidate.
                        var managed = _repository.GetUploadTranslationSources(packageId, language)
                            .Where(row => !string.IsNullOrWhiteSpace(row.OutputFile))
                            .ToLookup(row => UploadEntryIdentity(
                                Path.Combine(_generatedPackRoot, row.OutputFile.Replace('/', Path.DirectorySeparatorChar)),
                                row.EntryKey, string.Empty), StringComparer.Ordinal);
                        foreach (var file in edits.GroupBy(edit => edit.File, WorkflowPath.Comparer))
                        {
                            token.ThrowIfCancellationRequested();
                            List<UploadPreviewTranslationEdit> external = new List<UploadPreviewTranslationEdit>();
                            foreach (UploadPreviewTranslationEdit edit in file)
                            {
                                token.ThrowIfCancellationRequested();
                                var bindings = managed[UploadEntryIdentity(edit.File, edit.Key, string.Empty)].ToList();
                                if (bindings.Count == 0)
                                {
                                    external.Add(edit);
                                    continue;
                                }
                                foreach (var binding in bindings)
                                    _manualTranslation.Save(binding.CandidateId, edit.Text, language,
                                        new TranslationWriteTarget
                                        {
                                            RelativePath = binding.OutputFile,
                                            EntryKey = binding.EntryKey
                                        });
                            }
                            if (external.Count == 0) continue;
                            // Read after managed writes so other edits in this same file survive.
                            Dictionary<string, string> values = AutoTranslatorScanner.LoadXmlFileToDict(file.Key);
                            var provenance = new Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry>(
                                StringComparer.OrdinalIgnoreCase);
                            foreach (UploadPreviewTranslationEdit edit in external)
                            {
                                values[edit.Key] = edit.Text;
                                provenance[edit.Key] = AutoTranslatorScanner.CreateProvenance(
                                    AutoTranslatorScanner.ProvenanceKindManualEdit,
                                    packageId, "", file.Key, "", edit.Text);
                            }
                            AutoTranslatorScanner.SaveXml(file.Key, values);
                            AutoTranslatorScanner.SaveProvenanceForFile(
                                sourceDirectory, packageId, file.Key, values, provenance);
                        }
                    }, token);
                    AutoTranslatorScanner.NotifyTranslationFilesChanged(sourceDirectory);
                    UIInterceptor.ResetProbeCaches();
                    if (string.Equals(language, WorkflowRuntimeSettings.GetTargetLanguageFolder(),
                            StringComparison.OrdinalIgnoreCase) &&
                        !await RefreshEditedTranslationAsync(CandidateSourceDomain.Xml))
                        throw new InvalidOperationException("译文已保存，但游戏内刷新失败，请重试热重载。");
                }, CancellationToken.None);
        }
    }
}
