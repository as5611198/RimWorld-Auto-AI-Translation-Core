using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core
{
    public static class CloudDownloadRecoveryPolicy
    {
        public static bool ShouldRetry(long statusCode)
        {
            return statusCode <= 0 || statusCode == 408 || statusCode == 425 || statusCode == 429 || statusCode >= 500;
        }

        public static bool ShouldRefreshRegistry(long statusCode, bool usedRecordId)
        {
            return usedRecordId && statusCode == 404;
        }

        public static CloudModRecord SelectReplacement(
            IEnumerable<CloudModRecord> records,
            string packageId,
            string language,
            CloudModRecord staleRecord)
        {
            List<CloudModRecord> candidates = (records ?? Enumerable.Empty<CloudModRecord>())
                .Where(record => record != null &&
                                 string.Equals(record.PackageId, packageId, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(record.Language, language, StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(record.RecordId, staleRecord?.RecordId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0) return null;

            IEnumerable<CloudModRecord> sameType = candidates.Where(record =>
                staleRecord != null &&
                string.Equals(record.TranslationType, staleRecord.TranslationType, StringComparison.OrdinalIgnoreCase));
            CloudModRecord replacement = OrderCandidates(sameType).FirstOrDefault();
            return replacement ?? OrderCandidates(candidates).FirstOrDefault();
        }

        private static IOrderedEnumerable<CloudModRecord> OrderCandidates(IEnumerable<CloudModRecord> records)
        {
            return (records ?? Enumerable.Empty<CloudModRecord>())
                .OrderByDescending(record => record.IsVerified || string.Equals(record.TranslationType, "Official_Group", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(record => string.Equals(record.TranslationType, "Manual", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(record => record.LastUpdated != DateTime.MinValue ? record.LastUpdated : record.TranslationDate);
        }
    }
}
