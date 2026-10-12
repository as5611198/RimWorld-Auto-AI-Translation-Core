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
                RunTest("short temporary path and atomic replacement", TestShortTemporaryPathAndAtomicReplacement);
                RunTest("failed save is isolated", TestFailedSaveIsIsolated);
                Console.WriteLine("PASS: " + _passed + " translation XML atomic file-store self-tests");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
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
