using System;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AutoTranslator_Core.Workflow
{
    public static class WorkflowIdentity
    {
        public const string CandidateIdentitySchemaVersion = "atc-candidate-v1";
        // Small rule updates use decimal revisions within the released xml-3 series.
        public const string XmlAnalyzerVersion = "xml-3.01";
        public const string DllAnalyzerVersion = "dll-3.08";
        // One revision covers UI capture, source-Mod attribution and candidate classification.
        public const string UiObservationPipelineVersion = "ui-1.02";
        // AI review metadata is retained for diagnostics. A non-empty AI layer is
        // reusable until the user explicitly clears that mod's AI review results.
        public const string AiReviewVersion = "ai-review-4";
        public const string AiReviewPromptVersion = "ai-review-prompt-4.0";
        public const string AiTranslationPromptVersion = "ai-translation-prompt-2.2";
        private const string CandidateIdPrefix = "atc1_";

        public static string CreateCandidateId(
            string modIdentity,
            CandidateSourceDomain sourceDomain,
            string entryKind,
            string logicalLocator)
        {
            StringBuilder canonical = new StringBuilder();
            AppendCanonicalField(canonical, "version", CandidateIdentitySchemaVersion);
            AppendCanonicalField(canonical, "modIdentity", NormalizeIdentityPart(modIdentity));
            AppendCanonicalField(canonical, "sourceDomain", sourceDomain.ToString().ToLowerInvariant());
            AppendCanonicalField(canonical, "entryKind", NormalizeSemanticPart(entryKind));
            AppendCanonicalField(canonical, "logicalLocator", NormalizeLocator(logicalLocator));
            return CandidateIdPrefix + Sha256(canonical.ToString());
        }

        public static string CreateReadableEntryIdentity(
            CandidateSourceDomain sourceDomain,
            string entryKind,
            string logicalLocator)
        {
            return sourceDomain.ToString().ToLowerInvariant() + ":" +
                   NormalizeSemanticPart(entryKind) + ":" + NormalizeLocator(logicalLocator);
        }

        public static string CreateModIdentity(
            string packageId,
            string author,
            string displayName,
            string fallbackRootName)
        {
            if (!string.IsNullOrWhiteSpace(packageId))
                return "pkg:" + NormalizeIdentityPart(packageId);

            StringBuilder fallback = new StringBuilder();
            AppendCanonicalField(fallback, "author", NormalizeIdentityPart(author));
            AppendCanonicalField(fallback, "displayName", NormalizeIdentityPart(displayName));
            AppendCanonicalField(fallback, "rootName", NormalizeIdentityPart(fallbackRootName));
            string fallbackMaterial = fallback.ToString();
            return "local:" + Sha256(fallbackMaterial);
        }

        public static string HashText(string value)
        {
            return Sha256(NormalizeNewLines(value ?? string.Empty));
        }

        public static string CreateContentFingerprint(string sourceText, string context)
        {
            return Sha256(NormalizeNewLines(sourceText ?? string.Empty) + "\n" + NormalizeNewLines(context ?? string.Empty));
        }

        public static string CreateAnalysisFingerprint(
            string analyzerVersion,
            string modVersionFingerprint,
            string settingsFingerprint)
        {
            return Sha256(string.Join("\n", new[]
            {
                NormalizeIdentityPart(analyzerVersion),
                NormalizeIdentityPart(modVersionFingerprint),
                NormalizeNewLines(settingsFingerprint ?? string.Empty)
            }));
        }

        public static string CreateAnalysisResultFingerprint(
            IEnumerable<CandidateRecord> candidates)
        {
            StringBuilder material = new StringBuilder();
            foreach (CandidateRecord candidate in (candidates ?? Enumerable.Empty<CandidateRecord>())
                         .Where(candidate => candidate != null)
                         .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal))
            {
                AppendCanonicalField(material, "candidateId", candidate.CandidateId);
                AppendCanonicalField(material, "sourceHash", candidate.SourceTextHash);
                AppendCanonicalField(material, "contentFingerprint", candidate.ContentFingerprint);
                AppendCanonicalField(
                    material, "classificationFlags", candidate.ClassificationFlags.ToString());
                AppendCanonicalField(material, "contextHash", HashText(candidate.ContextJson));
                AppendCanonicalField(material, "outputPath", candidate.DefaultOutputFileRelativePath);
                AppendCanonicalField(material, "outputKey", candidate.TranslationEntryKey);
                AppendCanonicalField(material, "xmlReason", candidate.XmlReasonCode);
                AppendCanonicalField(material, "dllReason", candidate.DllReasonCode);
            }
            return Sha256(material.ToString());
        }

        public static bool IsTranslationCurrent(CandidateRecord candidate)
        {
            if (candidate == null) return false;
            bool sourceIsCurrent = !string.IsNullOrEmpty(candidate.SourceTextHash) &&
                                   string.Equals(candidate.SourceTextHash,
                                       candidate.SourceTextHashAtTranslation, StringComparison.Ordinal);
            if (!sourceIsCurrent || string.IsNullOrWhiteSpace(candidate.TranslationText)) return false;
            if (candidate.TranslationState == CandidateTranslationState.Translated) return true;
            return candidate.TranslationOrigin > TranslationOrigin.AiTranslation;
        }

        public static string CreateAiReviewFingerprint(CandidateRecord candidate)
        {
            if (candidate == null) return string.Empty;
            return HashText(
                candidate.CandidateId + "\n" + candidate.SourceTextHash + "\n" +
                candidate.LogicalLocator + "\n" + candidate.SourceFileRelativePath + "\n" +
                candidate.ContextJson);
        }

        public static bool IsAiReviewCurrent(CandidateRecord candidate)
        {
            return candidate != null &&
                   ClassificationFlagsCodec.Get(
                       candidate.ClassificationFlags, ClassificationLayer.AiReview) !=
                   CandidateClassification.NotAnalyzed;
        }

        private static string NormalizeLocator(string value)
        {
            return NormalizeSemanticPart((value ?? string.Empty).Replace('\\', '/'));
        }

        private static string NormalizeIdentityPart(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC);
        }

        private static string NormalizeSemanticPart(string value)
        {
            return (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);
        }

        private static void AppendCanonicalField(StringBuilder output, string name, string value)
        {
            value = value ?? string.Empty;
            int byteLength = Encoding.UTF8.GetByteCount(value);
            output.Append(name.Length).Append(':').Append(name)
                .Append('=').Append(byteLength).Append(':').Append(value).Append(';');
        }

        private static string NormalizeNewLines(string value)
        {
            return value.Replace("\r\n", "\n").Replace('\r', '\n').Normalize(NormalizationForm.FormC);
        }

        private static string Sha256(string value)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder output = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) output.Append(hash[i].ToString("x2"));
                return output.ToString();
            }
        }
    }
}
