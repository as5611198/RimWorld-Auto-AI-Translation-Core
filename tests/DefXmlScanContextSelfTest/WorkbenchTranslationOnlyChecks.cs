using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;

internal static partial class Suite
{
    static void TestWorkbenchTranslationOnlyEntries()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        const string package = "fixture.workspacedata";
        string root = Path.Combine(temporary, "WorkspaceOnlyMod"); Directory.CreateDirectory(root);
        string previousPack = selectionPack; var previousSettings = AutoTranslatorMod.Settings;
        var previousRunning = Verse.LoadedModManager.RunningModsListForReading.ToArray();
        var harmony = new Harmony("atc.workbench.translation-only");
        harmony.Patch(typeof(AutoTranslatorScanner).GetMethod("GetLocalPackPath", flags),
            prefix: new HarmonyMethod(typeof(Suite), nameof(FixturePackPath)));
        var workbench = typeof(TranslationWorkbenchTab);
        var snapshotType = workbench.GetNestedType("WorkbenchModSnapshot", BindingFlags.NonPublic);
        var read = workbench.GetMethod("ReadWorkbenchData", flags);
        object snapshot = Activator.CreateInstance(snapshotType, true);
        void Snapshot(string lang = "ChineseSimplified")
        {
            foreach (var pair in new Dictionary<string, object> { ["PackageId"] = package, ["ModName"] = "Workspace fixture",
                ["RootDir"] = root, ["TargetLang"] = lang == "ChineseSimplified" ? TargetLanguage.Simplified : TargetLanguage.Traditional,
                ["TargetLangFolder"] = lang }) snapshotType.GetField(pair.Key).SetValue(snapshot, pair.Value);
        }
        Dictionary<string, List<TranslationWorkbenchTab.WorkbenchItem>> Read() =>
            (Dictionary<string, List<TranslationWorkbenchTab.WorkbenchItem>>)read.Invoke(null, new object[] { snapshot, null });
        void Xml(string relative, params (string key, string value)[] pairs) => AutoTranslatorScanner.SaveXml(Path.Combine(selectionPack, relative), pairs.ToDictionary(p => p.key, p => p.value));
        try
        {
            selectionPack = Path.Combine(temporary, "WorkspaceOnlyPack");
            AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Simplified };
            SetRunning(Pack(package, root, 0, root)); Invalidate(); Snapshot();
            Xml("Upload_Workspace/" + package + "/ChineseSimplified/Keyed/translations.xml", ("WorkspaceCaption", "工作区标题"));
            Xml("Upload_Workspace/" + package + "/ChineseSimplified/DefInjected/ThingDef/translations.xml", ("Widget.label", "工作区物品"));
            var data = Read();
            Check(data.TryGetValue("Keyed", out var keyed) && keyed.Single().TranslatedText == "工作区标题", "workbench shows translated workspace Keyed entries when original language files are unavailable");
            Check(data.TryGetValue("ThingDef", out var defs) && defs.Single().TranslatedText == "工作区物品", "workbench shows translated workspace DefInjected categories without original Def files");
            Check(string.IsNullOrEmpty(keyed[0].OriginalText) && string.IsNullOrEmpty(defs[0].OriginalText), "translation-only workbench rows do not invent original text");
            Check(keyed[0].Category == "Keyed" && defs[0].Category == "ThingDef" && !keyed[0].IsModified, "translation-only rows retain category, saved translation and clean edit state");

            Xml("Languages/ChineseSimplified/Keyed/fixture_workspacedata_AutoTranslated.xml", ("PackOnlyCaption", "已空降标题"));
            Xml("Languages/ChineseSimplified/DefInjected/RecipeDef/fixture_workspacedata_AutoTranslated.xml", ("InstallWidget.label", "安装物品"));
            Xml("Languages/ChineseSimplified/Keyed/fixture_workspacedata_neighbor_AutoTranslated.xml", ("OtherPackage", "别的模组"));
            Xml("Languages/ChineseSimplified/DefInjected/ThingDef/fixture_workspacedata_neighbor_AutoTranslated.xml", ("Foreign.label", "别的物品"));
            data = Read();
            Check(data["Keyed"].Any(i => i.Key == "PackOnlyCaption" && i.TranslatedText == "已空降标题"), "canonical landed Keyed output is visible without source files");
            Check(data["RecipeDef"].Single().TranslatedText == "安装物品", "canonical landed DefInjected output supplies otherwise missing categories");
            Check(data.SelectMany(p => p.Value).All(i => i.Key != "OtherPackage" && i.Key != "Foreign.label"), "workbench keeps ownership guards for packages with similar output filenames");
            Xml("Upload_Workspace/" + package + "/ChineseSimplified/DefInjected/ThingDef/protected.xml", ("Widget.graphicData.texPath", "Textures/Secret"), ("Widget.defName", "InternalName"));
            data = Read();
            Check(data["ThingDef"].All(i => i.Key != "Widget.graphicData.texPath" && i.Key != "Widget.defName"), "translation-only rows retain the local structural-data exclusions");

