using AutoTranslator_Core;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TranslationXmlAtomicFileStoreSelfTest
{
    internal static class Program
    {
        private static int _passed;

        private static int Main()
        {
            try
            {
                RunTest("multi-file rollback and concurrent writer", TestTransaction);
                RunTest("short temporary path and atomic replacement", TestShortTemporaryPathAndAtomicReplacement);
                RunTest("failed save is isolated", TestFailedSaveIsIsolated);
                RunTest("near MAX_PATH destination and failed write preserve committed data", TestNearMaxPath);
                Console.WriteLine("PASS: " + _passed + " translation XML atomic file-store self-tests");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
        }

        private static void TestTransaction()
        {
            string root = CreateTestRoot();
            try
            {
                string first = Path.Combine(root, "first.xml"), second = Path.Combine(root, "second.xml");
                File.WriteAllText(first, "original"); File.WriteAllText(second, "second");
                using (var tx = new TranslationFileTransaction(Path.Combine(root, "backup")).CaptureWrites())
                {
                    TranslationXmlAtomicFileStore.Save(first, s => WriteText(s, "new"));
                    TranslationXmlAtomicFileStore.SaveIfCurrent(second, s => WriteText(s, "new2"), () => false);
                    AssertEqual("original", File.ReadAllText(first), "Staging cannot publish XML before provenance");
                    bool failed = false;
                    try { tx.Commit(() => true); } catch (OperationCanceledException) { failed = true; }
                    AssertTrue(failed, "Second file commit failure must propagate");
                    AssertEqual("original", File.ReadAllText(first), "First file rolls back on second file failure");
                }
                using (var tx = new TranslationFileTransaction(Path.Combine(root, "backup")).CaptureWrites())
                {
                    TranslationXmlAtomicFileStore.Save(first, s => WriteText(s, "new"));
                    TranslationXmlAtomicFileStore.SaveIfCurrent(second, s => WriteText(s, "new2"), () => {
                        File.WriteAllText(first, "external edit"); return false;
                    });
                    bool recovery = false;
                    try { tx.Commit(() => true); } catch (IOException) { recovery = true; }
                    AssertTrue(recovery, "Conflicting rollback reports retained recovery material");
                    AssertEqual("external edit", File.ReadAllText(first), "Rollback cannot overwrite newer writer");
                }
                using (var tx = new TranslationFileTransaction(Path.Combine(root, "backup")).CaptureWrites())
                {
                    TranslationXmlAtomicFileStore.SaveIfCurrent(second, s => WriteText(s, "one"), () => true);
                    TranslationXmlAtomicFileStore.SaveIfCurrent(second, s => WriteText(s, "two"), () => false);
                    bool rejected = false;
                    try { tx.Commit(() => true); } catch (OperationCanceledException) { rejected = true; }
                    AssertTrue(rejected, "Repeated staging must preserve the later cancellation guard");
                    AssertEqual("second", File.ReadAllText(second), "Rejected staged update retains original");
                }
                AssertTrue(TranslationFileTransaction.Current == null, "Ambient transaction is released after failures");
                using (var tx = new TranslationFileTransaction(Path.Combine(root, "backup")).CaptureWrites())
                {
                    tx.Observe(first);
                    File.WriteAllText(first, "changed after read");
                    bool rejected = false;
                    try { TranslationXmlAtomicFileStore.Save(first, s => WriteText(s, "stale merge")); }
                    catch (IOException) { rejected = true; }
                    AssertTrue(rejected, "Staging rejects a source changed after observation");
                    AssertEqual("changed after read", File.ReadAllText(first), "Source race retains the newer version");
                }
                using (var tx = new TranslationFileTransaction(Path.Combine(root, "backup")).CaptureWrites())
                {
                    string created = Path.Combine(root, "created.xml");
                    tx.Observe(created);
                    File.WriteAllText(created, "concurrent create");
                    bool rejected = false;
                    try { TranslationXmlAtomicFileStore.Save(created, s => WriteText(s, "stale create")); }
                    catch (IOException) { rejected = true; }
                    AssertTrue(rejected, "Observed missing destination cannot replace a concurrent creation");
                    AssertEqual("concurrent create", File.ReadAllText(created), "Concurrent creation remains intact");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static void TestShortTemporaryPathAndAtomicReplacement()
        {
            string root = CreateTestRoot();
            try
            {
                string destination = CreateLongDestinationPath(root, "translations.xml", 225);
                string temporary = TranslationXmlAtomicFileStore.CreateTemporaryPath(destination);
                AssertTrue(destination.Length >= 225, "Destination must reproduce the long generated-pack path");
                AssertTrue(temporary.Length < 260, "Short temporary path must stay below legacy MAX_PATH");
                AssertTrue(!Path.GetFileName(temporary).Contains(Path.GetFileName(destination)), "Temporary filename must not repeat destination filename");

                TranslationXmlAtomicFileStore.Save(destination, stream => WriteText(stream, "first"));
                TranslationXmlAtomicFileStore.Save(destination, stream => WriteText(stream, "second"));

                AssertEqual("second", File.ReadAllText(destination), "Atomic replacement must publish the latest content");
                AssertTrue(!Directory.GetFiles(Path.GetDirectoryName(destination), "*.tmp").Any(), "Temporary files must be cleaned up");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void TestFailedSaveIsIsolated()
        {
            string root = CreateTestRoot();
            try
            {
                string blockedDirectory = Path.Combine(root, "blocked");
                File.WriteAllText(blockedDirectory, "not a directory");
                int errors = 0;

                bool failed = TranslationXmlAtomicFileStore.TrySave(
                    () => TranslationXmlAtomicFileStore.Save(Path.Combine(blockedDirectory, "broken.xml"), stream => WriteText(stream, "broken")),
                    ex => errors++);
                string nextFile = Path.Combine(root, "next.xml");
                bool succeeded = TranslationXmlAtomicFileStore.TrySave(
                    () => TranslationXmlAtomicFileStore.Save(nextFile, stream => WriteText(stream, "continues")),
                    ex => errors++);

                AssertTrue(!failed, "A single unwritable translation file must report failure");
                AssertTrue(succeeded, "The next translation file must still be saved");
                AssertEqual(1, errors, "Exactly one save error must be reported");
                AssertEqual("continues", File.ReadAllText(nextFile), "Subsequent translation output must remain intact");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string CreateTestRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "atc-atomic-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void TestNearMaxPath()
        {
            string root = CreateTestRoot();
            try
            {
                string destination = CreateLongDestinationPath(root, "ManualTranslation.xml", 252);
                string temporary = TranslationXmlAtomicFileStore.CreateTemporaryPath(destination);
                AssertTrue(temporary.Length <= destination.Length, "Temporary path must not exceed the valid destination length; temp=" + temporary.Length + ", destination=" + destination.Length);
                TranslationXmlAtomicFileStore.Save(destination, stream => WriteText(stream, "original"));
                bool saved = TranslationXmlAtomicFileStore.TrySave(() => TranslationXmlAtomicFileStore.Save(destination, stream =>
                {
                    WriteText(stream, "partial");
                    throw new IOException("simulated interrupted writer");
                }), null);
                AssertTrue(!saved, "Interrupted writer must fail");
                AssertEqual("original", File.ReadAllText(destination), "Interrupted writer must preserve committed file");
                TranslationXmlAtomicFileStore.Save(destination, stream => WriteText(stream, "replacement"));
                AssertEqual("replacement", File.ReadAllText(destination), "Near-limit replacement must commit");
                AssertTrue(Directory.GetFiles(Path.GetDirectoryName(destination)).Length == 1, "No temporary or backup files remain");
            }
            finally { Directory.Delete(root, true); }
        }

        private static string CreateLongDestinationPath(string root, string fileName, int minimumLength)
        {
            string directory = root;
            while (Path.Combine(directory, fileName).Length < minimumLength)
            {
                int remaining = minimumLength - Path.Combine(directory, fileName).Length;
                directory = Path.Combine(directory, new string('d', Math.Min(30, Math.Max(1, remaining))));
            }

            Directory.CreateDirectory(directory);
            return Path.Combine(directory, fileName);
        }

        private static void WriteText(Stream stream, string text)
        {
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
            {
                writer.Write(text);
            }
        }

        private static void RunTest(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS: " + name);
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!Equals(expected, actual))
                throw new InvalidOperationException(message + "; expected=" + expected + ", actual=" + actual);
        }
    }
}
