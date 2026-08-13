using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Verse;

namespace AutoTranslator_Core
{
    public static class TranslationUnresolvedReasons
    {
        public const string ApiFailure = "api_failure";
        public const string MalformedResponse = "malformed_response";
        public const string EmptyResponse = "empty_response";
        public const string EnglishResidual = "english_residual";
        public const string WrongChineseVariant = "wrong_chinese_variant";
        public const string WrongTargetLanguage = "wrong_target_language";
        public const string ProtectedTokenMismatch = "protected_token_mismatch";
        public const string FormatArgumentMismatch = "format_argument_mismatch";
        public const string TitleTagMismatch = "title_tag_mismatch";
        public const string SaveFailure = "save_failure";
        public const string SourceFailure = "source_failure";
        public const string PolicyReview = "policy_review";
        public const string PolicyAgentFailure = "policy_agent_failure";
        public const string Unknown = "unknown";
    }

    public static class TranslationUnresolvedStates
    {
        public const string Pending = "pending";
        public const string Retrying = "retrying";
        public const string Ignored = "ignored";
        public const string Resolved = "resolved";
    }

    public sealed class TranslationUnresolvedEntry
    {
        public string Id { get; set; }
        public string TargetLanguage { get; set; }
        public string PackageId { get; set; }
        public string ModName { get; set; }
        public string Bucket { get; set; }
        public string DefType { get; set; }
        public string Key { get; set; }
        public string SourceText { get; set; }
        public string SourceFile { get; set; }
        public string TargetFile { get; set; }
        public string Reason { get; set; }
        public string Detail { get; set; }
        public int Attempts { get; set; }
        public string SourceHash { get; set; }
        public string State { get; set; }
    }

    internal sealed class TranslationUnresolvedReport
    {
        public int ReportVersion { get; set; }
        public string RunId { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public bool IsComplete { get; set; }
        public List<TranslationUnresolvedEntry> Entries { get; set; }
    }

    internal sealed class TranslationUnresolvedIgnoreFile
    {
        public int Version { get; set; }
        public List<string> Identities { get; set; }
    }

    public static class TranslationUnresolvedManager
    {
        private sealed class PackageScanRefreshState
        {
            public string PackageId;
            public string TargetLanguage;
            public HashSet<string> BaselineIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SeenFailureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool IsIncomplete;
        }

        private const int ReportVersion = 2;
        private const int IgnoreVersion = 2;
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, TranslationUnresolvedEntry> Entries =
            new Dictionary<string, TranslationUnresolvedEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> IgnoredIdentities =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, PackageScanRefreshState> ActivePackageScans =
            new Dictionary<string, PackageScanRefreshState>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;
        private static string _runId = string.Empty;
        private static DateTime _startedUtc;
        private static DateTime? _completedUtc;
        private static bool _isComplete;

        public static bool HasPending
        {
            get { return Count > 0; }
        }

        public static bool IsFileLevelFailure(TranslationUnresolvedEntry entry)
        {
            return entry != null &&
                   (string.Equals(entry.Reason, TranslationUnresolvedReasons.SaveFailure, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Reason, TranslationUnresolvedReasons.SourceFailure, StringComparison.OrdinalIgnoreCase)) &&
                   (entry.Key ?? string.Empty).StartsWith("__ATC_", StringComparison.Ordinal);
        }

        public static int Count
        {
            get
            {
                EnsureLoaded();
                lock (Sync)
                {
                    return Entries.Values.Count(IsPending);
                }
            }
        }

        public static bool HasPendingForPackage(string packageId)
        {
            return HasPendingForPackage(packageId, null);
        }

        public static bool HasPendingForPackage(string packageId, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return false;
            EnsureLoaded();
            lock (Sync)
            {
                return Entries.Values.Any(entry => IsPending(entry) && string.Equals(
                    entry.PackageId,
                    packageId,
                    StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(targetLanguage) || string.Equals(
                        entry.TargetLanguage,
                        targetLanguage,
                        StringComparison.OrdinalIgnoreCase)));
            }
        }

