using System.Collections;
using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static bool FixtureMetaName(ref string __result) { __result = "Prevention fixture"; return false; }
    static void TestRepairPipelines(string languageRoot, string sourceFile, Harmony testHooks)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        // Display-name lookup otherwise initializes the installed mod catalog.
        testHooks.Patch(AccessTools.PropertyGetter(typeof(ModMetaData), "Name"),
            prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(FixtureMetaName), flags)));
        // Exercise the actual paid-work preparation with malformed English
        // DefInjected candidates (which bypass raw-Def extraction).
        string sourceRoot = Path.Combine(temporary, "Prevention", "Languages");
        Write("Prevention/Languages/English/DefInjected/RepairFixtureDef/Bad.xml",
            "<LanguageData><RepairFixture.category>Item category</RepairFixture.category>" +
            "<RepairFixture.reference>Def reference</RepairFixture.reference>" +
            "<RepairFixture.forbidden>Forbidden text</RepairFixture.forbidden>" +
            "<RepairFixture.missing>Missing field</RepairFixture.missing>" +
            "<RepairFixture.stages.99.label>Overflow</RepairFixture.stages.99.label>" +
            "<RepairFixture.coordinates.0>Dictionary value</RepairFixture.coordinates.0>" +
            "<NotLoaded.label>Disabled definition</NotLoaded.label>" +
            "<RepairFixture.label>可正常沿用的標籤</RepairFixture.label></LanguageData>");
        var meta = (ModMetaData)Uninitialized(typeof(ModMetaData));
        var metaField = AccessTools.Field(typeof(ModMetaData), "meta");
        var internals = Uninitialized(metaField.FieldType);
        AccessTools.Field(metaField.FieldType, "packageId").SetValue(internals, "prevention.fixture");
        AccessTools.Field(metaField.FieldType, "name").SetValue(internals, "Prevention fixture");
        metaField.SetValue(meta, internals);
        AccessTools.Field(typeof(ModMetaData), "packageIdLowerCase").SetValue(meta, "prevention.fixture");
        AccessTools.Field(typeof(ModMetaData), "rootDirInt").SetValue(meta, new DirectoryInfo(Path.Combine(temporary, "Prevention")));
        var process = scanner.GetMethods(flags).Single(m => m.Name == "ProcessModDefInjected" && m.GetParameters().Length == 3);
        var processing = (Task<int>)process.Invoke(null, new object[] { meta, new List<string> { sourceRoot }, new List<string>() });
        var validationPump = scanner.Assembly.GetType("AutoTranslator_Core.TranslationValidationQueue").GetMethod("Pump", flags);
        var processingDeadline = DateTime.UtcNow.AddSeconds(30);
        while (!processing.IsCompleted && DateTime.UtcNow < processingDeadline) { validationPump.Invoke(null, null); Thread.Sleep(1); }
        if (!processing.IsCompleted) throw new TimeoutException("Translation validation did not finish with main-thread pumping.");
        int translated = processing.GetAwaiter().GetResult();
        Check(translated == 0 && repairNetworkCalls == 0,
            "actual English-import processing rejects enum, Def reference, forbidden, missing, index and dictionary candidates before any paid request");
        var preventionOutput = AutoTranslatorScanner.LoadXmlFileToDict(Path.Combine(languageRoot, "DefInjected", nameof(RepairFixtureDef), "prevention_fixture_AutoTranslated.xml"));
        Check(preventionOutput.Count == 1 && preventionOutput["RepairFixture.label"] == "可正常沿用的標籤",
            "same import pipeline retains valid text while preventing invalid output");

        // Source merge retains its existing last-file priority and every value.
        string merge = Path.Combine(languageRoot, "DefInjected", "RepairFixtureDef", "Merge");
        Directory.CreateDirectory(merge);
        string first = Path.Combine(merge, "a.xml"), last = Path.Combine(merge, "b.xml");
        File.WriteAllText(first, "<LanguageData><RepairFixture.label>第一個付費版本</RepairFixture.label></LanguageData>");
        File.WriteAllText(last, "<LanguageData><RepairFixture.label>第二個付費版本</RepairFixture.label></LanguageData>");
        var sources = new Dictionary<string, string>();
        var merged = (Dictionary<string, string>)scanner.GetMethod("LoadXmlFilesToDictForMemoryDrop", flags)
            .Invoke(null, new object[] { merge, null, sources, nameof(RepairFixtureDef) });
        Check(merged["RepairFixture.label"] == "第二個付費版本" && sources["RepairFixture.label"] == last,
            "memory payload merge retains selected value and exact source");
        var journal = (TranslationRepairJournal)scanner.GetProperty("RepairJournal", flags).GetValue(null);
        Check(journal.ForSource(first).Any(r => r.Translation == "第一個付費版本") && journal.ForSource(last).Any(r => r.Translation == "第二個付費版本"),
            "payload merge preserves both conflicting paid candidates");

        string conflict = Path.Combine(Path.GetDirectoryName(sourceFile), "conflict.xml");
        foreach (string value in new[] { "候選一", "候選二" }) journal.Record(new TranslationRepairRecord {
            SourceFile = conflict, DefType = nameof(RepairFixtureDef), OriginalKey = "RepairFixture.label",
            Translation = value, Status = "仍待處理", SourceKind = "existing" });
        journal.Record(new TranslationRepairRecord { SourceFile = conflict, DefType = nameof(RepairFixtureDef),
            OriginalKey = "RepairFixture.expandable", Translation = "[\"完整清單\"]", SourceKind = "native-full-list", Status = "暫不適用" });
        var recovered = new Dictionary<string, string>();
        scanner.GetMethod("RecoverPreservedTranslations", flags).Invoke(null, new object[] { conflict, recovered });
        Check(recovered.Count == 0 && (bool)scanner.GetMethod("HasPreservedTranslation", flags).Invoke(null, new object[] { conflict, "RepairFixture.label" }),
            "ambiguous paid values stay held and block retranslation; full-list JSON never becomes a scalar translation");

        // Submit against a valid structure, then change one list before the real
        // game's two injection phases: only the actual injected flag counts.
        object NewNested(string name) => Activator.CreateInstance(scanner.GetNestedType(name, BindingFlags.NonPublic), true);
        void Field(object obj, string name, object value) => obj.GetType().GetField(name).SetValue(obj, value);
        int Count(object obj, string name) => (int)obj.GetType().GetField(name).GetValue(obj);
        var payload = NewNested("MemoryDropPayload");
        Field(payload, "PackageScoped", true);
        var bucket = NewNested("MemoryDropDefTypePayload");
        Field(bucket, "DefTypeName", nameof(RepairFixtureDef));
        var values = (Dictionary<string, string>)bucket.GetType().GetField("Data").GetValue(bucket);
        values["RepairFixture.label"] = "真正成功";
        values["RepairFixture.words.0"] = "提交後清單已變動";
        values["RepairFixture.category"] = "提交前攔截";
        ((IList)payload.GetType().GetField("DefsByType").GetValue(payload)).Add(bucket);
        var state = NewNested("MemoryDropApplyState");
        Field(state, "Payload", payload);
        var language = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
        language.defInjections = new List<DefInjectionPackage>();
        language.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement> {
            ["ATC_Log_MemoryDropSuccess"] = new LoadedLanguage.KeyedReplacement { key = "ATC_Log_MemoryDropSuccess", value = "{0}/{1}" }
        };
        AccessTools.Field(typeof(LoadedLanguage), "dataIsLoaded").SetValue(language, true);
        AccessTools.Field(typeof(LoadedLanguage), "loadErrors").SetValue(language, new List<string>());
        var previousLanguage = LanguageDatabase.activeLanguage;
        try
        {
            LanguageDatabase.activeLanguage = language;
            scanner.GetMethod("ApplyDefBatch", flags).Invoke(null, new[] { state, language });
            Check(Count(state, "SubmittedDefs") == 2 && Count(state, "InjectedDefs") == 0 && Count(state, "RejectedDefs") == 1,
                "real ApplyDefBatch counts submissions separately and blocks invalid candidates before native injection");
            var def = DefDatabase<RepairFixtureDef>.GetNamed("RepairFixture");
            def.words.Clear();
            while ((bool)scanner.GetMethod("ApplyNativeMemoryBatch", flags).Invoke(null, new[] { state })) { }
            scanner.GetMethod("FinishMemoryDropApply", flags).Invoke(null, new[] { state, (object)true });
            Check(Count(state, "InjectedDefs") == 1 && Count(state, "FailedDefs") == 1 && def.label == "真正成功",
                "real two-phase finish reports partial success from native injected flags, never submitted count");
            def.words.Add("old");
            TestNativeReportRepair(language);
            TestOwnedFullListPayload(languageRoot, language);
            TestMainMenuRepair(languageRoot, language, testHooks);
        }
        finally { LanguageDatabase.activeLanguage = previousLanguage; }
    }
    static void TestOwnedFullListPayload(string languageRoot, LoadedLanguage language)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        string file = Path.Combine(languageRoot, "DefInjected", nameof(RepairFixtureDef), "full_list_fixture_AutoTranslated.xml");
        string xml = "<LanguageData><RepairFixture.expandable><li>first</li><li>second</li></RepairFixture.expandable></LanguageData>";
        File.WriteAllText(file, xml);
        scanner.GetMethod("ResetMemoryDropStamps", flags).Invoke(null, null);
        var payload = scanner.GetMethods(flags).Single(m => m.Name == "BuildMemoryDropPayload" && m.GetParameters().Length == 1).Invoke(null, new object[] { false });
        var buckets = (System.Collections.IList)payload.GetType().GetField("DefsByType").GetValue(payload);
        var bucket = buckets.Cast<object>().Single(b => (string)b.GetType().GetField("DefTypeName").GetValue(b) == nameof(RepairFixtureDef));
        var fullListsField = bucket.GetType().GetField("FullLists");
        Check(fullListsField != null && ((Dictionary<string, List<string>>)fullListsField.GetValue(bucket))["RepairFixture.expandable"].Count == 2,
            "post-load payload preserves full-list shape and count-changing semantics");
        var values = (Dictionary<string, string>)bucket.GetType().GetField("Data").GetValue(bucket);
        var selected = values["RepairFixture.expandable"]; values.Clear(); values["RepairFixture.expandable"] = selected;
        buckets.Clear(); buckets.Add(bucket);
        var state = Activator.CreateInstance(scanner.GetNestedType("MemoryDropApplyState", BindingFlags.NonPublic), true);
        state.GetType().GetField("Payload").SetValue(state, payload);
        while ((bool)scanner.GetMethod("ApplyDefBatch", flags).Invoke(null, new[] { state, language })) { }
        while ((bool)scanner.GetMethod("ApplyNativeMemoryBatch", flags).Invoke(null, new[] { state })) { }
        Check(DefDatabase<RepairFixtureDef>.GetNamed("RepairFixture").expandable.SequenceEqual(new[] { "first", "second" }) && File.ReadAllText(file) == xml,
            "verified post-load full-list application writes back both values without changing source XML");
        File.Delete(file);
    }

}
