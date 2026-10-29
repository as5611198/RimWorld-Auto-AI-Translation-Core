using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;

internal static partial class Suite
{
    static void TestNestedUiTextGuard()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var guard = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.UIInterceptorPatchGuard");
        var enter = guard.GetMethod("EnterNestedLabelCall", flags);
        var exit = guard.GetMethod("ExitNestedLabelCall", flags);
        bool Nested() => (bool)guard.GetProperty("IsNestedLabelCall", flags).GetValue(null);
        bool Skip(string text) => (bool)guard.GetMethod("ShouldBypassNestedLabel", flags).Invoke(null, new object[] { text });
        int entered = 0;
        void Push(string text) { enter.Invoke(null, new object[] { text }); entered++; }
        void Pop() { exit.Invoke(null, null); entered--; }
        try
        {
            Check(!Nested(), "nested UI text guard starts outside a label scope");
            Push("護盾核心");
            Check(Nested(), "translated outer label starts a scoped UI guard");
            Check(Skip("護盾核心"), "same translated nested delegation is suppressed");
            Check(!Skip("Shield Core"), "foreign redraw of the English source remains translatable inside a translated scope");
            Check(!Skip("另一段文字"), "different child labels are not suppressed by an unrelated outer label");
            Push("子標籤");
            Check(Skip("子標籤") && !Skip("護盾核心"), "nested text scope protects only its own currently emitted caption");
            Pop();
            Check(Skip("護盾核心") && !Skip("子標籤"), "leaving a child scope restores the outer caption identity");
            Push("例外中的標籤");
            try { throw new InvalidOperationException("fixture rendering exception"); }
            catch (InvalidOperationException) { }
            finally { Pop(); }
            Check(Skip("護盾核心"), "exception cleanup restores the prior nested text scope");
            Pop();
            Check(!Nested() && !Skip("護盾核心"), "leaving all labels releases both depth and caption suppression");
            exit.Invoke(null, null);
            Check(!Nested(), "extra scope cleanup cannot underflow into a permanent bypass");
            Push(null);
            Check(Skip(null) && !Skip("Shield Core"), "empty GUIContent scope cannot suppress a later nonempty label");
            Pop();
            Check(!Nested(), "empty label scope is fully balanced");
            foreach (string name in new[] { "Patch_GUI_Label_GUIContent", "Patch_Widgets_Label_String", "Patch_Widgets_LabelFit" })
            {
                var type = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core." + name);
                var il = PatchProcessor.GetOriginalInstructions(type.GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public));
                Check(il.Any(i => i.operand is MethodInfo m && m.DeclaringType == guard && m.Name == "ShouldBypassNestedLabel"),
                    "production rendering prefix uses text-aware nested guard: " + name);
            }
        }
        finally { while (entered > 0) Pop(); }
    }
}
