using System.Collections;
using System.Reflection;
using System.Xml;
using AutoTranslator_Core;
using Newtonsoft.Json.Linq;

RepairRunChecks.Run();
JournalCheckpointChecks.Run();

// Reflection keeps the red test executable before the production feature exists.
var type = Assembly.GetExecutingAssembly().GetType("AutoTranslator_Core.DefTranslationPathValidator");
if (type == null) throw new Exception("FAIL: shared structural validator is missing; enum/Def-reference paths still reach injection");
var validate = type.GetMethod("Validate");
int passed = 0;
void Test(string path, string expected, object root = null) {
    object r = validate.Invoke(null, new object[] { root ?? new Fixture(), path, null });
    string reason = (string)r.GetType().GetField("Reason").GetValue(r);
    if (reason != expected) throw new Exception(path + ": expected " + expected + ", got " + reason);
    passed++;
}
Test("Def.label", "valid"); Test("Def.child.label", "valid");
Test("Def.words.0", "valid"); Test("Def.words.2", "index_out_of_bounds");
Test("Def.stages.1.label", "index_out_of_bounds");
Test("Def.thingCategory", "non_text_field"); Test("Def.reference", "non_text_field");
Test("Def.overrideDebugActionCategory", "untranslatable_field"); Test("Def.unsaved", "untranslatable_field");
Test("Def.missing", "missing_field"); Test("Def.optional.label", "null_reference");
Test("Def.pawns.0.value", "unsupported_container");
Test("Def.child.oldLabel", "valid"); Test("Def.readonlyLabel", "untranslatable_field");
Test("Def.words", "non_text_field"); Test("Def.words.-1", "index_out_of_bounds");
Test("Def.ints.0", "non_text_field");
Test("Def", "invalid_path");
// A dictionary getter must never run with an integer or even be probed.
if (DangerDictionary.GetterCalls != 0) throw new Exception("dictionary getter executed");
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); passed++; }
var temporary = Path.Combine(Path.GetTempPath(), "atc-self-repair-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try {
    var journal = new TranslationRepairJournal(Path.Combine(temporary, "journal"));
    TranslationRepairRecord Record(string value) => new() { DefType = "Fixture", OriginalKey = "Def.oldLabel", NewKey = "Def.label", Translation = value, SourceFile = "paid.xml", SourceKind = "import", Reason = "alias", Status = "衝突" };
    journal.Record(Record("付費譯文一")); journal.Record(Record("付費譯文二")); journal.Record(Record("付費譯文一")); journal.Flush();
    Check(journal.Snapshot().Count == 2, "conflicts retain every distinct paid candidate and repeated run is idempotent");
    string persisted = File.ReadAllText(Path.Combine(temporary, "journal", "records.json"));
    journal = new TranslationRepairJournal(Path.Combine(temporary, "journal")); journal.Record(Record("付費譯文一")); journal.Flush();
    Check(File.ReadAllText(Path.Combine(temporary, "journal", "records.json")) == persisted, "restarted journal is byte-idempotent");
    string original = Path.Combine(temporary, "paid.xml"); File.WriteAllText(original, "<LanguageData><key>一</key><key>二</key></LanguageData>");
    string backup = journal.Backup(original);
    Check(File.ReadAllBytes(backup).SequenceEqual(File.ReadAllBytes(original)), "original XML bytes include duplicate values in backup");
    try { TranslationXmlAtomicFileStore.Save(original, stream => { stream.WriteByte(0); throw new IOException("power loss fixture"); }); } catch (IOException) { }
    Check(File.ReadAllBytes(backup).SequenceEqual(File.ReadAllBytes(original)), "interrupted atomic write leaves original intact");
    TranslationRepairJournal.AtomicText(original, "changed"); File.Copy(backup, original, true);
    Check(File.ReadAllBytes(backup).SequenceEqual(File.ReadAllBytes(original)), "backup rollback restores exact original bytes");
    Check(KnownTranslationXmlRepair.TryRepair("/3394862242/Languages/English/DefInjected/ThingDef/Weapons_Moon.xml", "base64,AAAA", out _, out _) == false, "base64 decoding is left to upstream decoder");
    Check(!KnownTranslationXmlRepair.TryRepair("/unrelated.xml", "<?xml broken", out _, out _), "unknown XML corruption is not guessed");
    if (args.Length > 0) {
        var audit = JObject.Parse(File.ReadAllText(args[0])); int repaired = 0;
        foreach (var f in audit["errors"]) {
            string path = (string)f["path"]; string input = File.ReadAllText(path);
            bool ok = KnownTranslationXmlRepair.TryRepair(path, input, out string output, out string rule);
            if ((string)f["id"] == "cephalopod.deadmanswitch.moresynthetics" || (string)f["id"] == "moqi.memegoddess.replacestuff.zh") { Check(!ok && output == input, "ambiguous open/close tags retained for review"); continue; }
            Check(ok, "known exact XML repaired: " + path); repaired++;
            var xml = new XmlDocument { XmlResolver = null }; xml.LoadXml(output);
            Check(xml.DocumentElement.Name == "LanguageData", "repaired XML strictly parses: " + rule);
            Check(!KnownTranslationXmlRepair.TryRepair(path, input + " ", out _, out _), "changed source fingerprint is rejected: " + rule);
            Check(File.ReadAllText(path) == input, "Workshop original remains untouched: " + rule);
        }
        Check(repaired == 8, "eight exact known XML repairs; two ambiguous files preserved");
    }
} finally { Directory.Delete(temporary, true); }
Console.WriteLine("PASS " + passed + " self-repair checks; translator/network dependencies=0");

class Fixture {
    public string label = "paid";
    public Child child = new Child(); public Child optional;
    public List<string> words = new() { "one" }; public List<int> ints = new() { 1 };
    public List<Child> stages = new() { new Child() };
    public DayOfWeek thingCategory; public object reference = new();
    [Verse.NoTranslate] public string overrideDebugActionCategory;
    [Verse.Unsaved] public string unsaved;
    public readonly string readonlyLabel = "no";
    public DangerDictionary pawns = new();
}
class Child { [Verse.LoadAlias("oldLabel")] public string label = "child"; }
class DangerDictionary { public static int GetterCalls; public int Count => 3; public string this[DateTime index] { get { GetterCalls++; throw new Exception(); } } }
namespace Verse {
 [AttributeUsage(AttributeTargets.Field)] class NoTranslateAttribute : Attribute { }
 [AttributeUsage(AttributeTargets.Field)] class UnsavedAttribute : Attribute { }
 [AttributeUsage(AttributeTargets.Field)] class LoadAliasAttribute : Attribute { public string alias; public LoadAliasAttribute(string alias) { this.alias = alias; } }
}
