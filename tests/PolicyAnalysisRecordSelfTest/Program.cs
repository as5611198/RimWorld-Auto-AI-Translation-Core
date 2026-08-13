using AutoTranslator_Core;
using AutoTranslator_Core.TranslationPolicy;
using System;
using System.Collections.Generic;
using System.IO;

namespace PolicyAnalysisRecordSelfTest
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                PolicyAnalysisCloudRecord record = new PolicyAnalysisCloudRecord
                {
                    PackageId = "author.mod",
                    GameVersion = "1.6",
                    SourceFingerprint = "fingerprint-a",
                    PolicyVersion = "1",
                    PromptVersion = "4",
                    CandidateCount = 2,
                    AllowedCandidateIds = new List<string> { "tpc_a" },
                    Complete = true
                };
                Assert(PolicyAnalysisRecordValidator.IsUsable(record, "AUTHOR.MOD", "1.6", "fingerprint-a", "1", "4"), "exact record rejected");
                record.Complete = false;
                Assert(!PolicyAnalysisRecordValidator.IsUsable(record, "author.mod", "1.6", "fingerprint-a", "1", "4"), "incomplete record accepted");
                record.Complete = true;
                Assert(!PolicyAnalysisRecordValidator.IsUsable(record, "author.mod", "1.6", "fingerprint-b", "1", "4"), "wrong version fingerprint accepted");
                record.AllowedCandidateIds.Add("tpc_b");
                record.AllowedCandidateIds.Add("tpc_c");
                Assert(!PolicyAnalysisRecordValidator.IsUsable(record, "author.mod", "1.6", "fingerprint-a", "1", "4"), "more allowed IDs than candidates accepted");

                record.AllowedCandidateIds = new List<string> { "hardcoded-ui:mixed" };
                Assert(!PolicyAnalysisRecordValidator.IsUsable(record, "author.mod", "1.6", "fingerprint-a", "1", "4"), "DLL ID accepted in XML domain");

                var dllRecord = new PolicyAnalysisCloudRecord
                {
                    CandidateDomain = PolicyAnalysisCandidateDomain.Dll,
                    PackageId = "author.mod",
                    GameVersion = "1.6",
                    SourceFingerprint = "dll-fingerprint",
                    PolicyVersion = "dll-policy-2",
                    PromptVersion = "dll-prompt-1",
                    CandidateCount = 1,
                    AllowedCandidateIds = new List<string> { "hardcoded-ui:abc" },
                    Complete = true
                };
                Assert(PolicyAnalysisRecordValidator.IsUsable(
                    dllRecord,
                    PolicyAnalysisCandidateDomain.Dll,
                    "author.mod",
                    "1.6",
                    "dll-fingerprint",
                    "dll-policy-2",
                    "dll-prompt-1"), "exact DLL record rejected");
                Assert(!PolicyAnalysisRecordValidator.IsUsable(
                    dllRecord,
                    PolicyAnalysisCandidateDomain.Xml,
                    "author.mod",
                    "1.6",
                    "dll-fingerprint",
                    "dll-policy-2",
                    "dll-prompt-1"), "DLL record accepted as XML");

                record.SchemaVersion = 1;
                record.CandidateDomain = PolicyAnalysisCandidateDomain.Xml;
                record.AllowedCandidateIds = new List<string> { "tpc_legacy" };
                record.CandidateCount = 1;
                Assert(PolicyAnalysisRecordValidator.IsUsable(
                    record,
                    "author.mod",
                    "1.6",
                    "fingerprint-a",
                    "1",
                    "4"), "legacy XML record rejected");
                Assert(!PolicyAnalysisRecordValidator.IsUsable(
                    record,
                    PolicyAnalysisCandidateDomain.Dll,
                    "author.mod",
                    "1.6",
                    "fingerprint-a",
                    "1",
                    "4"), "legacy schema accepted for DLL domain");

                string root = Path.Combine(Path.GetTempPath(), "ATC_PolicyFingerprint_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "Defs"));
                string source = Path.Combine(root, "Defs", "Thing.xml");
                File.WriteAllText(source, "<Defs><ThingDef /></Defs>");
                string first = TranslationPolicySourceFingerprint.Compute(root, "1.6|Simplified", new[] { source });
                File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-30));
                string timestampChanged = TranslationPolicySourceFingerprint.Compute(root, "1.6|Simplified", new[] { source });
                Assert(first == timestampChanged, "fingerprint changed with installation timestamp");
                File.WriteAllText(source, "<Defs><ThingDef><label>changed</label></ThingDef></Defs>");
                string contentChanged = TranslationPolicySourceFingerprint.Compute(root, "1.6|Simplified", new[] { source });
                Assert(first != contentChanged, "fingerprint ignored source content changes");
                string branchChanged = TranslationPolicySourceFingerprint.Compute(root, "1.7|Simplified", new[] { source });
                Assert(contentChanged != branchChanged, "fingerprint ignored game branch identity");
                string dllFirst = TranslationPolicySourceFingerprint.ComputeCanonicalRecords(
                    "1.6|Simplified|dll",
                    new[] { "Assemblies/A.dll|hash-a|mvid-a", "Assemblies/B.dll|hash-b|mvid-b" });
                string dllReordered = TranslationPolicySourceFingerprint.ComputeCanonicalRecords(
                    "1.6|Simplified|dll",
                    new[] { "Assemblies/B.dll|hash-b|mvid-b", "Assemblies/A.dll|hash-a|mvid-a" });
                string dllChanged = TranslationPolicySourceFingerprint.ComputeCanonicalRecords(
                    "1.6|Simplified|dll",
                    new[] { "Assemblies/A.dll|hash-updated|mvid-a", "Assemblies/B.dll|hash-b|mvid-b" });
                Assert(dllFirst == dllReordered, "DLL fingerprint depended on candidate order");
                Assert(dllFirst != dllChanged, "DLL fingerprint ignored assembly identity changes");
                Directory.Delete(root, true);
                Console.WriteLine("PASS: 14 policy analysis record assertions");
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
