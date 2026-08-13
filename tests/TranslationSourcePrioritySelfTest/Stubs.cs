using System;
using System.Collections.Generic;

namespace AutoTranslator_Core
{
    internal sealed class AutoTranslatorSettings
    {
        public string GlobalTranslationSourcePriority = TranslationSourcePriorityPolicy.DefaultOrder;
        public Dictionary<string, string> ModTranslationSourcePriorityOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static class AutoTranslatorScanner
    {
        internal const string ProvenanceKindManualEdit = "ManualEdit";
        internal const string ProvenanceKindUnknownLegacy = "UnknownLegacy";
        internal const string ProvenanceKindExternalPatch = "ExternalPatch";
        internal const string ProvenanceKindModNativeTarget = "ModNativeTarget";
        internal const string ProvenanceKindCloud = "Cloud";
    }
}
