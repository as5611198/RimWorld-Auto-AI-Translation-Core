using System.Xml;
using AutoTranslator_Core;
using Verse;
using RimWorld.IO;
using System.Reflection;
using System.Collections;
using HarmonyLib;

internal static partial class Suite
{
    static void TestNumericDefInjectedXml()
    {
        const string numeric = "21stCenturyArsenal_HandsSettings.label";
        const string literal = "_x0032_Literal.label";
        const string translated = "測試譯文 & <tag>\n第二行";
        string path = Path.Combine(temporary, "Numeric", "Languages", "ChineseTraditional", "DefInjected", "ThingDef", "ManualTranslation.xml");
        AutoTranslatorScanner.SaveXml(path, new Dictionary<string, string> {
            [numeric] = translated, [literal] = "另一個字面鍵", ["Normal.label"] = "正常鍵"
        });
        var xml = new XmlDocument(); xml.Load(path);
        Check(xml.SelectSingleNode("/LanguageData/rep/path")?.InnerText == numeric, "single export preserves numeric DefInjected key in native rep syntax");
        var native = new DefInjectionPackage(typeof(ThingDef));
        native.AddDataFromFile(new NumericXmlFile(path), out bool parseError, File.ReadAllText(path));
        Check(!parseError && native.loadErrors.Count == 0 && native.injections.ContainsKey(numeric) && native.injections[numeric].injection == translated,
            "native DefInjected reader preserves numeric key and translation (host game for product " + Version + ")");
        Check(native.injections.ContainsKey(literal) && !native.injections.ContainsKey("2Literal.label"), "native reader preserves literal escape-looking keys");
        var loaded = AutoTranslatorScanner.LoadXmlFileToDict(path);
        Check(loaded.Count == 3 && loaded[numeric] == translated && loaded[literal] == "另一個字面鍵", "scanner reloads rep translations under original keys");
        var sourceLoader = typeof(AutoTranslatorScanner).GetMethod("TryLoadUnresolvedLanguageFile", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        object[] arguments = { path, null, null };
        Check((bool)sourceLoader.Invoke(null, arguments) && ((Dictionary<string, string>)arguments[1])[numeric] == translated,
            "unresolved source reader shares numeric-key semantics");
        string encoded = "<LanguageData><_x0032_1stCenturyArsenal_HandsSettings.label>wrong key</_x0032_1stCenturyArsenal_HandsSettings.label></LanguageData>";
        var wrong = new DefInjectionPackage(typeof(ThingDef));
        wrong.AddDataFromFile(new NumericXmlFile(path), out parseError, encoded);
        Check(!parseError && !wrong.injections.ContainsKey(numeric), "native reader does not decode XmlConvert.EncodeName output");
        TestNumericBatchWorkbenchRoundtrip();
    }

    static void TestNumericBatchWorkbenchRoundtrip()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        const string id = "test.numericworkbench";
        const string key = "21stCenturyArsenal_HandsSettings.label";
        string root = Path.Combine(temporary, "NumericWorkbench");
        Write("NumericWorkbench/Defs/Numeric.xml", "<Defs><ThingDef><defName>21stCenturyArsenal_HandsSettings</defName><label>Weapon hand settings</label></ThingDef><ThingDef><defName>Normal</defName><label>Normal item</label></ThingDef></Defs>");
        SetRunning(Pack(id, root, 0, root)); Invalidate();
        selectionPack = Path.Combine(temporary, "NumericBatchPack");
        var patcher = new Harmony("atc.numeric-batch-workbench");
        patcher.Patch(typeof(AutoTranslatorScanner).GetMethod("GetLocalPackPath", flags),
            prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(FixturePackPath), flags)));
        var oldSettings = AutoTranslatorMod.Settings;
        AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Traditional };
        try
        {
            var workbench = typeof(TranslationWorkbenchTab);
            object snapshot = Activator.CreateInstance(workbench.GetNestedType("WorkbenchModSnapshot", BindingFlags.NonPublic), true);
            foreach (var pair in new Dictionary<string, object> { ["PackageId"] = id, ["ModName"] = id, ["RootDir"] = root,
                ["TargetLang"] = TargetLanguage.Traditional, ["TargetLangFolder"] = "ChineseTraditional" })
                snapshot.GetType().GetField(pair.Key).SetValue(snapshot, pair.Value);
            object[] readArgs = { snapshot, null };
            var data = (Dictionary<string, List<TranslationWorkbenchTab.WorkbenchItem>>)workbench.GetMethod("ReadWorkbenchData", flags).Invoke(null, readArgs);
            Check(data["ThingDef"].Any(i => i.Key == key), "actual workbench source scan retains numeric Def name");
            object captured = workbench.GetMethod("CaptureBatchXmlData", flags).Invoke(null, new object[] { snapshot, data });
            Array mods = Array.CreateInstance(captured.GetType(), 1); mods.SetValue(captured, 0);
            var store = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.ManualXmlBatchStore");
            string batch = Path.Combine(selectionPack, "Manual_XML_Batches", "roundtrip");
            Check((int)store.GetMethod("Export", flags).Invoke(null, new object[] { batch, "ChineseTraditional", mods }) == 2,
                "compiled batch exports numeric and ordinary entries captured from real scanner");
            string xmlPath = (string)store.GetMethod("XmlPath", flags).Invoke(null, new object[] { batch, id, "ChineseTraditional", "ThingDef" });
            var xml = new XmlDocument(); xml.Load(xmlPath);
            xml.SelectSingleNode("/LanguageData/rep/trans").InnerText = "批量翻譯的武器手部設定"; xml.Save(xmlPath);
            object manifest = store.GetMethod("ReadManifest", flags).Invoke(null, new object[] { batch, "ChineseTraditional" });
            object plan = store.GetMethod("Preview", flags).Invoke(null, new object[] { batch, "ChineseTraditional", manifest, captured, null });
            var changes = (IList)plan.GetType().GetField("Changes").GetValue(plan);
            Check(changes.Count == 1 && (string)changes[0].GetType().GetField("Key").GetValue(changes[0]) == key,
                "compiled batch import stages numeric entry under its original key");
            var item = data["ThingDef"].Single(i => i.Key == key);
            item.TranslatedText = (string)changes[0].GetType().GetField("TranslatedText").GetValue(changes[0]);
            workbench.GetMethod("RefreshWorkbenchItemModifiedState", flags).Invoke(null, new object[] { item });
            object save = workbench.GetMethods(flags).Single(m => m.Name == "CreateSaveSnapshot" && m.GetParameters().Length == 2)
                .Invoke(null, new object[] { snapshot, data });
            object result = workbench.GetMethod("SaveSnapshot", flags).Invoke(null, new object[] { save });
            Check(string.IsNullOrEmpty((string)result.GetType().GetField("Error").GetValue(result)) &&
                (int)result.GetType().GetField("SavedCount").GetValue(result) == 1, "numeric batch edit saves through actual workbench persistence");
            string saved = Path.Combine(selectionPack, "Languages", "ChineseTraditional", "DefInjected", "ThingDef", "test_numericworkbench_AutoTranslated.xml");
            var native = new DefInjectionPackage(typeof(ThingDef));
            native.AddDataFromFile(new NumericXmlFile(saved), out bool parseError, File.ReadAllText(saved));
            Check(!parseError && native.injections.ContainsKey(key) && native.injections[key].injection == item.TranslatedText,
                "native reader accepts actual batch-to-pack saved translation");
            readArgs = new object[] { snapshot, null };
            var reloaded = (Dictionary<string, List<TranslationWorkbenchTab.WorkbenchItem>>)workbench.GetMethod("ReadWorkbenchData", flags).Invoke(null, readArgs);
            Check(reloaded["ThingDef"].Single(i => i.Key == key).TranslatedText == item.TranslatedText,
                "actual workbench reload retains numeric batch translation");
        }
        finally { AutoTranslatorMod.Settings = oldSettings; patcher.UnpatchAll("atc.numeric-batch-workbench"); }
    }

    sealed class NumericXmlFile : VirtualFile
    {
        readonly string path;
        public NumericXmlFile(string path) { this.path = path; }
        public override string Name => Path.GetFileName(path);
        public override string FullPath => path;
        public override bool Exists => File.Exists(path);
        public override long Length => new FileInfo(path).Length;
        public override Stream CreateReadStream() => File.OpenRead(path);
        public override string ReadAllText() => File.ReadAllText(path);
        public override string[] ReadAllLines() => File.ReadAllLines(path);
        public override byte[] ReadAllBytes() => File.ReadAllBytes(path);
    }
}
