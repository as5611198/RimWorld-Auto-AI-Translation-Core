using AutoTranslator_Core.TargetedHardcodedUi;
using System;
using System.IO;

namespace HardcodedUiDecisionSelfTest
{
    internal static class Program
    {
        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "atc-hardcoded-decision-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                HardcodedUiPatchEntry direct = CreateEntry("direct_ui_call", "Widgets", "Label");
                HardcodedUiDecisionRecord automatic = HardcodedUiBaselineDecisionAnalyzer.Analyze(direct);
                Assert(automatic.AutomaticDecision == HardcodedUiAutomaticDecision.Translate,
                    "direct UI call must be proven translatable locally");
                Assert(automatic.EffectiveDecision == HardcodedUiAutomaticDecision.Translate,
                    "automatic decision should be effective without an override");
                Assert(automatic.AutomaticReasonCode == "UI_DIRECT_CALL", "direct reason code");
                Assert(automatic.SemanticRole == "label", "semantic role");

                automatic.SetUserOverride(HardcodedUiUserOverride.DoNotTranslate);
                Assert(automatic.EffectiveDecision == HardcodedUiAutomaticDecision.DoNotTranslate,
                    "user override must win over automatic decision");

                direct.AssemblySha256 = "changed";
                HardcodedUiDecisionRecord reanalyzed = HardcodedUiBaselineDecisionAnalyzer.Analyze(direct, automatic);
                Assert(reanalyzed.UserOverride == HardcodedUiUserOverride.DoNotTranslate,
                    "reanalyzing changed input must preserve the user override");
                Assert(reanalyzed.DiagnosticFlags.Contains("previous_analysis_stale"),
                    "changed input must record stale previous analysis");
                reanalyzed.RestoreAutomaticDecision();
                Assert(reanalyzed.EffectiveDecision == HardcodedUiAutomaticDecision.Translate,
                    "restore automatic must only clear the override");

                HardcodedUiPatchEntry unknown = CreateEntry("review_string_literal", string.Empty, string.Empty);
                unknown.EntryId = "hardcoded-ui:unknown";
                HardcodedUiDecisionRecord unknownDecision = HardcodedUiBaselineDecisionAnalyzer.Analyze(unknown);
                Assert(unknownDecision.AutomaticDecision == HardcodedUiAutomaticDecision.Uncertain,
                    "recall-only literal must remain uncertain");

                string path = Path.Combine(root, "HardcodedUiAnalysis.v1.json");
                var store = new HardcodedUiDecisionStore(path);
                store.UpsertMany(new[] { reanalyzed, unknownDecision });
                var reloaded = new HardcodedUiDecisionStore(path);
                Assert(reloaded.TryGet(reanalyzed.EntryId, out HardcodedUiDecisionRecord loaded),
                    "persisted decision should reload");
                Assert(loaded.EffectiveDecision == HardcodedUiAutomaticDecision.Translate,
                    "effective decision should survive persistence");
                loaded.SetUserOverride(HardcodedUiUserOverride.DoNotTranslate);
                reloaded.TryGet(reanalyzed.EntryId, out HardcodedUiDecisionRecord defensiveCopy);
                Assert(defensiveCopy.UserOverride == HardcodedUiUserOverride.None,
                    "store reads must return defensive copies");

                Console.WriteLine("PASS: hardcoded UI decision model and store self-test");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }

        private static HardcodedUiPatchEntry CreateEntry(
            string discoveryKind,
            string callType,
            string callMethod)
        {
            return new HardcodedUiPatchEntry
            {
                EntryId = "hardcoded-ui:direct",
                PackageId = "example.mod",
                AssemblyRelativePath = "Assemblies/Example.dll",
                AssemblySha256 = "abc",
                AssemblyMvid = "mvid",
                MethodSignature = "Example.Window::Draw()->System.Void",
                MethodMetadataToken = 123,
                MethodIlFingerprint = "il-hash",
                LiteralOrdinal = 0,
                Literal = "Visible label",
                DeclaringType = "Example.Window",
                MethodName = "Draw",
                CallDeclaringType = callType,
                CallMethodName = callMethod,
                DiscoveryKind = discoveryKind
            };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
