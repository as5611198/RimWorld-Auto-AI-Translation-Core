using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責 Harmony 攔截點與文字替換。
// EN: This file defines Harmony patches for UI text replacement.

namespace AutoTranslator_Core
{
    internal static class UIInterceptorPatchGuard
    {
        [ThreadStatic]
        private static int _nestedLabelDepth;

        internal static bool IsNestedLabelCall => _nestedLabelDepth > 0;

        internal static void EnterNestedLabelCall()
        {
            _nestedLabelDepth++;
        }

        internal static void ExitNestedLabelCall()
        {
            if (_nestedLabelDepth > 0) _nestedLabelDepth--;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.GUI), "Label", new Type[] { typeof(UnityEngine.Rect), typeof(UnityEngine.GUIContent), typeof(UnityEngine.GUIStyle) })]
    // 這個類別負責 補丁GUILabelGUIContent 的主要流程與狀態。
    // EN: This class manages the main workflow and state for Patch_GUI_Label_GUIContent.
    public static class Patch_GUI_Label_GUIContent
    {
        // 這個欄位保存 BypassInterceptor 的執行狀態或快取資料。
        // EN: This field stores bypass interceptor runtime state or cached data.
        public static bool BypassInterceptor = false;
        private const int MaxGuiContentCacheSize = 8192;
        private static readonly Dictionary<string, GUIContentCacheBucket> guiContentCache =
            new Dictionary<string, GUIContentCacheBucket>();
        private static readonly Dictionary<string, GUIContentCacheBucket> activeTooltipContentCache =
            new Dictionary<string, GUIContentCacheBucket>();
        private static readonly Dictionary<string, string> originalTextTooltipCache = new Dictionary<string, string>();
        private static int guiContentCacheEntryCount;
        private static int activeTooltipContentCacheEntryCount;
        private static readonly ConditionalWeakTable<Func<string>, TranslatedTooltipGetter> translatedTooltipGetterCache =
            new ConditionalWeakTable<Func<string>, TranslatedTooltipGetter>();
        private static readonly ConditionalWeakTable<Func<string>, TranslatedTooltipGetter>.CreateValueCallback
            translatedTooltipGetterFactory = CreateTranslatedTooltipGetter;

        private sealed class GUIContentCacheBucket
        {
            internal readonly List<GUIContent> Items = new List<GUIContent>(2);
        }


        // 這個方法負責清除 快取 資料。
        // EN: This method clears cache.
        public static void ClearCache()
        {
            guiContentCache.Clear();
            activeTooltipContentCache.Clear();
            originalTextTooltipCache.Clear();
            guiContentCacheEntryCount = 0;
            activeTooltipContentCacheEntryCount = 0;
        }


        // 這個方法負責處理 Prefix 相關流程。
        // EN: This method handles prefix.
        public static void Prefix(UnityEngine.Rect position, ref UnityEngine.GUIContent content)
        {
            if (!AutoTranslatorMod.Settings.EnableUIInterceptor || BypassInterceptor) return;
            if (UIInterceptorPatchGuard.IsNestedLabelCall) return;

            if (content != null && !string.IsNullOrEmpty(content.text))
            {
                string originalText = content.text;

                bool tooltipActive = !string.IsNullOrEmpty(content.tooltip) && IsTooltipActive(position);
                string tooltipText = tooltipActive
                    ? TranslateTooltipText(content.tooltip)
                    : content.tooltip;
                bool useActiveTooltipCache = tooltipActive
                    && !string.Equals(tooltipText, content.tooltip, StringComparison.Ordinal);

                if (UIInterceptor.TryResolveRenderText(originalText, out string translated))
                {
                    RegisterOriginalTextTooltip(position, originalText);
                    content = useActiveTooltipCache
                        ? GetCachedContent(
                            activeTooltipContentCache,
                            ref activeTooltipContentCacheEntryCount,
                            originalText,
                            translated,
                            content.image,
                            tooltipText)
                        : GetCachedContent(
                            guiContentCache,
                            ref guiContentCacheEntryCount,
                            originalText,
                            translated,
                            content.image,
                            tooltipText);
                }
                else if (!string.Equals(tooltipText, content.tooltip, StringComparison.Ordinal))
                {
                    content = GetCachedContent(
                        activeTooltipContentCache,
                        ref activeTooltipContentCacheEntryCount,
                        originalText,
                        content.text,
                        content.image,
                        tooltipText);
                }
            }
        }

        private static GUIContent GetCachedContent(
            Dictionary<string, GUIContentCacheBucket> cache,
            ref int entryCount,
            string cacheKey,
            string text,
            Texture image,
            string tooltip)
        {
            if (cache.TryGetValue(cacheKey, out GUIContentCacheBucket bucket))
            {
                for (int i = 0; i < bucket.Items.Count; i++)
                {
                    GUIContent cached = bucket.Items[i];
                    if (string.Equals(cached.text, text, StringComparison.Ordinal)
                        && ReferenceEquals(cached.image, image)
                        && string.Equals(cached.tooltip, tooltip, StringComparison.Ordinal))
                    {
                        return cached;
                    }
                }
            }

            if (entryCount >= MaxGuiContentCacheSize)
            {
                cache.Clear();
                entryCount = 0;
                bucket = null;
            }

            if (bucket == null)
            {
                bucket = new GUIContentCacheBucket();
                cache[cacheKey] = bucket;
            }

            GUIContent created = new GUIContent(text, image, tooltip);
            bucket.Items.Add(created);
            entryCount++;
            return created;
        }

        // 這個方法負責翻譯 TooltipText 內容。
        // EN: This method translates tooltip text.
        private static string TranslateTooltipText(string tooltip)
        {
            if (string.IsNullOrWhiteSpace(tooltip)) return tooltip;
            if (UIInterceptor.TryResolveRenderText(tooltip, out string translated))
            {
                return translated;
            }
            return tooltip;
        }

        // 這個方法負責翻譯 TooltipSignalText 內容。
        // EN: This method translates tooltip signal text.
        internal static string TranslateTooltipSignalText(string tooltip)
        {
            return TranslateTooltipText(tooltip);
        }

        internal static Func<string> GetTranslatedTooltipGetter(Func<string> originalGetter)
        {
            if (originalGetter == null) return null;
            if (originalGetter.Target is TranslatedTooltipGetter) return originalGetter;
            return translatedTooltipGetterCache.GetValue(
                originalGetter,
                translatedTooltipGetterFactory).Callback;
        }

        private static TranslatedTooltipGetter CreateTranslatedTooltipGetter(Func<string> originalGetter)
        {
            return new TranslatedTooltipGetter(originalGetter);
        }

        internal static bool IsTooltipActive(Rect rect)
        {
            Event current = Event.current;
            return current != null
                && current.type == EventType.Repaint
                && (Mouse.IsOver(rect) || DebugViewSettings.drawTooltipEdges);
        }

        internal static void RegisterOriginalTextTooltip(Rect rect, string originalText)
        {
            if (!AutoTranslatorMod.Settings.ShowOriginalUI || string.IsNullOrEmpty(originalText)) return;
            if (!IsTooltipActive(rect)) return;

            if (!originalTextTooltipCache.TryGetValue(originalText, out string tooltip))
            {
                if (originalTextTooltipCache.Count >= MaxGuiContentCacheSize)
                {
                    originalTextTooltipCache.Clear();
                }
                tooltip = "\u200B" + "ATC_OriginalText".Translate() + ":\n" + originalText;
                originalTextTooltipCache[originalText] = tooltip;
            }

            Verse.TooltipHandler.TipRegion(
                rect,
                new Verse.TipSignal(tooltip));
        }

        internal sealed class TranslatedTooltipGetter
        {
            private readonly Func<string> _inner;

            public TranslatedTooltipGetter(Func<string> inner)
            {
                _inner = inner;
                Callback = Invoke;
            }

            public Func<string> Callback { get; }

            public string Invoke()
            {
                return TranslateTooltipSignalText(_inner());
            }
        }
    }

    public static class Patch_LudeonTK_LogWindow_Bypass
    {
        public static void Prefix(out bool __state)
        {
            __state = Patch_GUI_Label_GUIContent.BypassInterceptor;
            if (!AutoTranslatorMod.Settings.EnableUIErrorLogInterception)
            {
                Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            }
        }

        public static void Postfix(bool __state)
        {
            Patch_GUI_Label_GUIContent.BypassInterceptor = __state;
        }
    }

    [HarmonyPatch]
    public static class Patch_LudeonTK_LogWindow_Bypass_Target
    {
        public static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("LudeonTK.EditWindow_Log");
            return type == null
                ? null
                : AccessTools.Method(type, "DoWindowContents", new[] { typeof(Rect) });
        }

        public static void Prefix(out bool __state)
        {
            Patch_LudeonTK_LogWindow_Bypass.Prefix(out __state);
        }

        public static void Postfix(bool __state)
        {
            Patch_LudeonTK_LogWindow_Bypass.Postfix(__state);
        }
    }

    [HarmonyPatch(typeof(Verse.TooltipHandler), nameof(Verse.TooltipHandler.TipRegion), new Type[] { typeof(Rect), typeof(TipSignal) })]
    // 這個類別負責 補丁TooltipHandlerTipRegionTipSignal 的主要流程與狀態。
    // EN: This class manages the main workflow and state for Patch_TooltipHandler_TipRegion_TipSignal.
    public static class Patch_TooltipHandler_TipRegion_TipSignal
    {
        // 這個方法負責處理 Prefix 相關流程。
        // EN: This method handles prefix.
        public static void Prefix(Rect __0, ref TipSignal __1)
        {
            if (!AutoTranslatorMod.Settings.EnableUIInterceptor || Patch_GUI_Label_GUIContent.BypassInterceptor) return;
            if (!Patch_GUI_Label_GUIContent.IsTooltipActive(__0)) return;

            if (!string.IsNullOrWhiteSpace(__1.text))
            {
                __1.text = Patch_GUI_Label_GUIContent.TranslateTooltipSignalText(__1.text);
            }

            if (__1.textGetter != null)
            {
                __1.textGetter = Patch_GUI_Label_GUIContent.GetTranslatedTooltipGetter(__1.textGetter);
            }
        }
    }

    [HarmonyPatch(typeof(Verse.TooltipHandler), nameof(Verse.TooltipHandler.TipRegion), new Type[] { typeof(Rect), typeof(Func<string>), typeof(int) })]
    // 這個類別負責 補丁TooltipHandlerTipRegionFunc 的主要流程與狀態。
    // EN: This class manages the main workflow and state for Patch_TooltipHandler_TipRegion_Func.
    public static class Patch_TooltipHandler_TipRegion_Func
    {
        // 這個方法負責處理 Prefix 相關流程。
        // EN: This method handles prefix.
        public static void Prefix(Rect __0, ref Func<string> __1)
        {
            if (!AutoTranslatorMod.Settings.EnableUIInterceptor || Patch_GUI_Label_GUIContent.BypassInterceptor) return;
            if (!Patch_GUI_Label_GUIContent.IsTooltipActive(__0)) return;
            if (__1 == null) return;
            __1 = Patch_GUI_Label_GUIContent.GetTranslatedTooltipGetter(__1);
        }
    }


    [HarmonyPatch(typeof(Verse.Widgets), nameof(Verse.Widgets.Label), new Type[] { typeof(Rect), typeof(string) })]
    // 這個類別負責 補丁WidgetsLabelString 的主要流程與狀態。
    // EN: This class manages the main workflow and state for Patch_Widgets_Label_String.
    public static class Patch_Widgets_Label_String
    {
        // 這個方法負責處理 Prefix 相關流程。
        // EN: This method handles prefix.
        public static void Prefix(Rect rect, ref string label, out bool __state)
        {
            __state = false;
            if (!AutoTranslatorMod.Settings.EnableUIInterceptor || Patch_GUI_Label_GUIContent.BypassInterceptor) return;
            if (UIInterceptorPatchGuard.IsNestedLabelCall) return;

            if (!string.IsNullOrEmpty(label)
                && UIInterceptor.TryResolveRenderText(label, out string translated))
            {
                Patch_GUI_Label_GUIContent.RegisterOriginalTextTooltip(rect, label);
                label = translated;
            }

            UIInterceptorPatchGuard.EnterNestedLabelCall();
            __state = true;
        }

        public static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) UIInterceptorPatchGuard.ExitNestedLabelCall();
            return __exception;
        }
    }
    [HarmonyPatch(typeof(Verse.Widgets), nameof(Verse.Widgets.LabelFit), new Type[] { typeof(Rect), typeof(string) })]
    // 這個類別負責 補丁WidgetsLabelFit 的主要流程與狀態。
    // EN: This class manages the main workflow and state for Patch_Widgets_LabelFit.
    public static class Patch_Widgets_LabelFit
    {
        // 這個方法負責處理 Prefix 相關流程。
        // EN: This method handles prefix.
        public static void Prefix(Rect rect, ref string label, out bool __state)
        {
            __state = false;
            if (!AutoTranslatorMod.Settings.EnableUIInterceptor || Patch_GUI_Label_GUIContent.BypassInterceptor) return;
            if (UIInterceptorPatchGuard.IsNestedLabelCall) return;
            if (string.IsNullOrEmpty(label)) return;

            if (UIInterceptor.TryResolveRenderText(label, out string translated))
            {
                Patch_GUI_Label_GUIContent.RegisterOriginalTextTooltip(rect, label);
                label = translated;
            }

            UIInterceptorPatchGuard.EnterNestedLabelCall();
            __state = true;
        }

        public static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) UIInterceptorPatchGuard.ExitNestedLabelCall();
            return __exception;
        }
    }
}
