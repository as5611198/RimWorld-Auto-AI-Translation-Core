using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;

internal static partial class Program
{
    private static object Field(object value, string name) => value.GetType().GetField(name).GetValue(value);
    private static void Field(object value, string name, object data) => value.GetType().GetField(name).SetValue(value, data);

    private static void TestWorkbenchBatch(Assembly mod, string temporary)
    {
        Type workbench = mod.GetType("AutoTranslator_Core.TranslationWorkbenchTab", true);
        Type scanner = mod.GetType("AutoTranslator_Core.AutoTranslatorScanner", true);
        MethodInfo formatCheck = scanner.GetMethod("IsManualXmlTranslationFormatValid", Static);
        Check((bool)formatCheck.Invoke(null, new object[] { "你好 {0}", "Hello {0}" }), "manual XML accepts matching format arguments");
        Check(!(bool)formatCheck.Invoke(null, new object[] { "你好", "Hello {0}" }), "manual XML rejects missing format arguments");
        Check(!(bool)formatCheck.Invoke(null, new object[] { "你好 {0} {1}", "Hello {0}" }), "manual XML rejects introduced format arguments");
        Type targetLanguage = mod.GetType("AutoTranslator_Core.TargetLanguage", true);
        object traditional = Enum.Parse(targetLanguage, "Traditional");
        Type settingsType = mod.GetType("AutoTranslator_Core.AutoTranslatorSettings", true);
        object settings = Activator.CreateInstance(settingsType);
        Field(settings, "TargetLang", traditional);
        mod.GetType("AutoTranslator_Core.AutoTranslatorMod", true).GetField("Settings").SetValue(null, settings);
        string languageFolder = (string)scanner.GetMethod("GetFolderNameByLanguage", Static).Invoke(null, new[] { traditional });
        Type snapshotType = workbench.GetNestedType("WorkbenchModSnapshot", BindingFlags.NonPublic);
        Type batchStore = mod.GetType("AutoTranslator_Core.ManualXmlBatchStore", true);
        Type modDataType = batchStore.GetNestedType("ModData", BindingFlags.NonPublic);
        IList captured = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(modDataType));
        var snapshots = new List<object>();
        var dataByMod = new List<object>();
        for (int index = 0; index < 2; index++)
        {
            string id = "test.batch" + index;
            string sourceRoot = Path.Combine(temporary, id);
            string english = Path.Combine(sourceRoot, "Languages", "English", "Keyed", "Source.xml");
            string native = Path.Combine(sourceRoot, "Languages", languageFolder, "Keyed", "Native.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(english));
            Directory.CreateDirectory(Path.GetDirectoryName(native));
            File.WriteAllText(english, "<LanguageData><SharedKey>Hello world</SharedKey><KeepKey>Keep source</KeepKey><NativeKey>Native source</NativeKey></LanguageData>");
            File.WriteAllText(native, "<LanguageData><NativeKey>原作者提供的翻譯</NativeKey></LanguageData>");
            string ownOutput = Path.Combine(packRoot, "Languages", languageFolder, "Keyed", id.Replace('.', '_') + "_AutoTranslated.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(ownOutput));
            File.WriteAllText(ownOutput, "<LanguageData><SharedKey>既有譯文</SharedKey><KeepKey>保留現有內容</KeepKey></LanguageData>");
            object snapshot = Activator.CreateInstance(snapshotType, true);
            Field(snapshot, "PackageId", id); Field(snapshot, "ModName", id); Field(snapshot, "RootDir", sourceRoot);
            Field(snapshot, "TargetLang", traditional); Field(snapshot, "TargetLangFolder", languageFolder);
            object data = workbench.GetMethod("ReadWorkbenchData", Static).Invoke(null, new object[] { snapshot, null });
            Check(((IDictionary)data).Contains("Keyed"), "batch reads real workbench sources for " + id);
            snapshots.Add(snapshot); dataByMod.Add(data);
            captured.Add(workbench.GetMethod("CaptureBatchXmlData", Static).Invoke(null, new[] { snapshot, data }));
        }
        string batchRoot = Path.Combine(packRoot, "Manual_XML_Batches", "batch-exchange");
        Check((bool)scanner.GetMethod("IsWorkbenchManualExportPath", Static).Invoke(null,
            new object[] { Path.Combine(packRoot, "Manual_XML_Batches", "example", "test.batch0", languageFolder, "Keyed", "ManualTranslation.xml") }),
            "raw batch originals are excluded from translation caches and cloud uploads");
        int exported = (int)batchStore.GetMethod("Export", Static).Invoke(null, new object[] { batchRoot, languageFolder, captured });
        Check(exported == 6, "batch export captures both real workbench snapshots");
        var cacheFiles = (IEnumerable)scanner.GetMethod("GetXmlFilesForTranslationCache", Static).Invoke(null, new object[] { packRoot, SearchOption.AllDirectories });
        Check(cacheFiles.Cast<string>().All(p => p.IndexOf("Manual_XML_Batches", StringComparison.OrdinalIgnoreCase) < 0),
            "translation file enumeration excludes exported batch originals");
        object manifest = batchStore.GetMethod("ReadManifest", Static).Invoke(null, new object[] { batchRoot, languageFolder });
        string foreign = Path.Combine(packRoot, "Languages", languageFolder, "Keyed", "test_other_AutoTranslated.xml");
        File.WriteAllText(foreign, "<LanguageData><ForeignKey>其他模組的譯文</ForeignKey></LanguageData>");
        string foreignBefore = File.ReadAllText(foreign);
        for (int index = 0; index < 2; index++)
        {
            string id = "test.batch" + index;
            string editedXml = (string)batchStore.GetMethod("XmlPath", Static).Invoke(null, new object[] { batchRoot, id, languageFolder, "Keyed" });
            var xml = new XmlDocument(); xml.Load(editedXml);
            xml.DocumentElement.SelectSingleNode("SharedKey").InnerText = "批次修改的譯文" + index;
            xml.Save(editedXml);
            object plan = batchStore.GetMethod("Preview", Static).Invoke(null, new object[] { batchRoot, languageFolder, manifest, captured[index], null });
            IList changes = (IList)Field(plan, "Changes");
            Check(changes.Count == 1, "only edited entry is staged for " + id);
            var data = (IDictionary)dataByMod[index];
            IList items = (IList)data["Keyed"];
            object item = items.Cast<object>().Single(i => (string)Field(i, "Key") == "SharedKey");
            Field(item, "TranslatedText", Field(changes[0], "TranslatedText"));
            workbench.GetMethod("RefreshWorkbenchItemModifiedState", Static).Invoke(null, new[] { item });
            MethodInfo captureSave = workbench.GetMethods(Static).Single(m => m.Name == "CreateSaveSnapshot" && m.GetParameters().Length == 2);
            object save = captureSave.Invoke(null, new object[] { snapshots[index], data });
            object result = workbench.GetMethod("SaveSnapshot", Static).Invoke(null, new[] { save });
            Check(string.IsNullOrEmpty((string)Field(result, "Error")) && (int)Field(result, "SavedCount") == 1,
                "batch saves through actual workbench persistence for " + id + ": " + Field(result, "Error"));
            string savedXml = Path.Combine(packRoot, "Languages", languageFolder, "Keyed", id.Replace('.', '_') + "_AutoTranslated.xml");
            xml.Load(savedXml);
            Check(xml.SelectSingleNode("/LanguageData/SharedKey").InnerText == "批次修改的譯文" + index &&
                xml.SelectSingleNode("/LanguageData/KeepKey").InnerText == "保留現有內容" &&
                xml.SelectSingleNode("/LanguageData/NativeKey") == null,
                "batch preserves untouched entries and does not copy read-only native references for " + id);
            object reloaded = workbench.GetMethod("ReadWorkbenchData", Static).Invoke(null, new object[] { snapshots[index], null });
            IList reloadedItems = (IList)((IDictionary)reloaded)["Keyed"];
            Check(reloadedItems.Cast<object>().Any(i => (string)Field(i, "Key") == "SharedKey" &&
                (string)Field(i, "TranslatedText") == "批次修改的譯文" + index), "saved batch translation reloads for " + id);
        }
        Check(File.ReadAllText(foreign) == foreignBefore, "batch persistence preserves other mods' files");
        Check(Directory.GetFiles(packRoot, "*.json", SearchOption.AllDirectories).Any(p => p.IndexOf("Provenance", StringComparison.OrdinalIgnoreCase) >= 0),
            "batch persistence records translation provenance");
    }
}
