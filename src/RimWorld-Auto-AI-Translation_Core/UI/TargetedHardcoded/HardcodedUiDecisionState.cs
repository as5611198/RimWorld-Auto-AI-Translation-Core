using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal static class HardcodedUiDecisionState
    {
        private static readonly object Gate = new object();
        private static HardcodedUiDecisionStore _store;

        internal static Dictionary<string, HardcodedUiDecisionRecord> AnalyzeAndPersist(
            IEnumerable<HardcodedUiPatchEntry> entries)
        {
            List<HardcodedUiPatchEntry> materialized = (entries ??
                    Enumerable.Empty<HardcodedUiPatchEntry>())
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.EntryId))
                .ToList();
            HardcodedUiDecisionStore store = GetStore();
            var analyzed = new List<HardcodedUiDecisionRecord>(materialized.Count);
            foreach (HardcodedUiPatchEntry entry in materialized)
            {
                store.TryGet(entry.EntryId, out HardcodedUiDecisionRecord existing);
                analyzed.Add(HardcodedUiBaselineDecisionAnalyzer.Analyze(entry, existing));
            }
            store.UpsertMany(analyzed);
            return analyzed.ToDictionary(
                record => record.EntryId,
                record => record.Clone(),
                StringComparer.Ordinal);
        }

        internal static void Persist(IEnumerable<HardcodedUiDecisionRecord> records)
        {
            GetStore().UpsertMany(records);
        }

        private static HardcodedUiDecisionStore GetStore()
        {
            lock (Gate)
            {
                if (_store != null) return _store;
                _store = new HardcodedUiDecisionStore(Path.Combine(
                    AutoTranslatorScanner.GetLocalPackPath(),
                    "Cache",
                    "HardcodedUiAnalysis.v1.json"));
                return _store;
            }
        }
    }
}
