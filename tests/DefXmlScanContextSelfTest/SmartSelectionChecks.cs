using System.Reflection;
using System.Reflection.Emit;
using AutoTranslator_Core;
using HarmonyLib;

internal static partial class Suite
{
    static string selectionPack;
    static bool FixturePackPath(ref string __result) { __result = selectionPack; return false; }

    static void TestSmartSelectionStatuses()
    {
        selectionPack = Path.Combine(temporary, "SelectionGeneratedPack");
        var patcher = new Harmony("atc.smart-selection-offline");
        var packPath = typeof(AutoTranslatorScanner).GetMethod("GetLocalPackPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        patcher.Patch(packPath, prefix: new HarmonyMethod(typeof(Suite).GetMethod(nameof(FixturePackPath), BindingFlags.Static | BindingFlags.NonPublic)));
        try
        {
            var status = typeof(ModUpdateDetector).GetMethod("GetTranslationStatusUncached", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(ModUpdateDetector.TranslationStatusCheckSnapshot) }, null);
            ModUpdateDetector.TranslationStatusCheckSnapshot Candidate(string id, string folder) => new()
            {
                PackageId = id, RootDir = Path.Combine(temporary, folder), TargetLang = TargetLanguage.Traditional
            };
            ModTranslationStatus GetStatus(ModUpdateDetector.TranslationStatusCheckSnapshot check) =>
                (ModTranslationStatus)status.Invoke(null, new object[] { check });
            Write("Smart/Untranslated/Defs/Source.xml", "<Defs><ThingDef><defName>Fixture</defName><label>Fixture label</label></ThingDef></Defs>");
            var missing = Candidate("fixture.missing", "Smart/Untranslated");
            Check(GetStatus(missing) == ModTranslationStatus.Untranslated, "smart selection recognizes untranslated source mod");
            Write("Smart/Native/Defs/Source.xml", "<Defs><ThingDef><defName>Fixture</defName><label>Fixture label</label></ThingDef></Defs>");
            Write("Smart/Native/Languages/ChineseTraditional/Keyed/Native.xml", "<LanguageData><Title>已有漢化</Title></LanguageData>");
            Check(GetStatus(Candidate("fixture.native", "Smart/Native")) == ModTranslationStatus.Translated, "smart selection excludes native target-language mod");
            Directory.CreateDirectory(Path.Combine(temporary, "Smart/NoSources"));
            Check(GetStatus(Candidate("fixture.empty", "Smart/NoSources")) == ModTranslationStatus.Filtered, "smart selection excludes mod without source text");
            missing.SavedFingerprints[missing.PackageId] = "an older fingerprint";
            Check(GetStatus(missing) == ModTranslationStatus.PossiblyOutdated, "smart selection distinguishes outdated from untranslated");
            var window = typeof(ModSelectWindow);
            var button = window.GetMethod("DrawSelectionButtons", BindingFlags.Instance | BindingFlags.NonPublic);
            var selector = window.GetMethod("SelectUntranslatedMods", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(selector != null && PatchProcessor.GetOriginalInstructions(button).Any(i =>
                (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && Equals(i.operand, selector)),
                "compiled mod-selection button calls untranslated selection in product " + Version);
            Check(typeof(AutoTranslatorScanner).GetMethod("IsTranslationPatchMod") != null,
                "shared translation-patch filter is present alongside selector");
        }
        finally { patcher.UnpatchAll("atc.smart-selection-offline"); }
    }
}
