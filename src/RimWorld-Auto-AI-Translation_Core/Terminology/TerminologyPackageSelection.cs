using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core.Terminology
{
    internal static class TerminologyPackageSelection
    {
        internal static bool IsSelected(
            bool featureEnabled,
            IEnumerable<string> selectedPackageIds,
            string packageId)
        {
            if (!featureEnabled || string.IsNullOrWhiteSpace(packageId)) return false;
            string normalized = packageId.Trim();
            return (selectedPackageIds ?? Enumerable.Empty<string>()).Any(
                selected => !string.IsNullOrWhiteSpace(selected) &&
                    string.Equals(selected.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
        }

        internal static List<T> FilterSelected<T>(
            bool featureEnabled,
            IEnumerable<string> selectedPackageIds,
            IEnumerable<T> values,
            Func<T, string> packageIdSelector)
        {
            if (!featureEnabled || packageIdSelector == null) return new List<T>();
            var selected = new HashSet<string>(
                (selectedPackageIds ?? Enumerable.Empty<string>())
                    .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
                    .Select(packageId => packageId.Trim()),
                StringComparer.OrdinalIgnoreCase);
            if (selected.Count == 0) return new List<T>();

            return (values ?? Enumerable.Empty<T>())
                .Where(value => value != null)
                .Where(value =>
                {
                    string packageId = packageIdSelector(value);
                    return !string.IsNullOrWhiteSpace(packageId) && selected.Contains(packageId.Trim());
                })
                .ToList();
        }
    }
}
