using HarmonyLib;
using RimWorld;
using System;
using System.Reflection;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal static class SettingsWindowSizingPatch
    {
        private static readonly object Sync = new object();
        private static readonly FieldInfo ModField =
            AccessTools.Field(typeof(Dialog_ModSettings), "mod");
        private static bool _installed;
        private static bool _loggedAppliedSize;

        internal static void EnsureInstalled()
        {
            if (_installed) return;
            lock (Sync)
            {
                if (_installed) return;

                MethodInfo getter = AccessTools.PropertyGetter(
                    typeof(Dialog_ModSettings),
                    nameof(Dialog_ModSettings.InitialSize));
                MethodInfo postfix = AccessTools.Method(
                    typeof(SettingsWindowSizingPatch),
                    nameof(AfterGetInitialSize));
                if (getter == null || postfix == null || ModField == null)
                {
                    Log.Warning("[AutoTranslationCore] Dynamic settings window sizing was not installed: target members were not found.");
                    return;
                }

                new Harmony("MingYang.AutoTranslation.SettingsWindowSizing")
                    .Patch(getter, postfix: new HarmonyMethod(postfix));
                _installed = true;
            }
        }

        private static void AfterGetInitialSize(Dialog_ModSettings __instance, ref Vector2 __result)
        {
            try
            {
                Mod mod = ModField.GetValue(__instance) as Mod;
                if (!(mod is AutoTranslatorMod)) return;

                SettingsWindowSize size = SettingsWindowSizePolicy.Resolve(
                    UI.screenWidth,
                    UI.screenHeight);
                __result = new Vector2(size.Width, size.Height);
                if (!_loggedAppliedSize)
                {
                    _loggedAppliedSize = true;
                    Log.Message(
                        "[AutoTranslationCore] Dynamic settings window size: " +
                        size.Width.ToString("0") + "x" + size.Height.ToString("0") +
                        " for logical screen " + UI.screenWidth + "x" + UI.screenHeight + ".");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] Dynamic settings window sizing failed: " + ex.Message);
            }
        }
    }
}
