using HarmonyLib;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public static class UIInterceptorPatchGuard
    {
        [ThreadStatic] private static int _nestedLabelDepth;
        public static bool IsNestedLabelCall => _nestedLabelDepth > 0;
        public static void EnterNestedLabelCall() { _nestedLabelDepth++; }
        public static void ExitNestedLabelCall() { if (_nestedLabelDepth > 0) _nestedLabelDepth--; }
    }

    [HarmonyPatch(typeof(GUI), "Label", new Type[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) })]
    public static class Patch_GUI_Label_GUIContent
    {
        public static bool BypassInterceptor;
        internal static bool CanObserve => AutoTranslatorMod.Settings?.EnableUIInterceptor == true &&
            !BypassInterceptor && !UIInterceptorPatchGuard.IsNestedLabelCall;
        public static void Prefix(Rect position, GUIContent content)
        {
            if (CanObserve && content != null) UIInterceptor.ObserveDisplayedText(content.text);
        }
    }
    public static class Patch_LudeonTK_LogWindow_Bypass
    {
        public static void Prefix(out bool __state)
        {
            __state = Patch_GUI_Label_GUIContent.BypassInterceptor;
            if (AutoTranslatorMod.Settings?.EnableUIErrorLogInterception != true)
                Patch_GUI_Label_GUIContent.BypassInterceptor = true;
        }
        public static void Postfix(bool __state) { Patch_GUI_Label_GUIContent.BypassInterceptor = __state; }
    }
    [HarmonyPatch]
    public static class Patch_Widgets_Label_TaggedString
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Widgets), "Label", new[] { typeof(Rect), typeof(TaggedString) });
        }
        public static void Prefix(TaggedString __1, out bool __state)
        {
            __state = Patch_GUI_Label_GUIContent.CanObserve;
            if (!__state) return;
            UIInterceptor.ObserveDisplayedText(__1.ToString());
            UIInterceptorPatchGuard.EnterNestedLabelCall();
        }
        public static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) UIInterceptorPatchGuard.ExitNestedLabelCall();
            return __exception;
        }
    }
    [HarmonyPatch(typeof(Widgets), nameof(Widgets.Label), new Type[] { typeof(Rect), typeof(string) })]
    public static class Patch_Widgets_Label_String
    {
        public static void Prefix(string __1, out bool __state)
        {
            __state = Patch_GUI_Label_GUIContent.CanObserve;
            if (!__state) return;
            UIInterceptor.ObserveDisplayedText(__1);
            UIInterceptorPatchGuard.EnterNestedLabelCall();
        }
        public static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) UIInterceptorPatchGuard.ExitNestedLabelCall();
            return __exception;
        }
    }
    [HarmonyPatch(typeof(Widgets), nameof(Widgets.LabelFit), new Type[] { typeof(Rect), typeof(string) })]
    public static class Patch_Widgets_LabelFit
    {
        public static void Prefix(string __1, out bool __state) { Patch_Widgets_Label_String.Prefix(__1, out __state); }
        public static Exception Finalizer(Exception __exception, bool __state)
        {
            return Patch_Widgets_Label_String.Finalizer(__exception, __state);
        }
    }
    [HarmonyPatch(typeof(GUI), "Button", new Type[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) })]
    public static class Patch_GUI_Button_GUIContent
    {
        public static void Prefix(GUIContent __1)
        {
            if (Patch_GUI_Label_GUIContent.CanObserve && __1 != null) UIInterceptor.ObserveDisplayedText(__1.text);
        }
    }
    [HarmonyPatch(typeof(GUI), "Box", new Type[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) })]
    public static class Patch_GUI_Box_GUIContent
    {
        public static void Prefix(GUIContent __1) { Patch_GUI_Button_GUIContent.Prefix(__1); }
    }
    [HarmonyPatch(typeof(GUI), "Button", new Type[] { typeof(Rect), typeof(string), typeof(GUIStyle) })]
    public static class Patch_GUI_Button_String
    {
        public static void Prefix(string __1)
        {
            if (Patch_GUI_Label_GUIContent.CanObserve) UIInterceptor.ObserveDisplayedText(__1);
        }
    }
    [HarmonyPatch(typeof(GUI), "Box", new Type[] { typeof(Rect), typeof(string), typeof(GUIStyle) })]
    public static class Patch_GUI_Box_String
    {
        public static void Prefix(string __1) { Patch_GUI_Button_String.Prefix(__1); }
    }
    internal static class TooltipObservation
    {
        private static readonly ConditionalWeakTable<Func<string>, Getter> Getters = new ConditionalWeakTable<Func<string>, Getter>();
        internal static Func<string> Wrap(Func<string> original)
        {
            if (original == null || original.Target is Getter) return original;
            return Getters.GetValue(original, getter => new Getter(getter)).Invoke;
        }
        private sealed class Getter
        {
            private readonly Func<string> _original;
            internal Getter(Func<string> original) { _original = original; }
            internal string Invoke()
            {
                string text = _original();
                // The delegate's frame has returned. Preserve its actual method identity
                // instead of attributing the text to this wrapper or the tooltip renderer.
                if (Patch_GUI_Label_GUIContent.CanObserve)
                    UIInterceptor.ObserveDisplayedText(text, _original.Method);
                return text;
            }
        }
    }
    [HarmonyPatch(typeof(TooltipHandler), nameof(TooltipHandler.TipRegion), new Type[] { typeof(Rect), typeof(TipSignal) })]
    public static class Patch_TooltipHandler_TipRegion_TipSignal
    {
        public static void Prefix(Rect __0, ref TipSignal __1)
        {
            if (!Patch_GUI_Label_GUIContent.CanObserve || !Mouse.IsOver(__0)) return;
            UIInterceptor.ObserveDisplayedText(__1.text);
            __1.textGetter = TooltipObservation.Wrap(__1.textGetter);
        }
    }
    [HarmonyPatch(typeof(TooltipHandler), nameof(TooltipHandler.TipRegion), new Type[] { typeof(Rect), typeof(Func<string>), typeof(int) })]
    public static class Patch_TooltipHandler_TipRegion_Func
    {
        public static void Prefix(Rect __0, ref Func<string> __1)
        {
            if (Patch_GUI_Label_GUIContent.CanObserve && Mouse.IsOver(__0)) __1 = TooltipObservation.Wrap(__1);
        }
    }
}
