using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoTranslator_Core.Terminology
{
    internal sealed class TerminologyCache
    {
        private readonly object _gate = new object();
        private readonly string _path;
        private TerminologyCacheFile _file;

        internal TerminologyCache(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _file = Load(path);
        }

        internal IReadOnlyList<TerminologyCandidate> GetByScope(string scopeKind, string scopeId)
        {
            lock (_gate)
            {
                return _file.Terms
                    .Where(term => term != null &&
                        string.Equals(term.ScopeKind, scopeKind, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(term.ScopeId, scopeId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    .Select(Clone)
                    .ToList();
            }
        }

        internal void UpsertMany(IEnumerable<TerminologyCandidate> terms)
        {
            lock (_gate)
            {
                var byId = _file.Terms
                    .Where(term => term != null && !string.IsNullOrWhiteSpace(term.TermId))
                    .GroupBy(term => term.TermId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                foreach (TerminologyCandidate term in terms ?? Enumerable.Empty<TerminologyCandidate>())
                {
                    if (term == null || string.IsNullOrWhiteSpace(term.TermId)) continue;
                    TerminologyCandidate incoming = Clone(term);
                    if (byId.TryGetValue(term.TermId, out TerminologyCandidate existing) &&
                        string.IsNullOrWhiteSpace(incoming.Target) &&
                        !string.IsNullOrWhiteSpace(existing.Target))
                    {
                        ApplyStoredState(incoming, existing);
                    }
                    else if (byId.TryGetValue(term.TermId, out existing) && existing.AgentAttempted)
                    {
                        incoming.AgentAttempted = true;
                        incoming.AgentReason = existing.AgentReason;
                    }
                    byId[term.TermId] = incoming;
                }
                _file.Terms = byId.Values.OrderBy(term => term.TermId, StringComparer.Ordinal).ToList();
                SaveAtomic();
            }
        }

        internal void MergeStoredState(IList<TerminologyCandidate> terms)
        {
            if (terms == null) return;
            lock (_gate)
            {
                var byId = _file.Terms
                    .Where(term => term != null && !string.IsNullOrWhiteSpace(term.TermId))
                    .GroupBy(term => term.TermId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                foreach (TerminologyCandidate term in terms)
                {
                    if (term == null || !byId.TryGetValue(term.TermId ?? string.Empty, out TerminologyCandidate stored)) continue;
                    if (!string.IsNullOrWhiteSpace(stored.Target)) ApplyStoredState(term, stored);
                    else if (stored.AgentAttempted)
                    {
                        term.AgentAttempted = true;
                        term.AgentReason = stored.AgentReason;
                    }
                }
            }
        }

        internal IReadOnlyList<TerminologyReviewItem> GetReviewQueue()
        {
            lock (_gate)
            {
                List<TerminologyCandidate> reviewable = _file.Terms
                    .Where(term => term != null &&
                        !string.Equals(term.Status, TerminologyStatus.Rejected, StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(term.Status, TerminologyStatus.Candidate, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(term.Status, TerminologyStatus.SessionActive, StringComparison.OrdinalIgnoreCase)) &&
                        (term.AgentAttempted || !string.IsNullOrWhiteSpace(term.Target)))
                    .ToList();
                var conflictTargets = _file.Terms
                    .Where(term => term != null &&
                        !string.Equals(term.Status, TerminologyStatus.Rejected, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(term.Target))
                    .GroupBy(ReviewScopeKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(term => term.Target.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        StringComparer.OrdinalIgnoreCase);
                return reviewable.Select(term =>
                {
                    List<string> targets = conflictTargets.TryGetValue(ReviewScopeKey(term), out List<string> values)
                        ? values
                        : new List<string>();
                    return new TerminologyReviewItem
                    {
                        Term = Clone(term),
                        HasConflict = targets.Count > 1,
                        ConflictingTargets = targets.ToList()
                    };
                }).OrderByDescending(item => item.HasConflict)
                  .ThenByDescending(item => item.Term.Status == TerminologyStatus.SessionActive)
                  .ThenBy(item => item.Term.SourceForm, StringComparer.OrdinalIgnoreCase)
                  .ToList();
            }
        }

        internal bool Approve(string termId, string target, string semanticRole, string scopeKind, string scopeId)
        {
            if (string.IsNullOrWhiteSpace(termId) || string.IsNullOrWhiteSpace(target) ||
                string.IsNullOrWhiteSpace(scopeId) ||
                (scopeKind != TerminologyScope.Mod && scopeKind != TerminologyScope.ModGroup && scopeKind != TerminologyScope.Global))
                return false;
            lock (_gate)
            {
                TerminologyCandidate term = _file.Terms.FirstOrDefault(item => item != null && item.TermId == termId);
                if (term == null) return false;
                term.Target = target.Trim();
                term.SemanticRole = (semanticRole ?? string.Empty).Trim();
                term.ScopeKind = scopeKind;
                term.ScopeId = scopeKind == TerminologyScope.Global ? "global" : scopeId.Trim();
                term.Status = TerminologyStatus.UserApproved;
                term.UpdatedUtc = DateTime.UtcNow.ToString("o");
                SaveAtomic();
                return true;
            }
        }

        internal bool Reject(string termId)
        {
            lock (_gate)
            {
                TerminologyCandidate term = _file.Terms.FirstOrDefault(item => item != null && item.TermId == termId);
                if (term == null) return false;
                term.Status = TerminologyStatus.Rejected;
                term.UpdatedUtc = DateTime.UtcNow.ToString("o");
                SaveAtomic();
                return true;
            }
        }

        internal IReadOnlyList<TerminologyCandidate> GetApplicable(
            string packageId,
            string groupId,
            string sessionId)
        {
            lock (_gate)
            {
                return _file.Terms
                    .Where(term => term != null &&
                        IsApplicableStatus(term.Status) &&
                        !string.IsNullOrWhiteSpace(term.Target) &&
                        (string.Equals(term.ScopeKind, TerminologyScope.Global, StringComparison.OrdinalIgnoreCase) ||
                         (string.Equals(term.ScopeKind, TerminologyScope.Mod, StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(term.ScopeId, packageId ?? string.Empty, StringComparison.OrdinalIgnoreCase)) ||
                         (string.Equals(term.ScopeKind, TerminologyScope.ModGroup, StringComparison.OrdinalIgnoreCase) &&
                          !string.IsNullOrWhiteSpace(groupId) &&
                          string.Equals(term.ScopeId, groupId, StringComparison.OrdinalIgnoreCase)) ||
                         (string.Equals(term.ScopeKind, TerminologyScope.Session, StringComparison.OrdinalIgnoreCase) &&
                          !string.IsNullOrWhiteSpace(sessionId) &&
                          string.Equals(term.ScopeId, sessionId, StringComparison.OrdinalIgnoreCase))))
                    .Select(Clone)
                    .ToList();
            }
        }

        private static bool IsApplicableStatus(string status)
        {
            return string.Equals(status, TerminologyStatus.SessionActive, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, TerminologyStatus.ModPersistent, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, TerminologyStatus.GroupPersistent, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, TerminologyStatus.UserApproved, StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyStoredState(TerminologyCandidate incoming, TerminologyCandidate stored)
        {
            incoming.Target = stored.Target;
            incoming.Status = stored.Status;
            incoming.SemanticRole = stored.SemanticRole;
            incoming.EvidenceKind = stored.EvidenceKind;
            incoming.ScopeKind = stored.ScopeKind;
            incoming.ScopeId = stored.ScopeId;
            incoming.SourceScopeKind = string.IsNullOrWhiteSpace(stored.SourceScopeKind)
                ? incoming.SourceScopeKind
                : stored.SourceScopeKind;
            incoming.SourceScopeId = string.IsNullOrWhiteSpace(stored.SourceScopeId)
                ? incoming.SourceScopeId
                : stored.SourceScopeId;
            incoming.AgentAttempted = stored.AgentAttempted;
            incoming.AgentReason = stored.AgentReason;
        }

        private static string ReviewScopeKey(TerminologyCandidate term)
        {
            string kind = string.IsNullOrWhiteSpace(term.SourceScopeKind) ? term.ScopeKind : term.SourceScopeKind;
            string id = string.IsNullOrWhiteSpace(term.SourceScopeId) ? term.ScopeId : term.SourceScopeId;
            string form = string.IsNullOrWhiteSpace(term.NormalizedForm) ? term.SourceForm : term.NormalizedForm;
            return (kind ?? string.Empty) + "|" + (id ?? string.Empty) + "|" + (form ?? string.Empty);
        }

        private static TerminologyCacheFile Load(string path)
        {
            if (!File.Exists(path)) return new TerminologyCacheFile();
            try
            {
                TerminologyCacheFile file = JsonConvert.DeserializeObject<TerminologyCacheFile>(File.ReadAllText(path));
                if (file == null || file.SchemaVersion != 1) return new TerminologyCacheFile();
                file.Terms = file.Terms ?? new List<TerminologyCandidate>();
                return file;
            }
            catch
            {
                return new TerminologyCacheFile();
            }
        }

        private void SaveAtomic()
        {
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(_file, Formatting.Indented));
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", true);
            else File.Move(temporary, _path);
        }

        private static TerminologyCandidate Clone(TerminologyCandidate term)
        {
            return JsonConvert.DeserializeObject<TerminologyCandidate>(JsonConvert.SerializeObject(term));
        }
    }
}
