using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTranslator_Core
{
    public static class PolicyAnalysisCandidateDomain
    {
        public const string Xml = "xml";
        public const string Dll = "dll";

        public static string Normalize(string value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == Dll ? Dll : normalized == Xml ? Xml : string.Empty;
        }

        public static bool IsKnown(string value)
        {
            return Normalize(value).Length > 0;
        }

        public static bool IsCandidateIdValid(string domain, string candidateId)
        {
            string normalizedDomain = Normalize(domain);
            string id = (candidateId ?? string.Empty).Trim();
            if (id.Length == 0) return false;
            if (normalizedDomain == Dll)
                return id.StartsWith("hardcoded-ui:", StringComparison.Ordinal);
            if (normalizedDomain == Xml)
                return id.StartsWith("tpc_", StringComparison.Ordinal);
            return false;
        }

        public static bool AreCandidateIdsValid(string domain, IEnumerable<string> candidateIds)
        {
            return IsKnown(domain) &&
                   (candidateIds ?? Enumerable.Empty<string>()).All(id => IsCandidateIdValid(domain, id));
        }
    }
}
