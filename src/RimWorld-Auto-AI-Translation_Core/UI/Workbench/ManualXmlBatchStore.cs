using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Newtonsoft.Json;

namespace AutoTranslator_Core
{
    // File-only batch exchange. Nothing is saved to the live translation pack during preview.
    internal static class ManualXmlBatchStore
    {
        internal sealed class Entry
        {
            public string Key, OriginalText, TranslatedText;
        }

        internal sealed class ModData
        {
            public string PackageId, Name;
            public Dictionary<string, List<Entry>> Categories = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed class Change
        {
            public string Category, Key, OriginalText, PreviousText, TranslatedText;
        }

        internal sealed class Plan
        {
            public string PackageId, Name, Error;
            public int Skipped, Unchanged, MissingFiles;
            public List<Change> Changes = new List<Change>();
        }

        internal sealed class Manifest
        {
            public int Version = 1;
            public string Language;
            public List<ManifestMod> Mods = new List<ManifestMod>();
        }

        internal sealed class ManifestMod
        {
            public string PackageId;
            public Dictionary<string, Dictionary<string, string>> Sources = new Dictionary<string, Dictionary<string, string>>();
        }

        internal static int Export(string root, string language, IList<ModData> mods)
        {
            Segment(language);
            if (Directory.Exists(root)) throw new IOException("Batch folder already exists. Create a new batch to preserve edited files.");
            var manifest = new Manifest { Language = language };
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModData mod in mods)
            {
                Segment(mod.PackageId);
                if (!ids.Add(mod.PackageId)) throw new InvalidDataException("Duplicate package ID: " + mod.PackageId);
                foreach (string category in mod.Categories.Keys) Segment(category);
            }
            Directory.CreateDirectory(root);
            int count = 0;
            foreach (ModData mod in mods)
            {
                var source = new ManifestMod { PackageId = mod.PackageId };
                foreach (var category in mod.Categories)
                {
                    var document = new XmlDocument { XmlResolver = null };
                    document.AppendChild(document.CreateXmlDeclaration("1.0", "utf-8", null));
                    XmlElement data = document.CreateElement("LanguageData");
                    document.AppendChild(data);
                    var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Entry entry in category.Value)
                    {
                        if (string.IsNullOrWhiteSpace(entry.OriginalText)) continue;
                        XmlConvert.VerifyNCName(entry.Key);
                        if (hashes.ContainsKey(entry.Key)) throw new InvalidDataException("Duplicate XML key: " + entry.Key);
                        XmlElement node = document.CreateElement(entry.Key);
                        node.InnerText = entry.OriginalText;
                        data.AppendChild(node);
                        hashes.Add(entry.Key, Hash(entry.OriginalText));
                        count++;
                    }
                    if (hashes.Count == 0) continue;
                    TranslationXmlAtomicFileStore.Save(XmlPath(root, mod.PackageId, language, category.Key), stream => document.Save(stream));
                    source.Sources.Add(category.Key, hashes);
                }
                manifest.Mods.Add(source);
            }
            // Written last: an incomplete export must not be accepted as an import batch.
            TranslationXmlAtomicFileStore.Save(Path.Combine(root, "manifest.json"), stream =>
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                    writer.Write(JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented));
            });
            return count;
        }

        internal static Manifest ReadManifest(string root, string language)
        {
            string path = Path.Combine(root, "manifest.json");
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Batch manifest is too large.");
            Manifest manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(path), new JsonSerializerSettings { MaxDepth = 32 });
            if (manifest == null || manifest.Version != 1 || manifest.Mods == null ||
                !string.Equals(manifest.Language, language, StringComparison.Ordinal))
                throw new InvalidDataException("Batch language or format does not match the current workbench language.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManifestMod mod in manifest.Mods)
            {
                if (mod == null || mod.Sources == null) throw new InvalidDataException("Invalid batch mod record.");
                Segment(mod.PackageId);
                if (!ids.Add(mod.PackageId)) throw new InvalidDataException("Duplicate batch mod record.");
                foreach (var category in mod.Sources)
                {
                    Segment(category.Key);
                    if (category.Value == null) throw new InvalidDataException("Invalid batch source record.");
                }
            }
            return manifest;
        }

        internal static Plan Preview(string root, string language, Manifest manifest, ModData current,
            Func<string, string, bool> validTranslation = null)
        {
            var plan = new Plan { PackageId = current.PackageId, Name = current.Name };
            ManifestMod source = manifest.Mods.SingleOrDefault(m => string.Equals(m.PackageId, current.PackageId, StringComparison.OrdinalIgnoreCase));
            if (source == null) { plan.MissingFiles++; return plan; }
            try
            {
                foreach (var category in source.Sources)
                {
                    string path = XmlPath(root, current.PackageId, language, category.Key);
                    if (!File.Exists(path)) { plan.MissingFiles++; continue; }
                    Dictionary<string, string> edited = ReadXml(path);
                    var hashes = new Dictionary<string, string>(category.Value, StringComparer.OrdinalIgnoreCase);
                    List<Entry> entries;
                    var known = current.Categories.TryGetValue(category.Key, out entries)
                        ? entries.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in edited)
                    {
                        Entry entry;
                        string hash;
                        if (!known.TryGetValue(pair.Key, out entry) || !hashes.TryGetValue(pair.Key, out hash) ||
                            !string.Equals(hash, Hash(entry.OriginalText), StringComparison.Ordinal) || string.IsNullOrWhiteSpace(pair.Value))
                        { plan.Skipped++; continue; }
                        // An untouched original must never replace an existing translation.
                        if (pair.Value == entry.OriginalText || pair.Value == (entry.TranslatedText ?? ""))
                        { plan.Unchanged++; continue; }
                        if (validTranslation != null && !validTranslation(pair.Value, entry.OriginalText))
                        { plan.Skipped++; continue; }
                        plan.Changes.Add(new Change { Category = category.Key, Key = entry.Key, OriginalText = entry.OriginalText,
                            PreviousText = entry.TranslatedText ?? "", TranslatedText = pair.Value });
                    }
                }
            }
            catch (Exception ex)
            {
                plan.Changes.Clear(); // A malformed file blocks this mod, while other mods remain importable.
                plan.Error = ex.Message;
            }
            return plan;
        }

        internal static bool StillMatches(Change change, Entry current)
        {
            return current != null && current.OriginalText == change.OriginalText &&
                (current.TranslatedText ?? "") == change.PreviousText;
        }

        internal static string XmlPath(string root, string packageId, string language, string category)
        {
            Segment(packageId); Segment(language); Segment(category);
            string bucket = string.Equals(category, "Keyed", StringComparison.OrdinalIgnoreCase)
                ? "Keyed" : Path.Combine("DefInjected", category);
            return Path.Combine(root, packageId, language, bucket, "ManualTranslation.xml");
        }

        private static void Segment(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." || value.EndsWith(".", StringComparison.Ordinal) ||
                value.EndsWith(" ", StringComparison.Ordinal) || value.IndexOfAny(new[] { '/', '\\', ':', '<', '>', '|', '?', '*', '"' }) >= 0 ||
                value.Any(char.IsControl)) throw new InvalidDataException("Invalid batch folder name.");
        }

        private static Dictionary<string, string> ReadXml(string path)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 }))
                document.Load(reader);
            if (document.DocumentElement == null || document.DocumentElement.Name != "LanguageData")
                throw new InvalidDataException("Expected a LanguageData XML document.");
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in document.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (node.ChildNodes.Cast<XmlNode>().Any(n => n.NodeType == XmlNodeType.Element))
                    throw new InvalidDataException("Translation text must escape XML tags: " + node.Name);
                if (data.ContainsKey(node.Name)) throw new InvalidDataException("Duplicate XML key: " + node.Name);
                data.Add(node.Name, node.InnerText);
            }
            return data;
        }

        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")));
        }
    }
}
