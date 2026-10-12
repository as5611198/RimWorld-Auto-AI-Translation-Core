using AutoTranslator_Core;
using System;
using System.Collections.Generic;
using System.IO;

namespace TranslationUnresolvedManagerSelfTest
{
    internal static class Program
    {
        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "atc-unresolved-" + Guid.NewGuid().ToString("N"));
            try
            {
                AutoTranslatorScanner.TestPackPath = root;
                TranslationUnresolvedManager.BeginRun();

                TranslationUnresolvedEntry entry = CreateEntry("Column");
                TranslationUnresolvedManager.RecordFailure(entry);
                AssertEqual(1, TranslationUnresolvedManager.Count, "Initial pending count");
                AssertTrue(TranslationUnresolvedManager.HasPendingForPackage("example.mod"), "Package pending state");

                TranslationUnresolvedManager.RecordFailure(CreateEntry("Column"));
                List<TranslationUnresolvedEntry> snapshot = TranslationUnresolvedManager.Snapshot();
                AssertEqual(1, snapshot.Count, "Duplicate identity count");
                AssertEqual(2, snapshot[0].Attempts, "Retry attempt count");
                AssertTrue(!string.IsNullOrWhiteSpace(snapshot[0].Id), "Canonical entry ID");
                AssertTrue(!string.IsNullOrWhiteSpace(snapshot[0].SourceHash), "Source fingerprint");

                TranslationUnresolvedManager.ResolveMatching(
                    "example.mod", "DefInjected", "ThingDef", "MF_Column.label", "Column", "Traditional");
                AssertEqual(0, TranslationUnresolvedManager.Count, "Resolved count");

                TranslationUnresolvedManager.BeginRun();
                TranslationUnresolvedManager.RecordFailure(CreateEntry("Column"));
                string ignoredId = TranslationUnresolvedManager.Snapshot()[0].Id;
                AssertTrue(TranslationUnresolvedManager.Ignore(new[] { ignoredId }), "Ignore persistence result");
                AssertEqual(0, TranslationUnresolvedManager.Count, "Ignored count");
                AssertTrue(
                    TranslationUnresolvedManager.ShouldKeepOriginal(
                        "example.mod", "DefInjected", "ThingDef", "MF_Column.label", "Column", "Traditional"),
                    "Matching source fingerprint ignore");
                AssertFalse(
                    TranslationUnresolvedManager.ShouldKeepOriginal(
                        "example.mod", "DefInjected", "ThingDef", "MF_Column.label", "Support column", "Traditional"),
                    "Changed source text must become reviewable");
                AssertFalse(
                    TranslationUnresolvedManager.ShouldKeepOriginal(
                        "example.mod", "DefInjected", "ThingDef", "MF_Column.label", "Column", "Japanese"),
                    "Keep-original decisions must be isolated by target language");

                TranslationUnresolvedManager.BeginRun();
                TranslationUnresolvedEntry japaneseEntry = CreateEntry("Column");
                japaneseEntry.TargetLanguage = "Japanese";
                TranslationUnresolvedManager.RecordFailure(japaneseEntry);
                TranslationUnresolvedManager.BeginRun();
                AssertEqual(1, TranslationUnresolvedManager.Count, "Pending backlog survives a new run");
                TranslationUnresolvedManager.BeginPackageScan("example.mod", "Traditional");
                AssertEqual(1, TranslationUnresolvedManager.Count, "Other-language backlog survives package refresh");
                TranslationUnresolvedManager.AbortPackageScan("example.mod", "Traditional");
                TranslationUnresolvedManager.BeginPackageScan("example.mod", "Japanese");
                AssertEqual(1, TranslationUnresolvedManager.Count, "Current-language backlog survives package start");
                TranslationUnresolvedManager.SaveRunProgress();
                AssertEqual(1, TranslationUnresolvedManager.Count, "Interrupted package refresh preserves backlog");
                string progressJson = File.ReadAllText(Path.Combine(
                    root,
                    "Reports",
                    "TranslationUnresolved",
                    "latest.json"));
                AssertTrue(progressJson.Contains("\"IsComplete\": false"), "Interrupted run remains incomplete");

                TranslationUnresolvedManager.BeginPackageScan("example.mod", "Japanese");
                TranslationUnresolvedManager.CompletePackageScan("example.mod", "Japanese");
                AssertEqual(0, TranslationUnresolvedManager.Count, "Successful package refresh removes unseen backlog");

                TranslationUnresolvedEntry retriedEntry = CreateEntry("Support column");
                TranslationUnresolvedManager.RecordFailure(retriedEntry);
                TranslationUnresolvedManager.BeginPackageScan("example.mod", "Traditional");
                TranslationUnresolvedManager.RecordFailure(CreateEntry("Support column"));
                TranslationUnresolvedManager.CompletePackageScan("example.mod", "Traditional");
                AssertEqual(1, TranslationUnresolvedManager.Count, "Re-seen package failure survives commit");
                AssertEqual(2, TranslationUnresolvedManager.Snapshot()[0].Attempts, "Re-seen failure attempt count");

                TranslationUnresolvedManager.BeginPackageScan("example.mod", "Traditional");
                TranslationUnresolvedManager.MarkPackageScanIncomplete("example.mod", "Traditional");
                TranslationUnresolvedEntry packageFailure = CreateEntry("Defs/ThingDefs.xml");
                packageFailure.Bucket = "Package";
                packageFailure.DefType = string.Empty;
                packageFailure.Key = "__ATC_PACKAGE_FAILURE__";
                packageFailure.Reason = TranslationUnresolvedReasons.SourceFailure;
                packageFailure.Detail = "System.ArgumentOutOfRangeException: injected package fault";
                TranslationUnresolvedManager.RecordFailure(packageFailure);
                TranslationUnresolvedManager.CompletePackageScan("example.mod", "Traditional");
                AssertEqual(2, TranslationUnresolvedManager.Count, "Incomplete package commit preserves backlog and package failure");
                AssertTrue(
                    TranslationUnresolvedManager.Snapshot().Exists(item =>
                        string.Equals(item.Key, "__ATC_PACKAGE_FAILURE__", StringComparison.Ordinal) &&
                        item.Detail.Contains("injected package fault")),
                    "Package failure remains actionable with diagnostic detail");

                TranslationUnresolvedManager.ResolveMatching(
                    "example.mod", "DefInjected", "ThingDef", "MF_Column.label", "Support column", "Traditional");
                TranslationUnresolvedManager.ResolveMatching(
                    "example.mod", "Package", string.Empty, "__ATC_PACKAGE_FAILURE__", "Defs/ThingDefs.xml", "Traditional");

                TranslationUnresolvedManager.CompleteRun();
                AssertTrue(
                    File.Exists(Path.Combine(root, "Reports", "TranslationUnresolved", "latest.json")),
                    "Latest report persistence");
                AssertTrue(
                    File.Exists(Path.Combine(root, "Reports", "TranslationUnresolved", "ignored.json")),
                    "Ignore persistence");

                TestFileLevelFailureClassification();
                TestBatchFaultGuard();

                Console.WriteLine("PASS: unresolved translation manager self-test");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static TranslationUnresolvedEntry CreateEntry(string sourceText)
        {
            return new TranslationUnresolvedEntry
            {
                TargetLanguage = "Traditional",
                PackageId = "example.mod",
                ModName = "Example Mod",
                Bucket = "DefInjected",
                DefType = "ThingDef",
                Key = "MF_Column.label",
                SourceText = sourceText,
                SourceFile = "Defs/ThingDefs.xml",
                TargetFile = "Languages/ChineseTraditional/DefInjected/ThingDef/example.xml",
                Reason = TranslationUnresolvedReasons.EnglishResidual,
                Attempts = 1,
                State = TranslationUnresolvedStates.Pending
            };
        }

        private static void TestFileLevelFailureClassification()
        {
            TranslationUnresolvedEntry saveFailure = CreateEntry("Languages/ChineseTraditional/Keyed/example.xml");
            saveFailure.Key = "__ATC_FILE_SAVE__";
            saveFailure.Reason = TranslationUnresolvedReasons.SaveFailure;
            AssertTrue(
                TranslationUnresolvedManager.IsFileLevelFailure(saveFailure),
                "File save sentinel classification");

            TranslationUnresolvedEntry sourceFailure = CreateEntry("Defs/ThingDefs.xml");
            sourceFailure.Key = "__ATC_SOURCE_FAILURE__";
            sourceFailure.Reason = TranslationUnresolvedReasons.SourceFailure;
            AssertTrue(
                TranslationUnresolvedManager.IsFileLevelFailure(sourceFailure),
                "Source XML sentinel classification");

            TranslationUnresolvedEntry packageFailure = CreateEntry("Defs/ThingDefs.xml");
            packageFailure.Key = "__ATC_PACKAGE_FAILURE__";
            packageFailure.Reason = TranslationUnresolvedReasons.SourceFailure;
            AssertTrue(
                TranslationUnresolvedManager.IsFileLevelFailure(packageFailure),
                "Package failure sentinel classification");

            TranslationUnresolvedEntry ordinaryEntry = CreateEntry("Column");
            ordinaryEntry.Reason = TranslationUnresolvedReasons.SaveFailure;
            AssertFalse(
                TranslationUnresolvedManager.IsFileLevelFailure(ordinaryEntry),
                "Ordinary translation entry remains actionable");
        }

        private static void TestBatchFaultGuard()
        {
            Dictionary<string, TranslationBatchItemResult> results =
                new Dictionary<string, TranslationBatchItemResult>(StringComparer.Ordinal)
                {
                    { "already-complete", new TranslationBatchItemResult { Value = "done" } }
                };
            TranslationBatchFaultGuard.RunChunkAsync(
                new[] { "already-complete", "failed-a", "failed-b" },
                results,
                TranslationUnresolvedReasons.ApiFailure,
                () => throw new InvalidOperationException("injected chunk fault"),
                null).GetAwaiter().GetResult();

            AssertEqual(3, results.Count, "Fault guard result count");
            AssertTrue(results["already-complete"].IsSuccess, "Fault guard preserves completed entries");
            AssertEqual(
                TranslationUnresolvedReasons.ApiFailure,
                results["failed-a"].FailureReason,
                "Fault guard first failed input");
            AssertEqual(
                TranslationUnresolvedReasons.ApiFailure,
                results["failed-b"].FailureReason,
                "Fault guard second failed input");

            List<TranslationBatchItemResult> ordered = TranslationBatchFaultGuard.CreateOrderedResults(
                new[] { "failed-a", "already-complete", "failed-b", "failed-a", "missing" },
                results,
                TranslationUnresolvedReasons.ApiFailure,
                "missing result");
            AssertEqual(5, ordered.Count, "Ordered batch result count");
            AssertTrue(ordered[1].IsSuccess, "Ordered batch preserves success position");
            AssertEqual(
                TranslationUnresolvedReasons.ApiFailure,
                ordered[4].FailureReason,
                "Ordered batch fills every missing input");
        }

        private static void AssertTrue(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message + " should be true.");
        }

        private static void AssertFalse(bool value, string message)
        {
            if (value) throw new InvalidOperationException(message + " should be false.");
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    message + ": expected <" + expected + ">, actual <" + actual + ">.");
            }
        }
    }
}
