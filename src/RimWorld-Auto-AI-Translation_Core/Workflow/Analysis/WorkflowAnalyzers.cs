using AutoTranslator_Core.TargetedHardcodedUi;
using AutoTranslator_Core.TranslationPolicy;
using AutoTranslator_Core.Workflow.AI;
using AutoTranslator_Core.Workflow.Persistence;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Verse;

namespace AutoTranslator_Core.Workflow.Analysis
{
    public sealed class ModAnalysisTarget
    {
        public ModMetaData Mod { get; set; }
        public ModSnapshotRecord Snapshot { get; set; }
        public List<string> XmlSourceDirectories { get; set; } = new List<string>();
        public List<string> TargetTranslationDirectories { get; set; } = new List<string>();
        public string TargetLanguage { get; set; } = string.Empty;
        public List<ModFileRecord> Files { get; set; } = new List<ModFileRecord>();
        internal List<NativeTranslationEntry> NativeTranslations { get; set; }
        internal HashSet<string> UiObservationKeys { get; set; }
    }

    public sealed class AnalyzerResult
    {
        public string AnalyzerVersion { get; set; } = string.Empty;
        public string AnalysisFingerprint { get; set; } = string.Empty;
        public List<CandidateRecord> Candidates { get; set; } = new List<CandidateRecord>();
        public List<string> Diagnostics { get; set; } = new List<string>();
        public List<DetectedTranslationRecord> DetectedTranslations { get; set; } = new List<DetectedTranslationRecord>();
    }

    public sealed class DetectedTranslationRecord
    {
        public string CandidateId { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public TranslationOrigin Origin { get; set; }
        public string SourcePackageId { get; set; } = string.Empty;
        public string SourceFileRelativePath { get; set; } = string.Empty;
        public string SourceEntryKey { get; set; } = string.Empty;
    }

    public sealed class XmlWorkflowCandidateContext
    {
        public string DefType { get; set; } = string.Empty;
        public string DefName { get; set; } = string.Empty;
        public string FieldPath { get; set; } = string.Empty;
        public string ParentPath { get; set; } = string.Empty;
        public bool IsInherited { get; set; }
        public List<string> NearbyEntries { get; set; } = new List<string>();
        public string CollectionReasonCode { get; set; } = string.Empty;
        public string PolicyReasonCode { get; set; } = string.Empty;
    }

    public interface IWorkflowAnalyzer
    {
        CandidateSourceDomain SourceDomain { get; }
        string Version { get; }
        string CreateAnalysisFingerprint(ModAnalysisTarget target);
        AnalyzerResult Analyze(
            ModAnalysisTarget target,
            CancellationToken cancellationToken,
            Action<double, string> reportProgress = null);
    }

    public sealed class XmlWorkflowAnalyzer : IWorkflowAnalyzer
    {
        public const string AnalyzerVersion = WorkflowIdentity.XmlAnalyzerVersion;
        public CandidateSourceDomain SourceDomain => CandidateSourceDomain.Xml;
        public string Version => AnalyzerVersion;

        public string CreateAnalysisFingerprint(ModAnalysisTarget target)
        {
            return WorkflowIdentity.CreateAnalysisFingerprint(
                Version, target.Snapshot.VersionFingerprint,
                "target-language-aware-policy+def-inheritance-v1+shared-known-text-v1+source-priority|language=" +
                target.TargetLanguage);
        }

