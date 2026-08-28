using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace AutoTranslator_Core.TranslationPolicy
{
    public static class TranslationPolicyXmlScanner
    {
        public static List<TranslationPolicyCandidate> ScanKeyedXml(
            string xml,
            TranslationPolicySourceContext context)
        {
            XmlDocument document = LoadDocument(xml);
            List<TranslationPolicyCandidate> candidates = new List<TranslationPolicyCandidate>();
            if (document.DocumentElement == null ||
                !document.DocumentElement.Name.Equals("LanguageData", StringComparison.OrdinalIgnoreCase))
            {
                return candidates;
            }

            TraverseKeyed(document.DocumentElement, string.Empty, context, candidates);
            return candidates.OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal).ToList();
        }

        public static List<TranslationPolicyCandidate> ScanDefsXml(
            string xml,
            TranslationPolicySourceContext context)
        {
            XmlDocument document = LoadDocument(xml);
            List<TranslationPolicyCandidate> candidates = new List<TranslationPolicyCandidate>();
            if (document.DocumentElement == null ||
                !document.DocumentElement.Name.Equals("Defs", StringComparison.OrdinalIgnoreCase))
            {
                return candidates;
            }

            List<ResolvedDefXmlNode> resolvedNodes = DefXmlInheritanceResolver.Resolve(
                new[] { new DefXmlSourceDocument { Document = document, SourceFile = context?.SourceFile } });
            foreach (ResolvedDefXmlNode resolved in resolvedNodes)
            {
                XmlNode defNode = resolved.OriginalNode;
                string defName = DefXmlInheritanceResolver.GetDirectChildText(defNode, "defName");
                if (string.IsNullOrWhiteSpace(defName)) continue;
                TraverseDef(resolved.ResolvedNode, defName.Trim(), defNode.Name, context, candidates);
            }

            return candidates.OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal).ToList();
        }

        public static List<TranslationPolicyCandidate> ScanDefInjectedXml(
            string xml,
            string defType,
            TranslationPolicySourceContext context)
        {
            XmlDocument document = LoadDocument(xml);
            List<TranslationPolicyCandidate> candidates = new List<TranslationPolicyCandidate>();
            if (document.DocumentElement == null ||
                !document.DocumentElement.Name.Equals("LanguageData", StringComparison.OrdinalIgnoreCase))
            {
                return candidates;
            }

            TraverseDefInjected(document.DocumentElement, string.Empty, defType, context, candidates);
            return candidates.OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal).ToList();
        }

        private static XmlDocument LoadDocument(string xml)
        {
            if (xml == null) throw new ArgumentNullException(nameof(xml));

            XmlReaderSettings settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                XmlResolver = null
            };

            XmlDocument document = new XmlDocument { XmlResolver = null };
            using (StringReader stringReader = new StringReader(xml))
            using (XmlReader reader = XmlReader.Create(stringReader, settings))
            {
                document.Load(reader);
            }

            return document;
        }

        private static void TraverseKeyed(
            XmlNode parent,
            string currentPath,
            TranslationPolicySourceContext context,
            List<TranslationPolicyCandidate> candidates)
        {
            List<XmlNode> children = ElementChildren(parent).ToList();
            Dictionary<string, int> totals = children
                .GroupBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (XmlNode child in children)
            {
                int siblingIndex;
                if (!seen.TryGetValue(child.Name, out siblingIndex)) siblingIndex = 0;
                seen[child.Name] = siblingIndex + 1;

                string segment = child.Name;
                if (totals[child.Name] > 1)
                {
                    segment += "[" + siblingIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                }

                string childPath = string.IsNullOrEmpty(currentPath) ? segment : currentPath + "." + segment;
                if (IsTextLeaf(child))
                {
                    AddCandidate(
                        candidates,
                        context,
                        TranslationPolicyBucket.Keyed,
                        string.Empty,
                        childPath,
                        child.Name,
                        child.InnerText.Trim());
                }
                else
                {
                    TraverseKeyed(child, childPath, context, candidates);
                }
            }
        }

        private static void TraverseDef(
            XmlNode parent,
            string currentPath,
            string defType,
            TranslationPolicySourceContext context,
            List<TranslationPolicyCandidate> candidates)
        {
            int listIndex = 0;
            foreach (XmlNode child in ElementChildren(parent))
            {
                bool isListItem = child.Name.Equals("li", StringComparison.OrdinalIgnoreCase);
                string segment;
                string fieldName;
                if (isListItem)
                {
                    segment = listIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    listIndex++;
                    fieldName = parent.Name;
                }
                else
                {
                    segment = child.Name;
                    fieldName = child.Name;
                }

                string childPath = currentPath + "." + segment;
                if (IsTextLeaf(child))
                {
                    AddCandidate(
                        candidates,
                        context,
                        TranslationPolicyBucket.DefInjected,
                        defType,
                        childPath,
                        fieldName,
                        child.InnerText.Trim());
                }
                else
                {
                    TraverseDef(child, childPath, defType, context, candidates);
                }
            }
        }

        private static void TraverseDefInjected(
            XmlNode parent,
            string currentPath,
            string defType,
            TranslationPolicySourceContext context,
            List<TranslationPolicyCandidate> candidates)
        {
            List<XmlNode> children = ElementChildren(parent).ToList();
            Dictionary<string, int> totals = children
                .GroupBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (XmlNode child in children)
            {
                int siblingIndex;
                if (!seen.TryGetValue(child.Name, out siblingIndex)) siblingIndex = 0;
                seen[child.Name] = siblingIndex + 1;

                string segment = child.Name;
                if (totals[child.Name] > 1)
                {
                    segment += "[" + siblingIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                }

                string childPath = string.IsNullOrEmpty(currentPath) ? segment : currentPath + "." + segment;
                if (IsTextLeaf(child))
                {
                    AddCandidate(
                        candidates,
                        context,
                        TranslationPolicyBucket.DefInjected,
                        defType ?? string.Empty,
                        childPath,
                        GetTerminalFieldName(childPath),
                        child.InnerText.Trim());
                }
                else
                {
                    TraverseDefInjected(child, childPath, defType, context, candidates);
                }
            }
        }

        private static IEnumerable<XmlNode> ElementChildren(XmlNode parent)
        {
            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element) yield return child;
            }
        }

        private static bool IsTextLeaf(XmlNode node)
        {
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element) return false;
            }

            return !string.IsNullOrWhiteSpace(node.InnerText);
        }

        private static string GetDirectChildText(XmlNode parent, string childName)
        {
            foreach (XmlNode child in ElementChildren(parent))
            {
                if (child.Name.Equals(childName, StringComparison.OrdinalIgnoreCase))
                {
                    return child.InnerText ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static string GetTerminalFieldName(string path)
        {
            string[] parts = (path ?? string.Empty).Split('.');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string part = parts[i];
                int bracket = part.IndexOf('[');
                if (bracket >= 0) part = part.Substring(0, bracket);
                if (part.Length == 0 || part.All(char.IsDigit)) continue;
                return part;
            }

            return string.Empty;
        }

        private static void AddCandidate(
            List<TranslationPolicyCandidate> candidates,
            TranslationPolicySourceContext context,
            TranslationPolicyBucket bucket,
            string defType,
            string keyOrPath,
            string fieldName,
            string sourceText)
        {
            TranslationPolicySourceContext safeContext = context ?? new TranslationPolicySourceContext();
            TranslationPolicyCandidate candidate = new TranslationPolicyCandidate
            {
                PackageId = safeContext.PackageId ?? string.Empty,
                ModName = safeContext.ModName ?? string.Empty,
                SourceFile = safeContext.SourceFile ?? string.Empty,
                Bucket = bucket,
                DefType = defType ?? string.Empty,
                KeyOrPath = keyOrPath ?? string.Empty,
                FieldName = fieldName ?? string.Empty,
                SourceText = sourceText ?? string.Empty,
                DeclaringAssembly = safeContext.DeclaringAssembly ?? string.Empty,
                SchemaFingerprint = safeContext.SchemaFingerprint ?? string.Empty
            };
            candidate.CandidateId = TranslationPolicyIdentity.CreateCandidateId(candidate);
            candidates.Add(candidate);
        }
    }
}
