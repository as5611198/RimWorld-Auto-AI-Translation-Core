using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoTranslator_Core
{
    public sealed class PolicyAnalysisLocalState
    {
        public PolicyAnalysisLocalState()
        {
            CandidateDomain = PolicyAnalysisCandidateDomain.Xml;
            PackageId = string.Empty;
            ModName = string.Empty;
            GameVersion = string.Empty;
            SourceFingerprint = string.Empty;
            PolicyVersion = string.Empty;
            PromptVersion = string.Empty;
            Status = string.Empty;
            UpdatedUtc = string.Empty;
            PendingContributionId = string.Empty;
            PendingAllowedCandidateIds = new List<string>();
        }

        public string CandidateDomain { get; set; }
        public string PackageId { get; set; }
        public string ModName { get; set; }
        public string GameVersion { get; set; }
        public string SourceFingerprint { get; set; }
        public string PolicyVersion { get; set; }
        public string PromptVersion { get; set; }
        public string Status { get; set; }
        public int CandidateCount { get; set; }
        public int CloudAllowedCount { get; set; }
        public string UpdatedUtc { get; set; }
        public string PendingContributionId { get; set; }
        public List<string> PendingAllowedCandidateIds { get; set; }
    }

    public sealed class PolicyAnalysisLocalStateStore
    {
        public const string AcceleratedStatus = "accelerated";
        public const string PendingUploadStatus = "pending_upload";
        public const string UploadedStatus = "uploaded";

        private sealed class StateFile
        {
            public StateFile()
            {
                SchemaVersion = 2;
                Records = new Dictionary<string, PolicyAnalysisLocalState>(StringComparer.OrdinalIgnoreCase);
            }

            public int SchemaVersion { get; set; }
            public Dictionary<string, PolicyAnalysisLocalState> Records { get; set; }
        }

        private readonly object _gate = new object();
        private readonly string _path;
        private StateFile _file;

        public PolicyAnalysisLocalStateStore(string path)
        {
            _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        }

        public PolicyAnalysisLocalState Get(string packageId)
        {
            return Get(packageId, PolicyAnalysisCandidateDomain.Xml);
        }

        public PolicyAnalysisLocalState Get(string packageId, string candidateDomain)
        {
            lock (_gate)
            {
                EnsureLoaded();
                if (string.IsNullOrWhiteSpace(packageId) ||
                    !_file.Records.TryGetValue(
                        BuildKey(candidateDomain, packageId),
                        out PolicyAnalysisLocalState record))
                    return null;
                return Clone(record);
            }
        }

        public void RecordAccelerated(PolicyAnalysisCloudRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.PackageId)) return;
            Put(new PolicyAnalysisLocalState
            {
                CandidateDomain = NormalizeDomain(record.CandidateDomain),
                PackageId = record.PackageId,
                ModName = record.ModName,
                GameVersion = record.GameVersion,
                SourceFingerprint = record.SourceFingerprint,
                PolicyVersion = record.PolicyVersion,
                PromptVersion = record.PromptVersion,
                Status = AcceleratedStatus,
                CandidateCount = record.CandidateCount,
                CloudAllowedCount = (record.AllowedCandidateIds ?? new List<string>()).Distinct(StringComparer.Ordinal).Count(),
                UpdatedUtc = DateTime.UtcNow.ToString("o")
            });
        }

        public void RecordPending(PolicyAnalysisContribution contribution)
        {
            if (contribution == null || string.IsNullOrWhiteSpace(contribution.PackageId)) return;
            Put(new PolicyAnalysisLocalState
            {
                CandidateDomain = NormalizeDomain(contribution.CandidateDomain),
                PackageId = contribution.PackageId,
                ModName = contribution.ModName,
                GameVersion = contribution.GameVersion,
                SourceFingerprint = contribution.SourceFingerprint,
                PolicyVersion = contribution.PolicyVersion,
                PromptVersion = contribution.PromptVersion,
                Status = PendingUploadStatus,
                CandidateCount = contribution.CandidateCount,
                CloudAllowedCount = 0,
                UpdatedUtc = DateTime.UtcNow.ToString("o"),
                PendingContributionId = string.IsNullOrWhiteSpace(contribution.ContributionId)
                    ? Guid.NewGuid().ToString("N")
                    : contribution.ContributionId,
                PendingAllowedCandidateIds = NormalizeIds(contribution.AddAllowedCandidateIds)
            });
        }

        public void MarkUploaded(string packageId)
        {
            MarkUploaded(packageId, PolicyAnalysisCandidateDomain.Xml);
        }

        public void MarkUploaded(string packageId, string candidateDomain)
        {
            lock (_gate)
            {
                EnsureLoaded();
                if (!_file.Records.TryGetValue(
                        BuildKey(candidateDomain, packageId),
                        out PolicyAnalysisLocalState record)) return;
                record.Status = UploadedStatus;
                record.UpdatedUtc = DateTime.UtcNow.ToString("o");
                record.PendingAllowedCandidateIds.Clear();
                record.PendingContributionId = string.Empty;
                Save();
            }
        }

        public void DiscardPending(string packageId)
        {
            DiscardPending(packageId, PolicyAnalysisCandidateDomain.Xml);
        }

        public void DiscardPending(string packageId, string candidateDomain)
        {
            lock (_gate)
            {
                EnsureLoaded();
                if (!_file.Records.TryGetValue(
                        BuildKey(candidateDomain, packageId),
                        out PolicyAnalysisLocalState record)) return;
                record.PendingAllowedCandidateIds.Clear();
                record.PendingContributionId = string.Empty;
                record.Status = string.Empty;
                record.UpdatedUtc = DateTime.UtcNow.ToString("o");
                Save();
            }
        }

        private void Put(PolicyAnalysisLocalState record)
        {
            lock (_gate)
            {
                EnsureLoaded();
                record.CandidateDomain = NormalizeDomain(record.CandidateDomain);
                if (record.CandidateDomain.Length == 0)
                    throw new ArgumentException("Unknown policy-analysis candidate domain.");
                record.PackageId = Normalize(record.PackageId);
                record.PendingAllowedCandidateIds = NormalizeIds(record.PendingAllowedCandidateIds);
                if (!PolicyAnalysisCandidateDomain.AreCandidateIdsValid(
                        record.CandidateDomain,
                        record.PendingAllowedCandidateIds))
                    throw new ArgumentException("Policy-analysis state contains mixed-domain candidate IDs.");
                _file.Records[BuildKey(record.CandidateDomain, record.PackageId)] = record;
                Save();
            }
        }

        private void EnsureLoaded()
        {
            if (_file != null) return;
            try
            {
                _file = File.Exists(_path)
                    ? JsonConvert.DeserializeObject<StateFile>(File.ReadAllText(_path))
                    : null;
            }
            catch
            {
                _file = null;
            }
            if (_file == null || (_file.SchemaVersion != 1 && _file.SchemaVersion != 2))
                _file = new StateFile();
            if (_file.Records == null)
                _file.Records = new Dictionary<string, PolicyAnalysisLocalState>(StringComparer.OrdinalIgnoreCase);
            if (_file.SchemaVersion == 1)
            {
                var migrated = new Dictionary<string, PolicyAnalysisLocalState>(StringComparer.OrdinalIgnoreCase);
                foreach (PolicyAnalysisLocalState record in _file.Records.Values.Where(record => record != null))
                {
                    record.CandidateDomain = PolicyAnalysisCandidateDomain.Xml;
                    record.PackageId = Normalize(record.PackageId);
                    record.PendingAllowedCandidateIds = NormalizeIds(record.PendingAllowedCandidateIds);
                    migrated[BuildKey(record.CandidateDomain, record.PackageId)] = record;
                }
                _file.Records = migrated;
                _file.SchemaVersion = 2;
                Save();
            }
        }

        private void Save()
        {
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(_file, Formatting.Indented));
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
        }

        private static string Normalize(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();

        private static string NormalizeDomain(string value)
        {
            return PolicyAnalysisCandidateDomain.Normalize(value);
        }

        private static string BuildKey(string candidateDomain, string packageId)
        {
            string domain = NormalizeDomain(candidateDomain);
            return domain.Length == 0 ? string.Empty : domain + "|" + Normalize(packageId);
        }

        private static List<string> NormalizeIds(IEnumerable<string> ids)
        {
            return (ids ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        private static PolicyAnalysisLocalState Clone(PolicyAnalysisLocalState source)
        {
            return JsonConvert.DeserializeObject<PolicyAnalysisLocalState>(
                JsonConvert.SerializeObject(source, Formatting.None));
        }
    }

    internal static class PolicyAnalysisLocalStateManager
    {
        private static readonly object Gate = new object();
        private static PolicyAnalysisLocalStateStore _store;

        public static PolicyAnalysisLocalState Get(string packageId)
        {
            try { return Store.Get(packageId); }
            catch { return null; }
        }

        public static PolicyAnalysisLocalState Get(string packageId, string candidateDomain)
        {
            try { return Store.Get(packageId, candidateDomain); }
            catch { return null; }
        }

        public static void RecordAccelerated(PolicyAnalysisCloudRecord record)
        {
            try { Store.RecordAccelerated(record); } catch { }
        }

        public static void RecordPending(PolicyAnalysisContribution contribution)
        {
            try { Store.RecordPending(contribution); } catch { }
        }

        public static void MarkUploaded(string packageId)
        {
            try { Store.MarkUploaded(packageId); } catch { }
        }

        public static void MarkUploaded(string packageId, string candidateDomain)
        {
            try { Store.MarkUploaded(packageId, candidateDomain); } catch { }
        }

        public static void DiscardPending(string packageId)
        {
            try { Store.DiscardPending(packageId); } catch { }
        }

        public static void DiscardPending(string packageId, string candidateDomain)
        {
            try { Store.DiscardPending(packageId, candidateDomain); } catch { }
        }

        private static PolicyAnalysisLocalStateStore Store
        {
            get
            {
                lock (Gate)
                {
                    if (_store == null)
                    {
                        _store = new PolicyAnalysisLocalStateStore(Path.Combine(
                            AutoTranslatorScanner.GetLocalPackPath(),
                            "Cache",
                            "PolicyCloudAccelerationState.v1.json"));
                    }
                    return _store;
                }
            }
        }
    }
}
