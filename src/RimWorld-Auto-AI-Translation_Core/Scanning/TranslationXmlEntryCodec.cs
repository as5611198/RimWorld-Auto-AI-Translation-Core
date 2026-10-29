using System;
using System.IO;
using System.Linq;
using System.Xml;

namespace AutoTranslator_Core
{
    internal static class TranslationXmlEntryCodec
    {
        internal static XmlElement Create(XmlDocument document, string key, string value, bool defInjected)
        {
            try
            {
                XmlConvert.VerifyNCName(key);
            }
            catch (XmlException) when (defInjected && !string.IsNullOrWhiteSpace(key) && key.IndexOf('.') > 0)
            {
                // RimWorld reads rep/path/trans literally. EncodeName or an added
                // prefix would silently address a different Def instead.
                XmlConvert.VerifyXmlChars(key);
                XmlElement replacement = document.CreateElement("rep");
                XmlElement path = document.CreateElement("path");
                path.InnerText = key;
                replacement.AppendChild(path);
                XmlElement translation = document.CreateElement("trans");
                translation.InnerText = value;
                replacement.AppendChild(translation);
                return replacement;
            }
            XmlElement element = document.CreateElement(key);
            element.InnerText = value;
            return element;
        }

        internal static bool TryReadReplacement(XmlNode node, out string key, out string value)
        {
            key = value = null;
            if (node.Name != "rep" || (node["path"] == null && node["trans"] == null)) return false;
            XmlElement[] children = node.ChildNodes.OfType<XmlElement>().ToArray();
            if (children.Length != 2 || children.Count(n => n.Name == "path") != 1 || children.Count(n => n.Name == "trans") != 1 ||
                children.Any(n => n.ChildNodes.OfType<XmlElement>().Any()))
                throw new InvalidDataException("Expected one plain-text path and trans in a DefInjected rep entry.");
            key = node["path"].InnerText;
            value = node["trans"].InnerText;
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("DefInjected rep path is empty.");
            return true;
        }
    }
}
