using AutoTranslator_Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace TranslationCoreRegressionSelfTest
{
    internal static class Program
    {
        private static int _passed;

        private static int Main()
        {
            try
            {
                Run("same-file named Def inheritance", TestSameFileInheritance);
                Run("cross-file chained Def inheritance", TestCrossFileInheritance);
                Run("Def inheritance cycle terminates", TestInheritanceCycle);
                Run("Inherit false blocks parent values", TestInheritFalse);
                Run("explicit cloud translation order wins", TestExplicitLoadOrder);
                Run("game load order is deterministic", TestGameLoadOrder);
                Run("manual and correction layers load last", TestTranslationLayers);
                Run("translation files group by exact package", TestFileGrouping);
                Run("cloud HTTP retry decisions", TestCloudRetryPolicy);
                Run("stale cloud record replacement", TestCloudReplacement);
                Console.WriteLine("PASS: " + _passed + " translation core regression self-tests");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
        }

        private static void TestSameFileInheritance()
        {
            List<ResolvedDefXmlNode> resolved = Resolve(
                "<Defs><ThingDef Name='Human'><label>Human</label></ThingDef>" +
                "<ThingDef ParentName='Human'><defName>Dummy</defName></ThingDef></Defs>");
            ResolvedDefXmlNode dummy = resolved.Single(node =>
                DefXmlInheritanceResolver.GetDirectChildText(node.OriginalNode, "defName") == "Dummy");
            Equal("Human", DefXmlInheritanceResolver.GetDirectChildText(dummy.ResolvedNode, "label"), "Inherited label");
        }

        private static void TestCrossFileInheritance()
        {
            XmlDocument first = Parse("<Defs><ThingDef Name='Base'><label>Base label</label><description>Base description</description></ThingDef></Defs>");
            XmlDocument second = Parse("<Defs><ThingDef Name='Middle' ParentName='Base'><label>Middle label</label></ThingDef><ThingDef ParentName='Middle'><defName>Child</defName></ThingDef></Defs>");
            List<ResolvedDefXmlNode> resolved = DefXmlInheritanceResolver.Resolve(new[]
            {
                new DefXmlSourceDocument { Document = first, SourceFile = "A.xml" },
                new DefXmlSourceDocument { Document = second, SourceFile = "B.xml" }
            });
            ResolvedDefXmlNode child = resolved.Single(node =>
                DefXmlInheritanceResolver.GetDirectChildText(node.OriginalNode, "defName") == "Child");
            Equal("Middle label", DefXmlInheritanceResolver.GetDirectChildText(child.ResolvedNode, "label"), "Child override chain");
            Equal("Base description", DefXmlInheritanceResolver.GetDirectChildText(child.ResolvedNode, "description"), "Cross-file inherited description");
        }

        private static void TestInheritanceCycle()
        {
            int warnings = 0;
            List<ResolvedDefXmlNode> resolved = DefXmlInheritanceResolver.Resolve(new[]
            {
                new DefXmlSourceDocument
                {
                    Document = Parse("<Defs><ThingDef Name='A' ParentName='B'><label>A</label></ThingDef><ThingDef Name='B' ParentName='A'><label>B</label></ThingDef></Defs>")
                }
            }, _ => warnings++);
            True(resolved.Count == 2 && warnings > 0, "Cycle should terminate with a warning");
        }

        private static void TestInheritFalse()
        {
            List<ResolvedDefXmlNode> resolved = Resolve(
                "<Defs><ThingDef Name='Base'><label>Base label</label></ThingDef>" +
                "<ThingDef ParentName='Base' Inherit='False'><defName>Child</defName></ThingDef></Defs>");
            ResolvedDefXmlNode child = resolved.Single(node =>
                DefXmlInheritanceResolver.GetDirectChildText(node.OriginalNode, "defName") == "Child");
            Equal(string.Empty, DefXmlInheritanceResolver.GetDirectChildText(child.ResolvedNode, "label"), "Inherit false label");
        }

        private static void TestExplicitLoadOrder()
        {
            string[] files = { "z_mod.xml", "a_mod.xml" };
            List<string> ordered = TranslationLoadOrderPolicy.OrderFiles(files, new[] { "z.mod", "a.mod" }, new[] { "z.mod", "a.mod" });
            Equal("a_mod.xml", ordered[0], "Lower explicit priority first");
            Equal("z_mod.xml", ordered[1], "Highest explicit priority last");
        }

        private static void TestGameLoadOrder()
        {
            string[] files = { "high_mod.xml", "low_mod.xml" };
            List<string> ordered = TranslationLoadOrderPolicy.OrderFiles(files, new[] { "low.mod", "high.mod" }, Array.Empty<string>());
            Equal("low_mod.xml", ordered[0], "Low game order first");
            Equal("high_mod.xml", ordered[1], "High game order last");
        }

        private static void TestTranslationLayers()
        {
            string[] files =
            {
                Path.Combine("Keyed", "foo_bar.xml"),
                Path.Combine("Manual_Translation", "foo_bar_ManualTranslation.xml"),
                Path.Combine("Keyed", "foo_bar_CloudCorrections.xml")
            };
            List<string> ordered = TranslationLoadOrderPolicy.OrderFiles(files, new[] { "foo.bar" }, Array.Empty<string>());
            True(ordered[0].EndsWith("foo_bar.xml", StringComparison.Ordinal), "Base layer first");
            True(ordered[1].IndexOf("ManualTranslation", StringComparison.Ordinal) >= 0, "Manual layer second");
            True(ordered[2].IndexOf("CloudCorrections", StringComparison.Ordinal) >= 0, "Correction layer last");
        }

        private static void TestFileGrouping()
        {
            Dictionary<string, List<string>> grouped = TranslationLoadOrderPolicy.GroupFilesByPackage(
                new[] { "foo_bar_A.xml", "foo_bar_addon_A.xml", "unmatched.xml" },
                new[] { "foo.bar", "foo.bar.addon" });
            Equal(1, grouped["foo.bar"].Count, "Base package group");
            Equal(1, grouped["foo.bar.addon"].Count, "Addon package group");
        }

        private static void TestCloudRetryPolicy()
        {
            True(CloudDownloadRecoveryPolicy.ShouldRefreshRegistry(404, true), "Record 404 refresh");
            True(!CloudDownloadRecoveryPolicy.ShouldRefreshRegistry(404, false), "Package 404 does not refresh");
            True(!CloudDownloadRecoveryPolicy.ShouldRetry(404), "Permanent 404");
            True(CloudDownloadRecoveryPolicy.ShouldRetry(429), "Rate limit retry");
            True(CloudDownloadRecoveryPolicy.ShouldRetry(503), "Server error retry");
        }

        private static void TestCloudReplacement()
        {
            var stale = new CloudModRecord { RecordId = "old", PackageId = "foo.bar", Language = "Thai", TranslationType = "Manual" };
            var verifiedAi = new CloudModRecord { RecordId = "ai", PackageId = "foo.bar", Language = "Thai", TranslationType = "AI_Auto", IsVerified = true, LastUpdated = DateTime.UtcNow };
            var manual = new CloudModRecord { RecordId = "manual", PackageId = "foo.bar", Language = "Thai", TranslationType = "Manual", LastUpdated = DateTime.UtcNow.AddDays(-1) };
            CloudModRecord selected = CloudDownloadRecoveryPolicy.SelectReplacement(new[] { stale, verifiedAi, manual }, "foo.bar", "Thai", stale);
            Equal("manual", selected.RecordId, "Same translation type should be preferred");
        }

        private static List<ResolvedDefXmlNode> Resolve(string xml)
        {
            return DefXmlInheritanceResolver.Resolve(new[] { new DefXmlSourceDocument { Document = Parse(xml), SourceFile = "Defs.xml" } });
        }

        private static XmlDocument Parse(string xml)
        {
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(xml);
            return document;
        }

        private static void Run(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS: " + name);
        }

        private static void True(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message + " should be true.");
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(message + ": expected <" + expected + ">, actual <" + actual + ">.");
        }
    }
}
