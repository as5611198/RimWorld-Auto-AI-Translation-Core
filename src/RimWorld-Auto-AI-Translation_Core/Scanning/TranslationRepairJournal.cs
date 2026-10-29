using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace AutoTranslator_Core
{
    public sealed class TranslationRepairRecord
    {
        public string Id, PackageId, GameVersion, Environment, DefType, OriginalKey, NewKey, Translation;
        public string SourceFile, SourceKind, Reason, Status, Evidence;
        public string ModVersionEvidence, Provenance;
        public string NativeFailureKey;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string AppliedTranslation;
    }

    // Append by content identity, not by key: every different paid value/source survives.
    // No translator, network client or upload dependency is reachable from this store.
    public sealed class TranslationRepairJournal
    {
        internal static event Action<TranslationRepairRecord> Observed;
        private readonly string root;
        private readonly object gate = new object();
        private readonly object writerGate = new object();
        private readonly object backupGate = new object();
        private readonly Dictionary<string, TranslationRepairRecord> records;
        private readonly Dictionary<string, List<TranslationRepairRecord>> bySource = new Dictionary<string, List<TranslationRepairRecord>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> nativeFailures = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, TranslationRepairRecord> pending = new Dictionary<string, TranslationRepairRecord>();
        private bool uncompacted;
        private volatile bool historyLoaded;
        public TranslationRepairJournal(string root) : this(root, true) { }
        internal TranslationRepairJournal(string root, bool loadExisting)
        {
            this.root = root;
            records = new Dictionary<string, TranslationRepairRecord>();
            if (loadExisting) WarmHistory();
        }
        internal void WarmHistory()
        {
            if (historyLoaded) return;
            lock (writerGate)
            {
                if (historyLoaded) return;
                var existing = new List<TranslationRepairRecord>();
                string path = Path.Combine(root, "records.json");
                if (File.Exists(path)) existing.AddRange(JsonConvert.DeserializeObject<List<TranslationRepairRecord>>(File.ReadAllText(path)) ?? new List<TranslationRepairRecord>());
                string checkpoints = Path.Combine(root, "checkpoints");
                if (Directory.Exists(checkpoints))
                    foreach (string checkpoint in Directory.GetFiles(checkpoints, "*.json"))
                    {
                        existing.AddRange(JsonConvert.DeserializeObject<List<TranslationRepairRecord>>(File.ReadAllText(checkpoint)) ?? new List<TranslationRepairRecord>());
                        uncompacted = true;
                    }
                lock (gate)
                    foreach (var record in existing)
                        if (record?.Id != null && !records.ContainsKey(record.Id)) { records.Add(record.Id, record); Index(record); }
                historyLoaded = true;
            }
        }
        public void Record(TranslationRepairRecord record)
        {
            record.Id = Hash(string.Join("\n", new[] { record.DefType, record.OriginalKey, record.Translation,
                record.SourceFile, record.SourceKind, record.PackageId, record.GameVersion, record.Environment, record.ModVersionEvidence, record.Provenance, record.Status, record.Reason, record.NewKey }));
            if (!string.IsNullOrEmpty(record.NativeFailureKey)) record.Id = Hash(record.Id + "\n" + record.NativeFailureKey);
            if (record.AppliedTranslation != null) record.Id = Hash(record.Id + "\napplied:" + record.AppliedTranslation);
            lock (gate)
            {
                if (!records.ContainsKey(record.Id))
                {
                    records.Add(record.Id, record); Index(record); pending.Add(record.Id, record);
                }
            }
            // Re-observation is relevant to this session even when history is unchanged.
            Observed?.Invoke(record);
        }
        public List<TranslationRepairRecord> Snapshot()
        {
            List<TranslationRepairRecord> snapshot;
            lock (gate) snapshot = records.Values.ToList();
            snapshot.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            return snapshot;
        }
        public List<TranslationRepairRecord> ForSource(string source)
        {
            lock (gate) return bySource.TryGetValue(source ?? "", out var entries) ? entries.ToList() : new List<TranslationRepairRecord>();
        }
        internal bool HasNativeFailure(string identity)
        {
            lock (gate) return nativeFailures.Contains(identity);
        }
        private void Index(TranslationRepairRecord record)
        {
            if (!string.IsNullOrEmpty(record.NativeFailureKey) && record.Reason != "config_validation_unavailable" && record.Reason != "validation_unavailable") nativeFailures.Add(record.NativeFailureKey);
            string source = record.SourceFile ?? "";
            if (!bySource.TryGetValue(source, out var entries)) bySource[source] = entries = new List<TranslationRepairRecord>();
            entries.Add(record);
        }
        // Small atomic checkpoints preserve new paid values before source mutation.
        // Only a completed run needs to rewrite the full historical report.
        public void Checkpoint()
        {
            lock (writerGate)
            {
                List<TranslationRepairRecord> additions;
                lock (gate) additions = pending.Values.ToList();
                if (additions.Count == 0) return;
                additions.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
                string id = Hash(string.Join("\n", additions.Select(r => r.Id)));
                AtomicText(Path.Combine(root, "checkpoints", id + ".json"), JsonConvert.SerializeObject(additions, Formatting.None));
                lock (gate) foreach (var record in additions) pending.Remove(record.Id);
                uncompacted = true;
            }
        }

        public void Flush()
        {
            WarmHistory();
            lock (writerGate)
            {
                List<TranslationRepairRecord> snapshot;
                lock (gate)
                {
                    if (pending.Count == 0 && !uncompacted) return;
                    snapshot = records.Values.ToList();
                }
                snapshot.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
                Directory.CreateDirectory(root);
                string checkpointRoot = Path.Combine(root, "checkpoints");
                string[] checkpoints = Directory.Exists(checkpointRoot) ? Directory.GetFiles(checkpointRoot, "*.json") : new string[0];
                string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                AtomicText(Path.Combine(root, "records.json"), json);
                var lines = new List<string> { "# 翻譯自動修復紀錄", "", "原譯文全部保留；此流程不呼叫翻譯服務。", "" };
                foreach (var group in snapshot.GroupBy(r => r.Status + " / " + r.Reason))
                    lines.Add("- " + group.Key + "：" + group.Count());
                lines.Add(""); lines.Add("完整來源、原鍵、新鍵、譯文、環境及衝突候選請見同目錄 records.json 與 checkpoints。歷史紀錄包含先前環境，不是目前遊戲錯誤總數。");
                foreach (var group in snapshot.GroupBy(r => (r.PackageId ?? "unknown") + " / " + r.SourceFile + " / " + r.Reason))
                {
                    lines.Add(""); lines.Add("## " + group.Key);
                    foreach (var r in group) lines.Add("- " + r.Status + "：`" + r.OriginalKey + "` → `" + r.NewKey + "`；" + r.Evidence);
                }
                AtomicText(Path.Combine(root, "修復紀錄.md"), string.Join(Environment.NewLine, lines));
                // Delete only after both reports commit. Replaying a checkpoint
                // after an interrupted compaction is harmless (content identity).
                foreach (string checkpoint in checkpoints) File.Delete(checkpoint);
                lock (gate) foreach (var record in snapshot) pending.Remove(record.Id);
                uncompacted = false;
            }
        }
        public string Backup(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string backup = Path.Combine(root, "originals", Hash(Path.GetFullPath(path)) + "-" + HashBytes(bytes) + ".bak");
            lock (backupGate)
            {
                if (!File.Exists(backup)) TranslationXmlAtomicFileStore.Save(backup, s => s.Write(bytes, 0, bytes.Length));
            }
            return backup;
        }
        public static void AtomicText(string path, string text)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            TranslationXmlAtomicFileStore.Save(path, s => s.Write(bytes, 0, bytes.Length));
        }
        public static string Hash(string text) => HashBytes(Encoding.UTF8.GetBytes(text ?? ""));
        private static string HashBytes(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
