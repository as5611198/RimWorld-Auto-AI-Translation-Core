using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static string pausedRepairPath;
    static ManualResetEventSlim repairWriteEntered, releaseRepairWrite;
    static void PauseRepairWrite(string path)
    {
        if (path != pausedRepairPath && Path.GetDirectoryName(path) != pausedRepairPath) return;
        repairWriteEntered.Set();
        if (!releaseRepairWrite.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("fixture write was not released");
    }

    static void TestRepairPersistence(Harmony hooks)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        string root = Path.Combine(temporary, "journal-concurrency");
        var journal = new TranslationRepairJournal(root);
        journal.Record(new TranslationRepairRecord { SourceFile = "first.xml", Translation = "first paid value" });
        pausedRepairPath = Path.Combine(root, "records.json");
        repairWriteEntered = new ManualResetEventSlim(); releaseRepairWrite = new ManualResetEventSlim();
        hooks.Patch(typeof(TranslationRepairJournal).GetMethod("AtomicText"), prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(PauseRepairWrite), flags)));
        var writer = Task.Run(() => journal.Flush());
        bool responsive = false;
        Task reader = null;
        try
        {
            if (!repairWriteEntered.Wait(TimeSpan.FromSeconds(10))) throw new Exception("fixture did not reach journal write");
            reader = Task.Run(() => {
                journal.Record(new TranslationRepairRecord { SourceFile = "second.xml", Translation = "concurrent paid value" });
                journal.ForSource("first.xml"); journal.Snapshot();
            });
            responsive = reader.Wait(TimeSpan.FromSeconds(1));
        }
        finally { releaseRepairWrite.Set(); writer.GetAwaiter().GetResult(); reader?.GetAwaiter().GetResult(); pausedRepairPath = null; }
        Check(responsive, "slow journal persistence never blocks observations or source lookup on the main thread");
        journal.Flush();
        Check(new TranslationRepairJournal(root).Snapshot().Count == 2, "observations added during a flush stay dirty and survive a later restart");
        TestRepairGateResponsiveness();
        TestMemoryApplyTimeBudget(hooks);
        TestBackgroundMemoryCheckpoint();
    }

    static void TestRepairGateResponsiveness()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        var journal = (TranslationRepairJournal)scanner.GetProperty("RepairJournal", flags).GetValue(null);
        journal.Record(new TranslationRepairRecord { SourceFile = "gate.xml", Translation = "gate paid value" });
        pausedRepairPath = Path.Combine(selectionPack, "SelfRepair", "checkpoints");
        repairWriteEntered.Reset(); releaseRepairWrite.Reset();
        var writer = Task.Run(() => scanner.GetMethod("FlushSelfRepair", flags).Invoke(null, null));
        Task reader = null; bool responsive = false;
        try
        {
            if (!repairWriteEntered.Wait(TimeSpan.FromSeconds(10))) throw new Exception("checkpoint fixture did not reach persistence");
            reader = Task.Run(() => scanner.GetProperty("RepairJournal", flags).GetValue(null));
            responsive = reader.Wait(TimeSpan.FromSeconds(1));
        }
        finally { releaseRepairWrite.Set(); writer.GetAwaiter().GetResult(); reader?.GetAwaiter().GetResult(); pausedRepairPath = null; }
        Check(responsive, "background persistence does not hold the scanner's shared validation gate");
    }

    static void SlowMemoryValidation(string key)
    {
        if (key == "MemoryTime.label") Thread.Sleep(25);
    }

    static void TestMemoryApplyTimeBudget(Harmony hooks)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        DefDatabase<RepairFixtureDef>.Add(new RepairFixtureDef { defName = "MemoryTime" });
        hooks.Patch(scanner.GetMethod("ValidateLoadedDefTranslationCore", flags), prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(SlowMemoryValidation), flags)));
        object New(string name) => Activator.CreateInstance(scanner.GetNestedType(name, BindingFlags.NonPublic), true);
        void Set(object o, string name, object value) => o.GetType().GetField(name).SetValue(o, value);
        var payload = New("MemoryDropPayload");
        var bucket = New("MemoryDropDefTypePayload");
        Set(bucket, "DefTypeName", nameof(RepairFixtureDef));
        var values = (Dictionary<string, string>)bucket.GetType().GetField("Data").GetValue(bucket);
        values["MemoryTime.label"] = "one"; values["MemoryTime.menuText"] = "two"; values["MemoryTime.words.0"] = "three";
        ((System.Collections.IList)payload.GetType().GetField("DefsByType").GetValue(payload)).Add(bucket);
        var state = New("MemoryDropApplyState"); Set(state, "Payload", payload);
        var lang = (Verse.LoadedLanguage)Uninitialized(typeof(Verse.LoadedLanguage));
        lang.defInjections = new List<Verse.DefInjectionPackage>();
        bool remaining;
        freezeFixtureFrame = true; fixtureFrame++;
        try { remaining = (bool)scanner.GetMethod("ApplyDefBatch", flags).Invoke(null, new[] { state, lang }); }
        finally { freezeFixtureFrame = false; }
        int submitted = (int)state.GetType().GetField("SubmittedDefs").GetValue(state);
        Check(remaining && submitted == 1, "memory application yields after a slow entry instead of running its entire count budget");
    }

    static void TestBackgroundMemoryCheckpoint()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        var stateType = scanner.GetNestedType("MemoryDropApplyState", BindingFlags.NonPublic);
        Check(stateType.GetField("Preservation") != null, "memory-drop completion has an asynchronous persistence stage");
        var journal = (TranslationRepairJournal)scanner.GetProperty("RepairJournal", flags).GetValue(null);
        journal.Record(new TranslationRepairRecord { SourceFile = "memory-checkpoint.xml", Translation = "paid memory checkpoint" });
        var payload = Activator.CreateInstance(scanner.GetNestedType("MemoryDropPayload", BindingFlags.NonPublic), true);
        payload.GetType().GetField("KeyedOnly").SetValue(payload, true);
        payload.GetType().GetField("TargetFolder").SetValue(payload, AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang));
        pausedRepairPath = Path.Combine(selectionPack, "SelfRepair", "checkpoints");
        repairWriteEntered.Reset(); releaseRepairWrite.Reset();
        Task persistence = null;
        var previous = scanner.GetField("_activeMemoryDropApply", flags).GetValue(null);
        try
        {
            scanner.GetMethod("BeginMemoryDropApply", flags).Invoke(null, new[] { payload });
            scanner.GetMethod("ContinueMemoryDropApply", flags).Invoke(null, null);
            var state = scanner.GetField("_activeMemoryDropApply", flags).GetValue(null);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (state != null && persistence == null && DateTime.UtcNow < deadline)
            {
                scanner.GetMethod("ContinueMemoryDropApply", flags).Invoke(null, null);
                persistence = (Task)stateType.GetField("Preservation").GetValue(state);
                Thread.Sleep(1);
            }
            Check(persistence != null && repairWriteEntered.Wait(TimeSpan.FromSeconds(10)), "memory completion persists new records in the background");
            scanner.GetMethod("ContinueMemoryDropApply", flags).Invoke(null, null);
            Check(ReferenceEquals(state, scanner.GetField("_activeMemoryDropApply", flags).GetValue(null)), "blocked persistence keeps memory-drop completion pending without blocking the pump");
            releaseRepairWrite.Set(); persistence.GetAwaiter().GetResult();
            scanner.GetMethod("ContinueMemoryDropApply", flags).Invoke(null, null);
            Check(scanner.GetField("_activeMemoryDropApply", flags).GetValue(null) == null, "memory completion advances only after persistence succeeds");
        }
        finally { releaseRepairWrite.Set(); persistence?.GetAwaiter().GetResult(); pausedRepairPath = null; scanner.GetField("_activeMemoryDropApply", flags).SetValue(null, previous); }
    }
}
