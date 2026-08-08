using System;

namespace AutoTranslator_Core.TranslationPolicy
{
    public enum TranslationPolicyApplicationDecision
    {
        Translate = 0,
        KeepExisting = 1,
        Remove = 2
    }

    public static class TranslationPolicyApplication
    {
        public static TranslationPolicyApplicationDecision Resolve(
            TranslationPolicyDecision localDecision,
            TranslationPolicyAgentDecision agentDecision,
            bool hasExistingTranslation)
        {
            if (localDecision == TranslationPolicyDecision.HardAllow ||
                (localDecision == TranslationPolicyDecision.Ambiguous &&
                 agentDecision == TranslationPolicyAgentDecision.Allow))
            {
                return hasExistingTranslation
                    ? TranslationPolicyApplicationDecision.KeepExisting
                    : TranslationPolicyApplicationDecision.Translate;
            }

            if (localDecision == TranslationPolicyDecision.HardDeny ||
                (localDecision == TranslationPolicyDecision.Ambiguous &&
                 agentDecision == TranslationPolicyAgentDecision.Deny))
            {
                return TranslationPolicyApplicationDecision.Remove;
            }

            // Review, unresolved, cancellation, provider failure, and budget exhaustion
            // fail closed for new work but never discard a previously accepted translation.
            return hasExistingTranslation
                ? TranslationPolicyApplicationDecision.KeepExisting
                : TranslationPolicyApplicationDecision.Remove;
        }
    }

    public static class TranslationPolicyAgentTimeout
    {
        public const int DefaultSeconds = 60;
        public const int MinimumSeconds = 15;
        public const int MaximumSeconds = 300;

        public static int Resolve(int configuredSeconds, int providerFloorSeconds)
        {
            int configured = configuredSeconds > 0 ? configuredSeconds : DefaultSeconds;
            int floor = Math.Max(0, providerFloorSeconds);
            return Math.Min(
                MaximumSeconds,
                Math.Max(MinimumSeconds, Math.Max(configured, floor)));
        }
    }
}
