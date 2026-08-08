using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AutoTranslator_Core.TranslationPolicy;

namespace AutoTranslator_Core
{
    internal sealed class TranslationPolicyAgentDecisionCache
    {
        internal sealed class CacheEntry
        {
            public CacheEntry()
            {
                CacheKey = string.Empty;
                GroupKey = string.Empty;
                GroupCorpusFingerprint = string.Empty;
                PackageId = string.Empty;
                Decision = string.Empty;
                Reason = string.Empty;
                PolicyVersion = string.Empty;
                PromptVersion = string.Empty;
                EvaluatorFingerprint = string.Empty;
                CreatedUtc = string.Empty;
                LastUsedUtc = string.Empty;
            }

            public string CacheKey { get; set; }
            public string GroupKey { get; set; }
            public string GroupCorpusFingerprint { get; set; }
            public string PackageId { get; set; }
            public string Decision { get; set; }
            public string Reason { get; set; }
            public string PolicyVersion { get; set; }
            public string PromptVersion { get; set; }
            public string EvaluatorFingerprint { get; set; }
            public string CreatedUtc { get; set; }
            public string LastUsedUtc { get; set; }
        }

        internal sealed class CandidateCacheEntry
        {
            public CandidateCacheEntry()
            {
                CacheKey = string.Empty;
                CandidateId = string.Empty;
                GroupKey = string.Empty;
                PackageId = string.Empty;
                Decision = string.Empty;
                Reason = string.Empty;
                PolicyVersion = string.Empty;
                PromptVersion = string.Empty;
                EvaluatorFingerprint = string.Empty;
                CreatedUtc = string.Empty;
                LastUsedUtc = string.Empty;
            }

            public string CacheKey { get; set; }
            public string CandidateId { get; set; }
            public string GroupKey { get; set; }
            public string PackageId { get; set; }
            public string Decision { get; set; }
            public string Reason { get; set; }
            public string PolicyVersion { get; set; }
            public string PromptVersion { get; set; }
            public string EvaluatorFingerprint { get; set; }
            public string CreatedUtc { get; set; }
            public string LastUsedUtc { get; set; }
        }

        private sealed class CacheFile
        {
            public CacheFile()
            {
                Version = CurrentCacheFileVersion;
                Entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
                CandidateEntries = new Dictionary<string, CandidateCacheEntry>(StringComparer.Ordinal);
            }

            public int Version { get; set; }
            public Dictionary<string, CacheEntry> Entries { get; set; }
            public Dictionary<string, CandidateCacheEntry> CandidateEntries { get; set; }
        }

        private sealed class PruneEntryReference
        {
            public bool IsCandidate;
            public string Key;
            public string LastUsedUtc;
        }

        private const int LegacyCacheFileVersion = 1;
        private const int CurrentCacheFileVersion = 2;
        private const int MaximumGroupEntries = 50000;
        private const int MaximumCandidateEntries = 50000;
        private const long MaximumFileBytes = 32L * 1024L * 1024L;
        private readonly object _gate = new object();
        private readonly string _path;
        private Dictionary<string, CacheEntry> _entries =
            new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private Dictionary<string, CandidateCacheEntry> _candidateEntries =
            new Dictionary<string, CandidateCacheEntry>(StringComparer.Ordinal);
        private bool _loaded;
        private bool _dirty;
        private bool _persistenceDisabled;

        public TranslationPolicyAgentDecisionCache(string path)
        {
            _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        }

        public bool TryGet(string cacheKey, out TranslationPolicyAgentGroupDecision decision)
        {
            decision = null;
            lock (_gate)
            {
                EnsureLoadedLocked();

                CacheEntry entry;
                if (string.IsNullOrWhiteSpace(cacheKey) || !_entries.TryGetValue(cacheKey, out entry))
                    return false;

                TranslationPolicyAgentDecision parsedDecision;
                if (!TryParseDecision(entry.Decision, out parsedDecision) || !IsValidReason(entry.Reason))
                {
                    _entries.Remove(cacheKey);
                    _dirty = true;
                    return false;
                }

                entry.LastUsedUtc = DateTime.UtcNow.ToString("o");
                _dirty = true;
                decision = CreateDecision(entry.GroupKey, parsedDecision, entry.Reason);
                return true;
            }
        }

