using System.Reflection;
using System.Runtime.CompilerServices;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static void ObserveCompatibilityWarning(string __0)
    {
        if (__0.StartsWith("[ATC] Cached translation compatibility:")) Console.WriteLine(__0);
    }
    static bool RedirectHeadlessCommandDraw(Harmony harmony, Type type, string method, string prefix, ref bool __result)
    {
        if (type != typeof(Command)) return true;
        // Headless runtimes have no Unity textures/shaders. Check the actual
        // target metadata, then install the same production prefix on a managed
        // draw fixture. No claim of executing the real renderer is made.
        if (AccessTools.DeclaredMethod(type, method) == null) throw new MissingMethodException(type.FullName, method);
        var compatibility = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.CachedModTranslationCompatibility");
        harmony.Patch(AccessTools.Method(typeof(AllowTool.Designator_DefBased), "Draw"), prefix: new HarmonyMethod(compatibility, prefix));
        __result = true;
        return false;
    }
    static void TestCachedModTranslations()
    {
        var compatibility = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.CachedModTranslationCompatibility");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        void Call(string name, params object[] args) => compatibility.GetMethod(name, flags).Invoke(null, args);
        var previousLanguage = LanguageDatabase.activeLanguage;
        var previousDefault = LanguageDatabase.defaultLanguage;
        int previousFixtureThread = fixtureGameThread;
        var harmony = new Harmony("atc.cached-mod-text-regression");
        var drawEntry = AccessTools.DeclaredMethod(typeof(Command), "GizmoOnGUIInt") ?? AccessTools.DeclaredMethod(typeof(Command), "GizmoOnGUI");
        void DrawCommand(Command command)
        {
            if (command is AllowTool.Designator_DefBased designator) designator.Draw();
            else Call("RefreshAllowTool", command);
        }
        LoadedLanguage Language(params (string key, string value)[] entries)
        {
            var language = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
            language.keyedReplacements = entries.ToDictionary(e => e.key,
                e => new LoadedLanguage.KeyedReplacement { key = e.key, value = e.value });
            return language;
        }
        void Put(string key, string value) => LanguageDatabase.activeLanguage.keyedReplacements[key] = new LoadedLanguage.KeyedReplacement { key = key, value = value };
        var dialog = new RimHUD.Interface.Dialog.Dialog_Config();
        void Draw() => dialog.DrawContent();
        try
        {
            // ContentFinder's existing off-thread guard avoids Unity texture
            // loading when Command initializes in this headless fixture.
            fixtureGameThread = -1;
            harmony.Patch(AccessTools.Method(typeof(Log), "Warning", new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(Suite), nameof(ObserveCompatibilityWarning)));
            harmony.Patch(compatibility.GetMethod("PatchPrefix", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(RedirectHeadlessCommandDraw)));
            LanguageDatabase.defaultLanguage = Language(
                ("RimHUD.Theme.RefreshRate", "Refresh rate"), ("RimHUD.Theme.RefreshRateDesc", "Refresh interval"),
                ("RimHUD.Theme.TextStyleRegular", "Regular Text"),
                ("RimHUD.Interface.Dialog_Config.Tab_Credits", "Credits"),
                ("RimHUD.One", "Shared"), ("RimHUD.Two", "Shared"),
                ("OtherMod.Label", "Foreign source"));
            LanguageDatabase.activeLanguage = Language();
            Call("Install", harmony);
            NiceHealthTab.CombatExtended_Compatibility.mm_translated = "old unit";
            Call("TranslationsChanged");
            Check(NiceHealthTab.CombatExtended_Compatibility.mm_translated == null, "Nice Health Tab derived armor-unit cache resets after Keyed changes");
            var setting = new RimHUD.Configuration.Settings.ValueSetting("Refresh rate", "Refresh interval") { Value = 500 };
            var style = new RimHUD.Configuration.Settings.TextStyle("Regular Text");
            RimHUD.Configuration.Theme.Setting = setting; RimHUD.Configuration.Theme.Style = style;
            RimHUD.Interface.Dialog.Dialog_Config.Tabs = new[] { new RimHUD.Interface.Dialog.Tabs.Tab("Credits") };
            Draw();
            Check(setting.Label == "Refresh rate", "RimHUD source is kept when translation is absent");
            Put("RimHUD.Theme.RefreshRate", "刷新率");
            Put("RimHUD.Theme.RefreshRateDesc", "重新整理間隔");
            Put("RimHUD.Theme.TextStyleRegular", "一般文字");
            Put("RimHUD.Interface.Dialog_Config.Tab_Credits", "製作名單");
            Call("TranslationsChanged");
            Draw();
            Check(setting.Label == "刷新率", "RimHUD cached settings label resolves newly injected Keyed text");
            Check(setting.Tooltip == "重新整理間隔", "RimHUD cached tooltip resolves current Keyed text");
            Check(style.Label == "一般文字", "RimHUD cached text-style header resolves current Keyed text");
            Check(RimHUD.Interface.Dialog.Dialog_Config.Tabs[0].Label == "製作名單", "RimHUD static tab captions refresh with theme settings");
            Check(setting.Value == 500, "RimHUD localization keeps configured values intact");
            Put("RimHUD.Theme.RefreshRate", "更新頻率"); Call("TranslationsChanged"); Draw();
            Check(setting.Label == "更新頻率", "RimHUD same-count replacement invalidates cached display lookup");
            Put("RimHUD.One", "第一種"); Put("RimHUD.Two", "第二種");
            Put("OtherMod.Label", "不應套用");
            RimHUD.Configuration.Theme.Shared = new RimHUD.Configuration.Settings.TextStyle("Shared");
            RimHUD.Configuration.Theme.Foreign = new RimHUD.Configuration.Settings.TextStyle("Foreign source");
            RimHUD.Configuration.Theme.Localized = new RimHUD.Configuration.Settings.TextStyle("一般文字");
            Call("TranslationsChanged"); Draw();
            Check(RimHUD.Configuration.Theme.Shared.Label == "Shared", "ambiguous source captions are never guessed");
            Check(RimHUD.Configuration.Theme.Foreign.Label == "Foreign source", "foreign package keys cannot translate RimHUD settings");
            Check(RimHUD.Configuration.Theme.Localized.Label == "一般文字", "already localized captions pass through");
            LanguageDatabase.activeLanguage = Language(("RimHUD.Theme.RefreshRate", "刷新频率"));
            Draw();
            Check(setting.Label == "刷新频率", "switching the active language drops the previous language index");
            LanguageDatabase.activeLanguage = LanguageDatabase.defaultLanguage;
            NiceHealthTab.CombatExtended_Compatibility.mm_translated = "other unit";
            Call("Invalidate");
            Check(NiceHealthTab.CombatExtended_Compatibility.mm_translated == null, "Nice Health Tab unit cache resets during language/environment invalidation");
            Draw();
            Check(setting.Label == "Refresh rate", "English fallback selection keeps source settings captions");
            Check(RimHUD.Configuration.Theme.Localized.Label == "Regular Text", "settings first seen already localized can return to English");
            Call("Invalidate");
            LanguageDatabase.activeLanguage = Language(("RimHUD.Theme.RefreshRate", "刷新率"));
            Draw();
            Check(setting.Label == "刷新率", "environment invalidation rebuilds captions from current language");

            var def = (ThingDef)Uninitialized(typeof(ThingDef));
            def.label = "Harvest fully grown"; def.description = "Harvest mature plants";
            var designator = (AllowTool.Designator_DefBased)Uninitialized(typeof(AllowTool.Designator_DefBased));
            designator.Def = def; designator.defaultLabel = def.label; designator.defaultDesc = def.description;
            Check(designator.Label == "Harvest fully grown", "Allow Tool uses its existing source before injection");
            def.label = "收穫成熟植物"; def.description = "僅收穫完全成熟的植物";
            DrawCommand(designator);
            Check(designator.Label == "收穫成熟植物", "Allow Tool reads translated Def label after a late injection");
            Check(designator.Desc == "僅收穫完全成熟的植物", "Allow Tool reads translated Def description after a late injection");
            Check(designator.defaultLabel == def.label, "Allow Tool updates cached captions even when display getters were inlined");
            var unrelated = (Command_Action)Uninitialized(typeof(Command_Action));
            unrelated.defaultLabel = "Harvest fully grown"; unrelated.defaultDesc = "Other description";
            DrawCommand(unrelated);
            Check(unrelated.Label == "Harvest fully grown" && unrelated.Desc == "Other description", "unrelated commands remain unchanged");
            designator.Def = null;
            DrawCommand(designator);
            Check(designator.Label == "收穫成熟植物", "Allow Tool without an assigned Def keeps its last caption");
            Check(drawEntry != null && Harmony.GetPatchInfo(AccessTools.Method(typeof(AllowTool.Designator_DefBased), "Draw"))?.Prefixes.Any(p => p.owner == harmony.Id) == true,
                "Allow Tool real draw target metadata and production prefix on managed draw fixture are verified; native graphics not started");

            var size = typeof(MainTabWindow_AutoTranslator).GetMethod("GetTabSize", flags);
            UnityEngine.Vector2 Size(float width, float height) => (UnityEngine.Vector2)size.Invoke(null, new object[] { width, height });
            var standard = Size(1280, 720);
            Check(standard.x == 1088f && standard.y == 684f, "ATC tab fits the standard logical screen above the main buttons");
            var scaled = Size(2560, 1386);
            Check(scaled.x == 2176f && scaled.y > 1200f && scaled.y < 1351f, "ATC tab grows on the player's low-UI-scale logical screen");
            var small = Size(800, 600);
            Check(small.x == 800f && small.y == 565f, "ATC tab clamps to a small screen without covering main buttons");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            LanguageDatabase.activeLanguage = previousLanguage;
            LanguageDatabase.defaultLanguage = previousDefault;
            fixtureGameThread = previousFixtureThread;
            Call("Invalidate");
        }
    }
}

