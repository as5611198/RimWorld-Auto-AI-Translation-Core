using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static readonly HashSet<string> startupParsedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static void TrackStartupXmlParse(string filePath) { lock (startupParsedFiles) startupParsedFiles.Add(Path.GetFullPath(filePath)); }
    static void ReportBaselineFailure(Exception ex) { Console.WriteLine("Repair failure detail: " + ex); }
    static void TestBaselineFileDelta(Type scanner, Type controller, string file, Action pump)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        string root = Path.Combine(selectionPack, "Languages", "ChineseTraditional");
        string keyed = Path.Combine(root, "Keyed");
        Directory.CreateDirectory(keyed);
        string deletedA = Path.Combine(keyed, "deleted_a_AutoTranslated.xml");
        string deletedB = Path.Combine(keyed, "deleted_b_AutoTranslated.xml");
        string modified = Path.Combine(keyed, "delta_keyed_AutoTranslated.xml");
        foreach (string path in new[] { deletedA, deletedB, modified })
            File.WriteAllText(path, "<LanguageData><" + Path.GetFileNameWithoutExtension(path) + ">原始文字</" + Path.GetFileNameWithoutExtension(path) + "></LanguageData>");
        void Finish()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            do { pump(); Thread.Sleep(1); }
            while (((bool)controller.GetField("startupLookup", flags).GetValue(null) ||
                !(bool)controller.GetProperty("Finished", flags).GetValue(null)) && DateTime.UtcNow < deadline);
            Check((bool)controller.GetProperty("Finished", flags).GetValue(null) && controller.GetProperty("Failure", flags).GetValue(null) == null,
                "baseline lifecycle completes without a full fallback or failed run (phase=" + controller.GetProperty("Phase", flags).GetValue(null) +
                ", failure=" + controller.GetProperty("Failure", flags).GetValue(null) + ")");
        }
        controller.GetMethod("RequestStart", flags).Invoke(null, null);
        Finish();
        Check((string)controller.GetProperty("ModeLabel", flags).GetValue(null) == "ATC_Repair_ModeFull",
            "only the explicit manual full-check button can rerun full repair with an existing baseline");
        DateTime beforeTime = File.GetLastWriteTimeUtc(modified);
        File.WriteAllText(modified, File.ReadAllText(modified).Replace("原始文字", "新增文字"));
        File.SetLastWriteTimeUtc(modified, beforeTime);
        File.Delete(deletedA); File.Delete(deletedB);
        string broken = Path.Combine(Path.GetDirectoryName(file), "delta_broken_AutoTranslated.xml");
        File.WriteAllText(broken, "<LanguageData><broken>新增損壞項目");
        string original = File.ReadAllText(file);
        File.WriteAllText(file, original.Replace("背景銜接完成", "增量完成測試"));
        AutoTranslatorScanner.NotifyTranslationFilesChanged(root);
        scanner.GetMethod("InvalidateGameData", flags).Invoke(null, null);
        var freshLanguage = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
        freshLanguage.info = new LanguageInfo();
        freshLanguage.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
        freshLanguage.defInjections = new List<DefInjectionPackage>(); freshLanguage.loadErrors = new List<string>();
        AccessTools.Field(typeof(LoadedLanguage), "dataIsLoaded").SetValue(freshLanguage, true);
        LanguageDatabase.activeLanguage = freshLanguage;
        controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
        controller.GetProperty("Finished", flags).SetValue(null, false);
        var parseHooks = new Harmony("atc.baseline-delta-parse-scope");
        parseHooks.Patch(AccessTools.Method(typeof(WindowStack), "Add"), prefix: new HarmonyMethod(typeof(Suite), nameof(AddAutomaticRepairWindow)));
        parseHooks.Patch(controller.GetMethod("Fail", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(ReportBaselineFailure)));
        startupParsedFiles.Clear();
        parseHooks.Patch(scanner.GetMethod("TryParseXmlFileToDict", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(TrackStartupXmlParse)));
        try
        {
            controller.GetMethod("RequestStartupRepairOrInject", flags).Invoke(null, null);
            Finish();
        }
        finally { parseHooks.UnpatchAll(parseHooks.Id); }
        var expected = new HashSet<string>(new[] { file, modified, broken }.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        Check(startupParsedFiles.Count > 0 && startupParsedFiles.All(expected.Contains),
            "startup reuses persisted parsed translations and parses only changed XML, including when prior repair had errors");
        var scope = (HashSet<string>)controller.GetField("repairFileScope", flags).GetValue(null);
        Check(scope != null && scope.SetEquals(expected),
            "two package deletions plus modified/new XML select only the three changed files, including same-time same-size edits");
        var work = controller.GetField("work", flags).GetValue(null);
        var scanned = (string[])work.GetType().GetField("files", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(work);
        Check(scanned.Length == 2 && scanned.All(expected.Contains),
            "step 1 reads only modified/new DefInjected XML; unchanged and deleted XML are never scanned");
        var results = (TranslationRepairRun)controller.GetProperty("Results", flags).GetValue(null);
        Check(results.Total > 0 && results.Snapshot().All(r => r.SourceFile != null && expected.Contains(Path.GetFullPath(r.SourceFile))),
            "step 3 verifies only delta observations even while startup injects unchanged translations into fresh memory");
        Check(results.Unresolved > 0 && LanguageDatabase.activeLanguage.keyedReplacements["delta_keyed_AutoTranslated"].value == "新增文字",
            "malformed changed XML is retained while valid changed Keyed text is applied");
        Check(!LanguageDatabase.activeLanguage.keyedReplacements.ContainsKey("deleted_a_AutoTranslated") &&
            !LanguageDatabase.activeLanguage.keyedReplacements.ContainsKey("deleted_b_AutoTranslated"),
            "deleted XML contributes no stale cached translations");
        Check((bool)scanner.GetMethod("HasCompletedStartupRepair", flags).Invoke(null, new object[] { TargetLanguage.Traditional }),
            "delta completion updates baseline despite errors and deletions outside the changed packages");
        var plan = scanner.GetMethod("PlanStartupRepair", flags).Invoke(null, new object[] { TargetLanguage.Traditional });
        Check((string)plan.GetType().GetProperty("Decision", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(plan) == "fast-inject",
            "the next launch has no repair work after the deletion-plus-change reproducer");
        plan = scanner.GetMethod("PlanStartupRepair", flags).Invoke(null, new object[] { TargetLanguage.Simplified });
        Check((bool)plan.GetType().GetField("BaselineExists", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(plan),
            "changing language never clears the baseline-exists gate");

        scanner.GetMethod("InvalidateGameData", flags).Invoke(null, null);
        AutoTranslatorScanner.NotifyTranslationFilesChanged(root);
        controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
        startupParsedFiles.Clear(); queuedRepairStarts = 0;
        parseHooks.Patch(scanner.GetMethod("TryParseXmlFileToDict", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(TrackStartupXmlParse)));
        parseHooks.Patch(controller.GetMethod("BeginRepair", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(CaptureQueuedRepairStart)));
        try
        {
            controller.GetMethod("RequestStartupRepairOrInject", flags).Invoke(null, null);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            do { pump(); Thread.Sleep(1); }
            while (((bool)controller.GetField("startupLookup", flags).GetValue(null) ||
                (bool)scanner.GetProperty("MemoryDropBusy", flags).GetValue(null)) && DateTime.UtcNow < deadline);
            Check(!(bool)controller.GetField("startupLookup", flags).GetValue(null) &&
                !(bool)scanner.GetProperty("MemoryDropBusy", flags).GetValue(null) && queuedRepairStarts == 0 && startupParsedFiles.Count == 0,
                "unchanged startup really injects with zero repair starts and zero XML reparses after clearing memory caches");
        }
        finally { parseHooks.UnpatchAll(parseHooks.Id); }
        using (var locked = new FileStream(modified, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool skipped = false;
            try
            {
                var parsed = scanner.GetMethod("ReadInjectionXmlFile", flags).Invoke(null, new object[] { modified });
                skipped = ((Dictionary<string, string>)parsed.GetType().GetField("Data").GetValue(parsed)).Count == 0;
            }
            catch (TargetInvocationException) { }
            Check(skipped, "an unreadable individual XML is skipped without aborting injection of the other files");
        }
    }
}
