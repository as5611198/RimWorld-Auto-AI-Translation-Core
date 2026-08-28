using AutoTranslator_Core;
using AutoTranslator_Core.TranslationPolicy;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace TranslationPolicyShadowSelfTest
{
    internal static class Program
    {
        private sealed class AuditModRoot
        {
            public string PackageId { get; set; }
            public string ModName { get; set; }
            public string RootPath { get; set; }
        }

        private sealed class AuditError
        {
            public string Source { get; set; }
            public string Error { get; set; }
        }

        private sealed class AuditReport
        {
            public AuditReport()
            {
                ReportVersion = 1;
                GeneratedUtc = string.Empty;
                ModsConfigPath = string.Empty;
                WorkshopRoot = string.Empty;
                GameContentRoot = string.Empty;
                ActivePackageIds = new List<string>();
                MissingPackageIds = new List<string>();
                Errors = new List<AuditError>();
                Result = new TranslationPolicyShadowResult();
            }

            public int ReportVersion { get; set; }
            public string GeneratedUtc { get; set; }
            public long ElapsedMilliseconds { get; set; }
            public int ActualAiApiCalls { get; set; }
            public long ActualConsumedTokens { get; set; }
            public int TranslationWrites { get; set; }
            public int RuntimeInjections { get; set; }
            public string ModsConfigPath { get; set; }
            public string WorkshopRoot { get; set; }
            public string GameContentRoot { get; set; }
            public List<string> ActivePackageIds { get; set; }
            public List<string> MissingPackageIds { get; set; }
            public int DiscoveredModCount { get; set; }
            public int ScannedModCount { get; set; }
            public int ScannedFileCount { get; set; }
            public int ScanErrorCount { get; set; }
            public List<AuditError> Errors { get; set; }
            public TranslationPolicyShadowResult Result { get; set; }
        }

        private static int _passed;

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0].Equals("--audit-active", StringComparison.OrdinalIgnoreCase))
                {
                    return RunActiveAudit(args);
                }

                RunTest("precedence and known cases", TestPrecedenceAndKnownCases);
                RunTest("native target local filter", TestNativeTargetLocalFilter);
                RunTest("grammar safety", TestGrammarSafety);
                RunTest("raw Def and Keyed XML scanning", TestRawXmlScanning);
                RunTest("DefInjected XML scanning", TestDefInjectedXmlScanning);
                RunTest("translation XML nested list flattening", TestTranslationXmlNestedListFlattening);
                RunTest("path normalization and schema grouping", TestPathNormalizationAndGrouping);
                RunTest("deterministic bounded streaming result", TestDeterministicBoundedResult);
                RunTest("token and latency estimator", TestEstimatorArithmetic);
                RunTest("hard limits and completion state", TestLimitsAndCompletionState);
                RunTest("secure XML parsing", TestSecureXmlParsing);
                RunTest("About.xml direct package identity", TestAboutDirectPackageIdentity);
                RunTest("RimWorld 1.6 LoadFolders audit roots", TestAuditLoadFolderResolution);
                RunTest("custom audit roots and root-level DefInjected type", TestAuditCustomRootsAndGeneralDefType);
                RunTest("Agent run budget atomic limits", TestAgentRunBudgetAtomicLimits);
                RunTest("Agent runtime batch planner coalesces across DefTypes", TestAgentBatchPlannerAcrossDefTypes);
                RunTest("Agent identity and cache invalidation", TestAgentIdentityAndCacheInvalidation);
                RunTest("Agent candidate cache incrementally reuses expanded groups", TestAgentCandidateCacheIncrementalPlanning);
                RunTest("Agent candidate cache scope invalidation", TestAgentCandidateCacheScopeInvalidation);
                RunTest("strict Agent response acceptance", TestAgentResponseParserAcceptance);
                RunTest("strict Agent response rejection", TestAgentResponseParserRejection);
                RunTest("Agent token estimator output cap and saturation", TestAgentTokenEstimator);
                RunTest("Agent decision cache lifecycle", TestAgentDecisionCacheLifecycle);
                RunTest("Agent application fallback and timeout bounds", TestAgentApplicationFallbackAndTimeout);
                RunTest("Agent outcome unresolved reporting boundaries", TestAgentOutcomeReportingBoundaries);

                Console.WriteLine("PASS: " + _passed + " translation policy shadow self-tests");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
        }

        private static void TestPrecedenceAndKnownCases()
        {
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.alienRace.customLabel", "customLabel", "Visible label"),
                TranslationPolicyDecision.HardDeny,
                "protected_path");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.label", "label", "Power"),
                TranslationPolicyDecision.HardAllow,
                "known_text_field");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.label", "label", "On/Off"),
                TranslationPolicyDecision.HardAllow,
                "known_text_field");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "CoolAnimation.label", "label", "Animation controls"),
                TranslationPolicyDecision.HardAllow,
                "known_text_field");
            AssertDecision(
                Candidate(
                    TranslationPolicyBucket.DefInjected,
                    "EyeShape.label",
                    "label",
                    "Round eyes",
                    "test.mod",
                    "FacialAnimation.EyeballShapeDef"),
                TranslationPolicyDecision.HardDeny,
                "protected_def_type");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.customNarrative", "customNarrative", "A readable sentence"),
                TranslationPolicyDecision.Ambiguous,
                "unknown_field_semantics");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.customSlot", "customSlot", "SomeCamelCase"),
                TranslationPolicyDecision.Ambiguous,
                "identifier_like_unknown");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.thingDef", "thingDef", "Steel"),
                TranslationPolicyDecision.HardDeny,
                "denied_field");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.debugLabel", "debugLabel", "Internal trace"),
                TranslationPolicyDecision.HardDeny,
                "denied_field");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.label", "label", "123, 456"),
                TranslationPolicyDecision.HardDeny,
                "structured_numeric_value");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.requiresAcceptance", "requiresAcceptance", "false"),
                TranslationPolicyDecision.HardDeny,
                "structured_boolean_value");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.countRange", "countRange", "4~8"),
                TranslationPolicyDecision.HardDeny,
                "structured_numeric_range");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.currency", "currency", "PowerCoin"),
                TranslationPolicyDecision.HardDeny,
                "enum_or_reference_value");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.workSkill", "workSkill", "Cooking"),
                TranslationPolicyDecision.HardDeny,
                "enum_or_reference_value");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.journalText", "journalText", "A journal entry"),
                TranslationPolicyDecision.HardAllow,
                "known_text_field");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Event.disabledReason", "disabledReason", "BANANANOTENOUGH"),
                TranslationPolicyDecision.HardDeny,
                "localization_key_reference");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Event.label", "label", "SILVERNOTENOUGH"),
                TranslationPolicyDecision.HardDeny,
                "localization_key_reference");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Event.label", "label", "GOBACK"),
                TranslationPolicyDecision.HardDeny,
                "localization_key_reference");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Weapon.label", "label", "IED"),
                TranslationPolicyDecision.HardAllow,
                "known_text_field");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Weapon.label", "label", "GAU4"),
                TranslationPolicyDecision.HardAllow,
                "known_text_field");
            AssertDecision(
                Candidate(TranslationPolicyBucket.Keyed, "ATC.Power", "ATC.Power", "Power"),
                TranslationPolicyDecision.HardAllow,
                "keyed_text");
            AssertDecision(
                Candidate(TranslationPolicyBucket.Keyed, "ATC.Pronoun", "ATC.Pronoun", "I"),
                TranslationPolicyDecision.HardAllow,
                "keyed_text");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.label", "label", "A"),
                TranslationPolicyDecision.HardAllow,
                "known_text_field");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "Def.unknownCode", "unknownCode", "X"),
                TranslationPolicyDecision.Ambiguous,
                "unknown_field_semantics");
            AssertDecision(
                Candidate(TranslationPolicyBucket.Keyed, "ATC.thingDef", "ATC.thingDef", "Visible thing"),
                TranslationPolicyDecision.HardAllow,
                "keyed_text");
            AssertDecision(
                Candidate(TranslationPolicyBucket.Keyed, "ATC.debugLabel", "debugLabel", "Visible debug setting"),
                TranslationPolicyDecision.HardAllow,
                "keyed_text");
            AssertDecision(
                Candidate(TranslationPolicyBucket.Keyed, "ATC.Internal", "ATC.Internal", "SOME_INTERNAL_ID"),
                TranslationPolicyDecision.Ambiguous,
                "keyed_identifier_like");
            AssertDecision(
                Candidate(TranslationPolicyBucket.Keyed, "ATC.Icon", "ATC.Icon", "Textures/UI/Icon.png"),
                TranslationPolicyDecision.HardDeny,
                "path_or_resource_value");
        }

        private static void TestNativeTargetLocalFilter()
        {
            AssertTrue(!TranslationPolicyNativeTargetFilter.ShouldKeep(
                TranslationPolicyBucket.DefInjected,
                "ThingDef",
                "Example.debugLabel",
                "Internal trace"),
                "DefInjected debugLabel must be rejected by the native target filter");
            AssertTrue(TranslationPolicyNativeTargetFilter.ShouldKeep(
                TranslationPolicyBucket.DefInjected,
                "ThingDef",
                "Example.label",
                "Visible label"),
                "Known DefInjected label must be retained by the native target filter");
            AssertTrue(TranslationPolicyNativeTargetFilter.ShouldKeep(
                TranslationPolicyBucket.DefInjected,
                "ThingDef",
                "Example.customNarrative",
                "A readable sentence"),
                "Ambiguous DefInjected text must be retained without an Agent decision");
            AssertTrue(!TranslationPolicyNativeTargetFilter.ShouldKeep(
                TranslationPolicyBucket.DefInjected,
                "ThingDef",
                "Example.thingDef",
                "Steel"),
                "Hard-denied Def/reference fields must not be exposed as Workbench candidates");
            AssertTrue(!TranslationPolicyNativeTargetFilter.ShouldKeep(
                TranslationPolicyBucket.DefInjected,
                "EventDef",
                "Example.disabledReason",
                "BANANANOTENOUGH"),
                "Localization-key references must not be exposed as Workbench candidates");
            AssertTrue(TranslationPolicyNativeTargetFilter.ShouldKeep(
                TranslationPolicyBucket.Keyed,
                string.Empty,
                "ATC.debugLabel",
                "Visible debug setting"),
                "Keyed names must not be rejected by the DefInjected field blacklist");
        }

        private static void TestGrammarSafety()
        {
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "RuleDef.rulesStrings.0", "rulesStrings", "start->Hello colonist"),
                TranslationPolicyDecision.HardDeny,
                "protected_grammar_fragment");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "RuleDef.rulesStrings.1", "rulesStrings", "middle->A complete sentence"),
                TranslationPolicyDecision.HardDeny,
                "protected_grammar_fragment");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "RuleDef.rulesStrings.2", "rulesStrings", "end->Farewell colonist"),
                TranslationPolicyDecision.HardDeny,
                "protected_grammar_fragment");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "RuleDef.rulesStrings.3", "rulesStrings", "subject->Hello colonist"),
                TranslationPolicyDecision.HardAllow,
                "known_text_field");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "RuleDef.rulesStrings.4", "rulesStrings", "subject->[PAWN_nameDef]"),
                TranslationPolicyDecision.HardDeny,
                "protected_grammar_fragment");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "RuleDef.rulesStrings.5", "rulesStrings", "subject->OK"),
                TranslationPolicyDecision.HardDeny,
                "protected_grammar_fragment");
            AssertDecision(
                Candidate(TranslationPolicyBucket.DefInjected, "RuleDef.rulesStrings.6", "rulesStrings", "subject->SOME_ID"),
                TranslationPolicyDecision.Ambiguous,
                "grammar_rhs_identifier_like");
        }

        private static void TestRawXmlScanning()
        {
            const string defsXml =
                "<Defs>" +
                "<ThingDef><defName>AlphaDef</defName><label>Alpha</label><description>Alpha description</description>" +
                "<graphicData><texPath>Things/Alpha</texPath></graphicData>" +
                "<customNarrative>Alpha story</customNarrative>" +
                "<stages><li><label>Stage one</label></li><li><label>Stage two</label></li></stages>" +
                "<options><li>Friendly fire</li><li>Careful mode</li></options></ThingDef>" +
                "<ThingDef><defName>BetaDef</defName><customNarrative>Beta story</customNarrative></ThingDef>" +
                "</Defs>";
            TranslationPolicySourceContext context = Context("Defs/ThingDefs.xml");
            List<TranslationPolicyCandidate> defs = TranslationPolicyXmlScanner.ScanDefsXml(defsXml, context);

            AssertTrue(defs.Count == 11, "Raw Def scanner should emit every non-empty leaf, including denied leaves.");
            TranslationPolicyCandidate stage = defs.Single(item => item.KeyOrPath == "AlphaDef.stages.1.label");
            AssertEqual("label", stage.FieldName, "Nested list child field name");
            TranslationPolicyCandidate option = defs.Single(item => item.KeyOrPath == "AlphaDef.options.0");
            AssertEqual("options", option.FieldName, "Pure list item should inherit its parent field name");
            AssertDecision(
                defs.Single(item => item.KeyOrPath == "AlphaDef.graphicData.texPath"),
                TranslationPolicyDecision.HardDeny,
                "denied_field");

            const string keyedXml =
                "<LanguageData><ATC.Power>Power</ATC.Power><ATC.Help>Open settings</ATC.Help>" +
                "<ATC.Icon>Textures/UI/Icon.png</ATC.Icon></LanguageData>";
            List<TranslationPolicyCandidate> keyed = TranslationPolicyXmlScanner.ScanKeyedXml(keyedXml, Context("Languages/English/Keyed/Main.xml"));
            AssertTrue(keyed.Count == 3, "Keyed scanner candidate count");
            AssertDecision(keyed.Single(item => item.KeyOrPath == "ATC.Power"), TranslationPolicyDecision.HardAllow, "keyed_text");
            AssertDecision(keyed.Single(item => item.KeyOrPath == "ATC.Icon"), TranslationPolicyDecision.HardDeny, "path_or_resource_value");

            const string inheritedDefsXml =
                "<Defs><ThingDef Name='Human'><label>Human</label><description>Inherited description</description></ThingDef>" +
                "<ThingDef ParentName='Human'><defName>Dummy</defName></ThingDef></Defs>";
            List<TranslationPolicyCandidate> inherited = TranslationPolicyXmlScanner.ScanDefsXml(inheritedDefsXml, context);
            AssertEqual("Human", inherited.Single(item => item.KeyOrPath == "Dummy.label").SourceText, "Inherited Def label candidate");
            AssertEqual("Inherited description", inherited.Single(item => item.KeyOrPath == "Dummy.description").SourceText, "Inherited Def description candidate");

            const string multilineKeyedXml =
                "<LanguageData><Milira.Difficulty_CrazyDesc>First line of a long settings description.\n" +
                "Second line must remain available for translation.</Milira.Difficulty_CrazyDesc></LanguageData>";
            List<TranslationPolicyCandidate> multiline = TranslationPolicyXmlScanner.ScanKeyedXml(multilineKeyedXml, context);
            AssertTrue(multiline.Count == 1, "Long multiline Keyed value must not disappear");
            AssertTrue(multiline[0].SourceText.Contains("Second line"), "Multiline Keyed content must be preserved");
        }

        private static void TestDefInjectedXmlScanning()
        {
            const string xml =
                "<LanguageData>" +
                "<AlphaDef.label>Alpha label</AlphaDef.label>" +
                "<AlphaDef.customNarrative>Alpha story</AlphaDef.customNarrative>" +
                "<BetaDef.customNarrative>Beta story</BetaDef.customNarrative>" +
                "</LanguageData>";
            List<TranslationPolicyCandidate> candidates = TranslationPolicyXmlScanner.ScanDefInjectedXml(
                xml,
                "ThingDef",
                Context("Languages/English/DefInjected/ThingDef/ThingDefs.xml"));

            AssertTrue(candidates.Count == 3, "DefInjected scanner candidate count");
            AssertTrue(candidates.All(item => item.Bucket == TranslationPolicyBucket.DefInjected), "DefInjected bucket");
            AssertTrue(candidates.All(item => item.DefType == "ThingDef"), "DefInjected type");
            AssertEqual(
                "customNarrative",
                candidates.Single(item => item.KeyOrPath == "BetaDef.customNarrative").FieldName,
                "DefInjected terminal field");

            TranslationPolicyShadowResult result = TranslationPolicyShadowEngine.Run(new TranslationPolicyShadowInput
            {
                Candidates = candidates,
                Options = TestOptions()
            });
            TranslationPolicyGroup customGroup = result.AmbiguousGroups.Single();
            AssertTrue(customGroup.CandidateCount == 2, "Two DefInjected defNames should share one custom-field group");
            AssertEqual("$def.customnarrative", customGroup.NormalizedPath, "DefInjected group path");
        }

        private static void TestTranslationXmlNestedListFlattening()
        {
            const string xml =
                "<LanguageData>" +
                "<RuleDef.rulesStrings><li>subject-&gt;First rule</li><li>subject-&gt;Second\\nrule</li></RuleDef.rulesStrings>" +
                "<QuestScriptDef.root><li><label>First quest</label><description>First description</description></li>" +
                "<li><label>Second quest</label></li></QuestScriptDef.root>" +
                "<Flat.label>Flat value</Flat.label><Empty.label />" +
                "</LanguageData>";

            XmlDocument document = new XmlDocument();
            document.LoadXml(xml);
            Dictionary<string, string> parsed = TranslationXmlDictionaryParser.Parse(document.DocumentElement);

            AssertTrue(!parsed.ContainsKey("RuleDef.rulesStrings"), "Nested RulePack parent must not be emitted as aggregate text");
            AssertEqual("subject->First rule", parsed["RuleDef.rulesStrings.0"], "First RulePack list item");
            AssertEqual("subject->Second\nrule", parsed["RuleDef.rulesStrings.1"], "Second RulePack list item and escaped newline");
            AssertTrue(!parsed.ContainsKey("QuestScriptDef.root"), "Nested QuestScript parent must not be emitted as aggregate text");
            AssertEqual("First quest", parsed["QuestScriptDef.root.0.label"], "First nested QuestScript label");
            AssertEqual("First description", parsed["QuestScriptDef.root.0.description"], "First nested QuestScript description");
            AssertEqual("Second quest", parsed["QuestScriptDef.root.1.label"], "Second nested QuestScript label");
            AssertEqual("Flat value", parsed["Flat.label"], "Flat translation entries must remain unchanged");
            AssertEqual(string.Empty, parsed["Empty.label"], "Empty flat translation entries must remain present");
            AssertEqual(7, parsed.Count, "Only flattened leaves should be emitted");
        }

        private static void TestPathNormalizationAndGrouping()
        {
            AssertEqual(
                "thing.stages.[].options[].label",
                TranslationPolicyGrouping.NormalizeIndexedPath("Thing.stages.12.options[3].label"),
                "Indexed path normalization");

            List<TranslationPolicyCandidate> candidates = new List<TranslationPolicyCandidate>
            {
                Candidate(TranslationPolicyBucket.DefInjected, "AlphaDef.stages.0.customNarrative", "customNarrative", "First story"),
                Candidate(TranslationPolicyBucket.DefInjected, "BetaDef.stages.9.customNarrative", "customNarrative", "Second story")
            };
            TranslationPolicyShadowResult result = TranslationPolicyShadowEngine.Run(new TranslationPolicyShadowInput
            {
                Candidates = candidates,
                Options = TestOptions()
            });

            AssertTrue(result.AmbiguousGroups.Count == 1, "Different defNames and list indexes must group by schema path");
            AssertEqual("$def.stages.[].customnarrative", result.AmbiguousGroups[0].NormalizedPath, "Normalized group path");
            AssertTrue(result.AmbiguousGroups[0].CandidateCount == 2, "Grouped candidate count");

            TranslationPolicyCandidate otherPackage = Candidate(
                TranslationPolicyBucket.DefInjected,
                "GammaDef.stages.1.customNarrative",
                "customNarrative",
                "Third story",
                "other.mod");
            AssertTrue(
                TranslationPolicyGrouping.CreateGroupKey(candidates[0]) != TranslationPolicyGrouping.CreateGroupKey(otherPackage),
                "Package boundary must isolate groups");
        }

        private static void TestDeterministicBoundedResult()
        {
            List<TranslationPolicyCandidate> candidates = new List<TranslationPolicyCandidate>();
            for (int i = 0; i < 8; i++)
            {
                candidates.Add(Candidate(
                    TranslationPolicyBucket.DefInjected,
                    "Def" + i + ".options." + i + ".customNarrative",
                    "customNarrative",
                    "Story number " + i));
            }
            candidates.Add(Candidate(TranslationPolicyBucket.DefInjected, "Allowed.label", "label", "Visible"));
            candidates.Add(Candidate(TranslationPolicyBucket.DefInjected, "Denied.texPath", "texPath", "Things/Path"));

            TranslationPolicyShadowOptions options = TestOptions();
            options.MaxDiagnosticSamples = 3;
            TranslationPolicyShadowResult forward = TranslationPolicyShadowEngine.Run(new TranslationPolicyShadowInput
            {
                Candidates = candidates,
                Options = options
            });
            TranslationPolicyShadowResult reverse = TranslationPolicyShadowEngine.Run(new TranslationPolicyShadowInput
            {
                Candidates = candidates.AsEnumerable().Reverse().ToList(),
                Options = options
            });
            TranslationPolicyShadowResult normalizedMetadataReverse = TranslationPolicyShadowEngine.Run(new TranslationPolicyShadowInput
            {
                Candidates = candidates.Select(WithMetadataNoise).Reverse().ToList(),
                Options = options
            });

            AssertEqual(forward.DeterministicFingerprint, reverse.DeterministicFingerprint, "Input order-independent fingerprint");
            AssertEqual(forward.CorpusFingerprint, reverse.CorpusFingerprint, "Input order-independent corpus fingerprint");
            AssertEqual(
                forward.DeterministicFingerprint,
                normalizedMetadataReverse.DeterministicFingerprint,
                "Normalized metadata casing, whitespace, path separators, and input order must not change fingerprint");
            AssertTrue(forward.Summary.TotalCandidates == 10, "Streaming total count");
            AssertTrue(forward.DiagnosticSamples.Count == 3, "Diagnostic sample bound");
            AssertTrue(forward.AmbiguousGroups.Count == 1, "One schema group");
            AssertTrue(forward.AmbiguousGroups[0].Samples.Count == 5, "Per-group sample hard limit");
            AssertTrue(
                forward.AmbiguousGroups[0].Samples.Select(item => item.CandidateId)
                    .SequenceEqual(forward.AmbiguousGroups[0].Samples.Select(item => item.CandidateId).OrderBy(id => id, StringComparer.Ordinal)),
                "Samples must be sorted deterministically");
            AssertTrue(
                typeof(TranslationPolicyShadowResult).GetProperty("Candidates") == null,
                "Result must not retain every candidate row");

            TranslationPolicyShadowOptions hiddenOptions = TestOptions();
            hiddenOptions.MaxDiagnosticSamples = 0;
            List<TranslationPolicyCandidate> hiddenCandidates = new List<TranslationPolicyCandidate>
            {
                Candidate(TranslationPolicyBucket.DefInjected, "Allowed.label", "label", "Visible text"),
                Candidate(TranslationPolicyBucket.DefInjected, "Denied.texPath", "texPath", "Things/Original")
            };
            TranslationPolicyShadowResult hiddenBase = TranslationPolicyShadowEngine.Run(new TranslationPolicyShadowInput
            {
                Candidates = hiddenCandidates,
                Options = hiddenOptions
            });
            List<TranslationPolicyCandidate> changedAllow = new List<TranslationPolicyCandidate>
            {
                Candidate(TranslationPolicyBucket.DefInjected, "Allowed.label", "label", "Updated visible text"),
                hiddenCandidates[1]
            };
            TranslationPolicyShadowResult hiddenChanged = TranslationPolicyShadowEngine.Run(new TranslationPolicyShadowInput
            {
                Candidates = changedAllow,
                Options = hiddenOptions
            });
            AssertTrue(hiddenBase.DiagnosticSamples.Count == 0, "Hidden corpus fingerprint case must not use diagnostics");
            AssertTrue(hiddenBase.AmbiguousGroups.Count == 0, "Hard allow and deny candidates must not create reported groups");
            AssertTrue(
                hiddenBase.CorpusFingerprint != hiddenChanged.CorpusFingerprint,
                "Changing unreported hard-decision text must change the full corpus fingerprint");
            AssertTrue(
                hiddenBase.DeterministicFingerprint != hiddenChanged.DeterministicFingerprint,
                "Changing unreported hard-decision text must change the deterministic result fingerprint");
        }

        private static void TestEstimatorArithmetic()
        {
            List<TranslationPolicyGroup> groups = new List<TranslationPolicyGroup>();
            for (int i = 0; i < 41; i++)
            {
                groups.Add(new TranslationPolicyGroup
                {
                    GroupKey = "group-" + i.ToString("D2"),
                    PackageId = "test.mod",
                    DefType = "ThingDef",
                    NormalizedPath = "$def.field" + i,
                    FieldName = "field" + i,
                    CandidateCount = 1,
                    Samples = new List<TranslationPolicyGroupSample>
                    {
                        new TranslationPolicyGroupSample
                        {
                            CandidateId = "candidate-" + i,
                            SourceFile = "Defs/Test.xml",
                            KeyOrPath = "Def.field" + i,
                            SourceText = "Visible sample " + i
                        }
                    }
                });
            }

            TranslationPolicyShadowOptions options = TestOptions();
            options.GroupsPerRequest = 20;
            options.MaxConcurrency = 2;
            options.PromptTokenEstimate = 100;
            options.CharactersPerToken = 4d;
            options.OutputTokensPerGroup = 10;
            options.MaxRetriesPerRequest = 1;
            options.EstimatedMillisecondsPerRequest = 1000;
            TranslationPolicyTokenEstimate estimate = TranslationPolicyEstimator.Estimate(groups, options);

            AssertTrue(estimate.EstimatedRequestCount == 3, "41 groups at 20/request should use 3 requests");
            AssertTrue(estimate.EstimatedRequestWaves == 2, "3 requests at concurrency 2 should use 2 waves");
            AssertTrue(estimate.EstimatedOutputTokens == 410, "Output token arithmetic");
            AssertTrue(estimate.EstimatedInputTokens > 300, "Each request must include its prompt and payload");
            AssertTrue(estimate.EstimatedMaximumRequestCount == 6, "One retry allowance doubles requests");
            AssertTrue(estimate.EstimatedMaximumTotalTokens == estimate.EstimatedTotalTokens * 2, "Retry token ceiling");
            AssertTrue(estimate.EstimatedLatencyMilliseconds == 2000, "Base latency wave estimate");
            AssertTrue(estimate.EstimatedMaximumLatencyMilliseconds == 3000, "Retry latency wave estimate");

            options.GroupsPerRequest = 200;
            TranslationPolicyTokenEstimate clamped = TranslationPolicyEstimator.Estimate(groups, options);
            AssertTrue(clamped.GroupsPerRequest == 20, "Groups per request must never exceed 20");
        }

        private static void TestLimitsAndCompletionState()
        {
            TranslationPolicyShadowOptions options = TestOptions();
            options.MaxCandidates = 2;
            TranslationPolicyShadowSession session = new TranslationPolicyShadowSession(options);
            session.AddCandidate(Candidate(TranslationPolicyBucket.DefInjected, "Def.a", "a", "First sentence"));
            session.AddCandidate(Candidate(TranslationPolicyBucket.DefInjected, "Def.b", "b", "Second sentence"));
            AssertThrows<InvalidOperationException>(
                () => session.AddCandidate(Candidate(TranslationPolicyBucket.DefInjected, "Def.c", "c", "Third sentence")),
                "Candidate hard limit");

            TranslationPolicyShadowResult completed = session.Complete();
            AssertTrue(object.ReferenceEquals(completed, session.Complete()), "Complete should be idempotent");
            AssertThrows<InvalidOperationException>(
                () => session.AddCandidate(Candidate(TranslationPolicyBucket.DefInjected, "Def.d", "d", "Fourth sentence")),
                "Cannot add after Complete");

            TranslationPolicyShadowOptions groupOptions = TestOptions();
            groupOptions.MaxAmbiguousGroups = 1;
            TranslationPolicyShadowSession groupSession = new TranslationPolicyShadowSession(groupOptions);
            groupSession.AddCandidate(Candidate(TranslationPolicyBucket.DefInjected, "Def.firstField", "firstField", "First sentence"));
            AssertThrows<InvalidOperationException>(
                () => groupSession.AddCandidate(Candidate(TranslationPolicyBucket.DefInjected, "Def.secondField", "secondField", "Second sentence")),
                "Ambiguous group hard limit");

            TranslationPolicyShadowOptions reportOptions = TestOptions();
            reportOptions.MaxReportedAmbiguousGroups = 2;
            TranslationPolicyShadowResult bounded = TranslationPolicyShadowEngine.Run(new TranslationPolicyShadowInput
            {
                Options = reportOptions,
                Candidates = new List<TranslationPolicyCandidate>
                {
                    Candidate(TranslationPolicyBucket.DefInjected, "Def.firstField", "firstField", "First sentence"),
                    Candidate(TranslationPolicyBucket.DefInjected, "Def.secondField", "secondField", "Second sentence"),
                    Candidate(TranslationPolicyBucket.DefInjected, "Def.thirdField", "thirdField", "Third sentence")
                }
            });
            AssertTrue(bounded.Summary.AmbiguousGroupCount == 3, "All distinct groups must be counted");
            AssertTrue(bounded.Summary.ReportedAmbiguousGroupCount == 2, "Reported groups must be bounded");
            AssertTrue(bounded.Summary.GroupsTruncated, "Bounded report must disclose truncation");
            AssertTrue(bounded.Estimate.AmbiguousGroupCount == 3, "Estimator must include unreported groups");
            AssertTrue(bounded.Estimate.PayloadEstimateUsesReportedSample, "Sampled payload estimate must be disclosed");

            reportOptions.MaxReportedAmbiguousGroups = 1;
            List<TranslationPolicyCandidate> fingerprintCandidates = new List<TranslationPolicyCandidate>
            {
                Candidate(TranslationPolicyBucket.DefInjected, "Def.firstField", "firstField", "First sentence"),
                Candidate(TranslationPolicyBucket.DefInjected, "Def.secondField", "secondField", "Second sentence"),
                Candidate(TranslationPolicyBucket.DefInjected, "Def.thirdField", "thirdField", "Third sentence")
            };
            TranslationPolicyShadowResult fingerprintBase = TranslationPolicyShadowEngine.Run(new TranslationPolicyShadowInput
            {
                Options = reportOptions,
                Candidates = fingerprintCandidates
            });
            string reportedKey = fingerprintBase.AmbiguousGroups[0].GroupKey;
            TranslationPolicyCandidate unreported = fingerprintCandidates.First(
                candidate => TranslationPolicyGrouping.CreateGroupKey(candidate) != reportedKey);
            fingerprintCandidates.Add(Candidate(
                unreported.Bucket,
                unreported.KeyOrPath,
                unreported.FieldName,
                "Another value in the same unreported group"));
            TranslationPolicyShadowResult fingerprintChanged = TranslationPolicyShadowEngine.Run(new TranslationPolicyShadowInput
            {
                Options = reportOptions,
                Candidates = fingerprintCandidates
            });
            AssertTrue(
                fingerprintBase.DistinctGroupFingerprint != fingerprintChanged.DistinctGroupFingerprint,
                "Distinct group fingerprint must include counts for unreported groups");
        }

        private static void TestSecureXmlParsing()
        {
            const string dtdXml = "<!DOCTYPE foo [<!ENTITY xxe SYSTEM 'file:///does-not-read'>]><LanguageData><Key>&xxe;</Key></LanguageData>";
            AssertThrows<XmlException>(
                () => TranslationPolicyXmlScanner.ScanKeyedXml(dtdXml, Context("unsafe.xml")),
                "DTD must be prohibited");
        }

        private static void TestAboutDirectPackageIdentity()
        {
            XmlDocument document = new XmlDocument();
            document.LoadXml(
                "<ModMetaData><modDependencies><li><packageId>brrainz.harmony</packageId></li></modDependencies>" +
                "<packageId>brrainz.achtung</packageId><name>Achtung!</name></ModMetaData>");
            AssertEqual(
                "brrainz.achtung",
                FindDirectChild(document.DocumentElement, "packageId").InnerText,
                "Dependency packageId must not replace the mod's direct packageId");
            AssertEqual(
                "Achtung!",
                FindDirectChild(document.DocumentElement, "name").InnerText,
                "Direct mod name");
        }

        private static void TestAuditLoadFolderResolution()
        {
            string modRoot = CreateTemporaryDirectory();
            try
            {
                CreateDirectory(modRoot, "Legacy");
                CreateDirectory(modRoot, "Common");
                CreateDirectory(modRoot, "Cont");
                CreateDirectory(modRoot, "Content");
                CreateDirectory(modRoot, "Content/DLC/Odyssey");
                WriteTestFile(
                    modRoot,
                    "LoadFolders.xml",
                    "<loadFolders>" +
                    "<v1.5><li>Legacy</li></v1.5>" +
                    "<v1.6><li>Common</li><li>Cont</li><li>Content</li><li>Content/DLC/Odyssey</li>" +
                    "<li>/</li><li></li><li>COMMON</li><li>Content/</li><li>Missing</li></v1.6>" +
                    "</loadFolders>");

                List<string> roots = ResolveAuditContentRoots(modRoot);
                string[] expected =
                {
                    Path.GetFullPath(Path.Combine(modRoot, "Common")),
                    Path.GetFullPath(Path.Combine(modRoot, "Cont")),
                    Path.GetFullPath(Path.Combine(modRoot, "Content")),
                    Path.GetFullPath(Path.Combine(modRoot, "Content", "DLC", "Odyssey")),
                    Path.GetFullPath(modRoot)
                };
                AssertPathSequence(expected, roots, "LoadFolders roots must preserve declared 1.6 order");
                AssertTrue(
                    !roots.Any(path => path.Equals(Path.Combine(modRoot, "Legacy"), StringComparison.OrdinalIgnoreCase)),
                    "RimWorld 1.5 LoadFolders entries must be excluded from a 1.6 audit");

                string fallbackRoot = CreateDirectory(modRoot, "FallbackMod");
                CreateDirectory(fallbackRoot, "Legacy");
                WriteTestFile(
                    fallbackRoot,
                    "LoadFolders.xml",
                    "<loadFolders><v1.5><li>Legacy</li></v1.5><v1.6><li>Missing</li></v1.6></loadFolders>");
                AssertPathSequence(
                    new[] { Path.GetFullPath(fallbackRoot) },
                    ResolveAuditContentRoots(fallbackRoot),
                    "Mod root fallback when no applicable configured directory exists");
            }
            finally
            {
                DeleteTemporaryDirectory(modRoot);
            }
        }

        private static void TestAuditCustomRootsAndGeneralDefType()
        {
            string modRoot = CreateTemporaryDirectory();
            try
            {
                WriteTestFile(
                    modRoot,
                    "LoadFolders.xml",
                    "<loadFolders><v1.6><li>Cont</li><li>Content</li><li>Content/DLC/Odyssey</li></v1.6></loadFolders>");
                WriteTestFile(
                    modRoot,
                    "Cont/Languages/English/Keyed/Main.xml",
                    "<LanguageData><Audit.CustomRoot>Visible keyed text</Audit.CustomRoot></LanguageData>");
                WriteTestFile(
                    modRoot,
                    "Content/Languages/English/DefInjected/Root.xml",
                    "<LanguageData><AlphaDef.customNarrative>Root story</AlphaDef.customNarrative></LanguageData>");
                WriteTestFile(
                    modRoot,
                    "Content/Languages/English/DefInjected/ThingDef/Nested.xml",
                    "<LanguageData><BetaDef.customNarrative>Nested story</BetaDef.customNarrative></LanguageData>");
                WriteTestFile(
                    modRoot,
                    "Content/DLC/Odyssey/Defs/Odyssey.xml",
                    "<Defs><ThingDef><defName>OdysseyAuditDef</defName><label>Odyssey label</label></ThingDef></Defs>");

                TranslationPolicyShadowSession session = new TranslationPolicyShadowSession();
                AuditReport report = new AuditReport();
                ScanAuditMod(
                    new AuditModRoot
                    {
                        PackageId = "test.audit.customroots",
                        ModName = "Audit Custom Roots",
                        RootPath = modRoot
                    },
                    session,
                    report);
                TranslationPolicyShadowResult result = session.Complete();

                AssertTrue(report.ScannedFileCount == 4, "Every configured custom content root should be scanned once");
                AssertTrue(result.Summary.TotalCandidates == 5, "Custom-root Keyed, DefInjected, and raw Defs candidates");
                AssertTrue(
                    result.AmbiguousGroups.Any(group => group.DefType == "general"),
                    "Root-level DefInjected XML must use the General def type");
                AssertTrue(
                    result.AmbiguousGroups.Any(group => group.DefType == "thingdef"),
                    "Nested DefInjected XML must keep its first directory as the def type");
            }
            finally
            {
                DeleteTemporaryDirectory(modRoot);
            }
        }

        private static void TestAgentRunBudgetAtomicLimits()
        {
            TranslationPolicyAgentRunBudget clamped = new TranslationPolicyAgentRunBudget(
                int.MaxValue,
                long.MaxValue,
                int.MaxValue);
            TranslationPolicyAgentBudgetSnapshot clampedSnapshot = clamped.GetSnapshot();
            AssertEqual(20, clampedSnapshot.MaximumCalls, "Run call ceiling");
            AssertEqual(200000L, clampedSnapshot.MaximumEstimatedTokens, "Run token ceiling");
            AssertEqual(20, clampedSnapshot.MaximumCallsPerMod, "Per-Mod call ceiling");

            TranslationPolicyAgentRunBudget sequential = new TranslationPolicyAgentRunBudget(3, 100L, 2);
            AssertTrue(sequential.TryReserveAttempt(" Test.Mod ", 30L, false), "First Mod attempt");
            AssertTrue(sequential.TryReserveAttempt("test.mod", 30L, true), "Retry counts against the same Mod limit");
            AssertTrue(!sequential.TryReserveAttempt("TEST.MOD", 0L, true), "Per-Mod limit must reject atomically");
            AssertTrue(sequential.TryReserveAttempt("other.mod", 40L, false), "Remaining run token budget");
            AssertTrue(!sequential.TryReserveAttempt("third.mod", 0L, false), "Run call limit must reject");
            TranslationPolicyAgentBudgetSnapshot sequentialSnapshot = sequential.GetSnapshot();
            AssertEqual(3, sequentialSnapshot.CallsUsed, "Reserved call count");
            AssertEqual(1, sequentialSnapshot.RetryCallsUsed, "Reserved retry count");
            AssertEqual(100L, sequentialSnapshot.EstimatedTokensReserved, "Reserved token count");
            AssertEqual(
                2,
                sequential.GetSnapshot(" TEST.MOD ").CallsUsedForMod,
                "Per-Mod snapshot uses normalized package id");

            sequential.GrantUnlimited();
            AssertTrue(
                sequential.TryReserveAttempt("TEST.MOD", 500000L, true),
                "Explicit per-run consent must bypass call, token, and per-Mod soft limits");
            TranslationPolicyAgentBudgetSnapshot unlimitedSnapshot = sequential.GetSnapshot();
            AssertTrue(unlimitedSnapshot.UnlimitedGranted, "Unlimited consent snapshot");
            AssertEqual(4, unlimitedSnapshot.CallsUsed, "Post-consent call accounting");
            AssertEqual(500100L, unlimitedSnapshot.EstimatedTokensReserved, "Post-consent token accounting");

            TranslationPolicyAgentRunBudget emergencyTokenGuard =
                new TranslationPolicyAgentRunBudget(1, 1L, 1);
            emergencyTokenGuard.GrantUnlimited();
            AssertTrue(
                emergencyTokenGuard.TryReserveAttempt(
                    "emergency.tokens",
                    TranslationPolicyAgentRunBudget.EmergencyMaximumEstimatedTokens,
                    false),
                "Explicit consent may use the full emergency token allowance");
            AssertTrue(
                !emergencyTokenGuard.TryReserveAttempt("emergency.tokens", 1L, false),
                "Emergency token guard cannot be bypassed by consent");
            TranslationPolicyAgentBudgetSnapshot emergencyTokenSnapshot = emergencyTokenGuard.GetSnapshot();
            AssertTrue(emergencyTokenSnapshot.EmergencyLimitReached, "Emergency token guard snapshot");
            AssertTrue(emergencyTokenSnapshot.AgentDisabled, "Emergency token guard disables later Agent use");

            TranslationPolicyAgentRunBudget emergencyCallGuard =
                new TranslationPolicyAgentRunBudget(1, 1L, 1);
            emergencyCallGuard.GrantUnlimited();
            for (int index = 0; index < TranslationPolicyAgentRunBudget.EmergencyMaximumCalls; index++)
            {
                AssertTrue(
                    emergencyCallGuard.TryReserveAttempt("emergency.calls", 0L, false),
                    "Emergency call allowance");
            }
            AssertTrue(
                !emergencyCallGuard.TryReserveAttempt("emergency.calls", 0L, false),
                "Emergency call guard cannot be bypassed by consent");

            sequential.DisableAgent();
            AssertTrue(
                !sequential.TryReserveAttempt("other.mod", 0L, false),
                "Local-only choice must reject every later Agent attempt in the run");
            AssertTrue(sequential.GetSnapshot().AgentDisabled, "Local-only snapshot");

            TranslationPolicyAgentRunBudget disabledFirst =
                new TranslationPolicyAgentRunBudget(1, 1L, 1);
            disabledFirst.DisableAgent();
            disabledFirst.GrantUnlimited();
            AssertTrue(
                !disabledFirst.TryReserveAttempt("disabled.mod", 0L, false),
                "Granting consent must not re-enable an Agent explicitly disabled for this run");

            TranslationPolicyAgentRunBudget runBound = new TranslationPolicyAgentRunBudget(20, 200000L, 2);
            int runSuccesses = 0;
            Parallel.For(0, 200, index =>
            {
                if (runBound.TryReserveAttempt("run.mod." + index, 1L, false))
                    Interlocked.Increment(ref runSuccesses);
            });
            AssertEqual(20, runSuccesses, "Concurrent run limit successes");
            AssertEqual(20, runBound.GetSnapshot().CallsUsed, "Concurrent run limit snapshot");

            TranslationPolicyAgentRunBudget perModBound = new TranslationPolicyAgentRunBudget(20, 200000L, 2);
            int perModSuccesses = 0;
            Parallel.For(0, 100, index =>
            {
                if (perModBound.TryReserveAttempt("same.mod", 1L, false))
                    Interlocked.Increment(ref perModSuccesses);
            });
            AssertEqual(2, perModSuccesses, "Concurrent per-Mod limit successes");
            AssertEqual(2, perModBound.GetSnapshot().CallsUsed, "Concurrent per-Mod limit snapshot");

            TranslationPolicyAgentRunBudget tokenBound = new TranslationPolicyAgentRunBudget(20, 100L, 2);
            int tokenSuccesses = 0;
            Parallel.For(0, 100, index =>
            {
                if (tokenBound.TryReserveAttempt("token.mod." + index, 30L, true))
                    Interlocked.Increment(ref tokenSuccesses);
            });
            TranslationPolicyAgentBudgetSnapshot tokenSnapshot = tokenBound.GetSnapshot();
            AssertEqual(3, tokenSuccesses, "Concurrent token limit successes");
            AssertEqual(90L, tokenSnapshot.EstimatedTokensReserved, "Concurrent token reservation");
            AssertEqual(tokenSuccesses, tokenSnapshot.RetryCallsUsed, "Concurrent retry accounting");
        }

        private static void TestAgentBatchPlannerAcrossDefTypes()
        {
            List<TranslationPolicyAgentRequestGroup> groups = new List<TranslationPolicyAgentRequestGroup>();
            for (int i = 0; i < 41; i++)
            {
                groups.Add(new TranslationPolicyAgentRequestGroup
                {
                    Id = "group-" + i.ToString("D2"),
                    DefType = "DefType" + i.ToString("D2")
                });
            }

            List<List<TranslationPolicyAgentRequestGroup>> batches =
                TranslationPolicyAgentBatchPlanner.CreateBatches(groups);
            AssertEqual(3, batches.Count, "41 runtime groups must create three requests");
            AssertEqual(20, batches[0].Count, "First runtime request group count");
            AssertEqual(20, batches[1].Count, "Second runtime request group count");
            AssertEqual(1, batches[2].Count, "Final runtime request group count");
            AssertTrue(
                batches[0].Select(group => group.DefType).Distinct(StringComparer.Ordinal).Count() > 1,
                "First runtime request must mix DefTypes");
            AssertTrue(
                batches[1].Select(group => group.DefType).Distinct(StringComparer.Ordinal).Count() > 1,
                "Second runtime request must mix DefTypes");

            List<string> expectedIds = groups.Select(group => group.Id).ToList();
            List<string> actualIds = batches.SelectMany(batch => batch).Select(group => group.Id).ToList();
            AssertEqual(expectedIds.Count, actualIds.Count, "Runtime batch planner item count");
            AssertEqual(
                actualIds.Count,
                actualIds.Distinct(StringComparer.Ordinal).Count(),
                "Runtime batch planner must not duplicate groups");
            AssertTrue(
                expectedIds.SequenceEqual(actualIds, StringComparer.Ordinal),
                "Runtime batch planner must preserve group order without omissions");

            AssertEqual(
                0,
                TranslationPolicyAgentBatchPlanner
                    .CreateBatches<TranslationPolicyAgentRequestGroup>(null)
                    .Count,
                "Null runtime group input");
            AssertEqual(
                0,
                TranslationPolicyAgentBatchPlanner
                    .CreateBatches(new List<TranslationPolicyAgentRequestGroup>())
                    .Count,
                "Empty runtime group input");
        }

        private static void TestAgentIdentityAndCacheInvalidation()
        {
            TranslationPolicyCandidate first = Candidate(
                TranslationPolicyBucket.DefInjected,
                "AlphaDef.stages.0.customNarrative",
                "customNarrative",
                "First sample");
            TranslationPolicyCandidate second = Candidate(
                TranslationPolicyBucket.DefInjected,
                "BetaDef.stages.1.customNarrative",
                "customNarrative",
                "Second sample");

            string forward = TranslationPolicyIdentity.CreateGroupCorpusFingerprint(new[] { first, second });
            string reverse = TranslationPolicyIdentity.CreateGroupCorpusFingerprint(new[] { second, first });
            string duplicate = TranslationPolicyIdentity.CreateGroupCorpusFingerprint(new[] { first, second, first });
            AssertEqual(forward, reverse, "Group corpus fingerprint must be order-independent");
            AssertEqual(forward, duplicate, "Duplicate candidate IDs must not change group corpus fingerprint");

            TranslationPolicyCandidate changedSecond = Candidate(
                second.Bucket,
                second.KeyOrPath,
                second.FieldName,
                "Changed second sample");
            string changedCorpus = TranslationPolicyIdentity.CreateGroupCorpusFingerprint(new[] { first, changedSecond });
            AssertTrue(forward != changedCorpus, "Source text change must invalidate group corpus fingerprint");

            string groupKey = TranslationPolicyGrouping.CreateGroupKey(first);
            string cacheKey = TranslationPolicyIdentity.CreateAgentCacheKey(
                "policy-v1",
                "prompt-v1",
                "evaluator-v1",
                groupKey,
                forward);
            string normalizedCacheKey = TranslationPolicyIdentity.CreateAgentCacheKey(
                " POLICY-V1 ",
                " PROMPT-V1 ",
                " EVALUATOR-V1 ",
                " " + groupKey.ToUpperInvariant() + " ",
                " " + forward.ToUpperInvariant() + " ");
            AssertEqual(cacheKey, normalizedCacheKey, "Agent cache key metadata normalization");
            AssertTrue(
                cacheKey != TranslationPolicyIdentity.CreateAgentCacheKey(
                    "policy-v1", "prompt-v2", "evaluator-v1", groupKey, forward),
                "Prompt version must invalidate Agent cache key");
            AssertTrue(
                cacheKey != TranslationPolicyIdentity.CreateAgentCacheKey(
                    "policy-v1", "prompt-v1", "evaluator-v2", groupKey, forward),
                "Evaluator fingerprint must invalidate Agent cache key");
            AssertTrue(
                cacheKey != TranslationPolicyIdentity.CreateAgentCacheKey(
                    "policy-v1", "prompt-v1", "evaluator-v1", groupKey, changedCorpus),
                "Group corpus change must invalidate Agent cache key");

            string candidateCacheKey = TranslationPolicyIdentity.CreateAgentCandidateCacheKey(
                "policy-v1",
                "prompt-v1",
                "evaluator-v1",
                groupKey,
                first.CandidateId);
            AssertEqual(
                candidateCacheKey,
                TranslationPolicyIdentity.CreateAgentCandidateCacheKey(
                    " POLICY-V1 ",
                    " PROMPT-V1 ",
                    " EVALUATOR-V1 ",
                    " " + groupKey.ToUpperInvariant() + " ",
                    " " + first.CandidateId.ToUpperInvariant() + " "),
                "Candidate cache key metadata normalization");
            AssertTrue(
                candidateCacheKey != TranslationPolicyIdentity.CreateAgentCandidateCacheKey(
                    "policy-v1", "prompt-v1", "evaluator-v1", groupKey, changedSecond.CandidateId),
                "Candidate source change must invalidate only that candidate key");

            string candidateRequestId = TranslationPolicyIdentity.CreateAgentCandidateRequestId(
                groupKey,
                first.CandidateId);
            AssertEqual(
                candidateRequestId,
                TranslationPolicyIdentity.CreateAgentCandidateRequestId(
                    " " + groupKey.ToUpperInvariant() + " ",
                    " " + first.CandidateId.ToUpperInvariant() + " "),
                "Candidate request id metadata normalization");
            AssertTrue(
                candidateRequestId != TranslationPolicyIdentity.CreateAgentCandidateRequestId(
                    groupKey,
                    changedSecond.CandidateId),
                "Candidate request id must be candidate-specific");
        }

        private static void TestAgentCandidateCacheIncrementalPlanning()
        {
            TranslationPolicyCandidate first = Candidate(
                TranslationPolicyBucket.DefInjected,
                "AlphaDef.stages.0.customNarrative",
                "customNarrative",
                "First sample");
            TranslationPolicyCandidate second = Candidate(
                TranslationPolicyBucket.DefInjected,
                "BetaDef.stages.1.customNarrative",
                "customNarrative",
                "Second sample");
            TranslationPolicyCandidate third = Candidate(
                TranslationPolicyBucket.DefInjected,
                "GammaDef.stages.2.customNarrative",
                "customNarrative",
                "Third sample");
            string groupKey = TranslationPolicyGrouping.CreateGroupKey(first);
            AssertEqual(groupKey, TranslationPolicyGrouping.CreateGroupKey(second), "Second candidate group identity");
            AssertEqual(groupKey, TranslationPolicyGrouping.CreateGroupKey(third), "Third candidate group identity");

            Dictionary<string, TranslationPolicyAgentGroupDecision> cached =
                new Dictionary<string, TranslationPolicyAgentGroupDecision>(StringComparer.Ordinal);
            foreach (TranslationPolicyCandidate candidate in new[] { first, second })
            {
                string key = TranslationPolicyIdentity.CreateAgentCandidateCacheKey(
                    "policy-v1", "prompt-v1", "eval-v1", groupKey, candidate.CandidateId);
                cached[key] = new TranslationPolicyAgentGroupDecision
                {
                    Id = groupKey,
                    Decision = TranslationPolicyAgentDecision.Deny,
                    Reason = "structural field"
                };
            }

            TranslationPolicyAgentCandidateResolutionPlan expanded =
                TranslationPolicyAgentResolutionPlanner.CreateCandidatePlan(
                    new[] { first, second, third, first },
                    "policy-v1",
                    "prompt-v1",
                    "eval-v1",
                    (key, candidateId, expectedGroupKey) =>
                    {
                        TranslationPolicyAgentGroupDecision decision;
                        return cached.TryGetValue(key, out decision) ? decision : null;
                    });
            AssertEqual(2, expanded.CacheHitCandidateCount, "Unique candidate cache hit count");
            AssertEqual(1, expanded.MissGroups.Count, "Expanded group miss-group count");
            AssertEqual(groupKey, expanded.MissGroups[0].GroupKey, "Expanded miss group identity");
            AssertEqual(1, expanded.MissGroups[0].Candidates.Count, "Only the new candidate must be sent");
            AssertEqual(third.CandidateId, expanded.MissGroups[0].Candidates[0].CandidateId, "New candidate identity");
            AssertEqual(1, expanded.GetMissingCandidates().Count, "Expanded group outbound candidate count");

            List<TranslationPolicyAgentRequestScope> expandedScopes = expanded.CreateRequestScopes();
            AssertEqual(1, expandedScopes.Count, "Expanded request scope count");
            AssertTrue(expandedScopes[0].IsCandidateScoped, "Mixed cache group must use candidate-scoped request");
            AssertTrue(
                expandedScopes[0].RequestId != groupKey,
                "Candidate-scoped request id must not reuse semantic group id");
            AssertEqual(groupKey, expandedScopes[0].GroupKey, "Candidate-scoped semantic group identity");
            AssertEqual(1, expandedScopes[0].Candidates.Count, "Candidate-scoped request candidate count");
            AssertEqual(third.CandidateId, expandedScopes[0].Candidates[0].CandidateId, "Candidate-scoped request candidate");
            Dictionary<string, TranslationPolicyAgentGroupDecision> providerDecisions =
                new Dictionary<string, TranslationPolicyAgentGroupDecision>(StringComparer.Ordinal)
                {
                    {
                        expandedScopes[0].RequestId,
                        new TranslationPolicyAgentGroupDecision
                        {
                            Id = expandedScopes[0].RequestId,
                            Decision = TranslationPolicyAgentDecision.Allow,
                            Reason = "visible player text"
                        }
                    }
                };
            TranslationPolicyAgentGroupDecision mappedDecision;
            AssertTrue(
                TranslationPolicyAgentResponseMapper.TryMap(
                    groupKey,
                    expandedScopes[0].RequestId,
                    providerDecisions,
                    out mappedDecision),
                "Candidate-scoped provider response mapping");
            AssertEqual(groupKey, mappedDecision.Id, "Mapped decision restores semantic group id");
            AssertEqual(TranslationPolicyAgentDecision.Allow, mappedDecision.Decision, "Mapped candidate decision");
            providerDecisions[expandedScopes[0].RequestId].Id = groupKey;
            AssertTrue(
                !TranslationPolicyAgentResponseMapper.TryMap(
                    groupKey,
                    expandedScopes[0].RequestId,
                    providerDecisions,
                    out mappedDecision),
                "Provider response with a mismatched request id must be rejected");

            TranslationPolicyAgentCandidateResolutionPlan initial =
                TranslationPolicyAgentResolutionPlanner.CreateCandidatePlan(
                    new[] { first, second, third },
                    "policy-v1",
                    "prompt-v1",
                    "eval-v1",
                    (key, candidateId, expectedGroupKey) => null);
            List<TranslationPolicyAgentRequestScope> initialScopes = initial.CreateRequestScopes();
            AssertEqual(1, initialScopes.Count, "Initial request scope count");
            AssertTrue(!initialScopes[0].IsCandidateScoped, "Initial request keeps group-level batching");
            AssertEqual(groupKey, initialScopes[0].RequestId, "Initial request uses semantic group id");
            AssertEqual(3, initialScopes[0].Candidates.Count, "Initial request keeps complete group corpus");

            TranslationPolicyCandidate changedFirst = Candidate(
                first.Bucket,
                first.KeyOrPath,
                first.FieldName,
                "Changed first sample");
            TranslationPolicyAgentCandidateResolutionPlan changed =
                TranslationPolicyAgentResolutionPlanner.CreateCandidatePlan(
                    new[] { changedFirst, second },
                    "policy-v1",
                    "prompt-v1",
                    "eval-v1",
                    (key, candidateId, expectedGroupKey) =>
                    {
                        TranslationPolicyAgentGroupDecision decision;
                        return cached.TryGetValue(key, out decision) ? decision : null;
                    });
            AssertEqual(1, changed.CacheHitCandidateCount, "Unchanged peer candidate must remain cached");
            AssertEqual(second.CandidateId, changed.CachedCandidates[0].CandidateId, "Unchanged peer cache identity");
            AssertEqual(1, changed.GetMissingCandidates().Count, "Only changed source must miss");
            AssertEqual(changedFirst.CandidateId, changed.GetMissingCandidates()[0].CandidateId, "Changed source miss identity");
            List<TranslationPolicyAgentRequestScope> changedScopes = changed.CreateRequestScopes();
            AssertEqual(1, changedScopes.Count, "Changed candidate request scope count");
            AssertTrue(changedScopes[0].IsCandidateScoped, "Changed candidate request scope mode");
            AssertEqual(1, changedScopes[0].Candidates.Count, "Changed candidate request scope candidate count");
            AssertEqual(changedFirst.CandidateId, changedScopes[0].Candidates[0].CandidateId, "Changed candidate request scope candidate");
        }

        private static void TestAgentCandidateCacheScopeInvalidation()
        {
            TranslationPolicyCandidate candidate = Candidate(
                TranslationPolicyBucket.DefInjected,
                "AlphaDef.stages.0.customNarrative",
                "customNarrative",
                "Visible sample");
            string groupKey = TranslationPolicyGrouping.CreateGroupKey(candidate);
            string cacheKey = TranslationPolicyIdentity.CreateAgentCandidateCacheKey(
                "policy-v1", "prompt-v1", "eval-v1", groupKey, candidate.CandidateId);
            TranslationPolicyAgentGroupDecision cachedDecision = new TranslationPolicyAgentGroupDecision
            {
                Id = groupKey,
                Decision = TranslationPolicyAgentDecision.Allow,
                Reason = "visible player text"
            };
            Func<string, string, string, TranslationPolicyAgentGroupDecision> lookup =
                (key, candidateId, expectedGroupKey) =>
                    string.Equals(key, cacheKey, StringComparison.Ordinal) ? cachedDecision : null;

            TranslationPolicyAgentCandidateResolutionPlan baseline =
                TranslationPolicyAgentResolutionPlanner.CreateCandidatePlan(
                    new[] { candidate }, "policy-v1", "prompt-v1", "eval-v1", lookup);
            AssertEqual(1, baseline.CacheHitCandidateCount, "Baseline candidate cache hit");

            TranslationPolicyAgentCandidateResolutionPlan policyChanged =
                TranslationPolicyAgentResolutionPlanner.CreateCandidatePlan(
                    new[] { candidate }, "policy-v2", "prompt-v1", "eval-v1", lookup);
            AssertEqual(0, policyChanged.CacheHitCandidateCount, "Policy version change must miss");
            AssertEqual(1, policyChanged.GetMissingCandidates().Count, "Policy version changed outbound count");

            TranslationPolicyAgentCandidateResolutionPlan promptChanged =
                TranslationPolicyAgentResolutionPlanner.CreateCandidatePlan(
                    new[] { candidate }, "policy-v1", "prompt-v2", "eval-v1", lookup);
            AssertEqual(0, promptChanged.CacheHitCandidateCount, "Prompt version change must miss");

            TranslationPolicyAgentCandidateResolutionPlan evaluatorChanged =
                TranslationPolicyAgentResolutionPlanner.CreateCandidatePlan(
                    new[] { candidate }, "policy-v1", "prompt-v1", "eval-v2", lookup);
            AssertEqual(0, evaluatorChanged.CacheHitCandidateCount, "Evaluator change must miss");

            TranslationPolicyAgentGroupDecision wrongGroupDecision = new TranslationPolicyAgentGroupDecision
            {
                Id = "wrong-group",
                Decision = TranslationPolicyAgentDecision.Allow,
                Reason = "must not be reused"
            };
            TranslationPolicyAgentCandidateResolutionPlan wrongGroup =
                TranslationPolicyAgentResolutionPlanner.CreateCandidatePlan(
                    new[] { candidate },
                    "policy-v1",
                    "prompt-v1",
                    "eval-v1",
                    (key, candidateId, expectedGroupKey) => wrongGroupDecision);
            AssertEqual(0, wrongGroup.CacheHitCandidateCount, "Mismatched group decision must miss");
        }

        private static void TestAgentResponseParserAcceptance()
        {
            const string response =
                "[{\"id\":\"group-c\",\"decision\":\"review\",\"reason\":\"  needs author context  \"}," +
                "{\"id\":\"group-a\",\"decision\":\"allow\",\"reason\":\"visible player text\"}," +
                "{\"id\":\"group-b\",\"decision\":\"deny\",\"reason\":\"runtime identifier\"}]";
            List<TranslationPolicyAgentGroupDecision> decisions;
            AssertTrue(
                TranslationPolicyAgentResponseParser.TryParse(
                    response,
                    new[] { "group-b", "group-c", "group-a" },
                    out decisions),
                "Complete allow/deny/review response must parse");
            AssertEqual(3, decisions.Count, "Parsed Agent decision count");
            AssertEqual("group-a", decisions[0].Id, "Parsed decisions sort by ID");
            AssertEqual(TranslationPolicyAgentDecision.Allow, decisions[0].Decision, "Allow decision");
            AssertEqual(TranslationPolicyAgentDecision.Deny, decisions[1].Decision, "Deny decision");
            AssertEqual(TranslationPolicyAgentDecision.Review, decisions[2].Decision, "Review decision");
            AssertEqual("needs author context", decisions[2].Reason, "Reason trimming");

            List<TranslationPolicyAgentGroupDecision> fencedDecisions;
            AssertTrue(
                TranslationPolicyAgentResponseParser.TryParse(
                    "```json\n" + response + "\n```",
                    new[] { "group-b", "group-c", "group-a" },
                    out fencedDecisions),
                "Code-fenced JSON response must parse after normalization");
            AssertEqual(3, fencedDecisions.Count, "Code-fenced decision count");
        }

        private static void TestAgentResponseParserRejection()
        {
            const string validSingle =
                "[{\"id\":\"group-a\",\"decision\":\"allow\",\"reason\":\"visible text\"}]";
            AssertAgentResponseRejected(
                validSingle,
                new[] { "group-a", "group-b" },
                "Missing expected decision");
            AssertAgentResponseRejected(
                "[{\"id\":\"group-a\",\"decision\":\"allow\"}]",
                new[] { "group-a" },
                "Missing property");
            AssertAgentResponseRejected(
                "[{\"id\":\"group-a\",\"decision\":\"allow\",\"reason\":\"one\"}," +
                "{\"id\":\"group-a\",\"decision\":\"deny\",\"reason\":\"two\"}]",
                new[] { "group-a", "group-b" },
                "Duplicate ID");
            AssertAgentResponseRejected(
                "[{\"id\":\"unknown\",\"decision\":\"allow\",\"reason\":\"visible text\"}]",
                new[] { "group-a" },
                "Unknown ID");
            AssertAgentResponseRejected(
                "[{\"id\":\"group-a\",\"decision\":\"allow\",\"reason\":\"visible text\",\"confidence\":1}]",
                new[] { "group-a" },
                "Extra property");
            AssertAgentResponseRejected(
                "[{\"id\":\"group-a\",\"id\":\"group-a\",\"decision\":\"allow\",\"reason\":\"visible text\"}]",
                new[] { "group-a" },
                "Duplicate property");
            AssertAgentResponseRejected(
                "[{\"id\":\"group-a\",\"decision\":\"Allow\",\"reason\":\"visible text\"}]",
                new[] { "group-a" },
                "Decision casing");
            AssertAgentResponseRejected(
                "[{\"id\":\"group-a\",\"decision\":\"allow\",\"reason\":\"   \"}]",
                new[] { "group-a" },
                "Empty reason");
            AssertAgentResponseRejected(
                "[{\"id\":\"group-a\",\"decision\":\"allow\",\"reason\":\"" +
                new string('x', 241) + "\"}]",
                new[] { "group-a" },
                "Overlong reason");
        }

        private static void TestAgentTokenEstimator()
        {
            AssertEqual(
                105L,
                TranslationPolicyAgentTokenEstimator.EstimateAttemptTokens("1234", "123456", 100),
                "Input estimate plus maximum output token cap");
            AssertEqual(
                5L,
                TranslationPolicyAgentTokenEstimator.EstimateAttemptTokens("1234", "123456", -1),
                "Negative output cap clamps to zero");
            AssertEqual(
                64L,
                TranslationPolicyAgentTokenEstimator.EstimateAttemptTokens(null, null, 64),
                "Output-only estimate");
            AssertEqual(
                (long)int.MaxValue + 1L,
                TranslationPolicyAgentTokenEstimator.EstimateAttemptTokens("ab", null, int.MaxValue),
                "Output cap arithmetic must not overflow Int32");

            MethodInfo saturatingAdd = typeof(TranslationPolicyAgentTokenEstimator).GetMethod(
                "SaturatingAdd",
                BindingFlags.NonPublic | BindingFlags.Static);
            AssertTrue(saturatingAdd != null, "Agent token estimator saturation helper");
            long saturated = (long)saturatingAdd.Invoke(
                null,
                new object[] { long.MaxValue - 2L, 10L });
            AssertEqual(long.MaxValue, saturated, "Agent token estimate must saturate instead of overflowing");
        }

        private static void TestAgentDecisionCacheLifecycle()
        {
            string root = CreateTemporaryDirectory();
            try
            {
                string cachePath = Path.Combine(root, "agent-cache.json");
                string allowKey = TranslationPolicyIdentity.CreateAgentCacheKey(
                    "policy-v1", "prompt-v1", "eval-v1", "group-allow", "corpus-allow");
                string reviewKey = TranslationPolicyIdentity.CreateAgentCacheKey(
                    "policy-v1", "prompt-v1", "eval-v1", "group-review", "corpus-review");
                const string candidateId = "candidate-allow";
                string candidateKey = TranslationPolicyIdentity.CreateAgentCandidateCacheKey(
                    "policy-v1", "prompt-v1", "eval-v1", "group-allow", candidateId);
                TranslationPolicyAgentDecisionCache cache = new TranslationPolicyAgentDecisionCache(cachePath);
                cache.PutRange(
                    new[]
                    {
                        AgentCacheEntry(
                            allowKey,
                            "group-allow",
                            "corpus-allow",
                            "Allow",
                            "visible player text"),
                        AgentCacheEntry(
                            reviewKey,
                            "group-review",
                            "corpus-review",
                            "Review",
                            "needs author review"),
                        AgentCacheEntry(
                            "invalid-key",
                            "group-invalid",
                            "corpus-invalid",
                            "Unresolved",
                            "must not persist")
                    },
                    new[]
                    {
                        AgentCandidateCacheEntry(
                            candidateKey,
                            candidateId,
                            "group-allow",
                            "Allow",
                            "visible player text"),
                        AgentCandidateCacheEntry(
                            "invalid-candidate-key",
                            "candidate-invalid",
                            "group-invalid",
                            "Unresolved",
                            "must not persist")
                    });

                AssertTrue(File.Exists(cachePath), "Decision cache write");
                AssertTrue(!File.Exists(cachePath + ".tmp"), "Atomic cache write must not leave a temporary file");
                using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(cachePath)))
                {
                    AssertEqual(2, document.RootElement.GetProperty("Version").GetInt32(), "Candidate cache file version");
                    AssertTrue(
                        document.RootElement.TryGetProperty("CandidateEntries", out JsonElement candidateEntries),
                        "Candidate cache file section");
                    AssertEqual(1, candidateEntries.EnumerateObject().Count(), "Only valid candidate entries persist");
                }
                TranslationPolicyAgentGroupDecision decision;
                AssertTrue(cache.TryGet(allowKey, out decision), "In-memory decision cache hit");
                AssertEqual(TranslationPolicyAgentDecision.Allow, decision.Decision, "Cached allow decision");
                AssertEqual("group-allow", decision.Id, "Cached group ID");
                AssertTrue(cache.TryGet(reviewKey, out decision), "Cached review hit");
                AssertEqual(TranslationPolicyAgentDecision.Review, decision.Decision, "Cached review decision");
                AssertTrue(!cache.TryGet("invalid-key", out decision), "Invalid decision must not enter cache");
                AssertTrue(
                    cache.TryGetCandidate(candidateKey, candidateId, "group-allow", out decision),
                    "In-memory candidate decision cache hit");
                AssertEqual(TranslationPolicyAgentDecision.Allow, decision.Decision, "Cached candidate allow decision");
                AssertTrue(
                    !cache.TryGetCandidate(candidateKey, "wrong-candidate", "group-allow", out decision),
                    "Candidate identity mismatch must miss");
                AssertTrue(
                    !cache.TryGetCandidate(candidateKey, candidateId, "wrong-group", out decision),
                    "Candidate group mismatch must miss");
                cache.Flush();

                TranslationPolicyAgentDecisionCache reloaded = new TranslationPolicyAgentDecisionCache(cachePath);
                AssertTrue(reloaded.TryGet(allowKey, out decision), "Decision cache disk roundtrip");
                AssertEqual("visible player text", decision.Reason, "Cached reason roundtrip");
                AssertTrue(
                    reloaded.TryGetCandidate(candidateKey, candidateId, "group-allow", out decision),
                    "Candidate decision cache disk roundtrip");
                AssertEqual("visible player text", decision.Reason, "Candidate cached reason roundtrip");
                string changedPromptKey = TranslationPolicyIdentity.CreateAgentCacheKey(
                    "policy-v1", "prompt-v2", "eval-v1", "group-allow", "corpus-allow");
                AssertTrue(!reloaded.TryGet(changedPromptKey, out decision), "Changed cache-key version must miss");
                string changedCandidatePromptKey = TranslationPolicyIdentity.CreateAgentCandidateCacheKey(
                    "policy-v1", "prompt-v2", "eval-v1", "group-allow", candidateId);
                AssertTrue(
                    !reloaded.TryGetCandidate(
                        changedCandidatePromptKey,
                        candidateId,
                        "group-allow",
                        out decision),
                    "Changed candidate prompt version must miss");

                string deferredPath = Path.Combine(root, "deferred-cache.json");
                TranslationPolicyAgentDecisionCache deferred =
                    new TranslationPolicyAgentDecisionCache(deferredPath);
                deferred.PutCandidateRangeDeferred(new[]
                {
                    AgentCandidateCacheEntry(
                        candidateKey,
                        candidateId,
                        "group-allow",
                        "Allow",
                        "visible player text")
                });
                AssertTrue(!File.Exists(deferredPath), "Deferred candidate promotion must not rewrite cache immediately");
                deferred.Flush();
                AssertTrue(File.Exists(deferredPath), "Deferred candidate promotion persists on flush");
                TranslationPolicyAgentDecisionCache deferredReloaded =
                    new TranslationPolicyAgentDecisionCache(deferredPath);
                AssertTrue(
                    deferredReloaded.TryGetCandidate(candidateKey, candidateId, "group-allow", out decision),
                    "Deferred candidate promotion disk roundtrip");

                string recoveredPath = Path.Combine(root, "recovered-cache.json");
                File.Copy(cachePath, recoveredPath + ".tmp");
                TranslationPolicyAgentDecisionCache recovered =
                    new TranslationPolicyAgentDecisionCache(recoveredPath);
                AssertTrue(recovered.TryGet(allowKey, out decision), "Orphaned temporary cache must recover");
                AssertTrue(File.Exists(recoveredPath), "Recovered cache must restore active path");
                AssertTrue(!File.Exists(recoveredPath + ".tmp"), "Recovered cache must consume temporary file");

                string legacyPath = Path.Combine(root, "legacy-cache.json");
                string legacyKey = TranslationPolicyIdentity.CreateAgentCacheKey(
                    "policy-v1", "prompt-v1", "eval-v1", "legacy-group", "legacy-corpus");
                File.WriteAllText(
                    legacyPath,
                    "{\"Version\":1,\"Entries\":{\"" + legacyKey + "\":{\"CacheKey\":\"" + legacyKey +
                    "\",\"GroupKey\":\"legacy-group\",\"GroupCorpusFingerprint\":\"legacy-corpus\",\"Decision\":\"Deny\",\"Reason\":\"legacy structural field\",\"PolicyVersion\":\"policy-v1\",\"PromptVersion\":\"prompt-v1\",\"EvaluatorFingerprint\":\"eval-v1\"}}}");
                TranslationPolicyAgentDecisionCache legacy = new TranslationPolicyAgentDecisionCache(legacyPath);
                AssertTrue(legacy.TryGet(legacyKey, out decision), "Version 1 group cache must remain readable");
                AssertEqual(TranslationPolicyAgentDecision.Deny, decision.Decision, "Version 1 cached decision");
                legacy.Flush();
                using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(legacyPath)))
                {
                    AssertEqual(2, document.RootElement.GetProperty("Version").GetInt32(), "Version 1 cache migration");
                    AssertTrue(
                        document.RootElement.TryGetProperty("CandidateEntries", out JsonElement candidateEntries),
                        "Migrated cache candidate section");
                    AssertEqual(0, candidateEntries.EnumerateObject().Count(), "Migration must not invent candidate identities");
                }

                string versionPath = Path.Combine(root, "version-cache.json");
                File.WriteAllText(
                    versionPath,
                    "{\"Version\":99,\"Entries\":{\"version-key\":{\"CacheKey\":\"version-key\",\"GroupKey\":\"group\",\"Decision\":\"Allow\",\"Reason\":\"future format\"}}}");
                TranslationPolicyAgentDecisionCache wrongVersion = new TranslationPolicyAgentDecisionCache(versionPath);
                AssertTrue(!wrongVersion.TryGet("version-key", out decision), "Unsupported cache file version must be ignored");
                string unsupportedBefore = File.ReadAllText(versionPath);
                wrongVersion.PutRange(new[]
                {
                    AgentCacheEntry(
                        allowKey,
                        "group-allow",
                        "corpus-allow",
                        "Allow",
                        "must not overwrite newer cache")
                });
                AssertEqual(unsupportedBefore, File.ReadAllText(versionPath), "Unsupported cache file must not be overwritten");

                string metadataPath = Path.Combine(root, "metadata-cache.json");
                File.WriteAllText(
                    metadataPath,
                    "{\"Version\":1,\"Entries\":{\"" + allowKey + "\":{\"CacheKey\":\"" + allowKey +
                    "\",\"GroupKey\":\"group-allow\",\"GroupCorpusFingerprint\":\"corpus-allow\",\"Decision\":\"Allow\",\"Reason\":\"metadata mismatch\",\"PolicyVersion\":\"policy-v1\",\"PromptVersion\":\"wrong-prompt\",\"EvaluatorFingerprint\":\"eval-v1\"}}}");
                TranslationPolicyAgentDecisionCache invalidMetadata =
                    new TranslationPolicyAgentDecisionCache(metadataPath);
                AssertTrue(!invalidMetadata.TryGet(allowKey, out decision), "Cache metadata mismatch must miss");

                string invalidPath = Path.Combine(root, "invalid-cache.json");
                File.WriteAllText(
                    invalidPath,
                    "{\"Version\":1,\"Entries\":{\"bad-key\":{\"CacheKey\":\"bad-key\",\"GroupKey\":\"group\",\"Decision\":\"Unresolved\"}}}");
                TranslationPolicyAgentDecisionCache invalidDecision = new TranslationPolicyAgentDecisionCache(invalidPath);
                AssertTrue(!invalidDecision.TryGet("bad-key", out decision), "Invalid persisted decision must be ignored");

                string invalidReasonPath = Path.Combine(root, "invalid-reason-cache.json");
                File.WriteAllText(
                    invalidReasonPath,
                    "{\"Version\":1,\"Entries\":{\"bad-reason\":{\"CacheKey\":\"bad-reason\",\"GroupKey\":\"group\",\"Decision\":\"Allow\",\"Reason\":\"\"}}}");
                TranslationPolicyAgentDecisionCache invalidReason = new TranslationPolicyAgentDecisionCache(invalidReasonPath);
                AssertTrue(!invalidReason.TryGet("bad-reason", out decision), "Empty cached reason must be ignored");

                string malformedPath = Path.Combine(root, "malformed-cache.json");
                File.WriteAllText(malformedPath, "{not-json");
                TranslationPolicyAgentDecisionCache malformed = new TranslationPolicyAgentDecisionCache(malformedPath);
                AssertTrue(!malformed.TryGet("missing", out decision), "Malformed cache must behave as empty");
                AssertTrue(!File.Exists(malformedPath), "Malformed cache must be moved out of the active path");
                AssertEqual(
                    1,
                    Directory.GetFiles(root, "malformed-cache.json.broken-*.bak").Length,
                    "Malformed cache quarantine file");

                File.WriteAllText(cachePath + ".tmp", "stale");
                reloaded.Clear();
                AssertTrue(!File.Exists(cachePath), "Cache clear removes persisted cache");
                AssertTrue(!File.Exists(cachePath + ".tmp"), "Cache clear removes stale temporary cache");
                AssertTrue(!reloaded.TryGet(allowKey, out decision), "Cleared cache must miss");
            }
            finally
            {
                DeleteTemporaryDirectory(root);
            }
        }

        private static void TestAgentApplicationFallbackAndTimeout()
        {
            AssertEqual(
                TranslationPolicyApplicationDecision.Translate,
                TranslationPolicyApplication.Resolve(
                    TranslationPolicyDecision.HardAllow,
                    TranslationPolicyAgentDecision.Unresolved,
                    false),
                "Hard allow must translate new work");
            AssertEqual(
                TranslationPolicyApplicationDecision.KeepExisting,
                TranslationPolicyApplication.Resolve(
                    TranslationPolicyDecision.HardAllow,
                    TranslationPolicyAgentDecision.Unresolved,
                    true),
                "Hard allow must keep an existing translation");
            AssertEqual(
                TranslationPolicyApplicationDecision.Remove,
                TranslationPolicyApplication.Resolve(
                    TranslationPolicyDecision.HardDeny,
                    TranslationPolicyAgentDecision.Allow,
                    true),
                "Hard deny must remove an existing translation");
            AssertEqual(
                TranslationPolicyApplicationDecision.Remove,
                TranslationPolicyApplication.Resolve(
                    TranslationPolicyDecision.Ambiguous,
                    TranslationPolicyAgentDecision.Deny,
                    false),
                "Agent deny must reject new work");
            AssertEqual(
                TranslationPolicyApplicationDecision.Translate,
                TranslationPolicyApplication.Resolve(
                    TranslationPolicyDecision.Ambiguous,
                    TranslationPolicyAgentDecision.Allow,
                    false),
                "Agent allow must translate new work");
            AssertEqual(
                TranslationPolicyApplicationDecision.KeepExisting,
                TranslationPolicyApplication.Resolve(
                    TranslationPolicyDecision.Ambiguous,
                    TranslationPolicyAgentDecision.Review,
                    true),
                "Agent review must preserve an existing translation");
            AssertEqual(
                TranslationPolicyApplicationDecision.Remove,
                TranslationPolicyApplication.Resolve(
                    TranslationPolicyDecision.Ambiguous,
                    TranslationPolicyAgentDecision.Review,
                    false),
                "Agent review must fail closed for new work");
            AssertEqual(
                TranslationPolicyApplicationDecision.KeepExisting,
                TranslationPolicyApplication.Resolve(
                    TranslationPolicyDecision.Ambiguous,
                    TranslationPolicyAgentDecision.Unresolved,
                    true),
                "Unresolved Agent result must preserve an existing translation");

            AssertEqual(90, TranslationPolicyAgentTimeout.Resolve(60, 90), "Normal provider timeout floor");
            AssertEqual(300, TranslationPolicyAgentTimeout.Resolve(60, 300), "Reasoning provider timeout floor");
            AssertEqual(120, TranslationPolicyAgentTimeout.Resolve(120, 90), "Configured timeout above provider floor");
            AssertEqual(60, TranslationPolicyAgentTimeout.Resolve(0, 0), "Default timeout bound");
            AssertEqual(15, TranslationPolicyAgentTimeout.Resolve(1, 0), "Minimum timeout bound");
            AssertEqual(300, TranslationPolicyAgentTimeout.Resolve(999, 0), "Maximum timeout bound");
        }

        private static void TestAgentOutcomeReportingBoundaries()
        {
            TranslationPolicyAgentCandidateOutcome review = new TranslationPolicyAgentCandidateOutcome
            {
                Decision = TranslationPolicyAgentDecision.Review,
                Status = TranslationPolicyAgentOutcomeStatus.Classified,
                Reason = "ambiguous"
            };
            AssertTrue(review.ShouldReportUnresolved(false), "New Agent review must be reportable");
            AssertTrue(!review.ShouldReportUnresolved(true), "Existing Agent review must stay out of retry");

            TranslationPolicyAgentCandidateOutcome providerFailure = new TranslationPolicyAgentCandidateOutcome
            {
                Decision = TranslationPolicyAgentDecision.Unresolved,
                Status = TranslationPolicyAgentOutcomeStatus.ProviderFailure,
                ErrorCode = "http_503"
            };
            AssertTrue(providerFailure.ShouldReportUnresolved(false), "Provider failure must be reportable");
            AssertTrue(!providerFailure.ShouldReportUnresolved(true), "Existing provider failure must stay out of retry");

            TranslationPolicyAgentOutcomeStatus[] suppressedStatuses =
            {
                TranslationPolicyAgentOutcomeStatus.LocalOnly,
                TranslationPolicyAgentOutcomeStatus.NoProvider,
                TranslationPolicyAgentOutcomeStatus.Cancelled,
                TranslationPolicyAgentOutcomeStatus.SafetyLimit,
                TranslationPolicyAgentOutcomeStatus.BudgetLimit,
                TranslationPolicyAgentOutcomeStatus.NotAttempted
            };
            foreach (TranslationPolicyAgentOutcomeStatus status in suppressedStatuses)
            {
                TranslationPolicyAgentCandidateOutcome suppressed = new TranslationPolicyAgentCandidateOutcome
                {
                    Decision = TranslationPolicyAgentDecision.Unresolved,
                    Status = status
                };
                AssertTrue(!suppressed.ShouldReportUnresolved(false), status + " must not flood unresolved entries");
            }

            TranslationPolicyAgentCandidateOutcome deny = new TranslationPolicyAgentCandidateOutcome
            {
                Decision = TranslationPolicyAgentDecision.Deny,
                Status = TranslationPolicyAgentOutcomeStatus.Classified
            };
            AssertTrue(!deny.ShouldReportUnresolved(false), "Agent deny must not be reportable");
            AssertEqual(
                1,
                TranslationPolicyAgentUsageSummary.CountProviderFailures(new[]
                {
                    providerFailure,
                    review,
                    deny,
                    new TranslationPolicyAgentCandidateOutcome
                    {
                        Decision = TranslationPolicyAgentDecision.Unresolved,
                        Status = TranslationPolicyAgentOutcomeStatus.LocalOnly
                    },
                    new TranslationPolicyAgentCandidateOutcome
                    {
                        Decision = TranslationPolicyAgentDecision.Unresolved,
                        Status = TranslationPolicyAgentOutcomeStatus.Cancelled
                    }
                }),
                "Run summary unresolved count must include only provider failures");

            long actualTokens;
            AssertTrue(
                !TranslationPolicyAgentUsageSummary.TryGetActualTokens(false, 582776L, out actualTokens),
                "Estimated-only runs must not emit an actual-token summary");
            AssertTrue(
                TranslationPolicyAgentUsageSummary.TryGetActualTokens(true, 18342L, out actualTokens),
                "Provider usage must emit an actual-token summary");
            AssertEqual(18342L, actualTokens, "Provider-reported token value");
        }

        private static int RunActiveAudit(string[] args)
        {
            Stopwatch auditTimer = Stopwatch.StartNew();
            if (args.Length < 3 || args.Length > 5)
            {
                Console.Error.WriteLine(
                    "Usage: --audit-active <ModsConfig.xml> <workshopRoot> [gameContentRoot] [outputJson]");
                return 2;
            }

            string modsConfigPath = Path.GetFullPath(args[1]);
            string workshopRoot = Path.GetFullPath(args[2]);
            string gameContentRoot = args.Length >= 4 && !string.IsNullOrWhiteSpace(args[3])
                ? Path.GetFullPath(args[3])
                : string.Empty;
            string outputJson = args.Length >= 5 && !string.IsNullOrWhiteSpace(args[4])
                ? Path.GetFullPath(args[4])
                : string.Empty;

            if (!File.Exists(modsConfigPath)) throw new FileNotFoundException("ModsConfig.xml not found.", modsConfigPath);
            if (!Directory.Exists(workshopRoot)) throw new DirectoryNotFoundException(workshopRoot);
            if (gameContentRoot.Length > 0 && !Directory.Exists(gameContentRoot))
            {
                throw new DirectoryNotFoundException(gameContentRoot);
            }

            List<string> activePackageIds = ReadActivePackageIds(modsConfigPath);
            Dictionary<string, AuditModRoot> roots = new Dictionary<string, AuditModRoot>(StringComparer.OrdinalIgnoreCase);
            DiscoverModRoots(workshopRoot, roots, false);
            if (gameContentRoot.Length > 0) DiscoverModRoots(gameContentRoot, roots, true);

            AuditReport report = new AuditReport
            {
                ModsConfigPath = NormalizePath(modsConfigPath),
                WorkshopRoot = NormalizePath(workshopRoot),
                GameContentRoot = NormalizePath(gameContentRoot),
                ActivePackageIds = activePackageIds,
                DiscoveredModCount = roots.Count
            };
            TranslationPolicyShadowSession session = new TranslationPolicyShadowSession();

            int processedActiveMods = 0;
            foreach (string packageId in activePackageIds)
            {
                processedActiveMods++;
                if (processedActiveMods % 25 == 0)
                {
                    Console.Error.WriteLine(
                        "Audit progress: " + processedActiveMods + "/" + activePackageIds.Count +
                        " active mods, " + report.ScannedFileCount + " XML files");
                }
                AuditModRoot mod;
                if (!roots.TryGetValue(packageId, out mod))
                {
                    report.MissingPackageIds.Add(packageId);
                    continue;
                }

                report.ScannedModCount++;
                ScanAuditMod(mod, session, report);
            }

            report.MissingPackageIds.Sort(StringComparer.OrdinalIgnoreCase);
            report.Errors = report.Errors
                .OrderBy(error => error.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(error => error.Error, StringComparer.Ordinal)
                .ToList();
            report.Result = session.Complete();
            auditTimer.Stop();
            report.GeneratedUtc = DateTime.UtcNow.ToString("O");
            report.ElapsedMilliseconds = auditTimer.ElapsedMilliseconds;
            report.ActualAiApiCalls = 0;
            report.ActualConsumedTokens = 0L;
            report.TranslationWrites = 0;
            report.RuntimeInjections = 0;

            JsonSerializerOptions jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            jsonOptions.Converters.Add(new JsonStringEnumConverter());
            string json = JsonSerializer.Serialize(report, jsonOptions);
            if (outputJson.Length == 0)
            {
                Console.WriteLine(json);
            }
            else
            {
                string parent = Path.GetDirectoryName(outputJson);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(outputJson, json);
                Console.WriteLine("Audit JSON: " + outputJson);
            }

            return 0;
        }

        private static void ScanAuditMod(
            AuditModRoot mod,
            TranslationPolicyShadowSession session,
            AuditReport report)
        {
            HashSet<string> scannedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> acceptedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> contentRoots = ResolveAuditContentRoots(mod.RootPath);

            foreach (string contentRoot in contentRoots)
            {
                ScanAuditDirectory(
                    Path.Combine(contentRoot, "Languages", "English", "Keyed"),
                    mod,
                    scannedFiles,
                    acceptedCandidates,
                    session,
                    report,
                    (xml, context, file) => TranslationPolicyXmlScanner.ScanKeyedXml(xml, context));
            }

            foreach (string contentRoot in contentRoots)
            {
                ScanDefInjectedDirectory(
                    Path.Combine(contentRoot, "Languages", "English", "DefInjected"),
                    mod,
                    scannedFiles,
                    acceptedCandidates,
                    session,
                    report);
            }

            foreach (string contentRoot in contentRoots)
            {
                ScanAuditDirectory(
                    Path.Combine(contentRoot, "Defs"),
                    mod,
                    scannedFiles,
                    acceptedCandidates,
                    session,
                    report,
                    (xml, context, file) => TranslationPolicyXmlScanner.ScanDefsXml(xml, context));
            }
        }

        private static void ScanDefInjectedDirectory(
            string directory,
            AuditModRoot mod,
            HashSet<string> scannedFiles,
            HashSet<string> acceptedCandidates,
            TranslationPolicyShadowSession session,
            AuditReport report)
        {
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string fullPath = Path.GetFullPath(file);
                if (!scannedFiles.Add(fullPath)) continue;
                string relative = Path.GetRelativePath(directory, fullPath).Replace('\\', '/');
                string[] parts = relative.Split('/');
                string defType = parts.Length > 1 ? parts[0] : "General";
                ScanAuditFile(
                    file,
                    mod,
                    acceptedCandidates,
                    session,
                    report,
                    (xml, context) => TranslationPolicyXmlScanner.ScanDefInjectedXml(xml, defType, context));
            }
        }

        private static List<string> ResolveAuditContentRoots(string modRoot)
        {
            string fullModRoot = NormalizeAuditRootPath(modRoot);
            List<string> roots = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string loadFoldersPath = Path.Combine(fullModRoot, "LoadFolders.xml");

            if (File.Exists(loadFoldersPath))
            {
                try
                {
                    XmlDocument document = LoadSimpleXml(loadFoldersPath);
                    XmlNode loadFolders = document.DocumentElement;
                    if (loadFolders != null && loadFolders.LocalName.Equals("loadFolders", StringComparison.Ordinal))
                    {
                        foreach (string version in new[] { "v1.6", "1.6" })
                        {
                            XmlNode versionNode = FindDirectChildExact(loadFolders, version);
                            if (versionNode == null) continue;

                            foreach (XmlNode node in versionNode.ChildNodes)
                            {
                                if (node.NodeType != XmlNodeType.Element ||
                                    !node.LocalName.Equals("li", StringComparison.Ordinal))
                                {
                                    continue;
                                }

                                string relative = (node.InnerText ?? string.Empty).Trim();
                                string resolved = relative.Length == 0 || relative == "/" || relative == "\\"
                                    ? fullModRoot
                                    : NormalizeAuditRootPath(Path.Combine(
                                        fullModRoot,
                                        relative.Replace('/', Path.DirectorySeparatorChar)
                                            .Replace('\\', Path.DirectorySeparatorChar)));
                                if (Directory.Exists(resolved) && seen.Add(resolved)) roots.Add(resolved);
                            }

                            if (roots.Count > 0) break;
                        }
                    }
                }
                catch (Exception ex) when (ex is XmlException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    roots.Clear();
                    seen.Clear();
                }
            }

            if (roots.Count == 0) roots.Add(fullModRoot);
            return roots;
        }

        private static string NormalizeAuditRootPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string pathRoot = Path.GetPathRoot(fullPath);
            return pathRoot != null && fullPath.Length > pathRoot.Length
                ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : fullPath;
        }

        private static void ScanAuditDirectory(
            string directory,
            AuditModRoot mod,
            HashSet<string> scannedFiles,
            HashSet<string> acceptedCandidates,
            TranslationPolicyShadowSession session,
            AuditReport report,
            Func<string, TranslationPolicySourceContext, string, List<TranslationPolicyCandidate>> scanner)
        {
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string fullPath = Path.GetFullPath(file);
                if (!scannedFiles.Add(fullPath)) continue;
                ScanAuditFile(file, mod, acceptedCandidates, session, report, (xml, context) => scanner(xml, context, file));
            }
        }

        private static void ScanAuditFile(
            string file,
            AuditModRoot mod,
            HashSet<string> acceptedCandidates,
            TranslationPolicyShadowSession session,
            AuditReport report,
            Func<string, TranslationPolicySourceContext, List<TranslationPolicyCandidate>> scanner)
        {
            try
            {
                TranslationPolicySourceContext context = new TranslationPolicySourceContext
                {
                    PackageId = mod.PackageId,
                    ModName = mod.ModName,
                    SourceFile = Path.GetRelativePath(mod.RootPath, file).Replace('\\', '/'),
                    SchemaFingerprint = string.Empty
                };
                List<TranslationPolicyCandidate> scanned = scanner(File.ReadAllText(file), context);
                List<TranslationPolicyCandidate> accepted = new List<TranslationPolicyCandidate>();
                foreach (TranslationPolicyCandidate candidate in scanned)
                {
                    string stableKey = ((int)candidate.Bucket) + "|" +
                                       (candidate.DefType ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                                       (candidate.KeyOrPath ?? string.Empty).Trim().ToLowerInvariant();
                    if (acceptedCandidates.Add(stableKey)) accepted.Add(candidate);
                }

                session.AddCandidates(accepted);
                report.ScannedFileCount++;
            }
            catch (Exception ex) when (ex is XmlException || ex is IOException || ex is UnauthorizedAccessException)
            {
                report.ScanErrorCount++;
                if (report.Errors.Count < 100)
                {
                    report.Errors.Add(new AuditError
                    {
                        Source = NormalizePath(file),
                        Error = ex.GetType().Name + ": " + ex.Message
                    });
                }
            }
        }

        private static List<string> ReadActivePackageIds(string modsConfigPath)
        {
            XmlDocument document = LoadSimpleXml(modsConfigPath);
            XmlNodeList nodes = document.SelectNodes("//*[local-name()='activeMods']/*[local-name()='li']");
            return nodes.Cast<XmlNode>()
                .Select(node => (node.InnerText ?? string.Empty).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void DiscoverModRoots(
            string parentRoot,
            Dictionary<string, AuditModRoot> roots,
            bool overwrite)
        {
            foreach (string aboutPath in Directory.EnumerateFiles(parentRoot, "About.xml", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(Path.GetDirectoryName(path)).Equals("About", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                DirectoryInfo aboutDirectory = Directory.GetParent(aboutPath);
                if (aboutDirectory == null || aboutDirectory.Parent == null) continue;
                string root = aboutDirectory.Parent.FullName;

                try
                {
                    XmlDocument document = LoadSimpleXml(aboutPath);
                    XmlNode packageNode = FindDirectChild(document.DocumentElement, "packageId");
                    if (packageNode == null || string.IsNullOrWhiteSpace(packageNode.InnerText)) continue;
                    string packageId = packageNode.InnerText.Trim();
                    if (!overwrite && roots.ContainsKey(packageId)) continue;
                    XmlNode nameNode = FindDirectChild(document.DocumentElement, "name");
                    roots[packageId] = new AuditModRoot
                    {
                        PackageId = packageId,
                        ModName = nameNode == null ? packageId : nameNode.InnerText.Trim(),
                        RootPath = Path.GetFullPath(root)
                    };
                }
                catch
                {
                    // Individual malformed manifests are reported as missing if active.
                }
            }
        }

        private static XmlNode FindDirectChild(XmlNode parent, string name)
        {
            if (parent == null) return null;
            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element &&
                    child.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }

            return null;
        }

        private static XmlNode FindDirectChildExact(XmlNode parent, string name)
        {
            if (parent == null) return null;
            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element &&
                    child.LocalName.Equals(name, StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }

        private static XmlDocument LoadSimpleXml(string path)
        {
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            XmlDocument document = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, settings))
            {
                document.Load(reader);
            }

            return document;
        }

        private static void AssertAgentResponseRejected(
            string raw,
            IEnumerable<string> expectedIds,
            string message)
        {
            List<TranslationPolicyAgentGroupDecision> decisions;
            AssertTrue(
                !TranslationPolicyAgentResponseParser.TryParse(raw, expectedIds, out decisions),
                message + " must be rejected");
            AssertTrue(decisions == null, message + " must not return partial decisions");
        }

        private static TranslationPolicyAgentDecisionCache.CacheEntry AgentCacheEntry(
            string cacheKey,
            string groupKey,
            string groupCorpusFingerprint,
            string decision,
            string reason)
        {
            return new TranslationPolicyAgentDecisionCache.CacheEntry
            {
                CacheKey = cacheKey,
                GroupKey = groupKey,
                GroupCorpusFingerprint = groupCorpusFingerprint,
                PackageId = "test.mod",
                Decision = decision,
                Reason = reason,
                PolicyVersion = "policy-v1",
                PromptVersion = "prompt-v1",
                EvaluatorFingerprint = "eval-v1"
            };
        }

        private static TranslationPolicyAgentDecisionCache.CandidateCacheEntry AgentCandidateCacheEntry(
            string cacheKey,
            string candidateId,
            string groupKey,
            string decision,
            string reason)
        {
            return new TranslationPolicyAgentDecisionCache.CandidateCacheEntry
            {
                CacheKey = cacheKey,
                CandidateId = candidateId,
                GroupKey = groupKey,
                PackageId = "test.mod",
                Decision = decision,
                Reason = reason,
                PolicyVersion = "policy-v1",
                PromptVersion = "prompt-v1",
                EvaluatorFingerprint = "eval-v1"
            };
        }

        private static TranslationPolicyCandidate Candidate(
            TranslationPolicyBucket bucket,
            string path,
            string field,
            string text,
            string packageId = "test.mod",
            string defType = "ThingDef")
        {
            TranslationPolicyCandidate candidate = new TranslationPolicyCandidate
            {
                PackageId = packageId,
                ModName = "Test Mod",
                SourceFile = bucket == TranslationPolicyBucket.Keyed ? "Languages/English/Keyed/Test.xml" : "Defs/Test.xml",
                Bucket = bucket,
                DefType = bucket == TranslationPolicyBucket.DefInjected ? defType : string.Empty,
                KeyOrPath = path,
                FieldName = field,
                SourceText = text,
                DeclaringAssembly = "TestAssembly",
                SchemaFingerprint = "schema-v1"
            };
            candidate.CandidateId = TranslationPolicyIdentity.CreateCandidateId(candidate);
            return candidate;
        }

        private static TranslationPolicyCandidate WithMetadataNoise(TranslationPolicyCandidate source)
        {
            TranslationPolicyCandidate candidate = new TranslationPolicyCandidate
            {
                PackageId = " " + (source.PackageId ?? string.Empty).ToUpperInvariant() + " ",
                ModName = source.ModName,
                SourceFile = " " + (source.SourceFile ?? string.Empty).Replace('/', '\\').ToUpperInvariant() + " ",
                Bucket = source.Bucket,
                DefType = " " + (source.DefType ?? string.Empty).ToUpperInvariant() + " ",
                KeyOrPath = " " + (source.KeyOrPath ?? string.Empty).ToUpperInvariant() + " ",
                FieldName = " " + (source.FieldName ?? string.Empty).ToUpperInvariant() + " ",
                SourceText = source.SourceText,
                DeclaringAssembly = " " + (source.DeclaringAssembly ?? string.Empty).ToUpperInvariant() + " ",
                SchemaFingerprint = " " + (source.SchemaFingerprint ?? string.Empty).ToUpperInvariant() + " "
            };
            candidate.CandidateId = TranslationPolicyIdentity.CreateCandidateId(candidate);
            return candidate;
        }

        private static string CreateTemporaryDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "atc-policy-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string CreateDirectory(string root, string relativePath)
        {
            string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void WriteTestFile(string root, string relativePath, string contents)
        {
            string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllText(path, contents);
        }

        private static void DeleteTemporaryDirectory(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        private static void AssertPathSequence(
            IEnumerable<string> expected,
            IEnumerable<string> actual,
            string message)
        {
            List<string> expectedPaths = expected.Select(Path.GetFullPath).ToList();
            List<string> actualPaths = actual.Select(Path.GetFullPath).ToList();
            bool matches = expectedPaths.Count == actualPaths.Count;
            for (int i = 0; matches && i < expectedPaths.Count; i++)
            {
                matches = expectedPaths[i].Equals(actualPaths[i], StringComparison.OrdinalIgnoreCase);
            }

            if (!matches)
            {
                throw new InvalidOperationException(
                    message + ": expected <" + string.Join(", ", expectedPaths) +
                    ">, actual <" + string.Join(", ", actualPaths) + ">");
            }
        }

        private static TranslationPolicySourceContext Context(string sourceFile)
        {
            return new TranslationPolicySourceContext
            {
                PackageId = "test.mod",
                ModName = "Test Mod",
                SourceFile = sourceFile,
                DeclaringAssembly = "TestAssembly",
                SchemaFingerprint = "schema-v1"
            };
        }

        private static TranslationPolicyShadowOptions TestOptions()
        {
            return new TranslationPolicyShadowOptions
            {
                MaxSamplesPerGroup = 5,
                GroupsPerRequest = 20,
                MaxConcurrency = 3,
                PromptTokenEstimate = 1000,
                CharactersPerToken = 3d,
                OutputTokensPerGroup = 32,
                MaxRetriesPerRequest = 1,
                EstimatedMillisecondsPerRequest = 1000,
                MaxCandidates = 10000,
                MaxAmbiguousGroups = 1000,
                MaxReportedAmbiguousGroups = 1000,
                MaxDiagnosticSamples = 100
            };
        }

        private static void AssertDecision(
            TranslationPolicyCandidate candidate,
            TranslationPolicyDecision expectedDecision,
            string expectedReason)
        {
            TranslationPolicyClassification actual = TranslationPolicyClassifier.Classify(candidate);
            AssertEqual(expectedDecision, actual.Decision, "Decision for " + candidate.KeyOrPath);
            AssertEqual(expectedReason, actual.ReasonCode, "Reason for " + candidate.KeyOrPath);
        }

        private static void RunTest(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS: " + name);
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    message + ": expected <" + expected + ">, actual <" + actual + ">");
            }
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message + ": expected " + typeof(TException).Name);
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
        }
    }
}
