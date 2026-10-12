using System;
using System.Collections.Generic;

namespace AutoTranslator_Core
{
    internal sealed class TranslationErrorAggregateSnapshot
    {
        internal string Key = string.Empty;
        internal int Occurrences;
        internal long AffectedItems;
        internal bool IsFirstOccurrence;
    }

    internal sealed class TranslationErrorAggregationTracker
    {
        private sealed class State
        {
            internal int Occurrences;
            internal long AffectedItems;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, State> _states =
            new Dictionary<string, State>(StringComparer.Ordinal);

        internal TranslationErrorAggregateSnapshot Record(string key, int affectedItems)
        {
            string normalizedKey = string.IsNullOrWhiteSpace(key) ? "unknown" : key.Trim();
            lock (_gate)
            {
                if (!_states.TryGetValue(normalizedKey, out State state))
                {
                    state = new State();
                    _states[normalizedKey] = state;
                }

                state.Occurrences++;
                state.AffectedItems = SaturatingAdd(
                    state.AffectedItems,
                    Math.Max(0, affectedItems));
                return new TranslationErrorAggregateSnapshot
                {
                    Key = normalizedKey,
                    Occurrences = state.Occurrences,
                    AffectedItems = state.AffectedItems,
                    IsFirstOccurrence = state.Occurrences == 1
                };
            }
        }

        internal void Reset()
        {
            lock (_gate) _states.Clear();
        }

        private static long SaturatingAdd(long left, long right)
        {
            if (right <= 0L) return left;
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }
    }
}
