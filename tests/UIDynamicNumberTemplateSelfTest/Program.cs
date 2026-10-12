using AutoTranslator_Core;
using System;

namespace UIDynamicNumberTemplateSelfTest
{
    internal static class Program
    {
        private static int Main()
        {
            AssertEqual(
                "Vanilla Mod ({num1}) {num2}",
                UIDynamicNumberTemplate.Normalize("Vanilla Mod (123.87MB) 4"),
                "file size and trailing count");
            AssertEqual(
                "Vanilla Mod ({num1}) {num2}",
                UIDynamicNumberTemplate.Normalize("Vanilla Mod ({num1}.87MB) {num2}"),
                "legacy partial placeholder collapse");
            AssertEqual(
                "Cap: {num1}",
                UIDynamicNumberTemplate.Normalize("Cap: 10289L"),
                "liter unit");
            AssertEqual(
                "Latency: {num1}",
                UIDynamicNumberTemplate.Normalize("Latency: 12.5 ms"),
                "spaced millisecond unit");
            AssertEqual(
                "Tick: {num1}",
                UIDynamicNumberTemplate.Normalize("Tick: 8 \u00B5s"),
                "microsecond unit");
            AssertEqual(
                "Version 123.45ABC",
                UIDynamicNumberTemplate.Normalize("Version 123.45ABC"),
                "unknown suffix is not partially normalized");
            AssertEqual(
                "cached {num1} plus {num2}",
                UIDynamicNumberTemplate.Normalize("cached {num1} plus 10L"),
                "new slot follows persisted placeholders");
            AssertTrue(
                UIDynamicNumberTemplate.HasMixedPersistedTemplate("Vanilla Weapons Expanded (5MB) {num1}"),
                "unsafe mixed legacy template");
            AssertFalse(
                UIDynamicNumberTemplate.HasMixedPersistedTemplate("Vanilla Mod ({num1}.87MB) {num2}"),
                "collapsible legacy partial template");
            AssertEqual(
                "Translated (123.87MB) 4",
                UIDynamicNumberTemplate.Restore(
                    "Vanilla Mod (123.87MB) 4",
                    "Translated ({num1}) {num2}"),
                "full dynamic value restoration");
            AssertEqual(
                "Memory: {num1}",
                UIDynamicNumberTemplate.Normalize("Memory: 1,234,567MB"),
                "multi-group thousands value");
            AssertEqual(
                "記憶體：1,234,567MB",
                UIDynamicNumberTemplate.Restore(
                    "Memory: 1,234,567MB",
                    "記憶體：{num1}"),
                "multi-group thousands restoration");
            string noNumber = "Ordinary label without a number";
            AssertReferenceEqual(
                noNumber,
                UIDynamicNumberTemplate.Normalize(noNumber),
                "no-number fast path");

            Console.WriteLine("PASS: 13 UI dynamic-number template self-tests");
            return 0;
        }

        private static void AssertEqual(string expected, string actual, string name)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
            throw new InvalidOperationException(
                name + " failed. Expected [" + expected + "] but got [" + actual + "].");
        }

        private static void AssertTrue(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name + " failed.");
        }

        private static void AssertFalse(bool value, string name)
        {
            if (value) throw new InvalidOperationException(name + " failed.");
        }

        private static void AssertReferenceEqual(string expected, string actual, string name)
        {
            if (object.ReferenceEquals(expected, actual)) return;
            throw new InvalidOperationException(name + " failed: a new string instance was allocated.");
        }
    }
}
