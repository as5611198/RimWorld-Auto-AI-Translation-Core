using System.Reflection;
using AutoTranslator_Core;
using Verse;

public sealed class ConfigGuardFixtureDef : Def
{
    public string text = "safe";
    public RepairFixtureNode nested = new RepairFixtureNode();
    [TranslationCanChangeCount] public List<string> values = new List<string> { "safe" };
    public bool existingError;
    public bool throwAlways;
    public int calls;
    public int cacheClears;
    public bool cachedValidation;
    private string cachedText;
    public override void ClearCachedData() { cacheClears++; cachedText = null; base.ClearCachedData(); }
    public override IEnumerable<string> ConfigErrors()
    {
        calls++;
        if (throwAlways || text == "throw") throw new InvalidOperationException("fixture validator unavailable");
        if (existingError) yield return "pre-existing config error";
        if (cachedText == null) cachedText = text;
        if ((cachedValidation ? cachedText : text) == "bad" || nested.label == "bad" || values.Contains("bad")) yield return "translated text violates config";
    }
}

public sealed class DerivedConfigGuardThing : ThingDef
{
    public override IEnumerable<string> ConfigErrors()
    {
        if (description.StartsWith("新名稱", StringComparison.Ordinal)) yield return "derived normalization violates config";
    }
    public override void ClearCachedData() { }
}

