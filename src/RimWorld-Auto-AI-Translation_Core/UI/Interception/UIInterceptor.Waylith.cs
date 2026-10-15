#if RIMWORLD_1_7
using System;
using HarmonyLib;
using UnityEngine;

namespace AutoTranslator_Core
{
    // Waylith draws string labels directly instead of routing through GUIContent.
    [HarmonyPatch(typeof(GUI), nameof(GUI.Label), new[] { typeof(Rect), typeof(string), typeof(GUIStyle) })]
    public static class Patch_GUI_Label_String
    {
        public static void Prefix(Rect __0, ref string __1, out bool __state)
        {
            Patch_Widgets_Label_String.Prefix(__0, ref __1, out __state);
        }

        public static Exception Finalizer(Exception __exception, bool __state)
        {
            return Patch_Widgets_Label_String.Finalizer(__exception, __state);
        }
    }
}
#endif
