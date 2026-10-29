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
        public string PackageId = string.Empty;
        public int LoadOrder;
    }

    public sealed class ResolvedDefXmlNode
    {
        public XmlNode OriginalNode;
        public XmlNode ResolvedNode;
        public string SourceFile;
        public bool InheritanceIncomplete;
    }

    // A context belongs to one immutable scan snapshot. Its memoized nodes are never
    // returned directly, so callers cannot mutate another module's inheritance data.
    public sealed class DefXmlInheritanceContext
    {
        private readonly List<DefXmlSourceDocument> documents;
        private readonly Dictionary<string, List<XmlNode>> namedNodes = new Dictionary<string, List<XmlNode>>(StringComparer.Ordinal);
        private readonly Dictionary<XmlNode, DefXmlSourceDocument> owners = new Dictionary<XmlNode, DefXmlSourceDocument>();
        private readonly Dictionary<XmlNode, XmlNode> resolvedNodes = new Dictionary<XmlNode, XmlNode>();
        private readonly HashSet<XmlNode> incomplete = new HashSet<XmlNode>();
        private readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> duplicateListFields;
        private readonly Action<string> warning;
        private readonly object sync = new object();
        private readonly DefXmlInheritanceContext baseContext;

        public DefXmlInheritanceContext(IEnumerable<DefXmlSourceDocument> sourceDocuments,
            Action<string> warning = null, IEnumerable<string> duplicateListFields = null,
            DefXmlInheritanceContext baseContext = null)
        {
            this.baseContext = baseContext;
            this.warning = warning;
            this.duplicateListFields = new HashSet<string>(duplicateListFields ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            documents = (sourceDocuments ?? Enumerable.Empty<DefXmlSourceDocument>())
                .Where(source => source != null && source.Document != null)
                .ToList();

            foreach (DefXmlSourceDocument source in documents)
            {
                XmlElement root = source.Document.DocumentElement;
                if (root == null || !root.Name.Equals("Defs", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (XmlNode node in ElementChildren(root))
                {
                    owners[node] = source;
                    string name = GetAttribute(node, "Name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!namedNodes.TryGetValue(name, out List<XmlNode> candidates))
                        namedNodes[name] = candidates = new List<XmlNode>();
                    // RimWorld rejects a duplicate Name within the same mod (first wins).
                    if (candidates.Any(n => owners[n].PackageId == source.PackageId))
                        WarnOnce("duplicate|" + source.PackageId + "|" + name,
                            "Duplicate named XML parent: " + name + "; first definition retained; " + Describe(node));
                    else candidates.Add(node);
                }
            }
        }

        public List<ResolvedDefXmlNode> ResolveTargets(IEnumerable<string> targetFiles)
        {
            lock (sync)
            {
                var targets = new HashSet<string>(targetFiles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                var result = new List<ResolvedDefXmlNode>();
                foreach (DefXmlSourceDocument source in documents.Where(d => targets.Contains(d.SourceFile ?? string.Empty)))
                {
                    XmlElement root = source.Document.DocumentElement;
                    if (root == null || !root.Name.Equals("Defs", StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (XmlNode node in ElementChildren(root))
                    {
                        XmlNode resolved;
                        try { resolved = ResolveNode(node, new HashSet<XmlNode>()); }
                        catch (InheritanceCycleException) { resolved = null; }
                        result.Add(new ResolvedDefXmlNode
                        {
                            OriginalNode = CloneIntoNewDocument(node),
                            ResolvedNode = CloneIntoNewDocument(resolved),
                            InheritanceIncomplete = incomplete.Contains(node),
                            SourceFile = source.SourceFile ?? string.Empty
                        });
                    }
                }

                return result;
            }
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

        private sealed class InheritanceCycleException : Exception { }

        private void WarnOnce(string key, string message)
        {
            if (reported.Add(key)) warning?.Invoke(message);
        }

        private string Describe(XmlNode node)
        {
            DefXmlSourceDocument source = Owner(node);
            return "package=" + source.PackageId + "; file=" + source.SourceFile + "; node=" + node.Name +
                "; Name=" + GetAttribute(node, "Name") + "; defName=" + GetDirectChildText(node, "defName") + "; order=" + source.LoadOrder;
        }

        private DefXmlSourceDocument Owner(XmlNode node)
        {
            return owners.TryGetValue(node, out DefXmlSourceDocument source) ? source : baseContext.Owner(node);
        }

        private IEnumerable<XmlNode> Candidates(string name)
        {
            IEnumerable<XmlNode> local = namedNodes.TryGetValue(name, out List<XmlNode> nodes) ? nodes : Enumerable.Empty<XmlNode>();
            return baseContext == null ? local : local.Concat(baseContext.Candidates(name));
        }

        private bool IsIncomplete(XmlNode node)
        {
            lock (sync) return incomplete.Contains(node) || (!owners.ContainsKey(node) && baseContext != null && baseContext.IsIncomplete(node));
        }

        private XmlNode ResolveShared(XmlNode node)
        {
            lock (sync) return ResolveNode(node, new HashSet<XmlNode>());
        }

        private XmlNode ResolveNode(XmlNode node, HashSet<XmlNode> resolving)
        {
            if (node == null) return null;
            if (!owners.ContainsKey(node) && baseContext != null) return baseContext.ResolveShared(node);
            if (resolvedNodes.TryGetValue(node, out XmlNode cached)) return cached;
            if (!resolving.Add(node))
            {
                string cycle = string.Join(" -> ", resolving.Select(n => Describe(n)).OrderBy(s => s, StringComparer.Ordinal));
                WarnOnce("cycle|" + cycle, "Named XML inheritance cycle detected: " + cycle);
                throw new InheritanceCycleException();
            }
            try
            {
                string parentName = GetAttribute(node, "ParentName");
                XmlNode resolved = CloneIntoNewDocument(node);
                if (!string.IsNullOrWhiteSpace(parentName))
                {
                    // Latest provider at or before the child's mod, including same-mod
                    // forward references. A later mod must not change an earlier parent.
                    XmlNode parent = Candidates(parentName).Where(n => Owner(n).LoadOrder <= Owner(node).LoadOrder)
                        .OrderByDescending(n => Owner(n).LoadOrder).FirstOrDefault();
                    if (parent == null)
                    {
                        incomplete.Add(node);
                        WarnOnce("missing|" + owners[node].PackageId + "|" + parentName,
                            "Named XML parent was not found: " + parentName + "; " + Describe(node) + "; further occurrences in this package/context are suppressed.");
                    }
                    else
                    {
                        XmlNode resolvedParent = ResolveNode(parent, resolving);
                        if (resolvedParent == null) throw new InheritanceCycleException();
                        if (IsIncomplete(parent)) incomplete.Add(node);
                        resolved = MergeNodes(resolvedParent, node);
                    }
                }
                return resolvedNodes[node] = resolved;
            }
            catch (InheritanceCycleException)
            {
                incomplete.Add(node);
                resolvedNodes[node] = null;
                throw;
            }
            finally { resolving.Remove(node); }
        }

        private XmlNode MergeNodes(XmlNode parent, XmlNode child)
        {
            if (parent == null) return CloneIntoNewDocument(child);
            if (child == null) return CloneIntoNewDocument(parent);
            XmlNode merged = CloneIntoNewDocument(parent);
            XmlDocument owner = merged.OwnerDocument;
            if (IsFalse(GetAttribute(child, "Inherit")))
            {
                while (merged.HasChildNodes) merged.RemoveChild(merged.FirstChild);
                foreach (XmlNode element in child.ChildNodes) merged.AppendChild(owner.ImportNode(element, true));
                CopyAttributes(child, merged);
                merged.Attributes?.RemoveNamedItem("Inherit");
                return merged;
            }
            merged.Attributes?.RemoveAll();
            CopyAttributes(child, merged);

            List<XmlNode> childElements = ElementChildren(child).ToList();
            if (childElements.Count == 0 && ElementChildren(parent).Any() && !child.ChildNodes.Cast<XmlNode>().Any(n => n.NodeType == XmlNodeType.Text || n.NodeType == XmlNodeType.CDATA)) return merged;
            foreach (XmlNode childElement in childElements)
            {
                List<XmlNode> matches = ElementChildren(merged)
                    .Where(candidate => candidate.Name == childElement.Name)
                    .ToList();

                XmlNode imported;
                if (childElement.Name == "li" || duplicateListFields.Contains(child.Name))
                {
                    imported = owner.ImportNode(childElement, true);
                    merged.AppendChild(imported);
                    continue;
                }

                if (matches.Count > 0)
                {
                    XmlNode existing = matches[0];
                    imported = owner.ImportNode(MergeNodes(existing, childElement), true);
                    merged.ReplaceChild(imported, existing);
                }
                else
                {
                    imported = owner.ImportNode(childElement, true);
                    merged.AppendChild(imported);
                }
            }

            if (childElements.Count == 0)
            {
                while (merged.HasChildNodes) merged.RemoveChild(merged.FirstChild);
                foreach (XmlNode text in child.ChildNodes) merged.AppendChild(owner.ImportNode(text, true));
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

    public static class DefXmlInheritanceResolver
    {
        public static List<ResolvedDefXmlNode> Resolve(IEnumerable<DefXmlSourceDocument> sourceDocuments, Action<string> warning = null)
        {
            var documents = (sourceDocuments ?? Enumerable.Empty<DefXmlSourceDocument>()).Where(d => d?.Document != null).ToList();
            return new DefXmlInheritanceContext(documents, warning).ResolveTargets(documents.Select(d => d.SourceFile ?? string.Empty));
        }

        public static string GetDirectChildText(XmlNode parent, string childName)
        {
            return DefXmlInheritanceContext.GetDirectChildText(parent, childName);
        }
    }
}
