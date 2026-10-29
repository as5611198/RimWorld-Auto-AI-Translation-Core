using System.IO.Compression;
using System.Reflection;
using System.Text;
using AutoTranslator_Core;
using HarmonyLib;

internal static partial class Suite
{
    static void TestCloudArchiveCompatibility()
    {
        const string package = "fixture.cloud", language = "ChineseSimplified";
        var commit = typeof(AutoTranslatorCloudClient).GetMethod("CommitCloudArchive", BindingFlags.Static | BindingFlags.NonPublic);
        var hooks = new Harmony("atc.cloud-archive-compatibility");
        var previousPack = selectionPack;
        int fixture = 0;
        string Xml(string key, string value) => "<LanguageData><" + key + ">" + value + "</" + key + "></LanguageData>";
        string Hash(byte[] bytes) { using (var hash = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }
        string Archive(params (string path, string text)[] entries)
        {
            string path = Path.Combine(temporary, "cloud-" + Guid.NewGuid().ToString("N") + ".zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var entry in entries)
                    using (var writer = new StreamWriter(zip.CreateEntry(entry.path).Open(), new UTF8Encoding(false))) writer.Write(entry.text);
            return path;
        }
        string Manifest(string owner, string lang, params (string path, string text)[] entries) => Newtonsoft.Json.JsonConvert.SerializeObject(new {
            Schema = 1, PackageId = owner, Language = lang,
            Files = entries.ToDictionary(e => e.path, e => Hash(Encoding.UTF8.GetBytes(e.text))) });
        string Root() => Path.Combine(selectionPack, "Languages", language);
        string Canonical(string category = "Keyed") => Path.Combine(Root(), category, "fixture_cloud_AutoTranslated.xml");
        string Snapshot() => string.Join("\n", Directory.Exists(selectionPack) ? Directory.GetFiles(selectionPack, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "Backups" + Path.DirectorySeparatorChar)).OrderBy(p => p)
            .Select(p => p + "=" + Hash(File.ReadAllBytes(p))) : new string[0]);
        void Fresh()
        {
            selectionPack = Path.Combine(temporary, "CloudCompatibility" + ++fixture);
            Directory.CreateDirectory(Path.GetDirectoryName(Canonical()));
            File.WriteAllText(Canonical(), Xml("Existing", "保留既有譯文"));
            File.WriteAllText(Path.Combine(Root(), "Keyed", "fixture_cloud_extra_AutoTranslated.xml"), Xml("OtherPackage", "別的模組"));
            File.WriteAllText(Path.Combine(Root(), "Keyed", "player-notes.xml"), Xml("Manual", "玩家手動檔案"));
        }
        void Commit(string archive, Func<bool> canCommit = null, string owner = package, CloudModRecord record = null)
        {
            try { commit.Invoke(null, new object[] { archive, owner, language, record, true, canCommit ?? (() => true) }); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        void Reject(string description, (string path, string text)[] entries, Func<bool> canCommit = null, CloudModRecord record = null)
        {
            Fresh(); string before = Snapshot(); bool rejected = false;
            try { Commit(Archive(entries), canCommit, record: record); } catch (Exception error) when (error is InvalidDataException || error is System.Xml.XmlException || error is OperationCanceledException || error is Newtonsoft.Json.JsonException || error is ArgumentException) { rejected = true; }
            Check(rejected && Snapshot() == before, description + "; existing translations and workspace retained");
        }
        hooks.Patch(typeof(AutoTranslatorScanner).GetMethod("GetLocalPackPath"), prefix: new HarmonyMethod(typeof(Suite), nameof(FixturePackPath)));
        try
        {
            Fresh();
            string first = Xml("CloudFirst", "舊包第一份");
            string second = Xml("CloudSecond", "舊包第二份");
            string def = "<LanguageData><rep><path>2Legacy.label</path><trans>數字鍵譯文</trans></rep><Legacy.words><li>完整清單</li></Legacy.words></LanguageData>";
            Commit(Archive(("Keyed/fixture_cloud_Misc.xml", first), ("Keyed/fixture_cloud_Settings.xml", second), ("DefInjected/ThingDef/legacy.xml", def)));
            var values = AutoTranslatorScanner.LoadXmlFileToDict(Canonical());
            Check(values["CloudFirst"] == "舊包第一份" && values["CloudSecond"] == "舊包第二份", "legacy archives without manifests consolidate per-source Keyed files under the requested package");
            Check(File.ReadAllText(Canonical("DefInjected/ThingDef")).Contains("<rep>") && File.ReadAllText(Canonical("DefInjected/ThingDef")).Contains("<li>完整清單</li>"), "legacy migration preserves numeric replacement keys and full-list XML structures");
            Check(File.Exists(Path.Combine(selectionPack, "Upload_Workspace", package, language, "Keyed", "fixture_cloud_AutoTranslated.xml")), "legacy download updates the isolated upload workspace in the same transaction");
            Check(File.ReadAllText(Path.Combine(Root(), "Keyed", "player-notes.xml")).Contains("玩家手動檔案") &&
                File.ReadAllText(Path.Combine(Root(), "Keyed", "fixture_cloud_extra_AutoTranslated.xml")).Contains("別的模組"), "legacy migration preserves manual files and packages sharing a filename prefix");
            Check(Directory.GetFiles(Path.Combine(selectionPack, "Backups"), "*", SearchOption.AllDirectories).Any(p => File.ReadAllText(p).Contains("保留既有譯文")), "replacement of existing canonical translations has a durable backup");

            Fresh();
            Commit(Archive(("Keyed/a.xml", first), ("Keyed/b.xml", first)));
            Check(AutoTranslatorScanner.LoadXmlFileToDict(Canonical()).Count == 1, "identical legacy duplicate keys consolidate without losing text");
            Fresh();
            Commit(Archive(("Keyed/UI/a.xml", first), ("DefInjected/ThingDef/Items/legacy.xml", def)));
            Check(AutoTranslatorScanner.LoadXmlFileToDict(Canonical())["CloudFirst"] == "舊包第一份" && File.Exists(Canonical("DefInjected/ThingDef")),
                "nested legacy translation folders consolidate within their Keyed or DefType scope");
            Fresh();
            var canonical = (path: "Keyed/fixture_cloud_AutoTranslated.xml", text: first);
            Commit(Archive(canonical, ("ATC_Manifest.json", Manifest(package, language, canonical))));
            Check(File.ReadAllText(Canonical()) == first, "modern manifested archives retain their verified original bytes");

            Reject("conflicting legacy keys are rejected before replacement", new[] { ("Keyed/a.xml", first), ("Keyed/b.xml", Xml("CloudFirst", "不同的付費譯文")) });
            Reject("duplicate keys in one translation document are rejected", new[] { ("Keyed/a.xml", "<LanguageData><A>one</A><A>two</A></LanguageData>") });
            Reject("legacy path traversal is rejected", new[] { ("../outside.xml", first) });
            Reject("legacy normalized traversal is rejected", new[] { ("Keyed/../Keyed/a.xml", first) });
            Reject("absolute archive paths are rejected", new[] { ("/Keyed/a.xml", first) });
            Reject("foreign executable content is rejected", new[] { ("Assemblies/payload.dll", "data") });
            Reject("foreign Def source files cannot enter the translation pack", new[] { ("Defs/ThingDef/a.xml", first) });
            Reject("DTD and external entities are rejected", new[] { ("Keyed/a.xml", "<!DOCTYPE LanguageData [<!ENTITY x SYSTEM 'file:///not-read'>]><LanguageData><A>&x;</A></LanguageData>") });
            Reject("non-translation XML roots are rejected", new[] { ("Keyed/a.xml", "<Defs><ThingDef/></Defs>") });
            Reject("case-insensitive duplicate paths are rejected", new[] { ("Keyed/A.xml", first), ("Keyed/a.xml", first) });
            Reject("modern checksum failures cannot fall back to legacy mode", new[] { canonical, ("ATC_Manifest.json", Manifest(package, language, (canonical.path, second))) });
            Reject("modern package mismatches cannot fall back to legacy mode", new[] { canonical, ("ATC_Manifest.json", Manifest("other.mod", language, canonical)) });
            Reject("modern language mismatches cannot fall back to legacy mode", new[] { canonical, ("ATC_Manifest.json", Manifest(package, "English", canonical)) });
            Reject("malformed manifests cannot fall back to legacy mode", new[] { canonical, ("ATC_Manifest.json", "{broken") });
            Reject("duplicate manifests cannot fall back to legacy mode", new[] { canonical, ("ATC_Manifest.json", Manifest(package, language, canonical)), ("ATC_Manifest.json", Manifest(package, language, canonical)) });
            Reject("modern noncanonical filenames remain rejected", new[] { ("Keyed/legacy.xml", first), ("ATC_Manifest.json", Manifest(package, language, ("Keyed/legacy.xml", first))) });
            Reject("cancellation cannot delete existing translations", new[] { ("Keyed/a.xml", first) }, () => false);
            Reject("empty archives cannot replace existing translations", new (string, string)[0]);
            Reject("record package identity must match the requested download", new[] { ("Keyed/a.xml", first) }, record: new CloudModRecord { PackageId = "another.mod", Language = language });

            Fresh();
            Directory.CreateDirectory(Path.Combine(Root(), "ATC_Ownership"));
            File.WriteAllText(Path.Combine(Root(), "ATC_Ownership", "fixture_cloud.json"), "\"fixture_cloud\"");
            string collisionBefore = Snapshot(); bool collision = false;
            try { Commit(Archive(("Keyed/a.xml", first))); } catch (InvalidDataException) { collision = true; }
            Check(collision && Snapshot() == collisionBefore, "legacy conversion keeps the normalized package ownership collision guard");

            foreach (string sample in new[] { "andromeda.stackgap", "andromeda.packablecontainers", "leafzxg.masterofcooking" })
            {
                string path = Path.GetFullPath(".codex/cloud-legacy-manifest-fix-20261026/samples/" + sample + ".zip");
                if (!File.Exists(path)) continue;
                Fresh(); Commit(path, owner: sample);
                Check(Directory.GetFiles(Root(), sample.Replace('.', '_') + "_AutoTranslated.xml", SearchOption.AllDirectories).Length > 0,
                    "real reported cloud package imports without a manifest: " + sample);
            }
        }
        finally { hooks.UnpatchAll(hooks.Id); selectionPack = previousPack; }
    }
}
