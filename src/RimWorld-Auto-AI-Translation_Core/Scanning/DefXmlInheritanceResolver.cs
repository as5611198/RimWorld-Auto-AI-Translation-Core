using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace AutoTranslator_Core
{
    public sealed class DefXmlSourceDocument
    {
        public XmlDocument Document;
        public string SourceFile;
    }

    public sealed class ResolvedDefXmlNode
    {
        public XmlNode OriginalNode;
        public XmlNode ResolvedNode;
        public string SourceFile;
    }

    public static class DefXmlInheritanceResolver
    {
        public static List<ResolvedDefXmlNode> Resolve(
            IEnumerable<DefXmlSourceDocument> sourceDocuments,
            Action<string> warning = null)
        {
            List<DefXmlSourceDocument> documents = (sourceDocuments ?? Enumerable.Empty<DefXmlSourceDocument>())
                .Where(source => source != null && source.Document != null)
                .ToList();
            var namedNodes = new Dictionary<string, XmlNode>(StringComparer.OrdinalIgnoreCase);

            foreach (DefXmlSourceDocument source in documents)
            {
                XmlElement root = source.Document.DocumentElement;
                if (root == null || !root.Name.Equals("Defs", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (XmlNode node in ElementChildren(root))
                {
                    string name = GetAttribute(node, "Name");
                    if (!string.IsNullOrWhiteSpace(name)) namedNodes[name.Trim()] = node;
                }
            }

            var result = new List<ResolvedDefXmlNode>();
            foreach (DefXmlSourceDocument source in documents)
            {
                XmlElement root = source.Document.DocumentElement;
                if (root == null || !root.Name.Equals("Defs", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (XmlNode node in ElementChildren(root))
                {
                    result.Add(new ResolvedDefXmlNode
                    {
                        OriginalNode = node,
                        ResolvedNode = ResolveNode(
                            node,
                            namedNodes,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            warning),
                        SourceFile = source.SourceFile ?? string.Empty
                    });
                }
            }

            return result;
        }

        public static string GetDirectChildText(XmlNode parent, string childName)
        {
            if (parent == null || string.IsNullOrWhiteSpace(childName)) return string.Empty;
            foreach (XmlNode child in ElementChildren(parent))
            {
                if (child.Name.Equals(childName, StringComparison.OrdinalIgnoreCase))
                {
                    return child.InnerText ?? string.Empty;
                }
            }
            return string.Empty;
        }

        private static XmlNode ResolveNode(
            XmlNode node,
            Dictionary<string, XmlNode> namedNodes,
            HashSet<string> resolvingNames,
            Action<string> warning)
        {
            if (node == null) return null;
            if (IsFalse(GetAttribute(node, "Inherit"))) return CloneIntoNewDocument(node);

            string parentName = GetAttribute(node, "ParentName");
            if (string.IsNullOrWhiteSpace(parentName)) return CloneIntoNewDocument(node);
            parentName = parentName.Trim();

            if (!namedNodes.TryGetValue(parentName, out XmlNode parent))
            {
                warning?.Invoke("Named XML parent was not found: " + parentName);
                return CloneIntoNewDocument(node);
            }

            if (!resolvingNames.Add(parentName))
            {
                warning?.Invoke("Named XML inheritance cycle detected at: " + parentName);
                return CloneIntoNewDocument(node);
            }

            XmlNode resolvedParent = ResolveNode(parent, namedNodes, resolvingNames, warning);
            resolvingNames.Remove(parentName);
            return MergeNodes(resolvedParent, node);
        }

        private static XmlNode MergeNodes(XmlNode parent, XmlNode child)
        {
            if (parent == null) return CloneIntoNewDocument(child);
            if (child == null) return CloneIntoNewDocument(parent);
            if (IsFalse(GetAttribute(child, "Inherit"))) return CloneIntoNewDocument(child);

            XmlNode merged = CloneIntoNewDocument(parent);
            XmlDocument owner = merged.OwnerDocument;
            CopyAttributes(child, merged);

            List<XmlNode> childElements = ElementChildren(child).ToList();
            Dictionary<string, int> childOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode childElement in childElements)
            {
                int ordinal = 0;
                childOrdinals.TryGetValue(childElement.Name, out ordinal);
                childOrdinals[childElement.Name] = ordinal + 1;

                List<XmlNode> matches = ElementChildren(merged)
                    .Where(candidate => candidate.Name.Equals(childElement.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                XmlNode imported;
                if (childElement.Name.Equals("li", StringComparison.OrdinalIgnoreCase))
                {
                    imported = owner.ImportNode(childElement, true);
                    merged.AppendChild(imported);
                    continue;
                }

                if (ordinal < matches.Count)
                {
                    XmlNode existing = matches[ordinal];
                    bool canMergeChildren = ElementChildren(existing).Any() && ElementChildren(childElement).Any();
                    imported = canMergeChildren
                        ? owner.ImportNode(MergeNodes(existing, childElement), true)
                        : owner.ImportNode(childElement, true);
                    merged.ReplaceChild(imported, existing);
                }
                else
                {
                    imported = owner.ImportNode(childElement, true);
                    merged.AppendChild(imported);
                }
            }

            return merged;
        }

        private static XmlNode CloneIntoNewDocument(XmlNode node)
        {
            if (node == null) return null;
            XmlDocument document = new XmlDocument { XmlResolver = null };
            XmlNode clone = document.ImportNode(node, true);
            document.AppendChild(clone);
            return clone;
        }

        private static void CopyAttributes(XmlNode source, XmlNode destination)
        {
            if (source?.Attributes == null || destination?.Attributes == null) return;
            XmlDocument owner = destination.OwnerDocument;
            foreach (XmlAttribute attribute in source.Attributes)
            {
                XmlAttribute copy = owner.CreateAttribute(attribute.Name);
                copy.Value = attribute.Value;
                XmlAttribute existing = destination.Attributes[attribute.Name];
                if (existing != null) destination.Attributes.Remove(existing);
                destination.Attributes.Append(copy);
            }
        }

        private static IEnumerable<XmlNode> ElementChildren(XmlNode parent)
        {
            if (parent == null) yield break;
            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element) yield return child;
            }
        }

        private static string GetAttribute(XmlNode node, string name)
        {
            if (node?.Attributes == null) return string.Empty;
            XmlAttribute attribute = node.Attributes[name];
            return attribute?.Value ?? string.Empty;
        }

        private static bool IsFalse(string value)
        {
            return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0";
        }
    }
}
