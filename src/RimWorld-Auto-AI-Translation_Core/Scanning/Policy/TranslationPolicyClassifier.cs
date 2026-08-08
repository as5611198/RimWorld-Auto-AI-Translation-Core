using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core.TranslationPolicy
{
    public static class TranslationPolicyClassifier
    {
        private static readonly HashSet<string> KnownTextFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "label", "description", "jobString", "reportString", "text", "labelShort", "customLabel",
            "descriptionShort", "pawnLabel", "gerund", "verb", "deathMessage", "inspectString",
            "baseInspectString", "helpText", "letterLabel", "letterText", "message", "messageSuccess",
            "messageFailed", "rejectInputMessage", "skillLabel", "endMessage", "beginLetterLabel",
            "beginLetter", "recoveryMessage", "destroyedLabel", "pawnSingular", "pawnPlural", "pawnsPlural",
            "leaderTitle", "adjective", "royalFavorLabel", "arrivalText", "arrivalTextEnemy",
            "logRulesInitiator", "logRulesRecipient", "useLabel", "ingestCommandString",
            "ingestReportString", "meatLabel", "corpseLabel", "discoverLetterTitle", "discoverLetterText",
            "letterLabelEnemy", "letterTextEnemy", "commandLabel", "commandDescription", "formatString",
            "outfitName", "labelNoun", "labelNounPretty", "customSummary", "summary", "title", "titleShort",
            "titleFemale", "titleShortFemale", "titleMale", "titleShortMale", "subtitle", "theme", "member",
            "ideoName", "successMessage", "successMessageNoNegativeThought", "failureMessage", "failMessage",
            "labelMale", "labelFemale", "labelPlural", "labelMalePlural", "labelFemalePlural",
            "customDescription", "journalText",
            "warningMessage", "tooltip", "explanation", "caption", "labelShortAdj", "baseInspectLine",
            "inspectLine", "fuelLabel", "fuelGizmoLabel", "permanentLabel", "destroyedOutLabel",
            "customLetterLabel", "customLetterText", "extraTooltip", "disabledReason",
            "confirmationDialogText", "invalidTargetMessage", "cannotUseMessage", "targetingLabel",
            "targetLabel", "gizmoLabel", "gizmoDescription", "settingsLabel", "settingsDescription",
            "settingsTooltip", "rulesStrings", "thoughtStageDescriptions"
        };

        private static readonly HashSet<string> DeniedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "alienRace", "texPath", "graphicPath", "soundDef", "effecter", "iconPath", "shader",
            "soundCast", "soundCastTail", "soundInteract", "soundHitPawn", "soundMiss", "soundMeleeHit",
            "soundMeleeMiss", "soundAmbience", "linkSound", "fleckDef", "thingDef", "itemDef",
            "pawnKindDef", "hediffDef", "recipeDef", "researchProjectDef", "terrainDef", "traitDef",
            "skillDef", "damageDef", "weaponDef", "apparelDef", "projectileDef", "defName", "debugLabel", "dollName",
            "dollPartName", "methodName", "class", "worker", "eyeTexPath", "browTexPath", "lidTexPath",
            "lashTexPath", "mouthTexPath", "noseTexPath", "earTexPath", "hairTexPath", "headTexPath",
            "bodyTexPath", "skinTexPath", "eyeballTexPath", "irisTexPath", "pupilTexPath",
            "expressionPath", "animationPath", "facialDef", "texture", "texturePath", "path", "maskPath",
            "headGraphicPath", "bodyGraphicPath", "crownGraphicPath", "frontTexPath", "sideTexPath",
            "backTexPath", "bodyGraphicData", "headGraphicData", "graphicData", "bodyAddon", "bodyAddons",
            "headAddons", "bodyPart", "skinColorChannel", "hairColorChannel", "channelName",
            "linkedBodyPartsGroup", "renderNodeProperties", "shaderType", "subPath", "targetJobs",
            "animationFrames", "faceAnimationDef", "browOffset", "lidOffset", "headOffset", "mouthOffset",
            "noseOffset", "earOffset", "eyeballOffset", "eyeballOffsetL", "eyeballOffsetR", "layerOffset",
            "angle", "scale", "drawSize", "offset", "offsets", "li_ref", "parent", "parentName",
            "abstract", "inherit", "compClass", "thingClass", "race", "category", "categories",
            "tradeTags", "weaponTags", "apparelTags", "tags", "linkFlags", "renderNodeTagDef", "tagDef"
        };

        // These fields hold enum values or Def/reference identifiers. Their values can look like ordinary
        // English words (for example Building, North, or Crafting), so a value-only heuristic is unsafe.
        private static readonly HashSet<string> StructuralReferenceFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "altitudeLayer", "capacity", "capacities", "category", "categories", "currency", "direction",
            "foodType", "faction", "factionDef", "gender", "hairGender", "intelligence", "joyKind", "kind",
            "linkableFacilities", "mode", "passability", "priority", "quality", "qualityCategory", "recipeUsers",
            "researchPrerequisite", "researchPrerequisites", "researchProject", "researchProjects", "rotation",
            "slot", "spawnLocType", "style", "tag", "tags", "techLevel", "thingCategories", "thingCategory",
            "workSkill", "workType", "workgiver", "workGiver", "type", "targetMode", "targetType", "valueType"
        };

        private static readonly HashSet<string> ProtectedPathSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "targetjobs", "animationframes", "alienrace", "alienpartgenerator", "bodyaddons",
            "headaddons", "colorchannels", "bodytypes", "headtypes", "bodytype", "headtype",
            "bodydef", "bodypartlabel", "bodygraphicdata", "headgraphicdata", "lifestagegraphics",
            "graphicpaths", "graphicdata", "customdraw", "drawsize", "offsets", "texpath",
            "graphicpath", "facial", "expression", "animation"
        };

        private static readonly Regex NumericOrPunctuationRegex = new Regex(
            @"^[\d\s\-\+\.,:%\(\)]+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex NumericTupleRegex = new Regex(
            @"^\(?\s*[+-]?\d+(?:\.\d+)?(?:\s*,\s*[+-]?\d+(?:\.\d+)?){1,3}\s*\)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex NumericRangeRegex = new Regex(
            @"^[+-]?\d+(?:\.\d+)?\s*~\s*[+-]?\d+(?:\.\d+)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex BooleanLiteralRegex = new Regex(
            @"^(true|false)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex FilePathRegex = new Regex(
            @"\.(png|jpg|jpeg|wav|mp3|ogg|xml|txt|lua|tex|dds)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex UriRegex = new Regex(
            @"^[a-z][a-z0-9+.-]*://\S+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex StructuredIdentifierRegex = new Regex(
            @"^[A-Za-z][A-Za-z0-9_.:-]*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex LocalizationKeyReferenceRegex = new Regex(
            @"^[A-Z][A-Z0-9_]{5,}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ProtectedGrammarTokenRegex = new Regex(
            @"(<[^>]+>|\{[^{}\r\n]+\}|\[[^\[\]\r\n]+\]|\$[A-Za-z0-9_]+|%[A-Za-z])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex GrammarEnglishSignalRegex = new Regex(
            @"(?<!\p{L})(the|and|for|with|from|this|that|your|you|not|can|will|when|while|after|before|into|has|have|are|was|were|is|of|to|in|on|a|an)(?!\p{L})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static TranslationPolicyClassification Classify(TranslationPolicyCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));

            string candidateId = string.IsNullOrWhiteSpace(candidate.CandidateId)
                ? TranslationPolicyIdentity.CreateCandidateId(candidate)
                : candidate.CandidateId;
            string text = (candidate.SourceText ?? string.Empty).Trim();
            string field = (candidate.FieldName ?? string.Empty).Trim();
            string path = (candidate.KeyOrPath ?? string.Empty).Trim();

            if (text.Length == 0)
            {
                return Result(candidateId, TranslationPolicyDecision.HardDeny, "empty_text");
            }

            if (candidate.Bucket == TranslationPolicyBucket.DefInjected && IsProtectedDefType(candidate.DefType))
            {
                return Result(candidateId, TranslationPolicyDecision.HardDeny, "protected_def_type");
            }

            if (candidate.Bucket == TranslationPolicyBucket.DefInjected && IsDeniedField(field))
            {
                return Result(candidateId, TranslationPolicyDecision.HardDeny, "denied_field");
            }

            if (candidate.Bucket == TranslationPolicyBucket.DefInjected &&
                IsProtectedPath(TranslationPolicyGrouping.NormalizeForGrouping(candidate)))
            {
                return Result(candidateId, TranslationPolicyDecision.HardDeny, "protected_path");
            }

            if (BooleanLiteralRegex.IsMatch(text))
            {
                return Result(candidateId, TranslationPolicyDecision.HardDeny, "structured_boolean_value");
            }

            if (NumericOrPunctuationRegex.IsMatch(text) || NumericTupleRegex.IsMatch(text))
            {
                return Result(candidateId, TranslationPolicyDecision.HardDeny, "structured_numeric_value");
            }

            if (NumericRangeRegex.IsMatch(text))
            {
                return Result(candidateId, TranslationPolicyDecision.HardDeny, "structured_numeric_range");
            }

            if (LooksLikeStrongPathOrResource(text))
            {
                return Result(candidateId, TranslationPolicyDecision.HardDeny, "path_or_resource_value");
            }

            if (IsClearlyUntranslatableGrammarFragment(text))
            {
                return Result(candidateId, TranslationPolicyDecision.HardDeny, "protected_grammar_fragment");
            }

            if (candidate.Bucket == TranslationPolicyBucket.Keyed)
            {
                if (LooksLikeStrongIdentifier(text))
                {
                    return Result(candidateId, TranslationPolicyDecision.Ambiguous, "keyed_identifier_like");
                }

                return Result(candidateId, TranslationPolicyDecision.HardAllow, "keyed_text");
            }

            if (IsGrammarIdentifierLike(text))
            {
                return Result(candidateId, TranslationPolicyDecision.Ambiguous, "grammar_rhs_identifier_like");
            }

            if (candidate.Bucket == TranslationPolicyBucket.DefInjected &&
                IsStructuralReferenceField(field) &&
                LooksLikeEnumOrReferenceValue(text))
            {
                return Result(candidateId, TranslationPolicyDecision.HardDeny, "enum_or_reference_value");
            }

            if (candidate.Bucket == TranslationPolicyBucket.DefInjected &&
                (KnownTextFields.Contains(field) || IsKnownTextListPath(path)) &&
                LooksLikeLocalizationKeyReference(text))
            {
                return Result(candidateId, TranslationPolicyDecision.HardDeny, "localization_key_reference");
            }

            if (KnownTextFields.Contains(field) || IsKnownTextListPath(path))
            {
                return Result(candidateId, TranslationPolicyDecision.HardAllow, "known_text_field");
            }

            if (LooksLikeStrongIdentifier(text))
            {
                return Result(candidateId, TranslationPolicyDecision.Ambiguous, "identifier_like_unknown");
            }

            return Result(candidateId, TranslationPolicyDecision.Ambiguous, "unknown_field_semantics");
        }

        private static TranslationPolicyClassification Result(
            string candidateId,
            TranslationPolicyDecision decision,
            string reasonCode)
        {
            return new TranslationPolicyClassification
            {
                CandidateId = candidateId,
                Decision = decision,
                ReasonCode = reasonCode
            };
        }

        private static bool IsDeniedField(string field)
        {
            if (DeniedFields.Contains(field)) return true;

            string lower = field.ToLowerInvariant();
            return lower.EndsWith("defname", StringComparison.Ordinal) ||
                   lower.EndsWith("dollname", StringComparison.Ordinal) ||
                   lower.EndsWith("dollpartname", StringComparison.Ordinal) ||
                   lower.EndsWith("methodname", StringComparison.Ordinal) ||
                   lower.EndsWith("class", StringComparison.Ordinal) ||
                   lower.EndsWith("worker", StringComparison.Ordinal) ||
                   lower.EndsWith("def", StringComparison.Ordinal) ||
                   lower.EndsWith("texpath", StringComparison.Ordinal) ||
                   lower.EndsWith("graphicpath", StringComparison.Ordinal) ||
                   lower.EndsWith("texturepath", StringComparison.Ordinal) ||
                   lower.EndsWith("shader", StringComparison.Ordinal);
        }

        private static bool IsStructuralReferenceField(string field)
        {
            if (StructuralReferenceFields.Contains(field)) return true;

            string lower = (field ?? string.Empty).Trim().ToLowerInvariant();
            return lower.EndsWith("def", StringComparison.Ordinal) ||
                   lower.EndsWith("defs", StringComparison.Ordinal) ||
                   lower.EndsWith("kind", StringComparison.Ordinal) ||
                   lower.EndsWith("type", StringComparison.Ordinal) ||
                   lower.EndsWith("category", StringComparison.Ordinal) ||
                   lower.EndsWith("categories", StringComparison.Ordinal) ||
                   lower.EndsWith("skill", StringComparison.Ordinal) ||
                   lower.EndsWith("priority", StringComparison.Ordinal) ||
                   lower.EndsWith("ref", StringComparison.Ordinal) ||
                   lower.EndsWith("key", StringComparison.Ordinal) ||
                   lower.EndsWith("tag", StringComparison.Ordinal) ||
                   lower.EndsWith("hook", StringComparison.Ordinal) ||
                   lower.EndsWith("symbol", StringComparison.Ordinal);
        }

        private static bool LooksLikeEnumOrReferenceValue(string text)
        {
            return text.Length <= 80 && Regex.IsMatch(
                text,
                @"^[A-Za-z][A-Za-z0-9_]*$",
                RegexOptions.CultureInvariant);
        }

        internal static bool IsProtectedDefType(string defType)
        {
            string lower = (defType ?? string.Empty).Trim().ToLowerInvariant();
            return lower.Contains("facedef") ||
                   lower.Contains("eyedef") ||
                   lower.Contains("browdef") ||
                   lower.Contains("liddef") ||
                   lower.Contains("lashdef") ||
                   lower.Contains("mouthdef") ||
                   lower.Contains("nosedef") ||
                   lower.Contains("eardef") ||
                   lower.Contains("skindef") ||
                   lower.Contains("facialanimation");
        }

        private static bool IsProtectedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string[] segments = path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Any(segment => ProtectedPathSegments.Contains(segment));
        }

        private static bool LooksLikeStrongPathOrResource(string text)
        {
            if (FilePathRegex.IsMatch(text) || UriRegex.IsMatch(text)) return true;
            if (text.Contains("\\") && !text.Any(char.IsWhiteSpace)) return true;
            if (text.StartsWith("Tex/", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("UI/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int firstSlash = text.IndexOf('/');
            return firstSlash >= 0 &&
                   text.IndexOf('/', firstSlash + 1) >= 0 &&
                   !text.Any(char.IsWhiteSpace);
        }

        private static bool LooksLikeStrongIdentifier(string text)
        {
            if (!StructuredIdentifierRegex.IsMatch(text)) return false;
            if (text.IndexOf('_') >= 0 || text.IndexOf(':') >= 0) return true;
            if (text.IndexOf('.') >= 0 && !text.EndsWith(".", StringComparison.Ordinal)) return true;

            for (int i = 1; i < text.Length; i++)
            {
                if (char.IsUpper(text[i])) return true;
            }

            return false;
        }

        private static bool LooksLikeLocalizationKeyReference(string text)
        {
            return LocalizationKeyReferenceRegex.IsMatch(text);
        }

        private static bool IsClearlyUntranslatableGrammarFragment(string text)
        {
            string ruleName;
            string rightSide;
            if (!TrySplitGrammarRule(text, out ruleName, out rightSide)) return false;

            return !ShouldTranslateGrammarRuleRightSide(ruleName, rightSide);
        }

        private static bool TrySplitGrammarRule(string text, out string ruleName, out string rightSide)
        {
            ruleName = string.Empty;
            rightSide = string.Empty;
            if (string.IsNullOrEmpty(text)) return false;

            int arrow = text.IndexOf("->", StringComparison.Ordinal);
            if (arrow <= 0) return false;

            string leftSide = text.Substring(0, arrow).Trim();
            int metadataStart = leftSide.IndexOf('(');
            ruleName = metadataStart >= 0 ? leftSide.Substring(0, metadataStart).Trim() : leftSide;
            rightSide = text.Substring(arrow + 2);
            return ruleName.Length > 0;
        }

        internal static bool ShouldTranslateGrammarRuleRightSide(string ruleName, string rightSide)
        {
            if (ruleName.Equals("start", StringComparison.OrdinalIgnoreCase) ||
                ruleName.Equals("middle", StringComparison.OrdinalIgnoreCase) ||
                ruleName.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string sample = NormalizeGrammarRightSideForDecision(rightSide);
            if (sample.Length == 0) return false;
            if (Regex.IsMatch(sample, @"^[A-Za-z]{1,2}-?$", RegexOptions.CultureInvariant) &&
                !HasEnglishSignal(sample))
            {
                return false;
            }

            if (Regex.IsMatch(sample, @"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant) &&
                sample.IndexOf('_') >= 0)
            {
                return false;
            }

            int latinCount = sample.Count(character =>
                (character >= 'A' && character <= 'Z') ||
                (character >= 'a' && character <= 'z') ||
                (character >= '\u00C0' && character <= '\u024F'));
            int letterCount = sample.Count(char.IsLetter);
            return (letterCount >= 3 && latinCount >= 3) || HasEnglishSignal(sample);
        }

        private static string NormalizeGrammarRightSideForDecision(string rightSide)
        {
            if (string.IsNullOrWhiteSpace(rightSide)) return string.Empty;

            string sample = rightSide
                .Replace("\\n", " ")
                .Replace("\\r", " ")
                .Replace("\\t", " ")
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Replace("\t", " ");
            sample = Regex.Replace(sample, @"<[^>]+>", " ", RegexOptions.CultureInvariant);
            sample = ProtectedGrammarTokenRegex.Replace(sample, " ");
            sample = Regex.Replace(sample, @"\$[A-Za-z0-9_]+|%[A-Za-z]", " ", RegexOptions.CultureInvariant);
            sample = Regex.Replace(sample, @"[_/\\]+", " ", RegexOptions.CultureInvariant);
            sample = Regex.Replace(sample, @"[^A-Za-z\u00C0-\u024F\s'\-]", " ", RegexOptions.CultureInvariant);
            sample = Regex.Replace(sample, @"\s+", " ", RegexOptions.CultureInvariant);
            return sample.Trim();
        }

        private static bool HasEnglishSignal(string sample)
        {
            if (string.IsNullOrWhiteSpace(sample)) return false;
            if (GrammarEnglishSignalRegex.IsMatch(sample)) return true;
            return Regex.IsMatch(sample, @"^[A-Za-z][A-Za-z '\-]{2,}$", RegexOptions.CultureInvariant);
        }

        private static bool IsGrammarIdentifierLike(string text)
        {
            int arrow = text.IndexOf("->", StringComparison.Ordinal);
            if (arrow <= 0) return false;
            string rightSide = text.Substring(arrow + 2).Trim();
            return Regex.IsMatch(rightSide, @"^[A-Za-z0-9_.:-]+$", RegexOptions.CultureInvariant) &&
                   (rightSide.IndexOf('_') >= 0 || rightSide.IndexOf(':') >= 0 || rightSide.IndexOf('.') >= 0);
        }

        private static bool IsKnownTextListPath(string path)
        {
            string lower = (path ?? string.Empty).ToLowerInvariant();
            return lower.Contains(".rulesstrings.") ||
                   lower.Contains(".thoughtstagedescriptions.") ||
                   lower.EndsWith(".rulesstrings", StringComparison.Ordinal) ||
                   lower.EndsWith(".thoughtstagedescriptions", StringComparison.Ordinal);
        }
    }
}
