using System.Reflection;
using Newtonsoft.Json.Linq;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static bool UnresolvedModsFolder(string folderName, ref string __result)
    {
        __result = Path.Combine(temporary, "OfflineModCatalog", folderName);
        Directory.CreateDirectory(__result);
        return false;
    }
    static bool UnresolvedConfigFile(ref string __result)
    {
        __result = Path.Combine(temporary, "OfflineModCatalog", "ModsConfig.xml");
        return false;
    }
    static ModMetaData UnresolvedMod(string id)
    {
        var mod = (ModMetaData)Uninitialized(typeof(ModMetaData));
        AccessTools.Field(typeof(ModMetaData), "packageIdLowerCase").SetValue(mod, id);
        return mod;
    }

    static void TestUnresolvedSources()
    {
        var oldSettings = AutoTranslatorMod.Settings;
        AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Traditional };
        var hooks = new Harmony("atc.unresolved-scope-offline");
        var unresolvedInstalledMods = new List<ModMetaData> { UnresolvedMod("fixture.active"), UnresolvedMod("fixture.disabled") };
        var catalog = AccessTools.Field(typeof(ModLister), "mods");
        var active = AccessTools.Field(typeof(ModsConfig), "activeModsHashSet");
        object oldCatalog = null, oldActive = null;
        bool replacedCatalog = false;
        try
        {
            // Initialize native filesystem discovery only inside temporary folders.
            // The catalog/active fixtures then use the game's real getters and the
            // production scope filter, without reading player configuration.
            Write("OfflineModCatalog/Data/Core/About/About.xml", "<ModMetaData><name>Offline Core fixture</name><packageId>ludeon.rimworld</packageId><supportedVersions><li>" + Version + "</li></supportedVersions></ModMetaData>");
            Write("OfflineModCatalog/ModsConfig.xml", "<ModsConfigData><version>" + Version + ".0</version><activeMods><li>ludeon.rimworld</li></activeMods><knownExpansions /></ModsConfigData>");
            hooks.Patch(AccessTools.Method(typeof(GenFilePaths), "GetOrCreateModsFolder"),
                prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(UnresolvedModsFolder), BindingFlags.NonPublic | BindingFlags.Static)));
            hooks.Patch(AccessTools.PropertyGetter(typeof(GenFilePaths), "ModsConfigFilePath"),
                prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(UnresolvedConfigFile), BindingFlags.NonPublic | BindingFlags.Static)));
            Console.WriteLine("Offline catalog: mod discovery and ModsConfig.xml use temporary paths.");
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ModLister).TypeHandle);
            oldCatalog = catalog.GetValue(null);
            oldActive = active.GetValue(null);
            catalog.SetValue(null, unresolvedInstalledMods);
            active.SetValue(null, new HashSet<string> { "fixture.active" });
            replacedCatalog = true;
            var loader = typeof(AutoTranslatorScanner).GetMethod("TryLoadUnresolvedLanguageFile", BindingFlags.NonPublic | BindingFlags.Static);
            string source = Write("Unresolved/Languages/English/Keyed/Nested.xml", "<LanguageData><Menu><title>Current title</title><items><li>First</li><li>Second</li></items></Menu><Plain>Line one\\nLine two</Plain></LanguageData>");
            object[] args = { source, null, null };
            Check((bool)loader.Invoke(null, args), "unresolved LanguageData can be read");
            var values = (Dictionary<string, string>)args[1];
            Check(values.TryGetValue("Menu.title", out var title) && title == "Current title" && values["Menu.items.1"] == "Second", "retry uses the scanner's nested language dictionary semantics");
            var partition = typeof(AutoTranslatorScanner).GetMethod("SelectCurrentUnresolvedSources", BindingFlags.NonPublic | BindingFlags.Static);
            var entries = new List<TranslationUnresolvedEntry> {
                new() { Key = "Plain", Bucket = "Keyed", SourceFile = source, SourceText = "Line one\\nLine two" },
                new() { Key = "Menu.title", Bucket = "Keyed", SourceFile = source, SourceText = "Old title" },
                new() { Key = "Gone", Bucket = "Keyed", SourceFile = source, SourceText = "Removed" },
                new() { Key = "Menu.items.1", Bucket = "Keyed", SourceFile = source, SourceText = "Second" }
            };
            foreach (var entry in entries) entry.PackageId = "fixture.active";
            object[] partitionArgs = { entries, null };
            var current = (List<TranslationUnresolvedEntry>)partition.Invoke(null, partitionArgs);
            Check(current.Count == 2 && current[0] == entries[0] && current[1] == entries[3], "legacy literal newline sources remain current and one stale item does not block valid siblings");
            Check(((List<TranslationUnresolvedEntry>)partitionArgs[1]).SequenceEqual(new[] { entries[1], entries[2] }), "genuinely changed and removed sources remain blocked");

            var disabled = new TranslationUnresolvedEntry { PackageId = "fixture.disabled", Key = "Menu.title", Bucket = "Keyed", SourceFile = source, SourceText = "Current title" };
            var removed = new TranslationUnresolvedEntry { PackageId = "fixture.removed", Key = disabled.Key, Bucket = disabled.Bucket, SourceFile = source, SourceText = disabled.SourceText };
            object[] scopeArgs = { new[] { entries[0], disabled, removed }, null };
            current = (List<TranslationUnresolvedEntry>)partition.Invoke(null, scopeArgs);
            Check(current.SequenceEqual(new[] { entries[0] }), "disabled and removed packages cannot enter retry or keep-original even with readable matching source XML");
            Check(((List<TranslationUnresolvedEntry>)scopeArgs[1]).SequenceEqual(new[] { disabled, removed }), "unavailable selections remain unprocessed rather than marked successful");
            AutoTranslatorMod.Settings.OnlyScanActiveMods = false;
            current = (List<TranslationUnresolvedEntry>)partition.Invoke(null, scopeArgs);
            Check(current.SequenceEqual(new[] { entries[0], disabled }), "explicit inactive scanning permits disabled installed sources but never removed packages");
            AutoTranslatorMod.Settings.OnlyScanActiveMods = true;
            var isCurrent = typeof(AutoTranslatorScanner).GetMethod("IsUnresolvedEntryCurrent", BindingFlags.NonPublic | BindingFlags.Static);
            Check(!(bool)isCurrent.Invoke(null, new object[] { disabled }) && !(bool)isCurrent.Invoke(null, new object[] { removed }), "manual workbench source guard also rejects unavailable packages");
            unresolvedInstalledMods.Clear();
            current = (List<TranslationUnresolvedEntry>)partition.Invoke(null, scopeArgs);
            Check(current.Count == 0, "a stale open-window selection is rechecked against the updated catalog");

            // This helper is called by the production DefInjected language overlay loader.
            var merge = typeof(AutoTranslatorScanner).GetMethod("LoadDefLanguageSource", BindingFlags.NonPublic | BindingFlags.Static);
            var texts = new Dictionary<string, string> { ["Item.label"] = "Raw Def label" };
            var paths = new Dictionary<string, string> { ["Item.label"] = "Defs/Item.xml" };
            string overlay = Write("Unresolved/Languages/English/DefInjected/ThingDef/Labels.xml", "<LanguageData><Item><label>Language overlay label</label><description>Overlay description</description></Item></LanguageData>");
            merge.Invoke(null, new object[] { texts, paths, overlay });
            Check(texts["Item.label"] == "Language overlay label" && paths["Item.label"] == overlay && texts["Item.description"] == "Overlay description" && paths["Item.description"] == overlay, "production Def language loader parses nested fields and updates source text and source path together");
            foreach (var pair in new[] { ("zh.m00nl1ght.mappreview", "Map Preview_zh"), ("day29zh.aaaa", "AAAA-zh"), ("deadmanswitch.misc", "The Dead Man's Switch - CHS") })
            {
                Check(IsTranslationIdentity(pair.Item1, pair.Item2), "translation identity recognized: " + pair.Item2);
            }
            Check(IsTranslationIdentity("ordinary.mod", "Example Translation"), "existing translation-name rules remain compatible");
            Check(!IsTranslationIdentity("ordinary.mod", "Zhentarim Creatures"), "locale suffix recognition does not match arbitrary word substrings");
            AuditInstalledUnresolvedSources(loader);
        }
        finally
        {
            if (replacedCatalog)
            {
                catalog.SetValue(null, oldCatalog);
                active.SetValue(null, oldActive);
            }
            hooks.UnpatchAll("atc.unresolved-scope-offline");
            AutoTranslatorMod.Settings = oldSettings;
        }
    }

    static bool IsTranslationIdentity(string packageId, string name)
    {
        var type = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.TranslationPatchIdentity");
        return (bool)type.GetMethod("IsMatch", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { packageId, name });
    }

    static void AuditInstalledUnresolvedSources(MethodInfo loader)
    {
        const string report = @"E:\SteamLibrary\steamapps\common\RimWorld\Mods\!Translation_AI_Pack\Reports\TranslationUnresolved\latest.json";
        if (!File.Exists(report)) return;
        var entries = JObject.Parse(File.ReadAllText(report))["Entries"].ToObject<List<TranslationUnresolvedEntry>>();
        var patches = entries.Where(e => IsTranslationIdentity(e.PackageId, e.ModName)).ToList();
        int recovered = 0;
        var cache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var normalize = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.TranslationXmlDictionaryParser")
            .GetMethod("NormalizeValue", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var entry in entries.Where(e => (e.SourceFile ?? "").IndexOf("\\Languages\\", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            if (!cache.TryGetValue(entry.SourceFile, out var data))
            {
                object[] arguments = { entry.SourceFile, null, null };
                loader.Invoke(null, arguments);
                cache[entry.SourceFile] = data = (Dictionary<string, string>)arguments[1];
            }
            if (data.TryGetValue(entry.Key, out var text) && text != entry.SourceText &&
                (string)normalize.Invoke(null, new object[] { text }) == (string)normalize.Invoke(null, new object[] { entry.SourceText })) recovered++;
        }
        Console.WriteLine("READ-ONLY installed report audit: " + patches.Count + " translation-package entries across " + patches.Select(e => e.PackageId).Distinct().Count() + " packages excluded; " + recovered + " literal-newline source comparisons recovered. Report unchanged.");
    }
}
