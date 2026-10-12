using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace AutoTranslator_Core.TranslationPolicy
{
    public static class TranslationPolicyXmlScanner
    {
        // V3 traversal and structural filters are retained. Explicit text semantics
        // are shared with the classifier so later legacy fields are not lost before classification.

        private static readonly HashSet<string> V3BlacklistedFields =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "alienRace", "texPath", "graphicPath", "soundDef", "effecter", "iconPath", "shader",
                "soundCast", "soundCastTail", "soundInteract", "soundHitPawn", "soundMiss",
                "soundMeleeHit", "soundMeleeMiss", "soundAmbience", "linkSound", "fleckDef",
                "thingDef", "itemDef", "pawnKindDef", "hediffDef", "recipeDef", "researchProjectDef",
                "terrainDef", "traitDef", "skillDef", "damageDef", "weaponDef", "apparelDef",
                "projectileDef", "defName", "dollName", "dollPartName", "methodName", "class", "worker",
                "eyeTexPath", "browTexPath", "lidTexPath", "lashTexPath", "mouthTexPath", "noseTexPath",
                "earTexPath", "hairTexPath", "headTexPath", "bodyTexPath", "skinTexPath",
                "eyeballTexPath", "irisTexPath", "pupilTexPath", "expressionPath", "animationPath",
                "facialDef", "texture", "texturePath", "path", "maskPath", "headGraphicPath",
                "bodyGraphicPath", "crownGraphicPath", "frontTexPath", "sideTexPath", "backTexPath",
                "bodyGraphicData", "headGraphicData", "graphicData", "bodyAddon", "bodyAddons",
                "headAddons", "bodyPart", "skinColorChannel", "hairColorChannel", "channelName",
                "linkedBodyPartsGroup", "renderNodeProperties", "shaderType", "subPath", "targetJobs",
                "animationFrames", "faceAnimationDef", "browOffset", "lidOffset", "headOffset",
                "mouthOffset", "noseOffset", "earOffset", "eyeballOffset", "eyeballOffsetL",
                "eyeballOffsetR", "layerOffset", "angle", "scale", "drawSize", "offset", "offsets",
                "li_ref", "parent", "parentName", "abstract", "inherit", "compClass", "thingClass",
                "race", "category", "categories", "tradeTags", "weaponTags", "apparelTags", "tags",
                "linkFlags", "renderNodeTagDef", "tagDef"
            };

        private static readonly Regex V3FilePathRegex = new Regex(
            @"\.(png|jpg|jpeg|wav|mp3|ogg|xml|txt|lua|tex|dds)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<TranslationPolicyCandidate> ScanSourceXml(
            string xml, string sourceFile, string defType, TranslationPolicySourceContext context)
        {
            XDocument document = LoadDocument(xml);
            List<TranslationPolicyCandidate> candidates = new List<TranslationPolicyCandidate>();
            if (document.Root == null) return candidates;

            string rootName = GetQualifiedName(document.Root);
            if (rootName.Equals("Defs", StringComparison.OrdinalIgnoreCase))
            {
                return ScanResolvedDefsXml(xml, context);
            }
            else if (rootName.Equals("LanguageData", StringComparison.OrdinalIgnoreCase) ||
                     IsLanguageSourcePath(sourceFile))
            {
                if ((sourceFile ?? string.Empty).IndexOf("DefInjected", StringComparison.OrdinalIgnoreCase) >= 0)
                    TraverseLanguageData(document.Root, string.Empty, defType, TranslationPolicyBucket.DefInjected, context, candidates);
                else
                    TraverseLanguageData(document.Root, string.Empty, string.Empty, TranslationPolicyBucket.Keyed, context, candidates);
            }
            return candidates.OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal).ToList();
        }

        private static List<TranslationPolicyCandidate> ScanResolvedDefsXml(
            string xml,
            TranslationPolicySourceContext context)
        {
            XmlDocument document = LoadXmlDocument(xml);
            List<TranslationPolicyCandidate> candidates = new List<TranslationPolicyCandidate>();
            if (document.DocumentElement == null ||
                !document.DocumentElement.Name.Equals("Defs", StringComparison.OrdinalIgnoreCase))
                return candidates;

            DefXmlInheritanceResolver.Index index = DefXmlInheritanceResolver.CreateIndex();
            foreach (XmlNode node in document.DocumentElement.ChildNodes)
            {
                if (node.NodeType == XmlNodeType.Element) index.Add(node);
            }
            foreach (XmlNode node in document.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                ResolvedDefXmlNode resolved = index.Resolve(node, context?.SourceFile);
                string defName = DefXmlInheritanceResolver.GetDirectChildText(node, "defName").Trim();
                if (string.IsNullOrEmpty(defName) || resolved.ResolvedNode == null) continue;
                TraverseV3DefNode(
                    resolved.ResolvedNode,
                    defName,
                    node.Name,
                    context,
                    candidates,
                    0,
                    !string.IsNullOrWhiteSpace(node.Attributes?["ParentName"]?.Value) &&
                    !IsFalseInheritance(node.Attributes?["Inherit"]?.Value));
            }
            return candidates.OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal).ToList();
        }

        public static DefXmlInheritanceResolver.Index CreateDefInheritanceIndex(
            IEnumerable<string> sourceFiles,
            CancellationToken cancellationToken,
            Action<string> warning = null)
        {
            DefXmlInheritanceResolver.Index index = DefXmlInheritanceResolver.CreateIndex();
            foreach (string path in sourceFiles ?? Enumerable.Empty<string>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                try
                {
                    using (FileStream stream = new FileStream(
                               path, FileMode.Open, FileAccess.Read, FileShare.Read,
                               65536, FileOptions.SequentialScan))
                    using (XmlReader reader = XmlReader.Create(stream, CreateReaderSettings()))
                    {
                        reader.MoveToContent();
                        if (!reader.Name.Equals("Defs", StringComparison.OrdinalIgnoreCase)) continue;
                        int rootDepth = reader.Depth;
                        while (reader.Read())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (reader.NodeType != XmlNodeType.Element || reader.Depth != rootDepth + 1) continue;
                            if (string.IsNullOrWhiteSpace(reader.GetAttribute("Name"))) continue;
                            XmlDocument document = LoadDefSubtree(reader);
                            if (document.DocumentElement != null) index.Add(document.DocumentElement);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    warning?.Invoke((path ?? string.Empty) + ": " + ex.Message);
                }
            }
            return index;
        }

        public static List<TranslationPolicyCandidate> ScanSourceXmlFile(
            string path,
            string sourceFile,
            string defType,
            TranslationPolicySourceContext context,
            Action<long, long, int> reportProgress = null,
            DefXmlInheritanceResolver.Index defInheritanceIndex = null,
            Action<string> inheritanceWarning = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            long totalBytes = new FileInfo(path).Length;
            List<TranslationPolicyCandidate> candidates = new List<TranslationPolicyCandidate>();
            XmlReaderSettings settings = CreateReaderSettings();
            using (FileStream stream = new FileStream(
                       path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            using (XmlReader reader = XmlReader.Create(stream, settings))
            {
                reader.MoveToContent();
                string rootName = reader.Name;
                if (rootName.Equals("Defs", StringComparison.OrdinalIgnoreCase))
                    StreamDefs(
                        reader, candidates, context, reportProgress, stream, totalBytes,
                        defInheritanceIndex, inheritanceWarning);
                else if (rootName.Equals("LanguageData", StringComparison.OrdinalIgnoreCase) ||
                         IsLanguageSourcePath(sourceFile))
                    StreamLanguageData(
                        reader,
                        (sourceFile ?? string.Empty).IndexOf(
                            "DefInjected", StringComparison.OrdinalIgnoreCase) >= 0
                            ? TranslationPolicyBucket.DefInjected
                            : TranslationPolicyBucket.Keyed,
                        defType,
                        candidates,
                        context,
                        reportProgress,
                        stream,
                        totalBytes);
                reportProgress?.Invoke(totalBytes, totalBytes, GetReaderLineNumber(reader));
            }
            return candidates.OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal).ToList();
        }

        public static List<TranslationPolicyCandidate> ScanKeyedXml(
            string xml, TranslationPolicySourceContext context)
        {
            return ScanLanguageDataXml(xml, string.Empty, TranslationPolicyBucket.Keyed, context);
        }

        public static List<TranslationPolicyCandidate> ScanDefsXml(
            string xml, TranslationPolicySourceContext context)
        {
            return ScanSourceXml(xml, context?.SourceFile, string.Empty, context);
        }

        public static List<TranslationPolicyCandidate> ScanDefInjectedXml(
            string xml, string defType, TranslationPolicySourceContext context)
        {
            return ScanLanguageDataXml(xml, defType, TranslationPolicyBucket.DefInjected, context);
        }

        private static List<TranslationPolicyCandidate> ScanLanguageDataXml(
            string xml, string defType, TranslationPolicyBucket bucket, TranslationPolicySourceContext context)
        {
            XDocument document = LoadDocument(xml);
            List<TranslationPolicyCandidate> candidates = new List<TranslationPolicyCandidate>();
            if (document.Root == null) return candidates;
            TraverseLanguageData(document.Root, string.Empty, defType, bucket, context, candidates);
            return candidates.OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal).ToList();
        }

        private static XDocument LoadDocument(string xml)
        {
            if (xml == null) throw new ArgumentNullException(nameof(xml));
            XmlReaderSettings settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                XmlResolver = null
            };
            using (StringReader stringReader = new StringReader(xml))
            using (XmlReader reader = XmlReader.Create(stringReader, settings))
                return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }

        private static XmlDocument LoadXmlDocument(string xml)
        {
            if (xml == null) throw new ArgumentNullException(nameof(xml));
            XmlDocument document = new XmlDocument { XmlResolver = null };
            using (StringReader stringReader = new StringReader(xml))
            using (XmlReader reader = XmlReader.Create(stringReader, CreateReaderSettings()))
                document.Load(reader);
            return document;
        }

        private static XmlReaderSettings CreateReaderSettings()
        {
            return new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = true,
                XmlResolver = null
            };
        }

        private sealed class StreamElementFrame
        {
            public string Name = string.Empty;
            public string RelativePath = string.Empty;
            public string ParentName = string.Empty;
            public string ParentRelativePath = string.Empty;
            public bool IsListItem;
            public int NextListIndex;
            public int ContentNodeCount;
            public bool HasElementChild;
            public int LineNumber;
            public StringBuilder LeafText = new StringBuilder();
            public StringBuilder DirectChildText;
        }

        private sealed class DeferredDefLeaf
        {
            public string RelativePath = string.Empty;
            public string FieldName = string.Empty;
            public string ParentName = string.Empty;
            public string ParentRelativePath = string.Empty;
            public string Text = string.Empty;
            public bool IsListItem;
            public int LineNumber;
        }

        private static void StreamDefs(
            XmlReader reader,
            List<TranslationPolicyCandidate> candidates,
            TranslationPolicySourceContext context,
            Action<long, long, int> reportProgress,
            Stream stream,
            long totalBytes,
            DefXmlInheritanceResolver.Index defInheritanceIndex,
            Action<string> inheritanceWarning)
        {
            int rootDepth = reader.Depth;
            long lastReported = -1;
            while (reader.Read())
            {
                ReportStreamProgress(reader, stream, totalBytes, reportProgress, ref lastReported);
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != rootDepth + 1) continue;
                string parentName = reader.GetAttribute("ParentName");
                string inherit = reader.GetAttribute("Inherit");
                if (defInheritanceIndex != null &&
                    !string.IsNullOrWhiteSpace(parentName) &&
                    !IsFalseInheritance(inherit))
                {
                    int sourceLineNumber = GetReaderLineNumber(reader);
                    XmlDocument document = LoadDefSubtree(reader);
                    XmlNode originalNode = document.DocumentElement;
                    ResolvedDefXmlNode resolved = defInheritanceIndex.Resolve(
                        originalNode,
                        context?.SourceFile,
                        inheritanceWarning);
                    string defName = DefXmlInheritanceResolver
                        .GetDirectChildText(originalNode, "defName")
                        .Trim();
                    if (!string.IsNullOrEmpty(defName) && resolved.ResolvedNode != null)
                    {
                        TraverseV3DefNode(
                            resolved.ResolvedNode,
                            defName,
                            originalNode.Name,
                            context,
                            candidates,
                            sourceLineNumber,
                            true);
                    }
                    continue;
                }
                using (XmlReader subtree = reader.ReadSubtree())
                    StreamSingleDef(
                        subtree, candidates, context, reportProgress, stream, totalBytes);
            }
        }

        private static XmlDocument LoadDefSubtree(XmlReader reader)
        {
            XmlDocument document = new XmlDocument { XmlResolver = null };
            using (XmlReader subtree = reader.ReadSubtree()) document.Load(subtree);
            return document;
        }

        private static bool IsFalseInheritance(string value)
        {
            return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0";
        }

        private static void StreamSingleDef(
            XmlReader reader,
            List<TranslationPolicyCandidate> candidates,
            TranslationPolicySourceContext context,
            Action<long, long, int> reportProgress,
            Stream stream,
            long totalBytes)
        {
            if (!reader.Read() || reader.NodeType != XmlNodeType.Element) return;
            string defType = reader.Name;
            string defName = string.Empty;
            bool defNameSeen = false;
            List<StreamElementFrame> stack = new List<StreamElementFrame>
            {
                new StreamElementFrame { Name = defType }
            };
            List<DeferredDefLeaf> deferred = new List<DeferredDefLeaf>();
            if (reader.IsEmptyElement) return;
            long lastReported = -1;

            while (reader.Read())
            {
                ReportStreamProgress(reader, stream, totalBytes, reportProgress, ref lastReported);
                if (reader.NodeType == XmlNodeType.Element)
                {
                    StreamElementFrame parent = stack[stack.Count - 1];
                    parent.HasElementChild = true;
                    string name = reader.Name;
                    bool isListItem = name == "li";
                    string segment = isListItem
                        ? (parent.NextListIndex++).ToString(CultureInfo.InvariantCulture)
                        : name;
                    StreamElementFrame frame = new StreamElementFrame
                    {
                        Name = name,
                        ParentName = parent.Name,
                        ParentRelativePath = parent.RelativePath,
                        RelativePath = AppendPath(parent.RelativePath, segment),
                        IsListItem = isListItem,
                        LineNumber = GetReaderLineNumber(reader),
                        DirectChildText = stack.Count == 1 ? new StringBuilder() : null
                    };
                    stack.Add(frame);
                    if (reader.IsEmptyElement)
                        CompleteStreamDefFrame(stack, deferred, ref defName, ref defNameSeen);
                }
                else if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                {
                    StreamElementFrame frame = stack[stack.Count - 1];
                    frame.ContentNodeCount++;
                    frame.LeafText.Append(reader.Value);
                    StreamElementFrame direct = stack.Count > 1 ? stack[1] : null;
                    direct?.DirectChildText?.Append(reader.Value);
                }
                else if ((reader.NodeType == XmlNodeType.Comment ||
                          reader.NodeType == XmlNodeType.ProcessingInstruction) && stack.Count > 1)
                {
                    // XmlDocument in v3.0 kept these nodes. They make ChildNodes.Count != 1,
                    // so a text-plus-comment/PI element is not a pure-text candidate.
                    stack[stack.Count - 1].ContentNodeCount++;
                }
                else if (reader.NodeType == XmlNodeType.EndElement && stack.Count > 1)
                {
                    CompleteStreamDefFrame(stack, deferred, ref defName, ref defNameSeen);
                }
            }

            if (string.IsNullOrEmpty(defName)) return;
            foreach (DeferredDefLeaf leaf in deferred)
            {
                string fullPath = AppendPath(defName, leaf.RelativePath);
                string fullParentPath = string.IsNullOrEmpty(leaf.ParentRelativePath)
                    ? defName
                    : AppendPath(defName, leaf.ParentRelativePath);
                if (!IsDefLeafCandidate(
                        fullPath, leaf.FieldName, leaf.ParentName, fullParentPath,
                        leaf.Text, leaf.IsListItem)) continue;
                AddCandidate(candidates, context, TranslationPolicyBucket.DefInjected,
                    defType, fullPath, leaf.FieldName, leaf.Text, leaf.LineNumber);
            }
        }

        private static void CompleteStreamDefFrame(
            List<StreamElementFrame> stack,
            List<DeferredDefLeaf> deferred,
            ref string defName,
            ref bool defNameSeen)
        {
            StreamElementFrame frame = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 1 && frame.Name == "defName" && !defNameSeen)
            {
                defName = frame.DirectChildText?.ToString() ?? string.Empty;
                defNameSeen = true;
            }
            if (frame.Name == "defName" || frame.HasElementChild || frame.ContentNodeCount != 1) return;
            string text = frame.LeafText.ToString().Trim();
            string provisionalPath = AppendPath("_", frame.RelativePath);
            string provisionalParent = string.IsNullOrEmpty(frame.ParentRelativePath)
                ? "_"
                : AppendPath("_", frame.ParentRelativePath);
            if (!IsDefLeafCandidate(
                    provisionalPath, frame.Name, frame.ParentName, provisionalParent,
                    text, frame.IsListItem)) return;
            deferred.Add(new DeferredDefLeaf
            {
                RelativePath = frame.RelativePath,
                FieldName = frame.Name,
                ParentName = frame.ParentName,
                ParentRelativePath = frame.ParentRelativePath,
                Text = text,
                IsListItem = frame.IsListItem,
                LineNumber = frame.LineNumber
            });
        }

        // All three Def readers share the same admission rule. Unknown text is
        // recalled for review; admission does not mean it should be translated.
        private static bool IsDefLeafCandidate(
            string path, string fieldName, string parentName, string parentPath,
            string text, bool isListItem)
        {
            return IsV3DefLeafCandidate(path, fieldName, parentName, parentPath, text, isListItem) ||
                   (!V3IsProtectedDefPath(path) &&
                    TranslationPolicyClassifier.IsUnknownNaturalLanguageCandidate(fieldName, path, text));
        }

        private static bool IsV3DefLeafCandidate(
            string path,
            string fieldName,
            string parentName,
            string parentPath,
            string text,
            bool isListItem)
        {
            bool isGarbage = text.Length < 2 || Regex.IsMatch(text, @"^[\d\s\-\+\.\%]+$");
            if (isGarbage || string.IsNullOrWhiteSpace(text) || text.Contains(".xml") ||
                text.StartsWith("Tex/") || text.StartsWith("UI/")) return false;
            bool knownPath = V3IsKnownTranslatablePath(path);
            bool explicitTextField = TranslationPolicyClassifier.IsKnownTextField(fieldName);
            bool shouldTranslate = !V3IsProtectedDefPath(path) &&
                                   (knownPath || explicitTextField || !V3LooksLikeDefReferenceValue(text)) &&
                                   (knownPath || V3IsTranslationTarget(fieldName, text));
            if (isListItem && V3ShouldForceTranslateListItem(parentName, parentPath, text))
                shouldTranslate = true;
            return shouldTranslate;
        }

        private static void StreamLanguageData(
            XmlReader reader,
            TranslationPolicyBucket bucket,
            string defType,
            List<TranslationPolicyCandidate> candidates,
            TranslationPolicySourceContext context,
            Action<long, long, int> reportProgress,
            Stream stream,
            long totalBytes)
        {
            int rootDepth = reader.Depth;
            long lastReported = -1;
            while (reader.Read())
            {
                ReportStreamProgress(reader, stream, totalBytes, reportProgress, ref lastReported);
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != rootDepth + 1) continue;
                string name = reader.Name;
                int line = GetReaderLineNumber(reader);
                string value = ReadElementInnerText(reader);
                if (string.IsNullOrEmpty(value)) continue;
                value = NormalizeV3LanguageValue(bucket, value);
                AddCandidate(candidates, context, bucket, defType, name,
                    GetTerminalFieldName(name), value, line);
            }
        }

        private static string ReadElementInnerText(XmlReader reader)
        {
            if (reader == null || reader.NodeType != XmlNodeType.Element || reader.IsEmptyElement)
                return string.Empty;
            int depth = reader.Depth;
            StringBuilder value = new StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
                if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                    value.Append(reader.Value);
            }
            return value.ToString();
        }

        private static void ReportStreamProgress(
            XmlReader reader,
            Stream stream,
            long totalBytes,
            Action<long, long, int> reportProgress,
            ref long lastReported)
        {
            if (reportProgress == null) return;
            long current = Math.Max(0L, Math.Min(totalBytes, stream.Position));
            if (lastReported >= 0 && current < lastReported + 262144L && current < totalBytes) return;
            lastReported = current;
            reportProgress(current, totalBytes, GetReaderLineNumber(reader));
        }

        private static int GetReaderLineNumber(XmlReader reader)
        {
            IXmlLineInfo lineInfo = reader as IXmlLineInfo;
            return lineInfo != null && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;
        }

        private static string AppendPath(string prefix, string segment)
        {
            if (string.IsNullOrEmpty(prefix)) return segment ?? string.Empty;
            if (string.IsNullOrEmpty(segment)) return prefix;
            return prefix + "." + segment;
        }

        private static bool IsLanguageSourcePath(string sourceFile)
        {
            string normalized = (sourceFile ?? string.Empty).Replace('\\', '/');
            return normalized.IndexOf("/Keyed/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("/DefInjected/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.StartsWith("Keyed/", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith("DefInjected/", StringComparison.OrdinalIgnoreCase);
        }

        private static void TraverseV3DefNode(
            XElement node, string currentPath, string defType,
            TranslationPolicySourceContext context, List<TranslationPolicyCandidate> candidates)
        {
            int liIndex = 0;
            foreach (XElement child in node.Elements())
            {
                string childName = GetQualifiedName(child);
                if (childName == "defName") continue;
                bool isListItem = childName == "li";
                string childPath = isListItem
                    ? currentPath + "." + (liIndex++).ToString(CultureInfo.InvariantCulture)
                    : currentPath + "." + childName;

                if (IsV3PureText(child))
                {
                    string text = child.Value.Trim();
                    if (IsDefLeafCandidate(childPath, childName, GetQualifiedName(node),
                            currentPath, text, isListItem))
                        AddCandidate(candidates, context, TranslationPolicyBucket.DefInjected,
                            defType, childPath, childName, child);
                }
                else if (child.HasElements)
                {
                    TraverseV3DefNode(child, childPath, defType, context, candidates);
                }
            }
        }

        private static void TraverseV3DefNode(
            XmlNode node,
            string currentPath,
            string defType,
            TranslationPolicySourceContext context,
            List<TranslationPolicyCandidate> candidates,
            int sourceLineNumber,
            bool isInherited)
        {
            if (node == null) return;
            int liIndex = 0;
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child == null || child.NodeType != XmlNodeType.Element) continue;
                string childName = child.Name;
                if (childName == "defName") continue;
                bool isListItem = childName == "li";
                string childPath = isListItem
                    ? currentPath + "." + (liIndex++).ToString(CultureInfo.InvariantCulture)
                    : currentPath + "." + childName;

                if (IsV3PureText(child))
                {
                    string text = (child.InnerText ?? string.Empty).Trim();
                    if (IsDefLeafCandidate(childPath, childName, node.Name,
                            currentPath, text, isListItem))
                    {
                        AddCandidate(
                            candidates, context, TranslationPolicyBucket.DefInjected,
                            defType, childPath, childName, text, sourceLineNumber, isInherited);
                    }
                }
                else if (child.HasChildNodes)
                {
                    TraverseV3DefNode(
                        child, childPath, defType, context, candidates, sourceLineNumber, isInherited);
                }
            }
        }

        private static bool IsV3PureText(XmlNode node)
        {
            return node != null &&
                   node.ChildNodes.Count == 1 &&
                   (node.FirstChild.NodeType == XmlNodeType.Text ||
                    node.FirstChild.NodeType == XmlNodeType.CDATA);
        }

        private static bool V3IsTranslationTarget(string tagName, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 2) return false;
            if (value.All(char.IsDigit) || Regex.IsMatch(value, @"^[^\w\s]+$")) return false;
            string lower = tagName.ToLower();
            if (lower.EndsWith("defname") || lower.EndsWith("dollname") ||
                lower.EndsWith("dollpartname") || lower.EndsWith("methodname") ||
                lower.EndsWith("class") || lower.EndsWith("worker") || lower.EndsWith("def")) return false;
            if (V3BlacklistedFields.Contains(tagName)) return false;
            if ((value.Contains("/") || value.Contains("\\")) && !value.Contains(" ")) return false;
            if (value.Contains("_") && !value.Contains(" ")) return false;
            if (V3FilePathRegex.IsMatch(value)) return false;
            if (TranslationPolicyClassifier.IsKnownTextField(tagName)) return true;
            return lower.EndsWith("label") || lower.EndsWith("description") ||
                   lower.EndsWith("string") || lower.EndsWith("text") ||
                   lower.EndsWith("message") || lower.EndsWith("name") || lower.EndsWith("desc");
        }

        private static bool V3IsProtectedDefPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string lower = path.ToLowerInvariant();
            return lower.Contains(".targetjobs") || lower.Contains(".animationframes") ||
                   lower.Contains(".bodyaddons") || lower.Contains(".headaddons") ||
                   lower.Contains(".bodygraphicdata") || lower.Contains(".headgraphicdata") ||
                   lower.Contains(".graphicdata") || lower.Contains(".offsets") ||
                   lower.Contains(".texpath") || lower.Contains(".graphicpath") ||
                   lower.Contains(".facial") || lower.Contains(".expression") || lower.Contains(".animation");
        }

        private static bool V3LooksLikeDefReferenceValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string trimmed = value.Trim();
            if (trimmed.Contains("/") || trimmed.Contains("\\")) return true;
            if (V3FilePathRegex.IsMatch(trimmed)) return true;
            if (Regex.IsMatch(trimmed, @"^[+-]?\d+(?:\.\d+)?(?:\s*,\s*[+-]?\d+(?:\.\d+)?){1,3}$")) return true;
            if (Regex.IsMatch(trimmed, @"^\(?\s*[+-]?\d+(?:\.\d+)?(?:\s*,\s*[+-]?\d+(?:\.\d+)?){1,3}\s*\)?$")) return true;
            return Regex.IsMatch(trimmed, @"^[A-Za-z0-9_\.\-:]+$") && !trimmed.Contains(" ");
        }

        private static bool V3ShouldForceTranslateListItem(
            string parentName, string currentPath, string text)
        {
            parentName = parentName ?? string.Empty;
            string parentLower = parentName.ToLowerInvariant();
            if (V3IsProtectedDefPath(currentPath) || V3BlacklistedFields.Contains(parentName)) return false;
            bool knownTextList = TranslationPolicyClassifier.IsKnownTextListPath(currentPath);
            if (!knownTextList && V3LooksLikeDefReferenceValue(text)) return false;
            return knownTextList || V3IsTranslationTarget(parentName, text) || parentLower.Contains("rule");
        }

        private static bool V3IsKnownTranslatablePath(string path)
        {
            return TranslationPolicyClassifier.IsKnownTextPath(path);
        }

        private static bool IsV3PureText(XElement element)
        {
            if (element == null || element.HasElements) return false;
            List<XNode> nodes = element.Nodes().ToList();
            return nodes.Count == 1 && (nodes[0] is XText || nodes[0] is XCData);
        }

        private static void TraverseLanguageData(
            XElement parent, string currentPath, string defType, TranslationPolicyBucket bucket,
            TranslationPolicySourceContext context, List<TranslationPolicyCandidate> candidates)
        {
            // V3 LoadXmlFileToDict and ProcessModKeyed read only direct children of the file root.
            foreach (XElement child in parent.Elements())
            {
                string childName = GetQualifiedName(child);
                if (string.IsNullOrEmpty(child.Value)) continue;
                AddCandidate(candidates, context, bucket, defType, childName,
                    GetTerminalFieldName(childName),
                    NormalizeV3LanguageValue(bucket, child.Value), GetLineNumber(child));
            }
        }

        private static string NormalizeV3LanguageValue(
            TranslationPolicyBucket bucket, string value)
        {
            string safeValue = value ?? string.Empty;
            return bucket == TranslationPolicyBucket.DefInjected
                ? safeValue.Replace("\\n", "\n").Replace("\\r", "\r").Replace("/n", "\n")
                : safeValue;
        }

        private static string GetDirectChildText(XElement parent, string childName)
        {
            XElement child = parent.Elements().FirstOrDefault(
                item => GetQualifiedName(item) == childName);
            return child?.Value ?? string.Empty;
        }

        private static string GetQualifiedName(XElement element)
        {
            if (element == null) return string.Empty;
            string prefix = element.GetPrefixOfNamespace(element.Name.Namespace);
            return string.IsNullOrEmpty(prefix)
                ? element.Name.LocalName
                : prefix + ":" + element.Name.LocalName;
        }

        private static string GetTerminalFieldName(string path)
        {
            string[] parts = (path ?? string.Empty).Split('.');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string part = parts[i];
                int bracket = part.IndexOf('[');
                if (bracket >= 0) part = part.Substring(0, bracket);
                if (part.Length > 0 && !part.All(char.IsDigit)) return part;
            }
            return string.Empty;
        }

        private static void AddCandidate(
            List<TranslationPolicyCandidate> candidates, TranslationPolicySourceContext context,
            TranslationPolicyBucket bucket, string defType, string keyOrPath,
            string fieldName, XElement sourceElement)
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
                SourceText = sourceElement?.Value.Trim() ?? string.Empty,
                SourceLineNumber = GetLineNumber(sourceElement),
                DeclaringAssembly = safeContext.DeclaringAssembly ?? string.Empty,
                SchemaFingerprint = safeContext.SchemaFingerprint ?? string.Empty
            };
            candidate.CandidateId = TranslationPolicyIdentity.CreateCandidateId(candidate);
            candidates.Add(candidate);
        }

        private static void AddCandidate(
            List<TranslationPolicyCandidate> candidates, TranslationPolicySourceContext context,
            TranslationPolicyBucket bucket, string defType, string keyOrPath,
            string fieldName, string sourceText, int sourceLineNumber,
            bool isInherited = false)
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
                SourceLineNumber = sourceLineNumber,
                IsInherited = isInherited,
                DeclaringAssembly = safeContext.DeclaringAssembly ?? string.Empty,
                SchemaFingerprint = safeContext.SchemaFingerprint ?? string.Empty
            };
            candidate.CandidateId = TranslationPolicyIdentity.CreateCandidateId(candidate);
            candidates.Add(candidate);
        }

        private static int GetLineNumber(XElement element)
        {
            IXmlLineInfo lineInfo = element;
            return lineInfo != null && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;
        }
    }
}
