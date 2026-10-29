using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static void TestRepairCanonicalCollisions()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        var workType = scanner.GetNestedType("MainMenuRepairWork", BindingFlags.NonPublic);
        var loaded = AccessTools.Field(typeof(PlayDataLoader), "loadedInt");
        var wasLoaded = loaded.GetValue(null); var previousThread = fixtureGameThread;
        var previousPack = selectionPack; var previousSettings = AutoTranslatorMod.Settings;
        var previousLanguage = LanguageDatabase.activeLanguage;
        var previousMods = LoadedModManager.RunningModsListForReading.ToArray();
        var tkeys = (Dictionary<string, string>)AccessTools.Field(typeof(TKeySystem), "tKeyToNormalizedTranslationKey").GetValue(null);
        var names = new List<string>(); var mappings = new List<string>();
        var hooks = new Harmony("atc.repair-canonical-collision");
        hooks.Patch(scanner.GetMethod("GetLocalPackPath"), prefix: new HarmonyMethod(typeof(Suite), nameof(FixturePackPath)));
        hooks.Patch(typeof(AutoTranslatorAPI).GetMethod("TranslateBatchAsync"), prefix: new HarmonyMethod(typeof(Suite), nameof(ForbidRepairNetwork)));
        hooks.Patch(typeof(AutoTranslatorAPI).GetMethod("SendJsonRequestAttemptAsync", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(ForbidRepairNetwork)));
        int previousNetworkCalls = repairNetworkCalls;
        try
        {
            selectionPack = Path.Combine(temporary, "CanonicalCollisionPack");
            AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Traditional };
            fixtureGameThread = Thread.CurrentThread.ManagedThreadId; loaded.SetValue(null, true);
            var language = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
            language.info = new LanguageInfo(); language.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
            language.defInjections = new List<DefInjectionPackage>(); language.loadErrors = new List<string>();
            LanguageDatabase.activeLanguage = language;
            var mod = Pack("collision.fixture", "CollisionSource", 0, "CollisionSource");
            mod.assemblies.loadedAssemblies.Add(typeof(RepairFixtureDef).Assembly); SetRunning(mod);
            scanner.Assembly.GetType("AutoTranslator_Core.TranslationRuntimeResolver").GetMethod("Invalidate", flags).Invoke(null, null);
            foreach (bool incremental in new[] { false, true })
            foreach (bool canonicalFirst in new[] { false, true })
            foreach (bool identical in new[] { false, true })
            {
                string scenario = (incremental ? "delta" : "full") + (canonicalFirst ? "CanonicalFirst" : "AliasFirst") + (identical ? "Same" : "Conflict");
                string name = "Collision" + scenario; names.Add(name);
                var def = new RepairFixtureDef { defName = name, label = "Unchanged runtime label" }; DefDatabase<RepairFixtureDef>.Add(def);
                string canonical = name + ".label", alias = name + "Old.label", untouched = name + ".description";
                tkeys[alias] = canonical; mappings.Add(alias);
                string first = canonicalFirst ? canonical : alias, second = canonicalFirst ? alias : canonical;
                string firstValue = "第一份已付費譯文", secondValue = identical ? firstValue : "第二份已付費譯文";
                string root = Path.Combine(selectionPack, "Languages", "ChineseTraditional");
                string file = Path.Combine(root, "DefInjected", nameof(RepairFixtureDef), "collision_" + scenario + "_AutoTranslated.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, "<LanguageData><" + first + ">" + firstValue + "</" + first + "><" + second + ">" + secondValue + "</" + second + "><" + untouched + ">未變動譯文</" + untouched + "></LanguageData>");
                string package = "collision." + scenario;
                string[] scope = incremental ? new[] { package } : null;
                if (incremental)
                    scanner.GetMethod("RecordCommittedOutput", flags).Invoke(null, new object[] { package, "ChineseTraditional", file,
                        new Dictionary<string, string>(), new Dictionary<string, string> { [first] = firstValue, [second] = secondValue } });
                var run = new TranslationRepairRun();
                object work = Activator.CreateInstance(workType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { root, run, false, scope, new[] { file } }, null);
                var until = DateTime.UtcNow.AddSeconds(15);
                while (!(bool)workType.GetProperty("Done").GetValue(work) && DateTime.UtcNow < until)
                { fixtureFrame++; workType.GetMethod("Tick").Invoke(work, null); Thread.Sleep(1); }
                Check((bool)workType.GetProperty("Done").GetValue(work), "canonical collision repair completes without a missing dictionary key: " + scenario);
                var saved = AutoTranslatorScanner.LoadXmlFileToDict(file);
                Check(saved.Count == 2 && saved[canonical] == firstValue && !saved.ContainsKey(alias), "canonical collision retains the first selected paid value: " + scenario);
                Check(saved[untouched] == "未變動譯文" && def.label == "Unchanged runtime label", "collision checking preserves unrelated text and does not inject early: " + scenario);
                var journal = (TranslationRepairJournal)scanner.GetProperty("RepairJournal", flags).GetValue(null);
                Check(journal.ForSource(file).Any(r => r.Translation == secondValue && r.Status == (identical ? "相同譯文合併" : "衝突")), "duplicate or conflicting incoming paid candidate is explicitly recorded: " + scenario);
                Check(repairNetworkCalls == previousNetworkCalls, "canonical collision handling makes no paid translation request: " + scenario);
            }
        }
        finally
        {
            foreach (string key in mappings) tkeys.Remove(key);
            DefDatabase<RepairFixtureDef>.Clear(); loaded.SetValue(null, wasLoaded); fixtureGameThread = previousThread;
            selectionPack = previousPack; AutoTranslatorMod.Settings = previousSettings; LanguageDatabase.activeLanguage = previousLanguage;
            SetRunning(previousMods);
            scanner.Assembly.GetType("AutoTranslator_Core.TranslationRuntimeResolver").GetMethod("Invalidate", flags).Invoke(null, null);
            hooks.UnpatchAll(hooks.Id);
        }
    }
}