internal static partial class Suite
{
    static void TestTranslationConfigGuard(string source)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var scanner = typeof(AutoTranslatorScanner);
        var guard = scanner.Assembly.GetType("AutoTranslator_Core.TranslationConfigGuard");
        var apply = guard.GetMethod("ApplyValidated", flags);
        var def = new ConfigGuardFixtureDef { defName = "ConfigGuard", existingError = true, throwAlways = true };
        DefDatabase<ConfigGuardFixtureDef>.Add(def);
        try
        {
            foreach (string text in new[] { "bad", "throw", "valid translation" })
            {
                var package = new DefInjectionPackage(typeof(ConfigGuardFixtureDef));
                var entry = new DefInjectionPackage.DefInjection { path = "ConfigGuard.text", injection = text, fileSource = source };
                package.injections[entry.path] = entry;
                apply.Invoke(null, new object[] { package });
                Check(entry.injected && def.text == text && def.calls == 0, "bounded guard reads back text without invoking arbitrary ConfigErrors: " + text);
            }
            var full = new DefInjectionPackage(typeof(ConfigGuardFixtureDef));
            var list = new DefInjectionPackage.DefInjection { path = "ConfigGuard.values", fullListInjection = new List<string> { "translated", "second" }, fileSource = source };
            full.injections[list.path] = list; apply.Invoke(null, new object[] { full });
            Check(list.injected && def.values.SequenceEqual(list.fullListInjection), "full-list route has actual value readback");
            var shared = (ThingDef)Uninitialized(typeof(ThingDef)); shared.defName = "SharedTarget"; shared.label = "Original";
            var root = new RepairFixtureDef { defName = "SharedGuard", reference = shared };
            Check(!DefTranslationPathValidator.Validate(root, "SharedGuard.reference.label", null).Allowed && shared.label == "Original", "shared Def reference is not traversed");
            var setterPatches = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(typeof(DefInjectionPackage), "SetDefFieldAtPath"));
            Check(setterPatches == null || !setterPatches.Owners.Contains("MingYang.AutoTranslation.SelfRepair"), "guard installs no global native setter patch");
            TestScopedNativeFailures(source);
            TestMemoryConfigRollback(source);
            TestManualConfigRollback(source);
        }
        finally { DefDatabase<ConfigGuardFixtureDef>.Clear(); }
    }

    static void ThrowNativeCandidate(Type defType, string path, object value)
    {
        if (!(value is string text) || !text.StartsWith("native failure ", StringComparison.Ordinal)) return;
        ((ConfigGuardFixtureDef)GenDefDatabase.GetDefSilentFail(defType, path.Split('.')[0], false)).text = text;
        if (text.EndsWith("handle", StringComparison.Ordinal)) throw new InvalidOperationException("None of the list elements have a handle named CompPsionicManager.");
        if (text.EndsWith("index", StringComparison.Ordinal)) throw new IndexOutOfRangeException("Index out of bounds (max index is 0)");
        throw new NullReferenceException("native candidate null fixture");
    }

    static void TestScopedNativeFailures(string source)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var guard = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.TranslationConfigGuard");
        var apply = guard.GetMethod("ApplyValidated", flags);
        var h = new HarmonyLib.Harmony("atc.test.native-setter-failure");
        h.Patch(HarmonyLib.AccessTools.Method(typeof(DefInjectionPackage), "SetDefFieldAtPath"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(Suite), nameof(ThrowNativeCandidate)));
        try
        {
            foreach (string kind in new[] { "handle", "index", "null" })
            {
                var def = new ConfigGuardFixtureDef { defName = "NativeFailure_" + kind };
                DefDatabase<ConfigGuardFixtureDef>.Add(def);
                var package = new DefInjectionPackage(typeof(ConfigGuardFixtureDef));
                var bad = new DefInjectionPackage.DefInjection { path = def.defName + ".text", injection = "native failure " + kind, fileSource = source };
                var good = new DefInjectionPackage.DefInjection { path = def.defName + ".nested.label", injection = "surviving translation", fileSource = source };
                package.injections[bad.path] = bad;
                package.injections[good.path] = good;
                apply.Invoke(null, new object[] { package });
                Check(!bad.injected && def.text == "safe" && good.injected && def.nested.label == good.injection,
                    "native " + kind + " exception restores the old value and allows remaining candidates to continue");
                var journal = (TranslationRepairJournal)typeof(AutoTranslatorScanner).GetProperty("RepairJournal", flags).GetValue(null);
                Check(journal.Snapshot().Any(r => r.OriginalKey == bad.path && r.Translation == bad.injection && r.SourceFile == source && r.Reason == "native_injection_failed"),
                    "native " + kind + " exception preserves the original candidate and source for review");
                Check(!(bool)guard.GetProperty("ValidatedScope", flags).GetValue(null), "native " + kind + " failure releases the guarded scope");
            }
        }
        finally { h.UnpatchAll(h.Id); }
    }

    static void TestManualConfigRollback(string ownedSource)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        var def = new ConfigGuardFixtureDef { defName = "ManualConfigGuard" };
        DefDatabase<ConfigGuardFixtureDef>.Add(def);
        string source = Path.Combine(temporary, "ManualConfigSource.xml");
        File.WriteAllText(source, "<LanguageData />");
        var package = new DefInjectionPackage(typeof(ConfigGuardFixtureDef));
        var entry = new DefInjectionPackage.DefInjection { path = def.defName + ".text", injection = "bad", fileSource = source };
        package.injections[entry.path] = entry;
        var language = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
        language.defInjections = new List<DefInjectionPackage> { package };
        language.loadErrors = new List<string>();
        var run = new TranslationRepairRun();
        var workerType = scanner.GetNestedType("NativeTranslationRepairWork", BindingFlags.NonPublic);
        var worker = Activator.CreateInstance(workerType, new object[] { language, run });
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!(bool)workerType.GetProperty("Done").GetValue(worker))
        {
            workerType.GetMethod("Tick").Invoke(worker, null);
            if (DateTime.UtcNow > deadline) throw new Exception("manual config guard timed out");
            Thread.Sleep(1);
        }
        foreach (var record in run.Snapshot()) scanner.GetMethod("VerifyRepairRecord", flags).Invoke(null, new object[] { run, record });
        Check(def.text == "safe" && !entry.injected && ReferenceEquals(package.injections[entry.path], entry) && run.Verified == 0 && run.Unresolved > 0,
            "third-party manual diagnostics never mutate effective contents or claim repaired success");
        Check(File.ReadAllText(source) == "<LanguageData />", "manual config rejection leaves the third-party source untouched");
    }

    static void TestMemoryConfigRollback(string source)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        var def = new ConfigGuardFixtureDef { defName = "MemoryConfigGuard" };
        DefDatabase<ConfigGuardFixtureDef>.Add(def);
        var language = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
        var package = new DefInjectionPackage(typeof(ConfigGuardFixtureDef));
        var original = new DefInjectionPackage.DefInjection { path = def.defName + ".text", injection = "safe", injected = true, fileSource = "original.xml" };
        package.injections[original.path] = original;
        language.defInjections = new List<DefInjectionPackage> { package };
        language.loadErrors = new List<string>();
        object New(string name) => Activator.CreateInstance(scanner.GetNestedType(name, BindingFlags.NonPublic), true);
        var payload = New("MemoryDropPayload");
        var bucket = New("MemoryDropDefTypePayload");
        bucket.GetType().GetField("DefTypeName").SetValue(bucket, nameof(ConfigGuardFixtureDef));
        ((Dictionary<string, string>)bucket.GetType().GetField("Data").GetValue(bucket))[original.path] = "native failure handle";
        ((Dictionary<string, string>)bucket.GetType().GetField("Sources").GetValue(bucket))[original.path] = source;
        ((System.Collections.IList)payload.GetType().GetField("DefsByType").GetValue(payload)).Add(bucket);
        var state = New("MemoryDropApplyState");
        state.GetType().GetField("Payload").SetValue(state, payload);
        var previous = LanguageDatabase.activeLanguage;
        var h = new HarmonyLib.Harmony("atc.test.memory-native-failure");
        h.Patch(HarmonyLib.AccessTools.Method(typeof(DefInjectionPackage), "SetDefFieldAtPath"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(Suite), nameof(ThrowNativeCandidate)));
        try
        {
            LanguageDatabase.activeLanguage = language;
            while ((bool)scanner.GetMethod("ApplyDefBatch", flags).Invoke(null, new[] { state, language })) { }
            while ((bool)scanner.GetMethod("ApplyNativeMemoryBatch", flags).Invoke(null, new[] { state })) { }
            scanner.GetMethod("PrepareMemoryDropFinish", flags).Invoke(null, new object[] { state, true });
            Check(def.text == "safe" && ReferenceEquals(package.injections[original.path], original) && original.injected &&
                (int)state.GetType().GetField("InjectedDefs").GetValue(state) == 0 && (int)state.GetType().GetField("FailedDefs").GetValue(state) == 1,
                "actual memory application rolls back runtime text and restores the selected native dictionary entry and flags");
            scanner.GetMethod("FlushSelfRepair", flags).Invoke(null, null);
            var journal = (TranslationRepairJournal)scanner.GetProperty("RepairJournal", flags).GetValue(null);
            var rejected = journal.Snapshot().Last(r => r.OriginalKey == original.path && r.Reason == "native_injection_failed");
            var persisted = new TranslationRepairJournal(Path.Combine(selectionPack, "SelfRepair"));
            Check(persisted.Snapshot().Any(r => r.OriginalKey == original.path && r.Reason == "native_injection_failed"),
                "a fresh journal reload retains the native rejection and original candidate");
            var run = new TranslationRepairRun(); run.Observe(rejected);
            scanner.GetMethod("VerifyRepairRecord", flags).Invoke(null, new object[] { run, rejected });
            Check(run.Verified == 0 && run.Unresolved == 1, "final live verification cannot promote a rolled-back config rejection to repaired success");
        }
        finally { h.UnpatchAll(h.Id); LanguageDatabase.activeLanguage = previous; }
    }
}
