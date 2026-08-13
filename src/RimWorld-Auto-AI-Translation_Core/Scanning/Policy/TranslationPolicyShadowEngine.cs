using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AutoTranslator_Core.TranslationPolicy
{
    public static class TranslationPolicyShadowEngine
    {
        public static TranslationPolicyShadowResult Run(TranslationPolicyShadowInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            TranslationPolicyShadowSession session = new TranslationPolicyShadowSession(input.Options);
            session.AddCandidates(input.Candidates);
            return session.Complete();
        }
    }

    public sealed class TranslationPolicyShadowSession
    {
        private sealed class MutableGroup
        {
            private readonly int _maxSamples;
            private readonly SortedDictionary<string, TranslationPolicyGroupSample> _samples =
                new SortedDictionary<string, TranslationPolicyGroupSample>(StringComparer.Ordinal);

            public MutableGroup(
                string groupKey,
                TranslationPolicyCandidate candidate,
                string normalizedPath,
                int maxSamples)
            {
                _maxSamples = maxSamples;
                Group = new TranslationPolicyGroup
                {
                    GroupKey = groupKey,
                    Bucket = candidate.Bucket,
                    PackageId = TranslationPolicyIdentity.NormalizeIdentityPart(candidate.PackageId),
                    DeclaringAssembly = TranslationPolicyIdentity.NormalizeIdentityPart(candidate.DeclaringAssembly),
                    SchemaFingerprint = TranslationPolicyIdentity.NormalizeIdentityPart(candidate.SchemaFingerprint),
                    DefType = TranslationPolicyIdentity.NormalizeIdentityPart(candidate.DefType),
                    NormalizedPath = normalizedPath,
                    FieldName = TranslationPolicyIdentity.NormalizeIdentityPart(candidate.FieldName)
                };
            }

            public TranslationPolicyGroup Group { get; private set; }

            public void Add(TranslationPolicyCandidateResult candidate)
            {
                Group.CandidateCount++;
                if (_maxSamples <= 0 || _samples.ContainsKey(candidate.CandidateId)) return;

                TranslationPolicyGroupSample sample = new TranslationPolicyGroupSample
                {
                    CandidateId = candidate.CandidateId,
                    SourceFile = candidate.SourceFile,
                    KeyOrPath = candidate.KeyOrPath,
                    SourceText = candidate.SourceText
                };

                if (_samples.Count < _maxSamples)
                {
                    _samples.Add(candidate.CandidateId, sample);
                    return;
                }

                string largestKey = _samples.Keys.Last();
                if (string.CompareOrdinal(candidate.CandidateId, largestKey) < 0)
                {
                    _samples.Remove(largestKey);
                    _samples.Add(candidate.CandidateId, sample);
                }
            }

            public TranslationPolicyGroup Freeze()
            {
                Group.Samples = _samples.Values.ToList();
                return Group;
            }
        }

        private sealed class MutableModSummary
        {
            public string PackageId;
            public TranslationPolicyBucket Bucket;
            public int Total;
            public int HardAllow;
            public int HardDeny;
            public int Ambiguous;

            public TranslationPolicyModSummary Freeze()
            {
                return new TranslationPolicyModSummary
                {
                    PackageId = PackageId,
                    Bucket = Bucket,
                    TotalCandidates = Total,
                    HardAllowCount = HardAllow,
                    HardDenyCount = HardDeny,
                    AmbiguousCount = Ambiguous
                };
            }
        }

        private readonly object _gate = new object();
        private readonly TranslationPolicyShadowOptions _options;
        private readonly SortedDictionary<string, int> _ruleCounts =
            new SortedDictionary<string, int>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, MutableModSummary> _modSummaries =
            new SortedDictionary<string, MutableModSummary>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, MutableGroup> _groups =
            new SortedDictionary<string, MutableGroup>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _groupCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, TranslationPolicyCandidateResult> _diagnosticSamples =
            new SortedDictionary<string, TranslationPolicyCandidateResult>(StringComparer.Ordinal);
        private int _totalCandidates;
        private int _hardAllowCount;
        private int _hardDenyCount;
        private int _ambiguousCount;
        private ulong _corpusSum0;
        private ulong _corpusSum1;
        private ulong _corpusSum2;
        private ulong _corpusSum3;
        private ulong _corpusXor0;
        private ulong _corpusXor1;
        private ulong _corpusXor2;
        private ulong _corpusXor3;
        private TranslationPolicyShadowResult _completedResult;

        public TranslationPolicyShadowSession()
            : this(null)
        {
        }

        public TranslationPolicyShadowSession(TranslationPolicyShadowOptions options)
        {
            _options = NormalizeOptions(options ?? new TranslationPolicyShadowOptions());
        }

        public void AddCandidates(IEnumerable<TranslationPolicyCandidate> candidates)
        {
            if (candidates == null) return;
            foreach (TranslationPolicyCandidate candidate in candidates)
            {
                AddCandidate(candidate);
            }
        }

        public void AddCandidate(TranslationPolicyCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            TranslationPolicyClassification classification = TranslationPolicyClassifier.Classify(candidate);
            string normalizedPath = TranslationPolicyGrouping.NormalizeForGrouping(candidate);
            string groupKey = classification.Decision == TranslationPolicyDecision.Ambiguous
                ? TranslationPolicyGrouping.CreateGroupKey(candidate)
                : string.Empty;
            TranslationPolicyCandidateResult candidateResult = CreateCandidateResult(
                candidate,
                classification,
                normalizedPath,
                groupKey);

            lock (_gate)
            {
                if (_completedResult != null)
                {
                    throw new InvalidOperationException("The translation policy session is already complete.");
                }

                if (_totalCandidates >= _options.MaxCandidates)
                {
                    throw new InvalidOperationException(
                        "Translation policy candidate limit exceeded: " +
                        _options.MaxCandidates.ToString(CultureInfo.InvariantCulture));
                }

                int existingGroupCount = 0;
                bool isNewGroup = classification.Decision == TranslationPolicyDecision.Ambiguous &&
                                  !_groupCounts.TryGetValue(groupKey, out existingGroupCount);
                if (isNewGroup && _groupCounts.Count >= _options.MaxAmbiguousGroups)
                {
                    throw new InvalidOperationException(
                        "Translation policy ambiguous-group limit exceeded: " +
                        _options.MaxAmbiguousGroups.ToString(CultureInfo.InvariantCulture));
                }

                _totalCandidates++;
                AccumulateCorpusFingerprint(candidateResult);
                IncrementDecision(classification.Decision);
                IncrementRuleCount(classification);
                IncrementModSummary(candidate, classification.Decision);
                AddDiagnosticSample(candidateResult);

                if (classification.Decision == TranslationPolicyDecision.Ambiguous)
                {
                    _groupCounts[groupKey] = isNewGroup ? 1 : existingGroupCount + 1;
                    MutableGroup group;
                    if (!_groups.TryGetValue(groupKey, out group) && isNewGroup)
                    {
                        if (_groups.Count < _options.MaxReportedAmbiguousGroups)
                        {
                            group = new MutableGroup(groupKey, candidate, normalizedPath, _options.MaxSamplesPerGroup);
                            _groups.Add(groupKey, group);
                        }
                        else
                        {
                            string largestKey = _groups.Keys.Last();
                            if (string.CompareOrdinal(groupKey, largestKey) < 0)
                            {
                                _groups.Remove(largestKey);
                                group = new MutableGroup(groupKey, candidate, normalizedPath, _options.MaxSamplesPerGroup);
                                _groups.Add(groupKey, group);
                            }
                        }
                    }

                    if (group != null) group.Add(candidateResult);
                }
            }
        }

        public TranslationPolicyShadowResult Complete()
        {
            lock (_gate)
            {
                if (_completedResult != null) return _completedResult;

                TranslationPolicyShadowResult result = new TranslationPolicyShadowResult
                {
                    CorpusFingerprint = CreateCorpusFingerprint(),
                    AppliedOptions = CloneOptions(_options),
                    Summary = new TranslationPolicySummary
                    {
                        TotalCandidates = _totalCandidates,
                        HardAllowCount = _hardAllowCount,
                        HardDenyCount = _hardDenyCount,
                        AmbiguousCount = _ambiguousCount,
                        AmbiguousGroupCount = _groupCounts.Count,
                        ReportedAmbiguousGroupCount = _groups.Count,
                        GroupsTruncated = _groups.Count < _groupCounts.Count
                    },
                    RuleCounts = _ruleCounts
                        .Select(pair => new TranslationPolicyCount { Key = pair.Key, Count = pair.Value })
                        .ToList(),
                    ModSummaries = _modSummaries.Values.Select(summary => summary.Freeze()).ToList(),
                    DiagnosticSamples = _diagnosticSamples.Values.ToList(),
                    AmbiguousGroups = _groups.Values.Select(group => group.Freeze()).ToList()
                };
                result.DistinctGroupFingerprint = CreateGroupUniverseFingerprint(_groupCounts);
                result.Estimate = TranslationPolicyEstimator.Estimate(
                    result.AmbiguousGroups,
                    _options,
                    _groupCounts.Count);
                result.DeterministicFingerprint = CreateResultFingerprint(result);
                _completedResult = result;
                return result;
            }
        }

        private void IncrementDecision(TranslationPolicyDecision decision)
        {
            switch (decision)
            {
                case TranslationPolicyDecision.HardAllow:
                    _hardAllowCount++;
                    break;
                case TranslationPolicyDecision.HardDeny:
                    _hardDenyCount++;
                    break;
                default:
                    _ambiguousCount++;
                    break;
            }
        }

        private void AccumulateCorpusFingerprint(TranslationPolicyCandidateResult candidate)
        {
            string leaf = TranslationPolicyIdentity.ComputeSha256(
                TranslationPolicyIdentity.JoinCanonical(
                    candidate.CandidateId,
                    ((int)candidate.Decision).ToString(CultureInfo.InvariantCulture),
                    candidate.ReasonCode));

            ulong lane0 = ParseHashLane(leaf, 0);
            ulong lane1 = ParseHashLane(leaf, 16);
            ulong lane2 = ParseHashLane(leaf, 32);
            ulong lane3 = ParseHashLane(leaf, 48);
            unchecked
            {
                _corpusSum0 += lane0;
                _corpusSum1 += lane1;
                _corpusSum2 += lane2;
                _corpusSum3 += lane3;
            }
            _corpusXor0 ^= lane0;
            _corpusXor1 ^= lane1;
            _corpusXor2 ^= lane2;
            _corpusXor3 ^= lane3;
        }

        private string CreateCorpusFingerprint()
        {
            string canonical = TranslationPolicyIdentity.JoinCanonical(
                _totalCandidates.ToString(CultureInfo.InvariantCulture),
                _corpusSum0.ToString("x16", CultureInfo.InvariantCulture),
                _corpusSum1.ToString("x16", CultureInfo.InvariantCulture),
                _corpusSum2.ToString("x16", CultureInfo.InvariantCulture),
                _corpusSum3.ToString("x16", CultureInfo.InvariantCulture),
                _corpusXor0.ToString("x16", CultureInfo.InvariantCulture),
                _corpusXor1.ToString("x16", CultureInfo.InvariantCulture),
                _corpusXor2.ToString("x16", CultureInfo.InvariantCulture),
                _corpusXor3.ToString("x16", CultureInfo.InvariantCulture));
            return "tpcorpus_" + TranslationPolicyIdentity.ComputeSha256(canonical);
        }

        private static ulong ParseHashLane(string hash, int offset)
        {
            ulong value;
            if (hash == null || hash.Length < offset + 16 ||
                !ulong.TryParse(
                    hash.Substring(offset, 16),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                throw new InvalidDataException("Invalid translation policy hash lane.");
            }

            return value;
        }

        private void IncrementRuleCount(TranslationPolicyClassification classification)
        {
            string key = classification.Decision + "|" + classification.ReasonCode;
            int count;
            _ruleCounts.TryGetValue(key, out count);
            _ruleCounts[key] = count + 1;
        }

        private void IncrementModSummary(
            TranslationPolicyCandidate candidate,
            TranslationPolicyDecision decision)
        {
            string packageId = string.IsNullOrWhiteSpace(candidate.PackageId)
                ? "<unknown>"
                : TranslationPolicyIdentity.NormalizeIdentityPart(candidate.PackageId);
            string key = TranslationPolicyIdentity.JoinCanonical(
                packageId.ToLowerInvariant(),
                ((int)candidate.Bucket).ToString(CultureInfo.InvariantCulture));
            MutableModSummary summary;
            if (!_modSummaries.TryGetValue(key, out summary))
            {
                summary = new MutableModSummary { PackageId = packageId, Bucket = candidate.Bucket };
                _modSummaries.Add(key, summary);
            }

            summary.Total++;
            switch (decision)
            {
                case TranslationPolicyDecision.HardAllow:
                    summary.HardAllow++;
                    break;
                case TranslationPolicyDecision.HardDeny:
                    summary.HardDeny++;
                    break;
                default:
                    summary.Ambiguous++;
                    break;
            }
        }

        private void AddDiagnosticSample(TranslationPolicyCandidateResult candidate)
        {
            int maximum = _options.MaxDiagnosticSamples;
            if (maximum <= 0 || _diagnosticSamples.ContainsKey(candidate.CandidateId)) return;

            if (_diagnosticSamples.Count < maximum)
            {
                _diagnosticSamples.Add(candidate.CandidateId, candidate);
                return;
            }

            string largestKey = _diagnosticSamples.Keys.Last();
            if (string.CompareOrdinal(candidate.CandidateId, largestKey) < 0)
            {
                _diagnosticSamples.Remove(largestKey);
                _diagnosticSamples.Add(candidate.CandidateId, candidate);
            }
        }

        private static TranslationPolicyCandidateResult CreateCandidateResult(
            TranslationPolicyCandidate candidate,
            TranslationPolicyClassification classification,
            string normalizedPath,
            string groupKey)
        {
            return new TranslationPolicyCandidateResult
            {
                CandidateId = classification.CandidateId,
                PackageId = TranslationPolicyIdentity.NormalizeIdentityPart(candidate.PackageId),
                ModName = candidate.ModName ?? string.Empty,
                SourceFile = TranslationPolicyIdentity.NormalizePathPart(candidate.SourceFile),
                Bucket = candidate.Bucket,
                DefType = TranslationPolicyIdentity.NormalizeIdentityPart(candidate.DefType),
                KeyOrPath = TranslationPolicyIdentity.NormalizeIdentityPart(candidate.KeyOrPath),
                NormalizedPath = normalizedPath,
                FieldName = TranslationPolicyIdentity.NormalizeIdentityPart(candidate.FieldName),
                SourceText = candidate.SourceText ?? string.Empty,
                Decision = classification.Decision,
                ReasonCode = classification.ReasonCode,
                GroupKey = groupKey
            };
        }

        private static TranslationPolicyShadowOptions NormalizeOptions(TranslationPolicyShadowOptions options)
        {
            double charactersPerToken = options.CharactersPerToken;
            if (double.IsNaN(charactersPerToken) || double.IsInfinity(charactersPerToken) || charactersPerToken <= 0d)
            {
                charactersPerToken = 3.0d;
            }

            int trackedGroupLimit = Clamp(options.MaxAmbiguousGroups, 1, 500000);
            return new TranslationPolicyShadowOptions
            {
                MaxSamplesPerGroup = Clamp(options.MaxSamplesPerGroup, 1, 5),
                GroupsPerRequest = Clamp(options.GroupsPerRequest, 1, 20),
                MaxConcurrency = Clamp(options.MaxConcurrency, 1, 64),
                PromptTokenEstimate = Math.Max(0, options.PromptTokenEstimate),
                CharactersPerToken = charactersPerToken,
                OutputTokensPerGroup = Math.Max(0, options.OutputTokensPerGroup),
                MaxRetriesPerRequest = Clamp(options.MaxRetriesPerRequest, 0, 3),
                EstimatedMillisecondsPerRequest = Math.Max(0, options.EstimatedMillisecondsPerRequest),
                MaxCandidates = Clamp(options.MaxCandidates, 1, 10000000),
                MaxAmbiguousGroups = trackedGroupLimit,
                MaxReportedAmbiguousGroups = Math.Min(
                    trackedGroupLimit,
                    Clamp(options.MaxReportedAmbiguousGroups, 1, 25000)),
                MaxDiagnosticSamples = Clamp(options.MaxDiagnosticSamples, 0, 1000)
            };
        }

        private static TranslationPolicyShadowOptions CloneOptions(TranslationPolicyShadowOptions options)
        {
            return new TranslationPolicyShadowOptions
            {
                MaxSamplesPerGroup = options.MaxSamplesPerGroup,
                GroupsPerRequest = options.GroupsPerRequest,
                MaxConcurrency = options.MaxConcurrency,
                PromptTokenEstimate = options.PromptTokenEstimate,
                CharactersPerToken = options.CharactersPerToken,
                OutputTokensPerGroup = options.OutputTokensPerGroup,
                MaxRetriesPerRequest = options.MaxRetriesPerRequest,
                EstimatedMillisecondsPerRequest = options.EstimatedMillisecondsPerRequest,
                MaxCandidates = options.MaxCandidates,
                MaxAmbiguousGroups = options.MaxAmbiguousGroups,
                MaxReportedAmbiguousGroups = options.MaxReportedAmbiguousGroups,
                MaxDiagnosticSamples = options.MaxDiagnosticSamples
            };
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Min(maximum, Math.Max(minimum, value));
        }

        private static string CreateResultFingerprint(TranslationPolicyShadowResult result)
        {
            StringBuilder builder = new StringBuilder();
            Append(builder, result.ResultVersion, result.CorpusFingerprint, result.DistinctGroupFingerprint,
                result.Summary.TotalCandidates, result.Summary.HardAllowCount,
                result.Summary.HardDenyCount, result.Summary.AmbiguousCount, result.Summary.AmbiguousGroupCount,
                result.Summary.ReportedAmbiguousGroupCount, result.Summary.GroupsTruncated);
            Append(builder, result.AppliedOptions.MaxSamplesPerGroup, result.AppliedOptions.GroupsPerRequest,
                result.AppliedOptions.MaxConcurrency, result.AppliedOptions.PromptTokenEstimate,
                result.AppliedOptions.CharactersPerToken.ToString("R", CultureInfo.InvariantCulture),
                result.AppliedOptions.OutputTokensPerGroup, result.AppliedOptions.MaxRetriesPerRequest,
                result.AppliedOptions.EstimatedMillisecondsPerRequest, result.AppliedOptions.MaxCandidates,
                result.AppliedOptions.MaxAmbiguousGroups, result.AppliedOptions.MaxReportedAmbiguousGroups,
                result.AppliedOptions.MaxDiagnosticSamples);

            foreach (TranslationPolicyCount count in result.RuleCounts)
            {
                Append(builder, count.Key, count.Count);
            }

            foreach (TranslationPolicyModSummary summary in result.ModSummaries)
            {
                Append(builder, summary.PackageId, (int)summary.Bucket, summary.TotalCandidates,
                    summary.HardAllowCount, summary.HardDenyCount, summary.AmbiguousCount);
            }

            foreach (TranslationPolicyCandidateResult sample in result.DiagnosticSamples)
            {
                Append(builder, sample.CandidateId, sample.PackageId, sample.SourceFile, (int)sample.Bucket,
                    sample.DefType, sample.KeyOrPath, sample.NormalizedPath, sample.FieldName, sample.SourceText,
                    (int)sample.Decision, sample.ReasonCode, sample.GroupKey);
            }

            foreach (TranslationPolicyGroup group in result.AmbiguousGroups)
            {
                Append(builder, group.GroupKey, (int)group.Bucket, group.PackageId, group.DeclaringAssembly,
                    group.SchemaFingerprint, group.DefType, group.NormalizedPath, group.FieldName,
                    group.CandidateCount);
                foreach (TranslationPolicyGroupSample sample in group.Samples)
                {
                    Append(builder, sample.CandidateId, sample.SourceFile, sample.KeyOrPath, sample.SourceText);
                }
            }

            TranslationPolicyTokenEstimate estimate = result.Estimate;
            Append(builder, estimate.AmbiguousGroupCount, estimate.ReportedAmbiguousGroupCount,
                estimate.PayloadEstimateUsesReportedSample, estimate.GroupsPerRequest, estimate.EstimatedRequestCount,
                estimate.EstimatedRequestWaves, estimate.EstimatedPayloadCharacters, estimate.EstimatedInputTokens,
                estimate.EstimatedOutputTokens, estimate.EstimatedTotalTokens,
                estimate.EstimatedMaximumRequestCount, estimate.EstimatedMaximumTotalTokens,
                estimate.EstimatedLatencyMilliseconds, estimate.EstimatedMaximumLatencyMilliseconds);
            return "tpr_" + TranslationPolicyIdentity.ComputeSha256(builder.ToString());
        }

        private static string CreateGroupUniverseFingerprint(Dictionary<string, int> groupCounts)
        {
            StringBuilder builder = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in groupCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                Append(builder, pair.Key, pair.Value);
            }

            return "tpgs_" + TranslationPolicyIdentity.ComputeSha256(builder.ToString());
        }

        private static void Append(StringBuilder builder, params object[] values)
        {
            string[] strings = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                strings[i] = Convert.ToString(values[i], CultureInfo.InvariantCulture) ?? string.Empty;
            }

            builder.Append(TranslationPolicyIdentity.JoinCanonical(strings));
        }
    }
}
