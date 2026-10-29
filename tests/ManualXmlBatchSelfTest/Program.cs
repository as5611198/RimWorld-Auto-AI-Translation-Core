using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Newtonsoft.Json;
using AutoTranslator_Core;
using Store = AutoTranslator_Core.ManualXmlBatchStore;

internal static class Program
{
    private static int passed;
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "ATC-BatchXml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (args.Length == 0) Run(root);
            if (args.Length == 0 || args[0] == "numeric") NumericKeys(root);
            if (args.Length == 0 || args[0] == "long") LongBatchPath(root);
            Console.WriteLine("PASS: " + passed + " batch XML checks; CLR=" + Environment.Version);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.GetType().FullName + ": " + ex.Message); return 1; }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ATC-BatchXml-")), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path");
            Directory.Delete(root, true);
        }
    }

    private static Store.ModData Mod(string id, string original, string translated)
    {
        return new Store.ModData { PackageId = id, Name = id,
            Categories = new Dictionary<string, List<Store.Entry>>(StringComparer.OrdinalIgnoreCase) {
                { "Keyed", new List<Store.Entry> { new Store.Entry { Key = "SharedKey", OriginalText = original, TranslatedText = translated } } },
                { "ThingDef", new List<Store.Entry> { new Store.Entry { Key = "Thing.label", OriginalText = "A thing", TranslatedText = "舊物品" } } }
            } };
    }

    private static void Edit(string path, string key, string text)
    {
        var doc = new XmlDocument(); doc.Load(path);
        XmlNode node = doc.DocumentElement.SelectSingleNode(key);
        if (node == null) { node = doc.CreateElement(key); doc.DocumentElement.AppendChild(node); }
        node.InnerText = text; doc.Save(path);
    }

    private static void Run(string root)
    {
        const string language = "ChineseTraditional";
        var a = Mod("author.first", "Hello {0} <color=red>world</color> & friends\n第二行", "已有譯文 {0}");
        var b = Mod("author.second", "Another source", "已有第二譯文");
        string batch = Path.Combine(root, "roundtrip");
        Check(Store.Export(batch, language, new[] { a, b }) == 4, "exports every category of two selected mods");
        var manifest = Store.ReadManifest(batch, language);
        string aPath = Store.XmlPath(batch, a.PackageId, language, "Keyed");
        string bPath = Store.XmlPath(batch, b.PackageId, language, "Keyed");
        Check(File.Exists(aPath) && File.Exists(bPath), "same key in different mods has separate files");
        Check(File.Exists(Store.XmlPath(batch, a.PackageId, language, "ThingDef")), "DefInjected categories retain their own folder");
        Check(Store.Preview(batch, language, manifest, a).Changes.Count == 0, "unmodified exported originals never overwrite existing translations");
        Edit(aPath, "SharedKey", "你好 {0} <color=red>世界</color> & 朋友\n第二行");
        string editedBefore = File.ReadAllText(aPath);
        var planA = Store.Preview(batch, language, manifest, a);
        Check(planA.Changes.Count == 1 && planA.Changes[0].TranslatedText.Contains("<color=red>世界</color> & 朋友"), "edited Unicode and escaped rich text survive round trip");
        Check(Store.Preview(batch, language, manifest, b).Changes.Count == 0, "import does not leak identical keys between mods");
        Check(a.Categories["Keyed"][0].TranslatedText == "已有譯文 {0}" && File.ReadAllText(aPath) == editedBefore,
            "preview changes neither current translations nor input files");
        Check(Store.StillMatches(planA.Changes[0], a.Categories["Keyed"][0]), "fresh preview matches current entry");
        a.Categories["Keyed"][0].TranslatedText = "另一個使用者的編輯";
        Check(!Store.StillMatches(planA.Changes[0], a.Categories["Keyed"][0]), "translation changed after preview is a conflict");
        a.Categories["Keyed"][0].TranslatedText = "已有譯文 {0}";
        string oldSource = a.Categories["Keyed"][0].OriginalText;
        a.Categories["Keyed"][0].OriginalText = "New mod version source";
        Check(Store.Preview(batch, language, manifest, a).Changes.Count == 0, "source updated after export is skipped");
        Check(!Store.StillMatches(planA.Changes[0], a.Categories["Keyed"][0]), "source changed after preview is a conflict");
        a.Categories["Keyed"][0].OriginalText = oldSource;
        Edit(aPath, "UnknownKey", "不屬於此模組");
        Check(Store.Preview(batch, language, manifest, a).Skipped == 1, "unknown XML keys cannot enter the pack");
        Edit(aPath, "SharedKey", "");
        Check(Store.Preview(batch, language, manifest, a).Changes.Count == 0, "empty edited values never delete existing translations");
        Edit(aPath, "SharedKey", "缺少格式參數");
        Check(Store.Preview(batch, language, manifest, a, (value, original) => value.Contains("{0}")).Changes.Count == 0,
            "format validation can reject a missing placeholder before applying");
        Throws(() => Store.ReadManifest(batch, "French"), "batch cannot be imported into a different target language");
        Throws(() => Store.Export(batch, language, new[] { a }), "re-export cannot overwrite edited batch files");
        Check(File.ReadAllText(aPath).Contains("缺少格式參數"), "failed overwrite preserves edited input");
        File.Delete(Store.XmlPath(batch, a.PackageId, language, "ThingDef"));
        Check(Store.Preview(batch, language, manifest, a).MissingFiles == 1, "missing category file is reported without deleting translations");
        File.WriteAllText(aPath, "<LanguageData><SharedKey>one</SharedKey><SharedKey>two</SharedKey></LanguageData>");
        Check(Store.Preview(batch, language, manifest, a).Error != null, "duplicate XML keys fail the mod preview");
        File.WriteAllText(aPath, "<LanguageData><SharedKey><color>nested</color></SharedKey></LanguageData>");
        Check(Store.Preview(batch, language, manifest, a).Error != null, "unescaped nested XML is rejected instead of silently stripping tags");
        File.WriteAllText(aPath, "<!DOCTYPE LanguageData [<!ENTITY leak SYSTEM 'file:///nonexistent-secret'>]><LanguageData><SharedKey>&leak;</SharedKey></LanguageData>");
        Check(Store.Preview(batch, language, manifest, a).Error != null, "external XML entities are prohibited");
        Edit(bPath, "SharedKey", "第二個模組的新譯文");
        Check(Store.Preview(batch, language, manifest, b).Changes.Count == 1, "a broken mod does not prevent another mod from importing");
        Throws(() => Store.XmlPath(batch, "../outside", language, "Keyed"), "package folder cannot escape batch root");
        Throws(() => Store.XmlPath(batch, a.PackageId, language, ".."), "category folder cannot escape batch root");
        Throws(() => Store.XmlPath(batch, a.PackageId, language, "C:\\outside"), "absolute category paths are rejected");
        manifest.Mods.Add(manifest.Mods[0]);
        File.WriteAllText(Path.Combine(batch, "manifest.json"), JsonConvert.SerializeObject(manifest));
        Throws(() => Store.ReadManifest(batch, language), "ambiguous duplicate package records are rejected");
    }

    private static void NumericKeys(string root)
    {
        const string language = "ChineseTraditional";
        string[] keys = {
            "2FemaleMissionaryThreesomeTrain_BEV_Stage5.label", "2MaleMissionaryThreesomeTrain_BEV_Stage5.label",
            "2Male2MissionaryThreesomeTrain_BEV_Stage5.label", "2FemaleMissionaryThreesomeTrain_BEV_Intro1.label",
            "2Male2MissionaryThreesomeTrain_BEV_Intro1.label", "2MaleMissionaryThreesomeTrain_BEV_Intro1.label",
            "2FemaleDoggystyleThreesomeTrain_BEV_Stage5.label", "2MaleDoggystyleThreesomeTrain_BEV_Stage5.label",
            "2Male2DoggystyleThreesomeTrain_BEV_Stage5.label", "2FemaleDoggystyleThreesomeTrain_BEV_Intro1.label",
            "2Male2DoggystyleThreesomeTrain_BEV_Intro1.label", "2MaleDoggystyleThreesomeTrain_BEV_Intro1.label",
            "21stCenturyArsenal_HandsSettings.label", "_x0032_Literal.label", "Normal.label"
        };
        var mod = new Store.ModData { PackageId = "test.numeric", Name = "numeric" };
        mod.Categories["ThingDef"] = keys.Select(k => new Store.Entry { Key = k, OriginalText = "Source & <tag>\nline", TranslatedText = "舊譯文" }).ToList();
        string batch = Path.Combine(root, "numeric");
        Check(Store.Export(batch, language, new[] { mod }) == keys.Length, "batch exports every numeric and ordinary key without skipping");
        string path = Store.XmlPath(batch, mod.PackageId, language, "ThingDef");
        var xml = new XmlDocument(); xml.Load(path);
        Check(xml.SelectNodes("/LanguageData/rep").Count == 13, "numeric keys use native DefInjected rep syntax");
        Check(xml.SelectSingleNode("/LanguageData/_x0032_Literal.label") != null, "literal XML escape-looking key is not decoded or renamed");
        var manifest = Store.ReadManifest(batch, language);
        Check(Store.Preview(batch, language, manifest, mod).Unchanged == keys.Length, "original numeric exports roundtrip without phantom changes");
        foreach (XmlElement rep in xml.SelectNodes("/LanguageData/rep")) rep["trans"].InnerText = "新譯文 & <tag>\n第二行";
        xml.Save(path);
        var plan = Store.Preview(batch, language, manifest, mod);
        Check(plan.Error == null && plan.Changes.Count == 13 && plan.Changes.All(c => keys.Contains(c.Key) && c.TranslatedText == "新譯文 & <tag>\n第二行"),
            "numeric import preserves original keys, rich text and newlines");
        xml.DocumentElement.AppendChild(xml.DocumentElement.FirstChild.CloneNode(true)); xml.Save(path);
        Check(Store.Preview(batch, language, manifest, mod).Error != null, "duplicate rep paths are rejected");
        File.WriteAllText(path, "<LanguageData><rep><path>21stCenturyArsenal_HandsSettings.label</path><trans><bad>nested</bad></trans></rep></LanguageData>");
        Check(Store.Preview(batch, language, manifest, mod).Error != null, "rep text must escape nested markup");
    }

    private static void LongBatchPath(string root)
    {
        const string language = "ChineseTraditional";
        var mod = new Store.ModData { PackageId = "chezhou.rimsim.restaurantextension", Name = "long path" };
        const string category = "RimSimRestaurantExtension.Inventory.RestaurantProductDef";
        mod.Categories[category] = new List<Store.Entry> { new Store.Entry { Key = "Item.label", OriginalText = "A product" } };
        string batchBase = Path.Combine(root, "Manual_XML_Batches");
        string probe = Store.XmlPath(Path.Combine(batchBase, "b"), mod.PackageId, language, category);
        string batch = Path.Combine(batchBase, new string('b', 252 - probe.Length + 1));
        string path = Store.XmlPath(batch, mod.PackageId, language, category);
        Check(path.Length == 252, "fixture reproduces a valid 252-character destination");
        Check(Store.Export(batch, language, new[] { mod }) == 1 && File.Exists(path), "batch exports near MAX_PATH on legacy runtime");
        Check(Store.ReadManifest(batch, language).Mods.Count == 1, "long-path export commits manifest last");
        Check(Store.Preview(batch, language, Store.ReadManifest(batch, language), mod).Unchanged == 1, "long-path batch is readable for import");
        Check(Directory.GetFiles(Path.GetDirectoryName(path)).All(p => p.EndsWith(".xml")), "long-path export leaves no temporary files");
    }

    private static void Check(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException("FAIL: " + name);
        passed++; Console.WriteLine("PASS: " + name);
    }
    private static void Throws(Action action, string name)
    {
        try { action(); }
        catch (Exception) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL: " + name);
    }
}
