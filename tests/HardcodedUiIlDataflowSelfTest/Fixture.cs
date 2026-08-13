namespace Verse
{
    public struct Rect { }
    public static class Widgets
    {
        public static void Label(Rect rect, string text) { }
    }
    public static class Log
    {
        public static void Message(string text) { }
    }
    public static class Messages
    {
        public static void Message(string text) { }
    }
}

namespace HarmonyLib
{
    public static class AccessTools
    {
        public static object Field(System.Type type, string name) { return null; }
    }
}

namespace Atc.IlDataflowFixture
{
    public static class Fixture
    {
        public static void Direct()
        {
            Verse.Widgets.Label(default(Verse.Rect), "Direct label");
        }

        public static void Local()
        {
            string text = "Local label";
            Verse.Widgets.Label(default(Verse.Rect), text);
        }

        public static void Formatted(string name)
        {
            Verse.Widgets.Label(default(Verse.Rect), string.Format("Hello {0}", name));
        }

        public static void LogOnly()
        {
            Verse.Log.Message("Developer diagnostic");
        }

        public static void ReflectionKey()
        {
            HarmonyLib.AccessTools.Field(typeof(Fixture), "failed");
        }

        public static void PlayerMessage()
        {
            Verse.Messages.Message("The ritual failed.");
        }

        public static void Builder()
        {
            string text = new System.Text.StringBuilder().Append("Builder label").ToString();
            Verse.Widgets.Label(default(Verse.Rect), text);
        }

        private static void DrawWrapped(string text)
        {
            Verse.Widgets.Label(default(Verse.Rect), text);
        }

        public static void WrapperCall()
        {
            DrawWrapped("Wrapped label");
        }

        public static void Branch(bool first)
        {
            string text;
            if (first) text = "Branch A";
            else text = "Branch B";
            Verse.Widgets.Label(default(Verse.Rect), text);
        }

        public static void Ambiguous()
        {
            string text = "Shared text";
            Verse.Log.Message(text);
            Verse.Widgets.Label(default(Verse.Rect), text);
        }

        public static void SameTextUi()
        {
            Verse.Widgets.Label(default(Verse.Rect), "Context-sensitive text");
        }

        public static void SameTextLog()
        {
            Verse.Log.Message("Context-sensitive text");
        }
    }
}