            var captured = workbench.GetMethod("CaptureBatchXmlData", flags).Invoke(null, new object[] { snapshot, data });
            var mods = Array.CreateInstance(captured.GetType(), 1); mods.SetValue(captured, 0);
            var store = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.ManualXmlBatchStore");
            Check((int)store.GetMethod("Export", flags).Invoke(null, new object[] { Path.Combine(selectionPack, "ExportWithoutSource"), "ChineseSimplified", mods }) == 0,
                "missing originals are not exported as invented source text for another paid translation");

            Xml("Upload_Workspace/" + package + "/ChineseSimplified/Keyed/translations.xml", ("WorkspaceCaption", "工作区标题"), ("CountCaption", "已安装 {0} 个"));
            data = Read();
            var counter = data["Keyed"].Single(i => i.Key == "CountCaption");
            counter.TranslatedText = "现已安装 {0} 个"; counter.IsModified = true;
            var captureSave = workbench.GetMethod("CreateSaveSnapshot", flags, null, new[] { snapshotType, data.GetType() }, null);
            var save = workbench.GetMethod("SaveSnapshot", flags);
            AutoTranslatorSettings.ResetPipelineCancellation();
            var saved = save.Invoke(null, new[] { captureSave.Invoke(null, new object[] { snapshot, data }) });
            Check(string.IsNullOrEmpty((string)saved.GetType().GetField("Error").GetValue(saved)), "source-unavailable manual edits validate against their saved translation and preserve placeholders");
            string canonical = Path.Combine(selectionPack, "Languages/ChineseSimplified/Keyed/fixture_workspacedata_AutoTranslated.xml");
            var savedXml = AutoTranslatorScanner.LoadXmlFileToDict(canonical);
            Check(savedXml["CountCaption"] == "现已安装 {0} 个" && savedXml["WorkspaceCaption"] == "工作区标题" && savedXml["PackOnlyCaption"] == "已空降标题",
                "saving a translation-only row preserves neighboring workspace and landed translations");
            string beforeInvalid = File.ReadAllText(canonical);
            counter.TranslatedText = "错误的参数 {1}";
            var rejected = save.Invoke(null, new[] { captureSave.Invoke(null, new object[] { snapshot, data }) });
            Check(!string.IsNullOrEmpty((string)rejected.GetType().GetField("Error").GetValue(rejected)) && File.ReadAllText(canonical) == beforeInvalid,
                "missing originals do not allow placeholder changes to corrupt an existing translation file");
            Snapshot("ChineseTraditional");
            Check(Read().Count == 0, "workbench never borrows translation-only rows from another target language");
            Snapshot();

            Write("WorkspaceOnlyMod/Languages/English/Keyed/Source.xml", "<LanguageData><WorkspaceCaption>Original caption</WorkspaceCaption></LanguageData>");
            Write("WorkspaceOnlyMod/Defs/Widget.xml", "<Defs><ThingDef><defName>Widget</defName><label>Original item</label></ThingDef></Defs>");
            Invalidate(); AutoTranslatorScanner.NotifyTranslationFilesChanged(root);
            data = Read();
            Check(data["Keyed"].Single(i => i.Key == "WorkspaceCaption").OriginalText == "Original caption", "original Keyed source takes precedence when it becomes available");
            Check(data["ThingDef"].Single(i => i.Key == "Widget.label").OriginalText == "Original item", "original Def source returns to the existing translated row without duplication");
            Check(data["ThingDef"].Count(i => i.Key == "Widget.label") == 1, "source and translated output merge into exactly one workbench row");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); selectionPack = previousPack; AutoTranslatorMod.Settings = previousSettings;
            SetRunning(previousRunning); Invalidate();
        }
    }
}
