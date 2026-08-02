using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責從 Def 結構抽取可翻譯文字。
// EN: This file extracts translatable text from RimWorld Def structures.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器掃描器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorScanner.
    public static partial class AutoTranslatorScanner
    {


        // 這個方法負責判斷 Is翻譯目標 條件是否成立。
        // EN: This method checks is translation target.
        private static bool IsTranslationTarget(string tagName, string value)
        {

            if (string.IsNullOrWhiteSpace(value) || value.Length < 2) return false;
            if (AutoTranslatorMod.Settings != null &&
                LanguageDetector.LooksLikePlaceholderTranslation(value, AutoTranslatorMod.Settings.TargetLang))
            {
                return false;
            }

            if (value.All(char.IsDigit) || Regex.IsMatch(value, @"^[^\w\s]+$")) return false;


            string lower = tagName.ToLower();
            if (lower.EndsWith("defname") || lower.EndsWith("dollname") ||
                lower.EndsWith("dollpartname") || lower.EndsWith("methodname") ||
                lower.EndsWith("class") || lower.EndsWith("worker") || lower.EndsWith("def"))
                return false;


            if (BlacklistedFields.Contains(tagName)) return false;


            if ((value.Contains("/") || value.Contains("\\")) && !value.Contains(" ")) return false;


            if (value.Contains("_") && !value.Contains(" ")) return false;


            if (FilePathRegex.IsMatch(value)) return false;


            return IsLikelyTranslatableFieldName(tagName);
        }

        private static bool IsLikelyTranslatableFieldName(string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName)) return false;
            if (ExactTextTags.Contains(fieldName)) return true;

            string lower = fieldName.ToLowerInvariant();
            return lower.EndsWith("label") || lower.EndsWith("description") ||
                   lower.EndsWith("string") || lower.EndsWith("text") ||
                   lower.EndsWith("message") || lower.Contains("message") ||
                   lower.EndsWith("name") || lower.EndsWith("desc") ||
                   lower.EndsWith("title") || lower.EndsWith("titleshort") ||
                   lower.EndsWith("theme") || lower.EndsWith("member") ||
                   lower.EndsWith("tooltip") || lower.EndsWith("caption") ||
                   lower.EndsWith("prompt") || lower.EndsWith("hint") ||
                   lower.EndsWith("reason");
        }

        private static bool IsUnsafeRuntimeDefInjectionPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || IsProtectedDefPath(path)) return true;

            string[] parts = path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return true;

            int terminalIndex = parts.Length - 1;
            while (terminalIndex > 0 && parts[terminalIndex].All(char.IsDigit))
            {
                terminalIndex--;
            }

            string terminal = parts[terminalIndex];
            if (IsLikelyTranslatableFieldName(terminal)) return false;
            if (BlacklistedFields.Contains(terminal)) return true;

            string lower = terminal.ToLowerInvariant();
            if (lower == "r" || lower == "g" || lower == "b" || lower == "a" ||
                lower == "rgb" || lower == "rgba" || lower == "hsv" ||
                lower == "alpha" || lower == "opacity" || lower == "hue" ||
                lower == "saturation" || lower == "brightness" || lower == "tint" ||
                lower == "offset" || lower == "position" || lower == "scale" ||
                lower == "size" || lower == "drawsize" || lower == "angle" ||
                lower == "rotation" || lower == "rect" || lower == "vector" ||
                lower == "curve")
            {
                return true;
            }

            if (lower.Contains("color") || lower.Contains("colour") ||
                lower.EndsWith("path") || lower.EndsWith("tex") ||
                lower.EndsWith("texture") || lower.EndsWith("shader"))
            {
                return true;
            }

            return false;
        }

        // 這個方法負責判斷 IsProtectedDef路徑 條件是否成立。
        // EN: This method checks is protected Def path.
        private static bool IsProtectedDefPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string lower = path.ToLowerInvariant();
            return lower.Contains(".targetjobs") ||
                   lower.Contains(".animationframes") ||
                   lower.Contains(".alienrace.") ||
                   lower.Contains(".alienpartgenerator") ||
                   lower.Contains(".bodyaddons") ||
                   lower.Contains(".headaddons") ||
                   lower.Contains(".colorchannels") ||
                   lower.Contains(".bodytypes") ||
                   lower.Contains(".headtypes") ||
                   lower.Contains(".bodytype") ||
                   lower.Contains(".headtype") ||
                   lower.Contains(".bodydef") ||
                   lower.Contains(".bodypartlabel") ||
                   lower.Contains(".bodygraphicdata") ||
                   lower.Contains(".headgraphicdata") ||
                   lower.Contains(".lifestagegraphics") ||
                   lower.Contains(".graphicpaths") ||
                   lower.Contains(".graphicdata") ||
                   lower.Contains(".customdraw") ||
                   lower.Contains(".drawsize") ||
                   lower.Contains(".offsets") ||
                   lower.Contains(".texpath") ||
                   lower.Contains(".graphicpath") ||
                   lower.Contains(".facial") ||
                   lower.Contains(".expression") ||
                   lower.Contains(".animation");
        }

        // 這個方法負責處理 LooksLikeDefReferenceValue 相關流程。
        // EN: This method handles looks like Def reference value.
        private static bool LooksLikeDefReferenceValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string trimmed = value.Trim();
            if (trimmed.Contains("/") || trimmed.Contains("\\")) return true;
            if (FilePathRegex.IsMatch(trimmed)) return true;
            if (Regex.IsMatch(trimmed, @"^[+-]?\d+(?:\.\d+)?(?:\s*,\s*[+-]?\d+(?:\.\d+)?){1,3}$")) return true;
            if (Regex.IsMatch(trimmed, @"^\(?\s*[+-]?\d+(?:\.\d+)?(?:\s*,\s*[+-]?\d+(?:\.\d+)?){1,3}\s*\)?$")) return true;
            if (Regex.IsMatch(trimmed, @"^[A-Za-z0-9_\.\-:]+$") && !trimmed.Contains(" ")) return true;
            return false;
        }

        // 這個方法負責判斷 ShouldForce翻譯ListItem 條件是否成立。
        // EN: This method checks should force translate list item.
        private static bool ShouldForceTranslateListItem(XmlNode parentNode, string currentPath, string text)
        {
            if (parentNode == null) return false;
            string parentName = parentNode.Name ?? "";
            string parentLower = parentName.ToLowerInvariant();
            string pathLower = (currentPath ?? "").ToLowerInvariant();

            if (IsProtectedDefPath(currentPath) || BlacklistedFields.Contains(parentName)) return false;

            bool isKnownTextList =
                parentLower == "rulesstrings" ||
                parentLower == "thoughtstagedescriptions" ||
                parentLower.EndsWith("stagedescriptions") ||
                pathLower.EndsWith(".thoughtstagedescriptions");

            if (!isKnownTextList && LooksLikeDefReferenceValue(text)) return false;
            if (parentLower == "rulesstrings" && IsUntranslatableGrammarRule(text)) return false;

            return isKnownTextList || IsTranslationTarget(parentName, text) || parentLower.Contains("rule");
        }


        // 這個方法負責判斷 IsKnownTranslatable路徑 條件是否成立。
        // EN: This method checks is known translatable path.
        private static bool IsKnownTranslatablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string lower = path.ToLowerInvariant();
            return lower.EndsWith(".jobstring") ||
                   lower.EndsWith(".customsummary") ||
                   lower.EndsWith(".summary") ||
                   lower.EndsWith(".filter.customsummary") ||
                   lower.Contains(".thoughtstagedescriptions.") ||
                   lower.Contains(".rulesstrings.") ||
                   lower.EndsWith(".resource.name") ||
                   lower.Contains(".ingredients.") && lower.EndsWith(".filter.customsummary");
        }

        // 這個方法負責處理 TraverseDefNode 相關流程。
        // EN: This method handles traverse Def node.
        private static void TraverseDefNode(XmlNode node, string currentPath, string defType, Dictionary<string, Dictionary<string, string>> result)
        {
            int liIndex = 0;
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                if (child.Name == "defName") continue;

                string childPath = currentPath;
                bool isListItem = child.Name == "li";

                if (isListItem)
                {
                    childPath = $"{currentPath}.{liIndex}";
                    liIndex++;
                }
                else
                {
                    childPath = $"{currentPath}.{child.Name}";
                }

                bool isPureText = false;
                if (child.ChildNodes.Count == 1)
                {
                    var cType = child.ChildNodes[0].NodeType;
                    if (cType == XmlNodeType.Text || cType == XmlNodeType.CDATA)
                    {
                        isPureText = true;
                    }
                }

                if (isPureText)
                {
                    string text = child.InnerText.Trim();


                    bool isGarbage = text.Length < 2 || Regex.IsMatch(text, @"^[\d\s\-\+\.\%]+$");

                    if (!isGarbage && !string.IsNullOrWhiteSpace(text) && !text.Contains(".xml") && !text.StartsWith("Tex/") && !text.StartsWith("UI/"))
                    {
                        if (IsUntranslatableGrammarRule(text)) continue;

                        bool isKnownTranslatablePath = IsKnownTranslatablePath(childPath);
                        bool isExactTextTag = ExactTextTags.Contains(child.Name);
                        bool shouldTranslate = !IsProtectedDefPath(childPath) &&
                                               (isKnownTranslatablePath || isExactTextTag || !LooksLikeDefReferenceValue(text)) &&
                                               (isKnownTranslatablePath || IsTranslationTarget(child.Name, text));


                        if (isListItem && ShouldForceTranslateListItem(node, currentPath, text))
                        {
                            shouldTranslate = true;
                        }

                        if (shouldTranslate)
                        {
                            if (!result.ContainsKey(defType)) result[defType] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            result[defType][childPath] = text;
                        }
                    }
                }
                else if (child.HasChildNodes)
                {
                    TraverseDefNode(child, childPath, defType, result);
                }
            }
        }
        // 這個方法負責處理 ExtractEnglishFromRawDefs 相關流程。
        // EN: This method handles extract english from raw defs.
        public static Dictionary<string, Dictionary<string, string>> ExtractEnglishFromRawDefs(string defsRoot)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(defsRoot)) return result;

            foreach (var file in GetXmlFilesCached(defsRoot, SearchOption.AllDirectories))
            {
                if (AutoTranslatorSettings.IsCancellationRequested) return result;

                try
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(file);
                    if (doc.DocumentElement == null || doc.DocumentElement.Name.ToLower() != "defs") continue;

                    foreach (XmlNode defNode in doc.DocumentElement.ChildNodes)
                    {
                        if (defNode.NodeType != XmlNodeType.Element) continue;
                        string defType = defNode.Name;
                        string defName = "";

                        foreach (XmlNode child in defNode.ChildNodes)
                        {
                            if (child.NodeType == XmlNodeType.Element && child.Name == "defName")
                            {
                                defName = child.InnerText;
                                break;
                            }
                        }

                        if (string.IsNullOrEmpty(defName)) continue;

                        TraverseDefNode(defNode, defName, defType, result);
                    }
                }
                catch { }
            }
            return result;
        }

        // Official language archives use stable list handles while raw XML paths use numeric indexes.
        // Build a suggested-path -> normalized-path map from the untouched source XML so translated
        // handle fields (for example ThoughtStage.untranslatedLabel) do not corrupt the lookup.
        public static Dictionary<string, Dictionary<string, string>> ExtractOfficialDefPathAliasesFromRawDefs(string defsRoot)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(defsRoot)) return result;

            foreach (string file in GetXmlFilesCached(defsRoot, SearchOption.AllDirectories))
            {
                if (AutoTranslatorSettings.IsCancellationRequested) return result;

                try
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(file);
                    if (doc.DocumentElement == null || !doc.DocumentElement.Name.Equals("Defs", StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (XmlNode defNode in doc.DocumentElement.ChildNodes)
                    {
                        if (defNode.NodeType != XmlNodeType.Element) continue;

                        string defName = GetDirectChildText(defNode, "defName");
                        if (string.IsNullOrWhiteSpace(defName)) continue;

                        string defType = defNode.Name;
                        Type runtimeType = GenTypes.GetTypeInAnyAssembly(defType);
                        TraverseRawDefPathAliases(defNode, defName, defName, defType, runtimeType, result);
                    }
                }
                catch
                {
                }
            }

            return result;
        }

        private static void TraverseRawDefPathAliases(
            XmlNode node,
            string normalizedPath,
            string suggestedPath,
            string defType,
            Type runtimeType,
            Dictionary<string, Dictionary<string, string>> result)
        {
            Dictionary<XmlNode, string> listHandles = BuildRawListItemHandles(node, runtimeType);
            int listIndex = 0;

            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                if (child.Name.Equals("defName", StringComparison.OrdinalIgnoreCase)) continue;

                bool isListItem = child.Name.Equals("li", StringComparison.OrdinalIgnoreCase);
                string normalizedChildPath;
                string suggestedChildPath;
                Type childRuntimeType;

                if (isListItem)
                {
                    normalizedChildPath = normalizedPath + "." + listIndex;
                    string handle = listHandles.TryGetValue(child, out string mappedHandle) && !string.IsNullOrWhiteSpace(mappedHandle)
                        ? mappedHandle
                        : listIndex.ToString();
                    suggestedChildPath = suggestedPath + "." + handle;
                    childRuntimeType = GetEnumerableElementType(runtimeType);
                    listIndex++;
                }
                else
                {
                    normalizedChildPath = normalizedPath + "." + child.Name;
                    suggestedChildPath = suggestedPath + "." + child.Name;
                    FieldInfo field = FindInstanceField(runtimeType, child.Name);
                    childRuntimeType = field != null ? field.FieldType : null;
                }

                if (!string.Equals(normalizedChildPath, suggestedChildPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!result.TryGetValue(defType, out Dictionary<string, string> aliases))
                    {
                        aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        result[defType] = aliases;
                    }

                    aliases[suggestedChildPath] = normalizedChildPath;
                }

                if (child.HasChildNodes)
                {
                    TraverseRawDefPathAliases(
                        child,
                        normalizedChildPath,
                        suggestedChildPath,
                        defType,
                        childRuntimeType,
                        result);
                }
            }
        }

        private sealed class RawListHandleCandidate
        {
            public FieldInfo Field;
            public string Handle;
        }

        private static Dictionary<XmlNode, string> BuildRawListItemHandles(XmlNode listNode, Type listType)
        {
            var result = new Dictionary<XmlNode, string>();
            Type elementType = GetEnumerableElementType(listType);
            if (listNode == null || elementType == null || elementType == typeof(string)) return result;

            List<XmlNode> items = listNode.ChildNodes
                .Cast<XmlNode>()
                .Where(n => n.NodeType == XmlNodeType.Element && n.Name.Equals("li", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (items.Count == 0) return result;

            var candidates = new List<RawListHandleCandidate>();
            foreach (XmlNode item in items)
            {
                candidates.Add(GetRawListHandleCandidate(item, elementType));
            }

            for (int i = 0; i < items.Count; i++)
            {
                RawListHandleCandidate candidate = candidates[i];
                if (candidate == null || candidate.Field == null || string.IsNullOrWhiteSpace(candidate.Handle)) continue;

                int duplicateCount = 0;
                int duplicateIndex = -1;
                for (int j = 0; j < candidates.Count; j++)
                {
                    RawListHandleCandidate other = candidates[j];
                    if (other == null || !AreSameHandleFields(candidate.Field, other.Field)) continue;
                    if (!string.Equals(candidate.Handle, other.Handle, StringComparison.OrdinalIgnoreCase)) continue;

                    if (j == i) duplicateIndex = duplicateCount;
                    duplicateCount++;
                }

                result[items[i]] = duplicateCount > 1
                    ? candidate.Handle + "-" + Math.Max(0, duplicateIndex)
                    : candidate.Handle;
            }

            return result;
        }

        private static RawListHandleCandidate GetRawListHandleCandidate(XmlNode itemNode, Type elementType)
        {
            if (itemNode == null || elementType == null) return null;

            var handleFields = GetAllInstanceFields(elementType)
                .Select(field => new
                {
                    Field = field,
                    Attribute = field.GetCustomAttributes(typeof(TranslationHandleAttribute), true)
                        .OfType<TranslationHandleAttribute>()
                        .FirstOrDefault()
                })
                .Where(x => x.Attribute != null)
                .OrderByDescending(x => x.Attribute.Priority)
                .ToList();

            foreach (var handleField in handleFields)
            {
                string rawValue = GetDirectChildText(itemNode, handleField.Field.Name);
                if (string.IsNullOrWhiteSpace(rawValue) && handleField.Field.Name.StartsWith("untranslated", StringComparison.OrdinalIgnoreCase))
                {
                    string sourceFieldName = handleField.Field.Name.Substring("untranslated".Length);
                    if (!string.IsNullOrEmpty(sourceFieldName))
                    {
                        sourceFieldName = char.ToLowerInvariant(sourceFieldName[0]) + sourceFieldName.Substring(1);
                        rawValue = GetDirectChildText(itemNode, sourceFieldName);
                    }
                }

                string normalized = NormalizeRawTranslationHandle(rawValue);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return new RawListHandleCandidate { Field = handleField.Field, Handle = normalized };
                }
            }

            return null;
        }

        private static string GetDirectChildText(XmlNode parent, string childName)
        {
            if (parent == null || string.IsNullOrWhiteSpace(childName)) return "";
            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element && child.Name.Equals(childName, StringComparison.OrdinalIgnoreCase))
                {
                    return (child.InnerText ?? "").Trim();
                }
            }

            return "";
        }

        private static IEnumerable<FieldInfo> GetAllInstanceFields(Type type)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    yield return field;
                }
            }
        }

        private static FieldInfo FindInstanceField(Type type, string fieldName)
        {
            if (type == null || string.IsNullOrWhiteSpace(fieldName)) return null;
            return GetAllInstanceFields(type)
                .FirstOrDefault(field => field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
        }

        private static Type GetEnumerableElementType(Type type)
        {
            if (type == null || type == typeof(string)) return null;
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType && type.GetGenericArguments().Length == 1)
            {
                Type genericDefinition = type.GetGenericTypeDefinition();
                if (genericDefinition == typeof(List<>) || genericDefinition == typeof(IEnumerable<>))
                {
                    return type.GetGenericArguments()[0];
                }
            }

            Type enumerableInterface = type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return enumerableInterface != null ? enumerableInterface.GetGenericArguments()[0] : null;
        }

        private static bool AreSameHandleFields(FieldInfo left, FieldInfo right)
        {
            return left != null && right != null &&
                   left.DeclaringType == right.DeclaringType &&
                   string.Equals(left.Name, right.Name, StringComparison.Ordinal);
        }

        private static string NormalizeRawTranslationHandle(string handle)
        {
            if (string.IsNullOrWhiteSpace(handle)) return "";

            handle = handle.Trim()
                .Replace(' ', '_')
                .Replace('\n', '_')
                .Replace("\r", "")
                .Replace('\t', '_')
                .Replace(".", "")
                .Replace("-", "");
            handle = Regex.Replace(handle, "\\{.*?\\}", "");

            StringBuilder filtered = new StringBuilder(handle.Length);
            const string allowed = "qwertyuiopasdfghjklzxcvbnmQWERTYUIOPASDFGHJKLZXCVBNM1234567890-_";
            foreach (char c in handle)
            {
                if (allowed.IndexOf(c) >= 0) filtered.Append(c);
            }

            string value = Regex.Replace(filtered.ToString(), "_+", "_").Trim('_');
            if (!string.IsNullOrEmpty(value) && value.All(char.IsDigit)) value = "_" + value;
            return value;
        }
    }
}
