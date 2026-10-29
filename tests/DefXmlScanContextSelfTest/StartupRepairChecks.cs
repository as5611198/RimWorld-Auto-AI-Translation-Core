using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal sealed class RepairOverlayWindow : Window { public override void DoWindowContents(UnityEngine.Rect rect) { } }
internal sealed class RepairPriorityHandles
{
    [TranslationHandle] public string low;
    [TranslationHandle(Priority = 5)] public object high;
}

internal static partial class Suite
{
    static int failedPreparationCalls, repairNoticeOpens;
    static WindowStack repairWindowStack;
    static bool SimulatePreparationFailure(ref object __result)
    {
        failedPreparationCalls++;
        if (failedPreparationCalls == 1) throw new ArgumentOutOfRangeException("chunkLength", "fixture preparation failure");
        __result = null; return false;
    }
    static void ForbidSharedHandle() => throw new ArgumentOutOfRangeException("chunkLength", "shared native builder is in use");
    static void ForbidSharedTypeCache() => throw new ArgumentException("An item with the same key has already been added. TypeCacheKey");
    static bool RepairWindowStack(ref WindowStack __result) { __result = repairWindowStack; return false; }
    static bool NoLongEvent(ref bool __result) { __result = false; return false; }
    static bool ActiveLongEvent(ref bool __result) { __result = true; return false; }
    static bool CaptureRepairNotice() { repairNoticeOpens++; return false; }

    static void TestStartupRepairFailures(string languageRoot)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var scanner = typeof(AutoTranslatorScanner);
        var failures = new List<Exception>();
        void Case(Action check) { try { check(); } catch (Exception ex) { failures.Add(ex); Console.WriteLine(ex.GetBaseException().Message); } }

