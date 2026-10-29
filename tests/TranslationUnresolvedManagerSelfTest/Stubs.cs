using System;
using System.Collections.Generic;

namespace Verse
{
    public sealed class ModMetaData
    {
        public string PackageId { get; set; }
        public bool Active { get; set; }
    }

    public static class ModLister
    {
        public static List<ModMetaData> AllInstalledMods = new List<ModMetaData>
        {
            new ModMetaData { PackageId = "example.mod", Active = true }
        };
    }

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
    public sealed class AutoTranslatorSettings
    {
        public bool OnlyScanActiveMods = true;
    }

    public static class AutoTranslatorMod
    {
        public static AutoTranslatorSettings Settings = new AutoTranslatorSettings();
    }

    public static partial class AutoTranslatorScanner
    {
        public static string TestPackPath { get; set; }

        public static string GetLocalPackPath()
        {
            return TestPackPath;
        }
    }
}
