using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        internal sealed class CommittedChangeSet
        {
            internal readonly Dictionary<string, HashSet<string>> Files = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            internal long Revision;
        }
        private sealed class CommittedFile
        {
            internal string Package, Language;
            internal readonly Dictionary<string, long> Keys = new Dictionary<string, long>(StringComparer.Ordinal);
        }
        private static readonly object changesGate = new object();
        private static readonly Dictionary<string, CommittedFile> committedFiles = new Dictionary<string, CommittedFile>(StringComparer.OrdinalIgnoreCase);
        private static long committedRevision;
        private static CommittedChangeSet repairChanges;
        [ThreadStatic] private static CommittedChangeSet preparingChanges;
        internal static void RecordCommittedOutput(string package, string language, string file, IDictionary<string, string> before, IDictionary<string, string> after)
        {
            if (!IsOwnedTranslationFile(file)) return;
            var changed = before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Where(k =>
                !before.TryGetValue(k, out string old) || !after.TryGetValue(k, out string value) || old != value).ToArray();
            if (changed.Length == 0) return;
            lock (changesGate)
            {
                file = Path.GetFullPath(file);
                if (!committedFiles.TryGetValue(file, out var entry))
                    committedFiles[file] = entry = new CommittedFile { Package = package, Language = language };
                long revision = ++committedRevision;
                foreach (string key in changed) entry.Keys[key] = revision;
            }
        }
        internal static bool HasCommittedOutput(string package)
        {
            string language = GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
            lock (changesGate) return committedFiles.Values.Any(f => f.Package == package && f.Language == language && f.Keys.Count != 0);
        }
        internal static CommittedChangeSet CaptureCommittedChanges(string[] packages)
        {
            if (packages == null) return null;
            string language = GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
            var result = new CommittedChangeSet();
            lock (changesGate)
            {
                result.Revision = committedRevision;
                foreach (var pair in committedFiles)
                    if (packages.Contains(pair.Value.Package, StringComparer.OrdinalIgnoreCase) && pair.Value.Language == language && pair.Value.Keys.Count != 0)
                        result.Files[pair.Key] = new HashSet<string>(pair.Value.Keys.Keys, StringComparer.Ordinal);
            }
            return result.Files.Count == 0 ? null : result;
        }
        internal static void AcknowledgeCommittedChanges(CommittedChangeSet changes)
        {
            if (changes == null) return;
            lock (changesGate)
                foreach (var file in changes.Files)
                    if (committedFiles.TryGetValue(file.Key, out var current))
                        foreach (string key in file.Value)
                            if (current.Keys.TryGetValue(key, out long revision) && revision <= changes.Revision) current.Keys.Remove(key);
        }
        internal static void SetRepairChangeSet(CommittedChangeSet changes) { repairChanges = changes; }
    }
}
