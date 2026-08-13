using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Verse;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTranslator_Core.Terminology
{
    internal static class TerminologyRuntime
    {
        private static readonly object Gate = new object();
        private static TerminologyCache _cache;
        private static readonly Dictionary<string, List<TerminologyCorpusEntry>> SessionCorpusByScope =
            new Dictionary<string, List<TerminologyCorpusEntry>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TerminologySessionFile> SessionByScope =
            new Dictionary<string, TerminologySessionFile>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim AgentGate = new SemaphoreSlim(1, 1);

        internal static string BuildPromptContext(string packageId, IReadOnlyList<string> texts)
        {
            List<TerminologyCandidate> terms = GetRelevantTerms(packageId, texts);
            return TerminologyPromptContextBuilder.Build(terms, texts, 20, 2000);
        }

        internal static List<TerminologyCandidate> GetRelevantTerms(string packageId, IReadOnlyList<string> texts)
        {
            AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
            if (settings == null || !settings.IsTerminologyEnabledForPackage(packageId)) return new List<TerminologyCandidate>();
            string group = settings.GetTerminologyGroup(packageId);
            string scopeKind = string.IsNullOrWhiteSpace(group) ? TerminologyScope.Mod : TerminologyScope.ModGroup;
            string scopeId = string.IsNullOrWhiteSpace(group) ? (packageId ?? string.Empty).Trim() : group.Trim();
            string scopeKey = scopeKind + "|" + scopeId;
            string sessionId = string.Empty;
            lock (Gate)
            {
                if (SessionByScope.TryGetValue(scopeKey, out TerminologySessionFile session))
                    sessionId = session.SessionId;
            }
            IReadOnlyList<TerminologyCandidate> terms = GetCache().GetApplicable(packageId, group, sessionId);
            return TerminologyPromptContextBuilder.SelectRelevant(terms, texts, 20);
        }

        internal static TerminologyCache GetCache()
        {
            lock (Gate)
            {
                if (_cache != null) return _cache;
                string path = Path.Combine(
                    AutoTranslatorScanner.GetLocalPackPath(),
                    "Cache",
                    "Terminology.v1.json");
                _cache = new TerminologyCache(path);
                return _cache;
            }
        }

        internal static void ObserveTranslationInputs(
            string packageId,
            string bucket,
            string defType,
            IEnumerable<KeyValuePair<string, string>> inputs)
        {
            AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
            if (settings == null || !settings.IsTerminologyEnabledForPackage(packageId)) return;
            string group = settings.GetTerminologyGroup(packageId);
            string scopeKind = string.IsNullOrWhiteSpace(group) ? TerminologyScope.Mod : TerminologyScope.ModGroup;
            string scopeId = string.IsNullOrWhiteSpace(group) ? (packageId ?? string.Empty).Trim() : group.Trim();
            string scopeKey = scopeKind + "|" + scopeId;

            lock (Gate)
            {
                if (!SessionCorpusByScope.TryGetValue(scopeKey, out List<TerminologyCorpusEntry> corpus))
                {
                    TerminologySessionFile session = LoadOrCreateSession(scopeKind, scopeId, group);
                    corpus = session.Corpus ?? new List<TerminologyCorpusEntry>();
                    session.Corpus = corpus;
                    SessionCorpusByScope[scopeKey] = corpus;
                    SessionByScope[scopeKey] = session;
                }
                foreach (KeyValuePair<string, string> input in inputs ?? Enumerable.Empty<KeyValuePair<string, string>>())
                {
                    if (string.IsNullOrWhiteSpace(input.Value)) continue;
                    string key = input.Key ?? string.Empty;
                    if (corpus.Any(existing =>
                        string.Equals(existing.PackageId, packageId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.Key, key, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.Text, input.Value, StringComparison.Ordinal)))
                        continue;
                    corpus.Add(new TerminologyCorpusEntry
                    {
                        PackageId = packageId ?? string.Empty,
                        GroupId = group,
                        Key = key,
                        DefType = defType ?? string.Empty,
                        Field = GetTerminalField(key),
                        Text = input.Value,
                        SourceKind = bucket ?? string.Empty
                    });
                }
                if (corpus.Count > 20000) corpus.RemoveRange(0, corpus.Count - 20000);
                List<TerminologyCandidate> candidates = TerminologyCandidateExtractor.Extract(
                    corpus,
                    scopeKind,
                    scopeId,
                    200);
                GetCache().MergeStoredState(candidates);
                GetCache().UpsertMany(candidates);
                TerminologySessionFile currentSession = SessionByScope[scopeKey];
                currentSession.Candidates = candidates;
                GetSessionStore(currentSession.SessionId).Save(currentSession);
            }
        }

        internal static void ObserveAlignedTranslations(
            string packageId,
            IEnumerable<TerminologyAlignedSentencePair> pairs)
        {
            AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
            if (settings == null || !settings.IsTerminologyEnabledForPackage(packageId)) return;
            List<TerminologyAlignedSentencePair> safePairs = (pairs ?? Enumerable.Empty<TerminologyAlignedSentencePair>())
                .Where(pair => pair != null &&
                    !string.IsNullOrWhiteSpace(pair.Source) &&
                    !string.IsNullOrWhiteSpace(pair.Target) &&
                    !string.Equals(pair.Source.Trim(), pair.Target.Trim(), StringComparison.OrdinalIgnoreCase))
                .Take(5000)
                .ToList();
            if (safePairs.Count < 2) return;

            string group = settings.GetTerminologyGroup(packageId);
            string scopeKind = string.IsNullOrWhiteSpace(group) ? TerminologyScope.Mod : TerminologyScope.ModGroup;
            string scopeId = string.IsNullOrWhiteSpace(group) ? (packageId ?? string.Empty).Trim() : group.Trim();
            string scopeKey = scopeKind + "|" + scopeId;
            lock (Gate)
            {
                TerminologySessionFile session;
                if (!SessionByScope.TryGetValue(scopeKey, out session))
                {
                    session = LoadOrCreateSession(scopeKind, scopeId, group);
                    SessionByScope[scopeKey] = session;
                    SessionCorpusByScope[scopeKey] = session.Corpus ?? new List<TerminologyCorpusEntry>();
                }
                List<TerminologyTrustedAnchor> anchors = GetCache()
                    .GetApplicable(packageId, group, session.SessionId)
                    .Where(term => term.Status == TerminologyStatus.UserApproved ||
                                   term.Status == TerminologyStatus.ModPersistent ||
                                   term.Status == TerminologyStatus.GroupPersistent)
                    .Select(term => new TerminologyTrustedAnchor
                    {
                        TermId = term.TermId,
                        Source = term.SourceForm,
                        Target = term.Target
                    })
                    .ToList();
                if (anchors.Count == 0) return;

                List<TerminologyCandidate> mined = AlignedTranslationMiner.Mine(
                    safePairs,
                    anchors,
                    scopeKind,
                    scopeId);
                if (mined.Count == 0) return;
                GetCache().MergeStoredState(mined);
                var combined = (session.Candidates ?? new List<TerminologyCandidate>())
                    .Concat(mined)
                    .GroupBy(candidate => candidate.TermId, StringComparer.Ordinal)
                    .Select(candidateGroup => candidateGroup.OrderByDescending(candidate => candidate.Score).First())
                    .ToList();
                session.Candidates = combined;
                GetCache().UpsertMany(combined);
                GetSessionStore(session.SessionId).Save(session);
            }
        }

        internal static async Task ResolveHighValueCandidatesAsync(string packageId)
        {
            AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
            if (settings == null || !settings.IsTerminologyEnabledForPackage(packageId)) return;
            string group = settings.GetTerminologyGroup(packageId);
            string scopeKind = string.IsNullOrWhiteSpace(group) ? TerminologyScope.Mod : TerminologyScope.ModGroup;
            string scopeId = string.IsNullOrWhiteSpace(group) ? (packageId ?? string.Empty).Trim() : group.Trim();
            string scopeKey = scopeKind + "|" + scopeId;

            await AgentGate.WaitAsync();
            try
            {
                if (AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested) return;
                TerminologySessionFile session;
                List<TerminologyCandidate> pending;
                lock (Gate)
                {
                    if (!SessionByScope.TryGetValue(scopeKey, out session)) return;
                    if (session.AgentCalls >= 2) return;
                    pending = (session.Candidates ?? new List<TerminologyCandidate>())
                        .Where(candidate => candidate != null &&
                            candidate.Status == TerminologyStatus.Candidate &&
                            !candidate.AgentAttempted &&
                            string.IsNullOrWhiteSpace(candidate.Target) &&
                            candidate.Score >= 5f &&
                            (candidate.Frequency >= 2 || candidate.PackageCount >= 2))
                        .OrderByDescending(candidate => candidate.Score)
                        .Take(12)
                        .ToList();
                }
                if (pending.Count == 0 || !AutoTranslatorAPI.HasAnyPolicyAgentConfig()) return;
                lock (Gate)
                {
                    session.AgentCalls++;
                    GetSessionStore(session.SessionId).Save(session);
                }
                TerminologyAgentBatchResult result = await AutoTranslatorAPI.AnalyzeTerminologyCandidatesAsync(
                    packageId,
                    scopeId,
                    pending);
                if ((AutoTranslatorSettings.IsCancellationRequested || AutoTranslatorSettings.IsSkipCurrentRequested) &&
                    (result?.Decisions == null || result.Decisions.Count == 0))
                    return;

                var decisions = (result.Decisions ?? new List<TerminologyAgentDecision>())
                    .ToDictionary(decision => decision.TermId, decision => decision, StringComparer.Ordinal);
                lock (Gate)
                {
                    foreach (TerminologyCandidate candidate in pending)
                    {
                        candidate.AgentAttempted = true;
                        if (!decisions.TryGetValue(candidate.TermId, out TerminologyAgentDecision decision))
                        {
                            candidate.AgentReason = result.ErrorCode ?? "no_decision";
                            continue;
                        }
                        candidate.AgentReason = decision.Reason ?? string.Empty;
                        candidate.SemanticRole = decision.SemanticRole ?? string.Empty;
                        if (decision.Decision == "reject")
                        {
                            candidate.Status = TerminologyStatus.Rejected;
                            continue;
                        }
                        if (decision.Decision != "accept" ||
                            !TranslationResultLanguagePolicy.ShouldAccept(
                                decision.Target,
                                candidate.SourceForm,
                                settings.TargetLang))
                            continue;
                        candidate.Target = decision.Target.Trim();
                        candidate.SourceScopeKind = string.IsNullOrWhiteSpace(candidate.SourceScopeKind)
                            ? scopeKind
                            : candidate.SourceScopeKind;
                        candidate.SourceScopeId = string.IsNullOrWhiteSpace(candidate.SourceScopeId)
                            ? scopeId
                            : candidate.SourceScopeId;
                        candidate.Status = TerminologyStatus.SessionActive;
                        candidate.ScopeKind = TerminologyScope.Session;
                        candidate.ScopeId = session.SessionId;
                    }
                    session.Candidates = session.Candidates ?? new List<TerminologyCandidate>();
                    GetCache().UpsertMany(session.Candidates);
                    GetSessionStore(session.SessionId).Save(session);
                }
            }
            finally
            {
                AgentGate.Release();
            }
        }

        private static TerminologySessionFile LoadOrCreateSession(
            string scopeKind,
            string scopeId,
            string group)
        {
            string fingerprint = BuildScopeSourceFingerprint(scopeKind, scopeId, group);
            string sessionId = CreateHash("terminology-session-v1|" +
                AutoTranslatorMod.Settings.TargetLang + "|" + scopeKind + "|" + scopeId + "|" + fingerprint);
            TerminologySessionStore store = GetSessionStore(sessionId);
            TerminologySessionFile loaded = store.Load(sessionId, fingerprint);
            if (loaded != null) return loaded;
            return new TerminologySessionFile
            {
                SessionId = sessionId,
                ScopeKind = scopeKind,
                ScopeId = scopeId,
                SourceFingerprint = fingerprint
            };
        }

        private static string BuildScopeSourceFingerprint(string scopeKind, string scopeId, string group)
        {
            IEnumerable<ModMetaData> mods = ModLister.AllInstalledMods.Where(mod =>
                mod != null && !string.IsNullOrWhiteSpace(mod.PackageId) &&
                AutoTranslatorMod.Settings.IsTerminologyEnabledForPackage(mod.PackageId) &&
                (string.Equals(scopeKind, TerminologyScope.Mod, StringComparison.OrdinalIgnoreCase)
                    ? string.Equals(mod.PackageId, scopeId, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(AutoTranslatorMod.Settings.GetTerminologyGroup(mod.PackageId), group, StringComparison.OrdinalIgnoreCase)));
            var parts = new List<string>();
            foreach (ModMetaData mod in mods.OrderBy(mod => mod.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                string root = mod.RootDir != null ? mod.RootDir.FullName : string.Empty;
                ModUpdateDetector.SourceFingerprintSnapshot snapshot =
                    ModUpdateDetector.BuildSourceFingerprintSnapshot(
                        mod.PackageId,
                        root,
                        AutoTranslatorMod.Settings.TargetLang);
                parts.Add(mod.PackageId.ToLowerInvariant() + ":" + (snapshot?.Fingerprint ?? string.Empty));
            }
            return CreateHash(string.Join("|", parts));
        }

        private static TerminologySessionStore GetSessionStore(string sessionId)
        {
            return new TerminologySessionStore(Path.Combine(
                AutoTranslatorScanner.GetLocalPackPath(),
                "Cache",
                "TerminologySessions",
                sessionId + ".json"));
        }

        private static string CreateHash(string material)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material ?? string.Empty));
                return string.Concat(hash.Take(16).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static string GetTerminalField(string key)
        {
            string value = (key ?? string.Empty).Trim();
            int separator = Math.Max(value.LastIndexOf('.'), Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\')));
            return separator >= 0 && separator + 1 < value.Length ? value.Substring(separator + 1) : value;
        }
    }
}
