using System.Collections;
using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static bool GateNoStaticRefresh(ref bool __result) { __result = false; return false; }
    static int gateRepairStarts;
    static void CountGateRepairStart() { gateRepairStarts++; }
    static int nativeFullReinjectionRequests;
    static void CountNativeFullReinjection() { nativeFullReinjectionRequests++; }
    static ManualResetEventSlim nativePreparationEntered, nativePreparationRelease;
    static void HoldNativePreparation()
    {
        if (!UnityData.IsInMainThread)
        {
            nativePreparationEntered.Set();
            if (!nativePreparationRelease.Wait(10000)) throw new TimeoutException("Native preparation fixture timed out");
        }
    }

    static void TestStartupTranslationGate()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        var gate = scanner.Assembly.GetType("AutoTranslator_Core.StartupTranslationGate");
        var controller = scanner.Assembly.GetType("AutoTranslator_Core.TranslationRepairController");
        var hooks = new Harmony("atc.startup-gate-integration");
        var previousLanguage = LanguageDatabase.activeLanguage;
        var previousSettings = AutoTranslatorMod.Settings;
        var previousState = Current.ProgramState;
        var previousPack = selectionPack;
        var previousWindows = repairWindowStack;
        var currentEvent = AccessTools.Field(typeof(LongEventHandler), "currentEvent");
        var previousEvent = currentEvent.GetValue(null);
        var queue = AccessTools.Field(typeof(LongEventHandler), "eventQueue").GetValue(null);
        var queueType = queue.GetType();
        var previousQueue = ((IEnumerable)queue).Cast<object>().ToArray();
        var clearQueue = queueType.GetMethod("Clear");
        var enqueue = queueType.GetMethod("Enqueue");
        var dequeue = queueType.GetMethod("Dequeue");
        bool Flag(Type type, string name) => (bool)type.GetProperty(name, flags).GetValue(null);
        void Call(Type type, string name) => type.GetMethod(name, flags).Invoke(null, null);
        void FreshRuntime()
        {
            Call(gate, "Reset");
            Call(scanner, "InvalidateGameData");
            Call(controller, "ClearEnvironment");
            AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Traditional };
            var language = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
            language.info = new LanguageInfo();
            language.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
            language.defInjections = new List<DefInjectionPackage>(); language.loadErrors = new List<string>();
            AccessTools.Field(typeof(LoadedLanguage), "dataIsLoaded").SetValue(language, true);
            LanguageDatabase.activeLanguage = language;
            repairWindowStack = new WindowStack();
            currentEvent.SetValue(null, null); clearQueue.Invoke(queue, null);
            gateRepairStarts = 0;
        }
        IEnumerator QueueGate()
        {
            Call(gate, "Queue");
            var item = dequeue.Invoke(queue, null);
            currentEvent.SetValue(null, item);
            return (IEnumerator)AccessTools.Field(item.GetType(), "eventActionEnumerator").GetValue(item);
        }
        void Finish(IEnumerator enumerator)
        {
            bool advancing = true;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (advancing && DateTime.UtcNow < deadline)
            {
                fixtureFrame++;
                advancing = enumerator.MoveNext();
                if (advancing) Thread.Sleep(1);
            }
            Check(!advancing && !Flag(gate, "IsBlocking"), "native loading stage releases after bounded repair/injection work completes");
            Check((string)controller.GetProperty("Failure", flags).GetValue(null) == null,
                "startup stage completes without a repair failure");
            Check(Flag(gate, "NativeLoadingBlocksWork"), "outside the startup slice the real current long event still blocks ordinary ATC work");
            currentEvent.SetValue(null, null);
        }
        try
        {
            hooks.Patch(AccessTools.PropertyGetter(typeof(Find), "WindowStack"), prefix: new HarmonyMethod(typeof(Suite), nameof(RepairWindowStack)));
            hooks.Patch(AccessTools.Method(typeof(WindowStack), "Add"), prefix: new HarmonyMethod(typeof(Suite), nameof(AddAutomaticRepairWindow)));
            hooks.Patch(AccessTools.Method(typeof(UIRoot_Entry), "UIRootUpdate"), prefix: new HarmonyMethod(typeof(Suite), nameof(SkipEntryEngineUpdate)));
            hooks.Patch(controller.GetMethod("BeginRepair", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(CountGateRepairStart)));
            // Headless hosts have no engine-owned static UI labels. Exercise real
            // Def/Keyed repair, async IO, queues, callbacks and native loading guards.
            hooks.Patch(scanner.GetMethod("PumpStaticCachedTranslationRefresh", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(SkipEntryEngineUpdate)));
            hooks.Patch(AccessTools.PropertyGetter(scanner, "StaticTranslationRefreshBusy"), prefix: new HarmonyMethod(typeof(Suite), nameof(GateNoStaticRefresh)));
            freezeFixtureFrame = true;
            Current.ProgramState = ProgramState.Entry;
            AutoTranslatorSettings.IsRunning = false;
            selectionPack = Path.Combine(temporary, "StartupGatePack");
            FreshRuntime();
            var def = new RepairFixtureDef { defName = "StartupGate", menuText = "English menu text" };
            DefDatabase<RepairFixtureDef>.Add(def);
            string root = Path.Combine(selectionPack, "Languages", "ChineseTraditional");
            string file = Path.Combine(root, "DefInjected", nameof(RepairFixtureDef), "fixture_AutoTranslated.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "<LanguageData><StartupGate.oldMenuText>主選單前已修復</StartupGate.oldMenuText>" +
                "<StartupMissing.label>停用內容仍保留</StartupMissing.label></LanguageData>");
            string keyed = Path.Combine(root, "Keyed", "fixture_AutoTranslated.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(keyed));
            File.WriteAllText(keyed, "<LanguageData><StartupGateKey>主選單前介面文字</StartupGateKey></LanguageData>");

            // Root queues interface initialization before DoPlayLoad returns.
            LongEventHandler.QueueLongEvent((Action)(() => { }), "InitializingInterface", false, null);
            Call(gate, "Queue"); Call(gate, "Queue");
            var items = ((IEnumerable)queue).Cast<object>().ToArray();
            Check(items.Length == 2 && AccessTools.Field(items[0].GetType(), "eventActionEnumerator").GetValue(items[0]) == null &&
                AccessTools.Field(items[1].GetType(), "eventActionEnumerator").GetValue(items[1]) is IEnumerator,
                "startup stage queues once behind native interface initialization");
            dequeue.Invoke(queue, null);
            var nativeItem = dequeue.Invoke(queue, null); currentEvent.SetValue(null, nativeItem);
            var run = (IEnumerator)AccessTools.Field(nativeItem.GetType(), "eventActionEnumerator").GetValue(nativeItem);
            Check(LongEventHandler.ShouldWaitForEvent && Flag(gate, "NativeLoadingBlocksWork"),
                "native enumerable long event hides the menu and blocks ordinary injection before its first slice");
            // A queued gate alone cannot authorize live traversal.
            scanner.GetField("_pendingMemoryDrop", flags).SetValue(null, true);
            Call(scanner, "PumpMainThreadDispatcher");
            Check((int)scanner.GetField("_memoryDropPrepareRunning", flags).GetValue(null) == 0,
                "a queued startup stage cannot bypass an unrelated/native loading boundary");
            scanner.GetField("_pendingMemoryDrop", flags).SetValue(null, false);
            Finish(run);
            Check(def.menuText == "主選單前已修復" && LanguageDatabase.activeLanguage.keyedReplacements["StartupGateKey"].value == "主選單前介面文字",
                "first activation repairs aliases and injects Def/Keyed text before the native loading stage releases");
            Check(File.Exists(Path.Combine(selectionPack, "SelfRepair", "baseline.json")) && Flag(controller, "Finished") && gateRepairStarts == 1,
                "startup stage waits for verification and the persisted repair completion receipt");
            Check(new TranslationRepairJournal(Path.Combine(selectionPack, "SelfRepair")).Snapshot().Any(r => r.Translation == "停用內容仍保留"),
                "inapplicable paid translations remain preserved without holding the loading screen indefinitely");
            Check(!repairWindowStack.Windows.Any(w => w is Window_TranslationRepair || w.GetType().Name == "Window_TranslationRepairProgress"),
                "startup repair does not create corner progress or result windows behind the loading screen");
            var ui = (UIRoot_Entry)Uninitialized(typeof(UIRoot_Entry)); ui.windows = repairWindowStack;
            ui.UIRootUpdate();
            Check(repairWindowStack.Windows.Count(w => w is Window_TranslationRepair) == 1,
                "completed startup repair presents one result after the actual main-menu update");
            ((List<Window>)AccessTools.Field(typeof(WindowStack), "windows").GetValue(repairWindowStack)).Clear();
            ui.UIRootUpdate(); ui.UIRootUpdate();
            Check(!repairWindowStack.Windows.Any(w => w is Window_TranslationRepair), "closed startup result stays closed");

            FreshRuntime(); def.menuText = "English menu text";
            byte[] unchanged = File.ReadAllBytes(file);
            Finish(QueueGate());
            Check(def.menuText == "主選單前已修復" && gateRepairStarts == 0 && File.ReadAllBytes(file).SequenceEqual(unchanged),
                "unchanged restart injects existing translations and preserves repaired XML bytes");
            Check(!repairWindowStack.Windows.Any() && !(bool)controller.GetField("pendingStartupResult", flags).GetValue(null),
                "unchanged startup does not queue another repair result");

            FreshRuntime(); def.menuText = "English menu text";
            def.optional = new RepairFixtureNode();
            File.WriteAllText(file, "<LanguageData><StartupGate.oldMenuText>增量提前注入</StartupGate.oldMenuText>" +
                "<StartupGate.stages.stable.label>原生列表譯文</StartupGate.stages.stable.label>" +
                "<StartupGate.optional.label>晚建欄位譯文</StartupGate.optional.label></LanguageData>");
            Finish(QueueGate());
            var scope = (HashSet<string>)controller.GetField("repairFileScope", flags).GetValue(null);
            Check(def.menuText == "增量提前注入" && gateRepairStarts == 1 && scope != null && scope.SetEquals(new[] { Path.GetFullPath(file) }),
                "changed startup repairs only the changed ATC file before releasing the menu");

            void PrepareNative()
            {
                LanguageDatabase.activeLanguage.InjectIntoData_BeforeImpliedDefs();
                var task = (Task)scanner.GetField("nativeStartupPreparation", flags).GetValue(null);
                Check(task != null && task.Wait(10000), "native preparation finishes with no game-thread pump or live Def validation");
            }
            void NativeEvent()
            {
                LongEventHandler.QueueLongEvent((Action)(() => { }), "Initializing", false, null);
                currentEvent.SetValue(null, dequeue.Invoke(queue, null));
            }
            FreshRuntime(); def.menuText = "English menu text";
            scanner.GetField("_pendingStaticCachedTranslationRefresh", flags).SetValue(null, false);
            // During the early pass Loaded is still false. Preparation must use
            // immutable XML and receipts, never wait on the live validation queue.
            typeof(PlayDataLoader).GetField("loadedInt", flags).SetValue(null, false);
            PrepareNative();
            typeof(PlayDataLoader).GetField("loadedInt", flags).SetValue(null, true);
            scanner.GetField("_pendingKeyedMemoryDrop", flags).SetValue(null, true);
            var prequeuedNativeGate = QueueGate(); // Loaded polling can queue this before deferred native callbacks run.
            NativeEvent();
            LanguageDatabase.activeLanguage.InjectIntoData_AfterImpliedDefs();
            string staticConstructorConsumer = def.menuText;
            Check(staticConstructorConsumer == "增量提前注入" && LanguageDatabase.activeLanguage.keyedReplacements["StartupGateKey"].value == "主選單前介面文字",
                "real final native pass publishes verified Def/Keyed text before a subsequent static constructor can read it");
            bool NativeApplied() => (bool)scanner.GetMethod("NativeStartupInjectionIsCurrent", flags).Invoke(null, null);
            Check(NativeApplied() && !Flag(scanner, "MemoryDropBusy") && gateRepairStarts == 0,
                "native completion does not start repair or leave memory work pending");
            Check((bool)scanner.GetField("_pendingStaticCachedTranslationRefresh", flags).GetValue(null),
                "native injection retains refresh for UI caches created before the final native pass without reopening the loading stage");
            def.menuText = "原生之後的消費端修改";
            nativeFullReinjectionRequests = 0;
            hooks.Patch(scanner.GetMethod("RequestMemoryDrop"), prefix: new HarmonyMethod(typeof(Suite), nameof(CountNativeFullReinjection)));
            try { Finish(prequeuedNativeGate); }
            finally { hooks.Unpatch(scanner.GetMethod("RequestMemoryDrop"), typeof(Suite).GetMethod(nameof(CountNativeFullReinjection), flags)); }
            Check(nativeFullReinjectionRequests == 0, "executing a prequeued gate after native completion does not request a second full payload");
            Check(def.menuText == "原生之後的消費端修改" && gateRepairStarts == 0,
                "a gate queued before native completion cannot reapply the full payload or overwrite later consumer edits");
            NativeEvent();
            Call(gate, "Queue");
            Check(!Flag(gate, "IsBlocking") && !((IEnumerable)queue).Cast<object>().Any(),
                "a current native injection skips the entire post-load enumerable stage");
            Check(Flag(gate, "NativeLoadingBlocksWork"), "native startup application does not weaken the ordinary LongEvent guard");
            def.menuText = "consumer edit";
            LanguageDatabase.activeLanguage.InjectIntoData_AfterImpliedDefs();
            Check(def.menuText == "consumer edit", "repeated native final callbacks do not publish ATC translations twice");
            currentEvent.SetValue(null, null);
            scanner.GetMethod("RestoreRuntimeTranslationsAfterPackReset").Invoke(null, null);
            var restoreDeadline = DateTime.UtcNow.AddSeconds(10);
            while (Flag(scanner, "MemoryDropBusy") && DateTime.UtcNow < restoreDeadline)
            { fixtureFrame++; Call(scanner, "PumpMainThreadDispatcher"); Thread.Sleep(1); }
            Check(!Flag(scanner, "MemoryDropBusy") && def.menuText == "English menu text" &&
                !LanguageDatabase.activeLanguage.keyedReplacements.ContainsKey("StartupGateKey"),
                "pack reset restores the original runtime baselines captured during native startup injection");

            FreshRuntime(); def.menuText = "English menu text";
            PrepareNative();
            def.stages.Clear();
            def.optional = null;
            NativeEvent(); LanguageDatabase.activeLanguage.InjectIntoData_AfterImpliedDefs();
            Check(NativeApplied() && def.menuText == "增量提前注入" && def.stages.Count == 0 &&
                File.ReadAllText(file).Contains("原生列表譯文"),
                "native restart rechecks changed live Def structure, preserves rejected list text and still publishes valid translations");
            def.stages.Add(new RepairFixtureNode());
            def.optional = new RepairFixtureNode();
            def.menuText = "只允許補處理清單";
            Finish(QueueGate());
            Check(def.stages[0].label == "原生列表譯文" && def.optional.label == "晚建欄位譯文" && def.menuText == "只允許補處理清單" && gateRepairStarts == 0,
                "post-constructor catch-up retries only the previously unavailable native candidate, leaving successful translations untouched");

            FreshRuntime(); def.menuText = "English menu text";
            PrepareNative(); NativeEvent(); LanguageDatabase.activeLanguage.InjectIntoData_AfterImpliedDefs();
            Check(NativeApplied(), "fixture completes native startup before simulating a later automatic translation output");
            File.WriteAllText(file, File.ReadAllText(file).Replace("增量提前注入", "自動更新的新譯文"));
            Finish(QueueGate());
            Check(def.menuText == "自動更新的新譯文" && gateRepairStarts == 1,
                "new output after native completion invalidates the skip receipt and still follows normal incremental repair");

            FreshRuntime(); def.menuText = "English menu text";
            PrepareNative();
            var timestamp = File.GetLastWriteTimeUtc(file);
            byte[] beforeEdit = File.ReadAllBytes(file);
            byte[] afterEdit = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(beforeEdit).Replace("自動更新的新譯文", "等長變更新譯文喔"));
            File.WriteAllBytes(file, afterEdit); File.SetLastWriteTimeUtc(file, timestamp);
            Check(beforeEdit.Length == afterEdit.Length && File.GetLastWriteTimeUtc(file) == timestamp,
                "fixture changes translation contents while preserving file length and timestamp");
            NativeEvent(); LanguageDatabase.activeLanguage.InjectIntoData_AfterImpliedDefs();
            Check(!NativeApplied() && def.menuText == "English menu text", "files changed after preparation are withheld from native startup publication");
            Finish(QueueGate());
            Check(def.menuText == "等長變更新譯文喔" && gateRepairStarts == 1,
                "changed native preparation falls back to the existing scoped repair and injection pipeline");

            FreshRuntime(); def.menuText = "English menu text";
            PrepareNative(); AutoTranslatorMod.Settings.TargetLang = TargetLanguage.Simplified;
            NativeEvent(); LanguageDatabase.activeLanguage.InjectIntoData_AfterImpliedDefs();
            Check(!NativeApplied() && def.menuText == "English menu text", "a target-language change revokes prepared native startup translations");

            FreshRuntime(); def.menuText = "English menu text";
            nativePreparationEntered = new ManualResetEventSlim(); nativePreparationRelease = new ManualResetEventSlim();
            hooks.Patch(scanner.GetMethod("HasCompletedStartupRepair", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(HoldNativePreparation)));
            try
            {
                LanguageDatabase.activeLanguage.InjectIntoData_BeforeImpliedDefs();
                Check(nativePreparationEntered.Wait(5000), "fixture pauses background preparation before the native final pass");
                var delayedPreparation = (Task)scanner.GetField("nativeStartupPreparation", flags).GetValue(null);
                NativeEvent();
                var noWait = System.Diagnostics.Stopwatch.StartNew();
                LanguageDatabase.activeLanguage.InjectIntoData_AfterImpliedDefs();
                Check(noWait.ElapsedMilliseconds < 1000 && !NativeApplied() && def.menuText == "English menu text",
                    "an unfinished preparation cannot block the native loading thread");
                nativePreparationRelease.Set();
                delayedPreparation.Wait(10000);
                LanguageDatabase.activeLanguage.InjectIntoData_AfterImpliedDefs();
                Check(!NativeApplied() && def.menuText == "English menu text", "late preparation cannot publish after the native startup opportunity was deferred");
            }
            finally
            {
                nativePreparationRelease.Set();
                hooks.Unpatch(scanner.GetMethod("HasCompletedStartupRepair", flags), typeof(Suite).GetMethod(nameof(HoldNativePreparation), flags));
            }

            FreshRuntime(); def.menuText = "English menu text";
            string baselinePath = Path.Combine(selectionPack, "SelfRepair", "baseline.json");
            byte[] baselineBytes = File.ReadAllBytes(baselinePath);
            try
            {
                File.WriteAllText(baselinePath, "{corrupt receipt");
                PrepareNative(); NativeEvent(); LanguageDatabase.activeLanguage.InjectIntoData_AfterImpliedDefs();
                Check(!NativeApplied() && def.menuText == "English menu text", "a corrupt baseline cannot authorize native injection even when cached XML exists");
                FreshRuntime();
                File.Delete(baselinePath);
                PrepareNative(); NativeEvent(); LanguageDatabase.activeLanguage.InjectIntoData_AfterImpliedDefs();
                Check(!NativeApplied() && gateRepairStarts == 0 && def.menuText == "English menu text",
                    "first activation without a baseline stays on the verified repair path");
            }
            finally { File.WriteAllBytes(baselinePath, baselineBytes); }

            FreshRuntime();
            var abandoned = QueueGate(); Call(gate, "Reset");
            Check(!abandoned.MoveNext() && !Flag(controller, "StartupWorkActive"), "reset revokes a queued startup iterator without starting repair");
            FreshRuntime();
            abandoned = QueueGate(); Call(scanner, "InvalidateGameData");
            Check(!abandoned.MoveNext() && !Flag(gate, "IsBlocking"), "a changed game-data epoch releases an obsolete loading stage");

            FreshRuntime();
            abandoned = QueueGate();
            scanner.GetField("_pendingMemoryDrop", flags).SetValue(null, true);
            scanner.GetField("_pendingKeyedMemoryDrop", flags).SetValue(null, true);
            object[] args = { true, DateTime.MinValue, LanguageDatabase.activeLanguage, (TargetLanguage?)TargetLanguage.Traditional,
                AutoTranslatorSettings.PipelineCancellationGeneration };
            bool keepLoading = (bool)gate.GetMethod("Step", flags).Invoke(null, args);
            Check(!keepLoading && !Flag(gate, "IsBlocking") && !Flag(scanner, "MemoryDropBusy") &&
                controller.GetProperty("Failure", flags).GetValue(null) != null,
                "timeout releases native loading, revokes pending payloads and reports failure instead of hanging");
            FreshRuntime();
            abandoned = QueueGate();
            Current.ProgramState = ProgramState.Playing;
            Check(!abandoned.MoveNext() && !Flag(gate, "IsBlocking") && gateRepairStarts == 0,
                "an autoloaded game bypasses the menu-only stage without a loading deadlock");
            Current.ProgramState = ProgramState.Entry;
        }
        finally
        {
            Call(gate, "Reset"); Call(scanner, "InvalidateGameData"); Call(controller, "ClearEnvironment");
            currentEvent.SetValue(null, previousEvent); clearQueue.Invoke(queue, null);
            foreach (var item in previousQueue) enqueue.Invoke(queue, new[] { item });
            freezeFixtureFrame = false;
            hooks.UnpatchAll(hooks.Id);
            selectionPack = previousPack; AutoTranslatorMod.Settings = previousSettings;
            LanguageDatabase.activeLanguage = previousLanguage; Current.ProgramState = previousState;
            repairWindowStack = previousWindows;
        }
    }
}
