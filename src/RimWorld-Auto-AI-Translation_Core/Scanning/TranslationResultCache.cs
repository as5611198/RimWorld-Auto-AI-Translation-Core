using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AutoTranslator_Core
{
    internal sealed class TranslationResultCache
    {
        internal sealed class CacheEntry
        {
            public string Key { get; set; }
            public string PackageId { get; set; }
            public string TargetLanguage { get; set; }
            public string SourceHash { get; set; }
            public string Translation { get; set; }
            public string CreatedUtc { get; set; }
            public string LastUsedUtc { get; set; }
        }

        private sealed class CacheFile
        {
            public int Version { get; set; }
            public Dictionary<string, CacheEntry> Entries { get; set; }
        }

        private const int CurrentVersion = 1;
        private const int MaximumEntries = 100000;
        private const long MaximumFileBytes = 64L * 1024L * 1024L;
        private const string TranslationContractVersion = "stable-id-v2";
        private readonly object _gate = new object();
        private readonly string _path;
        private Dictionary<string, CacheEntry> _entries;
        private bool _loaded;
        private bool _dirty;

        internal TranslationResultCache(string path)
        {
            _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        }

        internal bool TryGet(
            string packageId,
            string targetLanguage,
            string source,
            out string translation)
        {
            translation = string.Empty;
            string key = CreateKey(packageId, targetLanguage, source);
            lock (_gate)
            {
                EnsureLoadedLocked();
                if (!_entries.TryGetValue(key, out CacheEntry entry) ||
                    entry == null || string.IsNullOrWhiteSpace(entry.Translation))
                {
                    return false;
                }

                entry.LastUsedUtc = DateTime.UtcNow.ToString("o");
                _dirty = true;
                translation = entry.Translation;
                return true;
            }
        }

        internal void PutRange(
            string packageId,
            string targetLanguage,
            IEnumerable<KeyValuePair<string, string>> values)
        {
            List<KeyValuePair<string, string>> safe = (values ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToList();
            if (safe.Count == 0) return;

            lock (_gate)
            {
                EnsureLoadedLocked();
                string now = DateTime.UtcNow.ToString("o");
                foreach (KeyValuePair<string, string> pair in safe)
                {
                    string key = CreateKey(packageId, targetLanguage, pair.Key);
                    if (_entries.TryGetValue(key, out CacheEntry existing) && existing != null)
                    {
                        // First accepted value wins within the same source identity. This
                        // prevents concurrent chunks from introducing inconsistent terms.
                        existing.LastUsedUtc = now;
                        continue;
                    }

                    _entries[key] = new CacheEntry
                    {
                        Key = key,
                        PackageId = Normalize(packageId),
                        TargetLanguage = Normalize(targetLanguage),
                        SourceHash = ComputeSha256(pair.Key ?? string.Empty),
                        Translation = pair.Value,
                        CreatedUtc = now,
                        LastUsedUtc = now
                    };
                }

                PruneLocked();
                _dirty = true;
                FlushLocked();
            }
        }

        internal void Flush()
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                FlushLocked();
            }
        }

        internal void Clear()
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                _entries.Clear();
                _dirty = true;
                FlushLocked();
            }
        }

        internal static string CreateKey(string packageId, string targetLanguage, string source)
        {
            return "tr_" + ComputeSha256(string.Join("\n", new[]
            {
                TranslationContractVersion,
                Normalize(packageId),
                Normalize(targetLanguage),
                source ?? string.Empty
            }));
        }

        private void EnsureLoadedLocked()
        {
            if (_loaded) return;
            _loaded = true;
            _entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            try
            {
                FileInfo info = new FileInfo(_path);
                if (!info.Exists || info.Length <= 0L || info.Length > MaximumFileBytes) return;
                CacheFile file = JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(_path));
                if (file == null || file.Version != CurrentVersion || file.Entries == null) return;
                _entries = new Dictionary<string, CacheEntry>(file.Entries, StringComparer.Ordinal);
                PruneLocked();
            }
            catch
            {
                _entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            }
        }

        private void PruneLocked()
        {
            if (_entries.Count <= MaximumEntries) return;
            int removeCount = _entries.Count - MaximumEntries;
            foreach (string key in _entries
                .OrderBy(pair => pair.Value?.LastUsedUtc ?? string.Empty, StringComparer.Ordinal)
                .Take(removeCount)
                .Select(pair => pair.Key)
                .ToList())
            {
                _entries.Remove(key);
            }
        }

        private void FlushLocked()
        {
            if (!_dirty) return;
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temp = _path + ".tmp";
            string backup = _path + ".bak";
            string json = JsonConvert.SerializeObject(new CacheFile
            {
                Version = CurrentVersion,
                Entries = _entries
            }, Formatting.Indented);
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(_path))
            {
                try
                {
                    File.Replace(temp, _path, backup, true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(temp, _path, true);
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    File.Copy(temp, _path, true);
                    File.Delete(temp);
                }
            }
            else
            {
                File.Move(temp, _path);
            }
            _dirty = false;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string ComputeSha256(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder builder = new StringBuilder(bytes.Length * 2);
                foreach (byte item in bytes) builder.Append(item.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
