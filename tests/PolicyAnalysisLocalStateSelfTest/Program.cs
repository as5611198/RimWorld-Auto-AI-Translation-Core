using System;
using System.Collections.Generic;
using System.IO;
using AutoTranslator_Core;

internal static class Program
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "atc-policy-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "state.json");
            var store = new PolicyAnalysisLocalStateStore(path);
            store.RecordAccelerated(new PolicyAnalysisCloudRecord
            {
                PackageId = "Author.Mod",
                CandidateCount = 4,
                AllowedCandidateIds = new List<string> { "tpc_b", "tpc_a", "tpc_a" }
            });
            PolicyAnalysisLocalState accelerated = store.Get("author.mod");
            Assert(accelerated.Status == PolicyAnalysisLocalStateStore.AcceleratedStatus, "accelerated status missing");
            Assert(accelerated.CloudAllowedCount == 2, "cloud IDs were not deduplicated");

            store.RecordPending(new PolicyAnalysisContribution
            {
                PackageId = "AUTHOR.MOD",
                CandidateCount = 4,
                ContributionId = "retry-id",
                AddAllowedCandidateIds = new List<string> { "tpc_c", "tpc_c", "tpc_a", "" }
            });
            PolicyAnalysisLocalState pending = new PolicyAnalysisLocalStateStore(path).Get("author.mod");
            Assert(pending.Status == PolicyAnalysisLocalStateStore.PendingUploadStatus, "pending state was not persisted");
            Assert(pending.PendingAllowedCandidateIds.Count == 2, "pending IDs were not normalized");
            Assert(pending.PendingContributionId == "retry-id", "idempotency ID was not persisted");

            store.RecordAccelerated(new PolicyAnalysisCloudRecord
            {
                CandidateDomain = PolicyAnalysisCandidateDomain.Dll,
                PackageId = "Author.Mod",
                CandidateCount = 2,
                AllowedCandidateIds = new List<string> { "hardcoded-ui:a" }
            });
            PolicyAnalysisLocalState dll = store.Get("author.mod", PolicyAnalysisCandidateDomain.Dll);
            Assert(dll != null && dll.CandidateDomain == PolicyAnalysisCandidateDomain.Dll,
                "DLL domain state was not persisted");
            Assert(store.Get("author.mod").CandidateDomain == PolicyAnalysisCandidateDomain.Xml,
                "DLL state overwrote XML state");

            string legacyPath = Path.Combine(root, "legacy-state.json");
            File.WriteAllText(legacyPath,
                "{\"SchemaVersion\":1,\"Records\":{\"legacy.mod\":{\"PackageId\":\"legacy.mod\",\"Status\":\"accelerated\",\"PendingAllowedCandidateIds\":[]}}}");
            PolicyAnalysisLocalState legacy = new PolicyAnalysisLocalStateStore(legacyPath).Get("legacy.mod");
            Assert(legacy != null && legacy.CandidateDomain == PolicyAnalysisCandidateDomain.Xml,
                "schema-v1 XML state was not migrated");
            Assert(File.ReadAllText(legacyPath).Contains("\"SchemaVersion\": 2"),
                "schema-v1 state file was not upgraded to v2");

            store.MarkUploaded("author.mod");
            PolicyAnalysisLocalState uploaded = store.Get("author.mod");
            Assert(uploaded.Status == PolicyAnalysisLocalStateStore.UploadedStatus, "uploaded status missing");
            Assert(uploaded.PendingAllowedCandidateIds.Count == 0, "pending IDs survived successful upload");
            Console.WriteLine("PolicyAnalysisLocalStateSelfTest: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

namespace AutoTranslator_Core
{
    internal static class AutoTranslatorScanner
    {
        public static string GetLocalPackPath() => Path.GetTempPath();
    }
}