        public static void BeginRun()
        {
            EnsureLoaded();
            lock (Sync)
            {
                ActivePackageScans.Clear();
                foreach (string id in Entries
                    .Where(pair => !IsPending(pair.Value))
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    Entries.Remove(id);
                }
                _runId = Guid.NewGuid().ToString("N");
                _startedUtc = DateTime.UtcNow;
                _completedUtc = null;
                _isComplete = false;
            }
        }

        public static void BeginPackageScan(string packageId, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return;
            EnsureLoaded();

            TranslationUnresolvedReport report;
            lock (Sync)
            {
                EnsureRunStartedLocked();
                PackageScanRefreshState state = new PackageScanRefreshState
                {
                    PackageId = packageId.Trim(),
                    TargetLanguage = (targetLanguage ?? string.Empty).Trim()
                };
                foreach (string id in Entries.Values
                    .Where(entry => IsPending(entry) && PackageScanMatches(
                        entry,
                        state.PackageId,
                        state.TargetLanguage))
                    .Select(entry => entry.Id))
                {
                    state.BaselineIds.Add(id);
                }

                ActivePackageScans[CreatePackageScanKey(state.PackageId, state.TargetLanguage)] = state;
                report = CreateReportLocked();
            }

            SaveReport(report);
        }

        public static void CompletePackageScan(string packageId, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return;
            EnsureLoaded();

            TranslationUnresolvedReport report;
            lock (Sync)
            {
                string scanKey = CreatePackageScanKey(packageId, targetLanguage);
                if (ActivePackageScans.TryGetValue(scanKey, out PackageScanRefreshState state))
                {
                    foreach (string baselineId in state.BaselineIds)
                    {
                        if (!state.IsIncomplete && !state.SeenFailureIds.Contains(baselineId))
                            Entries.Remove(baselineId);
                    }
                    ActivePackageScans.Remove(scanKey);
                }

                EnsureRunStartedLocked();
                report = CreateReportLocked();
            }

            SaveReport(report);
        }

        public static void MarkPackageScanIncomplete(string packageId, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return;
            EnsureLoaded();
            lock (Sync)
            {
                if (ActivePackageScans.TryGetValue(
                        CreatePackageScanKey(packageId, targetLanguage),
                        out PackageScanRefreshState state))
                {
                    state.IsIncomplete = true;
                }
            }
        }

        public static void AbortPackageScan(string packageId, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return;
            EnsureLoaded();

            TranslationUnresolvedReport report;
            lock (Sync)
            {
                ActivePackageScans.Remove(CreatePackageScanKey(packageId, targetLanguage));
                EnsureRunStartedLocked();
                report = CreateReportLocked();
            }

            SaveReport(report);
        }

        public static void RecordFailure(TranslationUnresolvedEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.PackageId) || string.IsNullOrWhiteSpace(entry.Key))
                return;

            EnsureLoaded();
            TranslationUnresolvedEntry normalized = Normalize(entry);
            lock (Sync)
            {
                EnsureRunStartedLocked();
                string identity = CreateIgnoreIdentity(normalized);
                normalized.State = IgnoredIdentities.Contains(identity)
                    ? TranslationUnresolvedStates.Ignored
                    : TranslationUnresolvedStates.Pending;

                TranslationUnresolvedEntry existing;
                if (Entries.TryGetValue(normalized.Id, out existing))
                {
                    normalized.Attempts = Math.Max(normalized.Attempts, Math.Max(1, existing.Attempts + 1));
                }

                Entries[normalized.Id] = normalized;
                foreach (PackageScanRefreshState state in ActivePackageScans.Values)
                {
                    if (PackageScanMatches(normalized, state.PackageId, state.TargetLanguage))
                        state.SeenFailureIds.Add(normalized.Id);
                }
            }
        }

        public static void CompleteRun()
        {
            EnsureLoaded();
            TranslationUnresolvedReport report;
            lock (Sync)
            {
                EnsureRunStartedLocked();
                ActivePackageScans.Clear();
                _isComplete = true;
                _completedUtc = DateTime.UtcNow;
                report = CreateReportLocked();
            }

            SaveReport(report);
        }

        public static void SaveRunProgress()
        {
            EnsureLoaded();
            TranslationUnresolvedReport report;
            lock (Sync)
            {
                EnsureRunStartedLocked();
                ActivePackageScans.Clear();
                report = CreateReportLocked();
            }

            SaveReport(report);
        }

