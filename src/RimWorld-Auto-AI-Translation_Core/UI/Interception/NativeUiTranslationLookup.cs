using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Verse;

namespace AutoTranslator_Core
{
    // Display-only reuse of existing Keyed translations. This also covers text
    // retained in readonly/static fields and instance objects without changing
    // those fields, invoking constructors, or requesting another AI translation.
    internal static class NativeUiTranslationLookup
    {
        private const int RowsPerPump = 1024;
        private sealed class Row
        {
            internal string Key, Source, Target;
            internal string[] Aliases;
        }
        private sealed class Snapshot
        {
            internal LoadedLanguage Language, Fallback;
            internal Dictionary<string, LoadedLanguage.KeyedReplacement> ActiveEntries, SourceEntries;
            internal int Generation, ActiveCount, SourceCount;
            internal Dictionary<string, Row> Rows = new Dictionary<string, Row>(StringComparer.Ordinal);
            internal Dictionary<string, string> Lookup;
            internal HashSet<string> Ambiguous;
            internal Dictionary<string, List<NativeUiTranslationTemplate>> Templates;
            internal readonly Dictionary<string, string> Wrapped = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        private sealed class Capture
        {
            internal Snapshot Data;
            internal IEnumerator<KeyValuePair<string, LoadedLanguage.KeyedReplacement>> Entries;
            internal bool ActivePass;
        }
        private static int generation;
        private static Capture capture;
        private static Task<Snapshot> building;
        private static Snapshot published, history;
        [ThreadStatic] private static bool pendingMatch;
        private static int templateFrame = int.MinValue;
        private static long templateDeadline;
        internal static bool PendingMatch => pendingMatch;

        internal static void Invalidate()
        {
            generation++;
            capture?.Entries.Dispose(); capture = null;
            building = null; published = null;
            // Keep the previous immutable strings for language switches/hot
            // corrections. A different English language instance drops history.
        }

        private static bool Available => LanguageDatabase.activeLanguage?.keyedReplacements != null &&
            LanguageDatabase.defaultLanguage?.keyedReplacements != null;

        private static bool Current(Snapshot snapshot) => snapshot != null && snapshot.Generation == generation &&
            ReferenceEquals(snapshot.Language, LanguageDatabase.activeLanguage) &&
            ReferenceEquals(snapshot.Fallback, LanguageDatabase.defaultLanguage) &&
            ReferenceEquals(snapshot.ActiveEntries, snapshot.Language.keyedReplacements) &&
            ReferenceEquals(snapshot.SourceEntries, snapshot.Fallback.keyedReplacements) &&
            snapshot.ActiveCount == snapshot.Language.keyedReplacements.Count &&
            snapshot.SourceCount == snapshot.Fallback.keyedReplacements.Count;

        internal static bool Preparing => Available && !Current(published);

