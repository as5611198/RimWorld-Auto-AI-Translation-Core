using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        // Parsed input is reusable even when some candidates cannot be applied.
        // It is not the all-valid runtime cache: native injection still checks
        // each candidate against the currently loaded game.
        private sealed class InjectionXmlFile
        {
            public string Hash;
            public Dictionary<string, string> Data = new Dictionary<string, string>();
            public Dictionary<string, List<string>> Lists = new Dictionary<string, List<string>>();
        }
        private sealed class InjectionXmlCache
        {
            public int Schema = 1;
            public Dictionary<string, InjectionXmlFile> Files = new Dictionary<string, InjectionXmlFile>(StringComparer.OrdinalIgnoreCase);
        }
        [ThreadStatic] private static InjectionXmlCache preparingXml;
        [ThreadStatic] private static Dictionary<string, string> preparingXmlHashes;

        private static string InjectionXmlCachePath(string folder) => Path.Combine(GetLocalPackPath(), "SelfRepair", "parsed-" + folder + ".json");

        private static InjectionXmlCache ReadInjectionXmlCache(string folder)
        {
            string path = InjectionXmlCachePath(folder);
            try
            {
                if (File.Exists(path))
                {
                    var cached = JsonConvert.DeserializeObject<InjectionXmlCache>(File.ReadAllText(path));
                    if (cached?.Schema == 1 && cached.Files != null)
                    {
                        cached.Files = new Dictionary<string, InjectionXmlFile>(cached.Files, StringComparer.OrdinalIgnoreCase);
                        return cached;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException)
            { Log.Warning("[ATC] Parsed translation cache unavailable; loading injection data: " + ex.Message); }
            return new InjectionXmlCache();
        }

        private static InjectionXmlFile ReadInjectionXmlFile(string file)
        {
            file = Path.GetFullPath(file);
            string hash;
            try
            {
                hash = preparingXmlHashes != null && preparingXmlHashes.TryGetValue(file, out string known)
                    ? known : TranslationFileTransaction.FileHash(file);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warning("[ATC] Unreadable translation skipped during injection: " + file + ": " + ex.Message);
                return new InjectionXmlFile();
            }
            if (preparingXml != null && preparingXml.Files.TryGetValue(file, out var cached) &&
                cached != null && cached.Hash == hash && cached.Data != null && cached.Lists != null) return cached;
            var parsed = new InjectionXmlFile { Hash = hash };
            try
            {
                // SHA256 wins over a same-size, same-timestamp in-memory entry.
                InvalidateXmlFileCache(file);
                parsed.Data = LoadXmlFileToDict(file);
                if (parsed.Data.Count != 0 && file.Replace('\\', '/').IndexOf("/DefInjected/", StringComparison.OrdinalIgnoreCase) >= 0)
                    parsed.Lists = TranslationXmlDictionaryParser.ReadFullLists(file);
                if (TranslationFileTransaction.FileHash(file) != hash) throw new TranslationSnapshotChangedException("Translation changed while reading: " + file);
            }
            catch (TranslationSnapshotChangedException) { throw; }
            catch (Exception ex)
            {
                parsed.Data.Clear(); parsed.Lists.Clear();
                RepairJournal.Record(new TranslationRepairRecord { SourceFile = file, SourceKind = "payload-file",
                    Reason = "file_operation_failed", Status = "仍待處理", Evidence = ex.Message });
                Log.Warning("[ATC] Skipped translation file during injection: " + file + ": " + ex.Message);
            }
            if (preparingXml != null) preparingXml.Files[file] = parsed;
            return parsed;
        }

        private static void SaveInjectionXmlCache(string folder, int epoch)
        {
            foreach (string path in preparingXml.Files.Keys.Where(p => !File.Exists(p)).ToArray()) preparingXml.Files.Remove(path);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(preparingXml));
            try
            {
                TranslationXmlAtomicFileStore.SaveIfCurrent(InjectionXmlCachePath(folder),
                    stream => stream.Write(bytes, 0, bytes.Length), () => epoch == GameDataEpoch);
            }
            catch (Exception ex) { Log.Warning("[ATC] Parsed translation cache could not be saved: " + ex.Message); }
        }
    }
}