        public static List<TranslationUnresolvedEntry> Snapshot()
        {
            EnsureLoaded();
            lock (Sync)
            {
                return Entries.Values
                    .Select(CloneEntry)
                    .OrderBy(entry => entry.ModName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Bucket ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.DefType ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.SourceFile ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public static bool Ignore(IEnumerable<string> ids)
        {
            if (ids == null) return false;
            EnsureLoaded();

            HashSet<string> selectedIds = new HashSet<string>(
                ids.Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            if (selectedIds.Count == 0) return false;

            List<string> identities;
            List<string> proposedIgnored;
            lock (Sync)
            {
                identities = selectedIds
                    .Where(id => Entries.ContainsKey(id))
                    .Select(id => CreateIgnoreIdentity(Entries[id]))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (identities.Count == 0) return false;

                proposedIgnored = IgnoredIdentities
                    .Concat(identities)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList();
            }

            if (!SaveIgnoredIdentities(proposedIgnored)) return false;

            TranslationUnresolvedReport report;
            lock (Sync)
            {
                foreach (string identity in identities) IgnoredIdentities.Add(identity);
                foreach (string id in selectedIds)
                {
                    TranslationUnresolvedEntry entry;
                    if (Entries.TryGetValue(id, out entry)) entry.State = TranslationUnresolvedStates.Ignored;
                }
                report = CreateReportLocked();
            }

            SaveReport(report);
            return true;
        }

        public static void Resolve(IEnumerable<string> ids)
        {
            UpdateStates(ids, TranslationUnresolvedStates.Resolved);
        }

        public static void ResolveMatching(
            string packageId,
            string bucket,
            string defType,
            string key,
            string sourceText,
            string targetLanguage)
        {
            ResolveMatching(new[]
            {
                new TranslationUnresolvedEntry
                {
                    TargetLanguage = targetLanguage,
                    PackageId = packageId,
                    Bucket = bucket,
                    DefType = defType,
                    Key = key,
                    SourceText = sourceText
                }
            });
        }

        public static void ResolveMatching(IEnumerable<TranslationUnresolvedEntry> entries)
        {
            if (entries == null) return;
            EnsureLoaded();

            HashSet<string> identities = new HashSet<string>(
                entries
                    .Where(entry => entry != null &&
                        !string.IsNullOrWhiteSpace(entry.PackageId) &&
                        !string.IsNullOrWhiteSpace(entry.Key))
                    .Select(entry => CreateIgnoreIdentity(Normalize(entry))),
                StringComparer.OrdinalIgnoreCase);
            if (identities.Count == 0) return;

            List<string> matchingIds;
            lock (Sync)
            {
                matchingIds = Entries.Values
                    .Where(entry => IsPending(entry) && identities.Contains(CreateIgnoreIdentity(entry)))
                    .Select(entry => entry.Id)
                    .ToList();
            }

            if (matchingIds.Count > 0) Resolve(matchingIds);
        }

        public static bool ShouldKeepOriginal(TranslationUnresolvedEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.PackageId) || string.IsNullOrWhiteSpace(entry.Key))
                return false;

            EnsureLoaded();
            TranslationUnresolvedEntry normalized = Normalize(entry);
            lock (Sync)
            {
                return IgnoredIdentities.Contains(CreateIgnoreIdentity(normalized));
            }
        }

        public static bool ShouldKeepOriginal(
            string packageId,
            string bucket,
            string defType,
            string key,
            string sourceText,
            string targetLanguage)
        {
            return ShouldKeepOriginal(new TranslationUnresolvedEntry
            {
                TargetLanguage = targetLanguage,
                PackageId = packageId,
                Bucket = bucket,
                DefType = defType,
                Key = key,
                SourceText = sourceText
            });
        }

        private static void UpdateStates(IEnumerable<string> ids, string state)
        {
            if (ids == null) return;
            EnsureLoaded();

            HashSet<string> selectedIds = new HashSet<string>(
                ids.Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            if (selectedIds.Count == 0) return;

            TranslationUnresolvedReport report;
            lock (Sync)
            {
                foreach (string id in selectedIds)
                {
                    TranslationUnresolvedEntry entry;
                    if (!Entries.TryGetValue(id, out entry)) continue;
                    entry.State = state;
                }

                report = CreateReportLocked();
            }

            SaveReport(report);
        }

        private static bool IsPending(TranslationUnresolvedEntry entry)
        {
            return entry != null && string.Equals(
                entry.State,
                TranslationUnresolvedStates.Pending,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool PackageScanMatches(
            TranslationUnresolvedEntry entry,
            string packageId,
            string targetLanguage)
        {
            return entry != null &&
                   string.Equals(entry.PackageId, packageId, StringComparison.OrdinalIgnoreCase) &&
                   (string.IsNullOrWhiteSpace(targetLanguage) || string.Equals(
                       entry.TargetLanguage,
                       targetLanguage,
                       StringComparison.OrdinalIgnoreCase));
        }

        private static string CreatePackageScanKey(string packageId, string targetLanguage)
        {
            return NormalizeIdentityPart(packageId) + "|" + NormalizeIdentityPart(targetLanguage);
        }

        private static TranslationUnresolvedEntry Normalize(TranslationUnresolvedEntry entry)
        {
            TranslationUnresolvedEntry normalized = CloneEntry(entry);
            normalized.TargetLanguage = (normalized.TargetLanguage ?? string.Empty).Trim();
            normalized.PackageId = (normalized.PackageId ?? string.Empty).Trim();
            normalized.ModName = (normalized.ModName ?? string.Empty).Trim();
            normalized.Bucket = string.IsNullOrWhiteSpace(normalized.Bucket)
                ? "Unknown"
                : normalized.Bucket.Trim();
            normalized.DefType = (normalized.DefType ?? string.Empty).Trim();
            normalized.Key = (normalized.Key ?? string.Empty).Trim();
            normalized.SourceText = normalized.SourceText ?? string.Empty;
            normalized.SourceFile = normalized.SourceFile ?? string.Empty;
            normalized.TargetFile = normalized.TargetFile ?? string.Empty;
            normalized.Reason = string.IsNullOrWhiteSpace(normalized.Reason)
                ? TranslationUnresolvedReasons.Unknown
                : normalized.Reason.Trim();
            normalized.Detail = normalized.Detail ?? string.Empty;
            normalized.Attempts = Math.Max(1, normalized.Attempts);
            normalized.SourceHash = string.IsNullOrWhiteSpace(normalized.SourceHash)
                ? ComputeHash(normalized.SourceText)
                : normalized.SourceHash.Trim().ToLowerInvariant();
            normalized.Id = ComputeHash(CreateIgnoreIdentity(normalized));
            return normalized;
        }

        private static string CreateIgnoreIdentity(TranslationUnresolvedEntry entry)
        {
            if (entry == null) return string.Empty;
            return string.Join("|", new[]
            {
                NormalizeIdentityPart(entry.TargetLanguage),
                NormalizeIdentityPart(entry.PackageId),
                NormalizeIdentityPart(entry.Bucket),
                NormalizeIdentityPart(entry.DefType),
                NormalizeIdentityPart(entry.Key),
                NormalizeIdentityPart(entry.SourceHash)
            });
        }

        private static string NormalizeIdentityPart(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string ComputeHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder builder = new StringBuilder(bytes.Length * 2);
                foreach (byte valueByte in bytes) builder.Append(valueByte.ToString("x2"));
                return builder.ToString();
            }
        }

        private static TranslationUnresolvedEntry CloneEntry(TranslationUnresolvedEntry source)
        {
            if (source == null) return null;
            return new TranslationUnresolvedEntry
            {
                Id = source.Id,
                TargetLanguage = source.TargetLanguage,
                PackageId = source.PackageId,
                ModName = source.ModName,
                Bucket = source.Bucket,
                DefType = source.DefType,
                Key = source.Key,
                SourceText = source.SourceText,
                SourceFile = source.SourceFile,
                TargetFile = source.TargetFile,
                Reason = source.Reason,
                Detail = source.Detail,
                Attempts = source.Attempts,
                SourceHash = source.SourceHash,
                State = source.State
            };
        }

        private static void EnsureRunStartedLocked()
        {
            if (!string.IsNullOrWhiteSpace(_runId)) return;
            _runId = Guid.NewGuid().ToString("N");
            _startedUtc = DateTime.UtcNow;
            _completedUtc = null;
            _isComplete = false;
        }

        private static TranslationUnresolvedReport CreateReportLocked()
        {
            return new TranslationUnresolvedReport
            {
                ReportVersion = ReportVersion,
                RunId = _runId,
                StartedUtc = _startedUtc,
                CompletedUtc = _completedUtc,
                IsComplete = _isComplete,
                Entries = Entries.Values.Select(CloneEntry).ToList()
            };
        }

        private static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (_loaded) return;
                _loaded = true;

                LoadIgnoredIdentitiesLocked();
                LoadLatestReportLocked();
            }
        }

        private static void LoadLatestReportLocked()
        {
            string path = GetLatestReportPath();
            if (!File.Exists(path)) return;

            try
            {
                TranslationUnresolvedReport report = JsonConvert.DeserializeObject<TranslationUnresolvedReport>(
                    File.ReadAllText(path, Encoding.UTF8));
                if (report == null) return;

                _runId = report.RunId ?? string.Empty;
                _startedUtc = report.StartedUtc;
                _completedUtc = report.CompletedUtc;
                _isComplete = report.IsComplete;
                foreach (TranslationUnresolvedEntry entry in report.Entries ?? new List<TranslationUnresolvedEntry>())
                {
                    if (entry == null) continue;
                    TranslationUnresolvedEntry normalized = Normalize(entry);
                    if (IgnoredIdentities.Contains(CreateIgnoreIdentity(normalized)))
                        normalized.State = TranslationUnresolvedStates.Ignored;
                    Entries[normalized.Id] = normalized;
                    if (string.Equals(entry.State, TranslationUnresolvedStates.Ignored, StringComparison.OrdinalIgnoreCase))
                        IgnoredIdentities.Add(CreateIgnoreIdentity(normalized));
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] Could not load unresolved translation report: " + ex.Message);
            }
        }

        private static void LoadIgnoredIdentitiesLocked()
        {
            string path = GetIgnorePath();
            if (!File.Exists(path)) return;

            try
            {
                TranslationUnresolvedIgnoreFile file = JsonConvert.DeserializeObject<TranslationUnresolvedIgnoreFile>(
                    File.ReadAllText(path, Encoding.UTF8));
                foreach (string identity in file != null
                    ? file.Identities ?? new List<string>()
                    : new List<string>())
                {
                    if (!string.IsNullOrWhiteSpace(identity)) IgnoredIdentities.Add(identity);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] Could not load unresolved translation ignores: " + ex.Message);
            }
        }

        private static bool SaveReport(TranslationUnresolvedReport report)
        {
            if (report == null) return false;
            return TrySaveJson(GetLatestReportPath(), report, "unresolved translation report");
        }

        private static bool SaveIgnoredIdentities(List<string> identities)
        {
            TranslationUnresolvedIgnoreFile file = new TranslationUnresolvedIgnoreFile
            {
                Version = IgnoreVersion,
                Identities = identities ?? new List<string>()
            };
            return TrySaveJson(GetIgnorePath(), file, "unresolved translation ignores");
        }

        private static bool TrySaveJson(string path, object value, string label)
        {
            try
            {
                string json = JsonConvert.SerializeObject(value, Formatting.Indented);
                byte[] data = Encoding.UTF8.GetBytes(json);
                TranslationXmlAtomicFileStore.Save(path, stream => stream.Write(data, 0, data.Length));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] Could not save " + label + ": " + ex.Message);
                return false;
            }
        }

        private static string GetReportDirectory()
        {
            return Path.Combine(
                AutoTranslatorScanner.GetLocalPackPath(),
                "Reports",
                "TranslationUnresolved");
        }

        private static string GetLatestReportPath()
        {
            return Path.Combine(GetReportDirectory(), "latest.json");
        }

        private static string GetIgnorePath()
        {
            return Path.Combine(GetReportDirectory(), "ignored.json");
        }
    }
}
