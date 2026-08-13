using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoTranslator_Core
{
    internal enum TranslationUsageRunStatus
    {
        Active,
        PausedByBudget,
        Completed
    }

    internal enum TranslationUsageReservationStatus
    {
        InFlight,
        Committed,
        AmbiguousInFlight
    }

    internal sealed class TranslationUsageReservation
    {
        public string RequestId { get; set; }
        public string PackageId { get; set; }
        public string Purpose { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public long SourceCharacters { get; set; }
        public long EstimatedTokens { get; set; }
        public long ActualInputTokens { get; set; }
        public long ActualOutputTokens { get; set; }
        public long ActualReasoningTokens { get; set; }
        public long ActualTotalTokens { get; set; }
        public bool HasActualUsage { get; set; }
        public TranslationUsageReservationStatus Status { get; set; }
        public string CreatedUtc { get; set; }
        public string UpdatedUtc { get; set; }
    }

    internal sealed class TranslationUsageRunState
    {
        public int Version { get; set; }
        public string RunKey { get; set; }
        public TranslationUsageRunStatus Status { get; set; }
        public long MaximumSourceCharacters { get; set; }
        public long MaximumEstimatedTokens { get; set; }
        public string CreatedUtc { get; set; }
        public string UpdatedUtc { get; set; }
        public Dictionary<string, TranslationUsageReservation> Requests { get; set; }
    }

    internal sealed class TranslationUsageSnapshot
    {
        public TranslationUsageRunStatus Status { get; set; }
        public long CommittedSourceCharacters { get; set; }
        public long ReservedSourceCharacters { get; set; }
        public long AccountedTokens { get; set; }
        public long ReservedEstimatedTokens { get; set; }
        public int CommittedRequests { get; set; }
        public int InFlightRequests { get; set; }
        public int AmbiguousRequests { get; set; }
    }

    /// <summary>
    /// Deterministic, provider-independent usage journal. It reserves budget before a
    /// request and atomically persists every state transition, so a stopped task can
    /// continue without forgetting already charged work.
    /// </summary>
    internal sealed class TranslationUsageLedger
    {
        private const int CurrentVersion = 1;
        private readonly object _gate = new object();
        private readonly string _path;
        private TranslationUsageRunState _state;

        private TranslationUsageLedger(string path, TranslationUsageRunState state, bool wasResumed)
        {
            _path = Path.GetFullPath(path);
            _state = state;
            WasResumed = wasResumed;
        }

        internal bool WasResumed { get; }

        internal static TranslationUsageLedger OpenOrCreate(
            string path,
            string runKey,
            long maximumSourceCharacters,
            long maximumEstimatedTokens)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A journal path is required.", nameof(path));
            if (string.IsNullOrWhiteSpace(runKey)) throw new ArgumentException("A run key is required.", nameof(runKey));

            TranslationUsageRunState state = TryLoad(path);
            bool canResume = state != null &&
                             state.Version == CurrentVersion &&
                             string.Equals(state.RunKey, runKey, StringComparison.Ordinal) &&
                             state.Status != TranslationUsageRunStatus.Completed;
            if (!canResume)
            {
                string now = DateTime.UtcNow.ToString("o");
                state = new TranslationUsageRunState
                {
                    Version = CurrentVersion,
                    RunKey = runKey,
                    Status = TranslationUsageRunStatus.Active,
                    MaximumSourceCharacters = Math.Max(0L, maximumSourceCharacters),
                    MaximumEstimatedTokens = Math.Max(0L, maximumEstimatedTokens),
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    Requests = new Dictionary<string, TranslationUsageReservation>(StringComparer.Ordinal)
                };
            }
            else
            {
                state.Requests = state.Requests ?? new Dictionary<string, TranslationUsageReservation>(StringComparer.Ordinal);
                state.Requests = new Dictionary<string, TranslationUsageReservation>(state.Requests, StringComparer.Ordinal);
                foreach (TranslationUsageReservation request in state.Requests.Values)
                {
                    if (request != null && request.Status == TranslationUsageReservationStatus.InFlight)
                        request.Status = TranslationUsageReservationStatus.AmbiguousInFlight;
                }
                state.MaximumSourceCharacters = Math.Max(0L, maximumSourceCharacters);
                state.MaximumEstimatedTokens = Math.Max(0L, maximumEstimatedTokens);
                state.Status = TranslationUsageRunStatus.Active;
            }

            TranslationUsageLedger ledger = new TranslationUsageLedger(path, state, canResume);
            ledger.SaveLocked();
            return ledger;
        }

        internal bool TryReserve(
            string requestId,
            string packageId,
            string purpose,
            string provider,
            string model,
            long sourceCharacters,
            long estimatedTokens,
            out string denialReason)
        {
            denialReason = string.Empty;
            if (string.IsNullOrWhiteSpace(requestId))
                throw new ArgumentException("A stable request id is required.", nameof(requestId));

            lock (_gate)
            {
                // A budget stop is latched for the remainder of this process run.
                // Concurrent chunks already waiting on a semaphore must not revive
                // the ledger after another chunk reached the limit. Reopening the
                // same journal is the explicit resume boundary.
                if (_state.Status == TranslationUsageRunStatus.PausedByBudget)
                {
                    denialReason = "run_paused_by_budget";
                    return false;
                }

                if (_state.Requests.TryGetValue(requestId, out TranslationUsageReservation existing))
                {
                    denialReason = existing.Status == TranslationUsageReservationStatus.Committed
                        ? "already_committed"
                        : "ambiguous_or_in_flight";
                    return false;
                }

                TranslationUsageSnapshot snapshot = CreateSnapshotLocked();
                long safeCharacters = Math.Max(0L, sourceCharacters);
                long safeTokens = Math.Max(0L, estimatedTokens);
                if (WouldExceed(snapshot.CommittedSourceCharacters, snapshot.ReservedSourceCharacters, safeCharacters, _state.MaximumSourceCharacters))
                {
                    _state.Status = TranslationUsageRunStatus.PausedByBudget;
                    denialReason = "source_character_budget";
                    SaveLocked();
                    return false;
                }
                if (WouldExceed(snapshot.AccountedTokens, snapshot.ReservedEstimatedTokens, safeTokens, _state.MaximumEstimatedTokens))
                {
                    _state.Status = TranslationUsageRunStatus.PausedByBudget;
                    denialReason = "estimated_token_budget";
                    SaveLocked();
                    return false;
                }

                string now = DateTime.UtcNow.ToString("o");
                _state.Requests.Add(requestId, new TranslationUsageReservation
                {
                    RequestId = requestId,
                    PackageId = packageId ?? string.Empty,
                    Purpose = purpose ?? string.Empty,
                    Provider = provider ?? string.Empty,
                    Model = model ?? string.Empty,
                    SourceCharacters = safeCharacters,
                    EstimatedTokens = safeTokens,
                    Status = TranslationUsageReservationStatus.InFlight,
                    CreatedUtc = now,
                    UpdatedUtc = now
                });
                _state.Status = TranslationUsageRunStatus.Active;
                SaveLocked();
                return true;
            }
        }

        internal void Commit(
            string requestId,
            long? actualInputTokens,
            long? actualOutputTokens,
            long? actualReasoningTokens,
            long? actualTotalTokens)
        {
            lock (_gate)
            {
                if (!_state.Requests.TryGetValue(requestId, out TranslationUsageReservation request))
                    throw new InvalidOperationException("Unknown usage reservation: " + requestId);

                request.ActualInputTokens = Math.Max(0L, actualInputTokens ?? 0L);
                request.ActualOutputTokens = Math.Max(0L, actualOutputTokens ?? 0L);
                request.ActualReasoningTokens = Math.Max(0L, actualReasoningTokens ?? 0L);
                request.ActualTotalTokens = Math.Max(0L, actualTotalTokens ?? 0L);
                request.HasActualUsage = actualTotalTokens.HasValue || actualInputTokens.HasValue || actualOutputTokens.HasValue;
                request.Status = TranslationUsageReservationStatus.Committed;
                request.UpdatedUtc = DateTime.UtcNow.ToString("o");
                TranslationUsageSnapshot snapshot = CreateSnapshotLocked();
                if ((_state.MaximumSourceCharacters > 0L &&
                     snapshot.CommittedSourceCharacters >= _state.MaximumSourceCharacters) ||
                    (_state.MaximumEstimatedTokens > 0L &&
                     snapshot.AccountedTokens >= _state.MaximumEstimatedTokens))
                {
                    _state.Status = TranslationUsageRunStatus.PausedByBudget;
                }
                SaveLocked();
            }
        }

        internal void MarkAmbiguous(string requestId)
        {
            lock (_gate)
            {
                if (!_state.Requests.TryGetValue(requestId, out TranslationUsageReservation request)) return;
                if (request.Status == TranslationUsageReservationStatus.Committed) return;
                request.Status = TranslationUsageReservationStatus.AmbiguousInFlight;
                request.UpdatedUtc = DateTime.UtcNow.ToString("o");
                SaveLocked();
            }
        }

        internal void Release(string requestId)
        {
            lock (_gate)
            {
                if (!_state.Requests.TryGetValue(requestId, out TranslationUsageReservation request)) return;
                if (request.Status == TranslationUsageReservationStatus.Committed) return;
                _state.Requests.Remove(requestId);
                SaveLocked();
            }
        }

        internal void Complete()
        {
            lock (_gate)
            {
                _state.Status = TranslationUsageRunStatus.Completed;
                SaveLocked();
            }
        }

        internal TranslationUsageSnapshot GetSnapshot()
        {
            lock (_gate) return CreateSnapshotLocked();
        }

        private TranslationUsageSnapshot CreateSnapshotLocked()
        {
            TranslationUsageSnapshot snapshot = new TranslationUsageSnapshot { Status = _state.Status };
            foreach (TranslationUsageReservation request in _state.Requests.Values.Where(value => value != null))
            {
                if (request.Status == TranslationUsageReservationStatus.Committed)
                {
                    snapshot.CommittedRequests++;
                    snapshot.CommittedSourceCharacters = SaturatingAdd(snapshot.CommittedSourceCharacters, request.SourceCharacters);
                    snapshot.AccountedTokens = SaturatingAdd(
                        snapshot.AccountedTokens,
                        request.HasActualUsage && request.ActualTotalTokens > 0L
                            ? request.ActualTotalTokens
                            : request.EstimatedTokens);
                }
                else
                {
                    snapshot.ReservedSourceCharacters = SaturatingAdd(snapshot.ReservedSourceCharacters, request.SourceCharacters);
                    snapshot.ReservedEstimatedTokens = SaturatingAdd(snapshot.ReservedEstimatedTokens, request.EstimatedTokens);
                    if (request.Status == TranslationUsageReservationStatus.AmbiguousInFlight) snapshot.AmbiguousRequests++;
                    else snapshot.InFlightRequests++;
                }
            }
            return snapshot;
        }

        private void SaveLocked()
        {
            _state.UpdatedUtc = DateTime.UtcNow.ToString("o");
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporaryPath = _path + ".tmp";
            string backupPath = _path + ".bak";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(_state, Formatting.Indented));
            if (File.Exists(_path))
            {
                try
                {
                    File.Replace(temporaryPath, _path, backupPath, true);
                    return;
                }
                catch (PlatformNotSupportedException) { }
            }
            File.Copy(temporaryPath, _path, true);
            File.Delete(temporaryPath);
        }

        private static TranslationUsageRunState TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<TranslationUsageRunState>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static bool WouldExceed(long committed, long reserved, long requested, long maximum)
        {
            if (maximum <= 0L) return requested > 0L;
            long used = SaturatingAdd(committed, reserved);
            return requested > maximum - Math.Min(maximum, used);
        }

        private static long SaturatingAdd(long left, long right)
        {
            left = Math.Max(0L, left);
            right = Math.Max(0L, right);
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }
    }
}
