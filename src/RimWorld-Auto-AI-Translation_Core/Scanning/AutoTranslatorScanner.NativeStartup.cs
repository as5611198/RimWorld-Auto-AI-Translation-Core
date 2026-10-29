using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private sealed class NativeStartupPreparation
        {
            internal int Epoch, Cancellation;
            internal LoadedLanguage Language;
            internal TargetLanguage Target;
            internal MemoryDropPayload Payload;
            internal string InputSignature;
            internal Dictionary<string, string> FileHashes;
            internal string Fallback;
            internal long PreparationMs;
        }
        private static Task<NativeStartupPreparation> nativeStartupPreparation;
        private static NativeStartupPreparation nativeStartupApplied;
        private static MemoryDropPayload nativeStartupDeferred;
        internal static bool NativeStartupHasDeferred => nativeStartupDeferred != null;
        private static bool nativeStartupAttempted;

        internal static void ResetNativeStartup()
        {
            nativeStartupPreparation = null; nativeStartupApplied = null; nativeStartupDeferred = null; nativeStartupAttempted = false;
        }

        // Called after the early native pass. Only XML/receipt IO runs here;
        // there is no wait for live Def validation or a main-thread callback.
        internal static void PrepareNativeStartup(LoadedLanguage language)
        {
            if (nativeStartupAttempted || nativeStartupPreparation != null || AutoTranslatorMod.Settings == null ||
                language == null || !ReferenceEquals(language, LanguageDatabase.activeLanguage)) return;
            var preparation = new NativeStartupPreparation { Epoch = GameDataEpoch, Language = language,
                Target = AutoTranslatorMod.Settings.TargetLang, Cancellation = AutoTranslatorSettings.PipelineCancellationGeneration };
            nativeStartupPreparation = Task.Run(() => {
                var timer = Stopwatch.StartNew();
                try
                {
                    // Missing, corrupt or changed baseline never enters the fast
                    // path, even if an old parsed/runtime cache is still present.
                    if (!NativeStartupCurrent(preparation) || !HasCompletedStartupRepair(preparation.Target))
                        preparation.Fallback = "no unchanged verified baseline";
                    else
                    {
                        preparation.Payload = BuildMemoryDropPayload(false);
                        if (preparation.Payload == null) preparation.Fallback = "no prepared translation payload";
                        else
                        {
                            preparation.InputSignature = preparation.Payload.InputSignature;
                            preparation.FileHashes = preparation.Payload.FileHashes;
                        }
                    }
                }
                catch (Exception error) { preparation.Fallback = error.GetType().Name + ": " + error.Message; }
                preparation.PreparationMs = timer.ElapsedMilliseconds;
                return preparation;
            });
        }

        private static bool NativeStartupCurrent(NativeStartupPreparation preparation)
            => preparation != null && preparation.Epoch == GameDataEpoch && AutoTranslatorMod.Settings != null &&
                ReferenceEquals(preparation.Language, LanguageDatabase.activeLanguage) &&
                preparation.Target == AutoTranslatorMod.Settings.TargetLang &&
                preparation.Cancellation == AutoTranslatorSettings.PipelineCancellationGeneration &&
                !AutoTranslatorSettings.IsCancellationRequested;

        // Postfix of the native final pass: all implied Defs exist, third-party
        // translations are applied, and startup static constructors have not run.
        internal static void TryApplyNativeStartup(LoadedLanguage language)
        {
            if (nativeStartupAttempted || !UnityData.IsInMainThread || !StructureReady ||
                !ReferenceEquals(language, LanguageDatabase.activeLanguage)) return;
            nativeStartupAttempted = true;
            var task = nativeStartupPreparation;
            nativeStartupPreparation = null; // Consume once; do not retain the full XML payload for the session.
            if (task == null || task.Status != TaskStatus.RanToCompletion)
            {
                Log.Message("[ATC] Native startup injection deferred: preparation not ready; no blocking wait.");
                return;
            }
            var preparation = task.GetAwaiter().GetResult(); // Already completed; never waits for a pump.
            if (!NativeStartupCurrent(preparation) || preparation.Payload == null)
            {
                Log.Message("[ATC] Native startup injection deferred: " + (preparation.Fallback ?? "obsolete preparation") + ".");
                return;
            }
            var payload = preparation.Payload;
            var timer = Stopwatch.StartNew();
            MemoryDropApplyState state = null;
            try
            {
                // Recheck on the game thread immediately before mutation. Cache
                // identity alone cannot authorize changed source files.
                if (!NativeStartupPayloadCurrent(preparation) ||
                    !HasCompletedStartupRepair(preparation.Target) || MemoryDropBusyExceptQueuedKeyed())
                {
                    Log.Message("[ATC] Native startup injection deferred: files changed or another apply is pending.");
                    return;
                }
                state = ApplyNativeStartupPayload(payload, true);
                if (state == null) return;
                if (!NativeStartupCurrent(preparation) || !NativeStartupPayloadCurrent(preparation)) return;
                nativeStartupApplied = preparation;
                if (state.NativeDeferredDefs.Count != 0)
                    nativeStartupDeferred = new MemoryDropPayload {
                        InputSignature = payload.InputSignature, FileHashes = payload.FileHashes,
                        TargetFolder = payload.TargetFolder, LangRoot = payload.LangRoot, KeyedPath = payload.KeyedPath,
                        PackageScoped = true, PackageId = "ATC:native-late-defs", DefsByType = state.NativeDeferredDefs
                    };
                lock (_pendingInjectLock) _pendingKeyedMemoryDrop = false;
                MarkKeyedMemoryDropStamp(payload.KeyedPath, payload.TargetFolder);
                TranslationRepairController.AcceptNativeStartupInjection();
                Log.Message("[ATC] Native startup injection completed before static constructors: keyed=" + state.InjectedKeyed +
                    ", defs=" + state.InjectedDefs + ", rejected=" + state.RejectedDefs + ", failed=" + state.FailedDefs +
                    ", preparationMs=" + preparation.PreparationMs + ", applyMs=" + timer.ElapsedMilliseconds + ".");
            }
            catch (Exception error)
            {
                // Per-entry guard already restores rejected writes. Retain any
                // successful text and let the ordinary startup pipeline retry.
                Log.Warning("[ATC] Native startup injection deferred after error: " + error.Message);
            }
            finally
            {
                preparation.Payload = null; // Only the small receipt is needed to skip the later stage.
                if (state != null && ReferenceEquals(_activeMemoryDropApply, state)) _activeMemoryDropApply = null;
            }
        }

        private static bool MemoryDropBusyExceptQueuedKeyed()
        {
            lock (_pendingInjectLock) return _activeMemoryDropApply != null || _pendingMemoryDropPayload != null ||
                _memoryDropPrepareRunning != 0 || _pendingMemoryDrop || _pendingMemoryDropPackageIds.Count != 0;
        }

        private static void RememberNativeDeferredCandidate(MemoryDropApplyState state, MemoryDropDefTypePayload source, string key)
        {
            if (state.NativeDeferredDefs == null) return;
            var bucket = state.NativeDeferredDefs.Find(t => t.DefTypeName == source.DefTypeName);
            if (bucket == null)
            {
                bucket = new MemoryDropDefTypePayload { DefTypeName = source.DefTypeName };
                state.NativeDeferredDefs.Add(bucket);
            }
            bucket.Data[key] = source.Data[key];
            if (source.Sources.TryGetValue(key, out string file)) bucket.Sources[key] = file;
            if (source.FullLists.TryGetValue(key, out var list)) bucket.FullLists[key] = new List<string>(list);
        }

        private static MemoryDropApplyState ApplyNativeStartupPayload(MemoryDropPayload payload, bool captureDeferred)
        {
            payload.Epoch = GameDataEpoch; payload.Language = LanguageDatabase.activeLanguage;
            payload.CancellationGeneration = AutoTranslatorSettings.PipelineCancellationGeneration;
            BeginMemoryDropApply(payload);
            var state = _activeMemoryDropApply;
            if (state == null) return null;
            if (captureDeferred) state.NativeDeferredDefs = new List<MemoryDropDefTypePayload>();
            try
            {
                using (TranslationFrameBudget.ForSynchronousNativeStartup())
                {
                    while (ApplyKeyedBatch(state, payload.Language)) { }
                    while (ApplyDefBatch(state, payload.Language)) { }
                    while (ApplyNativeMemoryBatch(state)) { }
                    while (ApplyMemoryNormalizationBatch(state)) { }
                    PrepareMemoryDropFinish(state, true);
                    var journal = RepairJournal;
                    state.Preservation = Task.Run(() => {
                        try
                        {
                            journal.Checkpoint();
                            if (!payload.PackageScoped && state.FailedDefs == 0 && state.RejectedDefs == 0) SaveVerifiedPayload(payload);
                        }
                        catch (Exception error) { Log.Warning("[ATC] Native startup checkpoint: " + error.Message); }
                    });
                    FinishMemoryDropApply(state, true);
                }
                return state;
            }
            finally { if (ReferenceEquals(_activeMemoryDropApply, state)) _activeMemoryDropApply = null; }
        }

        // LoadingProgress may yield between Loaded=true and the final native
        // pass. A gate queued in that gap must recheck completion when it runs.
        internal static bool TryCompleteNativeStartup()
        {
            if (!NativeStartupInjectionIsCurrent()) return false;
            if (nativeStartupDeferred != null)
            {
                if (MemoryDropBusyExceptQueuedKeyed()) return false;
                var payload = nativeStartupDeferred;
                nativeStartupDeferred = null;
                try
                {
                    var state = ApplyNativeStartupPayload(payload, false);
                    if (state == null || !NativeStartupInjectionIsCurrent()) return false;
                    Log.Message("[ATC] Native startup late-Def catch-up only: submitted=" + state.SubmittedDefs +
                        ", succeeded=" + state.InjectedDefs + ", rejected=" + state.RejectedDefs + ", failed=" + state.FailedDefs + ".");
                }
                catch (Exception error)
                {
                    nativeStartupDeferred = payload;
                    Log.Warning("[ATC] Native startup catch-up deferred: " + error.Message);
                    return false;
                }
            }
            TranslationRepairController.AcceptNativeStartupInjection();
            Log.Message("[ATC] Native startup injection is current; post-load full reinjection skipped at execution.");
            return true;
        }

        internal static bool NativeStartupInjectionIsCurrent()
            => NativeStartupCurrent(nativeStartupApplied) && NativeStartupPayloadCurrent(nativeStartupApplied);

        private static bool NativeStartupPayloadCurrent(NativeStartupPreparation preparation)
            => preparation.InputSignature != null && preparation.InputSignature == OwnedTranslationPackSignature(preparation.Target) &&
                SameRepairFiles(preparation.FileHashes, OwnedTranslationFileHashes(GetFolderNameByLanguage(preparation.Target)));
    }
}
