using System;
using System.Linq;

namespace AutoTranslator_Core.TranslationPolicy
{
    // Native author translations are trusted unless the local classifier can prove
    // they are structural data. Ambiguous values never invoke the Agent prediction here.
    internal static class TranslationPolicyNativeTargetFilter
    {
        internal static bool ShouldKeep(
            TranslationPolicyBucket bucket,
            string defType,
            string keyOrPath,
            string value)
        {
            return ShouldKeep(
                string.Empty,
                string.Empty,
                bucket,
                defType,
                keyOrPath,
                value,
                string.Empty);
        }

        internal static bool ShouldKeep(
            string packageId,
            string modName,
            TranslationPolicyBucket bucket,
            string defType,
            string keyOrPath,
            string value,
            string sourceFile)
        {
            TranslationPolicyCandidate candidate = new TranslationPolicyCandidate
            {
                PackageId = packageId ?? string.Empty,
                ModName = modName ?? string.Empty,
                SourceFile = sourceFile ?? string.Empty,
                Bucket = bucket,
                DefType = defType ?? string.Empty,
                KeyOrPath = keyOrPath ?? string.Empty,
                FieldName = bucket == TranslationPolicyBucket.Keyed
                    ? keyOrPath ?? string.Empty
                    : GetTerminalField(keyOrPath),
                SourceText = value ?? string.Empty,
                DeclaringAssembly = string.Empty,
                SchemaFingerprint = string.Empty,
                CandidateId = "native-local"
            };
            return TranslationPolicyClassifier.Classify(candidate).Decision != TranslationPolicyDecision.HardDeny;
        }

        private static string GetTerminalField(string path)
        {
            string[] segments = (path ?? string.Empty).Split('.');
            for (int index = segments.Length - 1; index >= 0; index--)
            {
                string segment = segments[index];
                int bracket = segment.IndexOf('[');
                if (bracket >= 0) segment = segment.Substring(0, bracket);
                if (segment.Length == 0 || segment.All(char.IsDigit)) continue;
                return segment;
            }

            return string.Empty;
        }
    }
}
