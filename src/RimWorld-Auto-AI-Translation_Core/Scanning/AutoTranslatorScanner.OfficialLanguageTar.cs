using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private const string OfficialTarKeyedCategory = "Keyed";
        private static readonly object OfficialTarCacheLock = new object();
        private static readonly Dictionary<string, OfficialTarIndexCacheEntry> OfficialTarIndexCache =
            new Dictionary<string, OfficialTarIndexCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, string>> OfficialTarXmlCache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public class OfficialTarTranslationFile
        {
            public string TarPath;
            public string EntryName;
            public string Category;
        }

        private class OfficialTarIndexCacheEntry
        {
            public long Length;
            public long LastWriteTicks;
            public List<OfficialTarTranslationFile> Files;
        }

        public static bool IsOfficialBaseGameOrDlcPackage(string packageId)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return false;

            return packageId.Equals("ludeon.rimworld", StringComparison.OrdinalIgnoreCase) ||
                   packageId.Equals("ludeon.rimworld.royalty", StringComparison.OrdinalIgnoreCase) ||
                   packageId.Equals("ludeon.rimworld.ideology", StringComparison.OrdinalIgnoreCase) ||
                   packageId.Equals("ludeon.rimworld.biotech", StringComparison.OrdinalIgnoreCase) ||
                   packageId.Equals("ludeon.rimworld.anomaly", StringComparison.OrdinalIgnoreCase) ||
                   packageId.Equals("ludeon.rimworld.odyssey", StringComparison.OrdinalIgnoreCase);
        }

        public static List<OfficialTarTranslationFile> GetOfficialTarTranslationFiles(
            string packageId,
            string rootDir,
            TargetLanguage targetLang,
            string categoryFilter = null)
        {
            List<OfficialTarTranslationFile> result = new List<OfficialTarTranslationFile>();
            string tarPath = GetOfficialLanguageTarPath(packageId, rootDir, targetLang);
            if (string.IsNullOrEmpty(tarPath)) return result;

            foreach (OfficialTarTranslationFile file in GetOfficialTarXmlFiles(tarPath))
            {
                if (!string.IsNullOrWhiteSpace(categoryFilter) &&
                    !string.Equals(file.Category, categoryFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(file);
            }

            return result;
        }

        public static Dictionary<string, string> LoadOfficialTarTranslationsByCategory(
            string packageId,
            string rootDir,
            TargetLanguage targetLang,
            string category)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (OfficialTarTranslationFile file in GetOfficialTarTranslationFiles(packageId, rootDir, targetLang, category))
            {
                foreach (KeyValuePair<string, string> pair in LoadOfficialTarXmlFileToDict(file, targetLang))
                {
                    result[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        public static Dictionary<string, Dictionary<string, string>> LoadOfficialTarDefTranslations(
            string packageId,
            string rootDir,
            TargetLanguage targetLang)
        {
            Dictionary<string, Dictionary<string, string>> result =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (OfficialTarTranslationFile file in GetOfficialTarTranslationFiles(packageId, rootDir, targetLang))
            {
                if (string.IsNullOrWhiteSpace(file.Category) ||
                    file.Category.Equals(OfficialTarKeyedCategory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!result.TryGetValue(file.Category, out Dictionary<string, string> categoryDict))
                {
                    categoryDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[file.Category] = categoryDict;
                }

                foreach (KeyValuePair<string, string> pair in LoadOfficialTarXmlFileToDict(file, targetLang))
                {
                    categoryDict[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        public static Dictionary<string, string> LoadOfficialTarXmlFileToDict(
            OfficialTarTranslationFile file,
            TargetLanguage targetLang)
        {
            Dictionary<string, string> dict = LoadRawOfficialTarXmlFileToDictCached(file);
            TargetLanguage placeholderLang = targetLang;

            foreach (string key in dict
                         .Where(pair => LanguageDetector.LooksLikePlaceholderTranslation(pair.Value, placeholderLang))
                         .Select(pair => pair.Key)
                         .ToList())
            {
                dict.Remove(key);
            }

            return dict;
        }

        private static string GetOfficialLanguageTarPath(string packageId, string rootDir, TargetLanguage targetLang)
        {
            if (!IsOfficialBaseGameOrDlcPackage(packageId) || string.IsNullOrWhiteSpace(rootDir)) return "";

            string languagesDir = Path.Combine(rootDir, "Languages");
            if (!Directory.Exists(languagesDir)) return "";

            string targetFolder = GetFolderNameByLanguage(targetLang);
            try
            {
                foreach (string tarPath in Directory.GetFiles(languagesDir, "*.tar", SearchOption.TopDirectoryOnly))
                {
                    string nameWithoutExtension = Path.GetFileNameWithoutExtension(tarPath);
                    if (IsLanguageFolderMatch(nameWithoutExtension, targetFolder))
                    {
                        return tarPath;
                    }
                }
            }
            catch { }

            return "";
        }

        private static List<OfficialTarTranslationFile> GetOfficialTarXmlFiles(string tarPath)
        {
            List<OfficialTarTranslationFile> empty = new List<OfficialTarTranslationFile>();
            if (string.IsNullOrEmpty(tarPath) || !File.Exists(tarPath)) return empty;

            FileInfo info;
            try
            {
                info = new FileInfo(tarPath);
            }
            catch
            {
                return empty;
            }

            string fullPath = NormalizeCachePath(tarPath);
            lock (OfficialTarCacheLock)
            {
                if (OfficialTarIndexCache.TryGetValue(fullPath, out OfficialTarIndexCacheEntry cached) &&
                    cached.Length == info.Length &&
                    cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
                {
                    return CloneOfficialTarFiles(cached.Files);
                }
            }

            List<OfficialTarTranslationFile> files = IndexOfficialTarXmlFiles(fullPath);
            lock (OfficialTarCacheLock)
            {
                OfficialTarIndexCache[fullPath] = new OfficialTarIndexCacheEntry
                {
                    Length = info.Length,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                    Files = CloneOfficialTarFiles(files)
                };
            }

            return files;
        }

        private static List<OfficialTarTranslationFile> IndexOfficialTarXmlFiles(string tarPath)
        {
            List<OfficialTarTranslationFile> files = new List<OfficialTarTranslationFile>();

            try
            {
                using (FileStream stream = File.OpenRead(tarPath))
                {
                    byte[] header = new byte[512];
                    while (TryReadTarHeader(stream, header, out string entryName, out long size))
                    {
                        if (IsOfficialTarXmlEntry(entryName, out string category))
                        {
                            files.Add(new OfficialTarTranslationFile
                            {
                                TarPath = tarPath,
                                EntryName = entryName,
                                Category = category
                            });
                        }

                        SkipTarEntryData(stream, size);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Official language tar index failed: {Path.GetFileName(tarPath)} ({ex.Message})");
            }

            return files;
        }

        private static Dictionary<string, string> LoadRawOfficialTarXmlFileToDictCached(OfficialTarTranslationFile file)
        {
            if (file == null || string.IsNullOrEmpty(file.TarPath) || string.IsNullOrEmpty(file.EntryName))
            {
                return new Dictionary<string, string>();
            }

            string key = NormalizeCachePath(file.TarPath) + "|" + file.EntryName;
            lock (OfficialTarCacheLock)
            {
                if (OfficialTarXmlCache.TryGetValue(key, out Dictionary<string, string> cached))
                {
                    return new Dictionary<string, string>(cached);
                }
            }

            Dictionary<string, string> parsed = LoadRawOfficialTarXmlFileToDict(file);
            lock (OfficialTarCacheLock)
            {
                OfficialTarXmlCache[key] = new Dictionary<string, string>(parsed);
            }

            return parsed;
        }

        private static Dictionary<string, string> LoadRawOfficialTarXmlFileToDict(OfficialTarTranslationFile file)
        {
            try
            {
                byte[] bytes = ExtractTarEntryBytes(file.TarPath, file.EntryName);
                if (bytes == null || bytes.Length == 0) return new Dictionary<string, string>();

                using (MemoryStream stream = new MemoryStream(bytes))
                {
                    return ParseTranslationXmlStreamToDict(stream);
                }
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        private static Dictionary<string, string> ParseTranslationXmlStreamToDict(Stream stream)
        {
            Dictionary<string, string> dict = new Dictionary<string, string>();
            XmlDocument doc = new XmlDocument();
            doc.Load(stream);
            if (doc.DocumentElement == null) return dict;

            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;

                string value = node.InnerText;
                if (!string.IsNullOrEmpty(value))
                {
                    value = value.Replace("\\n", "\n").Replace("\\r", "\r").Replace("/n", "\n");
                }

                dict[node.Name] = value;
            }

            return dict;
        }

        private static byte[] ExtractTarEntryBytes(string tarPath, string wantedEntryName)
        {
            string normalizedWanted = NormalizeTarEntryName(wantedEntryName);

            using (FileStream stream = File.OpenRead(tarPath))
            {
                byte[] header = new byte[512];
                while (TryReadTarHeader(stream, header, out string entryName, out long size))
                {
                    if (string.Equals(NormalizeTarEntryName(entryName), normalizedWanted, StringComparison.OrdinalIgnoreCase))
                    {
                        if (size <= 0 || size > int.MaxValue) return new byte[0];

                        byte[] data = new byte[size];
                        int offset = 0;
                        while (offset < data.Length)
                        {
                            int read = stream.Read(data, offset, data.Length - offset);
                            if (read <= 0) break;
                            offset += read;
                        }

                        return offset == data.Length ? data : new byte[0];
                    }

                    SkipTarEntryData(stream, size);
                }
            }

            return new byte[0];
        }

        private static bool TryReadTarHeader(Stream stream, byte[] header, out string entryName, out long size)
        {
            entryName = "";
            size = 0L;

            int offset = 0;
            while (offset < header.Length)
            {
                int read = stream.Read(header, offset, header.Length - offset);
                if (read <= 0) return false;
                offset += read;
            }

            bool empty = true;
            for (int i = 0; i < header.Length; i++)
            {
                if (header[i] != 0)
                {
                    empty = false;
                    break;
                }
            }

            if (empty) return false;

            string name = ReadTarString(header, 0, 100);
            string prefix = ReadTarString(header, 345, 155);
            entryName = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
            size = ParseTarOctal(header, 124, 12);
            return !string.IsNullOrEmpty(entryName);
        }

        private static bool IsOfficialTarXmlEntry(string entryName, out string category)
        {
            category = "";
            if (string.IsNullOrWhiteSpace(entryName)) return false;

            string normalized = NormalizeTarEntryName(entryName);
            if (!normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return false;

            string[] parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;

            if (parts[0].Equals("Keyed", StringComparison.OrdinalIgnoreCase))
            {
                category = OfficialTarKeyedCategory;
                return true;
            }

            if (parts[0].Equals("DefInjected", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
            {
                category = parts[1];
                return true;
            }

            return false;
        }

        private static string NormalizeTarEntryName(string value)
        {
            return (value ?? "").Replace('\\', '/').TrimStart('/');
        }

        private static string ReadTarString(byte[] header, int offset, int length)
        {
            int end = offset;
            int max = Math.Min(header.Length, offset + length);
            while (end < max && header[end] != 0) end++;
            return Encoding.UTF8.GetString(header, offset, end - offset).Trim();
        }

        private static long ParseTarOctal(byte[] header, int offset, int length)
        {
            long result = 0L;
            int max = Math.Min(header.Length, offset + length);
            for (int i = offset; i < max; i++)
            {
                byte b = header[i];
                if (b == 0 || b == 32) continue;
                if (b < (byte)'0' || b > (byte)'7') break;
                result = (result * 8L) + (b - (byte)'0');
            }

            return result;
        }

        private static void SkipTarEntryData(Stream stream, long size)
        {
            long padded = ((size + 511L) / 512L) * 512L;
            if (padded <= 0) return;

            if (stream.CanSeek)
            {
                stream.Seek(padded, SeekOrigin.Current);
                return;
            }

            byte[] buffer = new byte[8192];
            long remaining = padded;
            while (remaining > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0) break;
                remaining -= read;
            }
        }

        private static List<OfficialTarTranslationFile> CloneOfficialTarFiles(List<OfficialTarTranslationFile> files)
        {
            return (files ?? new List<OfficialTarTranslationFile>())
                .Select(file => new OfficialTarTranslationFile
                {
                    TarPath = file.TarPath,
                    EntryName = file.EntryName,
                    Category = file.Category
                })
                .ToList();
        }
    }
}
