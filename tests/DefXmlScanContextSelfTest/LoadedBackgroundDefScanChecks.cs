using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static void TestLoadedBackgroundDefScan()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        var loaded = AccessTools.Field(typeof(PlayDataLoader), "loadedInt");
        var previousLoaded = loaded.GetValue(null);
        var previousMods = LoadedModManager.RunningModsListForReading.ToArray();
        var previousThread = fixtureGameThread;
        var previousSettings = AutoTranslatorMod.Settings;
        var previousLanguage = LanguageDatabase.activeLanguage;
        var resolver = scanner.Assembly.GetType("AutoTranslator_Core.TranslationRuntimeResolver");
        var queue = scanner.Assembly.GetType("AutoTranslator_Core.TranslationValidationQueue");
        string source = Write("LoadedWorker/Defs/Relic.xml", "<Defs><RepairFixtureDef><defName>LoadedWorkerRelic</defName><label>English relic</label><menuText>Configure relic</menuText><category>Item</category></RepairFixtureDef><RepairFixtureDef><defName>AbsentWorkerRelic</defName><label>Absent relic</label></RepairFixtureDef></Defs>");
        var bytes = File.ReadAllBytes(source);
        var root = new RepairFixtureDef { defName = "LoadedWorkerRelic", label = "English relic", menuText = "Configure relic" };
        DefDatabase<RepairFixtureDef>.Add(root);
        try
        {
            fixtureGameThread = Thread.CurrentThread.ManagedThreadId;
            loaded.SetValue(null, true);
            AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Traditional };
            LanguageDatabase.activeLanguage = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
            var mod = Pack("loaded.worker", "LoadedWorker", 0, "LoadedWorker");
            mod.assemblies.loadedAssemblies.Add(typeof(RepairFixtureDef).Assembly);
            SetRunning(mod); Invalidate(); resolver.GetMethod("Invalidate", flags).Invoke(null, null);
            Check(PlayDataLoader.Loaded && UnityData.IsInMainThread, "regression fixture matches scans after real play-data loading");
            var extracted = Task.Run(() => AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(source))).GetAwaiter().GetResult();
            Check(extracted.TryGetValue("RepairFixtureDef", out var texts) && texts.TryGetValue("LoadedWorkerRelic.label", out string label) && label == "English relic" && texts["LoadedWorkerRelic.menuText"] == "Configure relic",
                "loaded-game background XML scan retains Def text instead of deleting every not_ready candidate");
            Check(texts["AbsentWorkerRelic.label"] == "Absent relic", "source extraction stays independent of live Def availability");
            string[] keys = { "LoadedWorkerRelic.label", "LoadedWorkerRelic.menuText", "LoadedWorkerRelic.category", "AbsentWorkerRelic.label" };
            var task = (Task<Dictionary<string, DefTranslationPathResult>>)queue.GetMethod("CaptureAsync", flags).Invoke(null, new object[] { "RepairFixtureDef", keys });
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < until)
            { fixtureFrame++; queue.GetMethod("Pump", flags).Invoke(null, null); Thread.Sleep(1); }
            Check(task.IsCompleted, "worker candidates reach the existing game-thread validation queue without blocking the game thread");
            var checkedPaths = task.GetAwaiter().GetResult();
            Check(checkedPaths["LoadedWorkerRelic.label"].Allowed && checkedPaths["LoadedWorkerRelic.menuText"].Allowed,
                "queued validation accepts the actual loaded string fields before translation");
            Check(!checkedPaths["LoadedWorkerRelic.category"].Allowed && !checkedPaths["AbsentWorkerRelic.label"].Allowed,
                "structural fields and absent live Defs remain excluded by game-thread validation");
            Check(root.label == "English relic" && root.menuText == "Configure relic" && File.ReadAllBytes(source).SequenceEqual(bytes),
                "source scan and immutable validation do not mutate live text or source XML");
        }
        finally
        {
            DefDatabase<RepairFixtureDef>.Clear(); loaded.SetValue(null, previousLoaded);
            fixtureGameThread = previousThread; AutoTranslatorMod.Settings = previousSettings;
            LanguageDatabase.activeLanguage = previousLanguage;
            SetRunning(previousMods); Invalidate(); resolver.GetMethod("Invalidate", flags).Invoke(null, null);
        }
    }
}
