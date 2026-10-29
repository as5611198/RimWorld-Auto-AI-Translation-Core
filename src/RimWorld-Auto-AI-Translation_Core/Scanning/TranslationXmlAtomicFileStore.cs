using System;
using System.IO;

namespace AutoTranslator_Core
{
    internal static class TranslationXmlAtomicFileStore
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> CommitGates =
            new System.Collections.Concurrent.ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static object Gate(string path) => CommitGates.GetOrAdd(Path.GetFullPath(path), _ => new object());

        internal static void Save(string path, Action<Stream> write)
        {
            SaveIfCurrent(path, write, null);
        }

        internal static void SaveIfCurrent(string path, Action<Stream> write, Func<bool> canCommit)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Translation XML path is empty.", nameof(path));
            if (write == null) throw new ArgumentNullException(nameof(write));

            if (TranslationFileTransaction.Current != null)
            {
                using (var staged = new MemoryStream())
                {
                    write(staged);
                    TranslationFileTransaction.Current.Stage(path, staged.ToArray(), canCommit);
                    return;
                }
            }
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidDataException("Translation XML path has no parent directory.");

            Directory.CreateDirectory(directory);
            string tempPath = null;

            try
            {
                using (var stream = OpenTemporaryFile(fullPath, out tempPath))
                {
                    write(stream);
                    stream.Flush(true);
                }

                lock (Gate(path))
                {
                    if (canCommit != null && !canCommit())
                        throw new OperationCanceledException("Stale translation transaction; destination retained.");
                    if (File.Exists(fullPath))
                    {
                        try
                        {
                            File.Replace(tempPath, fullPath, null, true);
                        }
                        catch (PlatformNotSupportedException)
                        {
                            ReplaceWithRollback(tempPath, fullPath);
                        }
                        catch (NotSupportedException)
                        {
                            ReplaceWithRollback(tempPath, fullPath);
                        }
                    }
                    else
                    {
                        File.Move(tempPath, fullPath);
                    }
                }
            }
            finally
            {
                if (tempPath != null && File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        internal static void DeleteIfCurrent(string path, Func<bool> canCommit)
        {
            if (TranslationFileTransaction.Current != null) { TranslationFileTransaction.Current.Stage(path, null, canCommit); return; }
            lock (Gate(path))
            {
                if (!canCommit()) throw new OperationCanceledException("Stale delete transaction.");
                if (File.Exists(path)) File.Delete(path);
            }
        }

        internal static bool TrySave(Action save, Action<Exception> onFailure)
        {
            try
            {
                save();
                return true;
            }
            catch (Exception ex)
            {
                onFailure?.Invoke(ex);
                return false;
            }
        }

        // An 8.3 random name fits wherever ManualTranslation.xml fits, including
        // legacy Windows paths near MAX_PATH. Keep staging on the same volume.
        internal static string CreateTemporaryPath(string destinationPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            return Path.Combine(directory, Path.ChangeExtension(Path.GetRandomFileName(), "tmp"));
        }

        private static FileStream OpenTemporaryFile(string destinationPath, out string ownedPath)
        {
            ownedPath = null;
            for (int attempt = 0; ; attempt++)
            {
                string candidate = CreateTemporaryPath(destinationPath);
                FileStream stream;
                try
                {
                    stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                }
                catch (IOException) when (attempt < 9 && File.Exists(candidate))
                {
                    continue; // Never overwrite or clean up another writer's file.
                }
                ownedPath = candidate;
                return stream;
            }
        }

        private static void ReplaceWithRollback(string tempPath, string destinationPath)
        {
            string backupPath = Path.ChangeExtension(CreateTemporaryPath(destinationPath), "bak");
            File.Move(destinationPath, backupPath);
            try
            {
                File.Move(tempPath, destinationPath);
                File.Delete(backupPath);
            }
            catch
            {
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                if (File.Exists(backupPath)) File.Move(backupPath, destinationPath);
                throw;
            }
        }
    }
}
