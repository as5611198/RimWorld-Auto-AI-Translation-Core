using System.Reflection;
using System.Runtime.CompilerServices;
#if !LEGACY_GAME
using System.Runtime.Loader;
#else
using System.Runtime.Serialization;
#endif
using System.Xml;
using System.Xml.Linq;
using AutoTranslator_Core;
using Verse;

internal static class Program
{
    static int Main(string[] args)
    {
#if LEGACY_GAME
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) => {
            string file = new AssemblyName(request.Name).Name + ".dll";
            foreach (string directory in new[] {
                Environment.GetEnvironmentVariable("ATC_TEST_MANAGED") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Documents\rimworld 1.5 data\RimWorldWin64_Data\Managed"),
                Path.GetFullPath("packages/Lib.Harmony.2.4.2/lib/net48") }) {
                string path = Path.Combine(directory, file);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        string runtimeDirectory = Environment.GetEnvironmentVariable("ATC_TEST_MANAGED") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Documents\rimworld 1.5 data\RimWorldWin64_Data\Managed");
        // Each legacy version runs against its own supplied game runtime.
        foreach (string path in Directory.GetFiles(runtimeDirectory, "Unity*.dll")) Assembly.LoadFrom(path);
        Assembly.LoadFrom(Path.Combine(runtimeDirectory, "Assembly-CSharp.dll"));
#else
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            string path = Path.Combine(@"E:\SteamLibrary\steamapps\common\RimWorld", name.Name + ".dll");
            if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            if (name.Name.StartsWith("UnityEngine", StringComparison.Ordinal))
                return context.LoadFromAssemblyPath(@"E:\SteamLibrary\steamapps\common\RimWorld\UnityStubs.dll");
            path = Path.Combine(@"E:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies", name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
#endif
        try {
            if (args.Length == 2 && args[0] == "--repair-environment")
            { Suite.PrintRepairEnvironment(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--repair-completion")
            { Suite.PrintCompletedRepairReceipt(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--migrate-repair-baseline")
            { Suite.MigrateRepairBaseline(args[1]); return 0; }
            string environmentProbe = Environment.GetEnvironmentVariable("ATC_TEST_REPAIR_ENVIRONMENT");
            if (!string.IsNullOrEmpty(environmentProbe))
            { Suite.PrintRepairEnvironment(environmentProbe); return 0; }
            return Suite.Run(args.Contains("--config-guard") || Environment.GetEnvironmentVariable("ATC_TEST_CONFIG_GUARD_ONLY") == "1");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

internal static partial class Suite
{
    static string temporary;
    static int passed;
    static string Version => (string)typeof(AutoTranslatorScanner).GetProperty("CurrentRimWorldVersion", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
    static object Uninitialized(Type type)
    {
#if LEGACY_GAME
        return FormatterServices.GetUninitializedObject(type);
#else
        return RuntimeHelpers.GetUninitializedObject(type);
#endif
    }
    static bool SkipEngineLog() => false;
    static int fixtureGameThread;
    static int fixtureFrame;
    static bool FixtureMainThread(ref bool __result) { __result = Thread.CurrentThread.ManagedThreadId == fixtureGameThread; return false; }
    static bool freezeFixtureFrame;
    static int FixtureFrameCount() => freezeFixtureFrame ? fixtureFrame : fixtureFrame++;
    static bool FixtureFrame(ref int __result) { __result = freezeFixtureFrame ? fixtureFrame : fixtureFrame++; return false; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(bool configGuardOnly = false)
    {
        temporary = Path.Combine(Path.GetTempPath(), "ATC-DefXmlScan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var logPatches = new HarmonyLib.Harmony("atc.def-context-offline-logs");
#if LEGACY_GAME
        // Unity logging requires an initialized native engine. Only replace the
        // test host's log sink; scanner diagnostics are checked separately.
        foreach (string name in new[] { "Warning", "Error", "Message" })
            logPatches.Patch(typeof(Log).GetMethod(name, new[] { typeof(string) }),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Suite).GetMethod(nameof(SkipEngineLog), BindingFlags.Static | BindingFlags.NonPublic)));
#endif
        fixtureGameThread = -1;
        logPatches.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(UnityData), "IsInMainThread"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(Suite).GetMethod(nameof(FixtureMainThread), BindingFlags.Static | BindingFlags.NonPublic)));
        typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.TranslationFrameBudget")
            .GetField("frameClock", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, (Func<int>)FixtureFrameCount);
        logPatches.Patch(typeof(ATC_Dispatcher).GetMethod("EnsureAlive"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(Suite).GetMethod(nameof(SkipEngineLog), BindingFlags.Static | BindingFlags.NonPublic)));

        try
        {
            // Native merge profiling reads preferences. Supply an in-memory object;
            // do not initialize the game or read/write the player's preferences.
            typeof(Prefs).GetField("data", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, Uninitialized(typeof(Prefs).Assembly.GetType("Verse.PrefsData")));
            Console.WriteLine("Product version=" + Version + "; dependency=" + typeof(Prefs).Assembly.Location + "; CLR=" + Environment.Version);
            if (Environment.GetEnvironmentVariable("ATC_TEST_WORKBENCH_ONLY") == "1")
            {
                TestWorkbenchTranslationOnlyEntries();
                Console.WriteLine("PASS: " + passed + " workbench translation-only checks (headless; no game started)");
                return 0;
            }
            string implantsAudit = Environment.GetEnvironmentVariable("ATC_TEST_IMPLANTS_AUDIT");
            if (!string.IsNullOrEmpty(implantsAudit))
            { AuditIntegratedImplants(implantsAudit); return 0; }
            if (Environment.GetEnvironmentVariable("ATC_TEST_NESTED_UI_ONLY") == "1")
            {
                TestNestedUiTextGuard();
                Console.WriteLine("PASS: " + passed + " nested UI text guard checks (headless; no game started)");
                return 0;
            }
            if (Environment.GetEnvironmentVariable("ATC_TEST_REPAIR_COLLISION_ONLY") == "1")
            {
                TestRepairCanonicalCollisions();
                Console.WriteLine("PASS: " + passed + " repair canonical collision checks (headless; no game started)");
                return 0;
            }
            if (Environment.GetEnvironmentVariable("ATC_TEST_LOADED_SCAN_ONLY") == "1")
            {
                TestLoadedBackgroundDefScan();
                Console.WriteLine("PASS: " + passed + " loaded background Def scan checks (headless; no game started)");
                return 0;
            }
            string nativeCorpus = Environment.GetEnvironmentVariable("ATC_TEST_NATIVE_UI_CORPUS");
            if (!string.IsNullOrEmpty(nativeCorpus))
            {
                TestNativeUiCorpus(nativeCorpus);
                Console.WriteLine("PASS: " + passed + " real source corpus checks (headless; synthetic target; no game started)");
                return 0;
            }
            if (Environment.GetEnvironmentVariable("ATC_TEST_CACHED_MOD_UI_ONLY") == "1")
            {
                TestCachedModTranslations();
                TestNativeUiLookup();
                Console.WriteLine("PASS: " + passed + " cached mod UI checks (headless; no game started)");
                return 0;
            }
            if (configGuardOnly)
            {
                TestSelfRepair(true);
                Console.WriteLine("PASS: " + passed + " config guard integration checks (headless; no game started)");
                return 0;
            }
            string early = Write("BeforeLoad/Defs/Child.xml", "<Defs><ThingDef><defName>Early</defName><label>Early label</label></ThingDef></Defs>");
            SetRunning();
            Check(AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(early)).Count == 0, "background extraction waits for a populated running mod stack");
            TestCrossModAndDirectories();
            TestEffectiveFoldersAndConditions();
            TestLoadOrder();
            TestMissingAndCycles();
            TestDeclarationSemantics();
            TestInactiveDependencyPolicy();
            TestInactiveLoadFolders();
            TestInactiveContextOverlay();
            TestCache();
            TestNativeMergeSemantics();
            TestInstalledLogicalFloors();
            TestUnresolvedSources();
            TestSmartSelectionStatuses();
            TestNumericDefInjectedXml();
            TestLoadedBackgroundDefScan();
            TestSelfRepair();
            TestRepairCanonicalCollisions();
            TestStartupRepairPolicy();
            TestCloudArchiveCompatibility();
            TestCachedModTranslations();
            TestNativeUiLookup();
            TestWorkbenchTranslationOnlyEntries();
            TestNestedUiTextGuard();
            Console.WriteLine("PASS: " + passed + " scanner integration checks (headless; no game started)");
            return 0;
        }
        finally
        {
#if LEGACY_GAME
            logPatches.UnpatchAll("atc.def-context-offline-logs");
#endif
            LoadedModManager.RunningModsListForReading.Clear();
            Directory.Delete(temporary, true);
        }
    }

    static void TestCrossModAndDirectories()
    {
        string core = Write("Core/Defs/Base.xml", "<Defs><ThingDef Name='BuildingBase'><description>Inherited building description</description></ThingDef><ThingDef><defName>CoreOnly</defName><label>Core only label</label></ThingDef></Defs>");
        string common = Write("Target/Common/Defs/Middle.xml", "<Defs><ThingDef Name='Middle' ParentName='BuildingBase'><label>Middle building label</label></ThingDef></Defs>");
        string target = Write("Target/1.7/Defs/Target.xml", "<Defs><ThingDef ParentName='Middle'><defName>TargetBuilding</defName></ThingDef></Defs>");
        SetRunning(Pack("core", "Core", 0, "Core"), Pack("target", "Target", 1, "Target/1.7", "Target/Common"));
        var values = AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(target));
        Check(values.TryGetValue("ThingDef", out var defs) && defs.TryGetValue("TargetBuilding.description", out var description) && description == "Inherited building description", "scanner passes cross-mod, cross-directory, multilevel context");
        Check(defs["TargetBuilding.label"] == "Middle building label", "middle override inherited");
        Check(!defs.Keys.Any(k => k.StartsWith("CoreOnly.")), "dependency concrete Def is not attributed to target");
        var sources = new Dictionary<string, Dictionary<string, string>>();
        var extract = typeof(AutoTranslatorScanner).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "ExtractEnglishFromRawDefs" && m.GetParameters().Length == 4);
        object[] arguments = { Path.GetDirectoryName(target), true, sources, null };
        extract.Invoke(null, arguments);
        Check(sources["ThingDef"]["TargetBuilding.description"] == target && ((List<string>)arguments[3]).Count == 0, "policy extraction preserves target source ownership and complete-scan status");
    }

    static void TestEffectiveFoldersAndConditions()
    {
        Write("Selection/Common/Defs/Base.xml", "<Defs><ThingDef Name='Base'><label>Common wrong label</label></ThingDef></Defs>");
        Write("Selection/1.7/Defs/Base.xml", "<Defs><ThingDef Name='Base'><label>Correct version label</label></ThingDef><ThingDef Name='Conditional' MayRequire='disabled.mod'><label>Wrong conditional label</label></ThingDef></Defs>");
        Write("Selection/1.6/Defs/Old.xml", "<Defs><ThingDef Name='Base'><label>Wrong old version</label></ThingDef></Defs>");
        Write("Selection/ConditionalFolder/Defs/Wrong.xml", "<Defs><ThingDef Name='Base'><label>Wrong folder condition</label></ThingDef></Defs>");
        Write("Disabled/Defs/Wrong.xml", "<Defs><ThingDef Name='Base'><label>Wrong disabled mod</label></ThingDef></Defs>");
        Write("Selection/1.7/Defs/.ignored.xml", "<Defs><ThingDef Name='Base'><label>Wrong hidden file</label></ThingDef></Defs>");
        string target = Write("Selection/1.7/Defs/Child.xml", "<Defs><ThingDef ParentName='Base'><defName>Selected</defName><description MayRequire='disabled.mod'>Wrong nested description</description></ThingDef><ThingDef Name='Conditional'><label>Correct active parent</label></ThingDef><ThingDef ParentName='Conditional'><defName>ConditionalChild</defName></ThingDef><ThingDef MayRequireAnyOf='disabled.mod'><defName>Excluded</defName><label>Excluded text</label></ThingDef><ThingDef MayRequire='selection' MayRequireAnyOf='disabled.mod,selection'><defName>Included</defName><label>Included text</label></ThingDef></Defs>");
        SetRunning(Pack("selection", "Selection", 0, "Selection/1.7", "Selection/Common"));
        var defs = AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(target))["ThingDef"];
        Check(defs["Selected.label"] == "Correct version label", "runtime LoadFolders exclude old, disabled and conditional folders; relative override wins");
        Check(defs["ConditionalChild.label"] == "Correct active parent", "inapplicable named nodes do not poison parent index");
        Check(!defs.ContainsKey("Selected.description") && !defs.Keys.Any(k => k.StartsWith("Excluded.")) && defs["Included.label"] == "Included text", "MayRequire and MayRequireAnyOf filter targets and nested fields");
    }

    static void TestLoadOrder()
    {
        Write("Order/Core/Defs/Base.xml", "<Defs><ThingDef Name='Shared'><label>Core label</label></ThingDef></Defs>");
        string early = Write("Order/Early/Defs/Child.xml", "<Defs><ThingDef ParentName='Shared'><defName>Early</defName></ThingDef><ThingDef ParentName='Future'><defName>Unavailable</defName><description>Own description</description></ThingDef></Defs>");
        Write("Order/Late/Defs/Base.xml", "<Defs><ThingDef Name='Shared'><label>Late label</label></ThingDef><ThingDef Name='Future'><label>Future label</label></ThingDef><ThingDef ParentName='Shared'><defName>Late</defName></ThingDef></Defs>");
        var core = Pack("order.core", "Order/Core", 0, "Order/Core");
        var first = Pack("order.early", "Order/Early", 1, "Order/Early");
        var last = Pack("order.late", "Order/Late", 2, "Order/Late");
        SetRunning(core, first, last);
        var defs = AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(early))["ThingDef"];
        Check(defs["Early.label"] == "Core label" && !defs.ContainsKey("Unavailable.label"), "a later provider cannot overwrite earlier inheritance or supply a future-only parent");
        var late = AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.Combine(last.RootDir, "Defs"))["ThingDef"];
        Check(late["Late.label"] == "Late label", "same-mod named parent takes precedence");
        first.loadOrder = 3;
        Check(AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(early))["ThingDef"]["Early.label"] == "Late label", "load order changes invalidate context immediately");
    }

    static void TestMissingAndCycles()
    {
        var doc = new XmlDocument();
        doc.LoadXml("<Defs><ThingDef ParentName='Missing'><defName>One</defName></ThingDef><ThingDef ParentName='Missing'><defName>Two</defName></ThingDef><ThingDef Name='A' ParentName='B'><label>Cycle A</label></ThingDef><ThingDef Name='B' ParentName='A'><label>Cycle B</label></ThingDef><ThingDef ParentName='A'><defName>CyclicChild</defName></ThingDef></Defs>");
        var warnings = new List<string>();
        var context = new DefXmlInheritanceContext(new[] { new DefXmlSourceDocument { Document = doc, SourceFile = "missing.xml", PackageId = "broken.mod" } }, warnings.Add);
        var resolved = context.ResolveTargets(new[] { "missing.xml" });
        context.ResolveTargets(new[] { "missing.xml" });
        Check(warnings.Count(w => w.Contains("was not found")) == 1 && warnings.Any(w => w.Contains("Missing") && w.Contains("broken.mod") && w.Contains("missing.xml") && w.Contains("One")), "missing parents are deduplicated with source, package, parent and child diagnostics");
        Check(warnings.Count(w => w.Contains("cycle detected")) == 1 && resolved.Last().ResolvedNode == null, "cycle and dependent child are rejected without fabricated partial inheritance");
        string path = Write("Broken/Defs/Defs.xml", doc.OuterXml);
        SetRunning(Pack("broken.mod", "Broken", 0, "Broken"));
        var values = AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(path));
        Check(!values.Values.SelectMany(v => v.Keys).Any(k => k.StartsWith("CyclicChild.")), "scanner skips cyclic Def extraction");
        object[] arguments = { Path.GetDirectoryName(path), null };
        typeof(AutoTranslatorScanner).GetMethod("ResolveRawDefTargets", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, arguments);
        Check(((List<string>)arguments[1]).Contains(path), "unresolved inheritance marks source scan incomplete");
    }