        // Called on the game thread. The worker only reads immutable string
        // rows; it never enumerates Verse's mutable dictionaries.
        internal static void Pump()
        {
            if (!Available || AutoTranslatorMod.Settings == null || !AutoTranslatorMod.Settings.EnableUIInterceptor) return;
            if (Current(published)) return;
            if (building != null)
            {
                if (!building.IsCompleted) return;
                var completed = building; building = null;
                if (completed.IsFaulted)
                    Log.Warning("[ATC] Existing UI translation lookup: " + completed.Exception.GetBaseException().Message);
                else if (Current(completed.Result))
                {
                    published = history = completed.Result;
                    return;
                }
            }
            if (capture != null && !Current(capture.Data))
            { capture.Entries.Dispose(); capture = null; }
            if (capture == null)
            {
                if (history != null && !ReferenceEquals(history.Fallback, LanguageDatabase.defaultLanguage)) history = null;
                var data = new Snapshot { Language = LanguageDatabase.activeLanguage, Fallback = LanguageDatabase.defaultLanguage,
                    ActiveEntries = LanguageDatabase.activeLanguage.keyedReplacements, SourceEntries = LanguageDatabase.defaultLanguage.keyedReplacements,
                    Generation = generation, ActiveCount = LanguageDatabase.activeLanguage.keyedReplacements.Count,
                    SourceCount = LanguageDatabase.defaultLanguage.keyedReplacements.Count };
                capture = new Capture { Data = data, Entries = data.Fallback.keyedReplacements.GetEnumerator() };
            }
            try
            {
                int processed = 0;
                while (processed++ < RowsPerPump && TranslationFrameBudget.HasTime)
                {
                    if (!capture.Entries.MoveNext())
                    {
                        capture.Entries.Dispose();
                        if (!capture.ActivePass)
                        {
                            capture.ActivePass = true;
                            capture.Entries = capture.Data.Language.keyedReplacements.GetEnumerator();
                            continue;
                        }
                        var data = capture.Data; capture = null;
                        building = Task.Run(() => Build(data));
                        return;
                    }
                    var entry = capture.Entries.Current;
                    if (string.IsNullOrEmpty(entry.Key) || capture.Data.Rows.ContainsKey(entry.Key)) continue;
                    if (entry.Value == null || entry.Value.isPlaceholder) continue;
                    string source = capture.ActivePass ? entry.Key : entry.Value?.value;
                    if (string.IsNullOrEmpty(source)) continue;
                    string target = capture.Data.Language.keyedReplacements.TryGetValue(entry.Key, out var translated) && translated != null && !translated.isPlaceholder && !string.IsNullOrEmpty(translated.value)
                        ? translated.value : source;
                    var aliases = new List<string>();
                    if (history != null && history.Rows.TryGetValue(entry.Key, out var previous) && previous.Source == source)
                    {
                        if (previous.Aliases != null) aliases.AddRange(previous.Aliases);
                        if (previous.Target != source && previous.Target != target && !aliases.Contains(previous.Target)) aliases.Add(previous.Target);
                        if (aliases.Count > 8) aliases.RemoveRange(0, aliases.Count - 8);
                    }
                    capture.Data.Rows[entry.Key] = new Row { Key = entry.Key, Source = source, Target = target,
                        Aliases = aliases.Count == 0 ? null : aliases.ToArray() };
                }
            }
            catch (InvalidOperationException)
            {
                // A native language reload invalidated an enumerator. Restart
                // next pump rather than publish a partially mixed snapshot.
                capture?.Entries.Dispose(); capture = null;
            }
        }

        private static Snapshot Build(Snapshot data)
        {
            var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
            var templates = new Dictionary<string, List<NativeUiTranslationTemplate>>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            Action<string, string> add = (source, target) => {
                if (string.IsNullOrEmpty(source) || UIDynamicNumberTemplate.IsNumericDisplayText(source) || ambiguous.Contains(source)) return;
                if (lookup.TryGetValue(source, out string existing) && existing != target)
                { lookup.Remove(source); ambiguous.Add(source); }
                else lookup[source] = target;
            };
            foreach (Row row in data.Rows.Values)
            {
                // Dedicated localization keys may intentionally contain number
                // words (RimHonkai's 3 -> 三, 7 -> 七). They must never become
                // a context-free replacement for unrelated stats or counters.
                if (UIDynamicNumberTemplate.IsNumericDisplayText(row.Source)) continue;
                if (!NativeUiTranslationTemplate.CompatiblePlaceholders(row.Source, row.Target))
                {
                    add(row.Source, row.Source);
                    continue;
                }
                add(row.Source, row.Target);
                AddTemplate(templates, row.Source, row.Target);
                if (row.Aliases != null) foreach (string alias in row.Aliases)
                { add(alias, row.Target); AddTemplate(templates, alias, row.Target); }
            }
            // Keep unchanged rows during ambiguity detection. Otherwise a key
            // without a translation could accidentally borrow a homonym's text.
            var unchanged = new List<string>();
            foreach (var pair in lookup) if (pair.Key == pair.Value) unchanged.Add(pair.Key);
            foreach (string source in unchanged) lookup.Remove(source);
            data.Lookup = lookup;
            data.Ambiguous = ambiguous;
            data.Templates = templates;
            return data;
        }

