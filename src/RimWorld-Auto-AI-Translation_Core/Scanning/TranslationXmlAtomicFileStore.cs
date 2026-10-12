using System;
using System.IO;

namespace AutoTranslator_Core
{
    internal static class TranslationXmlAtomicFileStore
    {
        private static readonly object CommitLock = new object();

        internal static void Save(string path, Action<Stream> write)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Translation XML path is empty.", nameof(path));
            if (write == null) throw new ArgumentNullException(nameof(write));

            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidDataException("Translation XML path has no parent directory.");

            Directory.CreateDirectory(directory);
            string tempPath = CreateTemporaryPath(fullPath);

            try
            {
                using (var stream = new FileStream(
                           tempPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    write(stream);
                    stream.Flush(true);
                }

                lock (CommitLock)
                {
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
                if (File.Exists(tempPath)) File.Delete(tempPath);
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

        // Keep the temporary name independent of the destination filename so legacy .NET paths stay below MAX_PATH.
        internal static string CreateTemporaryPath(string destinationPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            return Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        }

        private static void ReplaceWithRollback(string tempPath, string destinationPath)
        {
            string directory = Path.GetDirectoryName(destinationPath);
            string backupPath = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".bak");
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
