using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

public sealed class RepairFixtureDef : Def
{
    [LoadAlias("oldMenuText")] public string menuText = "English menu text";
    [LoadAlias("oldStages")] public List<RepairFixtureNode> stages = new List<RepairFixtureNode> { new RepairFixtureNode() };
    public Verse.Grammar.RulePack logRules = new Verse.Grammar.RulePack();
    public List<string> words = new List<string> { "old" };
    [TranslationCanChangeCount] public List<string> expandable = new List<string> { "old" };
    [NoTranslate] public string forbidden = "no";
    [Unsaved] public string unsaved = "no";
    public ThingCategory category = ThingCategory.Item;
    public ThingDef reference;
    public RepairFixtureNode optional;
    public Dictionary<IntVec3, string> coordinates = new Dictionary<IntVec3, string>();
    public RepairThrowingContainer brokenContainer = new RepairThrowingContainer();
}
public sealed class RepairThrowingContainer
{
    public int Count => throw new InvalidOperationException("third-party Count unavailable");
    public string this[int index] { get => "text"; set { } }
}
public sealed class RepairFixtureNode
{
    [TranslationHandle] public string name = "stable";
    [LoadAlias("oldLabel")] public string label = "English";
}

internal static partial class Suite
{
    static int repairNetworkCalls;
    static bool RepairLoadedFixture(ref bool __result) { __result = true; return false; }
    static void ForbidRepairNetwork() { repairNetworkCalls++; throw new Exception("Self-repair attempted a paid/network request"); }
    static void TestSelfRepair(bool configGuardOnly = false)
    {
        fixtureGameThread = Thread.CurrentThread.ManagedThreadId;
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var guardType = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.TranslationConfigGuard");
        var installedProperty = guardType.GetProperty("IsInstalled", flags);
        Check(installedProperty != null && !(bool)installedProperty.GetValue(null),
            "Def setter guard is deferred until after the initial native XML load");
        var runtimePack = Pack("fixture", "SelfRepairSource", 0, "SelfRepairSource");
        runtimePack.assemblies.loadedAssemblies.Add(typeof(RepairFixtureDef).Assembly);
        SetRunning(runtimePack);
        GenTypes.ClearCache();
        var scanner = typeof(AutoTranslatorScanner);
        var validator = scanner.GetMethod("ValidateLoadedDefTranslation", flags);
        var def = new RepairFixtureDef { defName = "RepairFixture", label = "English" };
        DefDatabase<RepairFixtureDef>.Add(def);
        DefTranslationPathResult Validate(string key) => (DefTranslationPathResult)validator.Invoke(null, new object[] { typeof(RepairFixtureDef), key, false });
        Check(Validate("RepairFixture.stages.stable.label").Allowed, "real Verse TranslationHandle resolves stable list item");
        Check(Validate("RepairFixture.stages.stable-1.label").Reason == "missing_handle", "real handle occurrence overflow is retained");
        Check(Validate("RepairFixture.stages.CompPsionicManager.label").Reason == "missing_handle", "absent handle never becomes guessed index");
        Check(Validate("RepairFixture.stages.1.label").Reason == "index_out_of_bounds", "real list overflow is caught before indexer");
        Check(Validate("RepairFixture.coordinates.0").Reason == "unsupported_container", "IntVec3 dictionary cannot receive integer index");
        Check(Validate("RepairFixture.brokenContainer.0").Reason == "unsupported_container",
            "unknown indexers are rejected without executing third-party getters");
        Check(Validate("RepairFixture.forbidden").Reason == "untranslatable_field" && Validate("RepairFixture.unsaved").Reason == "untranslatable_field", "native NoTranslate and Unsaved are respected");
        Check(Validate("RepairFixture.category").Reason == "non_text_field" && Validate("RepairFixture.reference").Reason == "non_text_field", "enum and Def reference are not translation targets");
        Check(Validate("RepairFixture.optional.label").Temporary, "optional null is deferred");
        Check(Validate("RepairFixture.stages.stable.oldLabel").Path == "RepairFixture.stages.stable.label", "explicit LoadAlias migrates without changing paid value");
        Check(Validate("DisabledFixture.label").Reason == "missing_def", "disabled Def is temporary");
        DefDatabase<RepairFixtureDef>.Add(new RepairFixtureDef { defName = "DisabledFixture" });
        Check(Validate("DisabledFixture.label").Allowed, "Def activation is revalidated without permanent invalid cache");

        selectionPack = Path.Combine(temporary, "SelfRepairPack");
        var h = new Harmony("atc.self-repair-test-path");
        h.Patch(scanner.GetMethod("GetLocalPackPath", BindingFlags.Public | BindingFlags.Static),
            prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(FixturePackPath), flags)));
        var settings = AutoTranslatorMod.Settings;
        AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Traditional };
        try
        {
            repairNetworkCalls = 0;
            h.Patch(typeof(AutoTranslatorAPI).GetMethod("TranslateBatchAsync", BindingFlags.Public | BindingFlags.Static),
                prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(ForbidRepairNetwork), flags)));
            h.Patch(typeof(AutoTranslatorAPI).GetMethod("SendJsonRequestAttemptAsync", flags),
                prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(ForbidRepairNetwork), flags)));
            string file = Path.Combine(selectionPack, "Languages", "ChineseTraditional", "DefInjected", "RepairFixtureDef", "fixture_AutoTranslated.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "<LanguageData><RepairFixture.label>已付費譯文</RepairFixture.label><RepairFixture.category>錯誤欄位的付費譯文</RepairFixture.category></LanguageData>");
            TranslationSelfRepairHooks.Install();
            string knownFile = @"E:\SteamLibrary\steamapps\workshop\content\294100\725576127\Languages\ChineseTraditional\DefInjected\ThingDef\Items_AdvancedBodyParts.xml";
            string knownOriginal = File.ReadAllText(knownFile);
            var knownPackage = new DefInjectionPackage(typeof(ThingDef));
            knownPackage.AddDataFromFile(new NumericXmlFile(knownFile), out bool knownError, knownOriginal);
            Check(knownError && File.ReadAllText(knownFile) == knownOriginal,
                "third-party malformed XML stays diagnostic-only and bytes remain unchanged");
            var package = new DefInjectionPackage(typeof(RepairFixtureDef));
            package.AddDataFromFile(new NumericXmlFile(file), out bool parseError, File.ReadAllText(file));
            Check(!parseError && package.injections["RepairFixture.label"].fileSource == file, "native AddData retains complete generated source path");
            package.InjectIntoDefs(true);
            Check(def.label == "English" && package.injections.Count == 0, "native loading withholds owned candidates for verified post-load injection");
            Check(!package.injections.ContainsKey("RepairFixture.category") && package.loadErrors.Count == 0 && def.category == ThingCategory.Item, "native hook holds only owned invalid entry without touching enum");
            package.InjectIntoDefs(true);
            Check(package.loadErrors.Count == 0, "unchanged invalid generated entry is not retried by native injector");
            var journalFile = Path.Combine(selectionPack, "SelfRepair", "records.json");
            string journalRoot = Path.GetDirectoryName(journalFile);
            Check(File.ReadAllText(file).Contains("錯誤欄位的付費譯文"), "withheld candidate remains in original XML before deferred repair");
            var blocked = new DefInjectionPackage(typeof(RepairFixtureDef));
            blocked.injections["RepairFixture.reference"] = new DefInjectionPackage.DefInjection {
                path = "RepairFixture.reference", injection = "不能遺失的新譯文", fileSource = file };
            blocked.InjectIntoDefs(true);
            Check(blocked.injections.Count == 0 && blocked.loadErrors.Count == 0, "owned candidates cannot reach native setter even without a journal checkpoint");

            // Upstream third-party failures must remain visible.
            var external = new DefInjectionPackage(typeof(RepairFixtureDef));
            external.injections["RepairFixture.category"] = new DefInjectionPackage.DefInjection { path = "RepairFixture.category", injection = "bad", fileSource = "third-party.xml" };
            external.InjectIntoDefs(true);
            Check(external.loadErrors.Count > 0 && !external.injections["RepairFixture.category"].injected, "third-party errors remain upstream and are never marked successful");

            var clone = scanner.GetMethod("CloneRuntimeDefInjection", flags);
            var original = new DefInjectionPackage.DefInjection { path = "RepairFixture.label", injection = "baseline", fileSource = "original.xml", replacedString = "English", injected = true };
            var copied = (DefInjectionPackage.DefInjection)clone.Invoke(null, new object[] { original });
            Check(copied.fileSource == "original.xml" && copied.replacedString == "English" && !ReferenceEquals(copied, original), "baseline snapshot retains provenance and replaced text");
            var restored = (DefInjectionPackage.DefInjection)scanner.GetMethod("RestoreRuntimeDefInjection", flags).Invoke(null, new object[] { copied, copied.path, copied.injection });
            Check(restored.fileSource == original.fileSource && !restored.injected && original.injected, "baseline restore preserves source and requests real reinjection");

            var tkeys = (Dictionary<string, string>)typeof(TKeySystem).GetField("tKeyToNormalizedTranslationKey", flags).GetValue(null);
            tkeys["RepairTestKey.label"] = "RepairFixture.label";
            try { Check(Validate("RepairTestKey.label").Path == "RepairFixture.label", "native TKey mapping deterministically reuses existing value"); }
            finally { tkeys.Remove("RepairTestKey.label"); }
            var fullList = scanner.GetMethod("ValidateLoadedDefList", flags);
            Check(((DefTranslationPathResult)fullList.Invoke(null, new object[] { typeof(RepairFixtureDef), "RepairFixture.expandable" })).Allowed, "TranslationCanChangeCount permits native full-list translation");
            Check(((DefTranslationPathResult)fullList.Invoke(null, new object[] { typeof(RepairFixtureDef), "RepairFixture.words" })).Reason == "count_change_forbidden", "ordinary string list cannot change count");

            typeof(PlayDataLoader).GetField("loadedInt", flags).SetValue(null, true);
            string migrated = Path.Combine(Path.GetDirectoryName(file), "migration_AutoTranslated.xml");
            AutoTranslatorScanner.SaveXml(migrated, new Dictionary<string, string> {
                ["RepairFixture.stages.stable.oldLabel"] = "已付費的別名譯文",
                ["RepairFixture.category"] = "保留無效譯文",
                ["AwaitActivation.label"] = "停用內容的付費譯文"
            });
            var saved = AutoTranslatorScanner.LoadXmlFileToDict(migrated);
            Check(saved.Count == 1 && saved["RepairFixture.stages.stable.label"] == "已付費的別名譯文", "actual XML writer migrates a proven alias and holds invalid entries");
            DefDatabase<RepairFixtureDef>.Add(new RepairFixtureDef { defName = "AwaitActivation" });
            AutoTranslatorScanner.NotifyRawDefSourcesChanged();
            string languageRoot = Path.Combine(selectionPack, "Languages", "ChineseTraditional");
            scanner.GetMethod("RepairOwnedTranslationDirectory", flags).Invoke(null, new object[] { languageRoot });
            saved = AutoTranslatorScanner.LoadXmlFileToDict(migrated);
            Check(saved["AwaitActivation.label"] == "停用內容的付費譯文", "activation automatically restores the original paid value from hold");
            scanner.GetMethod("FlushSelfRepair", flags).Invoke(null, null);
            byte[] xmlBefore = File.ReadAllBytes(migrated);
            string journalBefore = Newtonsoft.Json.JsonConvert.SerializeObject(new TranslationRepairJournal(journalRoot).Snapshot());
            scanner.GetMethod("RepairOwnedTranslationDirectory", flags).Invoke(null, new object[] { languageRoot });
            Check(xmlBefore.SequenceEqual(File.ReadAllBytes(migrated)) && journalBefore == Newtonsoft.Json.JsonConvert.SerializeObject(new TranslationRepairJournal(journalRoot).Snapshot()), "same environment repeated repair is byte-idempotent");
            string skipped = Path.Combine(languageRoot, "Keyed", "skip_AutoTranslated.xml");
            AutoTranslatorScanner.SaveXml(skipped, new Dictionary<string, string> { ["bad key"] = "付費但鍵不合法", ["empty"] = " \u200B " });
            scanner.GetMethod("FlushSelfRepair", flags).Invoke(null, null);
            var persistedSkipped = new TranslationRepairJournal(journalRoot).Snapshot();
            Check(persistedSkipped.Any(r => r.Translation == "付費但鍵不合法") && persistedSkipped.Any(r => r.Reason == "empty_translation"),
                "new invalid XML keys and empty values are preserved before writer skips them");
            if (!configGuardOnly)
            {
                TestRepairPipelines(languageRoot, file, h);
                TestStartupRepairFailures(languageRoot);
                TestRepairRestartPersistence(languageRoot);
                TestRepairWaiting(h);
                TestRepairMemoryHandoff();
                TestMemoryPreparationRetry();
                TestMainMenuNoticeLifecycle();
                TestStartupTranslationGate();
            }
            TestTranslationConfigGuard(file);
            Check(repairNetworkCalls == 0, "real product repair/restore runs with all paid request paths forbidden; zero requests");
        }
        finally
        {
            typeof(PlayDataLoader).GetField("loadedInt", flags).SetValue(null, false);
            AutoTranslatorMod.Settings = settings;
            h.UnpatchAll("atc.self-repair-test-path");
            DefDatabase<RepairFixtureDef>.Clear();
        }
    }
}
