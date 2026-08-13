using AutoTranslator_Core;
using System;
using System.Collections.Generic;
using System.IO;

namespace TranslationSourcePrioritySelfTest
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                List<TranslationSourceCategory> defaults =
                    TranslationSourcePriorityPolicy.ParseOrder(TranslationSourcePriorityPolicy.DefaultOrder);
                Assert(defaults.Count == 5, "default category count");
                Assert(defaults[0] == TranslationSourceCategory.UserManual, "manual is not first");
                Assert(defaults[4] == TranslationSourceCategory.Automatic, "automatic is not last");

                List<TranslationSourceCategory> repaired =
                    TranslationSourcePriorityPolicy.ParseOrder("Cloud,Cloud,garbage");
                Assert(repaired.Count == 5 && repaired[0] == TranslationSourceCategory.Cloud,
                    "invalid order was not repaired deterministically");

                AutoTranslatorSettings settings = new AutoTranslatorSettings();
                settings.ModTranslationSourcePriorityOverrides["author.mod"] =
                    "ModNative,UserManual,ExternalHuman,Cloud,Automatic";
                Assert(TranslationSourcePriorityPolicy.GetRank(
                    settings, "AUTHOR.MOD", TranslationSourceCategory.ModNative) == 0,
                    "case-insensitive per-mod override failed");
                Assert(TranslationSourcePriorityPolicy.GetRank(
                    settings, "other.mod", TranslationSourceCategory.UserManual) == 0,
                    "global fallback failed");
                Assert(TranslationSourcePriorityPolicy.ClassifyProvenance("ExternalPatch") ==
                    TranslationSourceCategory.ExternalHuman, "external provenance classification");
                Assert(TranslationSourcePriorityPolicy.ClassifyProvenance("AI") ==
                    TranslationSourceCategory.Automatic, "automatic provenance classification");

                string scopeRoot = Path.Combine(Path.GetTempPath(), "ATC_CloudScope_" + Guid.NewGuid().ToString("N"));
                string otherRoot = scopeRoot + "_other";
                Directory.CreateDirectory(scopeRoot);
                Directory.CreateDirectory(otherRoot);
                string inScope = Path.Combine(scopeRoot, "downloaded.xml");
                string outOfScope = Path.Combine(otherRoot, "unrelated.xml");
                string nonXml = Path.Combine(scopeRoot, "metadata.json");
                File.WriteAllText(inScope, "<LanguageData />");
                File.WriteAllText(outOfScope, "<LanguageData />");
                File.WriteAllText(nonXml, "{}");
                Assert(CloudDownloadedFileScope.TryResolveXml(scopeRoot, inScope, out string resolved) &&
                    string.Equals(resolved, Path.GetFullPath(inScope), StringComparison.OrdinalIgnoreCase),
                    "downloaded XML was not accepted");
                Assert(!CloudDownloadedFileScope.TryResolveXml(scopeRoot, outOfScope, out _),
                    "unrelated mod XML escaped cloud provenance scope");
                Assert(!CloudDownloadedFileScope.TryResolveXml(scopeRoot, nonXml, out _),
                    "non-XML metadata entered translation provenance");
                Directory.Delete(scopeRoot, true);
                Directory.Delete(otherRoot, true);
                Console.WriteLine("PASS: 11 translation source priority assertions");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
