using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace AutoTranslator_Core
{
    internal static class TranslationXmlDictionaryParser
    {
        internal static Dictionary<string, string> Parse(XmlNode documentElement)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            if (documentElement == null) return result;

            foreach (XmlNode child in documentElement.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                AddElement(result, child, child.Name);
            }

            return result;
        }

        private static void AddElement(
            Dictionary<string, string> result,
            XmlNode node,
            string path)
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

                AddElement(result, child, path + "." + segment);
            }

            if (hasElementChildren) return;

            string value = node.InnerText;
            if (!string.IsNullOrEmpty(value))
            {
                value = value.Replace("\\n", "\n").Replace("\\r", "\r").Replace("/n", "\n");
            }

            result[path] = value;
        }
    }
}