        public AnalyzerResult Analyze(
            ModAnalysisTarget target,
            CancellationToken cancellationToken,
            Action<double, string> reportProgress = null)
        {
            ValidateTarget(target);
            AnalyzerResult result = CreateResult(target);
            TargetLanguage targetLanguage = WorkflowRuntimeSettings.GetTargetLanguage();
            reportProgress?.Invoke(0d, "XML · " +
                AutoTranslatorMod.WfText("读取现有译文", "reading existing translations"));
            Dictionary<string, DetectedTranslationRecord> existingTranslations =
                ReadExistingTargetTranslations(target, cancellationToken, result.Diagnostics);
            HashSet<string> visitedFiles = new HashSet<string>(WorkflowPath.Comparer);
            List<string> sourceFiles = new List<string>();
            foreach (string directory in target.XmlSourceDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
                foreach (string file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories)
                             .OrderBy(path => path, WorkflowPath.Comparer))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string fullPath = Path.GetFullPath(file);
                    if (visitedFiles.Add(fullPath)) sourceFiles.Add(fullPath);
                }
            }
            reportProgress?.Invoke(
                0d,
                "XML · " + AutoTranslatorMod.WfText(
                    "建立 Def 继承索引",
                    "building Def inheritance index"));
            DefXmlInheritanceResolver.Index defInheritanceIndex =
                TranslationPolicyXmlScanner.CreateDefInheritanceIndex(
                    sourceFiles,
                    cancellationToken,
                    warning => result.Diagnostics.Add("Def inheritance: " + warning));
            int processedFiles = 0;
            foreach (string fullPath in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Relative(target, fullPath);
                string progressDetail = "XML · " +
                    AutoTranslatorMod.WfText("文件 ", "file ") + (processedFiles + 1) + "/" + sourceFiles.Count +
                    " · " + relative;
                reportProgress?.Invoke(
                    sourceFiles.Count == 0 ? 0d : (double)processedFiles / sourceFiles.Count,
                    progressDetail);
                try
                {
                    ReadFile(
                        target, fullPath, existingTranslations, result, cancellationToken,
                        defInheritanceIndex, targetLanguage,
                        (completedBytes, totalBytes, currentLine) =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string detail = "XML · " +
                                AutoTranslatorMod.WfText("文件 ", "file ") + (processedFiles + 1) + "/" +
                                sourceFiles.Count + " · " + relative +
                                AutoTranslatorMod.WfText(" · 已读取 ", " · read ") +
                                completedBytes + "/" + totalBytes +
                                (currentLine > 0
                                    ? AutoTranslatorMod.WfText(" 字节 · 行 ", " bytes · line ") + currentLine
                                    : AutoTranslatorMod.WfText(" 字节", " bytes"));
                            reportProgress?.Invoke(
                                sourceFiles.Count == 0
                                    ? 0d
                                    : (processedFiles + (totalBytes <= 0
                                        ? 0d
                                        : (double)completedBytes / totalBytes)) / sourceFiles.Count,
                                detail);
                        });
                }
                    catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result.Diagnostics.Add(relative + ": " + ex.Message); }
                processedFiles++;
                reportProgress?.Invoke(
                    sourceFiles.Count == 0 ? 1d : (double)processedFiles / sourceFiles.Count,
                    "XML · " + AutoTranslatorMod.WfText("文件 ", "file ") + processedFiles + "/" + sourceFiles.Count +
                    " · " + relative);
            }
            result.Candidates = result.Candidates
                .GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                // XmlSourceDirectories is ordered by language preference. Preserve
                // the preferred source instead of replacing it with a fallback.
                .Select(group => group.First())
                .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .ToList();
            result.DetectedTranslations = result.DetectedTranslations
                .GroupBy(translation => translation.CandidateId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();
            reportProgress?.Invoke(1d, "XML · " + AutoTranslatorMod.WfText("完成", "complete"));
            return result;
        }

        public List<DetectedTranslationRecord> DetectExistingTranslations(
            ModAnalysisTarget target,
            IEnumerable<CandidateRecord> candidates,
            CancellationToken cancellationToken,
            IList<string> diagnostics)
        {
            Dictionary<string, DetectedTranslationRecord> existing =
                ReadExistingTargetTranslations(target, cancellationToken, diagnostics);
            List<DetectedTranslationRecord> detected = new List<DetectedTranslationRecord>();
            foreach (CandidateRecord candidate in candidates ?? Enumerable.Empty<CandidateRecord>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.SourceDomain != CandidateSourceDomain.Xml) continue;
                TranslationPolicyBucket bucket = string.Equals(
                    candidate.EntryKind, "DefInjected", StringComparison.OrdinalIgnoreCase)
                    ? TranslationPolicyBucket.DefInjected
                    : TranslationPolicyBucket.Keyed;
                XmlWorkflowCandidateContext context = JsonConvert.DeserializeObject<XmlWorkflowCandidateContext>(
                    candidate.ContextJson ?? string.Empty) ?? new XmlWorkflowCandidateContext();
                string lookup = CreateTranslationLookupKey(
                    bucket, context.DefType, candidate.TranslationEntryKey);
                if (!existing.TryGetValue(lookup, out DetectedTranslationRecord translation)) continue;
                detected.Add(CloneDetectedTranslation(translation, candidate.CandidateId));
            }
            return detected;
        }

        private static void ReadFile(
            ModAnalysisTarget target,
            string file,
            IDictionary<string, DetectedTranslationRecord> existingTranslations,
            AnalyzerResult result,
            CancellationToken cancellationToken,
            DefXmlInheritanceResolver.Index defInheritanceIndex,
            TargetLanguage targetLanguage,
            Action<long, long, int> reportProgress)
        {
            string relativePath = Relative(target, file);
            TranslationPolicySourceContext context = new TranslationPolicySourceContext
            {
                PackageId = target.Snapshot.PackageId,
                ModName = target.Snapshot.DisplayName,
                SourceFile = relativePath
            };
            List<TranslationPolicyCandidate> policyCandidates =
                TranslationPolicyXmlScanner.ScanSourceXmlFile(
                    file,
                    relativePath,
                    GetDefType(relativePath),
                    context,
                    reportProgress,
                    defInheritanceIndex,
                    warning => result.Diagnostics.Add(relativePath + ": " + warning));
            Dictionary<string, XmlWorkflowCandidateContext> xmlContexts =
                BuildXmlCandidateContexts(policyCandidates);

            for (int policyIndex = 0; policyIndex < policyCandidates.Count; policyIndex++)
            {
                TranslationPolicyCandidate policyCandidate = policyCandidates[policyIndex];
                cancellationToken.ThrowIfCancellationRequested();
                TranslationPolicyClassification policyClassification =
                    TranslationPolicyClassifier.Classify(policyCandidate);
                CandidateClassification classification = ToWorkflowClassification(
                    policyClassification.Decision);
                byte flags = ClassificationFlagsCodec.Set(0, ClassificationLayer.Xml, classification);
                string locator = CreateStableXmlLocator(policyCandidate);
                string sourceText = policyCandidate.SourceText;
                XmlWorkflowCandidateContext xmlContext = xmlContexts[policyCandidate.CandidateId];
                xmlContext.PolicyReasonCode = policyClassification.ReasonCode;
                result.Candidates.Add(new CandidateRecord
                {
                    CandidateId = WorkflowIdentity.CreateCandidateId(
                        target.Snapshot.ModIdentity, CandidateSourceDomain.Xml, policyCandidate.Bucket.ToString(), locator),
                    PackageId = target.Snapshot.PackageId,
                    ModIdentity = target.Snapshot.ModIdentity,
                    ModVersionFingerprint = target.Snapshot.VersionFingerprint,
                    SnapshotId = target.Snapshot.SnapshotId,
                    SourceDomain = CandidateSourceDomain.Xml,
                    EntryKind = policyCandidate.Bucket.ToString(),
                    LogicalLocator = locator,
                    ContextJson = JsonConvert.SerializeObject(xmlContext),
                    DefaultOutputFileRelativePath = CreateDefaultOutputPath(
                        target, policyCandidate.Bucket, policyCandidate.DefType),
                    TranslationEntryKey = policyCandidate.KeyOrPath,
                    SourceFileRelativePath = relativePath,
                    SourceLineNumber = policyCandidate.SourceLineNumber,
                    SourceLineEnd = policyCandidate.SourceLineNumber,
                    SourceText = sourceText,
                    SourceTextHash = WorkflowIdentity.HashText(sourceText),
                    ContentFingerprint = WorkflowIdentity.CreateContentFingerprint(
                        sourceText, relativePath + "\n" + locator),
                    EstimatedTokens = ApproximateTokenEstimator.Estimate(sourceText),
                    ClassificationFlags = flags,
                    XmlAnalyzerVersion = AnalyzerVersion,
                    XmlAnalysisFingerprint = result.AnalysisFingerprint,
                    XmlReasonCode = policyClassification.ReasonCode,
                    UpdatedUtc = DateTime.UtcNow
                });
                string translationLookup = CreateTranslationLookupKey(
                    policyCandidate.Bucket, policyCandidate.DefType, policyCandidate.KeyOrPath);
                if (existingTranslations.TryGetValue(translationLookup, out DetectedTranslationRecord detected))
                {
                    result.DetectedTranslations.Add(CloneDetectedTranslation(
                        detected,
                        WorkflowIdentity.CreateCandidateId(
                            target.Snapshot.ModIdentity, CandidateSourceDomain.Xml, policyCandidate.Bucket.ToString(), locator)));
                }
            }
        }

        private static CandidateClassification ToWorkflowClassification(
            TranslationPolicyDecision decision)
        {
            switch (decision)
            {
                case TranslationPolicyDecision.HardAllow:
                    return CandidateClassification.NeedsTranslation;
                case TranslationPolicyDecision.HardDeny:
                    return CandidateClassification.NoTranslationNeeded;
                default:
                    return CandidateClassification.Undetermined;
            }
        }

        private static bool IsAlreadyInTargetLanguage(string text, TargetLanguage targetLanguage)
        {
            if (LanguageDetector.LooksLikeTargetLanguage(text, targetLanguage)) return true;
            if (targetLanguage != TargetLanguage.Simplified &&
                targetLanguage != TargetLanguage.Traditional) return false;
            if (string.IsNullOrWhiteSpace(text) ||
                LanguageDetector.HasWrongChineseVariant(text, targetLanguage)) return false;

            int han = 0;
            int kana = 0;
            int otherLetters = 0;
            foreach (char value in text)
            {
                if (!char.IsLetter(value)) continue;
                if (value >= '\u4E00' && value <= '\u9FFF') han++;
                else if ((value >= '\u3040' && value <= '\u30FF')) kana++;
                else otherLetters++;
            }
            int letters = han + kana + otherLetters;
            return han >= 2 && kana == 0 && letters > 0 && han * 100 / letters >= 35;
        }

        private static Dictionary<string, XmlWorkflowCandidateContext> BuildXmlCandidateContexts(
            IList<TranslationPolicyCandidate> candidates)
        {
            Dictionary<string, XmlWorkflowCandidateContext> result =
                new Dictionary<string, XmlWorkflowCandidateContext>(StringComparer.Ordinal);
            foreach (IGrouping<string, TranslationPolicyCandidate> group in
                     (candidates ?? Array.Empty<TranslationPolicyCandidate>())
                     .GroupBy(candidate => GetXmlContextGroupKey(candidate), StringComparer.Ordinal))
            {
                List<TranslationPolicyCandidate> ordered = group
                    .OrderBy(candidate => candidate.SourceLineNumber)
                    .ThenBy(candidate => candidate.KeyOrPath, StringComparer.Ordinal)
                    .ToList();
                for (int index = 0; index < ordered.Count; index++)
                {
                    TranslationPolicyCandidate candidate = ordered[index];
                    string keyOrPath = candidate.KeyOrPath ?? string.Empty;
                    int firstSeparator = keyOrPath.IndexOf('.');
                    string defName = candidate.Bucket == TranslationPolicyBucket.DefInjected && firstSeparator > 0
                        ? keyOrPath.Substring(0, firstSeparator)
                        : string.Empty;
                    string fieldPath = firstSeparator > 0
                        ? keyOrPath.Substring(firstSeparator + 1)
                        : keyOrPath;
                    int parentSeparator = fieldPath.LastIndexOf('.');
                    List<string> nearby = new List<string>();
                    for (int offset = -2; offset <= 2; offset++)
                    {
                        if (offset == 0 || index + offset < 0 || index + offset >= ordered.Count) continue;
                        TranslationPolicyCandidate neighbor = ordered[index + offset];
                        string text = neighbor.SourceText ?? string.Empty;
                        if (text.Length > 180) text = text.Substring(0, 180) + "…";
                        nearby.Add((neighbor.KeyOrPath ?? string.Empty) + " = " + text);
                    }
                    result[candidate.CandidateId] = new XmlWorkflowCandidateContext
                    {
                        DefType = candidate.DefType ?? string.Empty,
                        DefName = defName,
                        FieldPath = fieldPath,
                        ParentPath = parentSeparator > 0
                            ? fieldPath.Substring(0, parentSeparator)
                            : string.Empty,
                        IsInherited = candidate.IsInherited,
                        NearbyEntries = nearby,
                        CollectionReasonCode = candidate.Bucket == TranslationPolicyBucket.DefInjected &&
                            TranslationPolicyClassifier.IsUnknownNaturalLanguageCandidate(
                                candidate.FieldName, candidate.KeyOrPath, candidate.SourceText)
                                ? "unknown_field_natural_language"
                                : "existing_xml_collection_rule",
                        PolicyReasonCode = "v3_translation_target"
                    };
                }
            }
            return result;
        }

        private static string GetXmlContextGroupKey(TranslationPolicyCandidate candidate)
        {
            if (candidate == null) return string.Empty;
            string keyOrPath = candidate.KeyOrPath ?? string.Empty;
            if (candidate.Bucket != TranslationPolicyBucket.DefInjected) return "keyed";
            int separator = keyOrPath.IndexOf('.');
            return separator > 0 ? keyOrPath.Substring(0, separator) : keyOrPath;
        }

        private static string CreateStableXmlLocator(TranslationPolicyCandidate candidate)
        {
            if (candidate.Bucket == TranslationPolicyBucket.Keyed)
                return "key=" + (candidate.KeyOrPath ?? string.Empty);
            string keyOrPath = candidate.KeyOrPath ?? string.Empty;
            int separator = keyOrPath.IndexOf('.');
            string defName = separator > 0 ? keyOrPath.Substring(0, separator) : keyOrPath;
            string fieldPath = separator > 0 ? keyOrPath.Substring(separator + 1) : candidate.FieldName;
            return "defType=" + (candidate.DefType ?? string.Empty) +
                   ";defName=" + defName + ";fieldPath=" + (fieldPath ?? string.Empty);
        }

        private static Dictionary<string, DetectedTranslationRecord> ReadExistingTargetTranslations(
            ModAnalysisTarget target,
            CancellationToken cancellationToken,
            IList<string> diagnostics)
        {
            Dictionary<string, DetectedTranslationRecord> result =
                new Dictionary<string, DetectedTranslationRecord>(StringComparer.Ordinal);
            if (target.NativeTranslations != null)
            {
                foreach (NativeTranslationEntry entry in target.NativeTranslations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string lookup = entry.Bucket + "\n" + entry.DefType + "\n" + entry.EntryKey;
                    TranslationOrigin origin = entry.ModIdentity == target.Snapshot.ModIdentity
                        ? TranslationOrigin.ModNative : TranslationOrigin.ThirdParty;
                    if (result.TryGetValue(lookup, out DetectedTranslationRecord existing) &&
                        (int)existing.Origin > (int)origin) continue;
                    result[lookup] = new DetectedTranslationRecord
                    {
                        TargetLanguage = target.TargetLanguage, Text = entry.Text, Origin = origin,
                        SourcePackageId = entry.PackageId, SourceFileRelativePath = entry.SourceFile,
                        SourceEntryKey = entry.EntryKey
                    };
                }
                return result;
            }
            foreach (string directory in target.TargetTranslationDirectories ?? new List<string>())
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories)
                             .OrderBy(path => path, WorkflowPath.Comparer))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        string relative = Relative(target, file);
                        string xml = File.ReadAllText(file);
                        bool defInjected = relative.IndexOf("DefInjected", StringComparison.OrdinalIgnoreCase) >= 0;
                        List<TranslationPolicyCandidate> entries = defInjected
                            ? TranslationPolicyXmlScanner.ScanDefInjectedXml(xml, GetDefType(relative), new TranslationPolicySourceContext())
                            : TranslationPolicyXmlScanner.ScanKeyedXml(xml, new TranslationPolicySourceContext());
                        string targetRoot = Path.GetFullPath(target.Snapshot.RootPath)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        TranslationOrigin origin = Path.GetFullPath(file).StartsWith(targetRoot, WorkflowPath.Comparison)
                            ? TranslationOrigin.ModNative
                            : TranslationOrigin.ThirdParty;
                        foreach (TranslationPolicyCandidate entry in entries)
                        {
                            string lookup = CreateTranslationLookupKey(
                                entry.Bucket, entry.DefType, entry.KeyOrPath);
                            if (result.TryGetValue(lookup, out DetectedTranslationRecord current) &&
                                (int)current.Origin > (int)origin)
                                continue;
                            result[lookup] = new DetectedTranslationRecord
                            {
                                TargetLanguage = target.TargetLanguage,
                                Text = entry.SourceText,
                                Origin = origin,
                                SourcePackageId = origin == TranslationOrigin.ModNative
                                    ? target.Snapshot.PackageId
                                    : string.Empty,
                                SourceFileRelativePath = relative,
                                SourceEntryKey = entry.KeyOrPath
                            };
                        }
                    }
                    catch (Exception ex) { diagnostics.Add(Relative(target, file) + ": " + ex.Message); }
                }
            }
            return result;
        }

        private static string CreateTranslationLookupKey(
            TranslationPolicyBucket bucket,
            string defType,
            string keyOrPath)
        {
            return bucket + "\n" + (defType ?? string.Empty) + "\n" + (keyOrPath ?? string.Empty);
        }

        private static DetectedTranslationRecord CloneDetectedTranslation(
            DetectedTranslationRecord source,
            string candidateId)
        {
            return new DetectedTranslationRecord
            {
                CandidateId = candidateId ?? string.Empty,
                TargetLanguage = source?.TargetLanguage ?? string.Empty,
                Text = source?.Text ?? string.Empty,
                Origin = source?.Origin ?? TranslationOrigin.None,
                SourcePackageId = source?.SourcePackageId ?? string.Empty,
                SourceFileRelativePath = source?.SourceFileRelativePath ?? string.Empty,
                SourceEntryKey = source?.SourceEntryKey ?? string.Empty
            };
        }

        private static string CreateDefaultOutputPath(
            ModAnalysisTarget target,
            TranslationPolicyBucket bucket,
            string defType)
        {
            string fileName = TranslationGeneratedOutputOwnership.GetCanonicalFileName(target.Snapshot.PackageId);
            if (bucket == TranslationPolicyBucket.Keyed)
                return Path.Combine("Languages", target.TargetLanguage, "Keyed", fileName).Replace('\\', '/');
            return Path.Combine(
                "Languages", target.TargetLanguage, "DefInjected", defType ?? string.Empty, fileName).Replace('\\', '/');
        }

        private static string GetDefType(string relativePath)
        {
            string normalized = relativePath.Replace('\\', '/');
            int marker = normalized.IndexOf("DefInjected/", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return string.Empty;
            string remainder = normalized.Substring(marker + "DefInjected/".Length);
            int slash = remainder.IndexOf('/');
            return slash > 0 ? remainder.Substring(0, slash) : string.Empty;
        }

        private AnalyzerResult CreateResult(ModAnalysisTarget target)
        {
            return new AnalyzerResult
            {
                AnalyzerVersion = Version,
                AnalysisFingerprint = CreateAnalysisFingerprint(target)
            };
        }

        private static string Relative(ModAnalysisTarget target, string file)
        {
            string root = Path.GetFullPath(target.Snapshot.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(file);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, WorkflowPath.Comparison))
                return full.Substring(root.Length + 1).Replace('\\', '/');
            return Path.GetFileName(full);
        }

        private static void ValidateTarget(ModAnalysisTarget target)
        {
            if (target == null || target.Snapshot == null) throw new ArgumentNullException(nameof(target));
        }
    }

    public sealed class DllWorkflowAnalyzer : IWorkflowAnalyzer
    {
        public const string AnalyzerVersion = WorkflowIdentity.DllAnalyzerVersion;
        public CandidateSourceDomain SourceDomain => CandidateSourceDomain.Dll;
        public string Version => AnalyzerVersion;

        public string CreateAnalysisFingerprint(ModAnalysisTarget target)
        {
            if (target.UiObservationKeys == null)
                target.UiObservationKeys = HardcodedUiRuntimeObservationStore.GetObservationKeys(target.Snapshot.PackageId);
            return WorkflowIdentity.CreateAnalysisFingerprint(
                Version, target.Snapshot.VersionFingerprint,
                "cecil-dataflow-v3-candidate-ordinal-policy-nonlinguistic-v1|language=" +
                target.TargetLanguage + "|runtime-ui=" +
                HardcodedUiRuntimeObservationStore.GetSnapshotFingerprint(target.UiObservationKeys));
        }

        public AnalyzerResult Analyze(
            ModAnalysisTarget target,
            CancellationToken cancellationToken,
            Action<double, string> reportProgress = null)
        {
            if (target == null || target.Snapshot == null || target.Mod == null)
                throw new ArgumentNullException(nameof(target));
            if (!target.Mod.Active || !target.Snapshot.IsActive)
                throw new InvalidOperationException(
                    "DLL analysis requires a loaded mod or DLC; unloaded assemblies are not treated as a successful empty scan.");
            string analysisFingerprint = CreateAnalysisFingerprint(target);
            cancellationToken.ThrowIfCancellationRequested();
            reportProgress?.Invoke(0d, "DLL · " +
                AutoTranslatorMod.WfText("扫描程序集", "scanning assemblies"));
            HardcodedUiScanResult scan = HardcodedUiRuntimeScanner.Scan(
                target.Mod,
                false,
                (progress, detail) => reportProgress?.Invoke(
                    Math.Max(0d, Math.Min(0.9d, progress * 0.9d)),
                    "DLL · " + detail),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            bool targetContainsAssemblies = target.Files.Any(file =>
                file != null && string.Equals(file.FileType, "Assembly", StringComparison.Ordinal));
            if (targetContainsAssemblies && scan.AssemblyCount == 0)
            {
                throw new InvalidOperationException(
                    AutoTranslatorMod.WfText(
                        "Mod 包含 DLL 文件，但运行中的游戏没有找到它的任何程序集。不会保存 DLL 0 的成功结果；请重启游戏并重新分析已加载的 Mod。",
                        "The mod contains DLL files, but none of its assemblies were found in the running game. " +
                        "A zero-candidate result will not be saved; restart the game and analyze the loaded mod again."));
            }
            AnalyzerResult result = new AnalyzerResult
            {
                AnalyzerVersion = Version,
                AnalysisFingerprint = analysisFingerprint,
                Diagnostics = scan.Diagnostics.ToList()
            };
            Dictionary<string, int> candidateOrdinalsByMethod =
                new Dictionary<string, int>(StringComparer.Ordinal);
            List<HardcodedUiPatchEntry> orderedEntries = scan.Entries
                         .OrderBy(item => item.AssemblyRelativePath, StringComparer.Ordinal)
                         .ThenBy(item => item.DeclaringType, StringComparer.Ordinal)
                         .ThenBy(item => item.MethodSignature, StringComparer.Ordinal)
                         .ThenBy(item => item.LiteralOrdinal)
                         .ToList();
            Dictionary<string, int> runtimeMatchCounts = orderedEntries
                .GroupBy(HardcodedUiRuntimeObservationStore.CreateCandidateMatchKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            HashSet<string> observedKeys = target.UiObservationKeys;
            int unmatchedObservations = observedKeys.Count(key => !runtimeMatchCounts.ContainsKey(key));
            int ambiguousObservations = observedKeys.Count(key =>
                runtimeMatchCounts.TryGetValue(key, out int count) && count > 1);
            if (unmatchedObservations > 0 || ambiguousObservations > 0)
            {
                string diagnostic = AutoTranslatorMod.WfText(
                    "UI 采集证据与 DLL 候选匹配：未匹配 ",
                    "UI observations matched to DLL candidates: unmatched ") + unmatchedObservations +
                    AutoTranslatorMod.WfText(" 条，同一方法重复文本 ", "; repeated literals in the same method ") +
                    ambiguousObservations + AutoTranslatorMod.WfText(
                        " 条。未匹配证据不会直接生成可注入的翻译条目；可能涉及运行时拼接、调用位置或程序集版本变化。",
                        ". Unmatched observations do not directly create injectable translation entries; " +
                        "possible causes include runtime composition, call-site differences, or assembly version changes.");
                result.Diagnostics.Add(diagnostic);
                AutoTranslatorSettings.AddWarningLog(diagnostic);
            }
            int processedEntries = 0;
            foreach (HardcodedUiPatchEntry entry in orderedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scan.Decisions.TryGetValue(entry.EntryId, out HardcodedUiDecisionRecord decision);
                CandidateClassification classification = MapDecision(decision?.AutomaticDecision ?? HardcodedUiAutomaticDecision.Uncertain);
                string reasonCode = decision?.AutomaticReasonCode ?? string.Empty;
                bool runtimeObserved = observedKeys.Contains(HardcodedUiRuntimeObservationStore.CreateCandidateMatchKey(entry));
                if (runtimeObserved)
                {
                    runtimeMatchCounts.TryGetValue(
                        HardcodedUiRuntimeObservationStore.CreateCandidateMatchKey(entry), out int runtimeMatchCount);
                    bool conflictingUse = decision?.DiagnosticFlags?.Contains("flows_to_ui_and_non_ui") == true ||
                        decision?.AutomaticDecision == HardcodedUiAutomaticDecision.DoNotTranslate;
                    if (runtimeMatchCount == 1 && !conflictingUse)
                    {
                        classification = CandidateClassification.NeedsTranslation;
                        reasonCode = "runtime_ui_observed";
                    }
                    else if (!conflictingUse && classification != CandidateClassification.NeedsTranslation)
                    {
                        classification = CandidateClassification.Undetermined;
                        reasonCode = "runtime_ui_observed_ambiguous";
                    }
                }
                TranslationPolicyClassification policyClassification =
                    TranslationPolicyClassifier.Classify(new TranslationPolicyCandidate
                    {
                        CandidateId = entry.EntryId ?? string.Empty,
                        PackageId = target.Snapshot.PackageId,
                        ModName = target.Snapshot.DisplayName,
                        SourceFile = entry.AssemblyRelativePath ?? string.Empty,
                        Bucket = TranslationPolicyBucket.Keyed,
                        DefType = string.Empty,
                        KeyOrPath = entry.MethodSignature ?? string.Empty,
                        FieldName = string.Empty,
                        SourceText = entry.Literal ?? string.Empty,
                        DeclaringAssembly = entry.AssemblyRelativePath ?? string.Empty,
                        SchemaFingerprint = string.Empty
                    });
                if (policyClassification.Decision == TranslationPolicyDecision.HardDeny)
                {
                    classification = CandidateClassification.NoTranslationNeeded;
                    reasonCode = "policy_" + (policyClassification.ReasonCode ?? "hard_deny");
                }
                string methodIdentity =
                    (entry.AssemblyRelativePath ?? string.Empty).Replace('\\', '/') + "\n" +
                    (entry.DeclaringType ?? string.Empty) + "\n" +
                    (entry.MethodSignature ?? string.Empty);
                candidateOrdinalsByMethod.TryGetValue(methodIdentity, out int candidateOrdinal);
                candidateOrdinalsByMethod[methodIdentity] = candidateOrdinal + 1;
                string assemblyLogicalPath = Path.ChangeExtension(
                        (entry.AssemblyRelativePath ?? string.Empty).Replace('\\', '/'), null) ??
                    string.Empty;
                string locator =
                    "assembly=" + assemblyLogicalPath +
                    ";type=" + (entry.DeclaringType ?? string.Empty) +
                    ";method=" + (entry.MethodSignature ?? string.Empty) +
                    ";callSite=" + candidateOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result.Candidates.Add(new CandidateRecord
                {
                    CandidateId = WorkflowIdentity.CreateCandidateId(
                        target.Snapshot.ModIdentity, CandidateSourceDomain.Dll, "HardcodedUiLiteral", locator),
                    PackageId = target.Snapshot.PackageId,
                    ModIdentity = target.Snapshot.ModIdentity,
                    ModVersionFingerprint = target.Snapshot.VersionFingerprint,
                    SnapshotId = target.Snapshot.SnapshotId,
                    SourceDomain = CandidateSourceDomain.Dll,
                    EntryKind = "HardcodedUiLiteral",
                    LogicalLocator = locator,
                    ContextJson = CreateDllCandidateContext(entry, decision, runtimeObserved),
                    DefaultOutputFileRelativePath = "HardcodedUiPatchPrototype.json",
                    TranslationEntryKey = entry.EntryId,
                    SourceFileRelativePath = entry.AssemblyRelativePath ?? string.Empty,
                    SourceLineNumber = 0,
                    SourceLineEnd = 0,
                    SourceText = entry.Literal ?? string.Empty,
                    SourceTextHash = WorkflowIdentity.HashText(entry.Literal),
                    ContentFingerprint = WorkflowIdentity.CreateContentFingerprint(entry.Literal, locator),
                    EstimatedTokens = ApproximateTokenEstimator.Estimate(entry.Literal),
                    ClassificationFlags = ClassificationFlagsCodec.Set(0, ClassificationLayer.Dll, classification),
                    DllAnalyzerVersion = Version,
                    DllAnalysisFingerprint = result.AnalysisFingerprint,
                    DllReasonCode = reasonCode,
                    UpdatedUtc = DateTime.UtcNow
                });
                processedEntries++;
                reportProgress?.Invoke(
                    orderedEntries.Count == 0
                        ? 0.99d
                        : 0.9d + (0.09d * processedEntries / orderedEntries.Count),
                    "DLL · " + AutoTranslatorMod.WfText("整理条目 ", "collecting entries ") +
                    processedEntries + "/" + orderedEntries.Count + " · " +
                    (entry.AssemblyRelativePath ?? string.Empty));
            }
            reportProgress?.Invoke(
                0.99d,
                "DLL · " + AutoTranslatorMod.WfText("分析完成，等待保存", "analysis complete; awaiting save"));
            return result;
        }

        private static string CreateDllCandidateContext(
            HardcodedUiPatchEntry entry, HardcodedUiDecisionRecord decision, bool runtimeObserved)
        {
            var context = Newtonsoft.Json.Linq.JObject.FromObject(entry);
            context["analysisEvidence"] = Newtonsoft.Json.Linq.JObject.FromObject(new
            {
                reasonCode = decision?.AutomaticReasonCode ?? string.Empty,
                semanticRole = decision?.SemanticRole ?? string.Empty,
                evidencePath = decision?.EvidencePath ?? string.Empty,
                diagnosticFlags = decision?.DiagnosticFlags ?? new List<string>(),
                runtimeObserved,
                notice = "Only recorded analysis evidence; missing or uncertain evidence does not prove player visibility."
            });
            return context.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static CandidateClassification MapDecision(HardcodedUiAutomaticDecision decision)
        {
            switch (decision)
            {
                case HardcodedUiAutomaticDecision.Translate: return CandidateClassification.NeedsTranslation;
                case HardcodedUiAutomaticDecision.DoNotTranslate: return CandidateClassification.NoTranslationNeeded;
                default: return CandidateClassification.Undetermined;
            }
        }
    }

    internal sealed class AnalysisStepService
    {
        private readonly WorkflowRepository _repository;
        private readonly IWorkflowAnalyzer _analyzer;

        public AnalysisStepService(
            WorkflowRepository repository,
            IWorkflowAnalyzer analyzer)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        }

        public async Task ExecuteAsync(
            IList<ModMetaData> mods,
            bool forceAnalysis,
            CancellationToken cancellationToken,
            int stageIndex = 0,
            int stageCount = 1)
        {
            AutoTranslatorSettings.AddDebugLog(
                "workflow.analysis-step selectedMods=" + (mods?.Count ?? 0) +
                " domain=" + _analyzer.SourceDomain + " forced=" + forceAnalysis);
            var failures = new List<Exception>();
            List<ModAnalysisTarget> targets = await BuildTargetsAsync(
                mods, cancellationToken, stageIndex, stageCount, failures);
            PrepareTargets(targets);
            try
            {
                await RunLaneAsync(
                    targets, _analyzer, forceAnalysis, cancellationToken, stageIndex, stageCount);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add(ex); }
            if (failures.Count > 0) throw new AggregateException(failures);
        }

        internal async Task RefreshExistingTranslationsAsync(IList<ModMetaData> mods,
            CancellationToken cancellationToken)
        {
            if (!(_analyzer is XmlWorkflowAnalyzer xmlAnalyzer)) return;
            List<ModAnalysisTarget> targets = await BuildTargetsAsync(mods, cancellationToken, 0, 1);
            PrepareTargets(targets);
            await Task.Run(() =>
            {
                foreach (ModAnalysisTarget target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var diagnostics = new List<string>();
                    var detected = xmlAnalyzer.DetectExistingTranslations(target,
                        _repository.GetCandidates(new[] { target.Snapshot.ModIdentity }, target.TargetLanguage),
                        cancellationToken, diagnostics);
                    PersistDetectedTranslations(target, detected);
                }
            }, cancellationToken);
        }

        private Task<List<ModAnalysisTarget>> BuildTargetsAsync(
            IList<ModMetaData> mods,
            CancellationToken cancellationToken,
            int stageIndex,
            int stageCount,
            ICollection<Exception> preparationFailures = null)
        {
            TargetLanguage targetLanguage = WorkflowRuntimeSettings.GetTargetLanguage();
            return Task.Run(() =>
            {
                List<ModAnalysisTarget> targets = new List<ModAnalysisTarget>();
                IList<ModMetaData> selected = mods ?? new List<ModMetaData>();
                for (int index = 0; index < selected.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ModMetaData mod = selected[index];
                    ReportStageProgress(
                        stageIndex, stageCount,
                        mod?.Name ?? mod?.PackageId ?? string.Empty,
                        DomainLabel(_analyzer.SourceDomain) + " · " +
                        AutoTranslatorMod.WfText("准备文件", "preparing files"),
                        index + 1, selected.Count);
                    try
                    {
                    targets.Add(ModAnalysisTargetFactory.Create(
                        mod,
                        targetLanguage,
                        cancellationToken,
                        (file, completedBytes, totalBytes) =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            ReportStageProgress(
                                stageIndex, stageCount,
                                mod?.Name ?? mod?.PackageId ?? string.Empty,
                                DomainLabel(_analyzer.SourceDomain) + " · " +
                                AutoTranslatorMod.WfText("文件指纹 ", "file fingerprint ") +
                                Path.GetFileName(file) + " · " + completedBytes + "/" + totalBytes +
                                AutoTranslatorMod.WfText(" 字节", " bytes"),
                                index + 1, selected.Count);
                        }));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        if (preparationFailures == null) throw;
                        var failure = new InvalidOperationException(
                            (mod?.Name ?? mod?.PackageId ?? "Mod") + "：准备分析文件失败：" + ex.Message, ex);
                        preparationFailures.Add(failure);
                        AutoTranslatorSettings.AddWarningLog(failure.Message);
                        continue;
                    }
                    ReportStageProgress(
                        stageIndex, stageCount,
                        mod?.Name ?? mod?.PackageId ?? string.Empty,
                        DomainLabel(_analyzer.SourceDomain) + " · " +
                        AutoTranslatorMod.WfText("文件准备完成", "files prepared"),
                        index + 1, selected.Count);
                }
                return targets;
            }, cancellationToken);
        }

        private void PrepareTargets(IList<ModAnalysisTarget> targets)
        {
            List<ModAnalysisTarget> validTargets = (targets ?? new List<ModAnalysisTarget>())
                .Where(target => target?.Snapshot != null)
                .ToList();
            foreach (IGrouping<string, ModAnalysisTarget> duplicate in validTargets.GroupBy(
                         target => target.Snapshot.ModIdentity, StringComparer.Ordinal))
            {
                string[] roots = duplicate.Select(target => target.Snapshot.RootPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(WorkflowPath.Comparer)
                    .ToArray();
                if (roots.Length > 1)
                {
                    throw new InvalidOperationException(
                        "Multiple installed mod roots resolve to the same mod identity '" +
                        duplicate.Key + "': " + string.Join(" | ", roots));
                }
            }
            var translationsByLanguage = validTargets.Select(target => target.TargetLanguage)
                .Distinct(StringComparer.Ordinal).ToDictionary(language => language,
                    language => _repository.GetNativeTranslations(language), StringComparer.Ordinal);
            foreach (ModAnalysisTarget target in validTargets)
            {
                target.NativeTranslations = translationsByLanguage[target.TargetLanguage];
                _repository.UpsertMod(target.Snapshot);
                _repository.ReplaceModFiles(
                    target.Snapshot.ModIdentity, target.Snapshot.VersionFingerprint, target.Files);
            }
        }

        private async Task RunLaneAsync(
            IList<ModAnalysisTarget> targets,
            IWorkflowAnalyzer analyzer,
            bool forceAnalysis,
            CancellationToken cancellationToken,
            int stageIndex,
            int stageCount)
        {
            if (targets == null) return;
            if (analyzer.SourceDomain == CandidateSourceDomain.Dll && targets.Count > 1)
            {
                await RunDllTargetsConcurrentlyAsync(
                    targets,
                    analyzer,
                    forceAnalysis,
                    cancellationToken,
                    stageIndex,
                    stageCount);
                return;
            }
            List<Exception> failures = new List<Exception>();
            int completedTargets = 0;
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                ModAnalysisTarget target = targets[targetIndex];
                cancellationToken.ThrowIfCancellationRequested();
                ReportStageProgress(
                    stageIndex, stageCount,
                    target?.Snapshot?.DisplayName ?? target?.Snapshot?.PackageId ?? string.Empty,
                    DomainLabel(analyzer.SourceDomain) + " · " +
                    AutoTranslatorMod.WfText("开始", "starting"),
                    targetIndex + 1, targets.Count);
                string fingerprint = analyzer.CreateAnalysisFingerprint(target);
                if (!forceAnalysis && _repository.HasSuccessfulAnalysis(
                        target.Snapshot.ModIdentity, target.Snapshot.VersionFingerprint,
                        analyzer.SourceDomain, fingerprint))
                {
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.analysis skip mod=" + target.Snapshot.PackageId +
                        " domain=" + analyzer.SourceDomain + " reason=fingerprint-match");
                    AutoTranslatorSettings.AddLog(
                        "↷ " + DomainLabel(analyzer.SourceDomain) + " " + (targetIndex + 1) +
                        "/" + targets.Count + " · " + target.Snapshot.DisplayName +
                        " (" + target.Snapshot.PackageId + ") · " +
                        AutoTranslatorMod.WfText("结果仍然有效，已跳过重算", "result still valid; recomputation skipped"));
                    if (analyzer.SourceDomain == CandidateSourceDomain.Xml && analyzer is XmlWorkflowAnalyzer xmlAnalyzer)
                    {
                        List<string> diagnostics = new List<string>();
                        List<DetectedTranslationRecord> detected = xmlAnalyzer.DetectExistingTranslations(
                            target,
                            _repository.GetCandidates(
                                new[] { target.Snapshot.ModIdentity }, target.TargetLanguage),
                            cancellationToken,
                            diagnostics);
                        PersistDetectedTranslations(target, detected);
                        AutoTranslatorSettings.AddDebugLog(
                            "workflow.analysis translation-overlay refresh mod=" + target.Snapshot.PackageId +
                            " detected=" + detected.Count + " diagnostics=" + diagnostics.Count);
                    }
                    WorkflowTaskCoordinator.Instance.NotifyWorkbenchDataChanged();
                    completedTargets++;
                    ReportStageProgress(
                        stageIndex, stageCount,
                        target.Snapshot.DisplayName,
                        DomainLabel(analyzer.SourceDomain) + " · " +
                        AutoTranslatorMod.WfText("跳过", "skipped"),
                        targetIndex + 1, targets.Count, true);
                    continue;
                }

                Guid runId = Guid.NewGuid();
                bool targetFailed = false;
                try
                {
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.analysis start mod=" + target.Snapshot.PackageId +
                        " domain=" + analyzer.SourceDomain + " forced=" + forceAnalysis);
                    AutoTranslatorSettings.AddLog(
                        "▶ " + DomainLabel(analyzer.SourceDomain) + " " + (targetIndex + 1) +
                        "/" + targets.Count + " · " + target.Snapshot.DisplayName +
                        " (" + target.Snapshot.PackageId + ")");
                    AnalyzerResult result = await Task.Run(() => analyzer.Analyze(
                        target,
                        cancellationToken,
                        (itemProgress, detail) => ReportStageProgress(
                            stageIndex,
                            stageCount,
                            target.Snapshot.DisplayName,
                            detail,
                            targetIndex + 1,
                            targets.Count,
                            false,
                            itemProgress,
                            DomainLabel(analyzer.SourceDomain))), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    _repository.SaveAnalysisResult(
                        runId, target.Snapshot.ModIdentity, target.Snapshot.VersionFingerprint,
                        analyzer.SourceDomain,
                        result.AnalyzerVersion, result.AnalysisFingerprint, forceAnalysis,
                        result.Candidates, result.Diagnostics);
                    if (analyzer.SourceDomain == CandidateSourceDomain.Xml)
                        PersistDetectedTranslations(target, result.DetectedTranslations);
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.analysis complete mod=" + target.Snapshot.PackageId +
                        " domain=" + analyzer.SourceDomain + " candidates=" + result.Candidates.Count +
                        " diagnostics=" + result.Diagnostics.Count);
                    AutoTranslatorSettings.AddLog(
                        "✓ " + DomainLabel(analyzer.SourceDomain) + " " + (targetIndex + 1) +
                        "/" + targets.Count + " · " + target.Snapshot.DisplayName +
                        AutoTranslatorMod.WfText(" · 完成，候选 ", " · complete, candidates ") +
                        result.Candidates.Count +
                        AutoTranslatorMod.WfText("，诊断 ", ", diagnostics ") + result.Diagnostics.Count);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    targetFailed = true;
                    _repository.SaveAnalysisFailure(
                        runId, target.Snapshot.ModIdentity, target.Snapshot.VersionFingerprint,
                        analyzer.SourceDomain,
                        analyzer.Version, fingerprint, forceAnalysis, ex);
                    failures.Add(new InvalidOperationException(
                        target.Snapshot.PackageId + " / " + analyzer.SourceDomain + ": " + ex.Message, ex));
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.analysis failed mod=" + target.Snapshot.PackageId +
                        " domain=" + analyzer.SourceDomain + " error=" + ex.Message);
                    AutoTranslatorSettings.AddLog(
                        "✗ " + DomainLabel(analyzer.SourceDomain) + " " + (targetIndex + 1) +
                        "/" + targets.Count + " · " + target.Snapshot.DisplayName +
                        AutoTranslatorMod.WfText(" · 失败：", " · failed: ") + ex.Message);
                }
                WorkflowTaskCoordinator.Instance.NotifyWorkbenchDataChanged();
                completedTargets++;
                ReportStageProgress(
                    stageIndex, stageCount,
                    target.Snapshot.DisplayName,
                    DomainLabel(analyzer.SourceDomain) + " · " +
                    (targetFailed
                        ? AutoTranslatorMod.WfText("任务失败", "failed")
                        : AutoTranslatorMod.WfText("完成", "complete")),
                    targetIndex + 1, targets.Count, true);
            }
            if (failures.Count > 0) throw new AggregateException(failures);
        }

        private async Task RunDllTargetsConcurrentlyAsync(
            IList<ModAnalysisTarget> targets,
            IWorkflowAnalyzer analyzer,
            bool forceAnalysis,
            CancellationToken cancellationToken,
            int stageIndex,
            int stageCount)
        {
            int totalTargets = targets?.Count ?? 0;
            if (totalTargets == 0) return;
            int workerLimit = AutoTranslatorMod.Settings?.GetResolvedDllAnalysisMaxConcurrency() ??
                              AutoTranslatorSettings.GetDefaultDllAnalysisMaxConcurrency();
            workerLimit = Math.Max(1, Math.Min(workerLimit, totalTargets));
            AutoTranslatorSettings.AddLog(
                "▶ " + DomainLabel(analyzer.SourceDomain) +
                AutoTranslatorMod.WfText(" · 多 Mod 并发启动：", " · concurrent Mods started: ") +
                workerLimit + "/" + totalTargets);
            AutoTranslatorSettings.AddDebugLog(
                "workflow.analysis concurrent-mods domain=" + analyzer.SourceDomain +
                " targets=" + totalTargets + " workers=" + workerLimit);

            var repositoryGate = new SemaphoreSlim(1, 1);
            var progressGate = new object();
            var failures = new List<Exception>();
            var targetProgress = new double[totalTargets];
            var targetFinished = new bool[totalTargets];
            int nextTarget = -1;
            int completedTargets = 0;
            int activeTargets = 0;
            long progressSequence = 0L;

            Action<int, bool, bool, double, string> publishProgress =
                (targetIndex, starting, finished, itemProgress, detail) =>
                {
                    int completedSnapshot;
                    int activeSnapshot;
                    double aggregateProgress;
                    string currentMod;
                    string currentDetail;
                    long sequenceSnapshot;
                    lock (progressGate)
                    {
                        if (starting) activeTargets++;
                        if (itemProgress >= 0d)
                            targetProgress[targetIndex] = Math.Max(
                                targetProgress[targetIndex],
                                Math.Max(0d, Math.Min(1d, itemProgress)));
                        if (finished && !targetFinished[targetIndex])
                        {
                            targetFinished[targetIndex] = true;
                            completedTargets++;
                            activeTargets = Math.Max(0, activeTargets - 1);
                        }
                        completedSnapshot = completedTargets;
                        activeSnapshot = activeTargets;
                        aggregateProgress = targetProgress.Sum() / totalTargets;
                        currentMod = activeSnapshot > 0
                            ? AutoTranslatorMod.WfText(
                                "并发处理中 ", "processing concurrently ") +
                              activeSnapshot + AutoTranslatorMod.WfText(" 个 Mod", " Mods")
                            : targets[targetIndex].Snapshot.DisplayName;
                        currentDetail = targets[targetIndex].Snapshot.DisplayName + " · " + detail;
                        sequenceSnapshot = ++progressSequence;
                    }

                    int safeStageCount = Math.Max(1, stageCount);
                    long completedUnits = Math.Max(0, stageIndex) * (long)totalTargets +
                                          completedSnapshot;
                    WorkflowTaskCoordinator.Instance.ReportProgress(
                        completedUnits,
                        safeStageCount * (long)totalTargets,
                        currentMod,
                        currentDetail,
                        completedSnapshot,
                        totalTargets,
                        0L,
                        0L,
                        false,
                        Math.Max(0, stageIndex) + 1,
                        safeStageCount,
                        DomainLabel(analyzer.SourceDomain),
                        aggregateProgress,
                        activeSnapshot,
                        true,
                        sequenceSnapshot);
                };

            Task[] workers = Enumerable.Range(0, workerLimit).Select(_ => Task.Run(async () =>
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int targetIndex = Interlocked.Increment(ref nextTarget);
                    if (targetIndex >= totalTargets) return;
                    ModAnalysisTarget target = targets[targetIndex];
                    AutoTranslatorSettings.AddLog(
                        "• " + DomainLabel(analyzer.SourceDomain) + " " + (targetIndex + 1) +
                        "/" + totalTargets + " · " + target.Snapshot.DisplayName +
                        " (" + target.Snapshot.PackageId + ") · " +
                        AutoTranslatorMod.WfText(
                            "已进入并发队列，检查现有分析结果",
                            "entered concurrent queue; checking existing analysis"));
                    publishProgress(
                        targetIndex,
                        true,
                        false,
                        0d,
                        DomainLabel(analyzer.SourceDomain) + " · " +
                        AutoTranslatorMod.WfText("准备", "preparing"));
                    Guid runId = Guid.NewGuid();
                    string fingerprint = string.Empty;
                    bool targetFailed = false;
                    try
                    {
                        fingerprint = analyzer.CreateAnalysisFingerprint(target);
                        bool hasCurrentResult;
                        publishProgress(
                            targetIndex,
                            false,
                            false,
                            0d,
                            DomainLabel(analyzer.SourceDomain) + " · " +
                            AutoTranslatorMod.WfText("检查现有分析结果", "checking existing analysis"));
                        await repositoryGate.WaitAsync(cancellationToken);
                        try
                        {
                            hasCurrentResult = !forceAnalysis && _repository.HasSuccessfulAnalysis(
                                target.Snapshot.ModIdentity,
                                target.Snapshot.VersionFingerprint,
                                analyzer.SourceDomain,
                                fingerprint);
                        }
                        finally
                        {
                            repositoryGate.Release();
                        }

                        if (hasCurrentResult)
                        {
                            AutoTranslatorSettings.AddDebugLog(
                                "workflow.analysis skip mod=" + target.Snapshot.PackageId +
                                " domain=" + analyzer.SourceDomain + " reason=fingerprint-match");
                            AutoTranslatorSettings.AddLog(
                                "↷ " + DomainLabel(analyzer.SourceDomain) + " " + (targetIndex + 1) +
                                "/" + totalTargets + " · " + target.Snapshot.DisplayName +
                                " (" + target.Snapshot.PackageId + ") · " +
                                AutoTranslatorMod.WfText(
                                    "结果仍然有效，已跳过重算",
                                    "result still valid; recomputation skipped"));
                            publishProgress(
                                targetIndex,
                                false,
                                true,
                                1d,
                                DomainLabel(analyzer.SourceDomain) + " · " +
                                AutoTranslatorMod.WfText("跳过", "skipped"));
                            WorkflowTaskCoordinator.Instance.NotifyWorkbenchDataChanged();
                            continue;
                        }

                        AutoTranslatorSettings.AddDebugLog(
                            "workflow.analysis start mod=" + target.Snapshot.PackageId +
                            " domain=" + analyzer.SourceDomain + " forced=" + forceAnalysis +
                            " concurrentModWorker=true");
                        AutoTranslatorSettings.AddLog(
                            "▶ " + DomainLabel(analyzer.SourceDomain) + " " + (targetIndex + 1) +
                            "/" + totalTargets + " · " + target.Snapshot.DisplayName +
                            " (" + target.Snapshot.PackageId + ")");
                        AnalyzerResult result = await Task.Run(() => analyzer.Analyze(
                            target,
                            cancellationToken,
                            (itemProgress, detail) =>
                            {
                                double analysisProgress = Math.Max(
                                    0d,
                                    Math.Min(1d, itemProgress)) * 0.98d;
                                publishProgress(
                                    targetIndex,
                                    false,
                                    false,
                                    analysisProgress,
                                    detail);
                            }), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();

                        publishProgress(
                            targetIndex,
                            false,
                            false,
                            0.99d,
                            DomainLabel(analyzer.SourceDomain) + " · " +
                            AutoTranslatorMod.WfText("保存分析结果", "saving analysis result"));
                        AutoTranslatorSettings.AddLog(
                            "▶ " + DomainLabel(analyzer.SourceDomain) + " " + (targetIndex + 1) +
                            "/" + totalTargets + " · " + target.Snapshot.DisplayName +
                            AutoTranslatorMod.WfText(" · 保存分析结果", " · saving analysis result"));
                        AutoTranslatorSettings.AddDebugLog(
                            "workflow.analysis save-start mod=" + target.Snapshot.PackageId +
                            " domain=" + analyzer.SourceDomain +
                            " candidates=" + result.Candidates.Count);
                        await repositoryGate.WaitAsync(cancellationToken);
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _repository.SaveAnalysisResult(
                                runId,
                                target.Snapshot.ModIdentity,
                                target.Snapshot.VersionFingerprint,
                                analyzer.SourceDomain,
                                result.AnalyzerVersion,
                                result.AnalysisFingerprint,
                                forceAnalysis,
                                result.Candidates,
                                result.Diagnostics);
                        }
                        finally
                        {
                            repositoryGate.Release();
                        }
                        AutoTranslatorSettings.AddDebugLog(
                            "workflow.analysis complete mod=" + target.Snapshot.PackageId +
                            " domain=" + analyzer.SourceDomain + " candidates=" + result.Candidates.Count +
                            " diagnostics=" + result.Diagnostics.Count);
                        AutoTranslatorSettings.AddLog(
                            "✓ " + DomainLabel(analyzer.SourceDomain) + " " + (targetIndex + 1) +
                            "/" + totalTargets + " · " + target.Snapshot.DisplayName +
                            AutoTranslatorMod.WfText(" · 完成，候选 ", " · complete, candidates ") +
                            result.Candidates.Count +
                            AutoTranslatorMod.WfText("，诊断 ", ", diagnostics ") +
                            result.Diagnostics.Count);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        targetFailed = true;
                        Exception reportedFailure = ex;
                        try
                        {
                            await repositoryGate.WaitAsync(cancellationToken);
                            try
                            {
                                _repository.SaveAnalysisFailure(
                                    runId,
                                    target.Snapshot.ModIdentity,
                                    target.Snapshot.VersionFingerprint,
                                    analyzer.SourceDomain,
                                    analyzer.Version,
                                    fingerprint,
                                    forceAnalysis,
                                    ex);
                            }
                            finally
                            {
                                repositoryGate.Release();
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception persistenceException)
                        {
                            reportedFailure = new AggregateException(
                                "DLL analysis failed and its failure record could not be saved.",
                                ex,
                                persistenceException);
                            AutoTranslatorSettings.AddDebugLog(
                                "workflow.analysis failure-save-failed mod=" + target.Snapshot.PackageId +
                                " domain=" + analyzer.SourceDomain +
                                " error=" + persistenceException.Message);
                        }
                        lock (failures)
                        {
                            failures.Add(new InvalidOperationException(
                                target.Snapshot.PackageId + " / " + analyzer.SourceDomain +
                                ": " + reportedFailure.Message,
                                reportedFailure));
                        }
                        AutoTranslatorSettings.AddDebugLog(
                            "workflow.analysis failed mod=" + target.Snapshot.PackageId +
                            " domain=" + analyzer.SourceDomain + " error=" + ex.Message);
                        AutoTranslatorSettings.AddLog(
                            "✗ " + DomainLabel(analyzer.SourceDomain) + " " + (targetIndex + 1) +
                            "/" + totalTargets + " · " + target.Snapshot.DisplayName +
                            AutoTranslatorMod.WfText(" · 失败：", " · failed: ") + ex.Message);
                    }
                    WorkflowTaskCoordinator.Instance.NotifyWorkbenchDataChanged();
                    publishProgress(
                        targetIndex,
                        false,
                        true,
                        1d,
                        DomainLabel(analyzer.SourceDomain) + " · " +
                        (targetFailed
                            ? AutoTranslatorMod.WfText("任务失败", "failed")
                            : AutoTranslatorMod.WfText("完成", "complete")));
                }
            }, cancellationToken)).ToArray();

            try
            {
                await Task.WhenAll(workers);
            }
            finally
            {
                repositoryGate.Dispose();
            }
            if (failures.Count > 0) throw new AggregateException(failures);
        }

        private void ReportStageProgress(
            int stageIndex,
            int stageCount,
            string currentMod,
            string detail,
            int currentItemIndex,
            int totalItems,
            bool currentItemCompleted = false,
            double currentItemProgress = -1d,
            string currentStageName = null)
        {
            int safeStageCount = Math.Max(1, stageCount);
            int safeTotalItems = Math.Max(0, totalItems);
            long completedInStage = safeTotalItems == 0
                ? 0L
                : Math.Max(0, Math.Min(
                    safeTotalItems,
                    currentItemCompleted ? currentItemIndex : currentItemIndex - 1));
            long completed = Math.Max(0, stageIndex) * (long)safeTotalItems + completedInStage;
            bool hasSubProgress = currentItemProgress >= 0d;
            WorkflowTaskCoordinator.Instance.ReportProgress(
                completed,
                safeStageCount * (long)safeTotalItems,
                currentMod,
                detail,
                currentItemIndex,
                totalItems,
                0L,
                0L,
                true,
                Math.Max(0, stageIndex) + 1,
                safeStageCount,
                currentStageName ?? DomainLabel(_analyzer.SourceDomain),
                hasSubProgress ? currentItemProgress : -1d);
        }

        private static string DomainLabel(CandidateSourceDomain domain)
        {
            return domain == CandidateSourceDomain.Xml ? "XML " +
                AutoTranslatorMod.WfText("分析", "analysis") : "DLL " +
                AutoTranslatorMod.WfText("分析", "analysis");
        }

        private void PersistDetectedTranslations(
            ModAnalysisTarget target,
            IList<DetectedTranslationRecord> detectedTranslations)
        {
            if (string.IsNullOrWhiteSpace(target.TargetLanguage)) return;
            detectedTranslations = detectedTranslations ?? new List<DetectedTranslationRecord>();
            _repository.RemoveMissingExternalTranslations(
                target.Snapshot.ModIdentity,
                target.TargetLanguage,
                detectedTranslations.Select(item => item.CandidateId).ToList());
            Dictionary<string, CandidateRecord> candidates = _repository.GetCandidates(
                    new[] { target.Snapshot.ModIdentity }, target.TargetLanguage)
                .ToDictionary(candidate => candidate.CandidateId, StringComparer.Ordinal);
            HashSet<string> incompletePackages =
                _repository.GetIncompleteNativeTranslationPackages(target.TargetLanguage);
            foreach (DetectedTranslationRecord translation in detectedTranslations)
            {
                if (!candidates.TryGetValue(
                        translation.CandidateId, out CandidateRecord candidate)) continue;
                bool currentExternal =
                    candidate.TranslationOrigin == TranslationOrigin.ModNative ||
                    candidate.TranslationOrigin == TranslationOrigin.ThirdParty;
                // An unreadable provider is not evidence that its translation lost priority.
                // Only a fresh reading of this same source may replace its protected value.
                if (currentExternal &&
                    candidate.TranslationState == CandidateTranslationState.Translated &&
                    string.Equals(candidate.SourceTextHashAtTranslation,
                        candidate.SourceTextHash, StringComparison.Ordinal) &&
                    incompletePackages.Contains(candidate.TranslationSourcePackageId) &&
                    !(string.Equals(candidate.TranslationSourcePackageId,
                          translation.SourcePackageId, StringComparison.OrdinalIgnoreCase) &&
                      WorkflowPath.Comparer.Equals(candidate.TranslationSourceFileRelativePath,
                          translation.SourceFileRelativePath) &&
                      string.Equals(candidate.TranslationSourceEntryKey,
                          translation.SourceEntryKey, StringComparison.Ordinal)))
                    continue;
                // This is a fresh match against the current source and native catalog.
                // A stale hash invalidates the previous translation, not this new match.
                bool sameExternalSource = currentExternal &&
                    candidate.TranslationState == CandidateTranslationState.Translated &&
                    candidate.TranslationOrigin == translation.Origin &&
                    string.Equals(
                        candidate.TranslationHash,
                        WorkflowIdentity.HashText(translation.Text),
                        StringComparison.Ordinal) &&
                    string.Equals(
                        candidate.TranslationSourceFileRelativePath,
                        translation.SourceFileRelativePath,
                        WorkflowPath.Comparison) &&
                    string.Equals(
                        candidate.TranslationSourceEntryKey,
                        translation.SourceEntryKey,
                        StringComparison.Ordinal);
                if (sameExternalSource) continue;
                string failureReason = string.Empty;
                string failureDetail = string.Empty;
                if (target.NativeTranslations == null && !AutoTranslatorScanner.TryAcceptTranslatedValue(
                        translation.Text, candidate.SourceText,
                        out string ignoredSanitized, out failureReason, out failureDetail))
                {
                    if (candidate.TranslationState == CandidateTranslationState.Translated &&
                        (int)candidate.TranslationOrigin > (int)translation.Origin)
                        continue;
                    _repository.SetTranslationValidationFailure(
                        translation.CandidateId,
                        translation.TargetLanguage,
                        string.IsNullOrWhiteSpace(failureDetail) ? failureReason : failureDetail,
                        translation.Text);
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.analysis external-translation-invalid candidate=" +
                        translation.CandidateId + " reason=" + failureReason);
                    continue;
                }
                if (currentExternal)
                    _repository.ResetExternalTranslation(
                        translation.CandidateId, translation.TargetLanguage);
                _repository.SaveExternalTranslation(
                        translation.CandidateId, translation.TargetLanguage, translation.Text,
                        translation.Origin, translation.SourcePackageId,
                        translation.SourceFileRelativePath, translation.SourceEntryKey);
            }
            _repository.BindObservedGeneratedTranslations(
                target.Snapshot.ModIdentity, target.TargetLanguage);
        }
    }

    internal sealed class AnalysisWorkflowService
    {
        private readonly AnalysisStepService _xmlStep;
        private readonly AnalysisStepService _dllStep;
        private readonly WorkflowConfigurationStore _configuration;
        private readonly NativeTranslationCatalog _nativeTranslations;

        public AnalysisWorkflowService(
            WorkflowRepository repository,
            IWorkflowAnalyzer xmlAnalyzer,
            IWorkflowAnalyzer dllAnalyzer,
            WorkflowConfigurationStore configuration)
        {
            _xmlStep = new AnalysisStepService(repository, xmlAnalyzer);
            _dllStep = new AnalysisStepService(repository, dllAnalyzer);
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _nativeTranslations = new NativeTranslationCatalog(repository);
        }

        public async Task RunAnalysisButtonAsync(
            IList<ModMetaData> mods,
            bool forceAnalysis,
            CancellationToken cancellationToken)
        {
            bool dllAnalysisEnabled = _configuration.Load().EnableDllAnalysis;
            AutoTranslatorSettings.AddDebugLog(
                "workflow.analysis-button selectedMods=" + (mods?.Count ?? 0) +
                " dllEnabled=" + dllAnalysisEnabled + " forced=" + forceAnalysis);

            int stageCount = dllAnalysisEnabled ? 2 : 1;
            await _nativeTranslations.RefreshAsync(cancellationToken);
            var failures = new List<Exception>();
            try
            {
                await _xmlStep.ExecuteAsync(mods, forceAnalysis, cancellationToken, 0, stageCount);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add(ex); }
            if (dllAnalysisEnabled)
            {
                try
                {
                    await _dllStep.ExecuteAsync(mods, forceAnalysis, cancellationToken, 1, stageCount);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count > 0) throw new AggregateException(failures);
        }

        public async Task RunXmlOnlyAsync(
            IList<ModMetaData> mods,
            bool forceAnalysis,
            CancellationToken cancellationToken)
        {
            await _nativeTranslations.RefreshAsync(cancellationToken);
            await _xmlStep.ExecuteAsync(mods, forceAnalysis, cancellationToken);
        }

        public async Task RunDllOnlyAsync(
            IList<ModMetaData> mods,
            bool forceAnalysis,
            CancellationToken cancellationToken,
            int stageIndex = 0,
            int stageCount = 1)
        {
            await _nativeTranslations.RefreshAsync(cancellationToken);
            await _dllStep.ExecuteAsync(
                mods, forceAnalysis, cancellationToken, stageIndex, stageCount);
            await _xmlStep.RefreshExistingTranslationsAsync(mods, cancellationToken);
        }
    }
}
