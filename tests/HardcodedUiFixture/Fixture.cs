using System;

namespace UnityEngine
{
    public struct Rect
    {
        public float x;
    }
}

namespace Verse
{
    public static class Widgets
    {
        public static string LastLabel;

        public static void Label(UnityEngine.Rect rect, string label)
        {
            LastLabel = label;
        }
    }
}

namespace Atc.HardcodedUiFixture
{
    public sealed class FixtureWindow
    {
        public void DoWindowContents(UnityEngine.Rect rect)
        {
            Verse.Widgets.Label(rect, "ATC fixture hardcoded label");
        }

        public void WriteLogOnly()
        {
            string message = "ATC fixture hardcoded label";
            if (!string.IsNullOrEmpty(message))
            {
                // This is intentionally not a UI call and must not become a candidate.
                Console.WriteLine(message);
            }
        }
    }
}
