using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal sealed class HardcodedUiDecisionStore
    {
        private const int MaximumRecords = 250000;
        private const long MaximumFileBytes = 64L * 1024L * 1024L;
        private readonly object _gate = new object();
        private readonly string _path;
        private Dictionary<string, HardcodedUiDecisionRecord> _records;

        internal HardcodedUiDecisionStore(string path)
        {
            _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        }

        internal bool TryGet(string entryId, out HardcodedUiDecisionRecord record)
        {
            lock (_gate)
            {
                EnsureLoaded();
                if (!_records.TryGetValue(entryId ?? string.Empty, out HardcodedUiDecisionRecord found))
                {
                    record = null;
                    return false;
                }
                record = found.Clone();
                return true;
            }
        }

        internal List<HardcodedUiDecisionRecord> GetPackage(string packageId)
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _records.Values
                    .Where(record => string.Equals(
                        record.PackageId,
                        packageId,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(record => record.EntryId, StringComparer.Ordinal)
                    .Select(record => record.Clone())
                    .ToList();
            }
        }

        internal void UpsertMany(IEnumerable<HardcodedUiDecisionRecord> records)
        {
            lock (_gate)
            {
                EnsureLoaded();
                foreach (HardcodedUiDecisionRecord record in records ??
                         Enumerable.Empty<HardcodedUiDecisionRecord>())
                {
                    ValidateRecord(record);
                    _records[record.EntryId] = record.Clone();
                }
                if (_records.Count > MaximumRecords)
                    throw new InvalidDataException("Hardcoded UI decision store exceeds the record limit.");
                SaveLocked();
            }
        }

        private void EnsureLoaded()
        {
            if (_records != null) return;
            _records = new Dictionary<string, HardcodedUiDecisionRecord>(StringComparer.Ordinal);
            if (!File.Exists(_path)) return;
            var info = new FileInfo(_path);
            if (info.Length > MaximumFileBytes)
                throw new InvalidDataException("Hardcoded UI decision store exceeds the file-size limit.");
            HardcodedUiDecisionStoreFile file = JsonConvert.DeserializeObject<HardcodedUiDecisionStoreFile>(
                File.ReadAllText(_path, Encoding.UTF8));
            if (file == null || file.StoreVersion != 1)
                throw new InvalidDataException("Unsupported hardcoded UI decision store version.");
            foreach (HardcodedUiDecisionRecord record in file.Records ??
                     new List<HardcodedUiDecisionRecord>())
            {
                ValidateRecord(record);
                if (!_records.ContainsKey(record.EntryId))
                    _records.Add(record.EntryId, record.Clone());
            }
            if (_records.Count > MaximumRecords)
                throw new InvalidDataException("Hardcoded UI decision store exceeds the record limit.");
        }

        private void SaveLocked()
        {
            string directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidDataException("Hardcoded UI decision store directory is empty.");
            Directory.CreateDirectory(directory);
            var file = new HardcodedUiDecisionStoreFile
            {
                Records = _records.Values
                    .OrderBy(record => record.EntryId, StringComparer.Ordinal)
                    .Select(record => record.Clone())
                    .ToList()
            };
            string json = JsonConvert.SerializeObject(file, Formatting.Indented);
            if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
                throw new InvalidDataException("Hardcoded UI decision store exceeds the file-size limit.");
            string temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(temporaryPath, _path, _path + ".bak", true);
            else File.Move(temporaryPath, _path);
        }

        private static void ValidateRecord(HardcodedUiDecisionRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.EntryId) ||
                string.IsNullOrWhiteSpace(record.PackageId))
                throw new InvalidDataException("Hardcoded UI decision record identity is incomplete.");
            if (record.EntryId.Length > 160 || record.PackageId.Length > 512 ||
                (record.DiagnosticFlags != null && record.DiagnosticFlags.Count > 64))
                throw new InvalidDataException("Hardcoded UI decision record exceeds safety limits.");
        }
    }
}
