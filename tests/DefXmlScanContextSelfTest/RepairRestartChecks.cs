using System.Reflection;
using AutoTranslator_Core;
using Verse;

internal static partial class Suite
{
    internal static void PrintRepairEnvironment(string root)
    {
        temporary = Path.GetFullPath(root);
        SetRunning(Pack("restart.fixture", "", 0, ""));
        Console.WriteLine(typeof(AutoTranslatorScanner).GetProperty("SelfRepairEnvironment", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null));
    }

    static void TestRepairRestartPersistence(string languageRoot)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var scanner = typeof(AutoTranslatorScanner);
        string EnvironmentId() => (string)scanner.GetProperty("SelfRepairEnvironment", flags).GetValue(null);
        string environment = EnvironmentId();
        AutoTranslatorScanner.NotifyRawDefSourcesChanged();

        string source = Path.Combine(languageRoot, "DefInjected", nameof(RepairFixtureDef), "restart_AutoTranslated.xml");
        File.WriteAllText(source, "<LanguageData><RepairFixture.menuText>保留的原生失敗譯文</RepairFixture.menuText></LanguageData>");
        string key = "RepairFixture.menuText", value = "保留的原生失敗譯文";
        var record = (TranslationRepairRecord)scanner.GetMethod("RecordDefRepair", flags).Invoke(null, new object[] {
            nameof(RepairFixtureDef), key, value, source,
            new DefTranslationPathResult { Path = key, Reason = "native_injection_failed", Evidence = "native rejected after both phases" }, "native-result", null, null });
        var journal = (TranslationRepairJournal)scanner.GetProperty("RepairJournal", flags).GetValue(null);
        journal.Checkpoint();
        // Recreate the store and all transient rejection caches, as in a fresh game.
        scanner.GetField("repairJournal", flags).SetValue(null, null);
        Task.Run(() => scanner.GetProperty("RepairJournal", flags).GetValue(null)).GetAwaiter().GetResult();
        AutoTranslatorScanner.NotifyRawDefSourcesChanged();
        bool Failed(string text) => (bool)scanner.GetMethod("IsFailedNativeCandidate", flags).Invoke(null,
            new object[] { typeof(RepairFixtureDef), key, text, source });
        Check(!Failed(value), "historical native rejection does not become a permanent deny after journal reload");
        Check(EnvironmentId() == environment, "unchanged content keeps its persisted repair identity after runtime cache invalidation");
        var recovered = new Dictionary<string, string>();
        scanner.GetMethod("RecoverPreservedTranslations", flags).Invoke(null, new object[] { source, recovered });
        Check(recovered.TryGetValue(key, out string retry) && retry == value, "historical failure retains its original text for fresh structural validation");
        Check(!Failed("已更新的譯文"), "a changed candidate value is eligible for actual injection again");
        var package = new DefInjectionPackage(typeof(RepairFixtureDef));
        package.AddDataFromFile(new NumericXmlFile(source), out bool xmlError, File.ReadAllText(source));
        string before = DefDatabase<RepairFixtureDef>.GetNamed("RepairFixture").menuText;
        package.InjectIntoDefs(true);
        Check(!xmlError && package.loadErrors.Count == 0 && !package.injections.ContainsKey(key) &&
            DefDatabase<RepairFixtureDef>.GetNamed("RepairFixture").menuText == before,
            "native startup holds a known rejected candidate before retry while retaining the live fallback");
        Check(((TranslationRepairJournal)scanner.GetProperty("RepairJournal", flags).GetValue(null)).ForSource(source)
            .Any(r => r.Translation == value && r.Reason == "native_injection_failed"),
            "skipping an automatic retry preserves the paid value and failure evidence");
        var running = LoadedModManager.RunningModsListForReading.First();
        int previousOrder = running.loadOrder;
        try {
            running.loadOrder++;
            AutoTranslatorScanner.NotifyRawDefSourcesChanged();
            Check(!Failed(value), "a changed mod load environment permits revalidation of a previously rejected candidate");
        }
        finally { running.loadOrder = previousOrder; AutoTranslatorScanner.NotifyRawDefSourcesChanged(); }
    }
}