        internal static bool TryResolve(string original, out string translated)
        {
            pendingMatch = false;
            translated = null;
            var data = published;
            if (string.IsNullOrEmpty(original) || UIDynamicNumberTemplate.IsNumericDisplayText(original) || !Available || !Current(data)) return false;
            if (data.Ambiguous.Contains(original)) return false;
            if (data.Lookup.TryGetValue(original, out translated)) return true;
            if (data.Wrapped.TryGetValue(original, out translated)) return translated != null;
            if (TryTemplate(data, original, out translated)) return Cache(data, original, translated);
            // Preserve only balanced outer presentation tags. Never replace a
            // substring inside a larger message, identifier, or user text.
            string inner = original, prefix = "", suffix = "";
            for (int depth = 0; depth < 4 && inner.StartsWith("<", StringComparison.Ordinal); depth++)
            {
                int end = inner.IndexOf('>');
                if (end < 0) break;
                string opening = inner.Substring(0, end + 1), closing;
                if (opening == "<b>") closing = "</b>";
                else if (opening == "<i>") closing = "</i>";
                else if (opening.StartsWith("<color=", StringComparison.Ordinal)) closing = "</color>";
                else if (opening.StartsWith("<size=", StringComparison.Ordinal)) closing = "</size>";
                else break;
                if (!inner.EndsWith(closing, StringComparison.Ordinal) || inner.Length < opening.Length + closing.Length) break;
                prefix += opening; suffix = closing + suffix;
                inner = inner.Substring(opening.Length, inner.Length - opening.Length - closing.Length);
                if (data.Ambiguous.Contains(inner)) return false;
                if (data.Lookup.TryGetValue(inner, out string target) || TryTemplate(data, inner, out target))
                {
                    translated = prefix + target + suffix;
                    return Cache(data, original, translated);
                }
            }
            if (!pendingMatch) Cache(data, original, null);
            return false;
        }

        private static bool Cache(Snapshot data, string original, string translated)
        {
            if (data.Wrapped.Count >= 8192) data.Wrapped.Clear();
            data.Wrapped[original] = translated;
            return translated != null;
        }

        private static void AddTemplate(Dictionary<string, List<NativeUiTranslationTemplate>> templates, string source, string target)
        {
            var template = NativeUiTranslationTemplate.Create(source, target);
            if (template == null) return;
            if (!templates.TryGetValue(template.Bucket, out var bucket)) templates[template.Bucket] = bucket = new List<NativeUiTranslationTemplate>();
            bucket.Add(template);
        }

        private static bool TryTemplate(Snapshot data, string original, out string translated)
        {
            translated = null;
            if (original.Length > 4096 || data.Templates.Count == 0) return false;
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
            foreach (int length in new[] { 12, 8, 4 })
            {
                if (original.Length < length) continue;
                foreach (string key in new[] { "P" + length + ":" + original.Substring(0, length), "S" + length + ":" + original.Substring(original.Length - length) })
                {
                    if (!data.Templates.TryGetValue(key, out var bucket)) continue;
                    int frame = TranslationFrameBudget.CurrentFrame;
                    if (templateFrame != frame)
                    {
                        templateFrame = frame;
                        templateDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2 / 1000;
                    }
                    foreach (var template in bucket)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (now > deadline || now > templateDeadline) { pendingMatch = true; translated = null; return false; }
                        try
                        {
                            if (!template.TryMatch(original, out string candidate)) continue;
                            if (translated != null && translated != candidate) { translated = null; return false; }
                            translated = candidate;
                        }
                        catch (RegexMatchTimeoutException) { pendingMatch = true; translated = null; return false; }
                    }
                }
            }
            return translated != null && translated != original;
        }
    }
}
