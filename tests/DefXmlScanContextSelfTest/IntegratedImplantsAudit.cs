using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static void AuditIntegratedImplants(string output)
    {
        const string root = "E:/SteamLibrary/steamapps/workshop/content/294100/3223443793";
        const string data = "E:/SteamLibrary/steamapps/common/RimWorld/Data";
        var previousSettings = AutoTranslatorMod.Settings;
        var previousPacks = LoadedModManager.RunningModsListForReading.ToArray();
        var packs = new List<ModContentPack>();
        foreach (var entry in new[] { ("Core", "ludeon.rimworld"), ("Royalty", "ludeon.rimworld.royalty"),
            ("Ideology", "ludeon.rimworld.ideology"), ("Biotech", "ludeon.rimworld.biotech"),
            ("Anomaly", "ludeon.rimworld.anomaly"), ("Odyssey", "ludeon.rimworld.odyssey") })
        {
            string path = Path.Combine(data, entry.Item1);
            if (!Directory.Exists(path)) continue;
            var pack = Pack(entry.Item2, "unused", packs.Count, "unused");
            Set(pack, "rootDirInt", new DirectoryInfo(path)); pack.foldersToLoadDescendingOrder = new List<string> { path };
            packs.Add(pack);
        }
        var active = new HashSet<string>(packs.Select(p => p.PackageId), StringComparer.OrdinalIgnoreCase) { "brrainz.harmony", "lts.i" };
        var roots = (List<string>)typeof(AutoTranslatorScanner).GetMethod("ResolveInactiveRawDefRoots", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { root, active });
        var target = Pack("lts.i", "unused", packs.Count, "unused");
        Set(target, "rootDirInt", new DirectoryInfo(root)); target.foldersToLoadDescendingOrder = roots;
        packs.Add(target);
        var counts = new List<object>();
        try
        {
            AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Simplified };
            SetRunning(packs.ToArray()); Invalidate();
            var defsRoots = AutoTranslatorScanner.GetAllEffectiveDefsPaths("lts.i", root);
            var languageRoots = AutoTranslatorScanner.GetAllEffectiveLangPaths("lts.i", root);
            var union = new Dictionary<string, Dictionary<string, string>>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (string defs in defsRoots)
            {
                var extracted = AutoTranslatorScanner.ExtractEnglishFromRawDefs(defs);
                counts.Add(new { path = defs, files = Directory.GetFiles(defs, "*.xml", SearchOption.AllDirectories).Length,
                    categories = extracted.Count, keys = extracted.Sum(p => p.Value.Count) });
                foreach (var category in extracted)
                {
                    if (!union.TryGetValue(category.Key, out var entries)) union[category.Key] = entries = new Dictionary<string, string>();
                    foreach (var value in category.Value) entries[value.Key] = value.Value;
                }
            }
            var report = new { gameVersion = Version, packageId = "lts.i", root, enabledInPlayerConfig = false,
                testScope = "Real source files; isolated Core + DLC load-folder snapshot; no API, translation generation, native injection, or game launch.",
                active = active.OrderBy(s => s).ToArray(), contentRoots = roots, defsRoots, languageRoots, counts,
                totalKeys = union.Sum(p => p.Value.Count), categories = union.ToDictionary(p => p.Key, p => p.Value.Count),
                samples = union.ToDictionary(p => p.Key, p => p.Value.Take(5).ToDictionary(k => k.Key, k => k.Value)),
                elapsedMs = watch.ElapsedMilliseconds };
            File.WriteAllText(output, Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
            Console.WriteLine("Integrated Implants extraction: " + union.Sum(p => p.Value.Count) + " keys / " + defsRoots.Count + " Def roots; " + watch.ElapsedMilliseconds + " ms; " + output);
        }
        finally { SetRunning(previousPacks); AutoTranslatorMod.Settings = previousSettings; Invalidate(); }
    }
}
