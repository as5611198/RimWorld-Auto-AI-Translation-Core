using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static readonly ManualResetEventSlim retryReadReached = new ManualResetEventSlim();
    static readonly ManualResetEventSlim retryReadContinue = new ManualResetEventSlim();
    static int pauseRetryRead;
    static int pauseRetryFileRead;
    static string retryKeyedRoot;
    static readonly List<string> retryBuildScopes = new List<string>();
    static void CaptureRetryBuild(bool keyedOnly, List<string> packageIds)
    {
        lock (retryBuildScopes) retryBuildScopes.Add(keyedOnly ? "keyed" : packageIds == null ? "full" : string.Join(",", packageIds));
    }
    static void PauseRetryAfterKeyedRead(string path)
    {
        if (path != retryKeyedRoot || Interlocked.Exchange(ref pauseRetryRead, 0) == 0) return;
        retryReadReached.Set();
        if (!retryReadContinue.Wait(10000)) throw new TimeoutException("Test did not release XML read barrier");
    }
    static void PauseRetryAfterFileRead(string filePath)
    {
        if (Path.GetDirectoryName(filePath) != retryKeyedRoot || !Path.GetFileName(filePath).StartsWith("kiiro_") ||
            Interlocked.Exchange(ref pauseRetryFileRead, 0) == 0) return;
        retryReadReached.Set();
        if (!retryReadContinue.Wait(10000)) throw new TimeoutException("Test did not release per-file read barrier");
    }

    static void TestMemoryPreparationRetry()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var scanner = typeof(AutoTranslatorScanner);
        var controller = scanner.Assembly.GetType("AutoTranslator_Core.TranslationRepairController");
        var hooks = new Harmony("atc.memory-preparation-retry");
        var oldSettings = AutoTranslatorMod.Settings;
        var oldLanguage = LanguageDatabase.activeLanguage;
        var oldWindows = repairWindowStack;
        var oldState = Current.ProgramState;
        string oldPack = selectionPack;
        bool oldRunning = AutoTranslatorSettings.IsRunning;
        bool oldCancelled = AutoTranslatorSettings.IsCancellationRequested;
        var queue = (System.Collections.Concurrent.ConcurrentQueue<Action>)typeof(ATC_Dispatcher).GetField("executionQueue", flags).GetValue(null);
        bool Preparing() => (int)scanner.GetField("_memoryDropPrepareRunning", flags).GetValue(null) != 0;
        bool Busy() => (bool)scanner.GetProperty("MemoryDropBusy", flags).GetValue(null);
        void Dispatch() { for (int i = 0; i < 64 && queue.TryDequeue(out var a); i++) a(); }
        void Pump() { Dispatch(); scanner.GetMethod("PumpMainThreadDispatcher", flags).Invoke(null, null); }
        void Arm()
        {
            retryReadReached.Reset(); retryReadContinue.Reset();
            Interlocked.Exchange(ref pauseRetryRead, 1);
        }
        void Release()
        {
            retryReadContinue.Set();
            Check(SpinWait.SpinUntil(() => !Preparing(), 10000), "changed-file preparation exits without leaving a worker stuck");
        }
        void AdvanceRetryClock()
        {
            var retryAt = scanner.GetField("memoryPreparationRetryAfter", flags);
            Check(retryAt != null && (DateTime)retryAt.GetValue(null) > DateTime.UtcNow,
                "translation churn schedules a delayed retry instead of a tight loop");
            retryAt.SetValue(null, DateTime.MinValue);
        }
        try
        {
            hooks.Patch(AccessTools.PropertyGetter(typeof(Find), "WindowStack"), prefix: new HarmonyMethod(typeof(Suite), nameof(RepairWindowStack)));
            hooks.Patch(AccessTools.PropertyGetter(typeof(LongEventHandler), "AnyEventNowOrWaiting"), prefix: new HarmonyMethod(typeof(Suite), nameof(NoLongEvent)));
            hooks.Patch(scanner.GetMethod("PumpStaticCachedTranslationRefresh", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(SkipEntryEngineUpdate)));
            hooks.Patch(controller.GetMethod("BeginRepair", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(CaptureQueuedRepairStart)));
            hooks.Patch(scanner.GetMethods(flags).Single(m => m.Name == "BuildMemoryDropPayload" && m.GetParameters().Length == 3),
                prefix: new HarmonyMethod(typeof(Suite), nameof(CaptureRetryBuild)));
            hooks.Patch(scanner.GetMethod("LoadXmlFilesToDictForMemoryDrop", flags), postfix: new HarmonyMethod(typeof(Suite), nameof(PauseRetryAfterKeyedRead)));
            hooks.Patch(AccessTools.Method(scanner, "LoadXmlFileToDict"), postfix: new HarmonyMethod(typeof(Suite), nameof(PauseRetryAfterFileRead)));
            Current.ProgramState = ProgramState.Entry;
            AutoTranslatorSettings.IsRunning = false;
            AutoTranslatorSettings.IsCancellationRequested = false;
            foreach (string mode in new[] { "full", "repair-full", "package", "read-race", "file-lock", "cancel", "cancel-inflight" })
            {
                scanner.GetMethod("InvalidateGameData", flags).Invoke(null, null);
                controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
                controller.GetField("startupRepairAcknowledged", flags).SetValue(null, true);
                AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Traditional };
                selectionPack = Path.Combine(temporary, "InjectionRetry-" + mode);
                retryKeyedRoot = Path.Combine(selectionPack, "Languages", "ChineseTraditional", "Keyed");
                Directory.CreateDirectory(retryKeyedRoot);
                string changing = Path.Combine(retryKeyedRoot, "kiiro_expansion_AutoTranslated.xml");
                File.WriteAllText(changing, "<LanguageData><RetryChanged>舊內容</RetryChanged></LanguageData>");
                File.WriteAllText(Path.Combine(retryKeyedRoot, "unrelated_AutoTranslated.xml"),
                    "<LanguageData><RetryUnrelated>其他模組完整注入</RetryUnrelated></LanguageData>");
                var def = new RepairFixtureDef { defName = "Retry_" + mode.Replace('-', '_') };
                DefDatabase<RepairFixtureDef>.Add(def);
                string defs = Path.Combine(selectionPack, "Languages", "ChineseTraditional", "DefInjected", nameof(RepairFixtureDef));
                Directory.CreateDirectory(defs);
                File.WriteAllText(Path.Combine(defs, "unrelated_AutoTranslated.xml"),
                    "<LanguageData><" + def.defName + ".menuText>其他模組的 Def</" + def.defName + ".menuText></LanguageData>");
                var language = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
                language.info = new LanguageInfo(); language.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
                language.defInjections = new List<DefInjectionPackage>(); language.loadErrors = new List<string>();
                AccessTools.Field(typeof(LoadedLanguage), "dataIsLoaded").SetValue(language, true);
                LanguageDatabase.activeLanguage = language; repairWindowStack = new WindowStack();
                retryBuildScopes.Clear(); queuedRepairStarts = 0;
                int callbacks = 0; bool callbackSuccess = false;
                Arm();
                if (mode == "read-race") { Interlocked.Exchange(ref pauseRetryRead, 0); Interlocked.Exchange(ref pauseRetryFileRead, 1); }
                if (mode == "repair-full") scanner.GetMethod("RequestRepairMemoryDrop", flags).Invoke(null,
                    new object[] { (Action<bool>)(success => { callbacks++; callbackSuccess = success; }), null });
                else if (mode == "package") AutoTranslatorScanner.RequestMemoryDropForPackage("kiiro.expansion");
                else AutoTranslatorScanner.RequestMemoryDrop();
                Check(retryReadReached.Wait(10000), "real background preparation reaches the XML-read barrier: " + mode);
                File.WriteAllText(changing, "<LanguageData><RetryChanged>更新後第一版內容</RetryChanged></LanguageData>");
                if (mode == "cancel-inflight")
                {
                    scanner.GetMethod("InvalidateGameData", flags).Invoke(null, null);
                    Release(); Pump();
                    Check(!Busy() && retryBuildScopes.Count == 1 && language.keyedReplacements.Count == 0,
                        "an invalidated in-flight worker cannot publish, requeue, or apply stale translation data");
                    continue;
                }
                if (mode == "file-lock")
                {
                    using (var writeLock = new FileStream(changing, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Release();
                }
                else Release();
                Check(Busy() && scanner.GetField("_pendingMemoryDropPayload", flags).GetValue(null) == null,
                    "a changed snapshot retains its original pending injection and publishes no mixed payload: " + mode);
                Check(callbacks == 0, "a retryable preparation never fails the repair callback early: " + mode);
                if (mode == "cancel")
                {
                    scanner.GetMethod("InvalidateGameData", flags).Invoke(null, null);
                    Pump();
                    Check(!Busy() && retryBuildScopes.Count == 1, "environment invalidation revokes delayed retries and late dispatcher callbacks");
                    continue;
                }
                AutoTranslatorScanner.RequestMemoryDropForPackage("kiiro.expansion");
                Pump();
                Check(retryBuildScopes.Count == 1, "a later package request cannot bypass the retry delay or replace full scope: " + mode);
                AdvanceRetryClock(); Arm(); Pump();
                Check(retryReadReached.Wait(10000), "the main-thread pump automatically starts the retained request: " + mode);
                File.WriteAllText(changing, "<LanguageData><RetryChanged>更新後最終版內容更長</RetryChanged></LanguageData>");
                // A newer revision arrives while this second attempt is reading.
                AutoTranslatorScanner.RequestMemoryDropForPackage("kiiro.expansion");
                Release();
                Check(Busy() && callbacks == 0, "repeated concurrent changes preserve injection and its completion callback: " + mode);
                AdvanceRetryClock();
                var deadline = DateTime.UtcNow.AddSeconds(20);
                do { Pump(); Thread.Sleep(1); } while (Busy() && DateTime.UtcNow < deadline);
                Check(!Busy() && language.keyedReplacements.TryGetValue("RetryChanged", out var value) && value.value == "更新後最終版內容更長",
                    "once writes stop the retained request really injects the latest text: " + mode);
                string expectedScope = mode == "package" ? "kiiro.expansion" : "full";
                Check(retryBuildScopes.Count == 3 && retryBuildScopes.All(s => s == expectedScope),
                    "all retries retain the original full/package scope: " + mode);
                Check(mode == "package" ? !language.keyedReplacements.ContainsKey("RetryUnrelated") && def.menuText == "English menu text"
                    : language.keyedReplacements.ContainsKey("RetryUnrelated") && def.menuText == "其他模組的 Def",
                    "full recovery also injects unrelated Keyed and Def translations; package retry stays scoped: " + mode);
                Check(mode == "repair-full" ? callbacks == 1 && callbackSuccess : callbacks == 0,
                    "completion is delivered exactly once after the requested injection succeeds: " + mode);
                Check(queuedRepairStarts == 0 && !File.Exists(Path.Combine(selectionPack, "SelfRepair", "baseline.json")),
                    "injection retries never start full repair or manufacture a repair baseline: " + mode);
            }
        }
        finally
        {
            Interlocked.Exchange(ref pauseRetryRead, 0); Interlocked.Exchange(ref pauseRetryFileRead, 0); retryReadContinue.Set();
            SpinWait.SpinUntil(() => !Preparing(), 10000);
            scanner.GetMethod("InvalidateGameData", flags).Invoke(null, null);
            controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
            hooks.UnpatchAll(hooks.Id);
            AutoTranslatorMod.Settings = oldSettings; LanguageDatabase.activeLanguage = oldLanguage;
            repairWindowStack = oldWindows; Current.ProgramState = oldState; selectionPack = oldPack;
            AutoTranslatorSettings.IsRunning = oldRunning; AutoTranslatorSettings.IsCancellationRequested = oldCancelled;
        }
    }
}
