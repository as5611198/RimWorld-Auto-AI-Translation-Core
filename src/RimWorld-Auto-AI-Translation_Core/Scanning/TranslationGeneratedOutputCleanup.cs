using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core
{
    internal static class TranslationGeneratedOutputCleanup
    {
        internal static List<string> FindStaleAggregateKeys(
            IEnumerable<string> generatedKeys,
            IEnumerable<string> currentSourceKeys)
        {
            HashSet<string> sourceKeys = new HashSet<string>(
                currentSourceKeys ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> sourceAncestorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string sourceKey in sourceKeys)
            {
                if (string.IsNullOrWhiteSpace(sourceKey)) continue;

                int separator = sourceKey.IndexOf('.');
                while (separator > 0)
                {
                    sourceAncestorPaths.Add(sourceKey.Substring(0, separator));
                    separator = sourceKey.IndexOf('.', separator + 1);
                }
            }

            return (generatedKeys ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key) &&
                              !sourceKeys.Contains(key) &&
                              sourceAncestorPaths.Contains(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
