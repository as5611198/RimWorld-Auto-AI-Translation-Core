using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core
{
    internal static class TranslationTaskTierRouter
    {
        internal static List<ApiKeyConfig> SelectEligible(
            IEnumerable<ApiKeyConfig> configs,
            TranslationTaskTier requestedTier)
        {
            List<ApiKeyConfig> safe = (configs ?? Enumerable.Empty<ApiKeyConfig>())
                .Where(config => config != null)
                .ToList();
            TranslationTaskTier[] fallbackOrder;
            switch (requestedTier)
            {
                case TranslationTaskTier.Precision:
                    fallbackOrder = new[]
                    {
                        TranslationTaskTier.Precision,
                        TranslationTaskTier.Standard,
                        TranslationTaskTier.Bulk
                    };
                    break;
                case TranslationTaskTier.Standard:
                    fallbackOrder = new[]
                    {
                        TranslationTaskTier.Standard,
                        TranslationTaskTier.Precision,
                        TranslationTaskTier.Bulk
                    };
                    break;
                default:
                    fallbackOrder = new[] { TranslationTaskTier.Bulk };
                    break;
            }

            foreach (TranslationTaskTier tier in fallbackOrder)
            {
                List<ApiKeyConfig> matches = safe
                    .Where(config => config.TaskTier == tier)
                    .ToList();
                if (matches.Count > 0) return matches;
            }
            return new List<ApiKeyConfig>();
        }
    }
}
