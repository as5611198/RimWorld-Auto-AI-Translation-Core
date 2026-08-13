using AutoTranslator_Core.Terminology;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TerminologyMechanicalSelfTest
{
    internal static class Program
    {
        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "atc-terminology-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                Assert(TerminologyMorphology.NormalizeEnglishForm("Empires") == "empire", "plural normalization");
                Assert(TerminologyMorphology.NormalizeEnglishForm("Empire's") == "empire", "possessive normalization");
                Assert(TerminologyMorphology.NormalizeEnglishForm("Imperial") == "imperial", "derivation stays separate");
                Assert(TerminologyMorphology.NormalizeEnglishForm("Empire") != TerminologyMorphology.NormalizeEnglishForm("Imperial"),
                    "Empire and Imperial must not be merged mechanically");

                var fiftyModCorpus = Enumerable.Range(1, 50)
                    .Select(index => Entry(
                        "selection.mod." + index,
                        "selection-group",
                        "ThingDef",
                        "label",
                        "SelectionTerm item " + index))
                    .ToList();
                var nineSelectedPackageIds = Enumerable.Range(1, 9)
                    .Select(index => "selection.mod." + index)
                    .ToList();
                List<TerminologyCorpusEntry> selectedNineCorpus = TerminologyPackageSelection.FilterSelected(
                    true,
                    nineSelectedPackageIds,
                    fiftyModCorpus,
                    entry => entry.PackageId);
                Assert(selectedNineCorpus.Count == 9 &&
                    selectedNineCorpus.Select(entry => entry.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 9,
                    "50-mod selection boundary admits exactly the nine selected packages");
                Assert(selectedNineCorpus.All(entry => nineSelectedPackageIds.Contains(
                    entry.PackageId,
                    StringComparer.OrdinalIgnoreCase)),
                    "unselected packages cannot enter terminology extraction input");
                Assert(TerminologyPackageSelection.FilterSelected(
                    false,
                    nineSelectedPackageIds,
                    fiftyModCorpus,
                    entry => entry.PackageId).Count == 0,
                    "disabled terminology feature admits no packages");
                List<TerminologyCandidate> selectionCandidates = TerminologyCandidateExtractor.Extract(
                    selectedNineCorpus,
                    TerminologyScope.ModGroup,
                    "selection-group");
                Assert(selectionCandidates.Count > 0 && selectionCandidates.All(candidate =>
                    candidate.PackageIds.Count == 9 && candidate.PackageIds.All(packageId =>
                        nineSelectedPackageIds.Contains(packageId, StringComparer.OrdinalIgnoreCase))),
                    "candidate extraction and its Agent payload provenance contain only the nine selected packages");

                var selected = new List<TerminologyCorpusEntry>
                {
                    Entry("mod.a", "milira", "FactionDef", "label", "Milira Empire"),
                    Entry("mod.b", "milira", "ThingDef", "label", "Milira weapon"),
                    Entry("mod.b", "milira", "PawnKindDef", "label", "Milira soldier"),
                    Entry("mod.a", "milira", "ResearchProjectDef", "description", "The Milira Empire fields Milira soldiers."),
                };
                List<TerminologyCandidate> candidates = TerminologyCandidateExtractor.Extract(
                    selected,
                    TerminologyScope.ModGroup,
                    "milira");
                TerminologyCandidate milira = candidates.FirstOrDefault(candidate => candidate.NormalizedForm == "milira");
                Assert(milira != null, "TitleCase and repeated Milira candidate");
                Assert(milira.PackageCount == 2, "candidate crosses only the two selected mods");
                Assert(!milira.PackageIds.Contains("mod.c"), "unselected mod cannot contribute");
                Assert(milira.Contexts.Count <= 3, "representative context cap");

                List<TerminologyCandidate> otherGroup = TerminologyCandidateExtractor.Extract(
                    new[] { Entry("mod.c", "other", "FactionDef", "label", "Milira Colony") },
                    TerminologyScope.ModGroup,
                    "other");
                TerminologyCandidate otherMilira = otherGroup.First(candidate => candidate.NormalizedForm == "milira");
                Assert(milira.TermId != otherMilira.TermId, "same source in different groups has distinct identity");

                milira.Target = "米莉拉";
                milira.Status = TerminologyStatus.SessionActive;
                otherMilira.Target = "米利拉";
                otherMilira.Status = TerminologyStatus.SessionActive;
                string cachePath = Path.Combine(root, "Terminology.v1.json");
                var cache = new TerminologyCache(cachePath);
                cache.UpsertMany(new[] { milira, otherMilira });
                var reloaded = new TerminologyCache(cachePath);
                Assert(reloaded.GetByScope(TerminologyScope.ModGroup, "milira").Single().Target == "米莉拉",
                    "first group persists independently");
                Assert(reloaded.GetByScope(TerminologyScope.ModGroup, "other").Single().Target == "米利拉",
                    "second group persists independently");
                Assert(File.Exists(cachePath), "atomic cache file written");

                var refreshed = new TerminologyCandidate
                {
                    TermId = milira.TermId,
                    SourceForm = "Milira",
                    NormalizedForm = "milira",
                    ScopeKind = TerminologyScope.ModGroup,
                    ScopeId = "milira",
                    SourceScopeKind = TerminologyScope.ModGroup,
                    SourceScopeId = "milira"
                };
                reloaded.MergeStoredState(new[] { refreshed });
                Assert(refreshed.Target == "米莉拉" && refreshed.Status == TerminologyStatus.SessionActive,
                    "mechanical refresh preserves resolved term state");

                IReadOnlyList<TerminologyReviewItem> review = reloaded.GetReviewQueue();
                Assert(review.Count == 2 && review.All(item => !item.HasConflict),
                    "different groups are not reported as conflicts");
                Assert(reloaded.Approve(milira.TermId, "米莉拉", "proper_noun", TerminologyScope.ModGroup, "milira"),
                    "user can promote a term to group scope");
                Assert(reloaded.GetApplicable("mod.a", "milira", string.Empty)
                    .Any(item => item.TermId == milira.TermId && item.Status == TerminologyStatus.UserApproved),
                    "approved group term becomes applicable without session scope");
                reloaded.UpsertMany(new[]
                {
                    new TerminologyCandidate
                    {
                        TermId = "term:conflict",
                        SourceForm = "Milira",
                        NormalizedForm = "milira",
                        Target = "米利拉",
                        Status = TerminologyStatus.Candidate,
                        ScopeKind = TerminologyScope.ModGroup,
                        ScopeId = "milira",
                        SourceScopeKind = TerminologyScope.ModGroup,
                        SourceScopeId = "milira"
                    }
                });
                TerminologyReviewItem conflictReview = reloaded.GetReviewQueue()
                    .Single(item => item.Term.TermId == "term:conflict");
                Assert(conflictReview.HasConflict && conflictReview.ConflictingTargets.Count == 2,
                    "pending term conflicting with an approved term is surfaced for review");
                Assert(reloaded.Reject(otherMilira.TermId) &&
                    !reloaded.GetApplicable("mod.c", "other", string.Empty).Any(),
                    "rejected term is removed from applicable lookup");

                string promptContext = TerminologyPromptContextBuilder.Build(
                    reloaded.GetApplicable("mod.a", "milira", string.Empty),
                    new[] { "The Milira Empire fields new soldiers." },
                    maxTerms: 5,
                    maxCharacters: 500);
                Assert(promptContext.Contains("Milira => 米莉拉"), "only relevant term is injected");
                Assert(!promptContext.Contains("米利拉"), "term from another group is not injected");
                Assert(TerminologyPromptContextBuilder.Build(
                    reloaded.GetApplicable("mod.a", "milira", string.Empty),
                    new[] { "Unrelated colony text." }).Length == 0,
                    "unrelated batches receive no terminology context");

                var aligned = AlignedTranslationMiner.Mine(
                    new[]
                    {
                        new TerminologyAlignedSentencePair { PairId = "p1", PackageId = "mod.a", Source = "Milira Empire", Target = "米莉拉帝国" },
                        new TerminologyAlignedSentencePair { PairId = "p2", PackageId = "mod.b", Source = "Milira Empire", Target = "米莉拉帝国" }
                    },
                    new[] { new TerminologyTrustedAnchor { TermId = "official:milira", Source = "Milira", Target = "米莉拉" } },
                    TerminologyScope.ModGroup,
                    "milira");
                TerminologyCandidate empire = aligned.Single(item => item.NormalizedForm == "empire");
                Assert(empire.Target == "帝国" && empire.Status == TerminologyStatus.SessionActive,
                    "two independent aligned pairs promote Empire to session-active");

                var conflict = AlignedTranslationMiner.Mine(
                    new[]
                    {
                        new TerminologyAlignedSentencePair { PairId = "c1", Source = "Milira Empire", Target = "米莉拉帝国" },
                        new TerminologyAlignedSentencePair { PairId = "c2", Source = "Milira Empire", Target = "米莉拉帝制" }
                    },
                    new[] { new TerminologyTrustedAnchor { Source = "Milira", Target = "米莉拉" } },
                    TerminologyScope.ModGroup,
                    "milira");
                Assert(conflict.All(item => item.Status == TerminologyStatus.Candidate),
                    "conflicting aligned targets remain candidates");

                string sessionPath = Path.Combine(root, "TerminologySessions", "session-a.json");
                var sessionStore = new TerminologySessionStore(sessionPath);
                sessionStore.Save(new TerminologySessionFile
                {
                    SessionId = "session-a",
                    SourceFingerprint = "source-v1",
                    ScopeKind = TerminologyScope.ModGroup,
                    ScopeId = "milira",
                    Corpus = selected,
                    Candidates = candidates
                });
                TerminologySessionFile resumed = sessionStore.Load("session-a", "source-v1");
                Assert(resumed != null && resumed.Corpus.Count == selected.Count && resumed.Candidates.Count == candidates.Count,
                    "matching source fingerprint resumes terminology task");
                Assert(sessionStore.Load("session-a", "source-v2") == null,
                    "changed source fingerprint rejects stale terminology task");

                var requestTerm = new TerminologyCandidate
                {
                    TermId = "term:milira",
                    SourceForm = "Milira",
                    Target = "米莉拉",
                    Status = TerminologyStatus.SessionActive
                };
                TerminologyApplicationValidationResult validApplication = TerminologyApplicationValidator.Validate(
                    new[] { requestTerm },
                    new[] { "Milira soldier {0}" },
                    new[] { "米莉拉士兵 {0}" },
                    new[] { new TerminologyApplication { TermId = "term:milira", SourceForm = "Milira", Target = "米莉拉" } });
                Assert(validApplication.IsValid, "authorized term application with preserved placeholder");
                Assert(TerminologyApplicationValidator.Validate(
                    new[] { requestTerm },
                    new[] { "Milira soldier {0}" },
                    new[] { "米莉拉士兵" },
                    new[] { new TerminologyApplication { TermId = "term:milira", SourceForm = "Milira", Target = "米莉拉" } }).ErrorCode == "placeholder_mismatch",
                    "term application rejects placeholder loss");
                Assert(TerminologyApplicationValidator.Validate(
                    new[] { requestTerm },
                    new[] { "Milira soldier" },
                    new[] { "米莉拉士兵" },
                    new[] { new TerminologyApplication { TermId = "term:unknown", SourceForm = "Milira", Target = "米莉拉" } }).ErrorCode == "unknown_term_id",
                    "term application rejects IDs not supplied in this request");

                Assert(TerminologyAgentResponseParser.TryParse(
                    "{\"decisions\":[{\"termId\":\"term:milira\",\"decision\":\"accept\",\"target\":\"米莉拉\",\"semanticRole\":\"proper_noun\",\"reason\":\"consistent proper name\"}]}",
                    new[] { "term:milira" },
                    out List<TerminologyAgentDecision> agentDecisions) &&
                    agentDecisions.Single().Target == "米莉拉",
                    "strict terminology Agent response parsing");
                Assert(!TerminologyAgentResponseParser.TryParse(
                    "{\"decisions\":[{\"termId\":\"term:invented\",\"decision\":\"accept\",\"target\":\"错误\",\"semanticRole\":\"noun\",\"reason\":\"x\"}]}",
                    new[] { "term:milira" },
                    out _),
                    "terminology Agent cannot invent term IDs");

                Console.WriteLine("PASS: terminology mechanical extraction, scope isolation, and cache self-test");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }

        private static TerminologyCorpusEntry Entry(
            string packageId,
            string groupId,
            string defType,
            string field,
            string text)
        {
            return new TerminologyCorpusEntry
            {
                PackageId = packageId,
                GroupId = groupId,
                DefType = defType,
                Field = field,
                Text = text,
                Key = defType + "." + field,
                SourceKind = "xml"
            };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
