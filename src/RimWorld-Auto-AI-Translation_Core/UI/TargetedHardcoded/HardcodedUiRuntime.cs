using System.Collections.Generic;
using System.Threading;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    // This resolver is deliberately smaller than UIInterceptor. It only reads an
    // immutable in-memory snapshot and therefore cannot enqueue work or call AI.
    public static class HardcodedUiRuntime
    {
        private sealed class RuntimeSnapshot
        {
            public readonly Dictionary<string, string> Translations;
            public readonly Dictionary<string, string> SourceTexts;

            public RuntimeSnapshot(
                Dictionary<string, string> translations,
                Dictionary<string, string> sourceTexts)
            {
                Translations = translations ?? new Dictionary<string, string>(System.StringComparer.Ordinal);
                SourceTexts = sourceTexts ?? new Dictionary<string, string>(System.StringComparer.Ordinal);
            }
        }

        private static readonly RuntimeSnapshot EmptySnapshot = new RuntimeSnapshot(
            new Dictionary<string, string>(System.StringComparer.Ordinal),
            new Dictionary<string, string>(System.StringComparer.Ordinal));
        private static RuntimeSnapshot _snapshot = EmptySnapshot;
        private static long _resolveCount;

        public static long ResolveCount
        {
            get { return Interlocked.Read(ref _resolveCount); }
        }

        public static string Resolve(string source, string entryId)
        {
            Interlocked.Increment(ref _resolveCount);
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(entryId)) return source;

            RuntimeSnapshot snapshot = Volatile.Read(ref _snapshot);
            string translated;
            string expectedSource;
            if (snapshot != null && snapshot.Translations.TryGetValue(entryId, out translated) &&
                (!snapshot.SourceTexts.TryGetValue(entryId, out expectedSource) ||
                 string.Equals(source, expectedSource, System.StringComparison.Ordinal)) &&
                !string.IsNullOrWhiteSpace(translated) &&
                !string.Equals(source, translated, System.StringComparison.Ordinal))
            {
                return translated;
            }

            return source;
        }

        internal static void ReplaceSnapshot(Dictionary<string, string> translations)
        {
            ReplaceSnapshot(translations, null);
        }

        internal static void ReplaceSnapshot(
            Dictionary<string, string> translations,
            Dictionary<string, string> sourceTexts)
        {
            Dictionary<string, string> next = new Dictionary<string, string>(System.StringComparer.Ordinal);
            Dictionary<string, string> nextSources = new Dictionary<string, string>(System.StringComparer.Ordinal);
            if (translations != null)
            {
                foreach (KeyValuePair<string, string> pair in translations)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                    next[pair.Key] = pair.Value;
                    string source;
                    if (sourceTexts != null && sourceTexts.TryGetValue(pair.Key, out source) &&
                        !string.IsNullOrEmpty(source))
                    {
                        nextSources[pair.Key] = source;
                    }
                }
            }
            Interlocked.Exchange(ref _snapshot, new RuntimeSnapshot(next, nextSources));
        }

        internal static void ClearSnapshot()
        {
            Interlocked.Exchange(ref _snapshot, EmptySnapshot);
        }
    }
}
