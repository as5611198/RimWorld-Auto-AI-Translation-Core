using AutoTranslator_Core.TargetedHardcodedUi;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HardcodedUiIlDataflowSelfTest
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage: selftest <fixture.dll>");
                return 2;
            }
            try
            {
                string path = Path.GetFullPath(args[0]);
                List<HardcodedUiPatchEntry> entries = ReadEntries(path);
                HardcodedUiIlAnalysisResult result = HardcodedUiIlDataflowAnalyzer.Analyze(path, entries);
                AssertDecision(entries, result, "Direct label", HardcodedUiAutomaticDecision.Translate);
                AssertDecision(entries, result, "Local label", HardcodedUiAutomaticDecision.Translate);
                AssertDecision(entries, result, "Hello {0}", HardcodedUiAutomaticDecision.Translate);
                AssertDecision(entries, result, "Wrapped label", HardcodedUiAutomaticDecision.Translate);
                AssertDecision(entries, result, "Branch A", HardcodedUiAutomaticDecision.Translate);
                AssertDecision(entries, result, "Branch B", HardcodedUiAutomaticDecision.Translate);
                AssertDecision(entries, result, "Developer diagnostic", HardcodedUiAutomaticDecision.DoNotTranslate);
                AssertDecision(entries, result, "failed", HardcodedUiAutomaticDecision.DoNotTranslate);
                AssertDecision(entries, result, "The ritual failed.", HardcodedUiAutomaticDecision.Translate);
                AssertDecision(entries, result, "Builder label", HardcodedUiAutomaticDecision.Translate);
                AssertDecision(entries, result, "Shared text", HardcodedUiAutomaticDecision.Uncertain);
                AssertDecision(entries, result, "Context-sensitive text", "SameTextUi",
                    HardcodedUiAutomaticDecision.Translate);
                AssertDecision(entries, result, "Context-sensitive text", "SameTextLog",
                    HardcodedUiAutomaticDecision.DoNotTranslate);
                if (result.Diagnostics.Count > 0)
                    throw new InvalidOperationException("Unexpected diagnostics: " + string.Join(" | ", result.Diagnostics));
                Console.WriteLine("PASS: Mono.Cecil CFG/dataflow self-test (direct, local, format, wrapper, branch, non-UI, ambiguous)");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
        }

        private static List<HardcodedUiPatchEntry> ReadEntries(string path)
        {
            var output = new List<HardcodedUiPatchEntry>();
            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path))
            {
                foreach (MethodDefinition method in assembly.MainModule.Types
                             .SelectMany(Flatten)
                             .SelectMany(type => type.Methods)
                             .Where(method => method.HasBody))
                {
                    int ordinal = -1;
                    foreach (Instruction instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode.Code != Code.Ldstr) continue;
                        ordinal++;
                        string literal = instruction.Operand as string;
                        output.Add(new HardcodedUiPatchEntry
                        {
                            EntryId = "hardcoded-ui:test-" + output.Count,
                            PackageId = "fixture.mod",
                            AssemblyRelativePath = Path.GetFileName(path),
                            AssemblySha256 = HardcodedUiMethodIdentity.ComputeFileSha256(path),
                            AssemblyMvid = assembly.MainModule.Mvid.ToString("D"),
                            MethodSignature = method.FullName,
                            MethodMetadataToken = method.MetadataToken.ToInt32(),
                            MethodIlFingerprint = "fixture-il",
                            LiteralOrdinal = ordinal,
                            Literal = literal,
                            DeclaringType = method.DeclaringType.FullName,
                            MethodName = method.Name,
                            DiscoveryKind = "review_string_literal"
                        });
                    }
                }
            }
            return output;
        }

        private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
        {
            yield return type;
            foreach (TypeDefinition nested in type.NestedTypes.SelectMany(Flatten)) yield return nested;
        }

        private static void AssertDecision(
            IEnumerable<HardcodedUiPatchEntry> entries,
            HardcodedUiIlAnalysisResult result,
            string literal,
            HardcodedUiAutomaticDecision expected)
        {
            HardcodedUiPatchEntry entry = entries.Single(item => item.Literal == literal);
            if (!result.Decisions.TryGetValue(entry.EntryId, out HardcodedUiDecisionRecord record))
                throw new InvalidOperationException("Missing decision for " + literal);
            if (record.AutomaticDecision != expected)
                throw new InvalidOperationException(
                    literal + ": expected " + expected + ", got " + record.AutomaticDecision +
                    " (" + record.AutomaticReasonCode + ", " + record.EvidencePath + ")");
            if (string.IsNullOrWhiteSpace(record.AutomaticReasonCode) ||
                string.IsNullOrWhiteSpace(record.EvidencePath))
                throw new InvalidOperationException(literal + ": missing reason code or evidence path");
        }

        private static void AssertDecision(
            IEnumerable<HardcodedUiPatchEntry> entries,
            HardcodedUiIlAnalysisResult result,
            string literal,
            string methodName,
            HardcodedUiAutomaticDecision expected)
        {
            HardcodedUiPatchEntry entry = entries.Single(item =>
                item.Literal == literal && item.MethodName == methodName);
            if (!result.Decisions.TryGetValue(entry.EntryId, out HardcodedUiDecisionRecord record))
                throw new InvalidOperationException("Missing decision for " + methodName + ": " + literal);
            if (record.AutomaticDecision != expected)
                throw new InvalidOperationException(
                    methodName + ": expected " + expected + ", got " + record.AutomaticDecision +
                    " (" + record.AutomaticReasonCode + ", " + record.EvidencePath + ")");
            if (string.IsNullOrWhiteSpace(record.AutomaticReasonCode) ||
                string.IsNullOrWhiteSpace(record.EvidencePath))
                throw new InvalidOperationException(methodName + ": missing reason code or evidence path");
        }
    }
}
