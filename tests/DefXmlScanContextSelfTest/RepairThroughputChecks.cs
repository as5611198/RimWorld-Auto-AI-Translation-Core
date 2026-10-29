using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static HashSet<string> repairKeysThisTick;
    static Action<string> observeRepairKey;
    static string blockedRepairFile;
    static readonly ManualResetEventSlim batchWriterEntered = new ManualResetEventSlim();
    static readonly ManualResetEventSlim releaseBatchWriter = new ManualResetEventSlim();
    static void CountRepairCheck(string key) { repairKeysThisTick?.Add(key); observeRepairKey?.Invoke(key); }
    static void BlockRepairBatch(string path)
    {
        if (path != blockedRepairFile) return;
        batchWriterEntered.Set();
        if (!releaseBatchWriter.Wait(TimeSpan.FromSeconds(15))) throw new Exception("Blocked repair writer timed out");
    }

    static void TestRepairCheckThroughput(Harmony hooks)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        var workType = scanner.GetNestedType("MainMenuRepairWork", BindingFlags.NonPublic);
        var tick = workType.GetMethod("Tick");
        string root = Path.Combine(temporary, "RepairThroughput");
        string directory = Path.Combine(root, "DefInjected", nameof(RepairFixtureDef));
        Directory.CreateDirectory(directory);
        const int fileCount = 96;
        for (int i = 0; i < fileCount; i++)
        {
            DefDatabase<RepairFixtureDef>.Add(new RepairFixtureDef { defName = "Throughput" + i });
            string key = "Throughput" + i + (i % 2 == 0 ? ".oldMenuText" : ".menuText");
            File.WriteAllText(Path.Combine(directory, i.ToString("D3") + ".xml"),
                "<LanguageData><" + key + ">保留原有譯文" + i + "</" + key + "></LanguageData>");
        }
        hooks.Patch(scanner.GetMethod("ValidateDefTranslation"), prefix: new HarmonyMethod(typeof(Suite), nameof(CountRepairCheck)));
        repairKeysThisTick = new HashSet<string>();
        var results = new TranslationRepairRun();
        var work = Activator.CreateInstance(workType, new object[] { root, results });
        int ticks = 0, mostKeys = 0;
        double maxTickMs = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!(bool)workType.GetProperty("Done").GetValue(work))
            {
                repairKeysThisTick.Clear();
                var update = System.Diagnostics.Stopwatch.StartNew();
                tick.Invoke(work, null); ticks++;
                maxTickMs = Math.Max(maxTickMs, update.Elapsed.TotalMilliseconds);
                mostKeys = Math.Max(mostKeys, repairKeysThisTick.Count);
                if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw new Exception("Throughput fixture timed out");
                Thread.Sleep(1);
            }
            Console.WriteLine("REPAIR_THROUGHPUT files=" + fileCount + "; ticks=" + ticks +
                "; elapsedMs=" + elapsed.ElapsedMilliseconds + "; maxTickMs=" + maxTickMs.ToString("F2") + "; mostKeysPerTick=" + mostKeys);
            Check(mostKeys > 1, "ready small translation files share one bounded repair update instead of one read/check/save round trip per file");
            Check((int)workType.GetProperty("Processed").GetValue(work) == fileCount +
                (int)scanner.GetNestedType("NativeTranslationRepairWork", BindingFlags.NonPublic).GetProperty("Total").GetValue(
                    workType.GetField("native", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(work)),
                "batched repair reports completed files only after their writes finish");
            for (int i = 0; i < fileCount; i++)
            {
                var document = new System.Xml.XmlDocument(); document.Load(Path.Combine(directory, i.ToString("D3") + ".xml"));
                if (document.DocumentElement.FirstChild.Name != "Throughput" + i + ".menuText" ||
                    document.DocumentElement.FirstChild.InnerText != "保留原有譯文" + i) throw new Exception("Batched repair changed a paid value or missed an alias");
            }
            Check(true, "read-ahead and batched writes preserve every paid value and migrate every proven alias");
            TestBufferedRepairSafety(hooks, workType);
        }
        finally
        {
            workType.GetMethod("Cancel", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(work, null);
            ((Task)workType.GetProperty("PendingWrite", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(work))?.GetAwaiter().GetResult();
            repairKeysThisTick = null;
            hooks.Unpatch(scanner.GetMethod("ValidateDefTranslation"), typeof(Suite).GetMethod(nameof(CountRepairCheck), flags));
        }
    }

    static void TestBufferedRepairSafety(Harmony hooks, Type workType)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        string root = Path.Combine(temporary, "RepairBufferSafety");
        string directory = Path.Combine(root, "DefInjected", nameof(RepairFixtureDef));
        Directory.CreateDirectory(directory);
        for (int i = 0; i < 64; i++)
        {
            DefDatabase<RepairFixtureDef>.Add(new RepairFixtureDef { defName = "Buffer" + i });
            File.WriteAllText(Path.Combine(directory, i.ToString("D3") + ".xml"),
                "<LanguageData><Buffer" + i + ".oldMenuText>原始" + i + "</Buffer" + i + ".oldMenuText></LanguageData>");
        }
        var tick = workType.GetMethod("Tick");
        var done = workType.GetProperty("Done");
        var cancel = workType.GetMethod("Cancel", BindingFlags.Instance | BindingFlags.NonPublic);
        var pending = workType.GetProperty("PendingWrite", BindingFlags.Instance | BindingFlags.NonPublic);
        hooks.Patch(scanner.GetMethod("SaveValidatedXml", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(BlockRepairBatch)));
        blockedRepairFile = Path.Combine(directory, "000.xml");
        batchWriterEntered.Reset(); releaseBatchWriter.Reset();
        var work = Activator.CreateInstance(workType, new object[] { root, new TranslationRepairRun() });
        var seen = new HashSet<string>();
        observeRepairKey = key => seen.Add(key);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!batchWriterEntered.IsSet)
            {
                tick.Invoke(work, null); Thread.Sleep(1);
                if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Writer did not enter");
            }
            for (int i = 0; i < 40; i++) { tick.Invoke(work, null); Thread.Sleep(1); }
            Check(seen.Count > 16 && seen.Count <= 32 && !(bool)done.GetValue(work) &&
                (int)workType.GetProperty("Processed").GetValue(work) == 0,
                "blocked disk writes allow bounded read-ahead validation while progress waits for actual persistence");
        }
        finally
        {
            cancel.Invoke(work, null); releaseBatchWriter.Set();
            ((Task)pending.GetValue(work))?.GetAwaiter().GetResult();
            observeRepairKey = null; blockedRepairFile = null;
            hooks.Unpatch(scanner.GetMethod("SaveValidatedXml", flags), typeof(Suite).GetMethod(nameof(BlockRepairBatch), flags));
        }
        Check(Directory.GetFiles(directory).All(path => File.ReadAllText(path).Contains(".oldMenuText>")),
            "cancelling an active buffered repair preserves all source files including its blocked first commit");

        string changed = Path.Combine(directory, "000.xml");
        const string edited = "<LanguageData><Buffer0.menuText>玩家同時編輯的新譯文</Buffer0.menuText></LanguageData>";
        var results = new TranslationRepairRun();
        work = Activator.CreateInstance(workType, new object[] { root, results });
        observeRepairKey = key => { if (key == "Buffer0.oldMenuText") File.WriteAllText(changed, edited); };
        repairKeysThisTick = new HashSet<string>();
        elapsed.Restart();
        try
        {
            while (!(bool)done.GetValue(work))
            {
                tick.Invoke(work, null); Thread.Sleep(1);
                if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new Exception("Source change fixture timed out");
            }
            Check(File.ReadAllText(changed) == edited && results.Snapshot().Any(r => r.SourceFile == changed && r.Reason == "file_operation_failed"),
                "a source edited after read-ahead is retained and reported instead of overwritten with stale buffered data");
        }
        finally { cancel.Invoke(work, null); ((Task)pending.GetValue(work))?.GetAwaiter().GetResult(); observeRepairKey = null; }

        int slowestKeys = 0;
        work = Activator.CreateInstance(workType, new object[] { root, new TranslationRepairRun() });
        observeRepairKey = key => { if (key.StartsWith("Buffer")) Thread.Sleep(12); };
        elapsed.Restart();
        try
        {
            while (!(bool)done.GetValue(work))
            {
                repairKeysThisTick.Clear(); fixtureFrame++; freezeFixtureFrame = true;
                try { tick.Invoke(work, null); } finally { freezeFixtureFrame = false; }
                slowestKeys = Math.Max(slowestKeys, repairKeysThisTick.Count);
                Thread.Sleep(1);
                if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new Exception("Slow validation fixture timed out");
            }
            Check(slowestKeys == 1, "one shared time budget yields after a slow validation across buffered file boundaries");
        }
        finally { cancel.Invoke(work, null); ((Task)pending.GetValue(work))?.GetAwaiter().GetResult(); observeRepairKey = null; }
    }
}
