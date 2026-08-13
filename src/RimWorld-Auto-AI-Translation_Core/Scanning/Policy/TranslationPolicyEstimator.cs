using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core.TranslationPolicy
{
    public static class TranslationPolicyEstimator
    {
        public static TranslationPolicyTokenEstimate Estimate(
            IEnumerable<TranslationPolicyGroup> groups,
            TranslationPolicyShadowOptions options)
        {
            List<TranslationPolicyGroup> materialized = (groups ?? Enumerable.Empty<TranslationPolicyGroup>()).ToList();
            return Estimate(materialized, options, materialized.Count);
        }

        public static TranslationPolicyTokenEstimate Estimate(
            IEnumerable<TranslationPolicyGroup> reportedGroups,
            TranslationPolicyShadowOptions options,
            int totalAmbiguousGroupCount)
        {
            List<TranslationPolicyGroup> orderedGroups = (reportedGroups ?? Enumerable.Empty<TranslationPolicyGroup>())
                .OrderBy(group => group.GroupKey, StringComparer.Ordinal)
                .ToList();
            int totalGroupCount = Math.Max(orderedGroups.Count, Math.Max(0, totalAmbiguousGroupCount));
            TranslationPolicyShadowOptions safeOptions = options ?? new TranslationPolicyShadowOptions();
            int groupsPerRequest = Clamp(safeOptions.GroupsPerRequest, 1, 20);
            int concurrency = Clamp(safeOptions.MaxConcurrency, 1, 64);
            int promptTokens = Math.Max(0, safeOptions.PromptTokenEstimate);
            double charactersPerToken = safeOptions.CharactersPerToken;
            if (double.IsNaN(charactersPerToken) || double.IsInfinity(charactersPerToken) || charactersPerToken <= 0d)
            {
                charactersPerToken = 3.0d;
            }

            int outputTokensPerGroup = Math.Max(0, safeOptions.OutputTokensPerGroup);
            int retries = Clamp(safeOptions.MaxRetriesPerRequest, 0, 3);
            int latencyPerRequest = Math.Max(0, safeOptions.EstimatedMillisecondsPerRequest);
            int requestCount = CeilingDivide(totalGroupCount, groupsPerRequest);
            long reportedGroupCharacters = 0L;
            for (int i = 0; i < orderedGroups.Count; i++)
            {
                reportedGroupCharacters = SaturatingAdd(
                    reportedGroupCharacters,
                    EstimateGroupPayloadCharacters(orderedGroups[i]));
            }

            long estimatedGroupCharacters = 0L;
            if (orderedGroups.Count > 0 && totalGroupCount > 0)
            {
                double average = reportedGroupCharacters / (double)orderedGroups.Count;
                double scaled = Math.Ceiling(average * totalGroupCount);
                estimatedGroupCharacters = scaled >= long.MaxValue ? long.MaxValue : (long)scaled;
            }

            long payloadCharacters = SaturatingAdd(
                estimatedGroupCharacters,
                SaturatingMultiply(requestCount, 32L));
            long inputTokens = SaturatingAdd(
                SaturatingMultiply(requestCount, promptTokens),
                CeilingTokenEstimate(payloadCharacters, charactersPerToken));
            long outputTokens = SaturatingMultiply(totalGroupCount, outputTokensPerGroup);
            long totalTokens = SaturatingAdd(inputTokens, outputTokens);
            int maximumRequestCount = ClampToInt(SaturatingMultiply(requestCount, retries + 1));
            long maximumTotalTokens = SaturatingMultiply(totalTokens, retries + 1);
            int requestWaves = CeilingDivide(requestCount, concurrency);
            int maximumRequestWaves = CeilingDivide(maximumRequestCount, concurrency);

            return new TranslationPolicyTokenEstimate
            {
                AmbiguousGroupCount = totalGroupCount,
                ReportedAmbiguousGroupCount = orderedGroups.Count,
                GroupsTruncated = orderedGroups.Count < totalGroupCount,
                PayloadEstimateUsesReportedSample = orderedGroups.Count < totalGroupCount,
                GroupsPerRequest = groupsPerRequest,
                EstimatedRequestCount = requestCount,
                EstimatedRequestWaves = requestWaves,
                EstimatedPayloadCharacters = payloadCharacters,
                EstimatedInputTokens = inputTokens,
                EstimatedOutputTokens = outputTokens,
                EstimatedTotalTokens = totalTokens,
                EstimatedMaximumRequestCount = maximumRequestCount,
                EstimatedMaximumTotalTokens = maximumTotalTokens,
                EstimatedLatencyMilliseconds = SaturatingMultiply(requestWaves, latencyPerRequest),
                EstimatedMaximumLatencyMilliseconds = SaturatingMultiply(maximumRequestWaves, latencyPerRequest)
            };
        }

        private static long EstimateGroupPayloadCharacters(TranslationPolicyGroup group)
        {
            if (group == null) return 0L;
            long total = 96L;
            total = SaturatingAdd(total, Length(group.GroupKey));
            total = SaturatingAdd(total, Length(group.PackageId));
            total = SaturatingAdd(total, Length(group.DeclaringAssembly));
            total = SaturatingAdd(total, Length(group.SchemaFingerprint));
            total = SaturatingAdd(total, Length(group.DefType));
            total = SaturatingAdd(total, Length(group.NormalizedPath));
            total = SaturatingAdd(total, Length(group.FieldName));

            if (group.Samples == null) return total;
            foreach (TranslationPolicyGroupSample sample in group.Samples)
            {
                if (sample == null) continue;
                total = SaturatingAdd(total, 64L);
                total = SaturatingAdd(total, Length(sample.CandidateId));
                total = SaturatingAdd(total, Length(sample.SourceFile));
                total = SaturatingAdd(total, Length(sample.KeyOrPath));
                total = SaturatingAdd(total, Length(sample.SourceText));
            }

            return total;
        }

        private static long CeilingTokenEstimate(long characters, double charactersPerToken)
        {
            if (characters <= 0L) return 0L;
            double estimated = Math.Ceiling(characters / charactersPerToken);
            if (estimated >= long.MaxValue) return long.MaxValue;
            return (long)estimated;
        }

        private static int CeilingDivide(int value, int divisor)
        {
            if (value <= 0) return 0;
            return 1 + ((value - 1) / divisor);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Min(maximum, Math.Max(minimum, value));
        }

        private static int ClampToInt(long value)
        {
            if (value >= int.MaxValue) return int.MaxValue;
            if (value <= 0L) return 0;
            return (int)value;
        }

        private static int Length(string value)
        {
            return value == null ? 0 : value.Length;
        }

        private static long SaturatingAdd(long left, long right)
        {
            if (right > 0L && left > long.MaxValue - right) return long.MaxValue;
            return left + right;
        }

        private static long SaturatingMultiply(long left, long right)
        {
            if (left <= 0L || right <= 0L) return 0L;
            if (left > long.MaxValue / right) return long.MaxValue;
            return left * right;
        }
    }
}
