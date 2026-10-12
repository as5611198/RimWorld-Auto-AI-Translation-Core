using AutoTranslator_Core.TargetedHardcodedUi;
using AutoTranslator_Core.Workflow.Analysis;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AutoTranslator_Core.Workflow.Output
{
    public sealed class TranslationWriteTarget
    {
        public string RelativePath { get; set; } = string.Empty;
        public string EntryKey { get; set; } = string.Empty;
    }

    public sealed class TranslationOutputWrite
    {
        public CandidateRecord Candidate { get; set; }
        public string TranslatedText { get; set; } = string.Empty;
        public TranslationOrigin Origin { get; set; }
        public TranslationWriteTarget Target { get; set; }
    }

    public interface ITranslationOutputStore
    {
        TranslationWriteTarget Resolve(CandidateRecord candidate, string targetLanguage);
        void Write(
            CandidateRecord candidate,
            string targetLanguage,
            string translatedText,
            TranslationOrigin origin,
            TranslationWriteTarget target);
        void WriteBatch(IList<TranslationOutputWrite> writes, string targetLanguage);
        void Delete(CandidateRecord candidate, string targetLanguage, TranslationWriteTarget target);
    }

    public sealed class CompositeTranslationOutputStore : ITranslationOutputStore
    {
        private readonly XmlTranslationOutputStore _xml;
        private readonly DllTranslationOutputStore _dll;

        public CompositeTranslationOutputStore(string generatedPackRoot)
        {
            _xml = new XmlTranslationOutputStore(generatedPackRoot);
            _dll = new DllTranslationOutputStore(generatedPackRoot);
        }

        public TranslationWriteTarget Resolve(CandidateRecord candidate, string targetLanguage)
        {
            return Select(candidate).Resolve(candidate, targetLanguage);
        }

        public void Write(CandidateRecord candidate, string targetLanguage, string translatedText, TranslationOrigin origin, TranslationWriteTarget target)
        {
            Select(candidate).Write(candidate, targetLanguage, translatedText, origin, target);
        }

        public void WriteBatch(IList<TranslationOutputWrite> writes, string targetLanguage)
        {
            foreach (IGrouping<CandidateSourceDomain, TranslationOutputWrite> group in
                     (writes ?? Array.Empty<TranslationOutputWrite>())
                     .Where(write => write?.Candidate != null)
                     .GroupBy(write => write.Candidate.SourceDomain))
            {
                ITranslationOutputStore store = group.Key == CandidateSourceDomain.Dll
                    ? (ITranslationOutputStore)_dll
                    : _xml;
                store.WriteBatch(group.ToList(), targetLanguage);
            }
        }

        public void Delete(CandidateRecord candidate, string targetLanguage, TranslationWriteTarget target)
        {
            Select(candidate).Delete(candidate, targetLanguage, target);
        }

        private ITranslationOutputStore Select(CandidateRecord candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            return candidate.SourceDomain == CandidateSourceDomain.Dll
                ? (ITranslationOutputStore)_dll
                : _xml;
        }
    }

    public sealed class XmlTranslationOutputStore : ITranslationOutputStore
    {
        private static readonly object FileGate = new object();
        private readonly string _packRoot;

        public XmlTranslationOutputStore(string generatedPackRoot)
        {
            _packRoot = Path.GetFullPath(generatedPackRoot ?? throw new ArgumentNullException(nameof(generatedPackRoot)));
        }

        public TranslationWriteTarget Resolve(CandidateRecord candidate, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(candidate.TranslationEntryKey))
                throw new InvalidOperationException("XML candidate has no verified generated-file entry key: " + candidate.CandidateId);
            if (!string.IsNullOrWhiteSpace(candidate.DefaultOutputFileRelativePath))
            {
                return new TranslationWriteTarget
                {
                    RelativePath = candidate.DefaultOutputFileRelativePath,
                    EntryKey = candidate.TranslationEntryKey
                };
            }
            string cleanPackageId = TranslationGeneratedOutputOwnership.GetCleanPackageId(candidate.PackageId);
            string relativePath;
            if (string.Equals(candidate.EntryKind, "Keyed", StringComparison.OrdinalIgnoreCase))
            {
                relativePath = Path.Combine("Languages", targetLanguage, "Keyed", cleanPackageId + "_AutoTranslated.xml");
            }
            else
            {
                XmlWorkflowCandidateContext context = JsonConvert.DeserializeObject<XmlWorkflowCandidateContext>(
                    candidate.ContextJson ?? string.Empty);
                string defType = context?.DefType;
                if (string.IsNullOrWhiteSpace(defType)) defType = GetDefType(candidate.SourceFileRelativePath);
                if (string.IsNullOrWhiteSpace(defType))
                    throw new InvalidOperationException("DefInjected candidate has no verified Def type: " + candidate.CandidateId);
                relativePath = Path.Combine(
                    "Languages", targetLanguage, "DefInjected", defType, cleanPackageId + "_AutoTranslated.xml");
            }
            return new TranslationWriteTarget
            {
                RelativePath = relativePath.Replace('\\', '/'),
                EntryKey = candidate.TranslationEntryKey
            };
        }

        public void Write(CandidateRecord candidate, string targetLanguage, string translatedText, TranslationOrigin origin, TranslationWriteTarget target)
        {
            WriteBatch(new[]
            {
                new TranslationOutputWrite
                {
                    Candidate = candidate,
                    TranslatedText = translatedText,
                    Origin = origin,
                    Target = target
                }
            }, targetLanguage);
        }

        public void WriteBatch(IList<TranslationOutputWrite> writes, string targetLanguage)
        {
            foreach (IGrouping<string, TranslationOutputWrite> fileGroup in
                     (writes ?? Array.Empty<TranslationOutputWrite>())
                     .Where(write => write?.Candidate != null && write.Target != null)
                     .GroupBy(write => write.Target.RelativePath, WorkflowPath.Comparer))
            {
                string fullPath = ResolveInsidePack(fileGroup.Key);
                lock (FileGate)
                {
                    XDocument document = File.Exists(fullPath)
                        ? XDocument.Load(fullPath, LoadOptions.PreserveWhitespace)
                        : new XDocument(
                            new XDeclaration("1.0", "utf-8", null),
                            new XElement("LanguageData"));
                    if (document.Root == null) document.Add(new XElement("LanguageData"));
                    Dictionary<string, TranslationOutputWrite> changed =
                        new Dictionary<string, TranslationOutputWrite>(StringComparer.Ordinal);
                    foreach (TranslationOutputWrite write in fileGroup)
                    {
                        string entryKey = write.Target.EntryKey ?? string.Empty;
                        XElement element = document.Root.Elements().FirstOrDefault(item =>
                            string.Equals(item.Name.LocalName, entryKey, StringComparison.Ordinal));
                        if (element == null)
                        {
                            try { element = new XElement(entryKey); }
                            catch (XmlException ex)
                            {
                                throw new InvalidDataException(
                                    "Invalid translation entry key: " + entryKey, ex);
                            }
                            document.Root.Add(element);
                        }
                        element.Value = write.TranslatedText ?? string.Empty;
                        changed[entryKey] = write;
                    }
                    TranslationXmlAtomicFileStore.Save(fullPath, stream =>
                    {
                        XmlWriterSettings settings = new XmlWriterSettings
                        {
                            Encoding = new UTF8Encoding(false),
                            Indent = true,
                            CloseOutput = false
                        };
                        using (XmlWriter writer = XmlWriter.Create(stream, settings)) document.Save(writer);
                    });
                    SaveProvenanceBatch(
                        fileGroup.First().Candidate, targetLanguage, fullPath, document, changed);
                    XDocument verified = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
                    foreach (KeyValuePair<string, TranslationOutputWrite> pair in changed)
                    {
                        XElement actual = verified.Root?.Elements().FirstOrDefault(item =>
                            string.Equals(item.Name.LocalName, pair.Key, StringComparison.Ordinal));
                        if (actual == null || !string.Equals(
                                WorkflowIdentity.HashText(actual.Value),
                                WorkflowIdentity.HashText(pair.Value.TranslatedText),
                                StringComparison.Ordinal))
                            throw new InvalidDataException(
                                "Translation output verification failed: " + fileGroup.Key + " / " + pair.Key);
                    }
                }
            }
        }

        public void Delete(CandidateRecord candidate, string targetLanguage, TranslationWriteTarget target)
        {
            string fullPath = ResolveInsidePack(target.RelativePath);
            lock (FileGate)
            {
                if (!File.Exists(fullPath)) return;
                XDocument document = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
                XElement element = document.Root?.Elements().FirstOrDefault(item =>
                    string.Equals(item.Name.LocalName, target.EntryKey, StringComparison.Ordinal));
                if (element == null) return;
                element.Remove();
                if (document.Root == null || !document.Root.Elements().Any())
                {
                    File.Delete(fullPath);
                    AutoTranslatorScanner.SaveProvenanceForFile(
                        Path.Combine(_packRoot, "Languages", targetLanguage),
                        candidate.PackageId, fullPath,
                        new Dictionary<string, string>(), null);
                    if (File.Exists(fullPath))
                        throw new IOException("Empty translation output file could not be deleted: " + fullPath);
                    return;
                }
                TranslationXmlAtomicFileStore.Save(fullPath, stream => document.Save(stream));
                SaveProvenance(candidate, targetLanguage, fullPath, document, null, null, TranslationOrigin.None);
                XDocument verified = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
                if (verified.Root?.Elements().Any(item =>
                        string.Equals(item.Name.LocalName, target.EntryKey, StringComparison.Ordinal)) == true)
                    throw new InvalidDataException(
                        "Translation output deletion verification failed: " + target.RelativePath +
                        " / " + target.EntryKey);
            }
        }

        private void SaveProvenance(
            CandidateRecord candidate,
            string targetLanguage,
            string fullPath,
            XDocument document,
            string changedKey,
            string changedValue,
            TranslationOrigin origin)
        {
            Dictionary<string, string> values = document.Root?.Elements()
                .Where(item => !item.HasElements)
                .GroupBy(item => item.Name.LocalName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase) ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> sources =
                new Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(changedKey))
            {
                sources[changedKey] = AutoTranslatorScanner.CreateProvenance(
                    ToProvenanceKind(origin), candidate.PackageId, candidate.PackageId,
                    fullPath, targetLanguage, changedValue ?? string.Empty);
            }
            AutoTranslatorScanner.SaveProvenanceForFile(
                Path.Combine(_packRoot, "Languages", targetLanguage),
                candidate.PackageId, fullPath, values, sources);
        }

        private void SaveProvenanceBatch(
            CandidateRecord representative,
            string targetLanguage,
            string fullPath,
            XDocument document,
            IDictionary<string, TranslationOutputWrite> changed)
        {
            Dictionary<string, string> values = document.Root?.Elements()
                .Where(item => !item.HasElements)
                .GroupBy(item => item.Name.LocalName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase) ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry> sources =
                new Dictionary<string, AutoTranslatorScanner.TranslationProvenanceEntry>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, TranslationOutputWrite> pair in
                     changed ?? new Dictionary<string, TranslationOutputWrite>())
            {
                TranslationOutputWrite write = pair.Value;
                sources[pair.Key] = AutoTranslatorScanner.CreateProvenance(
                    ToProvenanceKind(write.Origin), write.Candidate.PackageId,
                    write.Candidate.PackageId, fullPath, targetLanguage,
                    write.TranslatedText ?? string.Empty);
            }
            AutoTranslatorScanner.SaveProvenanceForFile(
                Path.Combine(_packRoot, "Languages", targetLanguage),
                representative.PackageId, fullPath, values, sources);
        }

        private static string ToProvenanceKind(TranslationOrigin origin)
        {
            switch (origin)
            {
                case TranslationOrigin.Manual: return AutoTranslatorScanner.ProvenanceKindManualEdit;
                case TranslationOrigin.Cloud: return AutoTranslatorScanner.ProvenanceKindCloud;
                case TranslationOrigin.ThirdParty: return AutoTranslatorScanner.ProvenanceKindExternalPatch;
                case TranslationOrigin.ModNative: return AutoTranslatorScanner.ProvenanceKindModNativeTarget;
                default: return AutoTranslatorScanner.ProvenanceKindAI;
            }
        }

        private string ResolveInsidePack(string relativePath)
        {
            string full = Path.GetFullPath(Path.Combine(_packRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = _packRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, WorkflowPath.Comparison))
                throw new InvalidDataException("Translation output path escapes the generated pack.");
            return full;
        }

        private static string GetDefType(string sourceRelativePath)
        {
            string normalized = (sourceRelativePath ?? string.Empty).Replace('\\', '/');
            int marker = normalized.IndexOf("DefInjected/", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return string.Empty;
            string remainder = normalized.Substring(marker + "DefInjected/".Length);
            int slash = remainder.IndexOf('/');
            return slash > 0 ? remainder.Substring(0, slash) : string.Empty;
        }
    }

    public sealed class DllTranslationOutputStore : ITranslationOutputStore
    {
        private static readonly object ManifestGate = new object();
        private readonly string _packRoot;
        private readonly string _relativeManifestPath = "HardcodedUiPatchPrototype.json";

        public DllTranslationOutputStore(string generatedPackRoot)
        {
            _packRoot = Path.GetFullPath(generatedPackRoot ?? throw new ArgumentNullException(nameof(generatedPackRoot)));
        }

        public TranslationWriteTarget Resolve(CandidateRecord candidate, string targetLanguage)
        {
            HardcodedUiPatchEntry entry = DeserializeEntry(candidate);
            return new TranslationWriteTarget
            {
                RelativePath = _relativeManifestPath,
                EntryKey = entry.EntryId
            };
        }

        public void Write(CandidateRecord candidate, string targetLanguage, string translatedText, TranslationOrigin origin, TranslationWriteTarget target)
        {
            WriteBatch(new[]
            {
                new TranslationOutputWrite
                {
                    Candidate = candidate,
                    TranslatedText = translatedText,
                    Origin = origin,
                    Target = target
                }
            }, targetLanguage);
        }

        public void WriteBatch(IList<TranslationOutputWrite> writes, string targetLanguage)
        {
            List<TranslationOutputWrite> validWrites = (writes ?? Array.Empty<TranslationOutputWrite>())
                .Where(write => write?.Candidate != null)
                .ToList();
            if (validWrites.Count == 0) return;
            string path = Path.Combine(_packRoot, _relativeManifestPath);
            lock (ManifestGate)
            {
                HardcodedUiPatchManifest manifest = File.Exists(path)
                    ? JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(File.ReadAllText(path)) ?? new HardcodedUiPatchManifest()
                    : new HardcodedUiPatchManifest();
                manifest.Approved = true;
                Dictionary<string, HardcodedUiPatchEntry> entries = (manifest.Entries ?? new List<HardcodedUiPatchEntry>())
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.EntryId))
                    .GroupBy(item => item.EntryId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                foreach (TranslationOutputWrite write in validWrites)
                {
                    HardcodedUiPatchEntry replacement = DeserializeEntry(write.Candidate);
                    if (replacement.Translations == null)
                        replacement.Translations =
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    replacement.Translations[targetLanguage] = write.TranslatedText ?? string.Empty;
                    replacement.Enabled = true;
                    if (entries.TryGetValue(
                            replacement.EntryId, out HardcodedUiPatchEntry existing) &&
                        existing.Translations != null)
                    {
                        foreach (KeyValuePair<string, string> pair in existing.Translations)
                            if (!replacement.Translations.ContainsKey(pair.Key))
                                replacement.Translations[pair.Key] = pair.Value;
                    }
                    entries[replacement.EntryId] = replacement;
                }
                manifest.Entries = entries.Values.OrderBy(item => item.EntryId, StringComparer.Ordinal).ToList();
                TranslationXmlAtomicFileStore.Save(path, stream =>
                {
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                    using (JsonTextWriter jsonWriter = new JsonTextWriter(writer) { Formatting = Newtonsoft.Json.Formatting.Indented })
                    {
                        JsonSerializer.CreateDefault().Serialize(jsonWriter, manifest);
                        jsonWriter.Flush();
                    }
                });
                HardcodedUiPatchManifest verified =
                    JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(File.ReadAllText(path));
                Dictionary<string, HardcodedUiPatchEntry> verifiedEntries =
                    (verified?.Entries ?? new List<HardcodedUiPatchEntry>())
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.EntryId))
                    .ToDictionary(item => item.EntryId, StringComparer.Ordinal);
                foreach (TranslationOutputWrite write in validWrites)
                {
                    HardcodedUiPatchEntry expected = DeserializeEntry(write.Candidate);
                    if (!verifiedEntries.TryGetValue(expected.EntryId, out HardcodedUiPatchEntry actual) ||
                        actual.Translations == null ||
                        !actual.Translations.TryGetValue(targetLanguage, out string actualText) ||
                        !string.Equals(
                            WorkflowIdentity.HashText(actualText),
                            WorkflowIdentity.HashText(write.TranslatedText),
                            StringComparison.Ordinal))
                        throw new InvalidDataException(
                            "DLL translation output verification failed: " + expected.EntryId);
                }
            }
        }

        public void Delete(CandidateRecord candidate, string targetLanguage, TranslationWriteTarget target)
        {
            string path = Path.Combine(_packRoot, _relativeManifestPath);
            lock (ManifestGate)
            {
                if (!File.Exists(path)) return;
                HardcodedUiPatchManifest manifest = JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(File.ReadAllText(path));
                HardcodedUiPatchEntry entry = manifest?.Entries?.FirstOrDefault(item =>
                    item != null && string.Equals(item.EntryId, target.EntryKey, StringComparison.Ordinal));
                if (entry?.Translations == null || !entry.Translations.Remove(targetLanguage)) return;
                TranslationXmlAtomicFileStore.Save(path, stream =>
                {
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                    using (JsonTextWriter jsonWriter = new JsonTextWriter(writer) { Formatting = Newtonsoft.Json.Formatting.Indented })
                    {
                        JsonSerializer.CreateDefault().Serialize(jsonWriter, manifest);
                        jsonWriter.Flush();
                    }
                });
                HardcodedUiPatchManifest verified =
                    JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(File.ReadAllText(path));
                HardcodedUiPatchEntry verifiedEntry = verified?.Entries?.FirstOrDefault(item =>
                    item != null && string.Equals(item.EntryId, target.EntryKey, StringComparison.Ordinal));
                if (verifiedEntry?.Translations != null &&
                    verifiedEntry.Translations.ContainsKey(targetLanguage))
                    throw new InvalidDataException(
                        "DLL translation deletion verification failed: " + target.EntryKey);
            }
        }

        private static HardcodedUiPatchEntry DeserializeEntry(CandidateRecord candidate)
        {
            HardcodedUiPatchEntry entry = JsonConvert.DeserializeObject<HardcodedUiPatchEntry>(candidate.ContextJson ?? string.Empty);
            if (entry == null || string.IsNullOrWhiteSpace(entry.EntryId))
                throw new InvalidOperationException("DLL candidate has no immutable patch context: " + candidate.CandidateId);
            return entry;
        }
    }
}