        Case(() => {
            var h = new Harmony("atc.startup-test.shared-handle");
            h.Patch(AccessTools.Method(typeof(TranslationHandleUtility), "GetElementIndexByHandle"),
                prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(ForbidSharedHandle), flags)));
            try {
                var result = Task.Run(() => AutoTranslatorScanner.ValidateDefTranslation(nameof(RepairFixtureDef), "RepairFixture.stages.stable.label")).GetAwaiter().GetResult();
                Check(!result.Allowed && result.Reason == "not_ready", "background validation defers live handles to the main-thread queue");
            } finally { h.UnpatchAll(h.Id); }
        });
        Case(() => {
            var h = new Harmony("atc.startup-test.shared-types");
            h.Patch(AccessTools.Method(typeof(GenTypes), "GetTypeInAnyAssembly"),
                prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(ForbidSharedTypeCache), flags)));
            try {
                var result = Task.Run(() => AutoTranslatorScanner.ValidateDefTranslation(nameof(RepairFixtureDef), "RepairFixture.label")).GetAwaiter().GetResult();
                Check(!result.Allowed && result.Reason == "not_ready", "background validation defers live Def lookup without accessing native caches");
            } finally { h.UnpatchAll(h.Id); }
        });
        Case(() => {
            var h = new Harmony("atc.startup-test.loading");
            h.Patch(AccessTools.PropertyGetter(typeof(LongEventHandler), "AnyEventNowOrWaiting"), prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(ActiveLongEvent), flags)));
            try {
                scanner.GetField("_pendingMemoryDrop", flags).SetValue(null, true);
                scanner.GetMethod("StartMemoryDropPreparationIfNeeded", flags).Invoke(null, null);
                Check((int)scanner.GetField("_memoryDropPrepareRunning", flags).GetValue(null) == 0 &&
                    (bool)scanner.GetField("_pendingMemoryDrop", flags).GetValue(null),
                    "deferred native loading keeps full memory preparation queued instead of traversing live Defs");
                var payloadType = scanner.GetNestedType("MemoryDropPayload", BindingFlags.NonPublic);
                var payload = Activator.CreateInstance(payloadType, true);
                scanner.GetField("_pendingMemoryDropPayload", flags).SetValue(null, payload);
                scanner.GetMethod("PumpMainThreadDispatcher", flags).Invoke(null, null);
                Check(ReferenceEquals(scanner.GetField("_pendingMemoryDropPayload", flags).GetValue(null), payload),
                    "deferred native loading also postpones an already-prepared memory payload");
            } finally {
                scanner.GetField("_pendingMemoryDrop", flags).SetValue(null, false);
                scanner.GetField("_pendingMemoryDropPayload", flags).SetValue(null, null);
                scanner.GetField("_activeMemoryDropApply", flags).SetValue(null, null);
                h.UnpatchAll(h.Id);
            }
        });
        Case(() => {
            var h = new Harmony("atc.startup-test.failed-preparation");
            var build = scanner.GetMethods(flags).Single(m => m.Name == "BuildMemoryDropPayload" && m.GetParameters().Length == 3);
            h.Patch(build, prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(SimulatePreparationFailure), flags)));
            try {
                failedPreparationCalls = 0;
                scanner.GetField("_pendingMemoryDrop", flags).SetValue(null, true);
                scanner.GetField("_memoryDropPrepareRunning", flags).SetValue(null, 1);
                int generation = (int)scanner.GetField("_memoryDropGeneration", flags).GetValue(null);
                scanner.GetMethod("PrepareMemoryDropPayloadWorker", flags).Invoke(null, new object[] { false, null, null, generation, 0 });
                SpinWait.SpinUntil(() => (int)scanner.GetField("_memoryDropPrepareRunning", flags).GetValue(null) == 0, 5000);
                Check(failedPreparationCalls == 1 && !(bool)scanner.GetProperty("MemoryDropBusy", flags).GetValue(null),
                    "failed ordinary memory preparation releases busy state and does not automatically retry forever");
            } finally { h.UnpatchAll(h.Id); }
        });
        Case(() => {
            string xml = "<LanguageData><Quest.questDescriptionRules.rulesStrings><li>questName->original</li></Quest.questDescriptionRules.rulesStrings></LanguageData>";
            var hook = scanner.Assembly.GetType("AutoTranslator_Core.TranslationSelfRepairHooks").GetMethod("RepairReadContent", flags);
            Check((string)hook.Invoke(null, new object[] { Path.Combine(temporary, "Workshop", "Quest.xml"), xml }) == xml,
                "third-party quest rule keys are never rewritten by automatic read hooks");
        });
        Case(() => {
            string file = Path.Combine(languageRoot, "DefInjected", nameof(RepairFixtureDef), "duplicate-lists.xml");
            string xml = "<LanguageData><RepairFixture.expandable><li>保留第一個</li></RepairFixture.expandable>" +
                "<RepairFixture.expandable><li>保留第二個</li></RepairFixture.expandable><RepairFixture.menuText>後續仍能載入</RepairFixture.menuText></LanguageData>";
            File.WriteAllText(file, xml);
            var package = new DefInjectionPackage(typeof(RepairFixtureDef));
            package.AddDataFromFile(new NumericXmlFile(file), out bool error, xml);
            Check(!error && package.injections.ContainsKey("RepairFixture.menuText") &&
                package.injections["RepairFixture.expandable"].fullListInjection.Single() == "保留第一個",
                "duplicate native full lists retain the first candidate and allow subsequent entries to load");
            var journal = (TranslationRepairJournal)scanner.GetProperty("RepairJournal", flags).GetValue(null);
            Check(journal.ForSource(file).Any(r => r.Translation.Contains("保留第二個") && r.Status == "衝突") && File.ReadAllText(file) == xml,
                "conflicting full lists retain both source values and unchanged original XML for review");
            package.AddDataFromFile(new NumericXmlFile(file), out bool repeatedError,
                "<LanguageData><RepairFixture.expandable><li>保留第一個</li></RepairFixture.expandable><RepairFixture.label>重複後仍能載入</RepairFixture.label></LanguageData>");
            Check(!repeatedError && package.injections["RepairFixture.label"].injection == "重複後仍能載入",
                "identical full-list rereads are merged without stopping the native file reader");
            var recovered = new Dictionary<string, string>();
            scanner.GetMethod("RecoverPreservedTranslations", flags).Invoke(null, new object[] { file, recovered });
            Check(!recovered.ContainsKey("RepairFixture.expandable"), "preserved full-list JSON never re-enters scalar restoration");
        });
        Case(() => {
            var hook = scanner.Assembly.GetType("AutoTranslator_Core.TranslationSelfRepairHooks").GetMethod("BeforeFullList", flags);
            var package = new DefInjectionPackage(typeof(RepairFixtureDef));
            string path = Path.Combine(temporary, "Workshop", "Duplicate.xml");
            package.injections["RepairFixture.expandable"] = new DefInjectionPackage.DefInjection {
                path = "RepairFixture.expandable", fullListInjection = new List<string> { "first" }, fileSource = path };
            Check((bool)hook.Invoke(null, new object[] { package, new NumericXmlFile(path), "RepairFixture.expandable", new List<string> { "second" } }),
                "third-party duplicate-list handling stays with the native loader");
        });
        Case(() => {
            var resolver = scanner.Assembly.GetType("AutoTranslator_Core.TranslationRuntimeResolver");
            var resolve = resolver.GetMethod("ElementIndex", flags);
            var normalize = typeof(TranslationHandleUtility).GetMethod("NormalizedHandle", flags);
            var ownNormalize = resolver.GetMethod("Normalize", flags);
            foreach (string raw in new[] { " a  b ", "a.{x}-b\t_c", "123", "中文 foo", "___", "a\r\nb", "a{one\ntwo}b", "", "a-0", "Test.Type", "Ω🌟foo", "a___中文__b" })
                Check((string)normalize.Invoke(null, new object[] { raw }) == (string)ownNormalize.Invoke(null, new object[] { raw }), "local normalization agrees with native for " + raw.Replace("\n", "\\n"));
            var nodes = new List<RepairFixtureNode> { new RepairFixtureNode(), new RepairFixtureNode() };
            foreach (int occurrence in new[] { -1, 0, 1 })
                Check((int)resolve.Invoke(null, new object[] { nodes, "stable", occurrence }) == TranslationHandleUtility.GetElementIndexByHandle(nodes, "stable", occurrence),
                    "local handle occurrence matches native " + occurrence);
            var priorities = new List<RepairPriorityHandles> {
                new RepairPriorityHandles { low = "same", high = "other" }, new RepairPriorityHandles { low = "other", high = "same" }
            };
            Check((int)resolve.Invoke(null, new object[] { priorities, "same", 0 }) == TranslationHandleUtility.GetElementIndexByHandle(priorities, "same", 0),
                "higher-priority handle field wins across list elements exactly as native");
            priorities[1].high = typeof(RepairFixtureDef);
            Check((int)resolve.Invoke(null, new object[] { priorities, nameof(RepairFixtureDef), 0 }) ==
                TranslationHandleUtility.GetElementIndexByHandle(priorities, nameof(RepairFixtureDef), 0), "Type-valued handles match native names");
            int[] indexes = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => {
                int last = 0;
                for (int i = 0; i < 500; i++) last = (int)resolve.Invoke(null, new object[] { nodes, "stable", i % 2 });
                return last;
            }))).GetAwaiter().GetResult();
            Check(indexes.All(i => i == 1), "concurrent local handle resolution remains stable over 4000 lookups");
            var defType = resolver.GetMethod("DefType", flags);
            Check(typeof(Def).Assembly.GetTypes().Where(t => typeof(Def).IsAssignableFrom(t) && !t.ContainsGenericParameters)
                .All(t => Equals(defType.Invoke(null, new object[] { t.FullName }), GenTypes.GetTypeInAnyAssembly(t.FullName))),
                "local type resolution agrees with native for every game Def full name");
        });
        Case(() => {
            var controller = scanner.Assembly.GetType("AutoTranslator_Core.TranslationRepairController");
            var h = new Harmony("atc.startup-test.notice");
            repairWindowStack = new WindowStack();
            var windows = (List<Window>)AccessTools.Field(typeof(WindowStack), "windows").GetValue(repairWindowStack);
            windows.Add((Window)Uninitialized(typeof(RepairOverlayWindow)));
            h.Patch(AccessTools.PropertyGetter(typeof(Find), "WindowStack"), prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(RepairWindowStack), flags)));
            h.Patch(AccessTools.PropertyGetter(typeof(LongEventHandler), "AnyEventNowOrWaiting"), prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(NoLongEvent), flags)));
            h.Patch(AccessTools.Method(typeof(WindowStack), "Add"), prefix: new HarmonyMethod(typeof(Suite), nameof(AddRepairNotice)));
            void Poll() {
                controller.GetField("nextNoticeCheck", flags).SetValue(null, DateTime.MinValue);
                controller.GetField("readyAfter", flags).SetValue(null, DateTime.MinValue);
                controller.GetMethod("PollNotice", flags).Invoke(null, null);
            }
            var previous = Current.ProgramState;
            try {
                Current.ProgramState = ProgramState.Entry;
                repairNoticeOpens = 0;
                AutoTranslatorMod.Settings.LastRepairNotice = null;
                controller.GetField("checkedNoticeRevision", flags).SetValue(null, -1);
                controller.GetField("noticeSignature", flags).SetValue(null, null);
                Poll();
                Check(repairNoticeOpens == 1, "main-menu introduction appears with a noninteractive overlay window present");
                Poll();
                Check(repairNoticeOpens == 1, "main-menu poll does not repeatedly reopen an unchanged introduction");
                windows.RemoveAll(w => w is Window_TranslationRepair);
                windows[0].absorbInputAroundWindow = true;
                controller.GetField("checkedNoticeRevision", flags).SetValue(null, -1);
                controller.GetField("noticeSignature", flags).SetValue(null, null);
                Poll();
                Check(repairNoticeOpens == 1, "main-menu introduction waits for an interactive modal window to close");
            } finally { Current.ProgramState = previous; h.UnpatchAll(h.Id); }
        });
        if (failures.Count > 0) throw new AggregateException("Startup repair regressions", failures);
    }
}
