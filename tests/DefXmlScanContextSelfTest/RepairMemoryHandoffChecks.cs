using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static bool AddAutomaticRepairWindow(Window window)
    {
        if (!window.GetType().Name.StartsWith("Window_TranslationRepair", StringComparison.Ordinal)) return true;
        ((List<Window>)AccessTools.Field(typeof(WindowStack), "windows").GetValue(repairWindowStack)).Add(window);
        return false;
    }

    static void TestRepairMemoryHandoff()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var scanner = typeof(AutoTranslatorScanner);
        var controller = scanner.Assembly.GetType("AutoTranslator_Core.TranslationRepairController");
        var hooks = new Harmony("atc.repair-memory-handoff");
        var previousLanguage = LanguageDatabase.activeLanguage;
        var previousState = Current.ProgramState;
        bool previousBusy = AutoTranslatorSettings.IsRunning;
        var previousWindows = repairWindowStack;
        var previousSettings = AutoTranslatorMod.Settings;
        string previousPack = selectionPack;
        object Payload(string key, string value)
        {
            var payload = Activator.CreateInstance(scanner.GetNestedType("MemoryDropPayload", BindingFlags.NonPublic), true);
            payload.GetType().GetField("KeyedOnly").SetValue(payload, true);
            payload.GetType().GetField("Keyed").SetValue(payload, new Dictionary<string, string> { [key] = value });
            payload.GetType().GetField("TargetFolder").SetValue(payload, "ChineseTraditional");
            payload.GetType().GetField("KeyedPath").SetValue(payload, Path.Combine(selectionPack, "Languages", "ChineseTraditional", "Keyed"));
            return payload;
        }
        // Deliver actions on this test's main thread; the production exception
        // logger reads Unity Time.frameCount, unavailable in a headless legacy CLR.
        void Dispatch()
        {
            var queue = (System.Collections.Concurrent.ConcurrentQueue<Action>)typeof(ATC_Dispatcher).GetField("executionQueue", flags).GetValue(null);
            for (int i = 0; i < 64 && queue.TryDequeue(out var action); i++) action();
        }
        void Pump()
        {
            Dispatch();
            scanner.GetMethod("PumpMainThreadDispatcher", flags).Invoke(null, null);
        }
        bool Waiting() => (bool)controller.GetProperty("IsWaiting", flags).GetValue(null);
        bool Busy() => (bool)scanner.GetProperty("MemoryDropBusy", flags).GetValue(null);
        try
        {
            hooks.Patch(AccessTools.PropertyGetter(typeof(Find), "WindowStack"), prefix: new HarmonyMethod(typeof(Suite), nameof(RepairWindowStack)));
            hooks.Patch(AccessTools.PropertyGetter(typeof(LongEventHandler), "AnyEventNowOrWaiting"), prefix: new HarmonyMethod(typeof(Suite), nameof(NoLongEvent)));
            hooks.Patch(scanner.GetMethod("PumpStaticCachedTranslationRefresh", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(SkipEntryEngineUpdate)));
            hooks.Patch(controller.GetMethod("BeginRepair", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(CaptureQueuedRepairStart)));
            hooks.Patch(AccessTools.Method(typeof(UIRoot_Entry), "UIRootUpdate"), prefix: new HarmonyMethod(typeof(Suite), nameof(SkipEntryEngineUpdate)));
            Current.ProgramState = ProgramState.Entry;
            AutoTranslatorSettings.IsRunning = false;
            var language = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
            language.info = new LanguageInfo();
            language.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
            language.defInjections = new List<DefInjectionPackage>(); language.loadErrors = new List<string>();
            AccessTools.Field(typeof(LoadedLanguage), "dataIsLoaded").SetValue(language, true);
            LanguageDatabase.activeLanguage = language;
            foreach (bool nativeUpdates in new[] { true, false })
            {
                controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
                repairWindowStack = new WindowStack();
                ((List<Window>)AccessTools.Field(typeof(WindowStack), "windows").GetValue(repairWindowStack))
                    .Add((Window)Uninitialized(typeof(Window_TranslationRepair)));
                var root = (UIRoot_Entry)Uninitialized(typeof(UIRoot_Entry)); root.windows = repairWindowStack;
                root.UIRootUpdate(); // Establish the real, active menu before the player clicks.
                queuedRepairStarts = 0;
                scanner.GetMethod("BeginMemoryDropApply", flags).Invoke(null, new[] { Payload("HandoffFirst", "第一批") });
                scanner.GetField("_pendingMemoryDropPayload", flags).SetValue(null, Payload("HandoffNext", "後續批次"));
                Check((bool)controller.GetMethod("RequestStart", flags).Invoke(null, null) && Waiting(),
                    "repair queues while a real memory apply and a subsequent payload remain active (native updates=" + nativeUpdates + ")");
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (Busy() && DateTime.UtcNow < deadline)
                {
                    Pump(); if (nativeUpdates) root.UIRootUpdate(); Thread.Sleep(1);
                }
                Check(!Busy() && language.keyedReplacements["HandoffFirst"].value == "第一批" &&
                    language.keyedReplacements["HandoffNext"].value == "後續批次",
                    "the real dispatcher drains both batches and their background checkpoints before repair (native updates=" + nativeUpdates + ")");
                deadline = DateTime.UtcNow.AddSeconds(1);
                while (Waiting() && DateTime.UtcNow < deadline)
                {
                    Pump(); if (nativeUpdates) root.UIRootUpdate(); Thread.Sleep(1);
                }
                Check(!Waiting() && queuedRepairStarts == 1,
                    "memory completion starts the player's queued repair even when native menu updates stop arriving (native updates=" + nativeUpdates + ")");
                for (int i = 0; i < 5; i++) { Pump(); if (nativeUpdates) root.UIRootUpdate(); }
                Check(queuedRepairStarts == 1, "repeated memory and native updates cannot start the queued repair twice");
            }

            // Exercise the full controller, not just a captured start: while a
            // minimized repair receives no further native menu callbacks, its
            // real file repair, memory apply, verification and report must finish.
            hooks.Unpatch(controller.GetMethod("BeginRepair", flags), typeof(Suite).GetMethod(nameof(CaptureQueuedRepairStart), flags));
            selectionPack = Path.Combine(temporary, "HandoffCompletePack");
            AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Traditional };
            controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
            var repairedDef = new RepairFixtureDef { defName = "HandoffRepair" };
            DefDatabase<RepairFixtureDef>.Add(repairedDef);
            string file = Path.Combine(selectionPack, "Languages", "ChineseTraditional", "DefInjected", nameof(RepairFixtureDef), "handoff.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "<LanguageData><HandoffRepair.oldMenuText>背景銜接完成</HandoffRepair.oldMenuText></LanguageData>");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(file), "unresolved.xml"), "<LanguageData><broken>保留待確認內容");
            scanner.GetMethod("BeginMemoryDropApply", flags).Invoke(null, new[] { Payload("HandoffFinal", "最後一批") });
            Check((bool)controller.GetMethod("RequestStart", flags).Invoke(null, null) && Waiting(),
                "a complete real repair first queues behind the preceding memory apply");
            var finishDeadline = DateTime.UtcNow.AddSeconds(30);
            while (!(bool)controller.GetProperty("Finished", flags).GetValue(null) && DateTime.UtcNow < finishDeadline)
            {
                Pump(); Thread.Sleep(1);
            }
            Check((bool)controller.GetProperty("Finished", flags).GetValue(null) && controller.GetProperty("Failure", flags).GetValue(null) == null,
                "the memory dispatcher alone advances repair through checking, applying, verification and saved completion");
            var results = (TranslationRepairRun)controller.GetProperty("Results", flags).GetValue(null);
            Check(repairedDef.menuText == "背景銜接完成" && results.Verified == 1 && results.Waiting == 0,
                "dispatcher completion verifies the actual live repaired value without another menu callback or click");
            var report = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(selectionPack, "SelfRepair", "last-run.json")));
            Check((int)report["verified"] == 1 && (int)report["waiting"] == 0 && !string.IsNullOrEmpty(AutoTranslatorMod.Settings.LastRepairNotice),
                "dispatcher completion persists the verified result and acknowledges the completed repair");
            Check(results.Unresolved > 0, "completed repair fixture retains unresolved content without claiming everything is fixed");
            TestCompletedRepairRestart(scanner, controller, file);

            // Simulate a batch update while the startup repair still owns the
            // controller. Only the produced packages may be repaired/applied.
            hooks.Patch(AccessTools.Method(typeof(WindowStack), "Add"), prefix: new HarmonyMethod(typeof(Suite), nameof(AddAutomaticRepairWindow)));
            controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
            var first = new RepairFixtureDef { defName = "DeltaFirst" };
            var second = new RepairFixtureDef { defName = "DeltaSecond" };
            var untouched = new RepairFixtureDef { defName = "DeltaUntouched" };
            DefDatabase<RepairFixtureDef>.Add(first); DefDatabase<RepairFixtureDef>.Add(second); DefDatabase<RepairFixtureDef>.Add(untouched);
            string deltaRoot = Path.GetDirectoryName(file);
            string firstFile = Path.Combine(deltaRoot, "delta_first_AutoTranslated.xml");
            string secondFile = Path.Combine(deltaRoot, "delta_second_AutoTranslated.xml");
            string untouchedFile = Path.Combine(deltaRoot, "delta_other_AutoTranslated.xml");
            File.WriteAllText(firstFile, "<LanguageData><DeltaFirst.oldMenuText>增量一</DeltaFirst.oldMenuText></LanguageData>");
            File.WriteAllText(secondFile, "<LanguageData><DeltaSecond.oldMenuText>增量二</DeltaSecond.oldMenuText></LanguageData>");
            string untouchedXml = "<LanguageData><DeltaUntouched.oldMenuText>不可重掃</DeltaUntouched.oldMenuText></LanguageData>";
            File.WriteAllText(untouchedFile, untouchedXml);
            controller.GetField("running", flags).SetValue(null, true);
            var deltaRequest = controller.GetMethod("RequestPipelineRepairs", flags);
            Check(deltaRequest != null, "batch translation preserves produced package identities for incremental repair");
            if (deltaRequest == null) return;
            deltaRequest.Invoke(null, new object[] { new[] { "delta.first", "DELTA.FIRST", "delta.second" } });
            Dispatch();
            Check(!Waiting(), "new pipeline output queues behind an active repair without replacing it");
            controller.GetField("running", flags).SetValue(null, false);
            controller.GetProperty("Finished", flags).SetValue(null, false);
            var startDeadline = DateTime.UtcNow.AddSeconds(2);
            do { Pump(); Dispatch(); Thread.Sleep(1); }
            while (!(bool)controller.GetProperty("IsRunning", flags).GetValue(null) && DateTime.UtcNow < startDeadline);
            Check(!repairWindowStack.Windows.Any(w => w is Window_TranslationRepair) &&
                repairWindowStack.Windows.Count(w => w.GetType().Name == "Window_TranslationRepairProgress") == 1,
                "automatic work replaces the old result/minimized window with exactly one corner progress window");
            var activeDelta = controller.GetProperty("Results", flags).GetValue(null);
            controller.GetMethod("TickWaiting", flags).Invoke(null, null);
            Check(ReferenceEquals(activeDelta, controller.GetProperty("Results", flags).GetValue(null)),
                "a late waiting callback cannot start the already-consumed repair again");
            finishDeadline = DateTime.UtcNow.AddSeconds(30);
            do { Pump(); Thread.Sleep(1); }
            while (!(bool)controller.GetProperty("Finished", flags).GetValue(null) && DateTime.UtcNow < finishDeadline);
            Check((bool)controller.GetProperty("Finished", flags).GetValue(null) && controller.GetProperty("Failure", flags).GetValue(null) == null,
                "queued incremental repair completes through its scoped memory callback");
            Check(first.menuText == "增量一" && second.menuText == "增量二" && untouched.menuText != "不可重掃",
                "multi-package incremental repair applies only output from the changed mods");
            Check(File.ReadAllText(untouchedFile) == untouchedXml && (int)controller.GetProperty("Total", flags).GetValue(null) == 2,
                "incremental repair scans two changed package files and leaves unrelated XML byte-identical");
            Check(repairWindowStack.Windows.Count(w => w is Window_TranslationRepair) == 1 &&
                !repairWindowStack.Windows.Any(w => w.GetType().Name == "Window_TranslationRepairProgress"),
                "automatic completion leaves one result window and removes its corner progress surface");
            var completedDelta = controller.GetProperty("Results", flags).GetValue(null);
            ((List<Window>)AccessTools.Field(typeof(WindowStack), "windows").GetValue(repairWindowStack)).Clear();
            controller.GetMethod("RequestPipelineRepair", flags).Invoke(null, new object[] { "delta.other", false });
            deltaRequest.Invoke(null, new object[] { new[] { "", " " } });
            PollNoticeForCompletedResult(controller, flags);
            Check(!repairWindowStack.Windows.Any(w => w is Window_TranslationRepair),
                "a completed automatic result stays closed even when notice polling runs again");
            for (int i = 0; i < 10; i++) Pump();
            Check(!Waiting() && !(bool)controller.GetProperty("IsRunning", flags).GetValue(null) &&
                ReferenceEquals(completedDelta, controller.GetProperty("Results", flags).GetValue(null)),
                "closing results, empty batches and no-output translation cannot restart a full repair");
            bool HasCompletion() => (bool)scanner.GetMethod("HasCompletedStartupRepair", flags).Invoke(null,
                new object[] { TargetLanguage.Traditional });
            Check(!HasCompletion(), "a scoped repair cannot acknowledge newly changed files outside its scope");
            controller.GetMethod("RequestStart", flags).Invoke(null, null);
            finishDeadline = DateTime.UtcNow.AddSeconds(30);
            do { Pump(); Thread.Sleep(1); }
            while (!(bool)controller.GetProperty("Finished", flags).GetValue(null) && DateTime.UtcNow < finishDeadline);
            Check(controller.GetProperty("Failure", flags).GetValue(null) == null && HasCompletion(),
                "a completed full repair establishes a reusable baseline despite retained malformed XML");
            File.WriteAllText(firstFile, "<LanguageData><DeltaFirst.menuText>新增翻譯</DeltaFirst.menuText>" +
                "<DeltaFirst.expandable><li>清單翻譯</li></DeltaFirst.expandable>" +
                "<DeltaFirst.category>保留但不能注入</DeltaFirst.category></LanguageData>");
            deltaRequest.Invoke(null, new object[] { new[] { "delta.first" } });
            finishDeadline = DateTime.UtcNow.AddSeconds(30);
            do { Pump(); Thread.Sleep(1); }
            while (!(bool)controller.GetProperty("Finished", flags).GetValue(null) && DateTime.UtcNow < finishDeadline);
            results = (TranslationRepairRun)controller.GetProperty("Results", flags).GetValue(null);
            Check(controller.GetProperty("Failure", flags).GetValue(null) == null && results.Unresolved > 0 &&
                first.menuText == "新增翻譯" && scanner.GetField("LastVerifiedMemorySignature", flags).GetValue(null) == null,
                "a real delta with skipped candidates finishes and applies usable text without a clean-cache certificate");
            Check(HasCompletion(), "completed incremental repair advances the full-pack receipt even with unresolved and skipped candidates");
            TestCompletedRepairRestart(scanner, controller, file);
            TestBaselineFileDelta(scanner, controller, file, Pump);
        }
        finally
        {
            controller.GetMethod("CancelWaiting", flags).Invoke(null, null);
            scanner.GetField("_pendingMemoryDropPayload", flags).SetValue(null, null);
            scanner.GetField("_activeMemoryDropApply", flags).SetValue(null, null);
            scanner.GetField("_pendingStaticCachedTranslationRefresh", flags).SetValue(null, false);
            LanguageDatabase.activeLanguage = previousLanguage; Current.ProgramState = previousState;
            AutoTranslatorSettings.IsRunning = previousBusy; repairWindowStack = previousWindows;
            AutoTranslatorMod.Settings = previousSettings; selectionPack = previousPack;
            hooks.UnpatchAll(hooks.Id);
        }
    }
}
