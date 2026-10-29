using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static int restartInjections;
    static bool CaptureRestartInjection() { restartInjections++; return false; }

    internal static void MigrateRepairBaseline(string root)
    {
        selectionPack = Path.GetFullPath(root);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var hooks = new Harmony("atc.migrate-repair-baseline");
        hooks.Patch(AccessTools.Method(typeof(AutoTranslatorScanner), "GetLocalPackPath"),
            prefix: new HarmonyMethod(typeof(Suite), nameof(FixturePackPath)));
        try
        {
            var plan = typeof(AutoTranslatorScanner).GetMethod("PlanStartupRepair", flags).Invoke(null, new object[] { TargetLanguage.Traditional });
            Console.WriteLine("BaselineExists=" + plan.GetType().GetField("BaselineExists", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(plan));
            Console.WriteLine("Mode=" + plan.GetType().GetProperty("Decision", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(plan));
            Console.WriteLine("ChangedFiles=" + ((string[])plan.GetType().GetField("ChangedFiles", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(plan)).Length);
        }
        finally { hooks.UnpatchAll(hooks.Id); }
    }

    internal static void PrintCompletedRepairReceipt(string root)
    {
        selectionPack = Path.GetFullPath(root);
        var hooks = new Harmony("atc.completed-repair-readonly-probe");
        hooks.Patch(AccessTools.Method(typeof(AutoTranslatorScanner), "GetLocalPackPath"),
            prefix: new HarmonyMethod(typeof(Suite), nameof(FixturePackPath)));
        try
        {
            Console.WriteLine("Completed=" + typeof(AutoTranslatorScanner).GetMethod("HasCompletedStartupRepair",
                BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { TargetLanguage.Traditional }));
        }
        finally { hooks.UnpatchAll(hooks.Id); }
    }

    static void TestCompletedRepairRestart(Type scanner, Type controller, string file)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var hooks = new Harmony("atc.completed-repair-restart");
        var previousSettings = AutoTranslatorMod.Settings;
        var previousWindows = repairWindowStack;
        string original = File.ReadAllText(file);
        byte[] originalBytes = File.ReadAllBytes(file);
        string receiptPath = (string)scanner.GetMethod("CompletedRepairPath", flags).Invoke(null, new object[] { "ChineseTraditional" });
        byte[] receiptBytes = File.ReadAllBytes(receiptPath);
        var staleWriter = (Action)scanner.GetMethod("CaptureRepairCompletionWriter", flags).Invoke(null, null);
        var queue = (System.Collections.Concurrent.ConcurrentQueue<Action>)typeof(ATC_Dispatcher).GetField("executionQueue", flags).GetValue(null);
        void Restart(bool expectCompleted, string description)
        {
            scanner.GetMethod("InvalidateGameData", flags).Invoke(null, null);
            controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
            AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Traditional };
            repairWindowStack = new WindowStack();
            queuedRepairStarts = restartInjections = 0;
            controller.GetMethod("RequestStartupRepairOrInject", flags).Invoke(null, null);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((bool)controller.GetField("startupLookup", flags).GetValue(null) && DateTime.UtcNow < deadline)
            {
                while (queue.TryDequeue(out var action)) action();
                Thread.Sleep(1);
            }
            Check(!(bool)controller.GetField("startupLookup", flags).GetValue(null), "restart receipt lookup finishes asynchronously");
            Check(expectCompleted ? restartInjections == 1 && queuedRepairStarts == 0 : restartInjections == 0 && queuedRepairStarts == 1,
                description + " (injections=" + restartInjections + ", repairs=" + queuedRepairStarts + ")");
            if (expectCompleted)
            {
                PollNoticeForCompletedResult(controller, flags);
                Check(!repairWindowStack.Windows.Any(w => w is Window_TranslationRepair),
                    "completed restart does not reopen an introduction or result for retained unresolved issues");
            }
        }
        try
        {
            hooks.Patch(controller.GetMethod("BeginRepair", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(CaptureQueuedRepairStart)));
            hooks.Patch(AccessTools.Method(scanner, "RequestMemoryDrop"), prefix: new HarmonyMethod(typeof(Suite), nameof(CaptureRestartInjection)));
            hooks.Patch(AccessTools.Method(typeof(WindowStack), "Add"), prefix: new HarmonyMethod(typeof(Suite), nameof(AddAutomaticRepairWindow)));
            Restart(true, "a completed repair with unresolved entries survives cleared runtime state and fresh settings without another full repair");
            bool staleRejected = false;
            try { staleWriter(); } catch (OperationCanceledException) { staleRejected = true; }
            Check(staleRejected && File.ReadAllBytes(receiptPath).SequenceEqual(receiptBytes),
                "an invalidated completion writer cannot replace the persisted receipt");
            Restart(true, "a second unchanged restart still injects once without repeating full repair");
            File.WriteAllText(file, original.Replace("背景銜接完成", "新翻譯內容"));
            Restart(false, "an actually changed owned translation pack does not reuse an obsolete completion receipt");
            var scope = controller.GetField("repairFileScope", flags)?.GetValue(null) as HashSet<string>;
            Check(scope != null && scope.SetEquals(new[] { Path.GetFullPath(file) }),
                "a changed startup uses the exact changed file scope, never null/full scope");
            File.WriteAllBytes(file, originalBytes);
            var mod = LoadedModManager.RunningModsListForReading.First();
            int order = mod.loadOrder;
            try
            {
                mod.loadOrder++;
                AutoTranslatorScanner.NotifyRawDefSourcesChanged();
                Restart(true, "a mod environment change alone does not repeat a completed owned-pack repair");
            }
            finally { mod.loadOrder = order; AutoTranslatorScanner.NotifyRawDefSourcesChanged(); }
            Check(!(bool)scanner.GetMethod("HasCompletedStartupRepair", flags).Invoke(null, new object[] { TargetLanguage.Simplified }),
                "completion for one language cannot suppress another language's initial repair");
            File.WriteAllText(receiptPath, "{broken receipt");
            Restart(true, "an existing but corrupt baseline skips repair and never falls back to full");
            File.WriteAllBytes(receiptPath, receiptBytes);
            File.Delete(receiptPath);
            Restart(false, "only a missing baseline on first activation selects automatic full repair");
            Check(controller.GetField("repairFileScope", flags).GetValue(null) == null,
                "first activation explicitly uses full scope");
            File.WriteAllBytes(receiptPath, receiptBytes);
            var baseline = Newtonsoft.Json.Linq.JObject.Parse(System.Text.Encoding.UTF8.GetString(receiptBytes));
            string legacyPath = Path.Combine(Path.GetDirectoryName(receiptPath), "completed-legacy-ChineseTraditional.json");
            string legacy = Newtonsoft.Json.JsonConvert.SerializeObject(new {
                Schema = 1, GameVersion = "older-game", TargetFolder = "ChineseTraditional", FileHashes = baseline["Languages"]["ChineseTraditional"] });
            File.WriteAllText(legacyPath, legacy);
            try
            {
                File.Delete(receiptPath);
                Restart(true, "existing completed repair migrates across game versions without requiring another first full run");
                Check(File.Exists(receiptPath) && File.ReadAllText(legacyPath) == legacy,
                    "legacy migration creates baseline.json and preserves the old receipt");
            }
            finally { File.Delete(legacyPath); File.WriteAllBytes(receiptPath, receiptBytes); }
        }
        finally
        {
            File.WriteAllBytes(file, originalBytes);
            File.WriteAllBytes(receiptPath, receiptBytes);
            hooks.UnpatchAll(hooks.Id);
            controller.GetMethod("CancelWaiting", flags).Invoke(null, null);
            AutoTranslatorMod.Settings = previousSettings;
            repairWindowStack = previousWindows;
        }
    }
}
