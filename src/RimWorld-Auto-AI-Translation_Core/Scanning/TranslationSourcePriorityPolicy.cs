using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core
{
    public enum TranslationSourceCategory
    {
        UserManual = 0,
        ExternalHuman = 1,
        ModNative = 2,
        Cloud = 3,
        Automatic = 4
    }

    internal static class TranslationSourcePriorityPolicy
    {
        internal const string DefaultOrder =
            "UserManual,ExternalHuman,ModNative,Cloud,Automatic";

        internal static List<TranslationSourceCategory> ParseOrder(string serialized)
        {
            List<TranslationSourceCategory> parsed = new List<TranslationSourceCategory>();
            foreach (string token in (serialized ?? string.Empty).Split(','))
            {
                if (Enum.TryParse(token.Trim(), true, out TranslationSourceCategory category) &&
                    !parsed.Contains(category))
                {
                    parsed.Add(category);
                }
            }

            foreach (TranslationSourceCategory category in Enum.GetValues(typeof(TranslationSourceCategory)))
            {
                if (!parsed.Contains(category)) parsed.Add(category);
            }
            return parsed;
        }

        internal static string SerializeOrder(IEnumerable<TranslationSourceCategory> order)
        {
            return string.Join(",", ParseOrder(string.Join(",", order ?? Enumerable.Empty<TranslationSourceCategory>())));
        }

        internal static int GetRank(
            AutoTranslatorSettings settings,
            string packageId,
            TranslationSourceCategory category)
        {
            string serialized = settings?.GlobalTranslationSourcePriority;
            string modOrder = null;
            if (settings?.ModTranslationSourcePriorityOverrides != null &&
                !string.IsNullOrWhiteSpace(packageId))
            {
                if (!settings.ModTranslationSourcePriorityOverrides.TryGetValue(packageId, out modOrder))
                {
                    modOrder = settings.ModTranslationSourcePriorityOverrides
                        .FirstOrDefault(pair => string.Equals(
                            pair.Key,
                            packageId,
                            StringComparison.OrdinalIgnoreCase)).Value;
                }
            }
            if (!string.IsNullOrWhiteSpace(modOrder))
            {
                serialized = modOrder;
            }
            int index = ParseOrder(serialized).IndexOf(category);
            return index < 0 ? int.MaxValue : index;
        }

        internal static TranslationSourceCategory ClassifyProvenance(string sourceKind)
        {
            string kind = (sourceKind ?? string.Empty).Trim();
            if (string.Equals(kind, AutoTranslatorScanner.ProvenanceKindManualEdit, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, AutoTranslatorScanner.ProvenanceKindUnknownLegacy, StringComparison.OrdinalIgnoreCase))
                return TranslationSourceCategory.UserManual;
            if (string.Equals(kind, AutoTranslatorScanner.ProvenanceKindExternalPatch, StringComparison.OrdinalIgnoreCase))
                return TranslationSourceCategory.ExternalHuman;
            if (string.Equals(kind, AutoTranslatorScanner.ProvenanceKindModNativeTarget, StringComparison.OrdinalIgnoreCase))
                return TranslationSourceCategory.ModNative;
            if (string.Equals(kind, AutoTranslatorScanner.ProvenanceKindCloud, StringComparison.OrdinalIgnoreCase))
                return TranslationSourceCategory.Cloud;
            return TranslationSourceCategory.Automatic;
        }
    }
}
