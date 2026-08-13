using System;

namespace Verse
{
    public static class Log
    {
        public static void Warning(string message)
        {
            Console.Error.WriteLine(message);
        }
    }
}

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        public static string TestPackPath { get; set; }

        public static string GetLocalPackPath()
        {
            return TestPackPath;
        }
    }
}
