using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    // Runtime entry point for the opt-in feature. The manifest is an explicit
    // decision-approved allow-list; all validation happens before any patch is added.
    public static class HardcodedUiTargetedPatchManager
    {
        private const string HarmonyId = "MingYang.AutoTranslation.HardcodedUiTargetedPrototype";
        private const int ManifestVersion = 1;
        private const int MaximumTranslationsPerEntry = 32;
        private const int MaximumTranslationKeyLength = 64;
        private const int MaximumTextLength = 4096;

        private static readonly object Gate = new object();
        private static readonly Harmony Harmony = new Harmony(HarmonyId);
        private static readonly MethodInfo TranspilerMethod = AccessTools.Method(
            typeof(HardcodedUiTargetedPatchTranspiler),
            nameof(HardcodedUiTargetedPatchTranspiler.Transpile));
        private static readonly Dictionary<string, MethodBase> PatchedMethods =
            new Dictionary<string, MethodBase>(StringComparer.Ordinal);
        private static readonly HashSet<string> AppliedEntryIds =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> AppliedMethodSpecs = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<TaskCompletionSource<bool>> ReloadWaiters = new List<TaskCompletionSource<bool>>();
        private static IEnumerator<int> _applySteps;
        private static int _applyGeneration;
        private static int _lastApplyFrame = -1;
        private static bool _recoveringApply;
        private static HardcodedUiPatchManifest _recoveryManifest;
        private static int _initialized;
        private static int _loading;
        private static int _reloadGeneration;
        private static string _status = "not initialized";
        private static int _candidateCount;
        private static int _appliedCount;
        private static string _lastError = string.Empty;

        public static bool IsInitialized
        {
            get { return Interlocked.CompareExchange(ref _initialized, 0, 0) == 1; }
        }

        public static bool IsLoading
        {
            get { return Interlocked.CompareExchange(ref _loading, 0, 0) == 1; }
        }

        public static int CandidateCount
        {
            get { return Interlocked.CompareExchange(ref _candidateCount, 0, 0); }
        }

        public static int AppliedCount
        {
            get { return Interlocked.CompareExchange(ref _appliedCount, 0, 0); }
        }

        public static string Status
        {
            get
            {
                lock (Gate) return _status ?? string.Empty;
            }
        }

        public static string LastError
        {
            get
            {
                lock (Gate) return _lastError ?? string.Empty;
            }
        }

        public static string ManifestPath
        {
            get { return Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "HardcodedUiPatchPrototype.json"); }
        }

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 1) return;
            RequestReload();
        }

        public static Task<bool> RequestReloadAsync()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ATC_Dispatcher.RunOnMainThread(() => StartReload(completion));
            return completion.Task;
        }

        public static void RequestReload()
        {
            ATC_Dispatcher.RunOnMainThread(() => StartReload(null));
        }

        private static void StartReload(TaskCompletionSource<bool> completion)
        {
            if (completion != null) ReloadWaiters.Add(completion);
            int generation = Interlocked.Increment(ref _reloadGeneration);
            if (Interlocked.Exchange(ref _loading, 1) == 1) return;
            SetStatus("loading", string.Empty);
            if (AutoTranslatorMod.Settings == null || !AutoTranslatorMod.Settings.EnableHardcodedUiPrototype)
            {
                _applyGeneration = generation;
                _replacementStarted = false;
                _recoveringApply = false;
                _applySteps = ClearAppliedStateSteps();
                return;
            }
            Task.Run(() =>
            {
                HardcodedUiPatchManifest manifest = null;
                string error = string.Empty;
                try { manifest = LoadManifestFromDisk(out error); }
                catch (Exception ex) { error = ex.Message; }
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    if (generation != Interlocked.CompareExchange(ref _reloadGeneration, 0, 0))
                    {
                        Interlocked.Exchange(ref _loading, 0);
                        RequestReload();
                        return;
                    }
                    _applyGeneration = generation;
                    _recoveryManifest = _lastAppliedManifest;
                    _replacementStarted = false;
                    _recoveringApply = false;
                    _applySteps = AutoTranslatorMod.Settings == null || !AutoTranslatorMod.Settings.EnableHardcodedUiPrototype
                        ? ClearAppliedStateSteps() : ApplyManifest(manifest, error);
                });
            });
        }

        // Called from the existing dispatcher pump, at most once per Unity frame.
        internal static void PumpReload()
        {
            if (_applySteps == null || _lastApplyFrame == UnityEngine.Time.frameCount) return;
            _lastApplyFrame = UnityEngine.Time.frameCount;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            bool done = false;
            try
            {
                do { done = !_applySteps.MoveNext(); }
                while (!done && timer.ElapsedMilliseconds < 4);
            }
            catch (Exception ex)
            {
                _applySteps.Dispose();
                Log.Warning("[AutoTranslationCore] Hardcoded UI manifest apply failed: " + ex);
                if (!_recoveringApply && _replacementStarted)
                {
                    _recoveringApply = true;
                    _applySteps = RestorePreviousSteps(_recoveryManifest);
                    return;
                }
                SetStatus("error", ex.Message);
                done = true;
            }
            if (!done) return;
            _applySteps.Dispose();
            _applySteps = null;
            Interlocked.Exchange(ref _loading, 0);
            if (_applyGeneration != Interlocked.CompareExchange(ref _reloadGeneration, 0, 0))
            {
                RequestReload();
                return;
            }
            bool success = !_recoveringApply && (Status == "active" || Status == "ready" ||
                (Status == "disabled" && PatchedMethods.Count == 0));
            lock (Gate)
            {
                foreach (var waiter in ReloadWaiters) waiter.TrySetResult(success);
                ReloadWaiters.Clear();
            }
        }

        private static IEnumerator<int> RestorePreviousSteps(HardcodedUiPatchManifest previous)
        {
            using (var cleanup = ClearAppliedStateSteps()) while (cleanup.MoveNext()) yield return 0;
            if (previous != null && PatchedMethods.Count == 0)
            {
                _restoring = true;
                try
                {
                    using (var restore = ApplyManifest(previous, string.Empty))
                        while (restore.MoveNext()) yield return 0;
                }
                finally { _restoring = false; }
            }
            SetStatus("error", "new manifest could not be applied; previous manifest restoration attempted");
        }

        private static HardcodedUiPatchManifest LoadManifestFromDisk(out string error)
        {
            error = string.Empty;
            string path = ManifestPath;
            if (!File.Exists(path))
            {
                error = "manifest missing";
                return null;
            }

            string json;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                {
                    json = reader.ReadToEnd();
                }
            }

            if (json.Length == 0)
            {
                error = "manifest is empty";
                return null;
            }

            if (!TryValidateManifestJson(json, out error)) return null;

            HardcodedUiPatchManifest manifest = JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(
                json,
                new JsonSerializerSettings
                {
                    MaxDepth = 32
                });
            if (manifest == null) error = "manifest is empty";
            return manifest;
        }

        private static bool TryValidateManifestJson(string json, out string error)
        {
            error = string.Empty;
            try
            {
                // Newtonsoft.Json 13.0.4 used by the mod does not expose the
                // newer DuplicatePropertyNameHandling setting. Track object
                // scopes here so duplicate approval/translation fields cannot
                // be silently overwritten during deserialization.
                Stack<HashSet<string>> containers = new Stack<HashSet<string>>();
                using (JsonTextReader reader = new JsonTextReader(new StringReader(json)))
                {
                    while (reader.Read())
                    {
                        switch (reader.TokenType)
                        {
                            case JsonToken.StartObject:
                                containers.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                                if (containers.Count > 32)
                                {
                                    error = "manifest nesting is too deep";
                                    return false;
                                }
                                break;
                            case JsonToken.StartArray:
                                // A null marker separates object scopes for array
                                // elements; properties in sibling objects are independent.
                                containers.Push(null);
                                if (containers.Count > 32)
                                {
                                    error = "manifest nesting is too deep";
                                    return false;
                                }
                                break;
                            case JsonToken.PropertyName:
                                if (containers.Count == 0 || containers.Peek() == null) break;
                                string propertyName = reader.Value as string ?? string.Empty;
                                if (!containers.Peek().Add(propertyName))
                                {
                                    error = "duplicate manifest property: " + propertyName;
                                    return false;
                                }
                                break;
                            case JsonToken.EndObject:
                            case JsonToken.EndArray:
                                if (containers.Count > 0) containers.Pop();
                                break;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "invalid manifest JSON: " + ex.Message;
                return false;
            }
        }

        public static string GetStatusLine()
        {
            string error = LastError;
            string status = Status;
            if (!string.IsNullOrWhiteSpace(error)) status += ": " + error;
            return string.Format("{0} ({1}/{2})", status, AppliedCount, CandidateCount);
        }

        private static IEnumerator<int> ApplyManifest(HardcodedUiPatchManifest manifest, string loadError)
        {
            if (manifest == null)
            {
                SetStatus("unavailable", loadError);
                yield break;
            }

            if (manifest.ManifestVersion != ManifestVersion)
            {
                SetStatus("rejected", "unsupported manifest version");
                yield break;
            }

            if (!manifest.Approved)
            {
                SetStatus("awaiting approval", "manifest approved=false");
                yield break;
            }

            List<HardcodedUiPatchEntry> entries = (manifest.Entries ?? new List<HardcodedUiPatchEntry>())
                .Where(entry => entry != null && entry.Enabled)
                .ToList();
            Interlocked.Exchange(ref _candidateCount, entries.Count);
            if (entries.Count == 0)
            {
                using (var cleanup = ClearAppliedStateSteps()) while (cleanup.MoveNext()) yield return 0;
                if (PatchedMethods.Count != 0)
                {
                    SetStatus("error", "targeted patch cleanup is still pending");
                    yield break;
                }
                _lastAppliedManifest = manifest;
                SetStatus("ready", string.Empty);
                yield break;
            }

            string duplicateEntryId;
            if (HardcodedUiMethodIdentity.TryFindDuplicateEntryId(
                entries.Select(entry => entry.EntryId), out duplicateEntryId))
            {
                SetStatus("rejected", "duplicate entry id");
                Log.Warning("[AutoTranslationCore] Hardcoded UI manifest rejected: duplicate entry id " + duplicateEntryId);
                yield break;
            }

            var validation = new ReloadValidationCache();
            Dictionary<string, List<PreparedEntry>> grouped =
                new Dictionary<string, List<PreparedEntry>>(StringComparer.Ordinal);
            foreach (HardcodedUiPatchEntry entry in entries)
            {
                yield return 0;
                MethodBase method;
                string reason;
                bool prepared;
                try
                {
                    prepared = TryPrepareEntry(entry, validation, out method, out reason);
                }
                catch (Exception ex)
                {
                    method = null;
                    reason = "validation exception: " + ex.Message;
                    prepared = false;
                }

                if (!prepared)
                {
                    LogValidationFailure(entry, reason);
                    continue;
                }

                // Group by the manifest's complete assembly identity. A bare
                // method signature is not unique across third-party DLLs.
                string methodKey = HardcodedUiMethodIdentity.CreateMethodTargetIdentity(
                    entry.PackageId,
                    entry.AssemblyRelativePath,
                    entry.AssemblySha256,
                    entry.AssemblyMvid,
                    entry.MethodSignature,
                    entry.MethodMetadataToken,
                    entry.MethodIlFingerprint);
                List<PreparedEntry> list;
                if (!grouped.TryGetValue(methodKey, out list))
                {
                    list = new List<PreparedEntry>();
                    grouped[methodKey] = list;
                }
                list.Add(new PreparedEntry { Entry = entry, Method = method });
            }

            foreach (var pair in grouped.ToList())
            {
                yield return 0;
                var specs = pair.Value.Select(item => new HardcodedUiTranspileSpec
                { EntryId = item.Entry.EntryId, Literal = item.Entry.Literal, LiteralOrdinal = item.Entry.LiteralOrdinal }).ToList();
                var plan = HardcodedUiDisplayCallPlanner.Plan(validation.GetBody(pair.Value[0].Method), specs);
                var matched = new HashSet<string>(plan.SelectMany(site => site.Arguments.Values).Select(spec => spec.EntryId));
                foreach (var item in pair.Value.Where(item => !matched.Contains(item.Entry.EntryId)))
                    LogValidationFailure(item.Entry, "no unambiguous supported display argument");
                pair.Value.RemoveAll(item => !matched.Contains(item.Entry.EntryId));
                if (pair.Value.Count == 0) grouped.Remove(pair.Key);
            }
            if (grouped.Count == 0)
            {
                SetStatus("rejected", "no compatible display arguments; existing translations retained");
                yield break;
            }
            HardcodedUiPatchManifest previous = _lastAppliedManifest;
            _replacementStarted = true;
            var desiredSpecs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in grouped)
            {
                yield return 0;
                desiredSpecs[pair.Key] = JsonConvert.SerializeObject(pair.Value.OrderBy(item => item.Entry.EntryId, StringComparer.Ordinal)
                    .Select(item => new { item.Entry.EntryId, item.Entry.Literal, item.Entry.LiteralOrdinal, item.Entry.CallSignature }));
            }
            foreach (var old in PatchedMethods.ToList())
            {
                yield return 0;
                string signature;
                if (desiredSpecs.TryGetValue(old.Key, out signature) && AppliedMethodSpecs.ContainsKey(old.Key) &&
                    AppliedMethodSpecs[old.Key] == signature &&
                    HarmonyLib.Harmony.GetPatchInfo(old.Value)?.Transpilers.Any(patch => patch.owner == HarmonyId) == true) continue;
                Harmony.Unpatch(old.Value, TranspilerMethod);
                HardcodedUiTargetedPatchTranspiler.RemoveSpecs(old.Value);
                PatchedMethods.Remove(old.Key);
                AppliedMethodSpecs.Remove(old.Key);
            }
            AppliedEntryIds.Clear();
            Interlocked.Exchange(ref _appliedCount, 0);
            bool rollbackFailed = false;
            bool applicationFailed = false;
            foreach (KeyValuePair<string, List<PreparedEntry>> group in grouped)
            {
                yield return 0;
                if (PatchedMethods.ContainsKey(group.Key))
                {
                    foreach (var prepared in group.Value) AppliedEntryIds.Add(prepared.Entry.EntryId);
                    Interlocked.Add(ref _appliedCount, group.Value.Count);
                    continue;
                }
                if (group.Value.GroupBy(item => item.Entry.LiteralOrdinal).Count() != group.Value.Count)
                {
                    Log.Warning("[AutoTranslationCore] Hardcoded UI method skipped: duplicate literal ordinal.");
                    applicationFailed = true;
                    continue;
                }

                bool patchMayBeInstalled = false;
                try
                {
                    HardcodedUiTargetedPatchTranspiler.SetSpecs(
                        group.Value[0].Method,
                        group.Value.Select(item => new HardcodedUiTranspileSpec
                        {
                            EntryId = item.Entry.EntryId,
                            Literal = item.Entry.Literal,
                            LiteralOrdinal = item.Entry.LiteralOrdinal,
                            CallSignature = item.Entry.CallSignature
                        }));

                    patchMayBeInstalled = true;
                    Harmony.Patch(group.Value[0].Method, transpiler: new HarmonyMethod(TranspilerMethod));
                    PatchedMethods[group.Key] = group.Value[0].Method;
                    AppliedMethodSpecs[group.Key] = desiredSpecs[group.Key];

                    // Mark entries only after the whole method patch succeeds.
                    foreach (PreparedEntry prepared in group.Value)
                        AppliedEntryIds.Add(prepared.Entry.EntryId);
                    Interlocked.Add(ref _appliedCount, group.Value.Count);
                }
                catch (Exception ex)
                {
                    applicationFailed = true;
                    bool rollbackSucceeded = !patchMayBeInstalled;
                    if (patchMayBeInstalled)
                    {
                        try
                        {
                            Harmony.Unpatch(group.Value[0].Method, TranspilerMethod);
                            rollbackSucceeded = true;
                        }
                        catch (Exception unpatchEx)
                        {
                            // Preserve ownership so a later reload can retry cleanup.
                            rollbackFailed = true;
                            PatchedMethods[group.Key] = group.Value[0].Method;
                            Log.Warning("[AutoTranslationCore] Hardcoded UI rollback failed: " + unpatchEx);
                        }
                    }
                    foreach (PreparedEntry prepared in group.Value)
                        AppliedEntryIds.Remove(prepared.Entry.EntryId);
                    if (rollbackSucceeded)
                    {
                        PatchedMethods.Remove(group.Key);
                        AppliedMethodSpecs.Remove(group.Key);
                        try { HardcodedUiTargetedPatchTranspiler.RemoveSpecs(group.Value[0].Method); }
                        catch (Exception removeSpecsEx)
                        {
                            Log.Warning("[AutoTranslationCore] Hardcoded UI spec cleanup failed: " + removeSpecsEx);
                        }
                    }
                    Log.Warning("[AutoTranslationCore] Hardcoded UI patch failed: " + ex.Message);
                }
            }

            if (rollbackFailed || applicationFailed)
            {
                using (var cleanup = ClearAppliedStateSteps()) while (cleanup.MoveNext()) yield return 0;
                if (!_restoring && previous != null && PatchedMethods.Count == 0)
                {
                    using (var restore = RestorePreviousSteps(previous)) while (restore.MoveNext()) yield return 0;
                    yield break;
                }
                SetStatus("error", PatchedMethods.Count > 0
                    ? "targeted patch rollback is still pending"
                    : "targeted patch application failed");
                yield break;
            }

            // Rebuild one complete snapshot after all method groups have been applied.
            Dictionary<string, string> allTranslations = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, string> allSources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (HardcodedUiPatchEntry entry in entries)
            {
                yield return 0;
                if (!IsEntryApplied(entry)) continue;
                string translated = SelectTranslation(entry);
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    allTranslations[entry.EntryId] = translated;
                    allSources[entry.EntryId] = entry.Literal;
                }
            }
            HardcodedUiRuntime.ReplaceSnapshot(allTranslations, allSources);
            _lastAppliedManifest = manifest;
            SetStatus(PatchedMethods.Count > 0 ? "active" : "no compatible entries", string.Empty);
        }

        private static bool IsEntryApplied(HardcodedUiPatchEntry entry)
        {
            return entry != null && AppliedEntryIds.Contains(entry.EntryId);
        }

        private static string SelectTranslation(HardcodedUiPatchEntry entry)
        {
            if (entry == null || entry.Translations == null) return string.Empty;
            TargetLanguage target = AutoTranslatorMod.Settings != null
                ? AutoTranslatorMod.Settings.TargetLang
                : TargetLanguage.Traditional;
            string folder = AutoTranslatorScanner.GetFolderNameByLanguage(target);
            string value;
            if (entry.Translations.TryGetValue(folder, out value)) return SanitizeTranslation(value, entry.Literal);
            if (entry.Translations.TryGetValue(target.ToString(), out value)) return SanitizeTranslation(value, entry.Literal);

            foreach (KeyValuePair<string, string> pair in entry.Translations)
            {
                if (string.Equals(pair.Key, folder, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pair.Key, target.ToString(), StringComparison.OrdinalIgnoreCase))
                    return SanitizeTranslation(pair.Value, entry.Literal);
            }
            return string.Empty;
        }

        private static string SanitizeTranslation(string value, string source)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTextLength) return string.Empty;
            if (value.IndexOf('\0') >= 0) return string.Empty;
            return string.Equals(value, source, StringComparison.Ordinal) ? string.Empty : value;
        }

        private static bool TryPrepareEntry(HardcodedUiPatchEntry entry, ReloadValidationCache validation, out MethodBase method, out string reason)
        {
            method = null;
            reason = string.Empty;
            if (entry == null) { reason = "null entry"; return false; }
            if (string.IsNullOrWhiteSpace(entry.EntryId) || entry.EntryId.Length > 200 ||
                string.IsNullOrWhiteSpace(entry.PackageId) ||
                string.IsNullOrWhiteSpace(entry.Literal) || entry.Literal.Length > MaximumTextLength ||
                entry.LiteralOrdinal < 0)
            {
                reason = "invalid entry fields";
                return false;
            }
            if (entry.Translations != null)
            {
                if (entry.Translations.Count > MaximumTranslationsPerEntry)
                {
                    reason = "too many translations";
                    return false;
                }
                foreach (KeyValuePair<string, string> translation in entry.Translations)
                {
                    if (string.IsNullOrWhiteSpace(translation.Key) ||
                        translation.Key.Length > MaximumTranslationKeyLength ||
                        (!string.IsNullOrEmpty(translation.Value) && translation.Value.Length > MaximumTextLength))
                    {
                        reason = "invalid translation field";
                        return false;
                    }
                }
            }
            if (!HardcodedUiMethodIdentity.IsDeterministicEntryId(
                entry.EntryId,
                entry.PackageId,
                entry.AssemblyRelativePath,
                entry.MethodSignature,
                entry.LiteralOrdinal,
                entry.Literal))
            {
                reason = "entry id does not match deterministic identity";
                return false;
            }
            if (!IsSha256(entry.AssemblySha256) || !IsSha256(entry.MethodIlFingerprint) ||
                !Guid.TryParse(entry.AssemblyMvid, out _))
            {
                reason = "missing or malformed integrity fingerprint";
                return false;
            }

            ModMetaData mod = ModLister.AllInstalledMods.FirstOrDefault(candidate =>
                candidate != null && candidate.Active &&
                string.Equals(candidate.PackageId, entry.PackageId, StringComparison.OrdinalIgnoreCase));
            if (mod == null || mod.RootDir == null)
            {
                reason = "target Mod is not active";
                return false;
            }

            string assemblyPath;
            if (!TryResolveAssemblyPath(mod.RootDir.FullName, entry.AssemblyRelativePath, out assemblyPath))
            {
                reason = "assembly path escapes mod root";
                return false;
            }
            if (!File.Exists(assemblyPath) ||
                !string.Equals(validation.GetFileHash(assemblyPath), entry.AssemblySha256, StringComparison.OrdinalIgnoreCase))
            {
                reason = "assembly hash mismatch";
                return false;
            }

            Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly =>
                {
                    try
                    {
                        return !string.IsNullOrWhiteSpace(assembly.Location) &&
                            string.Equals(Path.GetFullPath(assembly.Location), assemblyPath, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                });
            if (loaded == null && Guid.TryParse(entry.AssemblyMvid, out Guid expectedMvid))
            {
                loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                {
                    try { return assembly.ManifestModule.ModuleVersionId == expectedMvid; }
                    catch { return false; }
                });
            }
            if (loaded == null)
            {
                reason = "assembly is not loaded; runtime will not load third-party code";
                return false;
            }
            if (!string.Equals(loaded.ManifestModule.ModuleVersionId.ToString("D"), entry.AssemblyMvid, StringComparison.OrdinalIgnoreCase))
            {
                reason = "assembly MVID mismatch";
                return false;
            }

            string methodCacheKey = loaded.ManifestModule.ModuleVersionId.ToString("D") + "\n" +
                entry.DeclaringType + "\n" + entry.MethodSignature + "\n" + entry.MethodMetadataToken;
            if (!validation.Methods.TryGetValue(methodCacheKey, out method))
            {
            Type declaringType = loaded.GetType(entry.DeclaringType, false, false);
            if (declaringType == null)
            {
                reason = "declaring type not found";
                return false;
            }
            const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static |
                                          BindingFlags.DeclaredOnly;
            method = declaringType.GetMethods(declared)
                .Cast<MethodBase>()
                .Concat(declaringType.GetConstructors(declared).Cast<MethodBase>())
                .FirstOrDefault(candidate =>
                    string.Equals(HardcodedUiMethodIdentity.GetMethodSignature(candidate), entry.MethodSignature, StringComparison.Ordinal) &&
                    (entry.MethodMetadataToken <= 0 || candidate.MetadataToken == entry.MethodMetadataToken));
                validation.Methods[methodCacheKey] = method;
            }
            if (method == null || method.Name != entry.MethodName)
            {
                reason = "method signature mismatch";
                return false;
            }
            if (entry.MethodMetadataToken <= 0 || method.MetadataToken != entry.MethodMetadataToken)
            {
                reason = "method metadata token mismatch";
                method = null;
                return false;
            }
            if (!string.Equals(validation.GetMethodHash(method), entry.MethodIlFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                reason = "method IL fingerprint mismatch";
                method = null;
                return false;
            }
            if (!HasUniqueLiteral(validation.GetBody(method), entry))
            {
                reason = "literal/ordinal pattern mismatch";
                method = null;
                return false;
            }

            Patches patches = Harmony.GetPatchInfo(method);
            if (patches != null && patches.Transpilers != null &&
                patches.Transpilers.Any(patch => patch != null && !string.Equals(patch.owner, HarmonyId, StringComparison.Ordinal)))
            {
                reason = "method already has another Harmony transpiler";
                method = null;
                return false;
            }
            return true;
        }

        private static bool HasUniqueLiteral(List<CodeInstruction> instructions, HardcodedUiPatchEntry entry)
        {
            int ordinal = -1;
            int matches = 0;
            for (int i = 0; i < instructions.Count; i++)
            {
                CodeInstruction instruction = instructions[i];
                if (instruction.opcode != OpCodes.Ldstr || !(instruction.operand is string)) continue;
                ordinal++;
                if (ordinal != entry.LiteralOrdinal || !string.Equals((string)instruction.operand, entry.Literal, StringComparison.Ordinal)) continue;

                if (!string.IsNullOrWhiteSpace(entry.CallSignature))
                {
                    int next = i + 1;
                    while (next < instructions.Count && instructions[next].opcode == OpCodes.Nop) next++;
                    MethodBase call = next < instructions.Count ? instructions[next].operand as MethodBase : null;
                    if (!HardcodedUiCallTarget.Matches(call, entry)) continue;
                }
                matches++;
            }
            return matches == 1;
        }

        private static bool TryResolveAssemblyPath(string root, string relative, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative)) return false;
            string normalized = HardcodedUiMethodIdentity.NormalizeRelativePath(relative);
            if (normalized.Length == 0 || normalized.IndexOf("../", StringComparison.Ordinal) >= 0 || normalized == "..") return false;
            try
            {
                string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
                fullPath = candidate;
                return true;
            }
            catch (SecurityException) { return false; }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }

        private static IEnumerator<int> ClearAppliedStateSteps()
        {
            foreach (KeyValuePair<string, MethodBase> pair in PatchedMethods.ToList())
            {
                yield return 0;
                bool unpatchSucceeded = false;
                try
                {
                    Harmony.Unpatch(pair.Value, TranspilerMethod);
                    unpatchSucceeded = true;
                }
                catch (Exception ex)
                {
                    Log.Warning("[AutoTranslationCore] Hardcoded UI unpatch failed: " + ex.Message);
                }

                if (!unpatchSucceeded) continue;

                try
                {
                    HardcodedUiTargetedPatchTranspiler.RemoveSpecs(pair.Value);
                    PatchedMethods.Remove(pair.Key);
                    AppliedMethodSpecs.Remove(pair.Key);
                }
                catch (Exception ex)
                {
                    // Keep the method in PatchedMethods so a later reload can retry
                    // spec cleanup instead of silently losing the ownership record.
                    Log.Warning("[AutoTranslationCore] Hardcoded UI spec cleanup failed: " + ex.Message);
                }
            }
            AppliedEntryIds.Clear();
            HardcodedUiRuntime.ClearSnapshot();
            Interlocked.Exchange(ref _candidateCount, 0);
            Interlocked.Exchange(ref _appliedCount, 0);
            SetStatus("disabled", string.Empty);
        }

        private static void SetStatus(string status, string error)
        {
            lock (Gate)
            {
                _status = status ?? string.Empty;
                _lastError = error ?? string.Empty;
            }
        }

        private static void LogValidationFailure(HardcodedUiPatchEntry entry, string reason)
        {
            Log.Warning("[AutoTranslationCore] Hardcoded UI entry rejected (" +
                (entry != null ? entry.EntryId : "unknown") + "): " + reason);
        }

        private static HardcodedUiPatchManifest _lastAppliedManifest;
        private static bool _restoring;
        private static bool _replacementStarted;

        private sealed class ReloadValidationCache
        {
            private readonly Dictionary<string, string> _fileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<MethodBase, string> _methodHashes = new Dictionary<MethodBase, string>();
            private readonly Dictionary<MethodBase, List<CodeInstruction>> _bodies = new Dictionary<MethodBase, List<CodeInstruction>>();
            public readonly Dictionary<string, MethodBase> Methods = new Dictionary<string, MethodBase>();
            public string GetFileHash(string path)
            {
                if (!_fileHashes.TryGetValue(path, out string hash))
                    _fileHashes[path] = hash = HardcodedUiMethodIdentity.ComputeFileSha256(path);
                return hash;
            }
            public string GetMethodHash(MethodBase method)
            {
                if (!_methodHashes.TryGetValue(method, out string hash))
                    _methodHashes[method] = hash = HardcodedUiMethodIdentity.ComputeMethodIlFingerprint(method);
                return hash;
            }
            public List<CodeInstruction> GetBody(MethodBase method)
            {
                if (!_bodies.TryGetValue(method, out List<CodeInstruction> body))
                    _bodies[method] = body = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
                return body;
            }
        }

        private sealed class PreparedEntry
        {
            public HardcodedUiPatchEntry Entry;
            public MethodBase Method;
        }
    }
}