    static void TestDeclarationSemantics()
    {
        var document = new XmlDocument();
        document.LoadXml("<Defs><ThingDef Name='Base'><label>First label</label></ThingDef><ThingDef Name='Base'><label>Rejected duplicate label</label></ThingDef><ThingDef Name='base'><label>Lowercase label</label></ThingDef><ThingDef ParentName='Base'><defName>First</defName></ThingDef><ThingDef ParentName='base'><defName>Lowercase</defName></ThingDef></Defs>");
        var warnings = new List<string>();
        var context = new DefXmlInheritanceContext(new[] { new DefXmlSourceDocument { Document = document, SourceFile = "duplicates.xml" } }, warnings.Add);
        var result = context.ResolveTargets(new[] { "duplicates.xml" });
        Check(DefXmlInheritanceResolver.GetDirectChildText(result[3].ResolvedNode, "label") == "First label" && DefXmlInheritanceResolver.GetDirectChildText(result[4].ResolvedNode, "label") == "Lowercase label" && warnings.Count == 1, "same-mod duplicates retain first definition and parent names remain case sensitive");
        result[3].OriginalNode.InnerXml = "<label>Mutated original</label>";
        result[3].ResolvedNode.InnerXml = "<label>Mutated resolved</label>";
        Check(DefXmlInheritanceResolver.GetDirectChildText(context.ResolveTargets(new[] { "duplicates.xml" })[3].ResolvedNode, "label") == "First label", "returned XML originals and resolved nodes cannot mutate shared context");
    }

