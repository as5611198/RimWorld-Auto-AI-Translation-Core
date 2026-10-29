using System.Reflection;
using AutoTranslator_Core;
using Verse;

internal static partial class Suite
{
    static void TestNativeReportRepair(LoadedLanguage language)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        var workerType = scanner.GetNestedType("NativeTranslationRepairWork", BindingFlags.NonPublic);
        Check(workerType != null, "manual repair also checks failed translations from the active native language packages");
        Check(!(bool)scanner.GetMethod("IsOwnedTranslationFile", flags).Invoke(null, new object[] { "ATC:baseline-restore" }) &&
            !(bool)scanner.GetMethod("IsOwnedTranslationFile", flags).Invoke(null, new object[] { "third-party.xml" }),
            "runtime source labels and bare native filenames are safely classified outside owned disk files on every runtime");
        string source = Path.Combine(temporary, "NativeReportSource", "DefInjected", nameof(RepairFixtureDef), "native.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(source));
        File.WriteAllText(source, "<LanguageData><!-- Workshop original must remain untouched --></LanguageData>");
        byte[] original = File.ReadAllBytes(source);
        var def = new RepairFixtureDef { defName = "NativeRepair" };
        var aliasDef = new RepairFixtureDef { defName = "NativeAlias" };
        var invalidRules = new RepairFixtureDef { defName = "NativeInvalidRules" };
        def.menuText = "已選定的譯文";
        def.stages[0].label = "已存在的相同譯文";
        DefDatabase<RepairFixtureDef>.Add(def);
        DefDatabase<RepairFixtureDef>.Add(aliasDef);
        DefDatabase<RepairFixtureDef>.Add(invalidRules);
        var package = new DefInjectionPackage(typeof(RepairFixtureDef));
        void Add(string key, string value, bool injected = false, string normalized = null) =>
            package.injections[key] = new DefInjectionPackage.DefInjection {
                path = key, injection = value, injected = injected, fileSource = source, normalizedPath = normalized };
        Add("NativeRepair.label", "未套用的正常翻譯");
        Add("NativeRepair.description", " \n未套用的描述\n保留內部換行。 \t");
        Add("NativeAlias.oldStages.stable.label", "中間欄位別名的付費譯文");
        Add("NativeInvalidRules.logRules.rulesStrings", "UNUSED");
        Add("NativeRepair.oldStages.stable.label", "已存在的相同譯文");
        Add("NativeRepair.stages.0.label", "已存在的相同譯文", true, "NativeRepair.stages.0.label");
        Add("NativeRepair.menuText", "已選定的譯文", true, "NativeRepair.menuText");
        Add("NativeRepair.oldMenuText", "衝突候選不能偷偷覆蓋");
        Add("NativeRepair.logRules.rulesStrings", "r_test->原生單字串規則");
        Add("NativeRepair.category", "不能寫進列舉");
        Add("NativeRepair.stages.99.label", "不能猜的索引");
        Add("NativeMissing.label", "暫停使用的譯文");
        package.loadErrors.Add("original native diagnostic");
        language.defInjections.Add(package);
        var previousErrors = language.loadErrors;
        language.loadErrors = new List<string> {
            "Duplicate keyed translation key: NativeConflict in language ChineseTraditional",
            "Error loading language from (fixture): dir UnavailableFixtureDef doesn't correspond to any def type. Skipping..."
        };
        try {
            var run = new TranslationRepairRun();
            var worker = Activator.CreateInstance(workerType, new object[] { language, run });
            Check(def.label != "未套用的正常翻譯" && package.injections.ContainsKey("NativeRepair.oldMenuText"),
                "constructing a repair check does not modify native translations before the main-thread repair ticks");
            var tick = workerType.GetMethod("Tick");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!(bool)workerType.GetProperty("Done").GetValue(worker)) {
                tick.Invoke(worker, null);
                if (DateTime.UtcNow > deadline) throw new Exception("Native repair did not finish");
                Thread.Sleep(1);
            }
            Check((int)workerType.GetProperty("Processed").GetValue(worker) == (int)workerType.GetProperty("Total").GetValue(worker),
                "native repair progress includes every failed candidate and general load diagnostic");
            Check(def.label != "未套用的正常翻譯" && def.menuText == "已選定的譯文" && def.category == ThingCategory.Item,
                "third-party diagnostics do not apply translations or replace selected conflicts");
            Check(def.description != "未套用的描述\n保留內部換行。" && aliasDef.stages[0].label == "English",
                "third-party descriptions and aliases remain unchanged in memory");
            Check(package.injections["NativeRepair.logRules.rulesStrings"].injection == "r_test->原生單字串規則" &&
                !package.injections["NativeRepair.logRules.rulesStrings"].IsFullListInjection &&
                package.injections.ContainsKey("NativeAlias.oldStages.stable.label"),
                "diagnostics do not convert third-party scalars or rename keys");
            Check(original.SequenceEqual(File.ReadAllBytes(source)) && package.loadErrors.SequenceEqual(new[] { "original native diagnostic" }) && language.loadErrors.Count == 2,
                "manual diagnostics preserve Workshop bytes and upstream errors");
            foreach (var record in run.Snapshot()) scanner.GetMethod("VerifyRepairRecord", flags).Invoke(null, new object[] { run, record });
            Check(run.Verified == 0 && run.Unresolved > 0,
                "diagnostic-only third-party candidates never receive successful repair credit");
            var journal = (TranslationRepairJournal)scanner.GetProperty("RepairJournal", flags).GetValue(null);
            Check(journal.ForSource(source).Any(r => r.Translation == "衝突候選不能偷偷覆蓋"),
                "diagnostic journal retains original conflicting translation");
            TestNativePreservationFailure(language, scanner, source);
            TestKnownReportRuleLists();
        }
        finally { language.defInjections.Remove(package); language.loadErrors = previousErrors; }
    }

    static void TestNativePreservationFailure(LoadedLanguage language, Type scanner, string source)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var def = new RepairFixtureDef { defName = "NativePreservationFailure" };
        DefDatabase<RepairFixtureDef>.Add(def);
        var package = new DefInjectionPackage(typeof(RepairFixtureDef));
        package.injections["NativePreservationFailure.label"] = new DefInjectionPackage.DefInjection {
            path = "NativePreservationFailure.label", injection = "保存失敗時不能套用", fileSource = source };
        language.defInjections.Add(package);
        string journalFile = Path.Combine(selectionPack, "SelfRepair", "checkpoints");
        string saved = journalFile + ".failure-test";
        Directory.Move(journalFile, saved);
        File.WriteAllText(journalFile, "block durable checkpoint");
        try {
            var workerType = scanner.GetNestedType("NativeTranslationRepairWork", BindingFlags.NonPublic);
            var run = new TranslationRepairRun();
            var worker = Activator.CreateInstance(workerType, new object[] { language, run });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            Exception failure = null;
            while (DateTime.UtcNow < deadline && failure == null) {
                try { workerType.GetMethod("Tick").Invoke(worker, null); }
                catch (TargetInvocationException ex) { failure = ex.InnerException; }
                Thread.Sleep(1);
            }
            Check(failure != null && !(bool)workerType.GetProperty("Done").GetValue(worker) &&
                def.label != "保存失敗時不能套用" && !package.injections["NativePreservationFailure.label"].injected && run.Verified == 0,
                "journal IO failure stops native memory changes and makes no success claim");
        }
        finally {
            File.Delete(journalFile); Directory.Move(saved, journalFile);
            language.defInjections.Remove(package);
            scanner.GetMethod("FlushSelfRepair", flags).Invoke(null, null);
        }
    }

    static void TestKnownReportRuleLists()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var hooks = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.TranslationSelfRepairHooks");
        string path = Path.Combine(temporary, "Workshop", "rules.xml");
        string original = "<LanguageData><SomeDef.logRules.rulesStrings>r_test->原文</SomeDef.logRules.rulesStrings></LanguageData>";
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, original);
        var overlay = hooks.GetMethod("RepairReadContent", flags);
        Check((string)overlay.Invoke(null, new object[] { path, original }) == original &&
            overlay.Invoke(null, new object[] { path, null }) == null && File.ReadAllText(path) == original,
            "automatic read hook preserves third-party scalar rules and null preload exactly");
    }
}
