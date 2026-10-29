using AutoTranslator_Core;

internal static class JournalCheckpointChecks
{
    internal static void Run()
    {
        var method = typeof(TranslationRepairJournal).GetMethod("Checkpoint");
        if (method == null) throw new Exception("FAIL: incremental durable repair checkpoints are missing");
        string root = Path.Combine(Path.GetTempPath(), "atc-checkpoint-" + Guid.NewGuid().ToString("N"));
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); }
        TranslationRepairRecord Record(string text) => new() { SourceFile = "paid.xml", Translation = text, OriginalKey = "Def.label" };
        try
        {
            var journal = new TranslationRepairJournal(root);
            journal.Record(Record("historical paid value")); journal.Flush();
            byte[] baseline = File.ReadAllBytes(Path.Combine(root, "records.json"));
            journal.Record(Record("new paid value")); method.Invoke(journal, null);
            Check(File.ReadAllBytes(Path.Combine(root, "records.json")).SequenceEqual(baseline), "checkpoint must not rewrite the entire historical journal");
            var restart = new TranslationRepairJournal(root);
            Check(restart.Snapshot().Count == 2, "restart recovers both the baseline and a durable checkpoint before compaction");
            restart.Record(Record("newest paid value")); method.Invoke(restart, null);
            restart.Flush();
            Check(new TranslationRepairJournal(root).Snapshot().Count == 3, "compaction retains all paid candidates");
            Check(Directory.GetFiles(Path.Combine(root, "checkpoints"), "*.json").Length == 0, "successful compaction retires included checkpoints");
            restart.Record(Record("failed commit must remain pending"));
            Directory.Move(Path.Combine(root, "checkpoints"), Path.Combine(root, "saved-checkpoints"));
            File.WriteAllText(Path.Combine(root, "checkpoints"), "block checkpoint directory");
            bool failed = false;
            try { method.Invoke(restart, null); } catch (System.Reflection.TargetInvocationException) { failed = true; }
            Check(failed, "checkpoint IO failure must propagate before source mutation");
            File.Delete(Path.Combine(root, "checkpoints"));
            Directory.Move(Path.Combine(root, "saved-checkpoints"), Path.Combine(root, "checkpoints"));
            method.Invoke(restart, null);
            Check(new TranslationRepairJournal(root).Snapshot().Count == 4, "failed checkpoint retains pending data for a durable retry");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: 6 journal checkpoint checks");
    }
}