// Minimal copies of the upstream display-getter contracts. Their names let the
// production optional-patch discovery run without loading or starting a game.
namespace AllowTool
{
    public class Designator_DefBased : Command_Action
    {
        public Def Def { get; set; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Draw() { }
    }
}

namespace RimHUD.Configuration.Settings
{
    public class ValueSetting
    {
        public int Value;
        public ValueSetting(string label, string tooltip) { Label = label; Tooltip = tooltip; }
        public string Label { get; }
        public string Tooltip { get; }
    }
    public class TextStyle
    {
        public TextStyle(string label) { Label = label; }
        public string Label { get; }
    }
}

namespace RimHUD.Configuration
{
    public static class Theme
    {
        public static Settings.ValueSetting Setting;
        public static Settings.TextStyle Style, Shared, Foreign, Localized;
    }
}

namespace RimHUD.Interface.Dialog
{
    public class Dialog_Config
    {
        public static Tabs.Tab[] Tabs;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void DrawContent() { }
    }
}

namespace RimHUD.Interface.Dialog.Tabs
{
    public class Tab
    {
        public Tab(string label) { Label = label; }
        public string Label { get; }
    }
}

namespace NiceHealthTab
{
    public static class CombatExtended_Compatibility
    {
        public static string mm_translated;
    }
}
