using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace AutoTranslator_Core
{
    internal static class TranslationXmlDictionaryParser
    {
        internal static Dictionary<string, string> Parse(XmlNode documentElement, Action<string, string, string> duplicate = null)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            if (documentElement == null) return result;

            foreach (XmlNode child in documentElement.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                if (TranslationXmlEntryCodec.TryReadReplacement(child, out string key, out string value))
                {
                    AddValue(result, key, NormalizeValue(value), duplicate);
                    continue;
                }
                AddElement(result, child, child.Name, duplicate);
            }

            return result;
        }

        internal static Dictionary<string, List<string>> ReadFullLists(string path)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
            foreach (XmlNode node in document.DocumentElement.ChildNodes)
            {
                if (!(node is XmlElement)) continue;
                var children = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OfType<XmlElement>(node.ChildNodes));
                if (children.Length == 0 || !System.Linq.Enumerable.All(children, c => c.Name == "li" && !System.Linq.Enumerable.Any(System.Linq.Enumerable.OfType<XmlElement>(c.ChildNodes)))) continue;
                if (result.ContainsKey(node.Name)) throw new System.IO.InvalidDataException("Conflicting full-list XML retained for review: " + node.Name);
                result[node.Name] = new List<string>(System.Linq.Enumerable.Select(children, c => NormalizeValue(c.InnerText)));
            }
            return result;
        }

        private static void AddElement(
            Dictionary<string, string> result,
            XmlNode node,
            string path, Action<string, string, string> duplicate)
        {
            bool hasElementChildren = false;
            int listIndex = 0;

            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                hasElementChildren = true;

                string segment;
                if (child.Name.Equals("li", StringComparison.OrdinalIgnoreCase))
                {
                    segment = listIndex.ToString(CultureInfo.InvariantCulture);
                    listIndex++;
                }
                else
                {
                    segment = child.Name;
                }

                AddElement(result, child, path + "." + segment, duplicate);
            }

            if (hasElementChildren) return;

            AddValue(result, path, NormalizeValue(node.InnerText), duplicate);
        }

        private static void AddValue(Dictionary<string, string> result, string key, string value, Action<string, string, string> duplicate)
        {
            if (result.TryGetValue(key, out string previous)) duplicate?.Invoke(key, previous, value);
            result[key] = value;
        }

        internal static string NormalizeValue(string value)
        {
            return (value ?? string.Empty).Replace("\\n", "\n").Replace("\\r", "\r");
        }
    }
}
