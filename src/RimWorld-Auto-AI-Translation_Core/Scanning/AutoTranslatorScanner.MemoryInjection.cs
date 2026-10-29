using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責把翻譯資料注入記憶體與主執行緒。
// EN: This file injects translation data into memory through the main thread.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器掃描器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorScanner.
    public static partial class AutoTranslatorScanner
    {
        private const int MemoryDropKeyedApplyBudget = 3000;
        private const int MemoryDropDefApplyBudget = 3000;
        private const int MemoryDropApplyMilliseconds = 2;
        private static long memoryRequestRevision;
        private static long memoryPreparationId;
        private static DateTime memoryPreparationDeadline;
        private static DateTime memoryPreparationRetryAfter;
        private static int memoryPreparationRetries;
        private sealed class TranslationSnapshotChangedException : IOException
        {
            internal TranslationSnapshotChangedException(string message) : base(message) { }
        }
        private static bool IsChangingTranslationSnapshot(Exception error)
        {
            if (error is TranslationSnapshotChangedException) return true;
            // Atomic replacements/deletions and writers holding a file lock can
            // interrupt the input fingerprint before comparison is possible.
            // Other failures remain terminal instead of retrying programming or
            // persistent access errors forever.
            if (!(error is IOException)) return false;
            int code = error.HResult & 0xffff;
            return code == 2 || code == 3 || code == 32 || code == 33;
        }
        // Pending scope is consumed only after a coherent payload is prepared.
        // Retry delay never replaces a full request with a later package request.
        private static void ResetMemoryPreparationRetry()
        {
            memoryPreparationRetryAfter = DateTime.MinValue;
            memoryPreparationRetries = 0;
        }
        internal static string LastVerifiedMemorySignature;
        private static LoadedLanguage runtimeBaselineLanguage;
        internal static int GameDataEpoch => Volatile.Read(ref _memoryDropGeneration);
        internal static void InvalidateGameData()
        {
            NativeUiTranslationLookup.Invalidate();
            CachedModTranslationCompatibility.Invalidate();
            ResetNativeStartup();
            completedRepairPayload = null;
            LastVerifiedMemorySignature = null;
            runtimeBaselineLanguage = null;
            Interlocked.Increment(ref _memoryDropGeneration);
            Interlocked.Increment(ref memoryRequestRevision);
            repairMemoryCompletion = null; repairMemoryRequest++;
            lock (_pendingInjectLock)
            {
                ResetMemoryPreparationRetry();
                _pendingMemoryDropPayload = null; _activeMemoryDropApply = null;
                _pendingMemoryDrop = false; _pendingKeyedMemoryDrop = false;
                _pendingMemoryDropPackageIds.Clear(); _pendingMemoryDropClearKeysByPackage.Clear();
            }
            RuntimeKeyedBaselines.Clear(); RuntimeInjectedKeyedKeys.Clear();
            RuntimeDefBaselines.Clear(); RuntimeInjectedDefKeys.Clear();
            Interlocked.Exchange(ref _startupFullMemoryDropQueued, 0);
            ResetMemoryDropStamps();
        }
        private static Action<bool> repairMemoryCompletion;
        private static int repairMemoryRequest;

        internal static void RequestRepairMemoryDrop(Action<bool> completion, string[] packageIds = null)
        {
            if (_activeMemoryDropApply != null || _pendingMemoryDropPayload != null || _memoryDropPrepareRunning != 0)
                throw new InvalidOperationException("Another memory injection is still running.");
            repairMemoryCompletion = completion;
            completedRepairPayload = null;
            LastVerifiedMemorySignature = null;
            repairMemoryRequest++;
            Directory.CreateDirectory(Path.Combine(GetLocalPackPath(), "Languages", GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang)));
            if (packageIds == null)
            {
                ResetMemoryDropStamps(); // A full repair always verifies a fresh payload.
                QueueMemoryDropPreparation(false, true);
            }
            else
            {
                if (packageIds.Length == 0) throw new ArgumentException("Incremental repair requires a package scope.");
                // Queue the whole batch before preparation can capture its scope.
                lock (_pendingInjectLock)
                    foreach (var package in packageIds) _pendingMemoryDropPackageIds.Add(package);
                StartMemoryDropPreparationIfNeeded();
            }
        }

        internal static void CancelRepairMemoryDrop()
        {
            completedRepairPayload = null;
            LastVerifiedMemorySignature = null;
            int cancelled = repairMemoryRequest;
            repairMemoryCompletion = null;
            repairMemoryRequest++;
            Interlocked.Increment(ref _memoryDropGeneration);
            lock (_pendingInjectLock)
            {
                ResetMemoryPreparationRetry();
                if (_pendingMemoryDropPayload?.RepairRequest == cancelled) _pendingMemoryDropPayload = null;
                _pendingMemoryDrop = false;
            }
            if (_activeMemoryDropApply?.Payload.RepairRequest == cancelled)
            {
                _activeMemoryDropApply = null;
                ResetMemoryDropStamps();
            }
        }

        internal static void CancelStartupMemoryDrop()
        {
            // Release the loading screen without permitting late preparation
            // callbacks to publish. Keep baselines for text already applied.
            lock (_pendingInjectLock)
            {
                Interlocked.Increment(ref memoryPreparationId);
                Interlocked.Increment(ref memoryRequestRevision);
                Interlocked.Exchange(ref _memoryDropPrepareRunning, 0);
                _pendingMemoryDropPayload = null; _activeMemoryDropApply = null;
                _pendingMemoryDrop = false; _pendingKeyedMemoryDrop = false;
                _pendingMemoryDropPackageIds.Clear(); _pendingMemoryDropClearKeysByPackage.Clear();
                ResetMemoryPreparationRetry();
            }
        }

        private static void CompleteRepairMemoryDrop(int request, bool success)
        {
            if (request == 0 || request != repairMemoryRequest) return;
            var completion = repairMemoryCompletion;
            repairMemoryCompletion = null;
            completion?.Invoke(success);
        }

        private sealed class RuntimeKeyedBaseline
        {
            public bool HadReplacement;
            public string Key;
            public string Value;
        }

        private sealed class RuntimeDefBaseline
        {
            public Type DefType;
            public string Path;
            public bool HadInjection;
            public string Injection;
            public DefInjectionPackage.DefInjection Snapshot;
        }

        private sealed class RuntimeDefRestoreRemoval
        {
            public DefInjectionPackage Package;
            public string Path;
            public DefInjectionPackage.DefInjection Injection;
        }

        private sealed class RuntimeSubmissionRollback
        {
            public DefInjectionPackage Package;
            public DefInjectionPackage.DefInjection Previous;
            public bool WasRuntimeOwned, HadBaseline;
        }

        private static readonly Dictionary<string, RuntimeKeyedBaseline> RuntimeKeyedBaselines =
            new Dictionary<string, RuntimeKeyedBaseline>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> RuntimeInjectedKeyedKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, RuntimeDefBaseline> RuntimeDefBaselines =
            new Dictionary<string, RuntimeDefBaseline>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> RuntimeInjectedDefKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private class MemoryDropPayload
        {
            [JsonIgnore] public int RepairRequest;
            [JsonIgnore] public int Epoch;
            [JsonIgnore] public LoadedLanguage Language;
            [JsonIgnore] public int CancellationGeneration = AutoTranslatorSettings.PipelineCancellationGeneration;
            public string PreparedStamp;
            public string InputSignature;
            public Dictionary<string, string> FileHashes;
            public Dictionary<string, HashSet<string>> ChangedKeys;
            public Dictionary<string, string> KeyedSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool KeyedOnly;
            public bool PackageScoped;
            public string PackageId;
            public string TargetFolder;
            public string LangRoot;
            public string KeyedPath;
            public Dictionary<string, HashSet<string>> ClearKeysByDefType =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Keyed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<MemoryDropDefTypePayload> DefsByType = new List<MemoryDropDefTypePayload>();
            public DateTime PreparedAtUtc;
            public int OfficialFallbackKeyed;
            public int OfficialFallbackDefs;
        }

        private class MemoryDropDefTypePayload
        {
            public string DefTypeName;
            public Dictionary<string, List<string>> FullLists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public Dictionary<string, string> Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private class MemoryDropApplyState
        {
            public List<MemoryDropDefTypePayload> NativeDeferredDefs;
            public bool CacheVerified;
            public MemoryDropPayload Payload;
            public System.Diagnostics.Stopwatch Timer;
            public List<KeyValuePair<string, string>> KeyedEntries = new List<KeyValuePair<string, string>>();
            public int KeyedIndex;
            public int DefTypeIndex;
            public List<KeyValuePair<string, string>> CurrentDefEntries;
            public int CurrentDefEntryIndex;
            public DefInjectionPackage CurrentPackage;
            public Type CurrentDefType;
            public int InjectedKeyed;
            public int InjectedDefs;
            public int SubmittedDefs;
            public int FailedDefs;
            public readonly List<DefInjectionPackage.DefInjection> Submitted = new List<DefInjectionPackage.DefInjection>();
            public readonly Dictionary<DefInjectionPackage.DefInjection, Type> SubmittedTypes = new Dictionary<DefInjectionPackage.DefInjection, Type>();
            public readonly Dictionary<DefInjectionPackage.DefInjection, RuntimeSubmissionRollback> SubmissionRollbacks = new Dictionary<DefInjectionPackage.DefInjection, RuntimeSubmissionRollback>();
            public int NativeIndex;
            public bool RestoresFinalized, NormalizationDone;
            public IEnumerator<Def> Normalization;
            public readonly Dictionary<string, DefInjectionPackage.DefInjection> CanonicalCandidates = new Dictionary<string, DefInjectionPackage.DefInjection>(StringComparer.Ordinal);
            public int ClearedKeyed;
            public int ClearedDefs;
            public int RejectedDefs;
            public bool NeedsDataInjection;
            public bool PackageClearApplied;
            public IEnumerator<bool> ClearSteps;
            public bool NativeFinalized;
            public Task Preservation;
            public int? NativeErrorsBefore, NativeErrorsAfter;
            public readonly List<RuntimeDefRestoreRemoval> TemporaryDefRestores = new List<RuntimeDefRestoreRemoval>();
        }

        // 這個方法負責重置 ValidationStats 狀態。
        // EN: This method resets validation stats.
        private static void ResetValidationStats()
        {
            lock (_validationStats)
            {
                _validationStats.Reset();
            }

            lock (_loggedEnglishResidualContexts)
            {
                _loggedEnglishResidualContexts.Clear();
            }
        }


        // 這個方法負責處理 AddValidationStat 相關流程。
        // EN: This method handles add validation stat.
        private static void AddValidationStat(Action<TranslationValidationStats> update)
        {
            lock (_validationStats) update(_validationStats);
        }


        // 這個方法負責處理 LogValidationSummary 相關流程。
        // EN: This method handles log validation summary.
        private static void LogValidationSummary()
        {
            TranslationValidationStats snapshot;
            lock (_validationStats)
            {
                snapshot = new TranslationValidationStats
                {
                    NewlineFixed = _validationStats.NewlineFixed,
                    RulePrefixFixed = _validationStats.RulePrefixFixed,
                    TokenFixed = _validationStats.TokenFixed,
                    StructureFallback = _validationStats.StructureFallback,
                    XmlKeySkipped = _validationStats.XmlKeySkipped,
                    EnglishResidualDetected = _validationStats.EnglishResidualDetected,
                    EnglishResidualRetried = _validationStats.EnglishResidualRetried,
                    EnglishResidualFallback = _validationStats.EnglishResidualFallback,
                    ProtectedTokenMismatchDetected = _validationStats.ProtectedTokenMismatchDetected,
                    ProtectedTokenMismatchRetried = _validationStats.ProtectedTokenMismatchRetried,
                    ProtectedTokenMismatchFallback = _validationStats.ProtectedTokenMismatchFallback
                };
            }

            int total = snapshot.NewlineFixed + snapshot.RulePrefixFixed + snapshot.TokenFixed + snapshot.StructureFallback + snapshot.XmlKeySkipped
                + snapshot.EnglishResidualDetected + snapshot.EnglishResidualRetried + snapshot.EnglishResidualFallback
                + snapshot.ProtectedTokenMismatchDetected + snapshot.ProtectedTokenMismatchRetried + snapshot.ProtectedTokenMismatchFallback;
            if (total <= 0)
            {
                AutoTranslatorSettings.AddLog("🩺 " + "ATC_Log_ValidationClean".Translate());
                return;
            }

            AutoTranslatorSettings.AddLog("🩺 " + AutoTranslatorAPI.TranslateText("ATC_Log_ValidationSummary",
                snapshot.NewlineFixed,
                snapshot.RulePrefixFixed,
                snapshot.TokenFixed,
                snapshot.StructureFallback,
                snapshot.XmlKeySkipped));

            if (snapshot.EnglishResidualDetected > 0 || snapshot.EnglishResidualRetried > 0 || snapshot.EnglishResidualFallback > 0)
            {
                AutoTranslatorSettings.AddLog("🩺 " +
                    AutoTranslatorAPI.TranslateText(
                        "ATC_Log_EnglishResidualSummary",
                        snapshot.EnglishResidualDetected,
                        snapshot.EnglishResidualRetried,
                        snapshot.EnglishResidualFallback));
            }
        }


        // 這個方法負責送出 記憶體Drop 請求。
        // EN: This method requests memory drop.
        public static void RequestMemoryDrop()
        {
            QueueMemoryDropPreparation(false, true);
        }

        public static void RequestMemoryDropForPackage(string packageId)
        {
            RequestMemoryDropForPackage(packageId, null);
        }

        public static void RequestMemoryDropForPackage(string packageId, Dictionary<string, HashSet<string>> clearKeysByDefType)
        {
            if (string.IsNullOrWhiteSpace(packageId))
            {
                RequestMemoryDrop();
                return;
            }

            QueueMemoryDropPreparation(false, true, packageId, clearKeysByDefType);
        }


        // 這個方法負責處理 Pump主畫面執行緒派發器 相關流程。
        // EN: This method handles pump main thread dispatcher.
        public static void RequestKeyedMemoryDrop()
        {
            QueueMemoryDropPreparation(true, false);
        }

        public static void QueueStartupFullMemoryDrop(int delayMs = 8000)
        {
            if (Interlocked.CompareExchange(ref _startupFullMemoryDropQueued, 1, 0) != 0)
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Math.Max(1000, delayMs));
                    for (int attempt = 0; attempt < 12; attempt++)
                    {
                        if (AutoTranslatorMod.Settings != null && LanguageDatabase.activeLanguage != null)
                        {
                            break;
                        }

                        await Task.Delay(1000);
                    }

                    if (AutoTranslatorMod.Settings == null || LanguageDatabase.activeLanguage == null)
                    {
                        Interlocked.Exchange(ref _startupFullMemoryDropQueued, 0);
                        return;
                    }

                    ATC_Dispatcher.RunOnMainThread(() => QueueMemoryDropPreparation(false, true));
                }
                catch (Exception ex)
                {
                    Log.Warning("[AutoTranslationCore] Startup full memory drop queue failed: " + ex.Message);
                }
            });
        }

        private static void QueueMemoryDropPreparation(bool keyedOnly, bool logQueued, string packageId = null, Dictionary<string, HashSet<string>> clearKeysByDefType = null)
        {
            string normalizedPackageId = string.IsNullOrWhiteSpace(packageId) ? null : packageId.Trim();
            Interlocked.Increment(ref memoryRequestRevision);
            lock (_pendingInjectLock)
            {
                if (keyedOnly)
                {
                    _pendingKeyedMemoryDrop = true;
                }
                else if (!string.IsNullOrEmpty(normalizedPackageId))
                {
                    if (!_pendingMemoryDrop)
                    {
                        _pendingMemoryDropPackageIds.Add(normalizedPackageId);
                        MergePendingPackageClearKeys(normalizedPackageId, clearKeysByDefType);
                    }
                }
                else
                {
                    _pendingMemoryDrop = true;
                    _pendingKeyedMemoryDrop = false;
                    _pendingMemoryDropPackageIds.Clear();
                    _pendingMemoryDropClearKeysByPackage.Clear();
                }
            }

            if (logQueued && !keyedOnly)
            {
                AutoTranslatorSettings.AddLog("🪂 " + "ATC_Log_MemoryDropQueued".Translate());
            }

            StartMemoryDropPreparationIfNeeded();
        }

        private static void StartMemoryDropPreparationIfNeeded()
        {
            if (AutoTranslatorSettings.IsCancellationRequested) return;
            if (TranslationRepairController.IsRunning && !TranslationRepairController.Applying) return;
            // Loaded can become true before deferred final native injections have
            // finished (e.g. LoadingProgress). The main-thread pump retries later.
            if (StartupTranslationGate.NativeLoadingBlocksWork) return;
            bool keyedOnly;
            long requestRevision;
            List<string> packageIds = null;
            Dictionary<string, Dictionary<string, HashSet<string>>> clearKeysByPackage = null;
            lock (_pendingInjectLock)
            {
                if (DateTime.UtcNow < memoryPreparationRetryAfter) return;
                requestRevision = Interlocked.Read(ref memoryRequestRevision);
                if (_pendingMemoryDropPayload != null)
                {
                    return;
                }

                if (_pendingMemoryDrop)
                {
                    keyedOnly = false;
                }
                else if (_pendingMemoryDropPackageIds.Count > 0)
                {
                    keyedOnly = false;
                    packageIds = _pendingMemoryDropPackageIds.ToList();
                    clearKeysByPackage = CloneClearKeysForPackages(packageIds);
                }
                else if (_pendingKeyedMemoryDrop)
                {
                    keyedOnly = true;
                }
                else
                {
                    return;
                }
            }

            if (Interlocked.CompareExchange(ref _memoryDropPrepareRunning, 1, 0) != 0)
            {
                return;
            }

            int generation = Volatile.Read(ref _memoryDropGeneration);
            int repairRequest = !keyedOnly && repairMemoryCompletion != null ? repairMemoryRequest : 0;
            int cancellationGeneration = AutoTranslatorSettings.PipelineCancellationGeneration;
            long preparationId = Interlocked.Increment(ref memoryPreparationId);
            memoryPreparationDeadline = DateTime.UtcNow.AddMinutes(2);
            Task.Run(() => PrepareMemoryDropPayloadWorkerCore(keyedOnly, packageIds, clearKeysByPackage, generation, repairRequest, requestRevision, cancellationGeneration, preparationId));
        }

        private static void PrepareMemoryDropPayloadWorker(bool keyedOnly, List<string> packageIds, Dictionary<string, Dictionary<string, HashSet<string>>> clearKeysByPackage, int generation, int repairRequest)
        {
            PrepareMemoryDropPayloadWorkerCore(keyedOnly, packageIds, clearKeysByPackage, generation, repairRequest, Interlocked.Read(ref memoryRequestRevision), AutoTranslatorSettings.PipelineCancellationGeneration, Interlocked.Read(ref memoryPreparationId));
        }
        private static void PrepareMemoryDropPayloadWorkerCore(bool keyedOnly, List<string> packageIds, Dictionary<string, Dictionary<string, HashSet<string>>> clearKeysByPackage, int generation, int repairRequest, long requestRevision, int cancellationGeneration, long preparationId)
        {
            LoadedLanguage capturedLanguage = LanguageDatabase.activeLanguage;
            string capturedFolder = GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
            bool CurrentPreparation() => generation == Volatile.Read(ref _memoryDropGeneration) &&
                preparationId == Interlocked.Read(ref memoryPreparationId) &&
                cancellationGeneration == AutoTranslatorSettings.PipelineCancellationGeneration && !AutoTranslatorSettings.IsCancellationRequested &&
                ReferenceEquals(capturedLanguage, LanguageDatabase.activeLanguage) && AutoTranslatorMod.Settings != null &&
                capturedFolder == GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
            try
            {
                preparingChanges = repairRequest != 0 ? repairChanges : null;
                MemoryDropPayload payload = BuildMemoryDropPayload(keyedOnly, packageIds, clearKeysByPackage);
                lock (_pendingInjectLock)
                {
                    if (!CurrentPreparation()) return;
                    ResetMemoryPreparationRetry();

                    if (payload != null)
                    {
                        payload.RepairRequest = repairRequest;
                        payload.Epoch = generation; payload.Language = capturedLanguage;
                        payload.CancellationGeneration = cancellationGeneration;
                        _pendingMemoryDropPayload = payload;
                        if (requestRevision != Interlocked.Read(ref memoryRequestRevision)) return;
                        if (payload.KeyedOnly)
                        {
                            _pendingKeyedMemoryDrop = false;
                        }
                        else
                        {
                            if (payload.PackageScoped && packageIds != null)
                            {
                                foreach (string packageId in packageIds)
                                {
                                    _pendingMemoryDropPackageIds.Remove(packageId);
                                    _pendingMemoryDropClearKeysByPackage.Remove(packageId);
                                }
                            }
                            else
                            {
                                _pendingMemoryDrop = false;
                                _pendingKeyedMemoryDrop = false;
                                _pendingMemoryDropPackageIds.Clear();
                                _pendingMemoryDropClearKeysByPackage.Clear();
                            }
                        }
                    }
                    else
                    {
                        if (requestRevision != Interlocked.Read(ref memoryRequestRevision)) return;
                        if (repairRequest != 0) ATC_Dispatcher.RunOnMainThread(() => CompleteRepairMemoryDrop(repairRequest, false));
                        if (keyedOnly)
                        {
                            _pendingKeyedMemoryDrop = false;
                        }
                        else if (packageIds != null)
                        {
                            foreach (string packageId in packageIds)
                            {
                                _pendingMemoryDropPackageIds.Remove(packageId);
                                _pendingMemoryDropClearKeysByPackage.Remove(packageId);
                            }
                        }
                        else
                        {
                            _pendingMemoryDrop = false;
                            _pendingKeyedMemoryDrop = false;
                            _pendingMemoryDropPackageIds.Clear();
                            _pendingMemoryDropClearKeysByPackage.Clear();
                        }
                    }
                }
            }
            catch (Exception ex) when (IsChangingTranslationSnapshot(ex))
            {
                lock (_pendingInjectLock)
                {
                    if (!CurrentPreparation()) return;
                    // Keep the existing full/keyed/package flags and clear-key
                    // sets, including requests queued while the worker read XML.
                    // A transient snapshot is not a failed repair completion.
                    memoryPreparationRetries = Math.Min(memoryPreparationRetries + 1, 4);
                    int delaySeconds = Math.Min(5, 1 << (memoryPreparationRetries - 1));
                    memoryPreparationRetryAfter = DateTime.UtcNow.AddSeconds(delaySeconds);
                    Log.Message("[ATC] Translation files changed while preparing injection; request retained, retry in " + delaySeconds +
                        "s. Scope=" + (keyedOnly ? "<keyed>" : packageIds == null ? "<full-injection>" : string.Join(",", packageIds)) + ". " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                lock (_pendingInjectLock)
                {
                    if (!CurrentPreparation()) return;
                    ResetMemoryPreparationRetry();
                    if (requestRevision == Interlocked.Read(ref memoryRequestRevision))
                    {
                        if (keyedOnly) _pendingKeyedMemoryDrop = false;
                        else if (packageIds != null)
                            foreach (string packageId in packageIds)
                            {
                                _pendingMemoryDropPackageIds.Remove(packageId);
                                _pendingMemoryDropClearKeysByPackage.Remove(packageId);
                            }
                        else
                        {
                            _pendingMemoryDrop = false;
                            _pendingKeyedMemoryDrop = false;
                            _pendingMemoryDropPackageIds.Clear();
                            _pendingMemoryDropClearKeysByPackage.Clear();
                        }
                    }
                }
                if (repairRequest != 0)
                {
                    ATC_Dispatcher.RunOnMainThread(() => { if (CurrentPreparation()) CompleteRepairMemoryDrop(repairRequest, false); });
                }
                AutoTranslatorSettings.AddErrorLog("❌ " + AutoTranslatorAPI.TranslateText("ATC_LogError_MemoryDropFailed", ex.Message));
                Log.Error("[AutoTranslationCore] Memory Drop preparation failed: " + ex);
            }
            finally
            {
                preparingChanges = null;
                lock (_pendingInjectLock)
                {
                    if (preparationId == Interlocked.Read(ref memoryPreparationId))
                    {
                        Interlocked.Exchange(ref _memoryDropPrepareRunning, 0);
                        ATC_Dispatcher.RunOnMainThread(StartMemoryDropPreparationIfNeeded);
                    }
                }
            }
        }

        private static void ExpireMemoryPreparation()
        {
            if (_memoryDropPrepareRunning == 0 || DateTime.UtcNow <= memoryPreparationDeadline) return;
            int request = repairMemoryRequest;
            lock (_pendingInjectLock)
            {
                Interlocked.Increment(ref memoryPreparationId);
                Interlocked.Increment(ref _memoryDropGeneration);
                Interlocked.Increment(ref memoryRequestRevision);
                Interlocked.Exchange(ref _memoryDropPrepareRunning, 0);
                ResetMemoryPreparationRetry();
                completedRepairPayload = null;
                LastVerifiedMemorySignature = null;
                _pendingMemoryDropPayload = null;
                _pendingMemoryDrop = false; _pendingKeyedMemoryDrop = false;
                _pendingMemoryDropPackageIds.Clear(); _pendingMemoryDropClearKeysByPackage.Clear();
            }
            CompleteRepairMemoryDrop(request, false);
            Log.Warning("[ATC] Translation preparation timed out; current text retained and late results revoked.");
        }

        internal static void PumpMainThreadDispatcher()
        {
            ExpireMemoryPreparation();
            if (StartupTranslationGate.NativeLoadingBlocksWork) return;
            TranslationValidationQueue.Pump();
            if (_activeMemoryDropApply != null)
            {
                ContinueMemoryDropApply();
            }

            MemoryDropPayload payload = null;
            lock (_pendingInjectLock)
            {
                if (_activeMemoryDropApply == null && _pendingMemoryDropPayload != null)
                {
                    payload = _pendingMemoryDropPayload;
                    _pendingMemoryDropPayload = null;
                }
            }

            if (payload != null)
            {
                BeginMemoryDropApply(payload);
            }

            if (_activeMemoryDropApply != null)
            {
                ContinueMemoryDropApply();
            }

            StartMemoryDropPreparationIfNeeded();

            TranslationRepairController.PumpActiveRepair();
            PumpStaticCachedTranslationRefresh();
            if (_activeMemoryDropApply == null) NativeUiTranslationLookup.Pump();
        }

        private static string VerifiedPayloadPath(string folder) => Path.Combine(GetLocalPackPath(), "SelfRepair", "verified-" + CurrentRimWorldVersion + "-" + folder + ".json");
        private sealed class VerifiedPayloadEnvelope
        {
            public int Schema = 1;
            public string Json, Hash;
        }
        private static MemoryDropPayload ReadVerifiedPayload(string folder)
        {
            string path = VerifiedPayloadPath(folder);
            if (!File.Exists(path)) return null;
            try
            {
                var envelope = JsonConvert.DeserializeObject<VerifiedPayloadEnvelope>(File.ReadAllText(path));
                if (envelope?.Schema != 1 || envelope.Json == null || TranslationRepairJournal.Hash(envelope.Json) != envelope.Hash) return null;
                return JsonConvert.DeserializeObject<MemoryDropPayload>(envelope.Json);
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException) { return null; }
        }
        private static bool SaveVerifiedPayload(MemoryDropPayload payload)
        {
            if (payload.PackageScoped)
            {
                var previous = ReadVerifiedPayload(payload.TargetFolder);
                if (previous?.FileHashes == null || payload.ChangedKeys == null) return false;
                var changed = new HashSet<string>(payload.ChangedKeys.Keys, StringComparer.OrdinalIgnoreCase);
                if (previous.FileHashes.Any(p => !changed.Contains(p.Key) && (!payload.FileHashes.TryGetValue(p.Key, out string hash) || hash != p.Value)) ||
                    payload.FileHashes.Any(p => !changed.Contains(p.Key) && !previous.FileHashes.ContainsKey(p.Key))) return false;
                foreach (var file in payload.ChangedKeys)
                {
                    foreach (string key in file.Value)
                    {
                        if (previous.KeyedSources.TryGetValue(key, out string source) && string.Equals(source, file.Key, StringComparison.OrdinalIgnoreCase))
                        { previous.Keyed.Remove(key); previous.KeyedSources.Remove(key); }
                        foreach (var type in previous.DefsByType)
                            if (type.Sources.TryGetValue(key, out source) && string.Equals(source, file.Key, StringComparison.OrdinalIgnoreCase))
                            { type.Data.Remove(key); type.Sources.Remove(key); type.FullLists.Remove(key); }
                    }
                }
                foreach (var pair in payload.Keyed) { previous.Keyed[pair.Key] = pair.Value; previous.KeyedSources[pair.Key] = payload.KeyedSources[pair.Key]; }
                foreach (var type in payload.DefsByType)
                {
                    var merged = GetOrCreateMemoryDropDefPayload(previous, type.DefTypeName);
                    foreach (var pair in type.Data) { merged.Data[pair.Key] = pair.Value; merged.Sources[pair.Key] = type.Sources[pair.Key]; }
                    foreach (var pair in type.FullLists) merged.FullLists[pair.Key] = pair.Value;
                }
                previous.InputSignature = payload.InputSignature; previous.FileHashes = payload.FileHashes;
                previous.Epoch = payload.Epoch; previous.Language = payload.Language;
                previous.CancellationGeneration = payload.CancellationGeneration;
                previous.PreparedStamp = payload.PreparedStamp; payload = previous;
            }
            string path = VerifiedPayloadPath(payload.TargetFolder);
            string json = JsonConvert.SerializeObject(payload, Newtonsoft.Json.Formatting.None);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new VerifiedPayloadEnvelope { Json = json, Hash = TranslationRepairJournal.Hash(json) }));
            TranslationXmlAtomicFileStore.SaveIfCurrent(path, stream => stream.Write(bytes, 0, bytes.Length), () =>
                payload.Epoch == GameDataEpoch && ReferenceEquals(payload.Language, LanguageDatabase.activeLanguage) &&
                payload.CancellationGeneration == AutoTranslatorSettings.PipelineCancellationGeneration &&
                payload.InputSignature == OwnedTranslationPackSignature(AutoTranslatorMod.Settings.TargetLang));
            return true;
        }

        private static MemoryDropPayload BuildMemoryDropPayload(bool keyedOnly)
        {
            return BuildMemoryDropPayload(keyedOnly, null);
        }

        private static MemoryDropPayload BuildMemoryDropPayload(bool keyedOnly, List<string> packageIds)
        {
            return BuildMemoryDropPayload(keyedOnly, packageIds, null);
        }

        private static MemoryDropPayload BuildMemoryDropPayload(bool keyedOnly, List<string> packageIds, Dictionary<string, Dictionary<string, HashSet<string>>> clearKeysByPackage)
        {
            if (AutoTranslatorMod.Settings == null) return null;

            string packPath = GetLocalPackPath();
            string targetFolder = GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
            string langRoot = Path.Combine(packPath, "Languages", targetFolder);
            if (!Directory.Exists(langRoot)) return null;
            // Preparing a runtime payload must not rewrite existing translation
            // files. Native injection guards still validate candidates on apply.

            string keyedPath = Path.Combine(langRoot, "Keyed");
            string inputSignature = OwnedTranslationPackSignature(AutoTranslatorMod.Settings.TargetLang);
            bool packageScoped = !keyedOnly && packageIds != null && packageIds.Count > 0;
            HashSet<string> cleanPackageIds = packageScoped
                ? new HashSet<string>(packageIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(CleanPackageIdForFileMatch), StringComparer.OrdinalIgnoreCase)
                : null;

            if (packageScoped && cleanPackageIds.Count == 0)
            {
                return null;
            }

            if (!packageScoped && keyedOnly && IsKeyedMemoryDropStampCurrent(keyedPath, targetFolder))
            {
                return null;
            }

            if (!packageScoped && !keyedOnly && IsFullMemoryDropStampCurrent(langRoot, targetFolder))
            {
                Log.Message("[AutoTranslationCore] Memory Drop skipped: translation files unchanged since last full injection.");
                return null;
            }

            if (!keyedOnly && !packageScoped)
            {
                MemoryDropPayload cached = ReadVerifiedPayload(targetFolder);
                if (cached != null && cached.InputSignature == inputSignature && cached.TargetFolder == targetFolder &&
                    !cached.PackageScoped && !cached.KeyedOnly && cached.Keyed != null && cached.DefsByType != null &&
                    SameRepairFiles(cached.FileHashes, OwnedTranslationFileHashes(targetFolder)))
                {
                    cached.RepairRequest = 0; cached.Epoch = GameDataEpoch; cached.Language = null;
                    return cached;
                }
            }
            var payload = new MemoryDropPayload
            {
                InputSignature = inputSignature,
                FileHashes = OwnedTranslationFileHashes(targetFolder),
                ChangedKeys = preparingChanges?.Files,
                PreparedStamp = keyedOnly ? BuildKeyedMemoryDropStamp(keyedPath, targetFolder) : BuildFullMemoryDropStamp(langRoot, targetFolder),
                KeyedOnly = keyedOnly,
                PackageScoped = packageScoped,
                PackageId = packageScoped ? string.Join(",", packageIds) : null,
                TargetFolder = targetFolder,
                LangRoot = langRoot,
                KeyedPath = keyedPath,
                ClearKeysByDefType = BuildMemoryDropClearKeys(packageIds, clearKeysByPackage),
                Keyed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                DefsByType = new List<MemoryDropDefTypePayload>(),
                PreparedAtUtc = DateTime.UtcNow
            };

            preparingXml = ReadInjectionXmlCache(targetFolder);
            preparingXmlHashes = payload.FileHashes;
            int xmlEpoch = GameDataEpoch;
            try
            {
            if (Directory.Exists(keyedPath)) payload.Keyed = LoadXmlFilesToDictForMemoryDrop(keyedPath, cleanPackageIds, payload.KeyedSources);
            string defPath = Path.Combine(langRoot, "DefInjected");
            if (!keyedOnly && Directory.Exists(defPath))
            {
                foreach (var typeDir in Directory.GetDirectories(defPath))
                {
                    string defTypeName = Path.GetFileName(typeDir);
                    var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var defDict = LoadXmlFilesToDictForMemoryDrop(typeDir, cleanPackageIds, sources, defTypeName);
                    if (defDict.Count == 0) continue;

                    var bucket = new MemoryDropDefTypePayload { DefTypeName = defTypeName, Data = defDict, Sources = sources };
                    foreach (string file in sources.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                        foreach (var list in ReadInjectionXmlFile(file).Lists)
                        {
                            if (preparingChanges != null && !preparingChanges.Files[file].Any(k => k == list.Key || k.StartsWith(list.Key + ".", StringComparison.Ordinal))) continue;
                            foreach (string key in defDict.Keys.Where(k => k.StartsWith(list.Key + ".", StringComparison.Ordinal) && sources[k] == file).ToArray())
                            { defDict.Remove(key); sources.Remove(key); }
                            bucket.FullLists[list.Key] = list.Value; defDict[list.Key] = JsonConvert.SerializeObject(list.Value); sources[list.Key] = file;
                        }
                    payload.DefsByType.Add(bucket);
                }
            }

            AddOfficialPackageFallbacksForClearedKeys(payload, packageIds, clearKeysByPackage);
            if (inputSignature != OwnedTranslationPackSignature(AutoTranslatorMod.Settings.TargetLang))
                throw new TranslationSnapshotChangedException("Translation pack changed during payload preparation.");
            SaveInjectionXmlCache(targetFolder, xmlEpoch);
            return payload;
            }
            finally { preparingXml = null; preparingXmlHashes = null; }
        }

        private static void AddOfficialPackageFallbacksForClearedKeys(
            MemoryDropPayload payload,
            List<string> packageIds,
            Dictionary<string, Dictionary<string, HashSet<string>>> clearKeysByPackage)
        {
            if (payload == null || !payload.PackageScoped || packageIds == null || clearKeysByPackage == null) return;
            if (AutoTranslatorMod.Settings == null) return;

            foreach (string packageId in packageIds)
            {
                if (string.IsNullOrWhiteSpace(packageId)) continue;
                if (!IsOfficialBaseGameOrDlcPackage(packageId)) continue;
                if (!clearKeysByPackage.TryGetValue(packageId, out Dictionary<string, HashSet<string>> packageClearKeys) ||
                    packageClearKeys == null ||
                    packageClearKeys.Count == 0)
                {
                    continue;
                }

                string rootDir = GetInstalledModRootDir(packageId);
                if (string.IsNullOrWhiteSpace(rootDir)) continue;

                if (packageClearKeys.TryGetValue("Keyed", out HashSet<string> keyedKeys) &&
                    keyedKeys != null &&
                    keyedKeys.Count > 0)
                {
                    Dictionary<string, string> officialKeyed = LoadOfficialTarTranslationsByCategory(
                        packageId,
                        rootDir,
                        AutoTranslatorMod.Settings.TargetLang,
                        "Keyed");
                    foreach (string key in keyedKeys)
                    {
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        if (payload.Keyed.ContainsKey(key)) continue;
                        if (!officialKeyed.TryGetValue(key, out string value) || string.IsNullOrWhiteSpace(value)) continue;

                        payload.Keyed[key] = value;
                        payload.KeyedSources[key] = "ATC:official:" + packageId;
                        payload.OfficialFallbackKeyed++;
                    }
                }

                Dictionary<string, Dictionary<string, string>> officialDefs = null;
                foreach (KeyValuePair<string, HashSet<string>> pair in packageClearKeys)
                {
                    if (pair.Value == null || pair.Value.Count == 0) continue;
                    if (pair.Key.Equals("Keyed", StringComparison.OrdinalIgnoreCase)) continue;

                    if (officialDefs == null)
                    {
                        officialDefs = LoadOfficialTarDefTranslations(packageId, rootDir, AutoTranslatorMod.Settings.TargetLang);
                    }

                    if (!officialDefs.TryGetValue(pair.Key, out Dictionary<string, string> officialTypeTranslations)) continue;
                    MemoryDropDefTypePayload typePayload = GetOrCreateMemoryDropDefPayload(payload, pair.Key);

                    foreach (string key in pair.Value)
                    {
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        if (typePayload.Data.ContainsKey(key)) continue;
                        if (!officialTypeTranslations.TryGetValue(key, out string value) || string.IsNullOrWhiteSpace(value)) continue;

                        typePayload.Data[key] = value;
                        typePayload.Sources[key] = "ATC:official:" + packageId + ":" + pair.Key;
                        payload.OfficialFallbackDefs++;
                    }
                }
            }
        }

        private static MemoryDropDefTypePayload GetOrCreateMemoryDropDefPayload(MemoryDropPayload payload, string defTypeName)
        {
            MemoryDropDefTypePayload existing = payload.DefsByType.FirstOrDefault(p =>
                p != null && string.Equals(p.DefTypeName, defTypeName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            MemoryDropDefTypePayload created = new MemoryDropDefTypePayload
            {
                DefTypeName = defTypeName ?? "",
                Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
            payload.DefsByType.Add(created);
            return created;
        }

        private static string GetInstalledModRootDir(string packageId)
        {
            try
            {
                ModMetaData mod = ModLister.AllInstalledMods.FirstOrDefault(m =>
                    m != null &&
                    string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase) &&
                    m.RootDir != null &&
                    Directory.Exists(m.RootDir.FullName));
                return mod != null ? mod.RootDir.FullName : "";
            }
            catch
            {
                return "";
            }
        }

        private static void MergePendingPackageClearKeys(string packageId, Dictionary<string, HashSet<string>> clearKeysByDefType)
        {
            if (string.IsNullOrWhiteSpace(packageId) || clearKeysByDefType == null || clearKeysByDefType.Count == 0) return;

            if (!_pendingMemoryDropClearKeysByPackage.TryGetValue(packageId, out Dictionary<string, HashSet<string>> target))
            {
                target = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                _pendingMemoryDropClearKeysByPackage[packageId] = target;
            }

            foreach (var pair in clearKeysByDefType)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null || pair.Value.Count == 0) continue;

                if (!target.TryGetValue(pair.Key, out HashSet<string> keys))
                {
                    keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    target[pair.Key] = keys;
                }

                foreach (string key in pair.Value)
                {
                    if (!string.IsNullOrWhiteSpace(key)) keys.Add(key);
                }
            }
        }

        private static Dictionary<string, Dictionary<string, HashSet<string>>> CloneClearKeysForPackages(List<string> packageIds)
        {
            var clone = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
            if (packageIds == null || packageIds.Count == 0) return clone;

            foreach (string packageId in packageIds)
            {
                if (string.IsNullOrWhiteSpace(packageId)) continue;
                if (!_pendingMemoryDropClearKeysByPackage.TryGetValue(packageId, out Dictionary<string, HashSet<string>> source)) continue;

                var packageClone = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in source)
                {
                    packageClone[pair.Key] = pair.Value != null
                        ? new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                clone[packageId] = packageClone;
            }

            return clone;
        }

        private static Dictionary<string, HashSet<string>> BuildMemoryDropClearKeys(List<string> packageIds, Dictionary<string, Dictionary<string, HashSet<string>>> clearKeysByPackage)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (packageIds == null || clearKeysByPackage == null || clearKeysByPackage.Count == 0) return result;

            foreach (string packageId in packageIds)
            {
                if (string.IsNullOrWhiteSpace(packageId)) continue;
                if (!clearKeysByPackage.TryGetValue(packageId, out Dictionary<string, HashSet<string>> packageKeys)) continue;

                foreach (var pair in packageKeys)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null || pair.Value.Count == 0) continue;

                    if (!result.TryGetValue(pair.Key, out HashSet<string> keys))
                    {
                        keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        result[pair.Key] = keys;
                    }

                    foreach (string key in pair.Value)
                    {
                        if (!string.IsNullOrWhiteSpace(key)) keys.Add(key);
                    }
                }
            }

            return result;
        }

        private static Dictionary<string, string> LoadXmlFilesToDictForMemoryDrop(string path, HashSet<string> cleanPackageIds,
            Dictionary<string, string> sources = null, string defType = "Keyed")
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(path)) return dict;

            foreach (var file in GetXmlFilesCached(path, SearchOption.AllDirectories))
            {
                HashSet<string> changedKeys = null;
                if (preparingChanges != null && !preparingChanges.Files.TryGetValue(Path.GetFullPath(file), out changedKeys)) continue;
                string fileName = Path.GetFileNameWithoutExtension(file);
                if (preparingChanges == null && cleanPackageIds != null && cleanPackageIds.Count > 0 && !cleanPackageIds.Any(cleanId => fileName.Equals(cleanId + "_AutoTranslated", StringComparison.OrdinalIgnoreCase) || fileName.Equals(cleanId + "_CloudCorrections", StringComparison.OrdinalIgnoreCase) || fileName.Equals(cleanId + "_ManualTranslation", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var fileDict = preparingXml != null ? ReadInjectionXmlFile(file).Data : LoadXmlFileToDict(file);
                foreach (var pair in fileDict)
                {
                    if (changedKeys != null && !changedKeys.Contains(pair.Key)) continue;
                    if (dict.TryGetValue(pair.Key, out string previous))
                    {
                        string previousSource = sources != null && sources.TryGetValue(pair.Key, out var s) ? s : path;
                        var conflict = new DefTranslationPathResult { Reason = "duplicate_key", Path = pair.Key, Evidence = "existing file load order; last candidate selected" };
                        RecordDefRepair(defType, pair.Key, previous, previousSource, conflict, "payload", previous == pair.Value ? "相同譯文合併" : "衝突");
                        RecordDefRepair(defType, pair.Key, pair.Value, file, conflict, "payload-selected", "保留候選");
                    }
                    dict[pair.Key] = pair.Value;
                    if (sources != null) sources[pair.Key] = file;
                }
            }

            return dict;
        }

        private static string CleanPackageIdForFileMatch(string packageId)
        {
            return (packageId ?? "").Replace(".", "_").ToLowerInvariant();
        }

        private static void BeginMemoryDropApply(MemoryDropPayload payload)
        {
            if (payload == null) return;
            if (LanguageDatabase.activeLanguage == null || AutoTranslatorMod.Settings == null) return;

            if (payload.Language == null) { payload.Language = LanguageDatabase.activeLanguage; payload.Epoch = GameDataEpoch; }
            if (payload.Epoch != GameDataEpoch || !ReferenceEquals(payload.Language, LanguageDatabase.activeLanguage) ||
                payload.CancellationGeneration != AutoTranslatorSettings.PipelineCancellationGeneration ||
                payload.TargetFolder != GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang)) return;
            if (!ReferenceEquals(runtimeBaselineLanguage, payload.Language))
            {
                RuntimeKeyedBaselines.Clear(); RuntimeInjectedKeyedKeys.Clear();
                RuntimeDefBaselines.Clear(); RuntimeInjectedDefKeys.Clear();
                runtimeBaselineLanguage = payload.Language;
            }
            _activeMemoryDropApply = new MemoryDropApplyState
            {
                Payload = payload,
                NativeErrorsBefore = TranslationRepairController.CountNativeErrors(LanguageDatabase.activeLanguage),
                Timer = System.Diagnostics.Stopwatch.StartNew(),
                KeyedEntries = payload.Keyed != null
                    ? payload.Keyed.ToList()
                    : new List<KeyValuePair<string, string>>()
            };
        }

        private static void ContinueMemoryDropApply()
        {
            MemoryDropApplyState state = _activeMemoryDropApply;
            if (state == null) return;

            try
            {
                LoadedLanguage activeLang = LanguageDatabase.activeLanguage;
                if (activeLang == null || AutoTranslatorMod.Settings == null || state.Payload.Epoch != GameDataEpoch ||
                    state.Payload.CancellationGeneration != AutoTranslatorSettings.PipelineCancellationGeneration ||
                    !ReferenceEquals(activeLang, state.Payload.Language) ||
                    state.Payload.TargetFolder != GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang))
                {
                    FinishMemoryDropApply(state, false);
                    return;
                }

                if (state.Preservation != null)
                {
                    if (!state.Preservation.IsCompleted) return;
                    state.Preservation.GetAwaiter().GetResult();
                    FinishMemoryDropApply(state, true);
                    return;
                }

                if (state.ClearSteps == null && !state.PackageClearApplied) state.ClearSteps = ApplyPackageClearSteps(state, activeLang).GetEnumerator();
                while (!state.PackageClearApplied && TranslationFrameBudget.HasTime)
                    if (!state.ClearSteps.MoveNext()) { state.ClearSteps.Dispose(); state.ClearSteps = null; state.PackageClearApplied = true; }
                if (!state.PackageClearApplied) return;
                if (ApplyKeyedBatch(state, activeLang)) return;
                if (!state.Payload.KeyedOnly && ApplyDefBatch(state, activeLang)) return;
                if (!state.Payload.KeyedOnly && ApplyNativeMemoryBatch(state)) return;
                FinalizeMemoryRestores(state, true);
                if (ApplyMemoryNormalizationBatch(state)) return;

                PrepareMemoryDropFinish(state, true);
                state.Preservation = Task.Run(() => {
                    FlushSelfRepair();
                    if (state.FailedDefs == 0 && state.RejectedDefs == 0 && !state.Payload.KeyedOnly && !string.IsNullOrEmpty(state.Payload.InputSignature))
                        state.CacheVerified = SaveVerifiedPayload(state.Payload);
                });
            }
            catch (Exception ex)
            {
                AutoTranslatorSettings.AddErrorLog("❌ " + AutoTranslatorAPI.TranslateText("ATC_LogError_MemoryDropFailed", ex.Message));
                Log.Error($"[AutoTranslationCore] Memory Drop Failed: {ex.Message}");
                FinishMemoryDropApply(state, false);
            }
        }

        private static IEnumerable<bool> ApplyPackageClearSteps(MemoryDropApplyState state, LoadedLanguage activeLang)
        {
            if (state == null || state.PackageClearApplied) yield break;

            if (activeLang == null || state.Payload == null || !state.Payload.PackageScoped ||
                state.Payload.ClearKeysByDefType == null || state.Payload.ClearKeysByDefType.Count == 0)
            {
                yield break;
            }

            if (activeLang.keyedReplacements == null)
            {
                activeLang.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
            }

            if (state.Payload.ClearKeysByDefType.TryGetValue("Keyed", out HashSet<string> keyedKeys) &&
                keyedKeys != null && keyedKeys.Count > 0)
            {
                foreach (string key in keyedKeys)
                {
                    yield return true;
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (!RuntimeInjectedKeyedKeys.Remove(key)) continue;

                    if (RuntimeKeyedBaselines.TryGetValue(key, out RuntimeKeyedBaseline baseline) && baseline.HadReplacement)
                    {
                        activeLang.keyedReplacements[key] = new LoadedLanguage.KeyedReplacement
                        {
                            key = baseline.Key ?? key,
                            value = baseline.Value ?? ""
                        };
                    }
                    else
                    {
                        activeLang.keyedReplacements.Remove(key);
                    }

                    RuntimeKeyedBaselines.Remove(key);
                    state.ClearedKeyed++;
                }
            }

            foreach (var pair in state.Payload.ClearKeysByDefType)
            {
                if (pair.Value == null || pair.Value.Count == 0) continue;
                if (pair.Key.Equals("Keyed", StringComparison.OrdinalIgnoreCase)) continue;

                Type defType = TranslationRuntimeResolver.DefType(pair.Key);
                if (defType == null) continue;

                DefInjectionPackage package = GetOrCreateRuntimeDefPackage(activeLang, defType);
                if (package == null) continue;

                foreach (string key in pair.Value)
                {
                    yield return true;
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    string runtimeKey = GetRuntimeDefKey(defType, key);
                    if (!RuntimeInjectedDefKeys.Remove(runtimeKey)) continue;

                    package.injections.TryGetValue(key, out DefInjectionPackage.DefInjection currentInjection);
                    if (RuntimeDefBaselines.TryGetValue(runtimeKey, out RuntimeDefBaseline baseline) && baseline.HadInjection)
                    {
                        package.injections[key] = RestoreRuntimeDefInjection(baseline.Snapshot, key, baseline.Injection);
                    }
                    else if (currentInjection != null && currentInjection.replacedString != null)
                    {
                        DefInjectionPackage.DefInjection restoreInjection = CreateRuntimeDefInjection(key, currentInjection.replacedString);
                        package.injections[key] = restoreInjection;
                        state.TemporaryDefRestores.Add(new RuntimeDefRestoreRemoval
                        {
                            Package = package,
                            Path = key,
                            Injection = restoreInjection
                        });
                    }
                    else
                    {
                        package.injections.Remove(key);
                    }

                    if (package.injections.TryGetValue(key, out var restore))
                    {
                        var isolated = new DefInjectionPackage(defType);
                        isolated.injections[key] = restore;
                        TranslationConfigGuard.ApplyValidated(isolated);
                    }
                    RuntimeDefBaselines.Remove(runtimeKey);
                    state.ClearedDefs++;
                    state.NeedsDataInjection = true;
                }
            }
        }

        private static void RememberRuntimeKeyedBaseline(LoadedLanguage activeLang, string key)
        {
            if (activeLang == null || string.IsNullOrWhiteSpace(key) || RuntimeInjectedKeyedKeys.Contains(key)) return;
            if (RuntimeKeyedBaselines.ContainsKey(key)) return;

            LoadedLanguage.KeyedReplacement replacement = null;
            bool hadReplacement = activeLang.keyedReplacements != null &&
                activeLang.keyedReplacements.TryGetValue(key, out replacement);
            RuntimeKeyedBaselines[key] = new RuntimeKeyedBaseline
            {
                HadReplacement = hadReplacement,
                Key = hadReplacement ? replacement.key : key,
                Value = hadReplacement ? replacement.value : null
            };
        }

        private static void RememberRuntimeDefBaseline(Type defType, DefInjectionPackage package, string path)
        {
            if (defType == null || package == null || string.IsNullOrWhiteSpace(path)) return;

            string runtimeKey = GetRuntimeDefKey(defType, path);
            if (RuntimeInjectedDefKeys.Contains(runtimeKey) || RuntimeDefBaselines.ContainsKey(runtimeKey)) return;

            DefInjectionPackage.DefInjection existing = null;
            bool hadInjection = package.injections != null &&
                package.injections.TryGetValue(path, out existing);
            RuntimeDefBaselines[runtimeKey] = new RuntimeDefBaseline
            {
                DefType = defType,
                Path = path,
                HadInjection = hadInjection,
                Injection = hadInjection ? existing.injection : null,
                Snapshot = hadInjection ? CloneRuntimeDefInjection(existing) : null
            };
        }

        private static string GetRuntimeDefKey(Type defType, string path)
        {
            return (defType != null ? defType.FullName : "") + "\n" + (path ?? "");
        }

        private static DefInjectionPackage.DefInjection CloneRuntimeDefInjection(DefInjectionPackage.DefInjection source)
        {
            if (source == null) return null;
            var copy = new DefInjectionPackage.DefInjection();
            foreach (var field in typeof(DefInjectionPackage.DefInjection).GetFields()) field.SetValue(copy, field.GetValue(source));
            if (source.fullListInjection != null) copy.fullListInjection = new List<string>(source.fullListInjection);
            return copy;
        }

        private static DefInjectionPackage.DefInjection RestoreRuntimeDefInjection(DefInjectionPackage.DefInjection source, string path, string value)
        {
            var copy = source == null ? CreateRuntimeDefInjection(path, value) : CloneRuntimeDefInjection(source);
            copy.injected = false;
            return copy;
        }

        private static DefInjectionPackage.DefInjection CreateRuntimeDefInjection(string path, string value, string fileSource = "ATC:baseline-restore")
        {
            return new DefInjectionPackage.DefInjection
            {
                path = path,
                injection = value ?? "",
                fileSource = fileSource,
                injected = false
            };
        }

        private static DefInjectionPackage GetOrCreateRuntimeDefPackage(LoadedLanguage activeLang, Type defType)
        {
            if (activeLang == null || defType == null) return null;
            if (activeLang.defInjections == null)
            {
                activeLang.defInjections = new List<DefInjectionPackage>();
            }

            DefInjectionPackage package = activeLang.defInjections.FirstOrDefault(p => p.defType == defType);
            if (package == null)
            {
                package = new DefInjectionPackage(defType);
                activeLang.defInjections.Add(package);
            }

            if (package.injections == null)
            {
                package.injections = new Dictionary<string, DefInjectionPackage.DefInjection>();
            }
            return package;
        }

        private static void RemoveTemporaryDefRestores(IEnumerable<RuntimeDefRestoreRemoval> removals)
        {
            if (removals == null) return;

            foreach (RuntimeDefRestoreRemoval removal in removals)
            {
                if (removal?.Package?.injections == null || string.IsNullOrWhiteSpace(removal.Path)) continue;
                if (removal.Package.injections.TryGetValue(removal.Path, out DefInjectionPackage.DefInjection current) &&
                    ReferenceEquals(current, removal.Injection))
                {
                    removal.Package.injections.Remove(removal.Path);
                }
            }
        }

        public static void RestoreRuntimeTranslationsAfterPackReset()
        {
            if (!UnityData.IsInMainThread)
            {
                ATC_Dispatcher.RunOnMainThread(RestoreRuntimeTranslationsAfterPackReset);
                return;
            }

            Interlocked.Increment(ref _memoryDropGeneration);
            lock (_pendingInjectLock)
            {
                _pendingMemoryDrop = false;
                _pendingKeyedMemoryDrop = false;
                _pendingMemoryDropPackageIds.Clear();
                _pendingMemoryDropClearKeysByPackage.Clear();
                _pendingMemoryDropPayload = null;
            }
            _activeMemoryDropApply = null;
            Interlocked.Exchange(ref _startupFullMemoryDropQueued, 0);

            LoadedLanguage activeLang = LanguageDatabase.activeLanguage;
            if (activeLang == null)
            {
                RuntimeKeyedBaselines.Clear();
                RuntimeInjectedKeyedKeys.Clear();
                RuntimeDefBaselines.Clear();
                RuntimeInjectedDefKeys.Clear();
                ResetMemoryDropStamps();
                return;
            }

            if (activeLang.keyedReplacements == null)
            {
                activeLang.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
            }

            var keys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) {
                ["Keyed"] = new HashSet<string>(RuntimeInjectedKeyedKeys, StringComparer.OrdinalIgnoreCase)
            };
            foreach (var baseline in RuntimeDefBaselines.Values)
            {
                if (baseline?.DefType == null || string.IsNullOrWhiteSpace(baseline.Path)) continue;
                string type = baseline.DefType.FullName;
                if (!keys.TryGetValue(type, out var paths)) keys[type] = paths = new HashSet<string>(StringComparer.Ordinal);
                paths.Add(baseline.Path);
            }
            ResetMemoryDropStamps();
            BeginMemoryDropApply(new MemoryDropPayload {
                PackageScoped = true, PackageId = "ATC:pack-reset", ClearKeysByDefType = keys,
                Epoch = GameDataEpoch, Language = activeLang,
                TargetFolder = GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang)
            });
        }

        private static void ResetMemoryDropStamps()
        {
            lock (_memoryDropStampLock)
            {
                _lastKeyedMemoryDropStamp = null;
                _lastFullMemoryDropStamp = null;
            }
        }

        private static bool ApplyKeyedBatch(MemoryDropApplyState state, LoadedLanguage activeLang)
        {
            if (state == null || activeLang == null || state.KeyedEntries == null) return false;
            if (state.KeyedIndex >= state.KeyedEntries.Count) return false;
            if (activeLang.keyedReplacements == null)
            {
                activeLang.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
            }

            int remaining = MemoryDropKeyedApplyBudget;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (state.KeyedIndex < state.KeyedEntries.Count && remaining-- > 0 && TranslationFrameBudget.HasTime)
            {
                var kvp = state.KeyedEntries[state.KeyedIndex++];

                RememberRuntimeKeyedBaseline(activeLang, kvp.Key);
                if (activeLang.keyedReplacements.TryGetValue(kvp.Key, out LoadedLanguage.KeyedReplacement existingReplacement))
                {
                    RememberStaticTranslationSourceHint(kvp.Key, existingReplacement.value);
                }

                activeLang.keyedReplacements[kvp.Key] = new LoadedLanguage.KeyedReplacement
                {
                    key = kvp.Key,
                    value = kvp.Value
                };
                RememberStaticTranslationTargetHint(kvp.Key, kvp.Value);
                RuntimeInjectedKeyedKeys.Add(kvp.Key);
                state.InjectedKeyed++;
            }

            return state.KeyedIndex < state.KeyedEntries.Count;
        }

        private static bool ApplyDefBatch(MemoryDropApplyState state, LoadedLanguage activeLang)
        {
            if (state == null || activeLang == null || state.Payload == null || state.Payload.DefsByType == null) return false;

            int remaining = MemoryDropDefApplyBudget;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (state.DefTypeIndex < state.Payload.DefsByType.Count && remaining > 0 && TranslationFrameBudget.HasTime)
            {
                if (state.CurrentDefEntries == null)
                {
                    MemoryDropDefTypePayload typePayload = state.Payload.DefsByType[state.DefTypeIndex];
                    Type defType = TranslationRuntimeResolver.DefType(typePayload.DefTypeName);
                    if (defType == null)
                    {
                        foreach (string key in typePayload.Data.Keys) RememberNativeDeferredCandidate(state, typePayload, key);
                        state.RejectedDefs += typePayload.Data.Count;
                        state.DefTypeIndex++;
                        continue;
                    }

                    DefInjectionPackage package = GetOrCreateRuntimeDefPackage(activeLang, defType);
                    if (package == null)
                    {
                        state.DefTypeIndex++;
                        continue;
                    }

                    state.CurrentPackage = package;
                    state.CurrentDefType = defType;
                    state.CurrentDefEntries = typePayload.Data != null
                        ? typePayload.Data.ToList()
                        : new List<KeyValuePair<string, string>>();
                    state.CurrentDefEntryIndex = 0;
                }

                while (state.CurrentDefEntryIndex < state.CurrentDefEntries.Count && remaining-- > 0 && TranslationFrameBudget.HasTime)
                {
                    var kvp = state.CurrentDefEntries[state.CurrentDefEntryIndex++];
                    var typePayload = state.Payload.DefsByType[state.DefTypeIndex];
                    string source = typePayload.Sources.TryGetValue(kvp.Key, out var sourceFile) ? sourceFile : "ATC:payload:" + state.Payload.PackageId + ":" + typePayload.DefTypeName;
                    bool fullList = typePayload.FullLists.TryGetValue(kvp.Key, out var listValue);
                    var check = fullList ? ValidateLoadedDefList(state.CurrentDefType, kvp.Key) : ValidateLoadedDefTranslation(state.CurrentDefType, kvp.Key);
                    string value = PrepareDefTranslationText(check, kvp.Value);
                    RecordDefRepair(typePayload.DefTypeName, kvp.Key, kvp.Value, source, check, "memory-payload", appliedValue: value);
                    if (!check.Allowed)
                    {
                        if (check.Temporary || check.Reason == "missing_handle" || check.Reason == "index_out_of_bounds")
                            RememberNativeDeferredCandidate(state, typePayload, kvp.Key);
                        state.RejectedDefs++;
                        continue;
                    }

                    if (IsFailedNativeCandidate(state.CurrentDefType, check.Path, value, source))
                    {
                        state.RejectedDefs++;
                        continue;
                    }
                    string canonicalKey = state.CurrentDefType.FullName + "/" + check.ResolvedPath;
                    if (state.CanonicalCandidates.TryGetValue(canonicalKey, out var previousCandidate))
                    {
                        RecordDefRepair(typePayload.DefTypeName, kvp.Key, kvp.Value, source, check, "memory-alias", previousCandidate.injection == kvp.Value ? "相同譯文合併" : "衝突");
                        RecordDefRepair(typePayload.DefTypeName, previousCandidate.path, previousCandidate.injection, previousCandidate.fileSource, check, "memory-selected", "保留候選");
                        state.RejectedDefs++;
                        continue;
                    }

                    string target = check.Path;
                    var candidate = CreateRuntimeDefInjection(target, value, source);
                    if (fullList) { candidate.injection = null; candidate.fullListInjection = new List<string>(listValue); }
                    string runtimeKey = GetRuntimeDefKey(state.CurrentDefType, target);
                    state.CurrentPackage.injections.TryGetValue(target, out var previous);
                    state.SubmissionRollbacks[candidate] = new RuntimeSubmissionRollback {
                        Package = state.CurrentPackage, Previous = previous,
                        WasRuntimeOwned = RuntimeInjectedDefKeys.Contains(runtimeKey), HadBaseline = RuntimeDefBaselines.ContainsKey(runtimeKey)
                    };
                    RememberRuntimeDefBaseline(state.CurrentDefType, state.CurrentPackage, target);
                    state.CanonicalCandidates[canonicalKey] = candidate;
                    state.CurrentPackage.injections[target] = candidate;
                    RuntimeInjectedDefKeys.Add(runtimeKey);
                    state.Submitted.Add(candidate);
                    state.SubmittedTypes[candidate] = state.CurrentDefType;
                    state.SubmittedDefs++;
                }

                if (state.CurrentDefEntryIndex >= state.CurrentDefEntries.Count)
                {
                    state.CurrentPackage = null;
                    state.CurrentDefType = null;
                    state.CurrentDefEntries = null;
                    state.CurrentDefEntryIndex = 0;
                    state.DefTypeIndex++;
                }
            }

            return state.DefTypeIndex < state.Payload.DefsByType.Count || state.CurrentDefEntries != null;
        }

        private static bool ApplyNativeMemoryBatch(MemoryDropApplyState state)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (state.NativeIndex < state.Submitted.Count && TranslationFrameBudget.HasTime)
            {
                var candidate = state.Submitted[state.NativeIndex++];
                Type type = state.SubmittedTypes[candidate];
                var isolated = new DefInjectionPackage(type);
                isolated.injections[candidate.path] = candidate;
                TranslationConfigGuard.ApplyValidated(isolated);
                if (!candidate.injected && state.SubmissionRollbacks.TryGetValue(candidate, out var rollback))
                {
                    if (rollback.Package.injections.TryGetValue(candidate.path, out var current) && ReferenceEquals(current, candidate))
                    {
                        if (rollback.Previous != null) rollback.Package.injections[candidate.path] = rollback.Previous;
                        else rollback.Package.injections.Remove(candidate.path);
                    }
                    string runtimeKey = GetRuntimeDefKey(type, candidate.path);
                    if (!rollback.WasRuntimeOwned) RuntimeInjectedDefKeys.Remove(runtimeKey);
                    if (!rollback.HadBaseline) RuntimeDefBaselines.Remove(runtimeKey);
                }
            }
            return state.NativeIndex < state.Submitted.Count;
        }

        private static void FinalizeMemoryRestores(MemoryDropApplyState state, bool success)
        {
            if (state.RestoresFinalized) return;
            if (success && !state.Payload.KeyedOnly && state.NeedsDataInjection)
            {
                RemoveTemporaryDefRestores(state.TemporaryDefRestores);
            }
            state.RestoresFinalized = true;
        }

        private static bool ApplyMemoryNormalizationBatch(MemoryDropApplyState state)
        {
            if (state.NormalizationDone || state.Payload.KeyedOnly || (state.SubmittedDefs == 0 && !state.NeedsDataInjection)) return false;
            if (state.Normalization == null) state.Normalization = NormalizeInjectedDefSteps(state.Submitted.Where(i => i.injected).Select(i =>
                GenDefDatabase.GetDefSilentFail(state.SubmittedTypes[i], (i.normalizedPath ?? i.path).Split('.')[0], false)).Where(d => d != null).Distinct()).GetEnumerator();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (TranslationFrameBudget.HasTime)
                if (!state.Normalization.MoveNext())
                {
                    state.Normalization.Dispose(); state.Normalization = null; state.NormalizationDone = true;
                    return false;
                }
            return true;
        }

        private static void PrepareMemoryDropFinish(MemoryDropApplyState state, bool success)
        {
            if (state.NativeFinalized) return;
            // The live dispatcher has already advanced each bounded stage. Direct
            // callers use the same code and drain it synchronously for completion.
            if (success && state.NativeIndex < state.Submitted.Count) throw new InvalidOperationException("Native application incomplete.");
            FinalizeMemoryRestores(state, success);


            if (state.RejectedDefs > 0)
                Log.Warning($"[AutoTranslationCore] Blocked {state.RejectedDefs} unsafe DefInjected entries during memory injection.");

            state.InjectedDefs = state.Submitted.Count(i => i.injected);
            state.FailedDefs = state.SubmittedDefs - state.InjectedDefs;
            state.NativeErrorsAfter = TranslationRepairController.CountNativeErrors(LanguageDatabase.activeLanguage);
            foreach (var failed in state.Submitted.Where(i => !i.injected))
            {
                var outcome = new DefTranslationPathResult { Path = failed.path, Reason = "native_injection_failed", Evidence = "game injected=false after both phases; no success claimed" };
                string defType = state.SubmittedTypes.TryGetValue(failed, out var submittedType) ? submittedType.FullName : "runtime";
                RecordDefRepair(defType, failed.path, failed.injection, failed.fileSource, outcome, "native-result");
            }
            state.NativeFinalized = true;
        }

        private static void FinishMemoryDropApply(MemoryDropApplyState state, bool success)
        {
            if (state == null) return;
            bool completed = false;
            try
            {
                PrepareMemoryDropFinish(state, success);
                // Immediate/headless callers retain synchronous completion. The
                // live pump already awaited this checkpoint without blocking.
                if (state.Preservation == null) FlushSelfRepair();
                Log.Message($"[AutoTranslationCore] Def injection results: submitted={state.SubmittedDefs}, succeeded={state.InjectedDefs}, failed={state.FailedDefs}, skipped={state.RejectedDefs}.");
                if (state.NativeErrorsBefore.HasValue && state.NativeErrorsAfter.HasValue)
                    Log.Message($"[AutoTranslationCore] Native translation diagnostics: before={state.NativeErrorsBefore.Value}, after={state.NativeErrorsAfter.Value}. These counts use current language/package error lists; submitted failures are a separate count.");

                bool injectionCompleted = ShouldContinueRepairAfterInjection(success, state.FailedDefs, state.RejectedDefs);
                bool fullyVerified = injectionCompleted && state.FailedDefs == 0 && state.RejectedDefs == 0;

                if (injectionCompleted && (state.InjectedKeyed > 0 || state.InjectedDefs > 0 || state.ClearedKeyed > 0 || state.ClearedDefs > 0))
                {
                    if (state.InjectedKeyed > 0 || state.ClearedKeyed > 0)
                    {
                        NativeUiTranslationLookup.Invalidate();
                        CachedModTranslationCompatibility.TranslationsChanged();
                        RequestStaticCachedTranslationRefresh();
                    }

                    if (!state.Payload.KeyedOnly)
                    {
                        AutoTranslatorSettings.AddLog("🪂 " + AutoTranslatorAPI.TranslateText("ATC_Log_MemoryDropSuccess", state.InjectedKeyed, state.InjectedDefs));
                    }

                    string mode = state.Payload.KeyedOnly
                        ? "Keyed memory prewarm"
                        : state.Payload.PackageScoped
                            ? "Package memory drop"
                            : state.RejectedDefs > 0
                                ? "Memory Drop Partial"
                                : "Memory Drop Success";
                    Log.Message($"[AutoTranslationCore] {mode}: Injected {state.InjectedKeyed} Keyed & {state.InjectedDefs} Defs without restart. Cleared {state.ClearedKeyed} Keyed & {state.ClearedDefs} Defs. Package={state.Payload.PackageId ?? "<full>"}");
                    if (state.Payload.OfficialFallbackKeyed > 0 || state.Payload.OfficialFallbackDefs > 0)
                    {
                        Log.Message($"[AutoTranslationCore] Official fallback restored {state.Payload.OfficialFallbackKeyed} Keyed & {state.Payload.OfficialFallbackDefs} Defs during package memory drop.");
                    }
                }

                if (fullyVerified && state.Payload.KeyedOnly && state.InjectedKeyed > 0)
                {
                    lock (_memoryDropStampLock) _lastKeyedMemoryDropStamp = state.Payload.PreparedStamp;
                }
                else if (fullyVerified && !state.Payload.KeyedOnly && !state.Payload.PackageScoped)
                {
                    lock (_memoryDropStampLock) _lastFullMemoryDropStamp = state.Payload.PreparedStamp;
                }
                completed = injectionCompleted;
                if (injectionCompleted && state.Payload.RepairRequest != 0 && !state.Payload.KeyedOnly &&
                    state.Payload.Epoch == GameDataEpoch &&
                    state.Payload.CancellationGeneration == AutoTranslatorSettings.PipelineCancellationGeneration &&
                    ReferenceEquals(state.Payload.Language, LanguageDatabase.activeLanguage))
                    // Retain only completion metadata, not the large injection dictionaries.
                    completedRepairPayload = new MemoryDropPayload {
                        RepairRequest = state.Payload.RepairRequest, Epoch = state.Payload.Epoch,
                        CancellationGeneration = state.Payload.CancellationGeneration, Language = state.Payload.Language,
                        TargetFolder = state.Payload.TargetFolder, FileHashes = state.Payload.FileHashes,
                        PackageScoped = state.Payload.PackageScoped, PackageId = state.Payload.PackageId,
                        ChangedKeys = state.Payload.ChangedKeys
                    };
                if (fullyVerified && state.CacheVerified && state.Payload.Epoch == GameDataEpoch &&
                    state.Payload.CancellationGeneration == AutoTranslatorSettings.PipelineCancellationGeneration &&
                    ReferenceEquals(state.Payload.Language, LanguageDatabase.activeLanguage))
                    LastVerifiedMemorySignature = state.Payload.InputSignature;
            }
            finally
            {
                state.Timer?.Stop();
                if (!state.Payload.KeyedOnly)
                {
                    AutoTranslatorPerf.RecordMemoryDrop(state.Timer != null ? state.Timer.ElapsedMilliseconds : 0, state.InjectedKeyed, state.InjectedDefs);
                }

                if (ReferenceEquals(_activeMemoryDropApply, state))
                {
                    _activeMemoryDropApply = null;
                }
                Interlocked.Exchange(ref _startupFullMemoryDropQueued, 0);
                CompleteRepairMemoryDrop(state.Payload.RepairRequest, completed);
                StartMemoryDropPreparationIfNeeded();
            }
        }

        private static bool ShouldContinueRepairAfterInjection(bool preparationSucceeded, int failedDefs, int rejectedDefs)
        {
            // Rejected candidates are already retained as unresolved journal
            // records. They must not prevent successful candidates from being
            // verified in the same repair run.
            // Native failures have their own unresolved records. Finish and
            // persist the baseline for the attempted files, then skip them on
            // unchanged restarts. Only an interrupted operation is unfinished.
            return preparationSucceeded;
        }

        private static void ApplyMemoryDropPayloadImmediate(MemoryDropPayload payload, System.Diagnostics.Stopwatch timer)
        {
            if (payload == null) return;
            if (LanguageDatabase.activeLanguage == null || AutoTranslatorMod.Settings == null) return;

            var state = new MemoryDropApplyState
            {
                Payload = payload,
                Timer = timer ?? System.Diagnostics.Stopwatch.StartNew(),
                KeyedEntries = payload.Keyed != null
                    ? payload.Keyed.ToList()
                    : new List<KeyValuePair<string, string>>()
            };

            LoadedLanguage activeLang = LanguageDatabase.activeLanguage;
            using (TranslationFrameBudget.UnlimitedForSynchronousFixture())
            {
            while (ApplyKeyedBatch(state, activeLang)) { }
            while (!payload.KeyedOnly && ApplyDefBatch(state, activeLang)) { }
            while (!payload.KeyedOnly && ApplyNativeMemoryBatch(state)) { }
            while (ApplyMemoryNormalizationBatch(state)) { }
            FinishMemoryDropApply(state, true);
            }
        }

        private static void NormalizeInjectedDefs()
        {
            foreach (var unused in NormalizeInjectedDefSteps()) { }
        }

        private static Dictionary<ThingDef, List<RecipeDef>> productRecipes;
        private static int productRecipeEpoch = -1, productRecipeCount;
        private static IEnumerable<Def> NormalizeInjectedDefSteps(IEnumerable<Def> touched = null)
        {
            var touchedArray = touched?.ToArray();
            IEnumerable<RecipeDef> recipes = DefDatabase<RecipeDef>.AllDefsListForReading;
            if (touchedArray != null)
            {
                var related = new HashSet<RecipeDef>(touchedArray.OfType<RecipeDef>());
                if (touchedArray.OfType<ThingDef>().Any())
                {
                    var all = DefDatabase<RecipeDef>.AllDefsListForReading;
                    if (productRecipes == null || productRecipeEpoch != GameDataEpoch || productRecipeCount != all.Count)
                    {
                        var index = new Dictionary<ThingDef, List<RecipeDef>>();
                        foreach (var recipe in all)
                        {
                            var product = recipe.products?.FirstOrDefault()?.thingDef;
                            if (product != null)
                            {
                                if (!index.TryGetValue(product, out var list)) index[product] = list = new List<RecipeDef>();
                                list.Add(recipe);
                            }
                            yield return recipe; // One shared frame budget includes building the dependency index.
                        }
                        productRecipes = index; productRecipeEpoch = GameDataEpoch; productRecipeCount = all.Count;
                    }
                    foreach (var thing in touchedArray.OfType<ThingDef>())
                        if (productRecipes.TryGetValue(thing, out var list)) foreach (var recipe in list) related.Add(recipe);
                }
                recipes = related;
            }
            foreach (var recipe in recipes)
            {
                NormalizeAutoGeneratedRecipeText(recipe);
                yield return recipe;
            }

            foreach (var thingDef in touchedArray == null ? DefDatabase<ThingDef>.AllDefsListForReading : touchedArray.OfType<ThingDef>())
            {
                NormalizeThingDescriptionLead(thingDef);

                if (thingDef.race != null && !string.IsNullOrEmpty(thingDef.label))
                {

                    if (thingDef.race.corpseDef != null && thingDef.race.corpseDef.defName == "Corpse_" + thingDef.defName)
                    {
                        TranslationConfigGuard.ApplyDerivedText(typeof(ThingDef), thingDef.race.corpseDef, "label", "CorpseLabel".Translate(thingDef.label).Resolve());
                    }


                    if (thingDef.race.meatDef != null && thingDef.race.meatDef.defName == "Meat_" + thingDef.defName)
                    {
                        TranslationConfigGuard.ApplyDerivedText(typeof(ThingDef), thingDef.race.meatDef, "label", "MeatLabel".Translate(thingDef.label).Resolve());
                    }


                    if (thingDef.race.leatherDef != null && !string.IsNullOrEmpty(thingDef.race.leatherDef.label) && thingDef.race.leatherDef.defName == "Leather_" + thingDef.defName)
                    {
                        string engName = thingDef.defName;
                        if (thingDef.race.leatherDef.label.IndexOf(engName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string leatherLabel = System.Text.RegularExpressions.Regex.Replace(
                                thingDef.race.leatherDef.label,
                                engName,
                                thingDef.label,
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                            );
                            TranslationConfigGuard.ApplyDerivedText(typeof(ThingDef), thingDef.race.leatherDef, "label", leatherLabel);
                        }
                    }
                }
                yield return thingDef;
            }
        }


        // 這個方法負責處理 記憶體DropInjectNow 相關流程。
        // EN: This method handles memory drop inject now.
        public static void MemoryDrop_InjectNow()
        {
            MemoryDrop_InjectNow(false);
        }

        public static void MemoryDrop_InjectKeyedNow()
        {
            MemoryDrop_InjectNow(true);
        }

        private static void MemoryDrop_InjectNow(bool keyedOnly)
        {
            QueueMemoryDropPreparation(keyedOnly, true);
        }

        // 這個方法負責清除 Global翻譯Database 資料。
        // EN: This method clears global translation database.
        private static void NormalizeAutoGeneratedRecipeText(RecipeDef recipe)
        {
            if (recipe == null || recipe.products == null || recipe.products.Count == 0) return;

            ThingDef productDef = recipe.products[0].thingDef;
            if (productDef == null || string.IsNullOrWhiteSpace(productDef.label)) return;
            string translatedProductLabel = ResolveTranslatedRecipeProductLabel(productDef);
            if (string.IsNullOrWhiteSpace(translatedProductLabel)) return;
            bool normalizeLabel = ShouldNormalizeAutoGeneratedRecipeLabel(recipe);
            bool normalizeJobString = ShouldNormalizeAutoGeneratedRecipeJobString(recipe);
            if (!normalizeLabel && !normalizeJobString) return;

            string verb = GetRecipeVerb(recipe);
            if (string.IsNullOrWhiteSpace(verb)) return;

            if (normalizeLabel)
            {
                TranslationConfigGuard.ApplyDerivedText(typeof(RecipeDef), recipe, "label", $"{verb} {translatedProductLabel}");
            }
            if (recipe.defName.StartsWith("Make_", StringComparison.OrdinalIgnoreCase) &&
                IsMakeRecipeVerb(recipe) &&
                !string.IsNullOrWhiteSpace(recipe.jobString) &&
                normalizeJobString)
            {
                TranslationConfigGuard.ApplyDerivedText(typeof(RecipeDef), recipe, "jobString", "RecipeMakeJobString".Translate(translatedProductLabel).Resolve());
            }
        }

        private static string ResolveTranslatedRecipeProductLabel(ThingDef productDef)
        {
            if (productDef == null || string.IsNullOrWhiteSpace(productDef.label)) return "";
            TargetLanguage targetLanguage = AutoTranslatorMod.Settings.TargetLang;
            if (targetLanguage == TargetLanguage.English || LanguageDetector.LooksLikeTargetLanguage(productDef.label, targetLanguage))
            {
                return productDef.label;
            }

            string normalizedPath = productDef.defName + ".label";
            string globalKey = "ThingDef/" + normalizedPath;
            if (GlobalPrimaryDefDict.TryGetValue(globalKey, out string cachedLabel) && !string.IsNullOrWhiteSpace(cachedLabel))
            {
                return cachedLabel;
            }

            LoadedLanguage activeLanguage = LanguageDatabase.activeLanguage;
            DefInjectionPackage package = activeLanguage?.defInjections?.FirstOrDefault(p => p.defType == typeof(ThingDef));
            if (package?.injections != null &&
                package.injections.TryGetValue(normalizedPath, out DefInjectionPackage.DefInjection injection) &&
                !string.IsNullOrWhiteSpace(injection.injection))
            {
                return injection.injection;
            }

            return "";
        }

        private static bool IsKeyedMemoryDropStampCurrent(string keyedPath, string targetFolder)
        {
            string stamp = BuildKeyedMemoryDropStamp(keyedPath, targetFolder);
            lock (_memoryDropStampLock)
            {
                return !string.IsNullOrEmpty(stamp) &&
                       string.Equals(_lastKeyedMemoryDropStamp, stamp, StringComparison.Ordinal);
            }
        }

        private static void MarkKeyedMemoryDropStamp(string keyedPath, string targetFolder)
        {
            string stamp = BuildKeyedMemoryDropStamp(keyedPath, targetFolder);
            if (string.IsNullOrEmpty(stamp)) return;

            lock (_memoryDropStampLock)
            {
                _lastKeyedMemoryDropStamp = stamp;
            }
        }

        private static string BuildKeyedMemoryDropStamp(string keyedPath, string targetFolder)
        {
            try
            {
                long ticks = 0L;
                if (Directory.Exists(keyedPath))
                {
                    ticks = Directory.GetLastWriteTimeUtc(keyedPath).Ticks;
                    foreach (string file in GetXmlFilesCached(keyedPath, SearchOption.AllDirectories))
                    {
                        long fileTicks = File.GetLastWriteTimeUtc(file).Ticks;
                        if (fileTicks > ticks) ticks = fileTicks;
                    }
                }

                return (targetFolder ?? "") + "|" + ticks.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static bool IsFullMemoryDropStampCurrent(string langRoot, string targetFolder)
        {
            string stamp = BuildFullMemoryDropStamp(langRoot, targetFolder);
            lock (_memoryDropStampLock)
            {
                return !string.IsNullOrEmpty(stamp) &&
                       string.Equals(_lastFullMemoryDropStamp, stamp, StringComparison.Ordinal);
            }
        }

        private static void MarkFullMemoryDropStamp(string langRoot, string targetFolder)
        {
            string stamp = BuildFullMemoryDropStamp(langRoot, targetFolder);
            if (string.IsNullOrEmpty(stamp)) return;

            lock (_memoryDropStampLock)
            {
                _lastFullMemoryDropStamp = stamp;
            }
        }

        private static string BuildFullMemoryDropStamp(string langRoot, string targetFolder)
        {
            try
            {
                return (targetFolder ?? "") + "|" + SelfRepairEnvironment + "|" + BuildXmlTreeFingerprint(langRoot);
            }
            catch
            {
                return null;
            }
        }

        private static bool ShouldNormalizeAutoGeneratedRecipeLabel(RecipeDef recipe)
        {
            if (recipe == null || string.IsNullOrWhiteSpace(recipe.defName) || string.IsNullOrWhiteSpace(recipe.label)) return false;
            if (!recipe.defName.StartsWith("Make_", StringComparison.OrdinalIgnoreCase)) return false;
            bool hasEnglishResidual = HasLikelyEnglishResidual(recipe.label, recipe.label);
            if (LanguageDetector.LooksLikeTargetLanguage(recipe.label, AutoTranslatorMod.Settings.TargetLang) && !hasEnglishResidual) return false;

            return recipe.label.StartsWith("make ", StringComparison.OrdinalIgnoreCase) ||
                   recipe.label.StartsWith("make_", StringComparison.OrdinalIgnoreCase) ||
                   recipe.label.StartsWith("Make ", StringComparison.Ordinal) ||
                   hasEnglishResidual;
        }

        private static bool ShouldNormalizeAutoGeneratedRecipeJobString(RecipeDef recipe)
        {
            if (recipe == null || string.IsNullOrWhiteSpace(recipe.jobString)) return false;
            bool hasEnglishResidual = HasLikelyEnglishResidual(recipe.jobString, recipe.jobString);
            if (LanguageDetector.LooksLikeTargetLanguage(recipe.jobString, AutoTranslatorMod.Settings.TargetLang) && !hasEnglishResidual) return false;
            return recipe.jobString.StartsWith("Making ", StringComparison.OrdinalIgnoreCase) || hasEnglishResidual;
        }

        private static bool IsMakeRecipeVerb(RecipeDef recipe)
        {
            return string.Equals(GetRecipeVerbKind(recipe), "make", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRecipeVerb(RecipeDef recipe)
        {
            return GetRecipeVerbText(GetRecipeVerbKind(recipe));
        }

        private static string GetRecipeVerbKind(RecipeDef recipe)
        {
            if (recipe == null || string.IsNullOrWhiteSpace(recipe.defName)) return "";
            string defName = recipe.defName;
            string jobString = recipe.jobString ?? "";
            string label = recipe.label ?? "";
            string combined = defName + " " + jobString + " " + label;

            if (combined.IndexOf("smelt", StringComparison.OrdinalIgnoreCase) >= 0) return "smelt";
            if (combined.IndexOf("refin", StringComparison.OrdinalIgnoreCase) >= 0) return "refine";
            if (combined.IndexOf("extract", StringComparison.OrdinalIgnoreCase) >= 0) return "extract";
            if (combined.IndexOf("incubat", StringComparison.OrdinalIgnoreCase) >= 0) return "incubate";
            if (combined.IndexOf("reclaim", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("recycle", StringComparison.OrdinalIgnoreCase) >= 0) return "recycle";
            if (defName.StartsWith("Make_", StringComparison.OrdinalIgnoreCase)) return "make";
            return "";
        }

        private static string GetRecipeVerbText(string verb)
        {
            bool simplified = AutoTranslatorMod.Settings.TargetLang == TargetLanguage.Simplified;
            switch (verb)
            {
                case "make": return simplified ? "制造" : "製作";
                case "smelt": return simplified ? "熔炼" : "熔煉";
                case "refine": return simplified ? "提炼" : "提煉";
                case "extract": return simplified ? "提取" : "提取";
                case "incubate": return simplified ? "培育" : "培育";
                case "recycle": return simplified ? "回收" : "回收";
                default: return "";
            }
        }

        private static void NormalizeThingDescriptionLead(ThingDef thingDef)
        {
            if (thingDef == null || string.IsNullOrWhiteSpace(thingDef.label) || string.IsNullOrWhiteSpace(thingDef.description)) return;
            if (AutoTranslatorMod.Settings.TargetLang != TargetLanguage.Simplified &&
                AutoTranslatorMod.Settings.TargetLang != TargetLanguage.Traditional) return;

            string label = thingDef.label.Trim();
            string description = thingDef.description.TrimStart();
            if (description.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return;

            string[] markers = { "是一", "是", "為", "为" };
            int markerIndex = -1;
            string marker = "";
            foreach (string candidate in markers)
            {
                int index = description.IndexOf(candidate, StringComparison.Ordinal);
                if (index > 0 && index <= 18 && (markerIndex < 0 || index < markerIndex))
                {
                    markerIndex = index;
                    marker = candidate;
                }
            }

            if (markerIndex <= 0 || markerIndex > 18) return;

            string lead = description.Substring(0, markerIndex).Trim();
            if (lead.Length < 2 || lead.Length > 18) return;
            if (!lead.Any(IsCjkChar) || lead.Contains(" ") || lead.Contains("\n") || lead.Contains("\r")) return;

            TranslationConfigGuard.ApplyDerivedText(typeof(ThingDef), thingDef, "description", label + description.Substring(markerIndex));
        }

        private static bool IsCjkChar(char c)
        {
            return (c >= '\u3400' && c <= '\u4DBF') ||
                   (c >= '\u4E00' && c <= '\u9FFF') ||
                   (c >= '\uF900' && c <= '\uFAFF');
        }

        private static void ClearGlobalTranslationDatabase()
        {
            GlobalPrimaryDefDict.Clear();
            GlobalSecondaryDefDict.Clear();
            GlobalPrimaryKeyedDict.Clear();
            GlobalSecondaryKeyedDict.Clear();
            GlobalPrimaryDefSourceDict.Clear();
            GlobalSecondaryDefSourceDict.Clear();
            GlobalPrimaryKeyedSourceDict.Clear();
            GlobalSecondaryKeyedSourceDict.Clear();
            AutoTranslatorSettings.AddLog("🧹 " + "ATC_Log_Clean".Translate());
        }


        // 這個方法負責建立 Global翻譯Database 所需資料。
        // EN: This method builds global translation database.
        private static void InvalidateGlobalTranslationDatabaseCache()
        {
            lock (GlobalTranslationDatabaseCacheLock)
            {
                GlobalTranslationDatabaseCacheGeneration++;
                GlobalTranslationDatabaseCacheKey = null;
                GlobalTranslationDatabaseCachedPrimaryDefDict = null;
                GlobalTranslationDatabaseCachedSecondaryDefDict = null;
                GlobalTranslationDatabaseCachedPrimaryKeyedDict = null;
                GlobalTranslationDatabaseCachedSecondaryKeyedDict = null;
                GlobalTranslationDatabaseCachedPrimaryDefSourceDict = null;
                GlobalTranslationDatabaseCachedSecondaryDefSourceDict = null;
                GlobalTranslationDatabaseCachedPrimaryKeyedSourceDict = null;
                GlobalTranslationDatabaseCachedSecondaryKeyedSourceDict = null;
            }
        }

        private static void BuildGlobalTranslationDatabase(List<ModMetaData> mods)
        {
            AutoTranslatorSettings.AddLog("📦 " + "ATC_Log_Init".Translate());

            var settings = AutoTranslatorMod.Settings;
            settings.SubTaskName = "ATC_SubTask_AnalyzingDict".Translate();
            settings.SubProgress = 0f;

            GlobalPrimaryDefDict.Clear(); GlobalSecondaryDefDict.Clear();
            GlobalPrimaryKeyedDict.Clear(); GlobalSecondaryKeyedDict.Clear();
            GlobalPrimaryDefSourceDict.Clear(); GlobalSecondaryDefSourceDict.Clear();
            GlobalPrimaryKeyedSourceDict.Clear(); GlobalSecondaryKeyedSourceDict.Clear();

            string cacheKey = BuildGlobalTranslationDatabaseCacheKey(mods, settings.TargetLang);
            int cacheGeneration;
            lock (GlobalTranslationDatabaseCacheLock)
            {
                cacheGeneration = GlobalTranslationDatabaseCacheGeneration;
                if (!string.IsNullOrEmpty(cacheKey) &&
                    string.Equals(GlobalTranslationDatabaseCacheKey, cacheKey, StringComparison.Ordinal) &&
                    GlobalTranslationDatabaseCachedPrimaryDefDict != null &&
                    GlobalTranslationDatabaseCachedSecondaryDefDict != null &&
                    GlobalTranslationDatabaseCachedPrimaryKeyedDict != null &&
                    GlobalTranslationDatabaseCachedSecondaryKeyedDict != null)
                {
                    GlobalPrimaryDefDict = new Dictionary<string, string>(GlobalTranslationDatabaseCachedPrimaryDefDict, StringComparer.OrdinalIgnoreCase);
                    GlobalSecondaryDefDict = new Dictionary<string, string>(GlobalTranslationDatabaseCachedSecondaryDefDict, StringComparer.OrdinalIgnoreCase);
                    GlobalPrimaryKeyedDict = new Dictionary<string, string>(GlobalTranslationDatabaseCachedPrimaryKeyedDict, StringComparer.OrdinalIgnoreCase);
                    GlobalSecondaryKeyedDict = new Dictionary<string, string>(GlobalTranslationDatabaseCachedSecondaryKeyedDict, StringComparer.OrdinalIgnoreCase);
                    GlobalPrimaryDefSourceDict = CloneProvenanceDict(GlobalTranslationDatabaseCachedPrimaryDefSourceDict);
                    GlobalSecondaryDefSourceDict = CloneProvenanceDict(GlobalTranslationDatabaseCachedSecondaryDefSourceDict);
                    GlobalPrimaryKeyedSourceDict = CloneProvenanceDict(GlobalTranslationDatabaseCachedPrimaryKeyedSourceDict);
                    GlobalSecondaryKeyedSourceDict = CloneProvenanceDict(GlobalTranslationDatabaseCachedSecondaryKeyedSourceDict);
                    settings.SubProgress = 1f;
                    settings.SubTaskName = "ATC_SubTask_DictDone".Translate();
                    AutoTranslatorSettings.AddLog("??" + AutoTranslatorAPI.TranslateText("ATC_Log_InitDone", GlobalPrimaryDefDict.Count));
                    return;
                }
            }

            string targetFolder = GetFolderNameByLanguage(settings.TargetLang);
            string otherFolder = GetSecondaryFolderNameByLanguage(settings.TargetLang);

            Action<string, Dictionary<string, string>, Dictionary<string, TranslationProvenanceEntry>, TargetLanguage?, TranslationProvenanceEntry> loadKeyed = (languageDir, dict, sourceDict, lang, sourceInfo) => {
                if (string.IsNullOrEmpty(languageDir) || dict == null || !Directory.Exists(languageDir)) return;
                string keyedDir = Path.Combine(languageDir, "Keyed");
                if (!Directory.Exists(keyedDir)) return;
                foreach (var file in GetXmlFilesCached(keyedDir, SearchOption.AllDirectories))
                {
                    var d = LoadXmlFileToDict(file, lang);
                    foreach (var kv in d)
                    {
                        dict[kv.Key] = kv.Value;
                        if (sourceDict != null) sourceDict[kv.Key] = CloneProvenance(sourceInfo, kv.Value);
                    }
                }
            };

            Action<string, Dictionary<string, string>, Dictionary<string, TranslationProvenanceEntry>, TargetLanguage?, TranslationProvenanceEntry> loadDef = (path, dict, sourceDict, lang, sourceInfo) => {
                if (!Directory.Exists(path)) return;
                foreach (var typeDir in Directory.GetDirectories(path))
                {
                    string defType = Path.GetFileName(typeDir);
                    foreach (var file in GetXmlFilesCached(typeDir, SearchOption.AllDirectories))
                    {
                        var d = LoadXmlFileToDict(file, lang);
                        foreach (var kv in d)
                        {
                            string globalKey = $"{defType}/{kv.Key}";
                            dict[globalKey] = kv.Value;
                            if (sourceDict != null) sourceDict[globalKey] = CloneProvenance(sourceInfo, kv.Value);
                        }
                    }
                }
                foreach (var file in GetXmlFilesCached(path, SearchOption.TopDirectoryOnly))
                {
                    var d = LoadXmlFileToDict(file, lang);
                    foreach (var kv in d)
                    {
                        string globalKey = $"General/{kv.Key}";
                        dict[globalKey] = kv.Value;
                        if (sourceDict != null) sourceDict[globalKey] = CloneProvenance(sourceInfo, kv.Value);
                    }
                }
            };

            string localPackLangRoot = Path.Combine(GetLocalPackPath(), "Languages");
            TranslationProvenanceEntry localPackSource = CreateProvenance(ProvenanceKindLocalPackExisting, "aitranslation.pack", "", localPackLangRoot, targetFolder, "");
            foreach (string targetLangDir in ResolveLanguageFolders(localPackLangRoot, targetFolder))
            {
                loadKeyed(targetLangDir, GlobalPrimaryKeyedDict, GlobalPrimaryKeyedSourceDict, settings.TargetLang, localPackSource);
                loadDef(Path.Combine(targetLangDir, "DefInjected"), GlobalPrimaryDefDict, GlobalPrimaryDefSourceDict, settings.TargetLang, localPackSource);
            }

            if (!string.IsNullOrEmpty(otherFolder))
            {
                TargetLanguage secLang = settings.TargetLang == TargetLanguage.Traditional ? TargetLanguage.Simplified : TargetLanguage.Traditional;
                foreach (string secondaryLangDir in ResolveLanguageFolders(localPackLangRoot, otherFolder))
                {
                    loadKeyed(secondaryLangDir, GlobalSecondaryKeyedDict, GlobalSecondaryKeyedSourceDict, secLang, localPackSource);
                    loadDef(Path.Combine(secondaryLangDir, "DefInjected"), GlobalSecondaryDefDict, GlobalSecondaryDefSourceDict, secLang, localPackSource);
                }
            }

            int modCount = mods != null ? mods.Count : 0;
            for (int i = 0; i < modCount; i++)
            {
                if (AutoTranslatorSettings.IsCancellationRequested) return;

                var mod = mods[i];

                settings.SubProgress = modCount > 0 ? (float)i / modCount : 1f;
                settings.SubTaskName = AutoTranslatorAPI.TranslateText("ATC_SubTask_BuildingDict", mod.Name);

                bool isTranslationPatch = IsTranslationPatchMod(mod);
                TranslationProvenanceEntry modSource = CreateProvenance(
                    isTranslationPatch ? ProvenanceKindExternalPatch : ProvenanceKindModNativeTarget,
                    mod.PackageId,
                    mod.Name,
                    mod.RootDir != null ? mod.RootDir.FullName : "",
                    targetFolder,
                    "");

                var langRoots = isTranslationPatch
                    ? GetAllTranslationPatchLangPaths(mod)
                    : GetAllEffectiveLangPaths(mod);

                foreach (var langRoot in langRoots)
                {
                    foreach (string targetLangDir in ResolveLanguageFolders(langRoot, targetFolder))
                    {
                        loadKeyed(targetLangDir, GlobalPrimaryKeyedDict, GlobalPrimaryKeyedSourceDict, settings.TargetLang, modSource);
                    }

                    if (!string.IsNullOrEmpty(otherFolder))
                    {
                        TargetLanguage secLang = settings.TargetLang == TargetLanguage.Traditional ? TargetLanguage.Simplified : TargetLanguage.Traditional;
                        foreach (string secondaryLangDir in ResolveLanguageFolders(langRoot, otherFolder))
                        {
                            loadKeyed(secondaryLangDir, GlobalSecondaryKeyedDict, GlobalSecondaryKeyedSourceDict, secLang, modSource);
                        }
                    }


                    foreach (string targetLangDir in ResolveLanguageFolders(langRoot, targetFolder))
                    {
                        loadDef(Path.Combine(targetLangDir, "DefInjected"), GlobalPrimaryDefDict, GlobalPrimaryDefSourceDict, settings.TargetLang, modSource);
                    }

                    if (!string.IsNullOrEmpty(otherFolder))
                    {
                        TargetLanguage secLang = settings.TargetLang == TargetLanguage.Traditional ? TargetLanguage.Simplified : TargetLanguage.Traditional;
                        foreach (string secondaryLangDir in ResolveLanguageFolders(langRoot, otherFolder))
                        {
                            loadDef(Path.Combine(secondaryLangDir, "DefInjected"), GlobalSecondaryDefDict, GlobalSecondaryDefSourceDict, secLang, modSource);
                        }
                    }
                }
            }

            settings.SubProgress = 1f;
            settings.SubTaskName = "ATC_SubTask_DictDone".Translate();
            AutoTranslatorSettings.AddLog("✨ " + AutoTranslatorAPI.TranslateText("ATC_Log_InitDone", GlobalPrimaryDefDict.Count));
            lock (GlobalTranslationDatabaseCacheLock)
            {
                if (!string.IsNullOrEmpty(cacheKey) && cacheGeneration == GlobalTranslationDatabaseCacheGeneration)
                {
                    GlobalTranslationDatabaseCacheKey = cacheKey;
                    GlobalTranslationDatabaseCachedPrimaryDefDict = new Dictionary<string, string>(GlobalPrimaryDefDict, StringComparer.OrdinalIgnoreCase);
                    GlobalTranslationDatabaseCachedSecondaryDefDict = new Dictionary<string, string>(GlobalSecondaryDefDict, StringComparer.OrdinalIgnoreCase);
                    GlobalTranslationDatabaseCachedPrimaryKeyedDict = new Dictionary<string, string>(GlobalPrimaryKeyedDict, StringComparer.OrdinalIgnoreCase);
                    GlobalTranslationDatabaseCachedSecondaryKeyedDict = new Dictionary<string, string>(GlobalSecondaryKeyedDict, StringComparer.OrdinalIgnoreCase);
                    GlobalTranslationDatabaseCachedPrimaryDefSourceDict = CloneProvenanceDict(GlobalPrimaryDefSourceDict);
                    GlobalTranslationDatabaseCachedSecondaryDefSourceDict = CloneProvenanceDict(GlobalSecondaryDefSourceDict);
                    GlobalTranslationDatabaseCachedPrimaryKeyedSourceDict = CloneProvenanceDict(GlobalPrimaryKeyedSourceDict);
                    GlobalTranslationDatabaseCachedSecondaryKeyedSourceDict = CloneProvenanceDict(GlobalSecondaryKeyedSourceDict);
                }
            }
        }

        private static Dictionary<string, TranslationProvenanceEntry> CloneProvenanceDict(Dictionary<string, TranslationProvenanceEntry> source)
        {
            Dictionary<string, TranslationProvenanceEntry> clone =
                new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
            if (source == null) return clone;

            foreach (KeyValuePair<string, TranslationProvenanceEntry> pair in source)
            {
                clone[pair.Key] = CloneProvenance(pair.Value);
            }

            return clone;
        }

        private static string BuildGlobalTranslationDatabaseCacheKey(List<ModMetaData> mods, TargetLanguage targetLang)
        {
            try
            {
                string targetFolder = GetFolderNameByLanguage(targetLang);
                string otherFolder = GetSecondaryFolderNameByLanguage(targetLang);
                string packLanguagesRoot = Path.Combine(GetLocalPackPath(), "Languages");
                string packFingerprint = BuildGlobalLanguageRootsFingerprint(
                    new List<string> { packLanguagesRoot },
                    targetFolder,
                    otherFolder);

                IEnumerable<string> modKeys = (mods ?? new List<ModMetaData>())
                    .Where(m => m != null && !string.IsNullOrEmpty(m.PackageId))
                    .Select(m =>
                    {
                        string root = m.RootDir?.FullName ?? "";
                        bool isTranslationPatch = IsTranslationPatchMod(m);
                        List<string> langRoots = isTranslationPatch
                            ? GetAllTranslationPatchLangPaths(m)
                            : GetAllEffectiveLangPaths(m);

                        string langFingerprint = BuildGlobalLanguageRootsFingerprint(
                            langRoots,
                            targetFolder,
                            otherFolder);
                        string loadFolders = BuildFileSignature(Path.Combine(root, "LoadFolders.xml"));
                        return (m.PackageId ?? "").ToLowerInvariant() + "|" +
                               (isTranslationPatch ? "patch" : "mod") + "|" +
                               loadFolders + "|" +
                               langFingerprint;
                    })
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

                return targetLang + "|" + packFingerprint + "|" + string.Join(";", modKeys.ToArray());
            }
            catch
            {
                return null;
            }
        }

        private static string BuildGlobalLanguageRootsFingerprint(List<string> langRoots, string targetFolder, string otherFolder)
        {
            if (langRoots == null || langRoots.Count == 0) return "none";

            List<string> parts = new List<string>();
            foreach (string langRoot in langRoots.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                List<string> languageDirs = new List<string>();
                try
                {
                    languageDirs.AddRange(ResolveLanguageFolders(langRoot, targetFolder));
                    if (!string.IsNullOrEmpty(otherFolder))
                    {
                        languageDirs.AddRange(ResolveLanguageFolders(langRoot, otherFolder));
                    }
                }
                catch { }

                languageDirs = languageDirs
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string rootPath = NormalizeCachePath(langRoot);
                if (languageDirs.Count == 0)
                {
                    parts.Add(rootPath + "|no-target-language");
                    continue;
                }

                foreach (string languageDir in languageDirs)
                {
                    string normalizedDir = NormalizeCachePath(languageDir);
                    parts.Add(rootPath + "|" + normalizedDir + "|" + BuildXmlTreeFingerprint(languageDir));
                }
            }

            return string.Join(";", parts.ToArray());
        }

    }
}
