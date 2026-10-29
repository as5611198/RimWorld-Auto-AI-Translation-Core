using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace AutoTranslator_Core
{
    // Optimistic multi-file transaction. Backups are retained even after success.
    // Rollback never replaces a newer writer's content; recovery material stays on disk.
    internal sealed class TranslationFileTransaction : IDisposable
    {
        [ThreadStatic] internal static TranslationFileTransaction Current;
        private TranslationFileTransaction previous;
        internal TranslationFileTransaction CaptureWrites()
        {
            if (Current != null) throw new InvalidOperationException("Nested translation transactions are not supported.");
            previous = Current; Current = this; return this;
        }
        public void Dispose() { if (ReferenceEquals(Current, this)) Current = previous; }
        internal bool TryRead(string path, out byte[] bytes)
        {
            var entry = entries.FirstOrDefault(e => string.Equals(e.Path, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            bytes = entry?.After; return entry != null;
        }
        private sealed class Entry
        {
            public string Path, Backup, BeforeHash, AfterHash;
            public byte[] After;
            public bool Applied;
            public Func<bool> CanCommit;
        }
        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<string, string> observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal void Observe(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            if (!observed.ContainsKey(path)) observed.Add(path, FileHash(path));
        }
        private readonly string root;
        internal TranslationFileTransaction(string backupRoot)
        {
            root = System.IO.Path.Combine(backupRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }
        internal static string Hash(byte[] bytes)
        {
            if (bytes == null) return "missing";
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
        internal static string FileHash(string path) => File.Exists(path) ? Hash(File.ReadAllBytes(path)) : "missing";
        internal void Stage(string path, byte[] content, Func<bool> canCommit = null)
        {
            path = System.IO.Path.GetFullPath(path);
            var existing = entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.After = content; existing.AfterHash = Hash(content);
                var priorGuard = existing.CanCommit;
                existing.CanCommit = () => (priorGuard == null || priorGuard()) && (canCommit == null || canCommit());
                return;
            }
            RejectReparsePoints(path);
            byte[] before = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var entry = new Entry { Path = path, BeforeHash = Hash(before), After = content, AfterHash = Hash(content),
                CanCommit = canCommit, Backup = System.IO.Path.Combine(root, entries.Count.ToString("D6") + ".bak") };
            if (before != null)
            {
                DurableWrite(entry.Backup, before);
                if (FileHash(entry.Backup) != entry.BeforeHash) throw new IOException("Backup verification failed.");
            }
            if (observed.TryGetValue(path, out string expected) && expected != entry.BeforeHash)
                throw new IOException("Translation source changed during staging.");
            entries.Add(entry);
        }
        internal void Commit(Func<bool> canCommit)
        {
            Dispose(); // physical commit must not recursively stage its own writes
            // Recovery map is durable before the first destructive operation.
            DurableWrite(System.IO.Path.Combine(root, "transaction.json"), System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(entries.Select(e =>
                new { e.Path, e.Backup, e.BeforeHash, e.AfterHash }), Formatting.Indented)));
            try
            {
                if (!canCommit()) throw new OperationCanceledException();
                if (observed.Any(p => FileHash(p.Key) != p.Value)) throw new IOException("Translation source changed before commit.");
                foreach (var entry in entries)
                {
                    RejectReparsePoints(entry.Path);
                    Func<bool> current = () => canCommit() && (entry.CanCommit == null || entry.CanCommit()) && FileHash(entry.Path) == entry.BeforeHash;
                    if (entry.After == null) TranslationXmlAtomicFileStore.DeleteIfCurrent(entry.Path, current);
                    else TranslationXmlAtomicFileStore.SaveIfCurrent(entry.Path,
                        stream => stream.Write(entry.After, 0, entry.After.Length), current);
                    entry.Applied = true;
                }
                File.WriteAllText(System.IO.Path.Combine(root, "committed"), DateTime.UtcNow.ToString("O"));
            }
            catch
            {
                var failures = new List<Exception>();
                foreach (var entry in entries.Where(e => e.Applied).Reverse())
                {
                    try
                    {
                        Func<bool> unchanged = () => FileHash(entry.Path) == entry.AfterHash;
                        if (entry.BeforeHash == "missing") TranslationXmlAtomicFileStore.DeleteIfCurrent(entry.Path, unchanged);
                        else
                        {
                            byte[] original = File.ReadAllBytes(entry.Backup);
                            if (Hash(original) != entry.BeforeHash) throw new IOException("Recovery backup checksum mismatch.");
                            TranslationXmlAtomicFileStore.SaveIfCurrent(entry.Path, s => s.Write(original, 0, original.Length), unchanged);
                        }
                    }
                    catch (Exception ex) { failures.Add(ex); }
                }
                if (failures.Count != 0) throw new IOException("Rollback requires recovery from " + root, new AggregateException(failures));
                throw;
            }
        }
        private static void DurableWrite(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }

        internal static void RejectReparsePoints(string path)
        {
            for (string current = System.IO.Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Translation destination crosses a reparse point: " + current);
        }
    }
}