        public bool TryGetCandidate(
            string cacheKey,
            string candidateId,
            string groupKey,
            out TranslationPolicyAgentGroupDecision decision)
        {
            decision = null;
            lock (_gate)
            {
                EnsureLoadedLocked();

                CandidateCacheEntry entry;
                if (string.IsNullOrWhiteSpace(cacheKey) ||
                    string.IsNullOrWhiteSpace(candidateId) ||
                    string.IsNullOrWhiteSpace(groupKey) ||
                    !_candidateEntries.TryGetValue(cacheKey, out entry))
                {
                    return false;
                }

                if (!string.Equals(entry.CandidateId, candidateId, StringComparison.Ordinal) ||
                    !string.Equals(entry.GroupKey, groupKey, StringComparison.Ordinal))
                {
                    return false;
                }

                TranslationPolicyAgentDecision parsedDecision;
                if (!TryParseDecision(entry.Decision, out parsedDecision) || !IsValidReason(entry.Reason))
                {
                    _candidateEntries.Remove(cacheKey);
                    _dirty = true;
                    return false;
                }

                entry.LastUsedUtc = DateTime.UtcNow.ToString("o");
                _dirty = true;
                decision = CreateDecision(entry.GroupKey, parsedDecision, entry.Reason);
                return true;
            }
        }

        public void PutRange(IEnumerable<CacheEntry> entries)
        {
            PutRange(entries, null);
        }

        public void PutCandidateRange(IEnumerable<CandidateCacheEntry> entries)
        {
            PutRange(null, entries, true);
        }

        public void PutCandidateRangeDeferred(IEnumerable<CandidateCacheEntry> entries)
        {
            PutRange(null, entries, false);
        }

        public void PutRange(
            IEnumerable<CacheEntry> entries,
            IEnumerable<CandidateCacheEntry> candidateEntries)
        {
            PutRange(entries, candidateEntries, true);
        }

        private void PutRange(
            IEnumerable<CacheEntry> entries,
            IEnumerable<CandidateCacheEntry> candidateEntries,
            bool saveImmediately)
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                if (_persistenceDisabled) return;
                string now = DateTime.UtcNow.ToString("o");

                foreach (CacheEntry entry in entries ?? Enumerable.Empty<CacheEntry>())
                {
                    TranslationPolicyAgentDecision parsedDecision;
                    if (entry == null ||
                        !IsValidGroupEntry(entry.CacheKey, entry, out parsedDecision))
                    {
                        continue;
                    }

                    entry.Decision = parsedDecision.ToString();
                    entry.Reason = Truncate(entry.Reason, 240);
                    entry.CreatedUtc = string.IsNullOrWhiteSpace(entry.CreatedUtc) ? now : entry.CreatedUtc;
                    entry.LastUsedUtc = now;
                    _entries[entry.CacheKey] = entry;
                    _dirty = true;
                }

                foreach (CandidateCacheEntry entry in candidateEntries ?? Enumerable.Empty<CandidateCacheEntry>())
                {
                    TranslationPolicyAgentDecision parsedDecision;
                    if (entry == null ||
                        !IsValidCandidateEntry(entry.CacheKey, entry, out parsedDecision))
                    {
                        continue;
                    }

                    entry.Decision = parsedDecision.ToString();
                    entry.Reason = Truncate(entry.Reason, 240);
                    entry.CreatedUtc = string.IsNullOrWhiteSpace(entry.CreatedUtc) ? now : entry.CreatedUtc;
                    entry.LastUsedUtc = now;
                    _candidateEntries[entry.CacheKey] = entry;
                    _dirty = true;
                }

