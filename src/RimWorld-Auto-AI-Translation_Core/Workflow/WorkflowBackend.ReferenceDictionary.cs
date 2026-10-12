using System;
using System.Collections.Generic;

namespace AutoTranslator_Core.Workflow
{
    public sealed partial class WorkflowBackend
    {
        public List<ReferenceDictionaryEntry> GetReferenceDictionaryEntries(string language)
        {
            EnsureDatabaseInitialized();
            return _repository.GetReferenceDictionaryEntries(language, includeDisabled: true);
        }

        public List<WorkflowModMetadataSnapshot> GetReferenceDictionaryScopes()
        {
            EnsureDatabaseInitialized();
            return _repository.GetReferenceDictionaryScopes();
        }

        public void SaveReferenceDictionaryEntry(ReferenceDictionaryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            entry.SourceForm = (entry.SourceForm ?? string.Empty).Trim();
            entry.TargetForm = (entry.TargetForm ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(entry.SourceForm) || string.IsNullOrEmpty(entry.TargetForm) ||
                string.IsNullOrWhiteSpace(entry.TargetLanguage))
                throw new InvalidOperationException("原词、建议译名和目标语种不能为空。");
            EnsureDatabaseInitialized();
            if (!WorkflowTaskCoordinator.Instance.TryBegin(WorkflowTaskKind.ReferenceDictionaryEdit,
                "保存参考字典", out WorkflowTaskLease lease))
                throw new InvalidOperationException("后台任务进行中，请结束后再编辑字典。");
            using (lease)
            {
                if (string.IsNullOrEmpty(entry.EntryId)) entry.EntryId = "user:" + Guid.NewGuid().ToString("N");
                _repository.SaveReferenceDictionaryEntry(entry);
                WorkflowTaskCoordinator.Instance.NotifyWorkbenchDataChanged();
            }
        }
    }
}
