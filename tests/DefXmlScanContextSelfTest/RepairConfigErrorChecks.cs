using System.Reflection;
using AutoTranslator_Core;
using Verse;

internal static partial class Suite
{
    static void TestRepairDescriptionErrors(string languageRoot, LoadedLanguage language)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        var def = new RepairFixtureDef { defName = "ConfigGuard", description = "Original description", label = "label" };
        DefDatabase<RepairFixtureDef>.Add(def);
        HarmonyLib.AccessTools.Field(typeof(Verse.Grammar.RulePack), "rulesStrings")
            .SetValue(def.logRules, new List<string> { "rule->original" });
        const string paid = " \n保留內部換行\n與文字。\t ";
        const string safe = "保留內部換行\n與文字。";
        string directory = Path.Combine(languageRoot, "DefInjected", nameof(RepairFixtureDef));
        string file = Path.Combine(directory, "config-guard_AutoTranslated.xml");
        File.WriteAllText(file, "<LanguageData><ConfigGuard.description>" + paid + "</ConfigGuard.description></LanguageData>");
        var startup = new DefInjectionPackage(typeof(RepairFixtureDef));
        startup.AddDataFromFile(new NumericXmlFile(file), out bool parseError, File.ReadAllText(file));
        startup.InjectIntoDefs(true);
        Check(!parseError && def.description == "Original description" && startup.injections.Count == 0,
            "native startup withholds owned descriptions until post-load validation");
        Check(File.ReadAllText(file).Contains(paid), "startup prevention leaves the paid source XML untouched");

        var workType = scanner.GetNestedType("MainMenuRepairWork", BindingFlags.NonPublic);
        var run = new TranslationRepairRun();
        var work = Activator.CreateInstance(workType, new object[] { languageRoot, run });
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!(bool)workType.GetProperty("Done").GetValue(work))
        {
            workType.GetMethod("Tick").Invoke(work, null);
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw new Exception("Config prevention repair timed out");
            Thread.Sleep(1);
        }
        Check(AutoTranslatorScanner.LoadXmlFileToDict(file)["ConfigGuard.description"] == safe,
            "manual repair persists a description that remains valid at the next game startup");
        var records = ((TranslationRepairJournal)scanner.GetProperty("RepairJournal", flags).GetValue(null)).ForSource(file);
        Check(records.Any(r => r.OriginalKey == "ConfigGuard.description" && r.Translation == paid),
            "description normalization preserves the complete original paid text in the repair journal");
        var record = run.Snapshot().Single(r => r.SourceFile == file && r.OriginalKey == "ConfigGuard.description");
        startup.injections["ConfigGuard.description"] = new DefInjectionPackage.DefInjection {
            path = "ConfigGuard.description", injection = safe, fileSource = file };
        scanner.Assembly.GetType("AutoTranslator_Core.TranslationConfigGuard").GetMethod("ApplyValidated", flags).Invoke(null, new object[] { startup });
        language.defInjections.Add(startup);
        try
        {
            scanner.GetMethod("VerifyRepairRecord", flags).Invoke(null, new object[] { run, record });
            Check(run.Outcome(record) == "verified", "description repair verifies the normalized live text against its preserved original");
        }
        finally { language.defInjections.Remove(startup); }

        string writerFile = Path.Combine(directory, "config-writer_AutoTranslated.xml");
        AutoTranslatorScanner.SaveXml(writerFile, new Dictionary<string, string> {
            ["ConfigGuard.description"] = paid, ["ConfigGuard.menuText"] = " 空白有意義 ",
            ["ConfigGuard.logRules.rulesStrings.0"] = "rule-> 尾端有意義 "
        });
        var saved = AutoTranslatorScanner.LoadXmlFileToDict(writerFile);
        Check(saved["ConfigGuard.description"] == safe && saved["ConfigGuard.menuText"] == " 空白有意義 " &&
            saved["ConfigGuard.logRules.rulesStrings.0"] == "rule-> 尾端有意義 ",
            "translation output prevents description config errors without trimming arbitrary text fields");

        // A fresh memory payload must enforce the same rule even before any XML rewrite.
        object New(string name) => Activator.CreateInstance(scanner.GetNestedType(name, BindingFlags.NonPublic), true);
        var payload = New("MemoryDropPayload");
        var bucket = New("MemoryDropDefTypePayload");
        bucket.GetType().GetField("DefTypeName").SetValue(bucket, nameof(RepairFixtureDef));
        ((Dictionary<string, string>)bucket.GetType().GetField("Data").GetValue(bucket))["ConfigGuard.description"] = paid;
        ((System.Collections.IList)payload.GetType().GetField("DefsByType").GetValue(payload)).Add(bucket);
        var state = New("MemoryDropApplyState");
        state.GetType().GetField("Payload").SetValue(state, payload);
        var isolatedLanguage = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
        isolatedLanguage.defInjections = new List<DefInjectionPackage>();
        scanner.GetMethod("ApplyDefBatch", flags).Invoke(null, new[] { state, isolatedLanguage });
        Check(isolatedLanguage.defInjections.Single().injections["ConfigGuard.description"].injection == safe,
            "memory-drop payload prevents description whitespace errors before native application");
        Check(def.category == ThingCategory.Item && !def.ignoreConfigErrors,
            "config prevention neither changes game structures nor disables native config validation");
        var blank = new DefInjectionPackage(typeof(RepairFixtureDef));
        blank.injections["ConfigGuard.description"] = new DefInjectionPackage.DefInjection {
            path = "ConfigGuard.description", injection = " \n\t ", fileSource = file };
        blank.InjectIntoDefs(true);
        Check(def.description == safe && !blank.injections.ContainsKey("ConfigGuard.description"),
            "a whitespace-only description is preserved outside injection instead of creating an empty-description config error");
        File.Delete(file); File.Delete(writerFile);
    }
}