                PruneLocked();
                if (saveImmediately) SaveLocked();
            }
        }

        public void Flush()
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                SaveLocked();
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _candidateEntries.Clear();
                _loaded = true;
                _dirty = false;
                _persistenceDisabled = false;
                if (File.Exists(_path)) File.Delete(_path);
                string temporaryPath = _path + ".tmp";
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private void EnsureLoadedLocked()
        {
            if (_loaded) return;

            try
            {
                RecoverTemporaryFileLocked();
                if (!File.Exists(_path))
                {
                    _loaded = true;
                    return;
                }

                FileInfo info = new FileInfo(_path);
                if (info.Length > MaximumFileBytes)
                    throw new InvalidDataException("Policy Agent cache exceeds its size limit.");

                CacheFile file = JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(_path, Encoding.UTF8));
                if (file == null ||
                    (file.Version != LegacyCacheFileVersion && file.Version != CurrentCacheFileVersion) ||
                    file.Entries == null)
                {
                    _persistenceDisabled = true;
                    _loaded = true;
                    return;
                }

                _entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, CacheEntry> pair in file.Entries)
                {
                    CacheEntry entry = pair.Value;
                    TranslationPolicyAgentDecision parsedDecision;
                    if (entry == null ||
                        !IsValidGroupEntry(pair.Key, entry, out parsedDecision))
                    {
                        continue;
                    }

                    _entries[pair.Key] = entry;
                }

                _candidateEntries = new Dictionary<string, CandidateCacheEntry>(StringComparer.Ordinal);
                if (file.Version == CurrentCacheFileVersion && file.CandidateEntries != null)
                {
                    foreach (KeyValuePair<string, CandidateCacheEntry> pair in file.CandidateEntries)
                    {
                        CandidateCacheEntry entry = pair.Value;
                        TranslationPolicyAgentDecision parsedDecision;
                        if (entry == null ||
                            !IsValidCandidateEntry(pair.Key, entry, out parsedDecision))
                        {
                            continue;
                        }

                        _candidateEntries[pair.Key] = entry;
                    }
                }

                if (file.Version == LegacyCacheFileVersion) _dirty = true;
                PruneLocked();
                _loaded = true;
            }
            catch (UnauthorizedAccessException)
            {
                _loaded = false;
                throw;
            }
            catch (System.Security.SecurityException)
            {
                _loaded = false;
                throw;
            }
            catch (IOException)
            {
                _loaded = false;
                throw;
            }
            catch
            {
                _entries.Clear();
                _candidateEntries.Clear();
                QuarantineBrokenFileLocked();
                _loaded = true;
            }
        }

        private void SaveLocked()
        {
            if (!_dirty || _persistenceDisabled) return;
            PruneLocked();

            CacheFile file = new CacheFile
            {
                Entries = _entries,
                CandidateEntries = _candidateEntries
            };
            string json = JsonConvert.SerializeObject(file, Formatting.None);
            while (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes &&
                   _entries.Count + _candidateEntries.Count > 1)
            {
                RemoveOldestCombinedLocked(Math.Max(1, (_entries.Count + _candidateEntries.Count) / 10));
                file.Entries = _entries;
                file.CandidateEntries = _candidateEntries;
                json = JsonConvert.SerializeObject(file, Formatting.None);
            }

            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            if (File.Exists(_path))
                File.Replace(temporaryPath, _path, null);
            else
                File.Move(temporaryPath, _path);
            _dirty = false;
        }

        private void RecoverTemporaryFileLocked()
        {
            string temporaryPath = _path + ".tmp";
            if (File.Exists(_path) || !File.Exists(temporaryPath)) return;
            File.Move(temporaryPath, _path);
        }

        private void PruneLocked()
        {
            if (_entries.Count > MaximumGroupEntries)
            {
                RemoveOldestGroupsLocked(_entries.Count - MaximumGroupEntries);
                _dirty = true;
            }

            if (_candidateEntries.Count > MaximumCandidateEntries)
            {
                RemoveOldestCandidatesLocked(_candidateEntries.Count - MaximumCandidateEntries);
                _dirty = true;
            }
        }

        private void RemoveOldestGroupsLocked(int count)
        {
            foreach (string key in _entries
                .OrderBy(pair => pair.Value != null ? pair.Value.LastUsedUtc : string.Empty, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(Math.Max(0, count))
                .Select(pair => pair.Key)
                .ToList())
            {
                _entries.Remove(key);
            }
        }

        private void RemoveOldestCandidatesLocked(int count)
        {
            foreach (string key in _candidateEntries
                .OrderBy(pair => pair.Value != null ? pair.Value.LastUsedUtc : string.Empty, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(Math.Max(0, count))
                .Select(pair => pair.Key)
                .ToList())
            {
                _candidateEntries.Remove(key);
            }
        }

        private void RemoveOldestCombinedLocked(int count)
        {
            IEnumerable<PruneEntryReference> groupEntries = _entries.Select(pair => new PruneEntryReference
            {
                IsCandidate = false,
                Key = pair.Key,
                LastUsedUtc = pair.Value != null ? pair.Value.LastUsedUtc : string.Empty
            });
            IEnumerable<PruneEntryReference> candidateEntries = _candidateEntries.Select(pair => new PruneEntryReference
            {
                IsCandidate = true,
                Key = pair.Key,
                LastUsedUtc = pair.Value != null ? pair.Value.LastUsedUtc : string.Empty
            });

            foreach (PruneEntryReference entry in groupEntries
                .Concat(candidateEntries)
                .OrderBy(item => item.LastUsedUtc, StringComparer.Ordinal)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Take(Math.Max(0, count))
                .ToList())
            {
                if (entry.IsCandidate) _candidateEntries.Remove(entry.Key);
                else _entries.Remove(entry.Key);
            }
        }

        private void QuarantineBrokenFileLocked()
        {
            try
            {
                if (!File.Exists(_path)) return;
                string backupPath = _path + ".broken-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".bak";
                File.Move(_path, backupPath);
            }
            catch
            {
            }
        }

        private static TranslationPolicyAgentGroupDecision CreateDecision(
            string groupKey,
            TranslationPolicyAgentDecision decision,
            string reason)
        {
            return new TranslationPolicyAgentGroupDecision
            {
                Id = groupKey ?? string.Empty,
                Decision = decision,
                Reason = reason ?? string.Empty
            };
        }

        private static bool IsValidGroupEntry(
            string expectedCacheKey,
            CacheEntry entry,
            out TranslationPolicyAgentDecision parsedDecision)
        {
            parsedDecision = TranslationPolicyAgentDecision.Unresolved;
            if (entry == null ||
                string.IsNullOrWhiteSpace(expectedCacheKey) ||
                !string.Equals(expectedCacheKey, entry.CacheKey, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(entry.GroupKey) ||
                string.IsNullOrWhiteSpace(entry.GroupCorpusFingerprint) ||
                string.IsNullOrWhiteSpace(entry.PolicyVersion) ||
                string.IsNullOrWhiteSpace(entry.PromptVersion) ||
                string.IsNullOrWhiteSpace(entry.EvaluatorFingerprint) ||
                !TryParseDecision(entry.Decision, out parsedDecision) ||
                !IsValidReason(entry.Reason))
            {
                return false;
            }

            string computedCacheKey = TranslationPolicyIdentity.CreateAgentCacheKey(
                entry.PolicyVersion,
                entry.PromptVersion,
                entry.EvaluatorFingerprint,
                entry.GroupKey,
                entry.GroupCorpusFingerprint);
            return string.Equals(expectedCacheKey, computedCacheKey, StringComparison.Ordinal);
        }

        private static bool IsValidCandidateEntry(
            string expectedCacheKey,
            CandidateCacheEntry entry,
            out TranslationPolicyAgentDecision parsedDecision)
        {
            parsedDecision = TranslationPolicyAgentDecision.Unresolved;
            if (entry == null ||
                string.IsNullOrWhiteSpace(expectedCacheKey) ||
                !string.Equals(expectedCacheKey, entry.CacheKey, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(entry.CandidateId) ||
                string.IsNullOrWhiteSpace(entry.GroupKey) ||
                string.IsNullOrWhiteSpace(entry.PolicyVersion) ||
                string.IsNullOrWhiteSpace(entry.PromptVersion) ||
                string.IsNullOrWhiteSpace(entry.EvaluatorFingerprint) ||
                !TryParseDecision(entry.Decision, out parsedDecision) ||
                !IsValidReason(entry.Reason))
            {
                return false;
            }

            string computedCacheKey = TranslationPolicyIdentity.CreateAgentCandidateCacheKey(
                entry.PolicyVersion,
                entry.PromptVersion,
                entry.EvaluatorFingerprint,
                entry.GroupKey,
                entry.CandidateId);
            return string.Equals(expectedCacheKey, computedCacheKey, StringComparison.Ordinal);
        }

        private static bool TryParseDecision(string value, out TranslationPolicyAgentDecision decision)
        {
            if (Enum.TryParse(value ?? string.Empty, true, out decision) &&
                (decision == TranslationPolicyAgentDecision.Allow ||
                 decision == TranslationPolicyAgentDecision.Deny ||
                 decision == TranslationPolicyAgentDecision.Review))
            {
                return true;
            }

            decision = TranslationPolicyAgentDecision.Unresolved;
            return false;
        }

        private static string Truncate(string value, int maximumLength)
        {
            string safe = value ?? string.Empty;
            return safe.Length <= maximumLength ? safe : safe.Substring(0, maximumLength);
        }

        private static bool IsValidReason(string value)
        {
            string safe = value ?? string.Empty;
            return safe.Trim().Length > 0 && safe.Length <= 240;
        }
    }
}
