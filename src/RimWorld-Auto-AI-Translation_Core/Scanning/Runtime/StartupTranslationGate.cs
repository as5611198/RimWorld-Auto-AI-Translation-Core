using System;
using System.Collections;
using System.Diagnostics;
using Verse;

namespace AutoTranslator_Core
{
    // A native enumerable long event keeps Entry hidden while yielding to the
    // game's loading renderer. Only this event's own main-thread slice may
    // bypass the ordinary long-event guard; other loaders still block ATC.
    internal static class StartupTranslationGate
    {
        private static int revision;
        private static bool blocking;
        [ThreadStatic] private static bool pumping;
        internal static bool IsBlocking => blocking;
        internal static bool IsPumping => pumping;
        internal static bool NativeLoadingBlocksWork => LongEventHandler.AnyEventNowOrWaiting && !pumping;

        internal static void Reset() { revision++; blocking = false; }

        internal static void Queue()
        {
            if (AutoTranslatorScanner.NativeStartupInjectionIsCurrent() && !AutoTranslatorScanner.NativeStartupHasDeferred)
            {
                Log.Message("[ATC] Native startup injection is current; post-load translation stage skipped.");
                return;
            }
            if (blocking) return;
            int request = ++revision;
            int epoch = AutoTranslatorScanner.GameDataEpoch;
            blocking = true;
            try
            {
                // DoPlayLoad's postfix dispatches this after Root has already
                // queued interface initialization. Deferred native injections
                // and static constructors finish before this event can run.
                LongEventHandler.QueueLongEvent(Run(request, epoch), "ATC_Repair_Applying", OnError, false);
                Log.Message("[ATC] Startup translation queued before main-menu display.");
            }
            catch { blocking = false; throw; }
        }

        private static IEnumerable Run(int request, int epoch)
        {
            var timer = Stopwatch.StartNew();
            var deadline = DateTime.UtcNow.AddMinutes(12);
            var language = LanguageDatabase.activeLanguage;
            var target = AutoTranslatorMod.Settings?.TargetLang;
            int cancellation = AutoTranslatorSettings.PipelineCancellationGeneration;
            int lastFrame = int.MinValue;
            bool started = false;
            try
            {
                while (request == revision && epoch == AutoTranslatorScanner.GameDataEpoch)
                {
                    // The native enumerator may call MoveNext repeatedly in a
                    // frame. Share one bounded slice instead of spinning pumps.
                    int frame = TranslationFrameBudget.CurrentFrame;
                    if (frame != lastFrame)
                    {
                        lastFrame = frame;
                        if (!Step(ref started, deadline, language, target, cancellation)) yield break;
                    }
                    yield return null;
                }
            }
            finally
            {
                if (request == revision)
                {
                    blocking = false;
                    Log.Message("[ATC] Startup translation loading stage released after " + timer.ElapsedMilliseconds +
                        " ms; repair=" + (TranslationRepairController.Failure ?? "finished") + ".");
                }
            }
        }

        private static bool Step(ref bool started, DateTime deadline, LoadedLanguage language,
            TargetLanguage? target, int cancellation)
        {
            pumping = true;
            try
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Startup translation exceeded twelve minutes.");
                if (AutoTranslatorMod.Settings == null || language == null || !target.HasValue ||
                    !ReferenceEquals(language, LanguageDatabase.activeLanguage) || target.Value != AutoTranslatorMod.Settings.TargetLang ||
                    cancellation != AutoTranslatorSettings.PipelineCancellationGeneration || AutoTranslatorSettings.IsCancellationRequested)
                    throw new OperationCanceledException("Startup translation environment changed.");
                if (Current.ProgramState != ProgramState.Entry)
                    throw new OperationCanceledException("Startup translation was superseded by game loading.");
                if (!AutoTranslatorScanner.StructureReady) return true;
                if (!started && AutoTranslatorScanner.TryCompleteNativeStartup()) return false;
                if (!started)
                {
                    started = true;
                    Log.Message("[ATC] Startup translation loading stage started after native initialization.");
                    if (!TranslationRepairController.RequestStartupRepairOrInject())
                        throw new InvalidOperationException("Startup translation settings are unavailable.");
                }
                ATC_Dispatcher.PumpPending();
                AutoTranslatorScanner.PumpMainThreadDispatcher();
                if (TranslationRepairController.Failure != null)
                {
                    AutoTranslatorScanner.CancelStartupMemoryDrop();
                    return false;
                }
                LongEventHandler.SetCurrentEventText((TranslationRepairController.IsRunning || TranslationRepairController.IsWaiting
                    ? TranslationRepairController.Phase : "ATC_Repair_Applying").Translate());
                bool busy = TranslationRepairController.StartupWorkActive || AutoTranslatorScanner.MemoryDropBusy ||
                    AutoTranslatorScanner.StaticTranslationRefreshBusy;
                if (!busy && !TranslationRepairController.Finished)
                    throw new InvalidOperationException("Startup translation stopped before completion.");
                return busy;
            }
            catch (Exception ex) { OnError(ex); return false; }
            finally { pumping = false; }
        }

        private static void OnError(Exception error)
        {
            TranslationRepairController.AbortStartupLoading(error);
            AutoTranslatorScanner.CancelStartupMemoryDrop();
            blocking = false; revision++;
            Log.Warning("[ATC] Startup translation loading stage stopped; continuing to menu: " + error.Message);
        }
    }
}
