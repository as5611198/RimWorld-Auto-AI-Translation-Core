using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private static readonly object RepairGate = new object();
        private static TranslationRepairJournal repairJournal;
        private static string repairJournalRoot;
        private static string repairEnvironment;
        private static readonly Dictionary<string, string> repairedDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> failedNativeCandidates = new HashSet<string>(StringComparer.Ordinal);
        internal static bool StructureReady => PlayDataLoader.Loaded;

        internal static TranslationRepairJournal RepairJournal
        {
            get
            {
                TranslationRepairJournal result;
                lock (RepairGate)
                {
                    string root = Path.Combine(GetLocalPackPath(), "SelfRepair");
                    if (repairJournal == null || root != repairJournalRoot)
                    {
                        repairJournal = new TranslationRepairJournal(root, false);
                        repairJournalRoot = root;
                    }
                    result = repairJournal;
                }
                if (!UnityData.IsInMainThread) result.WarmHistory();
                return result;
            }
        }

        public static DefTranslationPathResult ValidateDefTranslation(string defType, string key)
        {
            if (!StructureReady) return new DefTranslationPathResult { Path = key, Reason = "not_ready" };
            return ValidateLoadedDefTranslation(TranslationRuntimeResolver.DefType(defType), key);
        }

        internal static DefTranslationPathResult ValidateLoadedDefList(Type type, string key) => ValidateLoadedDefTranslation(type, key, true);

        internal static DefTranslationPathResult ValidateLoadedDefTranslation(Type type, string key, bool fullList = false)
        {
            if (!UnityData.IsInMainThread)
                return new DefTranslationPathResult { Path = key, Reason = "not_ready", Evidence = "Live validation requires game-thread capture." };
            try { return ValidateLoadedDefTranslationCore(type, key, fullList); }
            catch (Exception ex) when (ex is TargetInvocationException || ex is AmbiguousMatchException ||
                ex is MemberAccessException || ex is TypeLoadException || ex is InvalidOperationException || ex is TargetException)
            {
                var cause = ex.InnerException ?? ex;
                return new DefTranslationPathResult { Path = key, Reason = "validation_unavailable",
                    Evidence = cause.GetType().Name + ": " + cause.Message };
            }
        }

        private static DefTranslationPathResult ValidateLoadedDefTranslationCore(Type type, string key, bool fullList)
        {
            lock (RepairGate)
            {
                if (type == null || !typeof(Def).IsAssignableFrom(type))
                    return new DefTranslationPathResult { Path = key, Reason = "missing_def_type" };
                string normalized = (key ?? "").Replace("]", "").Replace('[', '.');
                if (TKeySystem.TryGetNormalizedPath(normalized, out string tkey)) normalized = tkey;
                string[] parts = normalized.Split('.');
                if (parts.Length < 2) return new DefTranslationPathResult { Path = key, Reason = "invalid_path" };
                parts[0] = BackCompatibility.BackCompatibleDefName(type, parts[0], true);
                if (type == typeof(RimWorld.ConceptDef)) normalized = string.Join(".", parts).Replace(".helpTexts.0", ".helpText");
                else normalized = string.Join(".", parts);
                Def root = GenDefDatabase.GetDefSilentFail(type, parts[0], false);
                var result = DefTranslationPathValidator.ValidateTarget(root, normalized, TranslationRuntimeResolver.ElementIndex, fullList);
                // Preserve the stable handle/TKey spelling where possible. Aliases are
                // emitted as actual fields, since Verse only accepts aliases at the leaf.
                if (result.Allowed && result.ResolvedPath != normalized && !normalized.Contains("-"))
                    result.Evidence = "loaded Def + native TKey/handle/LoadAlias resolution";
                return result;
            }
        }

        internal static TranslationRepairRecord RecordDefRepair(string defType, string key, string value, string source,
            DefTranslationPathResult result, string kind = "existing", string status = null, string appliedValue = null)
        {
            if (appliedValue == value) appliedValue = null;
            if (result.Allowed && key == result.Path && status == null && appliedValue == null) return null;
            string packageId = PackageForRepairFile(source);
            // Source bytes and provenance indexes are preserved by the background writer.
            // The hot validation path records identity only; it never reloads JSON or hashes files.
            string provenance = null;
            string modEvidence = CurrentRimWorldVersion;
            var record = new TranslationRepairRecord {
                DefType = defType, OriginalKey = key, NewKey = result.Allowed ? result.Path : null, Translation = value,
                SourceFile = source ?? "ATC:unknown-source", SourceKind = kind, GameVersion = CurrentRimWorldVersion,
                PackageId = packageId, Environment = SelfRepairEnvironment, ModVersionEvidence = modEvidence, Provenance = provenance,
                Reason = result.Reason, Status = status ?? (result.Allowed ? "已沿用修復" : result.Temporary ? "暫不適用" : "仍待處理"),
                Evidence = result.Evidence
            };
            record.AppliedTranslation = appliedValue;
            if (appliedValue != null) record.Evidence += "; description edge whitespace removed; original text retained";
            if (result.Reason == "native_injection_failed" || result.Reason == "config_error_regression")
            {
                Type nativeType = TranslationRuntimeResolver.DefType(defType);
                if (nativeType != null) record.NativeFailureKey = NativeCandidateIdentity(nativeType, key, value, source);
            }
            RepairJournal.Record(record);
            return record;
        }

        // Only the root Def description has Verse's edge-whitespace constraint.
        // Rule strings, nested descriptions and arbitrary text retain their spacing.
        internal static string PrepareDefTranslationText(DefTranslationPathResult check, string value)
        {
            if (!check.Allowed || check.ResolvedPath == null) return value;
            string[] parts = check.ResolvedPath.Split('.');
            if (parts.Length != 2 || parts[1] != "description") return value;
            string normalized = value?.Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                check.Reason = "empty_description";
                check.Evidence = "empty Def description rejected before native configuration checks";
                return value;
            }
            return normalized;
        }

        private static string PackageForRepairFile(string path)
        {
            if (!UnityData.IsInMainThread) return "unknown"; // Background records retain exact source path without live mod enumeration.
            string source = (path ?? "").Replace('\\', '/');
            var sourceOwner = LoadedModManager.RunningModsListForReading
                .Where(m => m != null && !string.IsNullOrEmpty(m.RootDir) &&
                    source.StartsWith(m.RootDir.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.RootDir.Length).FirstOrDefault();
            if (sourceOwner != null && !IsOwnedTranslationFile(path)) return sourceOwner.PackageId;
            string stem = Path.GetFileNameWithoutExtension(path ?? "") ?? "";
            var matches = LoadedModManager.RunningModsListForReading.Where(m => m != null && !string.IsNullOrEmpty(m.PackageId) &&
                stem.StartsWith(m.PackageId.Replace('.', '_'), StringComparison.OrdinalIgnoreCase)).ToList();
            return matches.Count == 1 ? matches[0].PackageId : "unknown";
        }

        internal static bool IsOwnedTranslationFile(string path)
        {
            // Native/runtime-only candidates can use labels such as ATC:baseline-restore.
            // Legacy .NET rejects these as paths; they never identify an owned disk file.
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return false;
            try
            {
                string root = Path.GetFullPath(Path.Combine(GetLocalPackPath(), "Languages")).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        internal static void InvalidateSelfRepair()
        {
            TranslationRuntimeResolver.Invalidate();
            repairEnvironment = null;
            lock (RepairGate) { repairedDirectories.Clear(); failedNativeCandidates.Clear(); }
            ResetMemoryDropStamps();
        }

        internal static string SelfRepairEnvironment
        {
            get
            {
                // The game-data epoch invalidates session deny sets separately. Stable identity
                // must not rescan every mod's Defs/Patches/Assemblies during a key validation.
                return repairEnvironment ?? (repairEnvironment = CurrentRimWorldVersion + ":" +
                    TranslationRepairJournal.Hash(typeof(Def).Assembly.ManifestModule.ModuleVersionId.ToString()));
            }
        }

        // Persistent identities must not contain process-randomized string hashes,
        // directory enumeration order or a session's cache-invalidation counter.
        private static string PersistentRepairTree(string root, string pattern)
        {
            if (!Directory.Exists(root)) return "missing";
            return TranslationRepairJournal.Hash(string.Join("\n", Directory.GetFiles(root, pattern, SearchOption.AllDirectories)
                .Select(path => Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant() + "|" + BuildFileSignature(path))
                .OrderBy(item => item, StringComparer.Ordinal)));
        }

        internal static void RecoverPreservedTranslations(string path, Dictionary<string, string> data)
        {
            if (!UnityData.IsInMainThread || !StructureReady || !IsOwnedTranslationFile(path)) return;
            foreach (var group in RepairJournal.ForSource(path).Where(r => r.OriginalKey != null && r.DefType != null &&
                r.SourceKind != "native-full-list" && r.SourceKind != "native-duplicate-list" && r.SourceKind != "native-manual-list" && r.SourceKind != "native-config-list").GroupBy(r => r.OriginalKey))
            {
                if (group.Select(candidate => candidate.Translation).Distinct(StringComparer.Ordinal).Count() != 1) continue;
                var r = group.First();
                if (r.OriginalKey == null || r.DefType == null || r.Status == "衝突" || r.Status == "保留候選") continue;
                var check = ValidateDefTranslation(r.DefType, r.OriginalKey);
                if (!check.Allowed || data.ContainsKey(check.Path)) continue;
                if (IsFailedNativeCandidate(TranslationRuntimeResolver.DefType(r.DefType), check.Path, r.Translation, path)) continue;
                data[check.Path] = r.Translation;
                RecordDefRepair(r.DefType, r.OriginalKey, r.Translation, path, check, "recovered", "已沿用修復");
            }
        }

        internal static bool HasPreservedTranslation(string path, string key)
            => RepairJournal.ForSource(path).Any(r => (r.OriginalKey == key || r.NewKey == key) && !string.IsNullOrEmpty(r.Translation));

        // Boundary operation, never a per-frame scan. Files and pending values are
        // revalidated after a restart, Def reload or a changed translation file.
        internal static void RepairOwnedTranslationDirectory(string languageRoot)
        {
            if (TranslationRepairController.IsRunning) return;
            if (!StructureReady || !Directory.Exists(languageRoot)) return;
            string signature = SelfRepairEnvironment + "|" + BuildXmlTreeFingerprint(languageRoot);
            lock (RepairGate)
                if (repairedDirectories.TryGetValue(languageRoot, out string previous) && previous == signature) return;
            foreach (string file in GetXmlFilesCached(Path.Combine(languageRoot, "DefInjected"), SearchOption.AllDirectories))
            {
                if (!IsOwnedTranslationFile(file)) continue;
                if (!TryLoadRawXmlFileToDict(file, out var data))
                {
                    PreserveRepairInput(file);
                    RepairJournal.Record(new TranslationRepairRecord { SourceFile = file, SourceKind = "generated-xml",
                        GameVersion = CurrentRimWorldVersion, Environment = SelfRepairEnvironment,
                        Status = "仍待處理", Reason = "malformed_xml", Translation = File.ReadAllText(file),
                        Evidence = "Original bytes archived; ambiguous XML is not discarded" });
                    continue;
                }
                int originalCount = data.Count;
                RecoverPreservedTranslations(file, data);
                var filtered = PrepareValidatedDefOutput(file, data);
                if (originalCount != data.Count || data.Count != filtered.Count || data.Any(p => !filtered.TryGetValue(p.Key, out string value) || value != p.Value))
                    SaveXml(file, data);
            }
            FlushSelfRepair();
            lock (RepairGate) repairedDirectories[languageRoot] = SelfRepairEnvironment + "|" + BuildXmlTreeFingerprint(languageRoot);
        }

        private static string NativeCandidateIdentity(Type type, string key, string value, string source)
            => TranslationRepairJournal.Hash(type?.FullName + "\n" + key + "\n" + value + "\n" +
                (source ?? "").Replace('\\', '/').ToLowerInvariant() + "\n" + SelfRepairEnvironment);

        internal static bool IsFailedNativeCandidate(Type type, string key, string value, string source)
        {
            lock (RepairGate)
            {
                string identity = NativeCandidateIdentity(type, key, value, source);
                return failedNativeCandidates.Contains(identity);
            }
        }

        internal static void RememberFailedNativeCandidate(Type type, string key, string value, string source)
        {
            lock (RepairGate) failedNativeCandidates.Add(NativeCandidateIdentity(type, key, value, source));
        }

        internal static void ValidateReceivedTranslationFile(string file)
        {
            if (!StructureReady || !IsOwnedTranslationFile(file) ||
                file.Replace('\\', '/').IndexOf("/DefInjected/", StringComparison.OrdinalIgnoreCase) < 0) return;
            PreserveRepairInput(file);
            if (TryLoadRawXmlFileToDict(file, out var data)) SaveXml(file, data);
        }

        // Called before a file replacement. Archive every original byte, including
        // duplicate XML nodes which a dictionary representation cannot retain.
        internal static void PreserveRepairInput(string path)
        {
            if (File.Exists(path) && IsOwnedTranslationFile(path)) RepairJournal.Backup(path);
        }

        private static void PreserveSkippedXmlValue(string path, string key, string value, string reason)
        {
            if (!IsOwnedTranslationFile(path)) return;
            RepairJournal.Record(new TranslationRepairRecord { SourceFile = path, OriginalKey = key,
                Translation = value, SourceKind = "writer-skipped", GameVersion = CurrentRimWorldVersion,
                Reason = reason, Status = "仍待處理", Evidence = "Preserved before XML writer rejection" });
        }

        internal static Dictionary<string, string> PrepareValidatedDefOutput(string path, Dictionary<string, string> data)
        {
            if (!UnityData.IsInMainThread || !StructureReady || !IsOwnedTranslationFile(path)) return data;
            string normalized = path.Replace('\\', '/');
            int marker = normalized.IndexOf("/DefInjected/", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return data;
            string type = normalized.Substring(marker + 13).Split('/')[0];
            var output = new Dictionary<string, string>(StringComparer.Ordinal);
            var canonicalTargets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in data)
            {
                var check = ValidateDefTranslation(type, pair.Key);
                string value = PrepareDefTranslationText(check, pair.Value);
                RecordDefRepair(type, pair.Key, pair.Value, path, check, "write", appliedValue: value);
                if (!check.Allowed) continue;
                string target = check.Path;
                if (canonicalTargets.TryGetValue(check.ResolvedPath, out string previousTarget))
                {
                    string previous = output[previousTarget];
                    RecordDefRepair(type, pair.Key, pair.Value, path, check, "write", previous == value ? "相同譯文合併" : "衝突", value);
                    RecordDefRepair(type, previousTarget, previous, path, check, "write-selected", "保留候選");
                    continue;
                }
                canonicalTargets[check.ResolvedPath] = target;
                output[target] = value;
            }
            // Commit preserved values BEFORE replacing XML. An interrupted write
            // can leave extra journal records, never lost translation values.
            FlushSelfRepair();
            return output;
        }

        internal static void FlushSelfRepair()
        {
            TranslationRepairJournal journal;
            lock (RepairGate) journal = repairJournal;
            journal?.Checkpoint();
        }
    }
}