    static object SnapshotContext()
    {
        object cache = typeof(AutoTranslatorScanner).GetField("RawDefContexts", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var snapshots = (System.Collections.IList)cache.GetType().GetField("snapshots", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cache);
        object last = snapshots[snapshots.Count - 1];
        return last.GetType().GetField("Context", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(last);
    }
    static void Invalidate() => typeof(AutoTranslatorScanner).GetMethod("NotifyRawDefSourcesChanged").Invoke(null, null);

    static object ParsedDocument(string path)
    {
        object cache = typeof(AutoTranslatorScanner).GetField("RawDefContexts", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var parsed = (System.Collections.IDictionary)cache.GetType().GetField("parsed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cache);
        object tuple = parsed[path];
        return tuple.GetType().GetProperty("Item2").GetValue(tuple);
    }

    static void TestInactiveDependencyPolicy()
    {
        var target = (ModMetaData)Uninitialized(typeof(ModMetaData));
        var metaField = typeof(ModMetaData).GetField("meta", BindingFlags.Instance | BindingFlags.NonPublic);
        object metadata = Uninitialized(metaField.FieldType);
        var dependencies = new List<ModDependency> {
            new ModDependency { packageId = "core" },
            new ModDependency { packageId = "disabled.required" }
        };
        typeof(ModDependency).GetField("alternativePackageIds")?.SetValue(dependencies[1], new List<string> { "active.alternative" });
        metaField.FieldType.GetField("modDependencies").SetValue(metadata, dependencies);
        metaField.SetValue(target, metadata);
        var validate = typeof(AutoTranslatorScanner).GetMethod("GetUnavailableRawDefDependencies", BindingFlags.Static | BindingFlags.NonPublic);
        var active = new HashSet<string>(new[] { "core" }, StringComparer.OrdinalIgnoreCase);
        var unavailable = (List<string>)validate.Invoke(null, new object[] { target, active });
        Check(unavailable.SequenceEqual(new[] { "disabled.required" }), "inactive scan explicitly rejects dependencies absent from running context");
        active.Add("ACTIVE.ALTERNATIVE");
        Check(((List<string>)validate.Invoke(null, new object[] { target, active })).Count == (Version == "1.5" ? 1 : 0), "dependency alternatives follow the selected game's dependency model");
        active.Add("disabled.required");
        Check(((List<string>)validate.Invoke(null, new object[] { target, active })).Count == 0, "inactive scan accepts directly loaded required dependency in every version");
    }

    static void TestInactiveContextOverlay()
    {
        var running = new XmlDocument();
        running.LoadXml("<Defs><ThingDef Name='Base'><label>Running base label</label></ThingDef><ThingDef ParentName='OfflineOnly'><defName>Earlier</defName></ThingDef></Defs>");
        var offline = new XmlDocument();
        offline.LoadXml("<Defs><ThingDef Name='OfflineOnly'><label>Offline only label</label></ThingDef><ThingDef ParentName='Base'><defName>OfflineChild</defName></ThingDef></Defs>");
        var warnings = new List<string>();
        var shared = new DefXmlInheritanceContext(new[] { new DefXmlSourceDocument { Document = running, SourceFile = "running.xml", PackageId = "running", LoadOrder = 0 } }, warnings.Add);
        var first = new DefXmlInheritanceContext(new[] { new DefXmlSourceDocument { Document = offline, SourceFile = "offline.xml", PackageId = "offline", LoadOrder = 1 } }, warnings.Add, null, shared);
        var result = first.ResolveTargets(new[] { "running.xml", "offline.xml" });
        Check(result.Count == 2 && DefXmlInheritanceResolver.GetDirectChildText(result[1].ResolvedNode, "label") == "Running base label", "inactive overlay reuses running parent context and emits only its own documents");
        Check(!DefXmlInheritanceResolver.GetDirectChildText(shared.ResolveTargets(new[] { "running.xml" })[1].ResolvedNode, "label").Any(), "inactive overlay does not contaminate the running parent index");
        var second = new DefXmlInheritanceContext(new[] { new DefXmlSourceDocument { Document = offline, SourceFile = "another.xml", PackageId = "another", LoadOrder = 1 } }, warnings.Add, null, shared);
        second.ResolveTargets(new[] { "another.xml" });
        Check(warnings.Count == 1, "multiple inactive overlays reuse shared missing-parent diagnostics");
    }

    static void TestInactiveLoadFolders()
    {
        if (Version == "1.6" && RimWorld.VersionControl.CurrentVersion.Minor != 6)
        {
            Console.WriteLine("SKIP 1.6 native LoadFolders execution: no 1.6 runtime installed. Current-version folder semantics are exercised on actual 1.5 and 1.7 dependencies.");
            return;
        }
        foreach (string folder in new[] { "1.5", "1.6", "1.7", "1.8", "Common", "Any", "All", "Not", "Disabled" })
            Directory.CreateDirectory(Path.Combine(temporary, "OfflineFolders", folder));
        string manifest = Write("OfflineFolders/LoadFolders.xml", "<loadFolders>" + string.Concat(new[] { "1.5", "1.6", "1.7", "1.8" }.Select(v => v == Version
            ? "<v" + v + "><li>Common</li><li>" + v + "</li><li IfModActive='missing,core'>Any</li><li IfModActiveAll='core,extra'>All</li><li IfModNotActive='blocked'>Not</li><li IfModActive='disabled'>Disabled</li></v" + v + ">"
            : "<v" + v + "><li>" + v + "</li></v" + v + ">")) + "</loadFolders>");
        var active = new HashSet<string>(new[] { "core" }, StringComparer.OrdinalIgnoreCase);
        var resolve = typeof(AutoTranslatorScanner).GetMethod("ResolveInactiveRawDefRoots", BindingFlags.Static | BindingFlags.NonPublic);
        string modRoot = Path.GetDirectoryName(manifest);
        var roots = (List<string>)resolve.Invoke(null, new object[] { modRoot, active });
        Check(roots.Select(Path.GetFileName).SequenceEqual(new[] { "Not", "Any", Version, "Common" }), "inactive LoadFolders uses exact version, actual running conditions and descending file priority");
        active.Add("extra"); active.Add("blocked");
        roots = (List<string>)resolve.Invoke(null, new object[] { modRoot, active });
        Check(roots.Select(Path.GetFileName).SequenceEqual(new[] { "All", "Any", Version, "Common" }), "inactive all/negative LoadFolders conditions respond to running context");
        File.WriteAllText(manifest, "<loadFolders><v" + Version + "><li IfModActive='disabled'>Disabled</li></v" + Version + "></loadFolders>");
        Check(((List<string>)resolve.Invoke(null, new object[] { modRoot, active })).Count == 0, "an explicitly empty applicable LoadFolders branch does not fall back to unrelated folders");
        File.Delete(manifest);
        roots = (List<string>)resolve.Invoke(null, new object[] { modRoot, active });
        Check(roots.Select(Path.GetFileName).SequenceEqual(new[] { Version, "Common", "OfflineFolders" }), "inactive fallback selects only current-compatible version plus Common/root");
    }

    static void TestCache()
    {
        string parent = Write("Cache/Parent/Defs/Base.xml", "<Defs><ThingDef Name='Base'><label>Original parent label</label></ThingDef></Defs>");
        string child = Write("Cache/Child/Defs/Child.xml", "<Defs><ThingDef ParentName='Base'><defName>Cached</defName></ThingDef></Defs>");
        var first = Pack("cache.parent", "Cache/Parent", 0, "Cache/Parent");
        var second = Pack("cache.child", "Cache/Child", 1, "Cache/Child");
        SetRunning(first, second); Invalidate();
        AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(parent));
        object context = SnapshotContext();
        object childDocument = ParsedDocument(child);
        var result = AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(child));
        Check(ReferenceEquals(context, SnapshotContext()), "different target modules reuse one complete index");
        result["ThingDef"]["Cached.label"] = "Caller mutation";
        Check(AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(child))["ThingDef"]["Cached.label"] == "Original parent label", "caller cannot corrupt cached inheritance");
        File.WriteAllText(parent, "<Defs><ThingDef Name='Base'><label>Changed parent label</label></ThingDef></Defs>");
        Thread.Sleep(2100);
        Check(AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(child))["ThingDef"]["Cached.label"] == "Changed parent label" && !ReferenceEquals(context, SnapshotContext()), "external source edits invalidate within metadata refresh interval");
        Check(ReferenceEquals(childDocument, ParsedDocument(child)), "unchanged XML documents are not reparsed during index rebuild");
        object changed = SnapshotContext(); Invalidate();
        AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(child));
        Check(!ReferenceEquals(changed, SnapshotContext()), "explicit refresh invalidates immediately");
        Parallel.For(0, 12, _ => {
            var values = AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(child));
            if (values["ThingDef"]["Cached.label"] != "Changed parent label") throw new Exception("Partial context exposed");
        });
        Check(true, "concurrent background readers see complete contexts");
        string added = Write("Cache/Parent/Defs/New.xml", "<Defs><ThingDef Name='NewParent'><description>New inherited description</description></ThingDef></Defs>");
        Write("Cache/Child/Defs/New.xml", "<Defs><ThingDef ParentName='NewParent'><defName>NewChild</defName></ThingDef></Defs>");
        Thread.Sleep(2100);
        Check(AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(child))["ThingDef"]["NewChild.description"] == "New inherited description", "new files are discovered after metadata refresh");
        File.Delete(added); Thread.Sleep(2100);
        Check(!AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(child))["ThingDef"].ContainsKey("NewChild.description"), "deleted dependency files cannot survive in cached index");
        Invalidate(); AutoTranslatorSettings.IsCancellationRequested = true;
        try { Check(AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(child)).Count == 0, "cancelled context build returns no partial data"); }
        finally { AutoTranslatorSettings.IsCancellationRequested = false; }
        Check(AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(child))["ThingDef"]["Cached.label"] == "Changed parent label", "cancelled build does not poison next scan");
        SetRunning(second); // active IDs and providers both change without a file edit
        Check(!AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(child)).Values.SelectMany(d => d.Keys).Any(k => k == "Cached.label"), "removed running module immediately invalidates providers");
        second.foldersToLoadDescendingOrder = null;
        Check(AutoTranslatorScanner.ExtractEnglishFromRawDefs(Path.GetDirectoryName(child)).Count == 0, "unfinished runtime folder state cannot be published as a complete context");
    }

    static void TestNativeMergeSemantics()
    {
        var doc = new XmlDocument();
        doc.LoadXml("<Defs><ThingDef Name='Base' Abstract='True'><label>Parent label</label><description>Parent description</description><rulesStrings><li>parent rule</li></rulesStrings><nested><description>Nested description</description></nested></ThingDef><ThingDef ParentName='Base'><defName>Merge</defName><label/><rulesStrings><li>child rule</li></rulesStrings><nested/></ThingDef><ThingDef ParentName='Base' Inherit='False'><defName>Reset</defName></ThingDef><ThingDef ParentName='Base'><defName>ListReset</defName><rulesStrings Inherit='False'><li>reset rule</li></rulesStrings></ThingDef></Defs>");
        var pack = Pack("merge", "Merge", 0, "Merge");
        XmlInheritance.Clear();
        foreach (XmlNode node in doc.DocumentElement.ChildNodes) XmlInheritance.TryRegister(node, pack);
        XmlInheritance.Resolve();
        var ours = DefXmlInheritanceResolver.Resolve(new[] { new DefXmlSourceDocument { Document = doc, SourceFile = "merge.xml" } });
        foreach (var item in ours.Where(r => !string.IsNullOrEmpty(DefXmlInheritanceResolver.GetDirectChildText(r.OriginalNode, "defName"))))
        {
            string defName = DefXmlInheritanceResolver.GetDirectChildText(item.OriginalNode, "defName");
            XmlNode original = doc.DocumentElement.ChildNodes.Cast<XmlNode>().Single(n => DefXmlInheritanceResolver.GetDirectChildText(n, "defName") == defName);
            XmlNode native = XmlInheritance.GetResolvedNodeFor(original);
            Check(NormalizeXml(native.OuterXml) == NormalizeXml(item.ResolvedNode.OuterXml), "merge contents match host game for product " + Version + ": " + DefXmlInheritanceResolver.GetDirectChildText(item.OriginalNode, "defName"));
        }
        XmlInheritance.Clear();
    }

    static string NormalizeXml(string xml)
    {
        var root = XElement.Parse("<root>" + xml + "</root>");
        foreach (var element in root.Descendants().Where(e => !e.HasElements && e.Value.Length == 0)) element.Value = "";
        return root.ToString(SaveOptions.DisableFormatting);
    }

    static void TestInstalledLogicalFloors()
    {
        const string floors = @"D:\RimCrow\mods\3813239016";
        const string core = @"E:\SteamLibrary\steamapps\common\RimWorld\Data\Core";
        if (!Directory.Exists(floors) || !Directory.Exists(core)) { Console.WriteLine("SKIP installed Logical Floors fixture unavailable"); return; }
        var corePack = Pack("ludeon.rimworld", "unused", 0, "unused");
        Set(corePack, "rootDirInt", new DirectoryInfo(core)); corePack.foldersToLoadDescendingOrder = new List<string> { core };
        var floorPack = Pack("logical.floors", "unused", 1, "unused");
        Set(floorPack, "rootDirInt", new DirectoryInfo(floors)); floorPack.foldersToLoadDescendingOrder = new List<string> { floors };
        SetRunning(corePack, floorPack); Invalidate();
        var resolved = (List<ResolvedDefXmlNode>)typeof(AutoTranslatorScanner).GetMethod("ResolveRawDefTargets", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { Path.Combine(floors, "Defs"), null });
        var ladder = resolved.Single(n => DefXmlInheritanceResolver.GetDirectChildText(n.OriginalNode, "defName") == "DDLF_Ladder");
        Check(DefXmlInheritanceResolver.GetDirectChildText(ladder.ResolvedNode, "category") == "Building", "installed Logical Floors inherits BuildingBase through real scanner context builder");
        Check(resolved.All(n => n.SourceFile.StartsWith(floors, StringComparison.OrdinalIgnoreCase)), "installed fixture result owns only target mod nodes");
    }

    static ModContentPack Pack(string id, string root, int order, params string[] folders)
    {
        var pack = (ModContentPack)Uninitialized(typeof(ModContentPack));
        Set(pack, "rootDirInt", new DirectoryInfo(Path.Combine(temporary, root)));
        Set(pack, "packageIdInt", id);
        Set(pack, "packageIdPlayerFacingInt", id);
        Set(pack, "nameInt", id);
        pack.assemblies = new ModAssemblyHandler(pack);
        pack.loadOrder = order;
        pack.foldersToLoadDescendingOrder = folders.Select(f => Path.Combine(temporary, f)).ToList();
        return pack;
    }
    static void Set(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
    static void SetRunning(params ModContentPack[] packs)
    {
        LoadedModManager.RunningModsListForReading.Clear();
        LoadedModManager.RunningModsListForReading.AddRange(packs);
    }
    static string Write(string path, string xml)
    {
        string full = Path.Combine(temporary, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllText(full, xml); return full;
    }
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
        passed++; Console.WriteLine("PASS: " + message);
    }
}
