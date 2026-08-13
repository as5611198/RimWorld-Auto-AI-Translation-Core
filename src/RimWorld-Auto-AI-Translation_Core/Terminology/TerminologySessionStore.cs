using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace AutoTranslator_Core.Terminology
{
    internal sealed class TerminologySessionStore
    {
        private readonly string _path;

        internal TerminologySessionStore(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        internal TerminologySessionFile Load(
            string expectedSessionId,
            string expectedSourceFingerprint)
        {
            if (!File.Exists(_path)) return null;
            try
            {
                TerminologySessionFile file = JsonConvert.DeserializeObject<TerminologySessionFile>(File.ReadAllText(_path));
                if (file == null || file.SchemaVersion != 1 || file.AnalyzerVersion != 1 ||
                    !string.Equals(file.SessionId, expectedSessionId, StringComparison.Ordinal) ||
                    !string.Equals(file.SourceFingerprint, expectedSourceFingerprint, StringComparison.Ordinal))
                    return null;
                file.Corpus = file.Corpus ?? new List<TerminologyCorpusEntry>();
                file.Candidates = file.Candidates ?? new List<TerminologyCandidate>();
                return file;
            }
            catch
            {
                return null;
            }
        }

        internal void Save(TerminologySessionFile file)
        {
            if (file == null) return;
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            file.UpdatedUtc = DateTime.UtcNow.ToString("o");
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(file, Formatting.Indented));
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", true);
            else File.Move(temporary, _path);
        }
    }
}
