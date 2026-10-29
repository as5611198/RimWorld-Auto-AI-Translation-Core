using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core
{
    internal static class TranslationRepairController
    {
        private static readonly object gate = new object();
        internal static readonly object OperationGate = new object();
        private static TranslationRepairRun live = new TranslationRepairRun();
        private static volatile TranslationRepairRun run;
        private static bool installed;
        private static volatile bool running, invalidated;
        private static bool waiting;
        private static string waitingState;
        private static long nextActiveUpdate;
        private static bool pumpingActiveRepair;
        private static LoadedLanguage waitingLanguage;
        private static TargetLanguage waitingTarget;
        private static DateTime nextNoticeCheck, readyAfter;
        private static string noticeSignature;
        private static string deferredNoticeEnvironment, pendingNoticeSignature, noticeState;
        private static bool noticeNeeded;
        private static WindowStack menuWindows;
        private static int observationRevision, checkedNoticeRevision = -1;
        private static string checkedNoticeEnvironment;
        private static AutoTranslatorScanner.MainMenuRepairWork work;
        private static bool automaticRepair;
        private static bool startupRepair;
        private static bool startupLookup;
        private static int startupLookupId;
        private static DateTime startupLookupDeadline;
        private static string[] automaticPackageIds;
        private static HashSet<string> repairFileScope;
        internal static string ModeLabel => automaticPackageIds != null || repairFileScope != null ? "ATC_Repair_ModeIncremental" : "ATC_Repair_ModeFull";

        private static bool InRepairScope(string file) => file != null &&
            (repairFileScope == null || repairFileScope.Contains(Path.GetFullPath(file))) &&
            AutoTranslatorScanner.MainMenuRepairWork.IsInPackageScope(file, automaticPackageIds);
        private static readonly HashSet<string> pendingPipelinePackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool automaticResultShown;
        private static bool pendingStartupResult;
        private static bool startupRepairAcknowledged;
        private static List<TranslationRepairRecord> verification;
        private static int verificationIndex;
        private static LoadedLanguage language;
        private static TargetLanguage target;
        private static DateTime applyDeadline, overallDeadline;
        private static Task reporting;
        private static int resultWriterRevision;
        internal static bool IsRunning => running;
        internal static bool IsWaiting => waiting;
        // The first-run repair owns the startup window. Automatic update
        // translation must wait until this queued or active repair has released
        // the pipeline, otherwise it can set IsRunning before the repair gets a
        // chance to start at the main menu.
        internal static bool StartupRepairActive => StartupTranslationGate.IsBlocking || StartupWorkActive;
        internal static bool StartupWorkActive => startupLookup || (startupRepair && (waiting || running));
        internal static string WaitingReason => StartBlockReason() ?? "ATC_Repair_WaitReady";
        internal static bool Applying => running && Phase == "ATC_Repair_Applying";
        internal static TranslationRepairRun Results => waiting ? null : run;
        internal static string Phase { get; private set; } = "ATC_Repair_IntroTitle";
        internal static string Failure { get; private set; }
        internal static int? NativeErrorsBefore { get; private set; }
        internal static int? NativeErrorsAfter { get; private set; }
        internal static bool Finished { get; private set; }
        internal static int Processed => waiting ? 0 : Phase == "ATC_Repair_Verifying" ? verificationIndex : work?.Processed ?? 0;
        internal static int Total => waiting ? 0 : Phase == "ATC_Repair_Verifying" ? verification?.Count ?? 0 : work?.Total ?? 0;

        internal static void EnsureAutomaticProgressWindow()
        {
            if (StartupTranslationGate.IsBlocking || !automaticRepair || Current.ProgramState != ProgramState.Entry || Find.WindowStack == null) return;
            // Automatic work uses one corner surface, including when an older
            // manual/result window was minimized before the pipeline finished.
            foreach (var window in Find.WindowStack.Windows.OfType<Window_TranslationRepair>().ToList()) window.Close();
            if (!Find.WindowStack.Windows.Any(w => w is Window_TranslationRepairProgress))
                Find.WindowStack.Add(new Window_TranslationRepairProgress());
        }

        private static void CloseAutomaticProgressWindow()
        {
            if (Find.WindowStack == null) return;
            foreach (var window in Find.WindowStack.Windows.OfType<Window_TranslationRepairProgress>().ToList())
                window.Close();
        }

        internal static void Install()
        {
            if (installed) return;
            installed = true;
            TranslationRepairJournal.Observed += Observe;
        }

        private static void Observe(TranslationRepairRecord record)
        {
            lock (gate)
            {
                int previous = live.Total;
                live.Observe(record);
                if (live.Total != previous) observationRevision++;
            }
            if (running && Phase != "ATC_Repair_Saving" && InRepairScope(record.SourceFile)) run?.Observe(record);
        }

        internal static void ClearEnvironment()
        {
            startupLookup = false; startupLookupId++;
            CancelWaiting();
            pendingPipelinePackages.Clear();
            automaticResultShown = false;
            pendingStartupResult = false;
            startupRepairAcknowledged = false;
            lock (gate) { live = new TranslationRepairRun(); observationRevision++; }
            invalidated = true;
            menuWindows = null;
            noticeSignature = deferredNoticeEnvironment = pendingNoticeSignature = null;
            checkedNoticeRevision = -1;
            checkedNoticeEnvironment = null;
            readyAfter = DateTime.UtcNow.AddSeconds(2);
        }

        // The game's own menu update proves that its UI root and window stack
        // exist. PlayDataLoader.Loaded can already be true during deferred load.
        internal static void OnMainMenuUpdate(UIRoot_Entry root)
        {
            if (root?.windows == null || !ReferenceEquals(root.windows, Find.WindowStack)) return;
            if (StartupTranslationGate.IsBlocking) return;
            if (pendingStartupResult)
            {
                pendingStartupResult = false;
                Open(true);
            }
            if (!ReferenceEquals(menuWindows, root.windows))
            {
                menuWindows = root.windows;
                checkedNoticeRevision = -1;
                checkedNoticeEnvironment = null;
                noticeSignature = null;
                nextNoticeCheck = DateTime.MinValue;
                readyAfter = DateTime.UtcNow.AddSeconds(2);
                NoticeState("attached to active main-menu window stack");
            }
            if (running || waiting) PumpActiveRepair();
            else Tick();
        }

        // Keep player-requested work moving on the same main-thread dispatcher
        // that drains memory injection. Menu notices still require a native UI
        // update, but an attached menu hook is not a completion signal for it.
        // Both callers share one cadence and cannot reenter a repair batch.
        internal static void PumpActiveRepair()
        {
            if ((!startupLookup && !running && !waiting && pendingPipelinePackages.Count == 0) || pumpingActiveRepair) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < nextActiveUpdate) return;
            nextActiveUpdate = now + System.Diagnostics.Stopwatch.Frequency / 20;
            pumpingActiveRepair = true;
            try { Tick(); }
            finally { pumpingActiveRepair = false; }
        }

        private static void NoticeState(string state)
        {
            if (state == noticeState) return;
            noticeState = state;
            Log.Message("[AutoTranslationCore] Repair introduction: " + state);
        }

        internal static void DeferNotice()
        {
            deferredNoticeEnvironment = NoticeSignature();
            NoticeState("deferred for this session by player");
        }

        internal static void NoticeDrawn()
        {
            noticeSignature = pendingNoticeSignature;
            NoticeState("rendered at main menu; waiting for player action");
        }

        private static List<TranslationRepairRecord> CurrentObservations(bool automatic = false)
        {
            string root = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Languages",
                AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang)).Replace('\\', '/').TrimEnd('/') + "/";
            List<TranslationRepairRecord> snapshot;
            lock (gate) snapshot = live.Snapshot();
            // These are current-session observations. Do not stat every source
            // from the menu poll; discovery/verification owns disk availability.
            return snapshot.Where(r => r.SourceFile != null &&
                ((r.SourceFile ?? "").Replace('\\', '/').StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                 (!automatic && (r.SourceKind == "third-party-read-overlay" || r.SourceKind == "native-duplicate-list"))) &&
                TranslationStartupRepairPolicy.ShouldAutoRepairObservation(automatic,
                    (r.SourceFile ?? "").Replace('\\', '/').StartsWith(root, StringComparison.OrdinalIgnoreCase), r.SourceKind)).ToList();
        }

        private static string NoticeSignature()
        {
            // v2 persisted both Later and successful completion. Only completed
            // runs may acknowledge v3; old dismissals cannot hide it forever.
            return "main-menu-repair-v3:" + AutoTranslatorScanner.SelfRepairEnvironment + ":" + AutoTranslatorMod.Settings.TargetLang;
        }

        private static string NoticeIssue(TranslationRepairRecord r) => TranslationRepairJournal.Hash(
            r.SourceFile + "|" + (r.NewKey ?? r.OriginalKey) + "|" + r.Translation);

        internal static void Acknowledge()
        {
            var settings = AutoTranslatorMod.Settings;
            string environment = NoticeSignature();
            var seen = new HashSet<string>(settings.LastRepairNotice == environment ? settings.SeenRepairIssues ?? new List<string>() : new List<string>());
            foreach (var record in run?.Snapshot() ?? CurrentObservations()) seen.Add(NoticeIssue(record));
            settings.LastRepairNotice = environment;
            settings.SeenRepairIssues = seen.OrderBy(s => s, StringComparer.Ordinal).ToList();
            LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
        }

        internal static void Open(bool showPreviousResult = true)
        {
            if (Current.ProgramState != ProgramState.Entry)
            {
                Find.WindowStack.Add(new Dialog_MessageBox("ATC_Repair_MainMenuOnly".Translate()));
                return;
            }
            if (!Find.WindowStack.Windows.Any(w => w is Window_TranslationRepair))
                Find.WindowStack.Add(new Window_TranslationRepair(showPreviousResult));
        }

        internal static bool Start()
        {
            if (!System.Threading.Monitor.TryEnter(OperationGate)) return false;
            try { return StartExclusive(); }
            finally { System.Threading.Monitor.Exit(OperationGate); }
        }

        private static bool StartExclusive()
        {
            if (running || waiting || Current.ProgramState != ProgramState.Entry || StartBlockReason() != null) return false;
            startupLookup = false; startupLookupId++;
            automaticRepair = false;
            startupRepair = false;
            automaticResultShown = false;
            automaticPackageIds = null;
            repairFileScope = null;
            BeginRepair();
            return true;
        }

        private static string StartBlockReason()
        {
            if (AutoTranslatorMod.Settings == null || LanguageDatabase.activeLanguage == null ||
                !AutoTranslatorScanner.StructureReady || StartupTranslationGate.NativeLoadingBlocksWork) return "ATC_Repair_WaitLoading";
            if (reporting != null && !reporting.IsCompleted) return "ATC_Repair_WaitSaving";
            if (AutoTranslatorSettings.IsRunning) return "ATC_Repair_WaitTranslation";
            if (AutoTranslatorScanner.PackMaintenanceBusy) return "ATC_Repair_WaitMaintenance";
            if (AutoTranslatorScanner.MemoryDropBusy) return "ATC_Repair_WaitApplying";
            return null;
        }

        // Waiting records player intent only. IsRunning stays false so the
        // translation and injection work being awaited can keep progressing.
        internal static bool RequestStart()
        {
            if (running || waiting) return true;
            if (Current.ProgramState != ProgramState.Entry || AutoTranslatorMod.Settings == null || LanguageDatabase.activeLanguage == null) return false;
            startupLookup = false; startupLookupId++;
            waitingTarget = AutoTranslatorMod.Settings.TargetLang;
            waitingLanguage = LanguageDatabase.activeLanguage;
            automaticRepair = false;
            startupRepair = false;
            automaticResultShown = false;
            automaticPackageIds = null;
            repairFileScope = null;
            invalidated = false; Finished = false; Failure = null;
            waitingState = null; nextActiveUpdate = 0;
            overallDeadline = DateTime.UtcNow.AddMinutes(3);
            waiting = true; Phase = "ATC_Repair_Waiting";
            try
            {
                if (UnityData.IsInMainThread) TickWaiting();
                else ATC_Dispatcher.RunOnMainThread(TickWaiting);
            }
            catch (Exception ex) { waiting = false; Fail(ex); }
            return true;
        }

        internal static bool RequestStartupRepairOrInject()
            => RequestRepairLookup(null);

        internal static void AcceptNativeStartupInjection()
        {
            startupLookup = false; startupLookupId++;
            startupRepair = false;
            startupRepairAcknowledged = true;
            Finished = true; Failure = null;
        }

        private static bool RequestRepairLookup(string[] packages)
        {
            if (AutoTranslatorMod.Settings == null || LanguageDatabase.activeLanguage == null) return false;
            int epoch = AutoTranslatorScanner.GameDataEpoch;
            var requestedLanguage = LanguageDatabase.activeLanguage;
            var requestedTarget = AutoTranslatorMod.Settings.TargetLang;
            var committed = AutoTranslatorScanner.CaptureCommittedChanges(packages);
            int lookupId = ++startupLookupId;
            Finished = false;
            Failure = null;
            startupLookup = true; startupLookupDeadline = DateTime.UtcNow.AddMinutes(2);
            startupRepair = packages == null;
            Task.Run(() => {
                try
                {
                    var plan = AutoTranslatorScanner.PlanStartupRepair(requestedTarget);
                    if (packages != null)
                        plan.ChangedFiles = plan.ChangedFiles.Where(f => AutoTranslatorScanner.MainMenuRepairWork.IsInPackageScope(f, packages) &&
                            (committed == null || committed.Files.ContainsKey(f))).ToArray();
                    if (plan.Decision != "fast-inject")
                    { var journal = AutoTranslatorScanner.RepairJournal; } // Only repair needs its history warmed before the menu starts.
                    ATC_Dispatcher.RunOnMainThread(() => {
                        if (lookupId != startupLookupId || epoch != AutoTranslatorScanner.GameDataEpoch || !ReferenceEquals(requestedLanguage, LanguageDatabase.activeLanguage) ||
                            requestedTarget != AutoTranslatorMod.Settings.TargetLang) return;
                        startupLookup = false;
                        if (plan.Decision == "fast-inject")
                        {
                            startupRepair = false;
                            Finished = true;
                            startupRepairAcknowledged = true;
                            Log.Message("[ATC] Repair skipped; baseline exists, no changed files selected for repair.");
                            if (packages == null) AutoTranslatorScanner.RequestMemoryDrop();
                        }
                        else if (plan.Decision == "repair-delta") QueueAutomaticRepair(packages, packages == null, plan.ChangedFiles);
                        else RequestAutomaticRepair(null, true); // Only absent baseline can reach first full repair.
                    });
                }
                catch (Exception ex)
                {
                    ATC_Dispatcher.RunOnMainThread(() => {
                        if (lookupId != startupLookupId || epoch != AutoTranslatorScanner.GameDataEpoch) return;
                        startupLookup = false; startupRepair = false;
                        Finished = true;
                        startupRepairAcknowledged = true;
                        Log.Warning("[ATC] Startup baseline check skipped; no full fallback: " + ex.Message);
                        if (packages == null) AutoTranslatorScanner.RequestMemoryDrop();
                    });
                }
            });
            return true;
        }

        internal static bool RequestPipelineRepair(string packageId, bool producedOutput)
        {
            if (!TranslationStartupRepairPolicy.DecidePipeline(producedOutput).Equals("repair-delta", StringComparison.Ordinal))
                return false;
            return RequestPipelineRepairs(new[] { packageId });
        }

        internal static bool RequestPipelineRepairs(IEnumerable<string> packageIds)
        {
            var packages = (packageIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (packages.Length == 0 || AutoTranslatorMod.Settings == null) return false;
            var requestedTarget = AutoTranslatorMod.Settings.TargetLang;
            var requestedLanguage = LanguageDatabase.activeLanguage;
            void Queue()
            {
                if (AutoTranslatorMod.Settings == null || AutoTranslatorMod.Settings.TargetLang != requestedTarget ||
                    !ReferenceEquals(requestedLanguage, LanguageDatabase.activeLanguage)) return;
                foreach (var package in packages) pendingPipelinePackages.Add(package);
                TryStartPendingPipelineRepair();
            }
            if (UnityData.IsInMainThread) Queue();
            else ATC_Dispatcher.RunOnMainThread(Queue);
            return true;
        }

        private static bool TryStartPendingPipelineRepair()
        {
            if (startupLookup || running || waiting || pendingPipelinePackages.Count == 0 || Current.ProgramState != ProgramState.Entry) return false;
            var packages = pendingPipelinePackages.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!RequestAutomaticRepair(packages)) return false;
            foreach (var package in packages) pendingPipelinePackages.Remove(package);
            return true;
        }

        private static bool RequestAutomaticRepair(string[] packageIds, bool isStartup = false)
        {
            if (packageIds == null && AutoTranslatorScanner.RepairBaselineExists) return RequestStartupRepairOrInject();
            if (packageIds != null && AutoTranslatorScanner.RepairBaselineExists) return RequestRepairLookup(packageIds);
            return QueueAutomaticRepair(packageIds, isStartup, null);
        }

        private static bool QueueAutomaticRepair(string[] packageIds, bool isStartup, string[] changedFiles)
        {
            if (running || waiting) return true;
            if ((!isStartup && Current.ProgramState != ProgramState.Entry) || AutoTranslatorMod.Settings == null || LanguageDatabase.activeLanguage == null)
                return false;
            automaticRepair = true;
            startupRepair = isStartup;
            automaticPackageIds = packageIds;
            repairFileScope = changedFiles == null ? null : new HashSet<string>(changedFiles.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            automaticResultShown = false;
            waitingTarget = AutoTranslatorMod.Settings.TargetLang;
            waitingLanguage = LanguageDatabase.activeLanguage;
            invalidated = false; Finished = false; Failure = null;
            waitingState = null; nextActiveUpdate = 0;
            overallDeadline = DateTime.UtcNow.AddMinutes(3);
            waiting = true; Phase = "ATC_Repair_Waiting";
            try
            {
                if (UnityData.IsInMainThread) TickWaiting();
                else ATC_Dispatcher.RunOnMainThread(TickWaiting);
            }
            catch (Exception ex) { waiting = false; Fail(ex); }
            return true;
        }

        internal static void CancelWaiting()
        {
            if (!waiting) return;
            bool wasAutomatic = automaticRepair;
            waiting = false; waitingLanguage = null;
            Log.Message("[AutoTranslationCore] Queued repair cancelled.");
            waitingState = null;
            Phase = "ATC_Repair_IntroTitle";
            if (wasAutomatic) CloseAutomaticProgressWindow();
        }

        private static void TickWaiting()
        {
            // The regular pump may have consumed this request before its
            // separately dispatched callback arrives.
            if (!waiting) return;
            if (DateTime.UtcNow > overallDeadline) { waiting = false; Fail(new TimeoutException("Repair waiting deadline exceeded; original text retained.")); return; }
            EnsureAutomaticProgressWindow();
            if (invalidated || (!startupRepair && Current.ProgramState != ProgramState.Entry) || AutoTranslatorMod.Settings == null ||
                waitingTarget != AutoTranslatorMod.Settings.TargetLang || !ReferenceEquals(waitingLanguage, LanguageDatabase.activeLanguage))
            { CancelWaiting(); return; }
            if (Current.ProgramState != ProgramState.Entry)
            {
                ReportWaiting("ATC_Repair_WaitLoading");
                return;
            }
            string reason = StartBlockReason();
            if (reason != null) { ReportWaiting(reason); return; }
            if (!System.Threading.Monitor.TryEnter(OperationGate)) { ReportWaiting("ATC_Repair_WaitReady"); return; }
            try
            {
                waiting = false; waitingLanguage = null;
                waitingState = null;
                Log.Message("[AutoTranslationCore] Queued repair ready; starting automatically.");
                BeginRepair();
            }
            finally { System.Threading.Monitor.Exit(OperationGate); }
        }

        private static void ReportWaiting(string reason)
        {
            string state = reason == "ATC_Repair_WaitApplying"
                ? reason + ": " + AutoTranslatorScanner.MemoryDropWaitState : reason;
            if (state == waitingState) return;
            waitingState = state;
            Log.Message("[AutoTranslationCore] Queued repair waiting: " + state);
        }

        private static void BeginRepair()
        {
            // Guard the last entry point too: a baseline may have appeared while
            // the first-run request waited for loading. Never start full then.
            if (automaticRepair && automaticPackageIds == null && repairFileScope == null && AutoTranslatorScanner.RepairBaselineExists)
            { RequestStartupRepairOrInject(); return; }
            AutoTranslatorScanner.LastVerifiedMemorySignature = null;
            System.Threading.Interlocked.Increment(ref resultWriterRevision);
            overallDeadline = DateTime.UtcNow.AddMinutes(10);
            target = AutoTranslatorMod.Settings.TargetLang;
            language = LanguageDatabase.activeLanguage;
            NativeErrorsBefore = CountNativeErrors(language);
            NativeErrorsAfter = null;
            run = new TranslationRepairRun();
            foreach (var record in CurrentObservations(automaticRepair).Where(r => InRepairScope(r.SourceFile))) run.Observe(record);
            invalidated = false;
            Finished = false; Failure = null; verification = null; verificationIndex = 0;
            Phase = "ATC_Repair_Checking";
            running = true;
            EnsureAutomaticProgressWindow();
            string root = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Languages", AutoTranslatorScanner.GetFolderNameByLanguage(target));
            Log.Message("[AutoTranslationCore] Repair mode=" + (ModeLabel == "ATC_Repair_ModeFull" ? "full" : "incremental") +
                "; scope=" + (repairFileScope != null ? string.Join(",", repairFileScope) : automaticPackageIds == null ? "<full>" : string.Join(",", automaticPackageIds)));
            work = new AutoTranslatorScanner.MainMenuRepairWork(root, run, !automaticRepair, automaticPackageIds, repairFileScope?.ToArray());
        }

        // Called only by the Unity main-thread pump, including while minimized.
        internal static void Tick()
        {
            if (startupLookup)
            {
                if (DateTime.UtcNow <= startupLookupDeadline) return;
                startupLookup = false; startupRepair = false; startupLookupId++;
                Log.Warning("[ATC] Startup lookup timed out; original text retained and late callback revoked.");
            }
            if (waiting)
            {
                try { TickWaiting(); } catch (Exception ex) { waiting = false; Fail(ex); }
                return;
            }
            if (!running)
            {
                if (TryStartPendingPipelineRepair()) return;
                try { PollNotice(); }
                catch (Exception ex)
                {
                    nextNoticeCheck = DateTime.UtcNow.AddMinutes(1);
                    Log.Warning("[AutoTranslationCore] Repair notice: " + ex.Message);
                }
                return;
            }
            try
            {
                if (DateTime.UtcNow > overallDeadline) throw new TimeoutException("Repair deadline exceeded; late writes revoked.");
                if (invalidated || Current.ProgramState != ProgramState.Entry || !ReferenceEquals(language, LanguageDatabase.activeLanguage) ||
                    AutoTranslatorMod.Settings.TargetLang != target)
                    throw new InvalidOperationException("Repair environment changed; no success claimed for unfinished work.");
                if (automaticRepair) EnsureAutomaticProgressWindow();
                else if (!Find.WindowStack.Windows.Any(w => w is Window_TranslationRepair)) Open();
                if (Phase == "ATC_Repair_Checking")
                {
                    work.Tick();
                    if (!work.Done) return;
                    Phase = "ATC_Repair_Applying";
                    applyDeadline = DateTime.UtcNow.AddMinutes(10);
                    AutoTranslatorScanner.SetRepairChangeSet(work.Changes);
                    AutoTranslatorScanner.RequestRepairMemoryDrop(OnMemoryApplied, automaticPackageIds);
                }
                else if (Phase == "ATC_Repair_Applying" && DateTime.UtcNow > applyDeadline)
                    throw new TimeoutException("Memory injection did not finish within ten minutes.");
                else if (Phase == "ATC_Repair_Verifying")
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    int budget = 64;
                    while (verificationIndex < verification.Count && budget-- > 0 && TranslationFrameBudget.HasTime)
                    {
                        var record = verification[verificationIndex++];
                        try { AutoTranslatorScanner.VerifyRepairRecord(run, record); }
                        catch (Exception ex) { run.Verify(record, false, false); Log.Warning("[AutoTranslationCore] Repair verification: " + ex.Message); }
                    }
                    if (verificationIndex == verification.Count) Complete();
                }
                else if (Phase == "ATC_Repair_Saving")
                {
                    if (!reporting.IsCompleted) return;
                    reporting.GetAwaiter().GetResult();
                    reporting = null;
                    FinishCompletion();
                }
            }
            catch (Exception ex) { Fail(ex); }
        }

        private static void OnMemoryApplied(bool success)
        {
            if (!running || Phase != "ATC_Repair_Applying") return;
            if (!success) { Fail(new InvalidOperationException("Memory injection failed; unfinished entries remain unresolved.")); return; }
            verification = run.Snapshot();
            Phase = "ATC_Repair_Verifying";
        }

        private static void Complete()
        {
            run.FailWaiting();
            NativeErrorsAfter = CountNativeErrors(language);
            Phase = "ATC_Repair_Saving";
            var write = CreateResultWriter(); // Capture translated UI strings on the main thread.
            var writeCompletion = AutoTranslatorScanner.CaptureScopedRepairCompletionWriter(repairFileScope);
            var journal = AutoTranslatorScanner.RepairJournal;
            reporting = Task.Run(() => { writeCompletion(); journal.Checkpoint(); write(); });
        }

        private static void FinishCompletion()
        {
            CloseAutomaticProgressWindow();
            Acknowledge();
            if (run != null && run.Unresolved == 0 && run.Waiting == 0)
                AutoTranslatorMod.Settings.StartupRepairSignature =
                    AutoTranslatorScanner.LastVerifiedMemorySignature ?? AutoTranslatorMod.Settings.StartupRepairSignature;
            LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
            if (run != null && run.Waiting == 0) AutoTranslatorScanner.AcknowledgeCommittedChanges(work?.Changes);
            running = false; Finished = true;
            lock (gate)
            {
                var remaining = live.Snapshot().Where(r => run.Outcome(r) != "verified").ToList();
                live = new TranslationRepairRun();
                foreach (var record in remaining) live.Observe(record);
                observationRevision++;
            }
            Phase = "ATC_Repair_Completed";
            if (automaticRepair)
            {
                automaticResultShown = true;
                if (StartupTranslationGate.IsBlocking) pendingStartupResult = true;
                else Open(true);
            }
        }

        internal static void AbortStartupLoading(Exception error)
        {
            startupLookup = false; startupLookupId++;
            waiting = false;
            pendingStartupResult = false;
            Fail(error);
            startupRepair = false;
            startupRepairAcknowledged = true;
        }

        private static void Fail(Exception ex)
        {
            Failure = ex.GetBaseException().Message;
            run?.FailWaiting();
            running = false; Finished = true; Phase = "ATC_Repair_Stopped";
            CloseAutomaticProgressWindow();
            AutoTranslatorScanner.CancelRepairMemoryDrop();
            work?.Cancel();
            Log.Warning("[AutoTranslationCore] Main-menu translation repair: " + Failure);
            // A failed run still preserves its report without blocking the menu.
            // Do not wait for abandoned IO. It owns only its staging and cannot commit after Cancel.
            var write = CreateResultWriter();
            reporting = null;
            Task.Run(() => { try { write(); } catch (Exception error) { Log.Warning("[ATC] Repair report: " + error.Message); } });
        }

        private static Action CreateResultWriter()
        {
            int writerRevision = System.Threading.Interlocked.Increment(ref resultWriterRevision);
            var result = run;
            if (result == null) return () => { };
            string root = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "SelfRepair");
            string version = AutoTranslatorScanner.CurrentRimWorldVersion, targetName = target.ToString(), failure = Failure;
            string mode = ModeLabel == "ATC_Repair_ModeFull" ? "full" : "incremental";
            string[] scopedFiles = repairFileScope?.ToArray(), scopedPackages = automaticPackageIds;
            int? nativeBefore = NativeErrorsBefore, nativeAfter = NativeErrorsAfter;
            var lines = new List<string> { "# " + "ATC_Repair_IntroTitle".Translate(), "",
                "ATC_Repair_VerifiedCount".Translate(result.Verified), "ATC_Repair_InapplicableCount".Translate(result.Inapplicable),
                "ATC_Repair_UnresolvedCount".Translate(result.Unresolved), "", "ATC_Repair_ResultHint".Translate() };
            if (nativeBefore.HasValue && nativeAfter.HasValue) lines.Insert(5, "ATC_Repair_NativeErrorCounts".Translate(nativeBefore.Value, nativeAfter.Value));
            return () => {
                var snapshot = result.Snapshot();
                using (var transaction = new TranslationFileTransaction(Path.Combine(root, "reports")).CaptureWrites())
                {
                TranslationRepairJournal.AtomicText(Path.Combine(root, "last-run.json"),
                    Newtonsoft.Json.JsonConvert.SerializeObject(new { completedUtc = DateTime.UtcNow, gameVersion = version,
                        targetLanguage = targetName, mode, scopedFiles, scopedPackages, verified = result.Verified, inapplicable = result.Inapplicable,
                        unresolved = result.Unresolved, waiting = result.Waiting, failure,
                        nativeErrorsBefore = nativeBefore, nativeErrorsAfter = nativeAfter,
                        issues = snapshot.Select(r => new { outcome = result.Outcome(r), record = r }) }, Newtonsoft.Json.Formatting.Indented));
                if (failure != null) lines.Add("\n" + failure);
                foreach (var record in snapshot) lines.Add("\n- " + result.Outcome(record) + ": " + record.OriginalKey + " → " + record.NewKey +
                    "\n  " + record.SourceFile + "\n  " + record.Reason + ": " + record.Evidence);
                TranslationRepairJournal.AtomicText(Path.Combine(root, "本次修復結果.md"), string.Join("\n", lines));
                transaction.Commit(() => writerRevision == System.Threading.Volatile.Read(ref resultWriterRevision));
                }
            };
        }

        // Same scope as LoadedLanguage.InjectIntoData_AfterImpliedDefs: the
        // current language-level errors plus the latest package-level errors.
        internal static int CountNativeErrors(LoadedLanguage active)
            => (active?.loadErrors?.Count ?? 0) + (active?.defInjections?.Sum(p => p?.loadErrors?.Count ?? 0) ?? 0);

        private static void PollNotice()
        {
            // A completed automatic run explicitly presents its result once.
            // Diagnostic observations must not reopen it after the player closes.
            if (automaticResultShown || startupRepairAcknowledged) return;
            if (DateTime.UtcNow < nextNoticeCheck) return;
            nextNoticeCheck = DateTime.UtcNow.AddSeconds(2);
            if (AutoTranslatorMod.Settings == null || Current.ProgramState != ProgramState.Entry || !AutoTranslatorScanner.StructureReady ||
                LongEventHandler.AnyEventNowOrWaiting || Find.WindowStack == null)
            {
                NoticeState("waiting for game loading to finish");
                readyAfter = DateTime.UtcNow.AddSeconds(2); return;
            }
            if (Find.WindowStack.Windows.Any(w => w is Window_TranslationRepair)) return;
            var blocker = Find.WindowStack.Windows.FirstOrDefault(w => w.absorbInputAroundWindow || w.forcePause);
            if (blocker != null)
            {
                NoticeState("waiting for window " + blocker.GetType().FullName);
                readyAfter = DateTime.UtcNow.AddSeconds(2); return;
            }
            if (DateTime.UtcNow < readyAfter) return;
            string signature = NoticeSignature();
            if (signature == deferredNoticeEnvironment) return;
            int revision = System.Threading.Volatile.Read(ref observationRevision);
            if (revision != checkedNoticeRevision || signature != checkedNoticeEnvironment)
            {
                var seen = new HashSet<string>(AutoTranslatorMod.Settings.LastRepairNotice == signature
                    ? AutoTranslatorMod.Settings.SeenRepairIssues ?? new List<string>() : new List<string>());
            var unseen = CurrentObservations().Where(r => r.Status != "相同譯文合併")
                    .Select(NoticeIssue).Where(id => !seen.Contains(id)).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
                noticeNeeded = signature != AutoTranslatorMod.Settings.LastRepairNotice || unseen.Count != 0;
                pendingNoticeSignature = signature + ":" + TranslationRepairJournal.Hash(string.Join("\n", unseen));
                checkedNoticeRevision = revision;
                checkedNoticeEnvironment = signature;
            }
            if (!noticeNeeded) { NoticeState("completed run acknowledged; no newly observed issues"); return; }
            if (pendingNoticeSignature == noticeSignature) return;
            // Adding a window is not proof that it was drawn. A lost/replaced
            // stack or a failed Add must remain eligible for another attempt.
            Open(true);
            NoticeState("window added; awaiting first render");
        }
    }
}
