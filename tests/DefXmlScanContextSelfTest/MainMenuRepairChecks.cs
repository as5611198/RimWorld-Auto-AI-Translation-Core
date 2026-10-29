using System.Collections;
using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static int repairThread, offThreadValidations, mainThreadRepairWrites;
    static void CheckRepairWriteThread()
    {
        if (Thread.CurrentThread.ManagedThreadId == repairThread) mainThreadRepairWrites++;
    }
    static void CheckRepairThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != repairThread) offThreadValidations++;
    }

    static void TestMainMenuRepair(string languageRoot, LoadedLanguage language, Harmony hooks)
    {
        TestManualRepairBoundary(languageRoot);
        TestRepairPersistence(hooks);
        TestRepairCheckThroughput(hooks);
        TestRepairDescriptionErrors(languageRoot, language);
        var scanner = typeof(AutoTranslatorScanner);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        TestPartialRepairCompletionPolicy(scanner, flags);
        var def = new RepairFixtureDef { defName = "MenuRepair" };
        DefDatabase<RepairFixtureDef>.Add(def);
        string file = Path.Combine(languageRoot, "DefInjected", nameof(RepairFixtureDef), "menu-repair.xml");
        string original = "<LanguageData><MenuRepair.oldMenuText>已付費且應即時生效</MenuRepair.oldMenuText>" +
            "<MenuRepair.category>保留錯誤欄位</MenuRepair.category><MenuDisabled.label>保留停用內容</MenuDisabled.label>" +
            "<MenuRepair.stages.stable.label>衝突一</MenuRepair.stages.stable.label>" +
            "<MenuRepair.stages.0.label>衝突二</MenuRepair.stages.0.label></LanguageData>";
        File.WriteAllText(file, original);
        string malformed = Path.Combine(Path.GetDirectoryName(file), "menu-malformed.xml");
        File.WriteAllText(malformed, "<LanguageData><broken>原始付費資料");

        var workType = scanner.GetNestedType("MainMenuRepairWork", BindingFlags.NonPublic);
        Check(workType != null, "main-menu repair has a pump-driven work item independent of window visibility");
        repairThread = Thread.CurrentThread.ManagedThreadId; offThreadValidations = 0;
        mainThreadRepairWrites = 0;
        hooks.Patch(scanner.GetMethod("SaveValidatedXml", flags), prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(CheckRepairWriteThread), flags)));
        hooks.Patch(scanner.GetMethod("ValidateDefTranslation"), prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(CheckRepairThread), flags)));
        TranslationRepairRun RunWork()
        {
            var results = new TranslationRepairRun();
            var work = Activator.CreateInstance(workType, new object[] { languageRoot, results });
            var tick = workType.GetMethod("Tick");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!(bool)workType.GetProperty("Done").GetValue(work))
            {
                tick.Invoke(work, null);
                if (DateTime.UtcNow > deadline) throw new Exception("Repair work did not finish");
                Thread.Sleep(1);
            }
            Check((int)workType.GetProperty("Processed").GetValue(work) == (int)workType.GetProperty("Total").GetValue(work),
                "repair progress finishes at the real processed file count");
            return results;
        }
        var run = RunWork();
        Check(mainThreadRepairWrites == 0, "manual repair backs up and writes validated XML off the main thread");
        Check(offThreadValidations == 0, "background file parsing never traverses Verse Defs; validation stays on the caller's main thread");
        Check(run.Verified == 0 && run.Waiting > 0, "file writes remain unverified until fresh native application completes");
        var saved = AutoTranslatorScanner.LoadXmlFileToDict(file);
        Check(saved["MenuRepair.menuText"] == "已付費且應即時生效" && !saved.ContainsKey("MenuRepair.category") && !saved.ContainsKey("MenuDisabled.label"),
            "batched repair migrates proven aliases and holds invalid/inapplicable paid entries");
        Check(File.ReadAllText(malformed) == "<LanguageData><broken>原始付費資料" &&
            Directory.GetFiles(Path.Combine(selectionPack, "SelfRepair", "originals")).Any(p => File.ReadAllText(p) == original),
            "malformed file is retained byte-for-byte and replaced XML has its original backup");

        scanner.GetMethod("ResetMemoryDropStamps", flags).Invoke(null, null);
        var build = scanner.GetMethods(flags).Single(m => m.Name == "BuildMemoryDropPayload" && m.GetParameters().Length == 1);
        var payload = build.Invoke(null, new object[] { false });
        Check(payload != null, "repair builds a fresh full memory payload despite earlier injection stamps");
        int completions = 0; bool appliedResult = false;
        var controller = scanner.Assembly.GetType("AutoTranslator_Core.TranslationRepairController");
        controller.GetField("running", flags).SetValue(null, true);
        controller.GetField("run", flags).SetValue(null, run);
        controller.GetField("<Phase>k__BackingField", flags).SetValue(null, "ATC_Repair_Applying");
        scanner.GetField("repairMemoryRequest", flags).SetValue(null, 71);
        scanner.GetField("repairMemoryCompletion", flags).SetValue(null, (Action<bool>)(ok => {
            completions++; appliedResult = ok;
            controller.GetMethod("OnMemoryApplied", flags).Invoke(null, new object[] { ok });
        }));
        var complete = scanner.GetMethod("CompleteRepairMemoryDrop", flags);
        complete.Invoke(null, new object[] { 70, true });
        Check(completions == 0, "an older memory-drop completion cannot finish this repair");
        payload.GetType().GetField("RepairRequest").SetValue(payload, 71);
        try { scanner.GetMethod("ApplyMemoryDropPayloadImmediate", flags).Invoke(null, new object[] { payload, null }); }
        finally { controller.GetField("running", flags).SetValue(null, false); }
        Check(completions == 1 && appliedResult && def.menuText == "已付費且應即時生效",
            "matching completion fires once after actual native injection has changed the live value");
        Check((string)controller.GetProperty("Phase", flags).GetValue(null) == "ATC_Repair_Verifying" &&
            ((IList)controller.GetField("verification", flags).GetValue(null)).Count == run.Total,
            "actual repair coordinator immediately advances from memory application to this run's verification without reload");
        complete.Invoke(null, new object[] { 71, true });
        Check(completions == 1, "repeated completion does not notify a completed repair twice");

        var verify = scanner.GetMethod("VerifyRepairRecord", flags);
        foreach (var record in run.Snapshot()) verify.Invoke(null, new object[] { run, record });
        Check(run.Verified == 1 && run.Inapplicable >= 1 && run.Unresolved >= 3 && run.Waiting == 0,
            "final results separate live verified repair, disabled target, conflict, invalid field and malformed file without restart");
        var repaired = run.Snapshot().Single(r => r.SourceFile == file && r.Translation == "已付費且應即時生效");
        def.menuText = "另一個模組覆蓋的文字";
        verify.Invoke(null, new object[] { run, repaired });
        Check(run.Verified == 0 && run.Outcome(repaired) == "unresolved",
            "an injected=true flag cannot hide a mismatching actual runtime value");
        var next = RunWork();
        Check(next.Verified == 0 && !next.Snapshot().Any(r => r.SourceFile == file && r.Translation == repaired.Translation),
            "a later clean recheck does not count an already migrated historical alias as another repair");
        Check(next.Snapshot().Any(r => r.SourceFile == file && r.Translation == "衝突二" && r.Status == "衝突"),
            "a recheck revalidates preserved conflicting paid candidates instead of silently reporting them clean");
        Check(repairNetworkCalls == 0, "main-menu repair and verification make no paid/network request");
        TestBackgroundRepairResult(controller, run, hooks);
    }

    static void TestPartialRepairCompletionPolicy(Type scanner, BindingFlags flags)
    {
        object New(string name) => Activator.CreateInstance(scanner.GetNestedType(name, BindingFlags.NonPublic), true);
        void Field(object obj, string name, object value) => obj.GetType().GetField(name).SetValue(obj, value);
        var payload = New("MemoryDropPayload");
        Field(payload, "RepairRequest", 68);
        Field(payload, "PreparedStamp", "partial-must-not-be-cached");
        Field(payload, "InputSignature", "partial-must-not-be-verified");
        Field(payload, "Language", LanguageDatabase.activeLanguage);
        Field(payload, "Epoch", scanner.GetProperty("GameDataEpoch", flags).GetValue(null));
        var bucket = New("MemoryDropDefTypePayload");
        Field(bucket, "DefTypeName", nameof(RepairFixtureDef));
        var values = (Dictionary<string, string>)bucket.GetType().GetField("Data").GetValue(bucket);
        values["RepairFixture.label"] = "部分完成仍需驗證";
        values["RepairFixture.category"] = "不應注入非文字欄位";
        ((IList)payload.GetType().GetField("DefsByType").GetValue(payload)).Add(bucket);
        var controller = scanner.Assembly.GetType("AutoTranslator_Core.TranslationRepairController");
        var run = new TranslationRepairRun();
        int callbacks = 0;
        bool result = false;
        scanner.GetMethod("ResetMemoryDropStamps", flags).Invoke(null, null);
        scanner.GetField("LastVerifiedMemorySignature", flags).SetValue(null, null);
        scanner.GetField("repairMemoryRequest", flags).SetValue(null, 68);
        scanner.GetField("repairMemoryCompletion", flags).SetValue(null, (Action<bool>)(ok => {
            callbacks++; result = ok;
            // The failure UI needs a live window stack; assert its callback
            // value below instead of entering that unrelated headless path.
            if (ok) controller.GetMethod("OnMemoryApplied", flags).Invoke(null, new object[] { ok });
        }));
        controller.GetField("running", flags).SetValue(null, true);
        controller.GetField("run", flags).SetValue(null, run);
        controller.GetField("<Phase>k__BackingField", flags).SetValue(null, "ATC_Repair_Applying");
        try
        {
            scanner.GetMethod("ApplyMemoryDropPayloadImmediate", flags).Invoke(null, new[] { payload, (object)null });
            Check(callbacks == 1 && result && DefDatabase<RepairFixtureDef>.GetNamed("RepairFixture").label == "部分完成仍需驗證",
                "real mixed valid/rejected payload applies safe text and reports completion exactly once");
            Check((string)controller.GetProperty("Phase", flags).GetValue(null) == "ATC_Repair_Verifying",
                "real mixed payload advances the coordinator to verification despite skipped entries");
            Check(scanner.GetField("LastVerifiedMemorySignature", flags).GetValue(null) == null &&
                scanner.GetField("_lastFullMemoryDropStamp", flags).GetValue(null) as string != "partial-must-not-be-cached",
                "partial application cannot publish a clean full-pack receipt or skip stamp");
        }
        finally { controller.GetField("running", flags).SetValue(null, false); }

        var policy = scanner.GetMethod("ShouldContinueRepairAfterInjection", flags);
        Check(policy != null, "partial memory injection exposes a completion policy");
        Check((bool)policy.Invoke(null, new object[] { true, 0, 480 }),
            "safe rejected entries do not abort verification of successful injections");
        Check((bool)policy.Invoke(null, new object[] { true, 1, 0 }),
            "individual native injection failures are recorded without aborting other entries or baseline completion");
        Check(!(bool)policy.Invoke(null, new object[] { false, 0, 480 }),
            "an interrupted injection never reports completed just because candidates were skipped");
    }

    static bool ResultHeaderTranslation(string key, ref TaggedString __result) { __result = key; return false; }

    static void TestBackgroundRepairResult(Type controller, TranslationRepairRun results, Harmony hooks)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        hooks.Patch(AccessTools.Method(typeof(Verse.Translator), "Translate", new[] { typeof(string) }),
            prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(ResultHeaderTranslation), flags)));
        hooks.Patch(AccessTools.Method(typeof(Verse.Translator).Assembly.GetType("Verse.TranslatorFormattedStringExtensions"), "Translate", new[] { typeof(string), typeof(NamedArgument) }),
            prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(ResultHeaderTranslation), flags)));
        var previousRun = controller.GetField("run", flags).GetValue(null);
        var previousPhase = controller.GetProperty("Phase", flags).GetValue(null);
        var previousReporting = controller.GetField("reporting", flags).GetValue(null);
        var previousFinished = controller.GetProperty("Finished", flags).GetValue(null);
        controller.GetField("run", flags).SetValue(null, results);
        controller.GetField("<Finished>k__BackingField", flags).SetValue(null, false);
        pausedRepairPath = Path.Combine(selectionPack, "SelfRepair", "last-run.json");
        repairWriteEntered.Reset(); releaseRepairWrite.Reset();
        Task reporting = null;
        try
        {
            controller.GetMethod("Complete", flags).Invoke(null, null);
            reporting = (Task)controller.GetField("reporting", flags).GetValue(null);
            Check(reporting != null && repairWriteEntered.Wait(TimeSpan.FromSeconds(10)), "completed repair persists its final results in a background task");
            Check((string)controller.GetProperty("Phase", flags).GetValue(null) == "ATC_Repair_Saving" &&
                !(bool)controller.GetProperty("Finished", flags).GetValue(null) && !reporting.IsCompleted,
                "repair never claims completion while final persistence is blocked");
        }
        finally
        {
            releaseRepairWrite.Set(); reporting?.GetAwaiter().GetResult(); pausedRepairPath = null;
            controller.GetField("run", flags).SetValue(null, previousRun);
            controller.GetField("<Phase>k__BackingField", flags).SetValue(null, previousPhase);
            controller.GetField("<Finished>k__BackingField", flags).SetValue(null, previousFinished);
            controller.GetField("reporting", flags).SetValue(null, previousReporting);
        }
        var report = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(selectionPack, "SelfRepair", "last-run.json")));
        Check((int)report["verified"] == results.Verified && (int)report["waiting"] == 0,
            "background report preserves actual verified totals and resolves unfinished entries honestly");
    }
}
