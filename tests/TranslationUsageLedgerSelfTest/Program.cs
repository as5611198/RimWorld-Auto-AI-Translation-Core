using AutoTranslator_Core;
using System;
using System.Collections.Generic;
using System.IO;

namespace TranslationUsageLedgerSelfTest
{
    internal static class Program
    {
        private static int _passed;

        private static int Main()
        {
            string directory = Path.Combine(Path.GetTempPath(), "atc-usage-ledger-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                Run("reserve and reconcile actual usage", () => TestReserveAndCommit(directory));
                Run("budget denial pauses without discarding data", () => TestBudgetPause(directory));
                Run("actual provider usage latches the budget stop", () => TestActualUsagePause(directory));
                Run("in-flight work becomes ambiguous after restart", () => TestCrashRecovery(directory));
                Run("increased budget resumes the same run", () => TestBudgetIncreaseResume(directory));
                Run("completed run is not silently resumed", () => TestCompletedRunStartsFresh(directory));
                Run("request coordinator reconciles provider usage", () => TestCoordinatorUsage(directory));
                Run("unsent request releases reservation", () => TestCoordinatorUnsent(directory));
                Run("HTTP rejection releases reservation", () => TestCoordinatorHttpRejection(directory));
                Run("network uncertainty blocks automatic replay", () => TestCoordinatorAmbiguous(directory));
                Console.WriteLine("PASS: " + _passed + " translation usage ledger self-tests");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static void TestReserveAndCommit(string directory)
        {
            string path = NewPath(directory);
            TranslationUsageLedger ledger = TranslationUsageLedger.OpenOrCreate(path, "run-a", 1000, 1000);
            AssertTrue(ledger.TryReserve("r1", "mod.a", "translation", "DeepSeek", "flash", 100, 200, out _), "reserve");
            ledger.Commit("r1", 80, 90, 10, 180);
            TranslationUsageSnapshot snapshot = ledger.GetSnapshot();
            AssertEqual(100L, snapshot.CommittedSourceCharacters, "committed characters");
            AssertEqual(180L, snapshot.AccountedTokens, "actual tokens replace estimate");
            AssertEqual(1, snapshot.CommittedRequests, "committed requests");
        }

        private static void TestBudgetPause(string directory)
        {
            string path = NewPath(directory);
            TranslationUsageLedger ledger = TranslationUsageLedger.OpenOrCreate(path, "run-b", 100, 100);
            AssertTrue(ledger.TryReserve("r1", "mod", "translation", "p", "m", 60, 60, out _), "first reserve");
            ledger.Commit("r1", null, null, null, null);
            AssertFalse(ledger.TryReserve("r2", "mod", "translation", "p", "m", 50, 10, out string reason), "over-budget reserve");
            AssertEqual("source_character_budget", reason, "denial reason");
            AssertFalse(ledger.TryReserve("r3", "mod", "translation", "p", "m", 1, 1, out reason),
                "a queued smaller request revived a paused run");
            AssertEqual("run_paused_by_budget", reason, "latched pause reason");
            TranslationUsageSnapshot snapshot = ledger.GetSnapshot();
            AssertEqual(TranslationUsageRunStatus.PausedByBudget, snapshot.Status, "paused status");
            AssertEqual(60L, snapshot.CommittedSourceCharacters, "prior data retained");
        }

        private static void TestCrashRecovery(string directory)
        {
            string path = NewPath(directory);
            TranslationUsageLedger first = TranslationUsageLedger.OpenOrCreate(path, "run-c", 1000, 1000);
            AssertTrue(first.TryReserve("r1", "mod", "translation", "p", "m", 10, 20, out _), "reserve");
            TranslationUsageLedger recovered = TranslationUsageLedger.OpenOrCreate(path, "run-c", 1000, 1000);
            AssertTrue(recovered.WasResumed, "resume marker");
            TranslationUsageSnapshot snapshot = recovered.GetSnapshot();
            AssertEqual(1, snapshot.AmbiguousRequests, "ambiguous count");
            AssertFalse(recovered.TryReserve("r1", "mod", "translation", "p", "m", 10, 20, out string reason), "no automatic replay");
            AssertEqual("ambiguous_or_in_flight", reason, "replay reason");
        }

        private static void TestActualUsagePause(string directory)
        {
            string path = NewPath(directory);
            TranslationUsageLedger ledger = TranslationUsageLedger.OpenOrCreate(path, "run-actual", 1000, 100);
            AssertTrue(ledger.TryReserve("r1", "mod", "translation", "p", "m", 10, 80, out _), "reserve");
            ledger.Commit("r1", 60, 60, null, 120);
            AssertEqual(TranslationUsageRunStatus.PausedByBudget, ledger.GetSnapshot().Status,
                "actual usage above the estimate did not pause");
            AssertFalse(ledger.TryReserve("r2", "mod", "translation", "p", "m", 1, 1, out _),
                "request accepted after actual usage crossed the limit");
        }

        private static void TestBudgetIncreaseResume(string directory)
        {
            string path = NewPath(directory);
            TranslationUsageLedger first = TranslationUsageLedger.OpenOrCreate(path, "run-d", 10, 10);
            AssertFalse(first.TryReserve("too-large", "mod", "translation", "p", "m", 20, 20, out _), "initial denial");
            TranslationUsageLedger resumed = TranslationUsageLedger.OpenOrCreate(path, "run-d", 100, 100);
            AssertTrue(resumed.TryReserve("now-allowed", "mod", "translation", "p", "m", 20, 20, out _), "resumed reserve");
            AssertEqual(TranslationUsageRunStatus.Active, resumed.GetSnapshot().Status, "active after increase");
        }

        private static void TestCompletedRunStartsFresh(string directory)
        {
            string path = NewPath(directory);
            TranslationUsageLedger first = TranslationUsageLedger.OpenOrCreate(path, "run-e", 100, 100);
            AssertTrue(first.TryReserve("old", "mod", "translation", "p", "m", 10, 10, out _), "old reserve");
            first.Commit("old", 5, 5, 0, 10);
            first.Complete();
            TranslationUsageLedger next = TranslationUsageLedger.OpenOrCreate(path, "run-e", 100, 100);
            AssertEqual(0, next.GetSnapshot().CommittedRequests, "fresh completed run");
        }

        private static void TestCoordinatorUsage(string directory)
        {
            string path = NewPath(directory);
            TranslationUsageCoordinator.BeginRun(path, "coordinator-a", 1000, 10000);
            using (TranslationUsageCoordinator.PushRequestContext("mod.a", "translation", "file-a", 25))
            {
                AssertTrue(TranslationUsageCoordinator.TryReserve(
                    TranslatorProvider.DeepSeek,
                    "{\"model\":\"deepseek-v4-flash\",\"max_tokens\":100}",
                    out TranslationUsageReservationHandle handle,
                    out string reason), reason);
                TranslationUsageCoordinator.Reconcile(
                    handle,
                    true,
                    200,
                    true,
                    "{\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":8,\"total_tokens\":20}}");
            }
            TranslationUsageSnapshot snapshot = TranslationUsageCoordinator.GetSnapshot();
            AssertEqual(25L, snapshot.CommittedSourceCharacters, "coordinator characters");
            AssertEqual(20L, snapshot.AccountedTokens, "coordinator actual tokens");
            TranslationUsageCoordinator.EndRun(true);
        }

        private static void TestCoordinatorHttpRejection(string directory)
        {
            string path = NewPath(directory);
            TranslationUsageCoordinator.BeginRun(path, "coordinator-b", 1000, 10000);
            using (TranslationUsageCoordinator.PushRequestContext("mod", "translation", "file", 25))
            {
                AssertTrue(TranslationUsageCoordinator.TryReserve(
                    TranslatorProvider.DeepSeek,
                    "{\"model\":\"m\",\"max_tokens\":100}",
                    out TranslationUsageReservationHandle handle,
                    out _), "reserve");
                TranslationUsageCoordinator.Reconcile(handle, true, 401, false, "{}");
            }
            TranslationUsageSnapshot snapshot = TranslationUsageCoordinator.GetSnapshot();
            AssertEqual(0, snapshot.InFlightRequests, "released in-flight count");
            AssertEqual(0, snapshot.CommittedRequests, "released committed count");
            TranslationUsageCoordinator.EndRun(false);
        }

        private static void TestCoordinatorUnsent(string directory)
        {
            string path = NewPath(directory);
            TranslationUsageCoordinator.BeginRun(path, "coordinator-unsent", 1000, 10000);
            using (TranslationUsageCoordinator.PushRequestContext("mod", "translation", "file", 25))
            {
                AssertTrue(TranslationUsageCoordinator.TryReserve(
                    TranslatorProvider.DeepSeek,
                    "{\"model\":\"m\",\"max_tokens\":100}",
                    out TranslationUsageReservationHandle handle,
                    out _), "reserve");
                TranslationUsageCoordinator.Reconcile(handle, false, 0, false, string.Empty);
            }
            TranslationUsageSnapshot snapshot = TranslationUsageCoordinator.GetSnapshot();
            AssertEqual(0, snapshot.InFlightRequests, "unsent in-flight count");
            AssertEqual(0, snapshot.CommittedRequests, "unsent committed count");
            AssertEqual(0, snapshot.AmbiguousRequests, "unsent ambiguous count");
            TranslationUsageCoordinator.EndRun(false);
        }

        private static void TestCoordinatorAmbiguous(string directory)
        {
            string path = NewPath(directory);
            const string payload = "{\"model\":\"m\",\"max_tokens\":100}";
            TranslationUsageCoordinator.BeginRun(path, "coordinator-c", 1000, 10000);
            using (TranslationUsageCoordinator.PushRequestContext("mod", "translation", "file", 25))
            {
                AssertTrue(TranslationUsageCoordinator.TryReserve(
                    TranslatorProvider.DeepSeek,
                    payload,
                    out TranslationUsageReservationHandle handle,
                    out _), "reserve");
                TranslationUsageCoordinator.Reconcile(handle, true, 0, false, string.Empty);
                AssertFalse(TranslationUsageCoordinator.TryReserve(
                    TranslatorProvider.DeepSeek,
                    payload,
                    out _,
                    out string reason), "automatic replay");
                AssertEqual("ambiguous_or_in_flight", reason, "ambiguous reason");
            }
            AssertEqual(1, TranslationUsageCoordinator.GetSnapshot().AmbiguousRequests, "ambiguous request count");
            TranslationUsageCoordinator.EndRun(false);
        }

        private static string NewPath(string directory)
        {
            return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        }

        private static void Run(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS: " + name);
        }

        private static void AssertTrue(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        private static void AssertFalse(bool value, string message)
        {
            if (value) throw new InvalidOperationException(message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(message + ": expected " + expected + ", actual " + actual);
        }
    }
}
