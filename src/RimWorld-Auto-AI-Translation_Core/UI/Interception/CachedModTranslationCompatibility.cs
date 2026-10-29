using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace AutoTranslator_Core
{
    // These mods copy localized strings into long-lived objects before ATC's
    // fallback/hot injection. Refresh their display caches from current data;
    // never recreate settings objects or reset their saved values.
    internal static class CachedModTranslationCompatibility
    {
        private static Type allowToolDesignator;
        private static PropertyInfo allowToolDef;
        private static Type rimHudTheme, rimHudSetting, rimHudStyle, rimHudDialog, rimHudTab;
        private static FieldInfo niceHealthArmorUnit;
        private static readonly ConditionalWeakTable<object, Dictionary<FieldInfo, SavedCaption>> savedCaptions = new ConditionalWeakTable<object, Dictionary<FieldInfo, SavedCaption>>();
        private static bool dirty = true;
        private static LoadedLanguage indexedLanguage, indexedDefaultLanguage;
        private sealed class CaptionKey { internal string Key, English; }
        private sealed class SavedCaption { internal CaptionKey Source; internal string LastApplied; }

        internal static void Install(Harmony harmony)
        {
            Type armor = AccessTools.TypeByName("NiceHealthTab.CombatExtended_Compatibility");
            // This cache has no initializer or gameplay state. Its original
            // getter rebuilds the derived unit from the current language.
            niceHealthArmorUnit = armor?.TypeInitializer == null ? armor?.GetField("mm_translated", BindingFlags.Public | BindingFlags.Static) : null;
            if (niceHealthArmorUnit != null && (niceHealthArmorUnit.FieldType != typeof(string) || niceHealthArmorUnit.IsInitOnly || niceHealthArmorUnit.IsLiteral)) niceHealthArmorUnit = null;
            allowToolDesignator = AccessTools.TypeByName("AllowTool.Designator_DefBased");
            allowToolDef = allowToolDesignator?.GetProperty("Def", BindingFlags.Instance | BindingFlags.Public);
            if (allowToolDef != null && typeof(Command).IsAssignableFrom(allowToolDesignator) && typeof(Def).IsAssignableFrom(allowToolDef.PropertyType))
            {
                // Patch the substantial render entry instead of trivial getters
                // which Mono/CoreCLR may inline before Harmony can intercept.
                string draw = AccessTools.DeclaredMethod(typeof(Command), "GizmoOnGUIInt") != null ? "GizmoOnGUIInt" : "GizmoOnGUI";
                PatchPrefix(harmony, typeof(Command), draw, nameof(RefreshAllowTool));
            }
            else { allowToolDesignator = null; allowToolDef = null; }

            rimHudSetting = AccessTools.TypeByName("RimHUD.Configuration.Settings.ValueSetting");
            rimHudStyle = AccessTools.TypeByName("RimHUD.Configuration.Settings.TextStyle");
            rimHudTheme = AccessTools.TypeByName("RimHUD.Configuration.Theme");
            rimHudDialog = AccessTools.TypeByName("RimHUD.Interface.Dialog.Dialog_Config");
            rimHudTab = AccessTools.TypeByName("RimHUD.Interface.Dialog.Tabs.Tab");
            if (rimHudTheme != null && rimHudSetting != null && rimHudStyle != null)
                PatchPrefix(harmony, rimHudDialog, "DrawContent", nameof(RefreshRimHud));
            Invalidate();
        }

        private static bool PatchPrefix(Harmony harmony, Type type, string method, string prefix)
        {
            if (type == null) return false;
            MethodInfo target = AccessTools.DeclaredMethod(type, method);
            if (target == null) return false;
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(CachedModTranslationCompatibility), prefix));
                return true;
            }
            catch (Exception error)
            {
                Log.Warning("[ATC] Cached translation compatibility: " + type.FullName + "." + method + ": " + error.Message);
                return false;
            }
        }

        internal static void Invalidate()
        {
            dirty = true;
            indexedLanguage = indexedDefaultLanguage = null;
            ResetDerivedTranslationCaches();
        }

        internal static void TranslationsChanged() { dirty = true; ResetDerivedTranslationCaches(); }

        private static void ResetDerivedTranslationCaches()
        {
            niceHealthArmorUnit?.SetValue(null, null);
        }

        private static void RefreshAllowTool(Command __instance)
        {
            Def def = GetAllowToolDef(__instance);
            if (def == null) return;
            __instance.defaultLabel = def.label;
            __instance.defaultDesc = def.description;
        }

        private static Def GetAllowToolDef(Command command)
        {
            if (allowToolDesignator == null || !allowToolDesignator.IsInstanceOfType(command)) return null;
            return allowToolDef.GetValue(command, null) as Def;
        }

        private static void RefreshRimHud()
        {
            if (rimHudTheme == null || rimHudSetting == null || rimHudStyle == null) return;
            LoadedLanguage language = LanguageDatabase.activeLanguage, fallback = LanguageDatabase.defaultLanguage;
            if (!dirty && ReferenceEquals(language, indexedLanguage) && ReferenceEquals(fallback, indexedDefaultLanguage)) return;
            if (language?.keyedReplacements == null || fallback?.keyedReplacements == null) return;
            var lookup = BuildRimHudLookup(language, fallback);
            var visited = new HashSet<object>();
            foreach (var field in rimHudTheme.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (IsRimHudSettingsType(field.FieldType)) RefreshRimHudObject(field.GetValue(null), language, lookup, visited);
            }
            if (rimHudDialog != null && rimHudTab != null)
                foreach (var field in rimHudDialog.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    if (field.FieldType.IsArray && rimHudTab.IsAssignableFrom(field.FieldType.GetElementType()) && field.GetValue(null) is Array tabs)
                        foreach (object tab in tabs) RefreshRimHudObject(tab, language, lookup, visited);
            indexedLanguage = language; indexedDefaultLanguage = fallback; dirty = false;
        }

        private static bool IsRimHudSettingsType(Type type) => rimHudSetting.IsAssignableFrom(type) || rimHudStyle.IsAssignableFrom(type);

        private static void RefreshRimHudObject(object setting, LoadedLanguage language, Dictionary<string, CaptionKey> lookup, HashSet<object> visited)
        {
            if (setting == null || !visited.Add(setting)) return;
            var captions = savedCaptions.GetOrCreateValue(setting);
            for (Type type = setting.GetType(); type != null &&
                (type.Namespace == "RimHUD.Configuration.Settings" || type.Namespace == "RimHUD.Interface.Dialog.Tabs"); type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (IsRimHudSettingsType(field.FieldType))
                    RefreshRimHudObject(field.GetValue(setting), language, lookup, visited);
                else if (field.FieldType == typeof(string) &&
                    (field.Name == "<Label>k__BackingField" || field.Name == "<Tooltip>k__BackingField"))
                {
                    string current = field.GetValue(setting) as string;
                    if (string.IsNullOrEmpty(current)) continue;
                    if (!captions.TryGetValue(field, out var saved) || !string.Equals(current, saved.LastApplied, StringComparison.Ordinal))
                    {
                        if (!lookup.TryGetValue(current, out var source)) continue;
                        captions[field] = saved = new SavedCaption { Source = source, LastApplied = current };
                    }
                    string translated = language.keyedReplacements.TryGetValue(saved.Source.Key, out var target) && !string.IsNullOrEmpty(target.value)
                        ? target.value : saved.Source.English;
                    if (!string.Equals(current, translated, StringComparison.Ordinal)) field.SetValue(setting, translated);
                    saved.LastApplied = translated;
                }
            }
        }

        private static Dictionary<string, CaptionKey> BuildRimHudLookup(LoadedLanguage language, LoadedLanguage fallback)
        {
            var result = new Dictionary<string, CaptionKey>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            Action<string, CaptionKey> add = (text, source) => {
                if (string.IsNullOrEmpty(text) || ambiguous.Contains(text)) return;
                // A stored label cannot identify which of two different keys it
                // came from, even if those keys currently share a translation.
                if (result.TryGetValue(text, out var previous) && previous.Key != source.Key)
                { result.Remove(text); ambiguous.Add(text); }
                else result[text] = source;
            };
            foreach (var source in fallback.keyedReplacements)
            {
                if (!source.Key.StartsWith("RimHUD.", StringComparison.Ordinal) || string.IsNullOrEmpty(source.Value.value)) continue;
                var key = new CaptionKey { Key = source.Key, English = source.Value.value };
                add(key.English, key);
                if (language.keyedReplacements.TryGetValue(source.Key, out var target)) add(target.value, key);
            }
            return result;
        }
    }
}
