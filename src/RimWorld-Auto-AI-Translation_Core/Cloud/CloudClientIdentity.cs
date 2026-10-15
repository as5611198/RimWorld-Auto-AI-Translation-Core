using System;
using System.IO;
using Verse;

namespace AutoTranslator_Core
{
    internal static class CloudClientIdentity
    {
#if RIMWORLD_1_7
        private static readonly Lazy<string> Identity = new Lazy<string>(() =>
            GetOrCreate(Path.Combine(GenFilePaths.ConfigFolderPath, "ATC_CloudClientId.txt")));

        internal static string Value => Identity.Value;

        internal static string GetOrCreate(string path)
        {
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(existing)) return existing;
            }

            string created = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // A competing process must use the winning identity, never overwrite it.
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, created);
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(existing)) return existing;
                throw;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return created;
        }
#else
        // Keep the identity of existing 1.5/1.6 cloud uploads unchanged.
        internal static string Value => UnityEngine.SystemInfo.deviceUniqueIdentifier;
#endif
    }
}
