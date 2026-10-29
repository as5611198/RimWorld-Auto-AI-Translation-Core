using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core
{
    // A run contains observations from this check, never totals from records.json.
    public sealed class TranslationRepairRun
    {
        private sealed class Issue
        {
            public TranslationRepairRecord Record;
            public bool Conflict;
            public int State; // 0 verifying, 1 verified, 2 inapplicable, 3 unresolved
        }

        private readonly object gate = new object();
        private readonly Dictionary<string, Issue> issues = new Dictionary<string, Issue>(StringComparer.Ordinal);
        private readonly int[] counts = new int[4];
        private static readonly string[] outcomes = { "verifying", "verified", "inapplicable", "unresolved" };
        public int Total { get { lock (gate) return issues.Count; } }
        public int Waiting => Count(0);
        public int Verified => Count(1);
        public int Inapplicable => Count(2);
        public int Unresolved => Count(3);
        private int Count(int state) { lock (gate) return counts[state]; }
        private void SetState(Issue issue, int state)
        {
            counts[issue.State]--; issue.State = state; counts[state]++;
        }

        private static string Identity(TranslationRepairRecord r)
        {
            string source = (r.SourceFile ?? "").Replace('\\', '/').ToLowerInvariant();
            string type = (r.DefType ?? "").Split('.').Last();
            int marker = source.IndexOf("/definjected/", StringComparison.Ordinal);
            if (marker >= 0) type = source.Substring(marker + 13).Split('/')[0];
            return source + "\n" + type.ToLowerInvariant() + "\n" + (r.NewKey ?? r.OriginalKey) + "\n" + r.Translation;
        }

        public void Observe(TranslationRepairRecord record)
        {
            if (record == null) return;
            lock (gate)
            {
                string id = Identity(record);
                if (!issues.TryGetValue(id, out var issue))
                {
                    issues[id] = issue = new Issue { Record = record };
                    counts[0]++;
                }
                issue.Conflict |= record.Status == "衝突" || record.Status == "保留候選";
                // Keep the original alias/source for the result and preservation report.
                if (issue.Conflict) SetState(issue, 3);
            }
        }

        public List<TranslationRepairRecord> Snapshot()
        {
            lock (gate) return issues.Values.Select(i => i.Record).ToList();
        }

        public void Verify(TranslationRepairRecord record, bool applied, bool temporary)
        {
            lock (gate)
            {
                if (!issues.TryGetValue(Identity(record), out var issue)) return;
                SetState(issue, issue.Conflict ? 3 : temporary ? 2 : applied ? 1 : 3);
            }
        }

        public void FailWaiting()
        {
            lock (gate) foreach (var issue in issues.Values) if (issue.State == 0) SetState(issue, 3);
        }

        public string Outcome(TranslationRepairRecord record)
        {
            lock (gate)
            {
                if (!issues.TryGetValue(Identity(record), out var issue)) return "unresolved";
                return outcomes[issue.State];
            }
        }
    }
}
