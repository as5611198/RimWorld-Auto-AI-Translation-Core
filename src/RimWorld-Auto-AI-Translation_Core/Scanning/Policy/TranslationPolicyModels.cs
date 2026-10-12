using System;
using System.Collections.Generic;

namespace AutoTranslator_Core.TranslationPolicy
{
    public enum TranslationPolicyBucket
    {
        Keyed = 0,
        DefInjected = 1
    }

    public enum TranslationPolicyDecision
    {
        HardAllow = 0,
        HardDeny = 1,
        Ambiguous = 2
    }

    public sealed class TranslationPolicySourceContext
    {
        public TranslationPolicySourceContext()
        {
            PackageId = string.Empty;
            ModName = string.Empty;
            SourceFile = string.Empty;
            DeclaringAssembly = string.Empty;
            SchemaFingerprint = string.Empty;
        }

        public string PackageId { get; set; }
        public string ModName { get; set; }
        public string SourceFile { get; set; }
        public string DeclaringAssembly { get; set; }
        public string SchemaFingerprint { get; set; }
    }

    public sealed class TranslationPolicyCandidate
    {
        public TranslationPolicyCandidate()
        {
            CandidateId = string.Empty;
            PackageId = string.Empty;
            ModName = string.Empty;
            SourceFile = string.Empty;
            DefType = string.Empty;
            KeyOrPath = string.Empty;
            FieldName = string.Empty;
            SourceText = string.Empty;
            DeclaringAssembly = string.Empty;
            SchemaFingerprint = string.Empty;
        }

        public string CandidateId { get; set; }
        public string PackageId { get; set; }
        public string ModName { get; set; }
        public string SourceFile { get; set; }
        public TranslationPolicyBucket Bucket { get; set; }
        public string DefType { get; set; }
        public string KeyOrPath { get; set; }
        public string FieldName { get; set; }
        public string SourceText { get; set; }
        public int SourceLineNumber { get; set; }
        public bool IsInherited { get; set; }
        public string DeclaringAssembly { get; set; }
        public string SchemaFingerprint { get; set; }
    }

    public sealed class TranslationPolicyClassification
    {
        public TranslationPolicyClassification()
        {
            CandidateId = string.Empty;
            ReasonCode = string.Empty;
        }

        public string CandidateId { get; set; }
        public TranslationPolicyDecision Decision { get; set; }
        public string ReasonCode { get; set; }
    }

}
